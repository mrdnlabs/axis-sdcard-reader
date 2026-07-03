using System.Windows;

namespace AxisSdReader.App;

public partial class App : Application
{
    /// <summary>Card image path passed on the command line, auto-opened at startup (dev/testing aid).</summary>
    public static string? StartupImagePath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Theme.Apply(dark: true, accent: "Trust Blue");

        // Locates the libvlc native libraries shipped by VideoLAN.LibVLC.Windows.
        LibVLCSharp.Shared.Core.Initialize();

        if (e.Args is [var path] && System.IO.File.Exists(path))
        {
            StartupImagePath = path;
        }

        base.OnStartup(e);
    }
}
