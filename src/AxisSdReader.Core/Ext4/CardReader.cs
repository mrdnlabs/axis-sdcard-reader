using System.Buffers.Binary;
using DiscUtils;
using DiscUtils.Ext;
using DiscUtils.Raw;
using DiscUtils.Streams;

namespace AxisSdReader.Core.Ext4;

/// <summary>Describes where on the disk the opened ext4 filesystem lives.</summary>
/// <param name="PartitionIndex">Zero-based partition index, or -1 when the filesystem occupies the whole disk (no partition table).</param>
/// <param name="FirstByte">Byte offset of the filesystem from the start of the disk.</param>
/// <param name="LengthBytes">Length of the partition (or whole disk) in bytes.</param>
public sealed record CardVolumeInfo(int PartitionIndex, long FirstByte, long LengthBytes);

/// <summary>
/// Opens an SD card — either a raw disk stream or an image file — and locates the ext4
/// filesystem on it. Strictly read-only: the underlying DiscUtils ext implementation has
/// no write paths, and callers are expected to supply read-only streams.
/// </summary>
public sealed class CardReader : IDisposable
{
    private const ushort Ext4SuperblockMagic = 0xEF53;
    private const int SuperblockOffset = 1024;
    private static readonly byte[] LuksMagic = [0x4C, 0x55, 0x4B, 0x53, 0xBA, 0xBE];

    private readonly Disk _disk;
    private SparseStream? _partitionStream;

    private CardReader(Disk disk, CardOpenStatus status, string? failureDetail)
    {
        _disk = disk;
        Status = status;
        FailureDetail = failureDetail;
    }

    private CardReader(Disk disk, ExtFileSystem fileSystem, SparseStream partitionStream, CardVolumeInfo volume)
    {
        _disk = disk;
        _partitionStream = partitionStream;
        FileSystem = fileSystem;
        Volume = volume;
        Status = CardOpenStatus.Ok;
    }

    public CardOpenStatus Status { get; }

    /// <summary>Human-readable detail for non-<see cref="CardOpenStatus.Ok"/> outcomes.</summary>
    public string? FailureDetail { get; }

    /// <summary>The opened ext4 filesystem; non-null exactly when <see cref="Status"/> is <see cref="CardOpenStatus.Ok"/>.</summary>
    public ExtFileSystem? FileSystem { get; }

    public CardVolumeInfo? Volume { get; }

    /// <summary>Opens a card image file (.img/.dd) read-only.</summary>
    public static CardReader OpenImage(string imagePath)
    {
        var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Open(stream, Ownership.Dispose);
    }

    /// <summary>
    /// Opens a card from a stream positioned over the whole disk (image file contents or a raw
    /// physical device). The stream must be seekable and report a correct <see cref="Stream.Length"/>.
    /// </summary>
    public static CardReader Open(Stream diskStream, Ownership ownership)
    {
        var disk = new Disk(diskStream, ownership);
        try
        {
            return Open(disk);
        }
        catch
        {
            disk.Dispose();
            throw;
        }
    }

    private static CardReader Open(Disk disk)
    {
        var content = disk.Content;

        // A partitionless ("superfloppy") card has the filesystem at byte 0.
        var candidates = new List<CardVolumeInfo>();
        if (disk.IsPartitioned && disk.Partitions is { } table)
        {
            for (var i = 0; i < table.Count; i++)
            {
                var p = table[i];
                candidates.Add(new CardVolumeInfo(i, p.FirstSector * disk.SectorSize,
                    (p.LastSector - p.FirstSector + 1) * disk.SectorSize));
            }
        }
        else
        {
            candidates.Add(new CardVolumeInfo(-1, 0, disk.Capacity));
        }

        var sawLuks = false;
        var sawExt = false;
        string? extFailure = null;

        foreach (var volume in candidates)
        {
            if (HasMagic(content, volume.FirstByte, LuksMagic))
            {
                sawLuks = true;
                continue;
            }

            if (!HasExt4Superblock(content, volume.FirstByte, volume.LengthBytes))
            {
                continue;
            }

            sawExt = true;
            var partitionStream = new SubStream(content, volume.FirstByte, volume.LengthBytes);
            try
            {
                var fs = new ExtFileSystem(partitionStream);
                return new CardReader(disk, fs, partitionStream, volume);
            }
            catch (IOException ex)
            {
                // DiscUtils throws for incompatible INCOMPAT feature flags and structural corruption.
                extFailure = ex.Message;
            }
        }

        if (sawLuks)
        {
            return new CardReader(disk, CardOpenStatus.Encrypted,
                "The card is LUKS-encrypted and cannot be read outside the camera.");
        }

        if (sawExt)
        {
            return new CardReader(disk, CardOpenStatus.IncompatibleExt4, extFailure);
        }

        return new CardReader(disk, CardOpenStatus.NoExt4FileSystem,
            "No ext4 filesystem was found on the card. It may be unformatted, FAT-formatted, or encrypted without a header.");
    }

    private static bool HasExt4Superblock(Stream disk, long volumeOffset, long volumeLength)
    {
        // The ext superblock lives 1024 bytes into the volume; the magic is at superblock offset 56.
        if (volumeLength < SuperblockOffset + 1024)
        {
            return false;
        }

        Span<byte> magic = stackalloc byte[2];
        if (!TryReadAt(disk, volumeOffset + SuperblockOffset + 56, magic))
        {
            return false;
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(magic) == Ext4SuperblockMagic;
    }

    private static bool HasMagic(Stream disk, long offset, ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = stackalloc byte[LuksMagic.Length];
        return TryReadAt(disk, offset, actual) && actual.SequenceEqual(expected);
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

    public void Dispose()
    {
        FileSystem?.Dispose();
        _partitionStream?.Dispose();
        _partitionStream = null;
        _disk.Dispose();
    }
}
