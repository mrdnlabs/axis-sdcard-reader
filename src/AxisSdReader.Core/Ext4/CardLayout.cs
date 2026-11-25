using System.Buffers.Binary;

namespace AxisSdReader.Core.Ext4;

/// <summary>
/// Bounded checks of the card's layout, done before the disk library sees it. The library's partition code
/// follows chains of extended partitions without a limit and sizes allocations from table fields, and its ext4
/// code allocates the group-descriptor table straight from superblock fields: a crafted card could exhaust memory
/// (or worse) just by being inserted. So the partitions are found here, with hard limits, and an ext4 superblock
/// must describe a filesystem that fits its partition before the library is allowed to open it.
/// </summary>
internal static class CardLayout
{
    private const int MaxGptEntries = 128;
    private const int MaxLogicalPartitions = 16;
    private const int MaxBlockGroups = 1 << 18;          // 262,144 groups: 8 TiB at 4 KiB blocks
    private const long MaxDescriptorTableBytes = 16L << 20;

    /// <summary>
    /// The partitions on the disk as byte ranges (clamped to <paramref name="capacity"/>), or null when it has no
    /// partition table (a "superfloppy" card, filesystem at byte 0). MBR primary and logical partitions (a bounded
    /// walk) and GPT entries (at most <see cref="MaxGptEntries"/>) are found; nothing else is followed.
    /// </summary>
    public static List<CardVolumeInfo>? FindPartitions(Stream disk, int sectorSize, long capacity)
    {
        var mbr = new byte[512];
        if (!TryReadAt(disk, 0, mbr) || mbr[510] != 0x55 || mbr[511] != 0xAA)
        {
            return null;
        }

        // A boot sector that isn't a partition table (a superfloppy FAT/NTFS volume) has other bytes here.
        for (var i = 0; i < 4; i++)
        {
            if (mbr[446 + i * 16] is not (0x00 or 0x80))
            {
                return null;
            }
        }

        var result = new List<CardVolumeInfo>();
        for (var i = 0; i < 4; i++)
        {
            var entry = 446 + i * 16;
            var type = mbr[entry + 4];
            var start = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(entry + 8));
            var count = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(entry + 12));
            if (type == 0 || count == 0)
            {
                continue;
            }

            if (type == 0xEE)
            {
                // GPT, protected by this entry. A hybrid MBR whose GPT header is missing keeps its MBR partitions.
                if (FindGptPartitions(disk, sectorSize, capacity) is { } gpt)
                {
                    return gpt;
                }

                continue;
            }

            if (type is 0x05 or 0x0F or 0x85)
            {
                AddLogicalPartitions(disk, sectorSize, capacity, start, result);
                continue;
            }

            Add(result, i, (long)start * sectorSize, (long)count * sectorSize, capacity);
        }

        return Distinct(result);
    }

    /// <summary>The partitions with each byte range once (a table can list the same range many times), or null.</summary>
    private static List<CardVolumeInfo>? Distinct(List<CardVolumeInfo> partitions)
    {
        var distinct = partitions.DistinctBy(p => (p.FirstByte, p.LengthBytes)).ToList();
        return distinct.Count > 0 ? distinct : null;
    }

    /// <summary>Logical partitions: a chain of extended boot records, walked at most a few steps and never twice
    /// through the same sector.</summary>
    private static void AddLogicalPartitions(Stream disk, int sectorSize, long capacity, uint extendedStart, List<CardVolumeInfo> result)
    {
        var visited = new HashSet<long>();
        var ebrSector = (long)extendedStart;
        var ebr = new byte[512];
        for (var n = 0; n < MaxLogicalPartitions && visited.Add(ebrSector); n++)
        {
            if (!TryReadAt(disk, ebrSector * sectorSize, ebr) || ebr[510] != 0x55 || ebr[511] != 0xAA)
            {
                return;
            }

            var type = ebr[446 + 4];
            var start = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(446 + 8));
            var count = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(446 + 12));
            if (type != 0 && count != 0)
            {
                Add(result, 4 + n, (ebrSector + start) * sectorSize, (long)count * sectorSize, capacity);
            }

            var nextType = ebr[462 + 4];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(462 + 8));
            if (nextType is not (0x05 or 0x0F or 0x85) || next == 0)
            {
                return;
            }

            ebrSector = extendedStart + (long)next;
        }
    }

    private static List<CardVolumeInfo>? FindGptPartitions(Stream disk, int sectorSize, long capacity)
    {
        // The header is in LBA 1; try the disk's sector size, then the other common one.
        foreach (var size in new[] { sectorSize, sectorSize == 512 ? 4096 : 512 })
        {
            var header = new byte[92];
            if (!TryReadAt(disk, size, header) || !header.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
            {
                continue;
            }

            var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
            var count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80)), MaxGptEntries);
            var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
            if (entrySize is < 128 or > 1024 || entrySize % 8 != 0 || entriesLba == 0 || entriesLba > (ulong)(capacity / size))
            {
                return null;
            }

            var table = new byte[count * entrySize];
            if (!TryReadAt(disk, (long)entriesLba * size, table))
            {
                return null;
            }

            var result = new List<CardVolumeInfo>();
            for (var i = 0; i < count; i++)
            {
                var entry = table.AsSpan((int)(i * entrySize), (int)entrySize);
                if (!entry[..16].ContainsAnyExcept((byte)0))
                {
                    continue; // unused entry
                }

                var first = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]);
                var last = BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]);
                if (last < first || first > (ulong)(capacity / size))
                {
                    continue;
                }

                var lengthSectors = Math.Min(last - first + 1, (ulong)(capacity / size));
                Add(result, i, (long)first * size, (long)lengthSectors * size, capacity);
            }

            return Distinct(result);
        }

        return null;
    }

    private static void Add(List<CardVolumeInfo> result, int index, long first, long length, long capacity)
    {
        if (first < 0 || length <= 0 || first >= capacity)
        {
            return;
        }

        result.Add(new CardVolumeInfo(index, first, Math.Min(length, capacity - first)));
    }

    /// <summary>
    /// Null when the ext4 superblock at <paramref name="volumeOffset"/> describes a filesystem that fits its
    /// partition and whose metadata the library can size sensibly; otherwise why not. Only the fields that drive
    /// allocations and arithmetic are checked - the library validates the rest.
    /// </summary>
    public static string? CheckSuperblock(Stream disk, long volumeOffset, long volumeLength)
    {
        var sb = new byte[1024];
        if (!TryReadAt(disk, volumeOffset + 1024, sb))
        {
            return "The filesystem's superblock could not be read.";
        }

        var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24));
        if (logBlockSize > 6)
        {
            return "The filesystem's block size is invalid.";
        }

        long blockSize = 1024L << (int)logBlockSize;
        var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32));
        var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(40));
        var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20));
        var incompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(96));
        var bigalloc = (BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(100)) & 0x200) != 0; // RO_COMPAT_BIGALLOC
        var clustersPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(36));
        var is64Bit = (incompat & 0x80) != 0;
        var blocks = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4)) |
                     (is64Bit ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x150)) << 32 : 0);
        long descriptorSize = is64Bit ? BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(0xFE)) : 32;

        // A group's bitmap is one block: at most 8 bits per byte of it - of clusters, with bigalloc.
        var perGroupBitmapped = bigalloc ? clustersPerGroup : blocksPerGroup;
        if (blocksPerGroup == 0 || perGroupBitmapped == 0 || perGroupBitmapped > 8 * blockSize ||
            inodesPerGroup == 0 || inodesPerGroup > 8 * blockSize)
        {
            return "The filesystem's group layout is invalid.";
        }

        // At most one block group past the end of the partition (a slightly truncated image still opens; reads past
        // the end just fail), so the sizes the library allocates from stay tied to the partition's real size.
        if (blocks == 0 || firstDataBlock >= blocks || blocks > (ulong)(volumeLength / blockSize) + blocksPerGroup)
        {
            return "The filesystem is larger than its partition.";
        }

        if (descriptorSize is < 32 or > 1024 || (descriptorSize & (descriptorSize - 1)) != 0)
        {
            return "The filesystem's group descriptors are invalid.";
        }

        var groups = (blocks - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup;
        if (groups > MaxBlockGroups || (long)groups * descriptorSize > MaxDescriptorTableBytes)
        {
            return "The filesystem has more block groups than this app reads.";
        }

        return null;
    }

    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
