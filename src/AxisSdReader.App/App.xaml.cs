using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AxisSdReader.App;

public partial class App : Application
{
    /// <summary>Card image path passed on the command line, auto-opened at startup (dev/testing aid).</summary>
    public static string? StartupImagePath { get; private set; }

    private static readonly string CrashLog =
        Path.Combine(Path.GetTempPath(), "axis-sd-reader-errors.log");

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
            File.AppendAllText(CrashLog, $"[{DateTime.Now:O}] {source}: {ex}\n\n");
        }
        catch
        {
            // logging must never itself throw
        }
    }
}
