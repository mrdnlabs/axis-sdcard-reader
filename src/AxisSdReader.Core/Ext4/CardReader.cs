using System.Buffers.Binary;
using AxisSdReader.Core.Ext4.Luks;
using DiscUtils;
using DiscUtils.Ext;
using DiscUtils.Streams;
using RawDisk = DiscUtils.Raw.Disk;

namespace AxisSdReader.Core.Ext4;

/// <summary>Describes where on the disk the opened ext4 filesystem lives.</summary>
/// <param name="PartitionIndex">Zero-based partition index, or -1 when the filesystem occupies the whole disk (no partition table).</param>
/// <param name="FirstByte">Byte offset of the filesystem from the start of the disk.</param>
/// <param name="LengthBytes">Length of the partition (or whole disk) in bytes.</param>
public sealed record CardVolumeInfo(int PartitionIndex, long FirstByte, long LengthBytes);

/// <summary>
/// Opens an SD card - either a raw disk stream or an image file - and locates the ext4
/// filesystem on it, transparently unlocking a LUKS-encrypted card when a passphrase is supplied.
/// Strictly read-only: the underlying DiscUtils ext implementation has no write paths, LUKS is
/// decrypted only on the way out, and callers are expected to supply read-only streams.
/// </summary>
public sealed class CardReader : IDisposable
{
    private const ushort Ext4SuperblockMagic = 0xEF53;
    private const int SuperblockOffset = 1024;
    private static readonly byte[] LuksMagic = [0x4C, 0x55, 0x4B, 0x53, 0xBA, 0xBE];

    private readonly RawDisk _disk;
    private SparseStream? _partitionStream;
    private IDisposable? _underlying; // for LUKS: the raw partition stream beneath the decrypting stream

    private CardReader(RawDisk disk, CardOpenStatus status, string? failureDetail)
    {
        _disk = disk;
        Status = status;
        FailureDetail = failureDetail;
    }

    private CardReader(RawDisk disk, ExtFileSystem fileSystem, SparseStream partitionStream, CardVolumeInfo volume,
        IDisposable? underlying = null)
    {
        _disk = disk;
        _partitionStream = partitionStream;
        _underlying = underlying;
        FileSystem = fileSystem;
        Volume = volume;
        Status = CardOpenStatus.Ok;
        IsEncrypted = underlying is not null;
    }

    public CardOpenStatus Status { get; }

    /// <summary>Human-readable detail for non-<see cref="CardOpenStatus.Ok"/> outcomes.</summary>
    public string? FailureDetail { get; }

    /// <summary>The opened ext4 filesystem; non-null exactly when <see cref="Status"/> is <see cref="CardOpenStatus.Ok"/>.</summary>
    public ExtFileSystem? FileSystem { get; }

    public CardVolumeInfo? Volume { get; }

    /// <summary>True when the opened filesystem was reached by decrypting a LUKS-encrypted card.</summary>
    public bool IsEncrypted { get; }

    /// <summary>Opens a card image file (.img/.dd) read-only, unlocking LUKS if <paramref name="passphrase"/> is given.</summary>
    public static CardReader OpenImage(string imagePath, string? passphrase = null)
    {
        var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Open(stream, Ownership.Dispose, passphrase);
    }

    /// <summary>
    /// Opens a card from a stream positioned over the whole disk (image file contents or a raw
    /// physical device). The stream must be seekable and report a correct <see cref="Stream.Length"/>.
    /// If the card is LUKS-encrypted, supply <paramref name="passphrase"/> to unlock it.
    /// </summary>
    public static CardReader Open(Stream diskStream, Ownership ownership, string? passphrase = null)
    {
        var disk = new RawDisk(diskStream, ownership);
        try
        {
            return Open(disk, passphrase);
        }
        catch
        {
            disk.Dispose();
            throw;
        }
    }

    private static CardReader Open(RawDisk disk, string? passphrase)
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
        var sawLuks1Locked = false; // a supported (LUKS1) volume is present but locked (no passphrase yet)
        var sawExt = false;
        var havePassphrase = !string.IsNullOrEmpty(passphrase);
        string? extFailure = null;
        string? luksUnsupported = null;

        foreach (var volume in candidates)
        {
            if (HasMagic(content, volume.FirstByte, LuksMagic))
            {
                sawLuks = true;

                // Reject unsupported LUKS versions (e.g. LUKS2) up front, so we don't prompt the user for a
                // passphrase for a card we can't read anyway.
                if (ReadLuksVersion(content, volume.FirstByte) != 1)
                {
                    luksUnsupported = "This card uses an encryption format this version can't read (only LUKS1 is supported).";
                    continue;
                }

                if (!havePassphrase)
                {
                    sawLuks1Locked = true;
                    continue; // detected but locked — the caller must supply a passphrase and retry
                }

                var luks = TryOpenLuks(disk, content, volume, passphrase!, ref luksUnsupported);
                if (luks is not null)
                {
                    return luks; // Ok, or a definitive failure (wrong passphrase)
                }

                continue; // unlock did not yield a usable filesystem — keep scanning
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

        // Prompt for a passphrase when a readable (LUKS1) encrypted volume is present — even if an
        // unsupported (e.g. LUKS2) volume was also seen on the same disk.
        if (sawLuks1Locked)
        {
            return new CardReader(disk, CardOpenStatus.EncryptedNeedsPassphrase,
                "This card is encrypted. Enter the camera's SD card passphrase to unlock it.");
        }

        // Only unsupported-encryption volumes present — report it WITHOUT prompting for a passphrase.
        if (luksUnsupported is not null)
        {
            return new CardReader(disk, CardOpenStatus.Encrypted, luksUnsupported);
        }

        if (sawLuks)
        {
            return new CardReader(disk, CardOpenStatus.IncorrectPassphrase,
                "The passphrase did not unlock the card.");
        }

        if (sawExt)
        {
            return new CardReader(disk, CardOpenStatus.IncompatibleExt4, extFailure);
        }

        return new CardReader(disk, CardOpenStatus.NoExt4FileSystem,
            "No ext4 filesystem was found on the card. It may be unformatted, FAT-formatted, or encrypted without a header.");
    }

    /// <summary>
    /// Attempts to unlock a LUKS volume and open the ext4 filesystem inside it. Returns a ready
    /// <see cref="CardReader"/> on success or on a definitive wrong-passphrase result; returns null (and
    /// sets <paramref name="luksUnsupported"/>) when this volume could not be used and scanning should
    /// continue.
    /// </summary>
    private static CardReader? TryOpenLuks(RawDisk disk, SparseStream content, CardVolumeInfo volume,
        string passphrase, ref string? luksUnsupported)
    {
        var luksPartition = new SubStream(content, volume.FirstByte, volume.LengthBytes);

        LuksUnlockResult unlock;
        try
        {
            unlock = LuksVolume.TryUnlock(luksPartition, passphrase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Any failure parsing/decrypting a hostile or corrupt LUKS header degrades to "can't read it"
            // rather than crashing the app.
            luksPartition.Dispose();
            luksUnsupported = "The card's encryption header could not be read.";
            return null;
        }

        switch (unlock.Status)
        {
            case LuksUnlockStatus.Success when unlock.PlaintextStream is { } plain:
                if (!HasExt4Superblock(plain, 0, plain.Length))
                {
                    plain.Dispose();
                    luksPartition.Dispose();
                    luksUnsupported = "The card was unlocked, but the decrypted volume is not ext4.";
                    return null;
                }

                try
                {
                    var fs = new ExtFileSystem(plain);
                    return new CardReader(disk, fs, plain, volume, luksPartition);
                }
                catch (IOException ex)
                {
                    plain.Dispose();
                    luksPartition.Dispose();
                    luksUnsupported = $"The decrypted filesystem could not be opened: {ex.Message}";
                    return null;
                }

            case LuksUnlockStatus.WrongPassphrase:
                luksPartition.Dispose();
                return new CardReader(disk, CardOpenStatus.IncorrectPassphrase,
                    unlock.Detail ?? "The passphrase is incorrect.");

            default: // Unsupported / NotLuks
                luksPartition.Dispose();
                luksUnsupported = unlock.Detail;
                return null;
        }
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

    /// <summary>Reads the LUKS version (big-endian u16 at header offset 6); 1 = LUKS1, 2 = LUKS2.
    /// Returns 1 if it cannot be read, so the normal unlock path reports the real detail.</summary>
    private static int ReadLuksVersion(Stream disk, long volumeOffset)
    {
        Span<byte> version = stackalloc byte[2];
        return TryReadAt(disk, volumeOffset + 6, version)
            ? BinaryPrimitives.ReadUInt16BigEndian(version)
            : 1;
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
        _partitionStream?.Dispose();  // for LUKS: the decrypting stream (also disposes its cipher SubStream + AES)
        _partitionStream = null;
        _underlying?.Dispose();        // for LUKS: the raw partition SubStream beneath it
        _underlying = null;
        _disk.Dispose();
    }
}
