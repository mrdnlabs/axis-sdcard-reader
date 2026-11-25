using System.Buffers.Binary;
using AxisSdReader.Core.Ext4;

namespace AxisSdReader.Core.Tests.Ext4;

/// <summary>Partition tables and superblocks a crafted card could carry: found or refused within hard limits,
/// before the disk library sees them.</summary>
public class CardLayoutTests
{
    private const int Sector = 512;

    [Fact]
    public void FindsAPrimaryPartitionAndClampsItToTheDisk()
    {
        var disk = new byte[64 * Sector];
        Mbr(disk, (0x83, 8, 1_000_000)); // claims far more sectors than the disk has
        var partitions = CardLayout.FindPartitions(new MemoryStream(disk), Sector, disk.Length)!;
        var only = Assert.Single(partitions);
        Assert.Equal(8 * Sector, only.FirstByte);
        Assert.Equal(disk.Length - 8 * Sector, only.LengthBytes);
    }

    [Fact]
    public void ASuperfloppyHasNoPartitionTable()
    {
        Assert.Null(CardLayout.FindPartitions(new MemoryStream(new byte[64 * Sector]), Sector, 64 * Sector));

        // A boot sector with the 55AA signature but boot code where entries would be: not a partition table.
        var bootSector = new byte[64 * Sector];
        bootSector[510] = 0x55;
        bootSector[511] = 0xAA;
        bootSector[446] = 0xEB;
        Assert.Null(CardLayout.FindPartitions(new MemoryStream(bootSector), Sector, bootSector.Length));
    }

    [Fact]
    public void ASelfLinkedChainOfLogicalPartitionsEnds()
    {
        var disk = new byte[64 * Sector];
        Mbr(disk, (0x05, 4, 40));

        // The extended boot record at sector 4 holds one logical partition and points "next" back at itself.
        var ebr = 4 * Sector;
        disk[ebr + 510] = 0x55;
        disk[ebr + 511] = 0xAA;
        Entry(disk, ebr + 446, 0x83, 2, 8);
        Entry(disk, ebr + 462, 0x05, 0, 40);

        var partitions = CardLayout.FindPartitions(new MemoryStream(disk), Sector, disk.Length)!;
        var logical = Assert.Single(partitions);
        Assert.Equal(6 * Sector, logical.FirstByte);
    }

    [Fact]
    public void AGptClaimingMillionsOfEntriesIsReadOnlyAsFarAsTheLimit()
    {
        var disk = new byte[2048 * Sector];
        Mbr(disk, (0xEE, 1, 2047));
        var header = disk.AsSpan(Sector);
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 92);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], 2);          // entries at LBA 2
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], 50_000_000); // "50 million entries"
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], 128);

        var entry = disk.AsSpan(2 * Sector, 128);
        entry[0] = 0xAF; // any non-zero type GUID
        BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], 100);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], 199);

        var partitions = CardLayout.FindPartitions(new MemoryStream(disk), Sector, disk.Length)!;
        var only = Assert.Single(partitions);
        Assert.Equal(100 * Sector, only.FirstByte);
        Assert.Equal(100 * Sector, only.LengthBytes);
    }

    [Fact]
    public void ATableListingOneRangeManyTimesYieldsItOnce()
    {
        // Every entry pointing at the same volume (with a LUKS header there, each would cost a key derivation).
        var disk = new byte[64 * Sector];
        Mbr(disk, (0x83, 8, 40), (0x83, 8, 40), (0x83, 8, 40), (0x83, 8, 40));
        var only = Assert.Single(CardLayout.FindPartitions(new MemoryStream(disk), Sector, disk.Length)!);
        Assert.Equal(8 * Sector, only.FirstByte);
    }

    [Fact]
    public void AHybridMbrWithoutItsGptKeepsItsMbrPartitions()
    {
        var disk = new byte[64 * Sector];
        Mbr(disk, (0xEE, 1, 7), (0x83, 8, 40)); // a protective entry, but no GPT header at LBA 1
        var only = Assert.Single(CardLayout.FindPartitions(new MemoryStream(disk), Sector, disk.Length)!);
        Assert.Equal(8 * Sector, only.FirstByte);
        Assert.Equal(40 * Sector, only.LengthBytes);
    }

    [Fact]
    public void BigallocCountsClustersPerGroupNotBlocks()
    {
        const long volume = 64L << 20;
        // 4 KiB blocks in 16-block clusters: 32,768 clusters (one bitmap block) but 524,288 blocks per group.
        var bigalloc = Superblock(blocks: 16384, logBlockSize: 2, blocksPerGroup: 524_288, clustersPerGroup: 32_768, roCompat: 0x200);
        Assert.Null(CardLayout.CheckSuperblock(new MemoryStream(bigalloc), 0, volume));

        // The same group size without bigalloc would need a bitmap 16 blocks long: refused.
        var plain = Superblock(blocks: 16384, logBlockSize: 2, blocksPerGroup: 524_288);
        Assert.NotNull(CardLayout.CheckSuperblock(new MemoryStream(plain), 0, volume));
    }

    [Fact]
    public void ASlightlyTruncatedImageStillOpens()
    {
        const long volume = 64L << 20;
        Assert.Null(CardLayout.CheckSuperblock(new MemoryStream(Superblock(blocks: 16384 + 1000, logBlockSize: 2)), 0, volume));
        Assert.NotNull(CardLayout.CheckSuperblock(new MemoryStream(Superblock(blocks: 16384 + 40_000, logBlockSize: 2)), 0, volume));
    }

    [Fact]
    public void RefusesSuperblocksThatDontFitTheirPartition()
    {
        const long volume = 64L << 20;
        Assert.Null(CardLayout.CheckSuperblock(new MemoryStream(Superblock(blocks: 16384, logBlockSize: 2)), 0, volume)); // 64 MiB of 4 KiB blocks

        Assert.NotNull(CardLayout.CheckSuperblock(new MemoryStream(Superblock(blocks: 1_000_000, logBlockSize: 2)), 0, volume)); // 4 GB claimed
        Assert.NotNull(CardLayout.CheckSuperblock(new MemoryStream(Superblock(blocks: 16384, logBlockSize: 30)), 0, volume));   // absurd block size
        Assert.NotNull(CardLayout.CheckSuperblock(new MemoryStream(Superblock(blocks: 16384, logBlockSize: 2, blocksPerGroup: 0)), 0, volume));
        Assert.NotNull(CardLayout.CheckSuperblock(new MemoryStream(Superblock(blocks: 1_000_000, logBlockSize: 0, blocksPerGroup: 1)), 0, 1L << 40)); // a million groups
    }

    private static void Mbr(byte[] disk, params (byte Type, uint Start, uint Count)[] entries)
    {
        for (var i = 0; i < entries.Length; i++)
        {
            Entry(disk, 446 + i * 16, entries[i].Type, entries[i].Start, entries[i].Count);
        }

        disk[510] = 0x55;
        disk[511] = 0xAA;
    }

    private static void Entry(byte[] disk, int offset, byte type, uint start, uint count)
    {
        disk[offset + 4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 8), start);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 12), count);
    }

    private static byte[] Superblock(uint blocks, uint logBlockSize, uint blocksPerGroup = 32768, uint? clustersPerGroup = null,
        uint roCompat = 0)
    {
        var volume = new byte[4096];
        var sb = volume.AsSpan(1024);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], blocks);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[24..], logBlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], blocksPerGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], clustersPerGroup ?? blocksPerGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[100..], roCompat);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], 8192);
        BinaryPrimitives.WriteUInt16LittleEndian(sb[56..], 0xEF53);
        return volume;
    }
}
