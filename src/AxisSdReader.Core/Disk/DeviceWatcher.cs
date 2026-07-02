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

    private void MessageLoop(ManualResetEventSlim ready)
    {
        var className = $"AxisSdReaderDeviceWatcher_{Environment.CurrentManagedThreadId}";
        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = className,
            hInstance = GetModuleHandle(null),
        };

        RegisterClass(ref wndClass);
        _hwnd = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0, HwndMessage, 0, wndClass.hInstance, 0);

        if (_hwnd != 0)
        {
            var filter = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                dbcc_devicetype = DbtDevTypDeviceInterface,
                dbcc_classguid = DiskInterfaceGuid,
            };
            _notificationHandle = RegisterDeviceNotification(_hwnd, ref filter, 0);
        }

        ready.Set();

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
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
                    (wParam == DbtDeviceArrival ? DiskArrived : DiskRemoved)?.Invoke(path);
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
        if (_notificationHandle != 0)
        {
            UnregisterDeviceNotification(_notificationHandle);
            _notificationHandle = 0;
        }

        if (_hwnd != 0)
        {
            PostMessage(_hwnd, WmClose, 0, 0);
            _hwnd = 0;
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
