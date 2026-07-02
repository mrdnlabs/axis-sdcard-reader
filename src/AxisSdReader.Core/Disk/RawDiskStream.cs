using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AxisSdReader.Core.Disk;

/// <summary>
/// Read-only <see cref="Stream"/> over a raw Windows disk device (e.g. <c>\\.\PhysicalDrive2</c>).
/// The device handle is opened with <c>GENERIC_READ</c> only, so the operating system itself
/// guarantees nothing can be written through it. Handles the two quirks of device handles:
/// <see cref="Stream.Length"/> is obtained via IOCTL (a device handle cannot report it), and all
/// reads are widened to sector-aligned windows as required for raw disk access.
/// </summary>
public sealed class RawDiskStream : Stream
{
    private readonly SafeFileHandle _handle;
    private readonly long _length;
    private readonly int _sectorSize;
    private long _position;

    private RawDiskStream(SafeFileHandle handle, long length, int sectorSize)
    {
        _handle = handle;
        _length = length;
        _sectorSize = sectorSize;
    }

    public int SectorSize => _sectorSize;

    /// <summary>Opens <c>\\.\PhysicalDriveN</c> read-only. Requires administrator rights.</summary>
    public static RawDiskStream OpenPhysicalDrive(int diskNumber) => Open($@"\\.\PhysicalDrive{diskNumber}");

    /// <summary>Opens a disk device path read-only. Requires administrator rights.</summary>
    public static RawDiskStream Open(string devicePath)
    {
        var handle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GenericRead,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            0, NativeMethods.OpenExisting, 0, 0);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Could not open {devicePath} (Win32 error {error}). Raw disk access requires administrator rights.",
                Marshal.GetHRForLastWin32Error());
        }

        try
        {
            var length = GetLength(handle, devicePath);
            var sectorSize = GetSectorSize(handle);
            return new RawDiskStream(handle, length, sectorSize);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length || buffer.IsEmpty)
        {
            return 0;
        }

        var count = (int)Math.Min(buffer.Length, _length - _position);

        // Widen [Position, Position+count) to a sector-aligned window.
        var alignedStart = _position / _sectorSize * _sectorSize;
        var alignedEnd = Math.Min(_length, (_position + count + _sectorSize - 1) / _sectorSize * _sectorSize);
        var windowLength = (int)(alignedEnd - alignedStart);

        var rented = ArrayPool<byte>.Shared.Rent(windowLength);
        try
        {
            var window = rented.AsSpan(0, windowLength);
            var read = ReadAligned(alignedStart, window);
            var skip = (int)(_position - alignedStart);
            var available = Math.Min(count, Math.Max(0, read - skip));
            if (available <= 0)
            {
                return 0;
            }

            window.Slice(skip, available).CopyTo(buffer);
            _position += available;
            return available;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private int ReadAligned(long position, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = RandomAccess.Read(_handle, buffer[total..], position + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _handle.Dispose();
        }

        base.Dispose(disposing);
    }

    private static long GetLength(SafeFileHandle handle, string devicePath)
    {
        var output = new byte[8];
        if (!NativeMethods.TryIoctl(handle, NativeMethods.IoctlDiskGetLengthInfo, output, out _))
        {
            throw new IOException($"Could not determine the size of {devicePath}.", Marshal.GetHRForLastWin32Error());
        }

        return BinaryPrimitives.ReadInt64LittleEndian(output);
    }

    private static int GetSectorSize(SafeFileHandle handle)
    {
        // DISK_GEOMETRY_EX: DISK_GEOMETRY (24 bytes; BytesPerSector at offset 20) + DiskSize + data.
        var output = new byte[256];
        if (!NativeMethods.TryIoctl(handle, NativeMethods.IoctlDiskGetDriveGeometryEx, output, out _))
        {
            return 512;
        }

        var bytesPerSector = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(20));
        return bytesPerSector > 0 ? bytesPerSector : 512;
    }
}
