using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AxisSdReader.App;

public partial class App : Application
{
    /// <summary>Card image path passed on the command line, auto-opened at startup (dev/testing aid).</summary>
    public static string? StartupImagePath { get; private set; }

    // Per-user app-data (not the world-readable %TEMP%) so a shared machine doesn't expose one user's log.
    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AxisSdReader", "logs", "errors.log");

    /// <summary>The running build's version (from the assembly), shown in logs and the window title.</summary>
    public static string Version { get; } = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";

    protected override void OnStartup(StartupEventArgs e)
    {
        Theme.Apply(dark: true);

        // Defense in depth: never let a stray exception silently vanish the window. UI-thread
        // exceptions are shown and swallowed so the session survives; background-thread and
        // unobserved-task faults are logged (and shown if the UI is still up).
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("AppDomain", args.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("UnobservedTask", args.Exception);
            args.SetObserved();
        };

        // Reclaim any export temp folders orphaned by a previous crash / power loss before we start.
        AxisSdReader.Core.Export.FfmpegExporter.SweepStaleTempDirs();

        // Locates the libvlc native libraries shipped by VideoLAN.LibVLC.Windows.
        LibVLCSharp.Shared.Core.Initialize();

        if (e.Args is [var path] && System.IO.File.Exists(path))
        {
            StartupImagePath = path;
        }

        base.OnStartup(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher", e.Exception);
        MessageBox.Show(
            "Something went wrong, but the app has recovered and your card is untouched (it is open read-only).\n\n" +
            $"Details were written to:\n{CrashLog}\n\n{e.Exception.Message}",
            "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // keep the session alive
    }

    private static void Log(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);

            // Log type + message + stack, but not the raw ToString(): release builds ship no PDB, so the
            // stack carries method names without absolute build-machine source paths.
            var detail = ex is null
                ? "(no exception object)"
                : $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}";
            File.AppendAllText(CrashLog, $"[{DateTime.Now:O}] v{Version} {source}: {detail}\n\n");
        }
        catch
        {
            // logging must never itself throw
        }
    }
}
