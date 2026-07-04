using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AxisSdReader.App.ViewModels;

namespace AxisSdReader.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly HashSet<nint> _blackenedWindows = [];
    private SubclassProc? _videoSubclass; // kept referenced so the GC can't collect the callback

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Timeline.Scrubbing += seconds => _viewModel.Player.ScrubPreview(seconds);
        Timeline.ScrubCommitted += seconds => _viewModel.Player.ScrubCommit(seconds);
        Overview.JumpRequested += seconds => _viewModel.Player.ScrubCommit(seconds);
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => _viewModel.Dispose();

        // LibVLCSharp.WPF hosts the video in a stack of Win32 windows: a "Static" control holding
        // libvlc's "VLC video main" (D3D11) output, inside which the actual "VLC video output"
        // sits. The D3D11 window's default background is the white system colour, so it shows as
        // white in the letterbox bars (portrait video in a landscape area) and while a seek
        // rebuilds the output. These windows are created after playback starts and may be
        // recreated, so poll and subclass any not-yet-handled one to paint its background black.
        _videoSubclass = VideoSubclassProc;
        var blackener = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        blackener.Tick += (_, _) => BlackenVideoWindows();
        blackener.Start();

        if (App.StartupImagePath is { } imagePath)
        {
            Loaded += async (_, _) => await _viewModel.OpenImageFileAsync(imagePath);
        }
    }

    private void BlackenVideoWindows()
    {
        var host = new WindowInteropHelper(this).Handle;
        if (host == nint.Zero)
        {
            return;
        }

        EnumChildWindows(host, (h, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            var cls = sb.ToString();
            if ((cls.StartsWith("VLC video", StringComparison.Ordinal) || cls == "Static") && _blackenedWindows.Add(h))
            {
                SetWindowSubclass(h, _videoSubclass!, 1, nint.Zero);
                SetClassLongPtr(h, GclpHbrBackground, GetStockObject(BlackBrush));
                InvalidateRect(h, nint.Zero, true);
            }

            return true;
        }, nint.Zero);
    }

    private nint VideoSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nint idSubclass, nint refData)
    {
        const uint WmEraseBkgnd = 0x0014;
        if (msg == WmEraseBkgnd)
        {
            GetClientRect(hWnd, out var rc);
            FillRect(wParam, ref rc, GetStockObject(BlackBrush)); // wParam is the device context
            return 1; // background handled — nothing white gets painted
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private const int BlackBrush = 4;
    private const int GclpHbrBackground = -10;

    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nint idSubclass, nint refData);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern nint SetClassLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, EnumChildProc callback, nint lParam);

    private delegate bool EnumChildProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint hdc, ref RECT rect, nint brush);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int index);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nint idSubclass, nint refData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    // Keep a maximized borderless window from covering the taskbar.
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private static nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        if (msg == WmGetMinMaxInfo)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != 0)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(monitor, ref info);
                var work = info.rcWork;
                var monitorRect = info.rcMonitor;
                mmi.ptMaxPosition.X = work.Left - monitorRect.Left;
                mmi.ptMaxPosition.Y = work.Top - monitorRect.Top;
                mmi.ptMaxSize.X = work.Right - work.Left;
                mmi.ptMaxSize.Y = work.Bottom - work.Top;
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
        }

        return 0;
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
