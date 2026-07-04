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
    private nint _videoHwnd;
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

        // LibVLCSharp.WPF hosts the video in a Win32 "Static" control whose background is the
        // white system colour. When a seek tears down/rebuilds libvlc's video output, that white
        // flashes through for ~1s. Subclass the Static window and paint its background black so
        // any transition clears to black. The window is created after the MediaPlayer (built on a
        // background thread) is attached, so poll until it exists.
        var blackener = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        blackener.Tick += (_, _) => TryBlackenVideoWindow(blackener);
        blackener.Start();

        if (App.StartupImagePath is { } imagePath)
        {
            Loaded += async (_, _) => await _viewModel.OpenImageFileAsync(imagePath);
        }
    }

    private void TryBlackenVideoWindow(DispatcherTimer timer)
    {
        if (_videoHwnd != nint.Zero)
        {
            timer.Stop();
            return;
        }

        var host = new WindowInteropHelper(this).Handle;
        if (host == nint.Zero)
        {
            return;
        }

        nint found = nint.Zero;
        EnumChildWindows(host, (h, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == "Static")
            {
                found = h;
                return false; // stop enumerating
            }

            return true;
        }, nint.Zero);

        if (found == nint.Zero)
        {
            return;
        }

        _videoHwnd = found;
        _videoSubclass = VideoSubclassProc;
        SetWindowSubclass(found, _videoSubclass, 1, nint.Zero);
        InvalidateRect(found, nint.Zero, true);
        timer.Stop();
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

    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nint idSubclass, nint refData);

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
