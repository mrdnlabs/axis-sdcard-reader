using System.Runtime.InteropServices;

namespace AxisSdReader.Core.Disk;

/// <summary>
/// Raises events when disk devices arrive or are removed, using a message-only window
/// registered for <c>GUID_DEVINTERFACE_DISK</c> notifications. Events fire on a
/// background thread and carry the device interface path (usable with
/// <see cref="DiskEnumerator.GetDiskNumber"/>). No elevation required.
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private static readonly Guid DiskInterfaceGuid = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

    private const uint WmDeviceChange = 0x0219;
    private const uint WmClose = 0x0010;
    private const nint DbtDeviceArrival = 0x8000;
    private const nint DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevTypDeviceInterface = 5;
    private const nint HwndMessage = -3;

    private readonly Thread _thread;
    private readonly WndProcDelegate _wndProc; // kept referenced so the GC cannot collect the callback
    private nint _hwnd;
    private nint _notificationHandle;
    private nint _hInstance;
    private string? _className;
    private volatile bool _listening;

    public event Action<string>? DiskArrived;
    public event Action<string>? DiskRemoved;

    public DeviceWatcher()
    {
        _wndProc = WndProc;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => MessageLoop(ready)) { IsBackground = true, Name = "DeviceWatcher" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// True when the watcher successfully registered for device-arrival notifications. When false,
    /// hot-plug detection is unavailable (the caller can still scan on demand); it is not fatal.
    /// </summary>
    public bool IsListening => _listening;

    private void MessageLoop(ManualResetEventSlim ready)
    {
        _hInstance = GetModuleHandle(null);
        // A GUID-based class name avoids colliding with a stale class atom if a previous watcher's
        // managed thread id is recycled before its class was unregistered.
        var className = $"AxisSdReaderDeviceWatcher_{Guid.NewGuid():N}";
        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = className,
            hInstance = _hInstance,
        };

        if (RegisterClass(ref wndClass) != 0)
        {
            _className = className;
            _hwnd = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0, HwndMessage, 0, _hInstance, 0);

            if (_hwnd != 0)
            {
                var filter = new DEV_BROADCAST_DEVICEINTERFACE
                {
                    dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                    dbcc_devicetype = DbtDevTypDeviceInterface,
                    dbcc_classguid = DiskInterfaceGuid,
                };
                _notificationHandle = RegisterDeviceNotification(_hwnd, ref filter, 0);
                _listening = _notificationHandle != 0;
            }
        }

        ready.Set();

        if (!_listening)
        {
            Cleanup(); // nothing to pump — release any partial registration and end the thread
            return;
        }

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Cleanup();
    }

    /// <summary>Releases the notification, window and window class. Runs on the watcher thread.</summary>
    private void Cleanup()
    {
        if (_notificationHandle != 0)
        {
            UnregisterDeviceNotification(_notificationHandle);
            _notificationHandle = 0;
        }

        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        if (_className is not null)
        {
            UnregisterClass(_className, _hInstance);
            _className = null;
        }
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmDeviceChange && lParam != 0 && (wParam == DbtDeviceArrival || wParam == DbtDeviceRemoveComplete))
        {
            var deviceType = Marshal.ReadInt32(lParam, 4);
            if (deviceType == DbtDevTypDeviceInterface)
            {
                // DEV_BROADCAST_DEVICEINTERFACE_W: size(4) + type(4) + reserved(4) + guid(16) + name (wide, NUL-terminated)
                var path = Marshal.PtrToStringUni(lParam + 28);
                if (!string.IsNullOrEmpty(path))
                {
                    try
                    {
                        (wParam == DbtDeviceArrival ? DiskArrived : DiskRemoved)?.Invoke(path);
                    }
                    catch
                    {
                        // A subscriber throwing must not tear down the native message pump.
                    }
                }
            }

            return 1;
        }

        if (msg == WmClose)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        // Ask the message loop to quit; it releases the window/notification/class on its own thread
        // (Cleanup) so there is no cross-thread teardown race. If it never started listening, the
        // thread has already exited and this posts to a null handle (a harmless no-op).
        var hwnd = _hwnd;
        if (hwnd != 0)
        {
            PostMessage(hwnd, WmClose, 0, 0);
        }

        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public short dbcc_name; // placeholder for the variable-length name
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, nint hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint RegisterDeviceNotification(nint recipient, ref DEV_BROADCAST_DEVICEINTERFACE filter, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterDeviceNotification(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
