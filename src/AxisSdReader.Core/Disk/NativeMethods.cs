using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AxisSdReader.Core.Disk;

/// <summary>Win32 interop for raw disk and volume access. All access here is read-oriented;
/// the only "write-ish" operations are volume lock/dismount/mount-point removal, which never
/// touch the disk contents.</summary>
internal static partial class NativeMethods
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;

    internal const uint FsctlLockVolume = 0x00090018;
    internal const uint FsctlUnlockVolume = 0x0009001C;
    internal const uint FsctlDismountVolume = 0x00090020;
    internal const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    internal const uint IoctlStorageQueryProperty = 0x002D1400;
    internal const uint IoctlDiskGetLengthInfo = 0x0007405C;
    internal const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    internal const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorNoMoreFiles = 18;
    internal const int ErrorMoreData = 234;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[]? lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool DeleteVolumeMountPoint(string lpszVolumeMountPoint);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFindVolumeHandle FindFirstVolume(StringBuilder lpszVolumeName, int cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool FindNextVolume(SafeFindVolumeHandle hFindVolume, StringBuilder lpszVolumeName, int cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool FindVolumeClose(nint hFindVolume);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetVolumePathNamesForVolumeName(
        string lpszVolumeName,
        char[] lpszVolumePathNames,
        int cchBufferLength,
        out int lpcchReturnLength);

    /// <summary>Issues a DeviceIoControl with no input and a fixed-size output buffer.</summary>
    internal static bool TryIoctl(SafeFileHandle handle, uint code, byte[] output, out int bytesReturned) =>
        DeviceIoControl(handle, code, null, 0, output, output.Length, out bytesReturned, 0);

    /// <summary>Issues a DeviceIoControl with no input or output, throwing on failure.</summary>
    internal static void Ioctl(SafeFileHandle handle, uint code, string operation)
    {
        if (!DeviceIoControl(handle, code, null, 0, null, 0, out _, 0))
        {
            throw new IOException($"{operation} failed.", Marshal.GetHRForLastWin32Error());
        }
    }
}

internal sealed class SafeFindVolumeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindVolumeHandle() : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.FindVolumeClose(handle);
}
