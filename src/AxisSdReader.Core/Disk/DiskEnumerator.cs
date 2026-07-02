using System.Buffers.Binary;
using System.Text;

namespace AxisSdReader.Core.Disk;

/// <summary>A volume residing on a physical disk, with any mount points (drive letters/folders) it has.</summary>
/// <param name="VolumeGuidPath">The volume GUID path, e.g. <c>\\?\Volume{...}\</c> (with trailing backslash).</param>
/// <param name="MountPoints">Mount points such as <c>E:\</c>; empty for unmounted volumes.</param>
public sealed record VolumeOnDisk(string VolumeGuidPath, IReadOnlyList<string> MountPoints);

/// <summary>A physical disk as seen by Windows.</summary>
public sealed record PhysicalDiskInfo(
    int DiskNumber,
    string DevicePath,
    string FriendlyName,
    StorageBusKind BusType,
    bool IsRemovableMedia,
    long? SizeBytes,
    IReadOnlyList<VolumeOnDisk> Volumes)
{
    public bool IsUsb => BusType == StorageBusKind.Usb;
}

public enum StorageBusKind
{
    Unknown = 0,
    Scsi = 1,
    Atapi = 2,
    Ata = 3,
    Ieee1394 = 4,
    Ssa = 5,
    Fibre = 6,
    Usb = 7,
    Raid = 8,
    iScsi = 9,
    Sas = 10,
    Sata = 11,
    Sd = 12,
    Mmc = 13,
    Nvme = 17,
}

/// <summary>
/// Enumerates physical disks and their volumes using only access-0 device handles,
/// which do not require administrator rights.
/// </summary>
public static class DiskEnumerator
{
    private const int MaxDiskNumber = 64;

    /// <summary>Lists all physical disks. Use <see cref="PhysicalDiskInfo.IsUsb"/> to find card readers.</summary>
    public static IReadOnlyList<PhysicalDiskInfo> GetPhysicalDisks()
    {
        var volumesByDisk = GetVolumesByDisk();
        var disks = new List<PhysicalDiskInfo>();

        for (var n = 0; n < MaxDiskNumber; n++)
        {
            var devicePath = $@"\\.\PhysicalDrive{n}";
            using var handle = NativeMethods.CreateFile(devicePath, 0,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                0, NativeMethods.OpenExisting, 0, 0);

            if (handle.IsInvalid)
            {
                continue;
            }

            var (busType, removable, friendlyName) = QueryDeviceProperty(handle);
            var size = QuerySize(handle);
            volumesByDisk.TryGetValue(n, out var volumes);

            disks.Add(new PhysicalDiskInfo(n, devicePath, friendlyName, busType, removable, size,
                volumes ?? []));
        }

        return disks;
    }

    /// <summary>Maps a disk or volume device path (e.g. from a device-arrival notification) to its disk number.</summary>
    public static int? GetDiskNumber(string devicePath)
    {
        using var handle = NativeMethods.CreateFile(devicePath.TrimEnd('\\'), 0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            0, NativeMethods.OpenExisting, 0, 0);

        if (handle.IsInvalid)
        {
            return null;
        }

        // STORAGE_DEVICE_NUMBER { DeviceType, DeviceNumber, PartitionNumber }
        var output = new byte[12];
        if (!NativeMethods.TryIoctl(handle, NativeMethods.IoctlStorageGetDeviceNumber, output, out _))
        {
            return null;
        }

        return BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(4));
    }

    private static Dictionary<int, List<VolumeOnDisk>> GetVolumesByDisk()
    {
        var result = new Dictionary<int, List<VolumeOnDisk>>();
        var name = new StringBuilder(260);

        using var find = NativeMethods.FindFirstVolume(name, name.Capacity);
        if (find.IsInvalid)
        {
            return result;
        }

        do
        {
            var volumeGuidPath = name.ToString();
            var diskNumbers = GetVolumeDiskNumbers(volumeGuidPath);
            if (diskNumbers.Count == 0)
            {
                continue;
            }

            var volume = new VolumeOnDisk(volumeGuidPath, GetMountPoints(volumeGuidPath));
            foreach (var disk in diskNumbers)
            {
                if (!result.TryGetValue(disk, out var list))
                {
                    result[disk] = list = [];
                }

                list.Add(volume);
            }
        }
        while (NativeMethods.FindNextVolume(find, name.Clear(), name.Capacity));

        return result;
    }

    private static List<int> GetVolumeDiskNumbers(string volumeGuidPath)
    {
        var numbers = new List<int>();

        // CreateFile on a volume requires the path without the trailing backslash.
        using var handle = NativeMethods.CreateFile(volumeGuidPath.TrimEnd('\\'), 0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            0, NativeMethods.OpenExisting, 0, 0);

        if (handle.IsInvalid)
        {
            return numbers;
        }

        // VOLUME_DISK_EXTENTS: count (4) + pad (4) + DISK_EXTENT[] { DiskNumber(4), pad(4), StartingOffset(8), ExtentLength(8) }
        var output = new byte[8 + 24 * 8];
        if (!NativeMethods.TryIoctl(handle, NativeMethods.IoctlVolumeGetVolumeDiskExtents, output, out _))
        {
            return numbers;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(output);
        for (var i = 0; i < count && i < 8; i++)
        {
            numbers.Add(BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8 + i * 24)));
        }

        return numbers;
    }

    private static IReadOnlyList<string> GetMountPoints(string volumeGuidPath)
    {
        var buffer = new char[1024];
        if (!NativeMethods.GetVolumePathNamesForVolumeName(volumeGuidPath, buffer, buffer.Length, out var length))
        {
            return [];
        }

        // REG_MULTI_SZ-style: NUL-separated strings with a final double-NUL.
        var mountPoints = new List<string>();
        var start = 0;
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] == '\0')
            {
                if (i > start)
                {
                    mountPoints.Add(new string(buffer, start, i - start));
                }

                start = i + 1;
            }
        }

        return mountPoints;
    }

    private static (StorageBusKind BusType, bool Removable, string FriendlyName) QueryDeviceProperty(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty (0), QueryType = Standard (0) }
        var query = new byte[12];
        var output = new byte[1024];
        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.IoctlStorageQueryProperty,
                query, query.Length, output, output.Length, out _, 0))
        {
            return (StorageBusKind.Unknown, false, "Unknown device");
        }

        // STORAGE_DEVICE_DESCRIPTOR layout (offsets): RemovableMedia 10, VendorIdOffset 12,
        // ProductIdOffset 16, BusType 28. String offsets are from the start of the descriptor.
        var removable = output[10] != 0;
        var busType = (StorageBusKind)BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(28));

        var vendor = ReadAnsiString(output, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(12)));
        var product = ReadAnsiString(output, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(16)));
        var friendly = $"{vendor} {product}".Trim();

        return (busType, removable, friendly.Length > 0 ? friendly : "Unknown device");
    }

    private static string ReadAnsiString(byte[] buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length)
        {
            return string.Empty;
        }

        var end = Array.IndexOf(buffer, (byte)0, offset);
        if (end < 0)
        {
            end = buffer.Length;
        }

        return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
    }

    private static long? QuerySize(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        // DISK_GEOMETRY_EX.DiskSize at offset 24.
        var output = new byte[256];
        if (!NativeMethods.TryIoctl(handle, NativeMethods.IoctlDiskGetDriveGeometryEx, output, out _))
        {
            return null;
        }

        return BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24));
    }
}
