using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AxisSdReader.Core.Media;
using AxisSdReader.Core.Security;

namespace AxisSdReader.App;

public partial class App : Application
{
    /// <summary>Card image path passed on the command line, auto-opened at startup (dev/testing aid).</summary>
    public static string? StartupImagePath { get; private set; }

    /// <summary>Why this copy's install folder is unsafe for an elevated app, or null when it is fine.</summary>
    public static string? InstallLocationWarning { get; private set; }

    /// <summary>True when running with administrator rights (needed to read SD cards; card images need none).</summary>
    public static bool IsElevated { get; private set; }

    /// <summary>The elevated entry point beside the app (see src\AxisSdReader.Launcher), and the variable it sets.</summary>
    private const string LauncherExe = "AxisSdReader.exe";
    private const string LaunchedMarker = "AXISSDREADER_LAUNCHED";

    // Per-user app-data (not the world-readable %TEMP%) so a shared machine doesn't expose one user's log. Its
    // standard location, not a per-user redirection of it, which would aim the elevated app's writes elsewhere;
    // if that can't be determined, nothing is logged.
    private static readonly string? LogRoot = ProcessHardening.DefaultLocalAppData();
    private static readonly string[] LogFolders = ["AxisSdReader", "logs"];
    private const string LogFile = "errors.log";

    public static string CrashLog => LogRoot is null ? "(no log)" : Path.Combine([LogRoot, .. LogFolders, LogFile]);

    /// <summary>The running build's version (from the assembly), shown in logs and the window title.</summary>
    public static string Version { get; } = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";

    private int _dialogDepth;
    private DateTime _lastDialog = DateTime.MinValue;
    private string? _lastDialogType;

    protected override void OnStartup(StartupEventArgs e)
    {
        // First, before any native media code loads: drop the user-controlled libVLC search-path variables
        // (a plugin path is a DLL the elevated app would load), take the current directory out of the DLL
        // search order, and refuse to follow junctions planted by non-administrators.
        ProcessHardening.Apply();

        IsElevated = ProcessHardening.IsElevated();

        // A card image named on the command line opens at startup (a development aid) - but never with
        // administrator rights: whoever wrote the command line (a shortcut, another program) would choose what the
        // elevated app parses. The launcher passes no arguments at all.
        if (!IsElevated && e.Args is [var path] && File.Exists(path))
        {
            StartupImagePath = Path.GetFullPath(path);
        }

        // Reading SD cards needs administrator rights, and the app gets them only through its launcher, which
        // starts it with a clean environment (see src\AxisSdReader.Launcher). Started directly and unelevated,
        // hand over to the launcher - unless it already started us (Windows didn't elevate it) or this is just a
        // card image, which needs no rights at all.
        if (!IsElevated && StartupImagePath is null && Environment.GetEnvironmentVariable(LaunchedMarker) is null &&
            TryRelaunchElevated())
        {
            ExitDuringStartup(0);
            return;
        }

        Theme.Apply(dark: true);

        // Defense in depth: never let a stray exception silently vanish the window. UI-thread
        // exceptions are shown and swallowed so the session survives; background-thread and
        // unobserved-task faults are logged.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("AppDomain", args.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("UnobservedTask", args.Exception);
            args.SetObserved();
        };

        if (IsElevated)
        {
            // The launcher has already checked the folder itself and its files before loading anything from it; this
            // covers everything below it too (libVLC and FFmpeg are loaded or run from subfolders later).
            InstallLocationWarning = ProcessHardening.DescribeUnsafeInstallLocation(AppContext.BaseDirectory);
            if (InstallLocationWarning is not null)
            {
                MessageBox.Show(
                    "Axis SD Card Reader won't run from this folder, because programs running without administrator rights " +
                    $"could change it:\n\n{InstallLocationWarning}\n\nThat would let any program on this PC run code as " +
                    "administrator through this app. Delete this copy, download the release again (check its SHA-256), and " +
                    "extract it into C:\\Program Files.",
                    "Unsafe install location", MessageBoxButton.OK, MessageBoxImage.Error);
                ExitDuringStartup(1);
                return;
            }
        }

        try
        {
            // Locates the libvlc native libraries shipped by VideoLAN.LibVLC.Windows.
            LibVLCSharp.Shared.Core.Initialize();
        }
        catch (Exception ex)
        {
            // Without libVLC the app can't play video; say so and exit rather than leaving a windowless process.
            Log("Startup", ex);
            MessageBox.Show($"The video engine could not be loaded, so the app can't start.\n\n{ex.Message}",
                "Axis SD Card Reader", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitDuringStartup(1);
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>Ends the app from OnStartup. Shutdown alone only queues the exit: WPF would still create the
    /// StartupUri window - whose view model starts looking for cards - before it runs.</summary>
    private void ExitDuringStartup(int exitCode)
    {
        StartupUri = null;
        Shutdown(exitCode);
    }

    /// <summary>Starts the launcher (Windows asks for administrator rights). False if it is missing or the user
    /// said no, in which case this copy carries on without rights: card images still open.</summary>
    private static bool TryRelaunchElevated()
    {
        var launcher = Path.Combine(AppContext.BaseDirectory, LauncherExe);
        if (!File.Exists(launcher))
        {
            return false;
        }

        try
        {
            // Started is started: the shell doesn't always hand back a process handle for an elevated start.
            Process.Start(new ProcessStartInfo(launcher)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // the user declined the administrator prompt
        }
        catch (Exception ex)
        {
            Log("Relaunch", ex);
            return false;
        }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher", e.Exception);
        e.Handled = true; // keep the session alive

        // Before the main window exists there is no session to keep: report and exit instead of leaving an
        // elevated process running with no window.
        if (MainWindow is not { IsLoaded: true })
        {
            MessageBox.Show($"The app could not start.\n\n{e.Exception.Message}\n\nDetails: {CrashLog}",
                "Axis SD Card Reader", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // A fault that repeats (say, on every repaint) must not become an endless stack of dialogs: one at a
        // time, and the same kind of error at most once every 30 seconds.
        var type = e.Exception.GetType().FullName;
        if (_dialogDepth > 0 || (type == _lastDialogType && DateTime.UtcNow - _lastDialog < TimeSpan.FromSeconds(30)))
        {
            return;
        }

        _dialogDepth++;
        try
        {
            _lastDialogType = type;
            _lastDialog = DateTime.UtcNow;
            MessageBox.Show(
                "Something went wrong, but the app has recovered and your card is untouched (it is open read-only).\n\n" +
                $"Details were written to:\n{CrashLog}\n\n{e.Exception.Message}",
                "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _dialogDepth--;
        }
    }

    /// <summary>
    /// Appends to the crash log. The app runs elevated but the log lives in the user's own (writable) profile,
    /// so it is written through <see cref="GuardedFile.TryAppendText"/>, which refuses junctions, links and hard
    /// links — otherwise anything running as the user could aim an elevated write at a system file.
    /// </summary>
    public static void Log(string source, Exception? ex)
    {
        try
        {
            // Full detail including inner exceptions. Release builds ship no PDB, so stack traces carry method
            // names but no build-machine source paths.
            // Lease tokens (credentials while they live) never reach the log.
            if (LogRoot is null)
            {
                return;
            }

            // Continuation lines are indented, so text from a card quoted in a message can't pass for a new entry.
            var detail = ex is null ? "(no exception object)" : LocalMediaServer.RedactTokens(ex.ToString());
            detail = detail.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\n    ");
            GuardedFile.TryAppendText(LogRoot, LogFolders, LogFile, $"[{DateTime.Now:O}] v{Version} {source}: {detail}\n\n");
        }
        catch
        {
            // logging must never itself throw
        }
    }
}
