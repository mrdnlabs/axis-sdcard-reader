using System.Security.Cryptography;
using System.Text;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Tests.Fixtures;
using DiscUtils.Streams;

namespace AxisSdReader.Core.Tests.Ext4;

[Collection("CardImage")]
public class CardReaderTests
{
    private const long BigFileSize = 5L * 1024 * 1024 * 1024;

    private readonly CardImageFixture _fixture;

    public CardReaderTests(CardImageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void OpensExt4PartitionOnMbrDisk()
    {
        using var card = CardReader.OpenImage(_fixture.ImagePath);

        Assert.Equal(CardOpenStatus.Ok, card.Status);
        Assert.NotNull(card.FileSystem);
        Assert.NotNull(card.Volume);
        Assert.Equal(0, card.Volume!.PartitionIndex);
        Assert.Equal(1024 * 1024, card.Volume.FirstByte);
    }

    [Fact]
    public void ReadsTheCardTheWayAGeneralExt4LibraryDoes()
    {
        // The app reads cards with its own ext4 reader. On a real card image it must see exactly what a general-purpose
        // ext4 library sees (DiscUtils, here only as a reference): every folder's entries, their kinds and inodes, and
        // every file's length and bytes - extents, a 5 GiB sparse file with holes and all.
        using var card = CardReader.OpenImage(_fixture.ImagePath);
        var fs = card.FileSystem!;
        using var image = File.OpenRead(_fixture.ImagePath);
        using var library = new DiscUtils.Ext.ExtFileSystem(new SubStream(image, card.Volume!.FirstByte, card.Volume.LengthBytes));
        var unix = (DiscUtils.IUnixFileSystem)library;
        var folders = 0;
        var files = 0;
        void Check(string path, long inode)
        {
            folders++;
            var entries = fs.ReadDirectory(inode, out var cost);
            Assert.NotNull(entries);
            Assert.InRange(cost, 1024, Ext4Volume.MaxDirectoryBytes);
            Assert.Equal(library.GetDirectories(path).Select(Path.GetFileName).Order(StringComparer.Ordinal),
                entries!.Where(e => e.Kind == CardEntryKind.Directory).Select(e => e.Name).Order(StringComparer.Ordinal));
            Assert.Equal(library.GetFiles(path).Select(Path.GetFileName).Order(StringComparer.Ordinal),
                entries.Where(e => e.Kind == CardEntryKind.File).Select(e => e.Name).Order(StringComparer.Ordinal));
            foreach (var entry in entries)
            {
                var child = path.TrimEnd('\\') + "\\" + entry.Name;
                Assert.Equal(unix.GetUnixFileInfo(child).Inode, entry.Inode);
                Assert.Equal(entry.Inode, fs.Resolve(child));
                if (entry.Kind == CardEntryKind.File)
                {
                    files++;
                    Assert.Equal(library.GetFileLength(child), fs.FileLength(entry.Inode));
                    using var ours = fs.OpenFile(entry.Inode)!;
                    using var theirs = library.OpenFile(child, FileMode.Open, FileAccess.Read);
                    Assert.Equal(Hash(theirs), Hash(ours));
                }
                else if (entry.Kind == CardEntryKind.Directory)
                {
                    Assert.Null(fs.FileLength(entry.Inode)); // a folder isn't a file
                    Assert.Null(fs.OpenFile(entry.Inode));
                    Check(child, entry.Inode);
                }
            }
        }

        Check(@"\", Ext4Volume.RootInode);
        Assert.True(folders > 10 && files > 10, $"{folders} folders, {files} files");
        Assert.Equal(library.VolumeLabel, fs.Label ?? "");
    }

    /// <summary>A SHA-256 of a stream's contents, read in uneven pieces (so reads straddle blocks and runs).</summary>
    private static string Hash(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[100_003];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    [Fact]
    public async Task AFolderWithAHoleIsRefusedAndTheRestOfTheCardStillOpens()
    {
        // A recording folder whose size claims a second block that isn't there (a crafted card's trick that makes some
        // ext4 readers loop forever on the zeros of the hole): the folder is refused, the walk skips that recording and
        // finishes.
        var copy = Path.Combine(Path.GetTempPath(), "axis-hole-" + Guid.NewGuid().ToString("N") + ".img");
        File.Copy(_fixture.ImagePath, copy);
        try
        {
            const string flat = "20250302_180000_0C3D_B8A44F998877";
            long inode;
            using (var card = CardReader.OpenImage(copy))
            {
                inode = card.FileSystem!.ReadDirectory(Ext4Volume.RootInode, out _)!.Single(e => e.Name == flat).Inode;
            }

            using (var image = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                var at = InodeOffset(image, inode);
                var size = new byte[4];
                image.Position = at + 4;
                image.ReadExactly(size);
                image.Position = at + 4;
                image.Write(BitConverter.GetBytes(BitConverter.ToUInt32(size) * 2)); // one more block than it maps
            }

            using var reader = CardReader.OpenImage(copy);
            Assert.Null(reader.FileSystem!.ReadDirectory(inode, out _));
            // (With a timeout: before the fix, the library spun forever here.)
            var index = await Task.Run(() => AxisSdReader.Core.Axis.AxisCardIndexer.Index(reader)).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.DoesNotContain(index.Recordings, r => r.Id.Raw == flat);
            Assert.NotEmpty(index.Recordings); // the rest of the card is still read
            Assert.True(index.UnreadableEntries > 0);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void ExtentTreesAreWalkedOncePerNodeAndOnlyWithinTheFile()
    {
        // A crafted tree can point thousands of entries at one node, or hang entries past the end of a small file, to make
        // every open of it read megabytes; every open is also charged by what it read. On a copy of a real chunk file:
        // a root of two index entries over leaves (empty ones, which fit any range) in the chunk's own first blocks.
        var copy = Path.Combine(Path.GetTempPath(), "axis-extents-" + Guid.NewGuid().ToString("N") + ".img");
        File.Copy(_fixture.ImagePath, copy);
        CardReader? reader = null;
        try
        {
            long inode, blockSize, first, blocks;
            using (var card = CardReader.OpenImage(copy))
            {
                var fs = card.FileSystem!;
                inode = fs.Resolve(@"\20250114\09\20250114_093000_1A2B_ACCC8E123456\20250114_09\20250114_093000_5B35.mkv")!.Value;
                blockSize = fs.BlockSize;
                blocks = (fs.FileLength(inode)!.Value + blockSize - 1) / blockSize;
            }

            using (var image = new FileStream(copy, FileMode.Open, FileAccess.Read))
            {
                var root = new byte[60];
                image.Position = InodeOffset(image, inode) + 0x28;
                image.ReadExactly(root);
                Assert.Equal(0, BitConverter.ToUInt16(root, 6)); // the chunk's own map: one level, its extents in the inode
                Assert.True(BitConverter.ToUInt16(root, 16) >= 2 && blocks >= 2, "the chunk spans at least two blocks");
                first = (long)BitConverter.ToUInt16(root, 18) << 32 | BitConverter.ToUInt32(root, 20);
            }

            const long volume = 1024 * 1024; // the fixture's partition
            Ext4Volume Reopen(params (uint Key, long Node)[] entries)
            {
                reader?.Dispose();
                using (var image = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
                {
                    foreach (var node in entries.Select(e => e.Node).Distinct())
                    {
                        var leaf = new byte[blockSize]; // an empty leaf
                        BitConverter.GetBytes((ushort)0xF30A).CopyTo(leaf, 0);
                        BitConverter.GetBytes((ushort)((blockSize - 12) / 12)).CopyTo(leaf, 4);
                        image.Position = volume + node * blockSize;
                        image.Write(leaf);
                    }

                    var root = new byte[60];
                    BitConverter.GetBytes((ushort)0xF30A).CopyTo(root, 0);
                    BitConverter.GetBytes((ushort)entries.Length).CopyTo(root, 2);
                    BitConverter.GetBytes((ushort)4).CopyTo(root, 4);
                    BitConverter.GetBytes((ushort)1).CopyTo(root, 6); // depth 1: index entries
                    for (var i = 0; i < entries.Length; i++)
                    {
                        BitConverter.GetBytes(entries[i].Key).CopyTo(root, 12 + i * 12);
                        BitConverter.GetBytes((uint)entries[i].Node).CopyTo(root, 12 + i * 12 + 4);
                    }

                    image.Position = InodeOffset(image, inode) + 0x28;
                    image.Write(root);
                }

                reader = CardReader.OpenImage(copy);
                return reader.FileSystem!;
            }

            // Two entries, two leaves: a file of holes, and both nodes (and the inode) are charged.
            using (var opened = Reopen((0, first), (1, first + 1)).OpenFile(inode, out var cost))
            {
                Assert.NotNull(opened);
                Assert.Equal(3 * blockSize, cost);
            }

            // The same node twice: refused.
            Assert.Null(Reopen((0, first), (1, first)).OpenFile(inode));

            // An index level with no entries at all: refused, as Linux does.
            Assert.Null(Reopen().OpenFile(inode));

            // An entry past the end of the file is never followed - even to a node already read.
            Assert.NotNull(Reopen((0, first), ((uint)blocks + 5, first)).OpenFile(inode));
        }
        finally
        {
            reader?.Dispose();
            File.Delete(copy);
        }
    }

    [Fact]
    public void AnEmptyChunkFileIsLeftOutOfItsRecording()
    {
        // A camera that loses power just after starting a chunk file often leaves it empty: no footage, and nothing FFmpeg
        // can open. It must not join the recording (where it would stretch the timeline and fail exports reaching it).
        var copy = Path.Combine(Path.GetTempPath(), "axis-emptychunk-" + Guid.NewGuid().ToString("N") + ".img");
        File.Copy(_fixture.ImagePath, copy);
        try
        {
            const string name = "20250114_093000_5B35.mkv";
            long inode;
            using (var card = CardReader.OpenImage(copy))
            {
                inode = card.FileSystem!.Resolve(@"\20250114\09\20250114_093000_1A2B_ACCC8E123456\20250114_09\" + name)!.Value;
                var before = AxisSdReader.Core.Axis.AxisCardIndexer.Index(card);
                Assert.Contains(before.Recordings.SelectMany(r => r.Chunks), c => c.FileName == name);
            }

            using (var image = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                var at = InodeOffset(image, inode);
                image.Position = at + 4;
                image.Write(BitConverter.GetBytes(0u)); // i_size_lo
                image.Position = at + 0x6C;
                image.Write(BitConverter.GetBytes(0u)); // i_size_high
            }

            using var reader = CardReader.OpenImage(copy);
            var index = AxisSdReader.Core.Axis.AxisCardIndexer.Index(reader);
            var recording = index.Recordings.Single(r => r.Id.Raw == "20250114_093000_1A2B_ACCC8E123456");
            Assert.DoesNotContain(recording.Chunks, c => c.FileName == name);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void AFileClaimingMoreThanItsMapCanHoldIsRefused()
    {
        // A size near 2^63 would overflow the block count: it's refused, not wrapped around.
        var copy = Path.Combine(Path.GetTempPath(), "axis-hugefile-" + Guid.NewGuid().ToString("N") + ".img");
        File.Copy(_fixture.ImagePath, copy);
        try
        {
            long inode;
            using (var card = CardReader.OpenImage(copy))
            {
                inode = card.FileSystem!.Resolve(@"\index.db")!.Value;
            }

            using (var image = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                var at = InodeOffset(image, inode);
                image.Position = at + 4;
                image.Write(BitConverter.GetBytes(0xFFFFF001u)); // i_size_lo
                image.Position = at + 0x6C;
                image.Write(BitConverter.GetBytes(0x7FFFFFFFu)); // i_size_high
            }

            using var reader = CardReader.OpenImage(copy);
            Assert.Null(reader.FileSystem!.FileLength(inode));
            Assert.Null(reader.FileSystem.OpenFile(inode));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <summary>The byte offset of an inode in the fixture image (its partition starts at 1 MiB).</summary>
    private static long InodeOffset(FileStream image, long inode)
    {
        const long volume = 1024 * 1024;
        var sb = new byte[1024];
        image.Position = volume + 1024;
        image.ReadExactly(sb);
        var blockSize = 1024L << (int)BitConverter.ToUInt32(sb, 24);
        var firstDataBlock = BitConverter.ToUInt32(sb, 20);
        var inodesPerGroup = BitConverter.ToUInt32(sb, 40);
        var inodeSize = BitConverter.ToUInt32(sb, 76) == 0 ? 128 : BitConverter.ToUInt16(sb, 88);
        var is64Bit = (BitConverter.ToUInt32(sb, 96) & 0x80) != 0;
        var descriptorSize = is64Bit ? BitConverter.ToUInt16(sb, 0xFE) : 32;
        var group = (inode - 1) / inodesPerGroup;
        var descriptor = new byte[32];
        image.Position = volume + (firstDataBlock + 1) * blockSize + group * descriptorSize;
        image.ReadExactly(descriptor);
        var table = (long)BitConverter.ToUInt32(descriptor, 8);
        return volume + table * blockSize + (inode - 1) % inodesPerGroup * inodeSize;
    }

    [Fact]
    public void RefusesACardWhoseTopFolderIsHuge()
    {
        // The library reads a folder whole the first time it touches it: a top folder claiming a gigabyte must be refused
        // before that, from the card's own inode table.
        var copy = Path.Combine(Path.GetTempPath(), "axis-hugeroot-" + Guid.NewGuid().ToString("N") + ".img");
        File.Copy(_fixture.ImagePath, copy);
        try
        {
            using (var image = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                const long volume = 1024 * 1024; // the fixture's partition
                var sb = new byte[1024];
                image.Position = volume + 1024;
                image.ReadExactly(sb);
                var blockSize = 1024L << (int)BitConverter.ToUInt32(sb, 24);
                var firstDataBlock = BitConverter.ToUInt32(sb, 20);
                var inodeSize = BitConverter.ToUInt32(sb, 76) == 0 ? 128 : BitConverter.ToUInt16(sb, 88);
                var descriptor = new byte[32];
                image.Position = volume + (firstDataBlock + 1) * blockSize;
                image.ReadExactly(descriptor);
                var inodeTable = (long)BitConverter.ToUInt32(descriptor, 8);
                image.Position = volume + inodeTable * blockSize + 1 * inodeSize + 4; // inode 2's i_size_lo
                image.Write(BitConverter.GetBytes(0x4000_0000u)); // 1 GiB
            }

            using var card = CardReader.OpenImage(copy);
            Assert.Equal(CardOpenStatus.IncompatibleExt4, card.Status);
            Assert.Contains("top folder", card.FailureDetail);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void ListsRootDirectories()
    {
        using var card = CardReader.OpenImage(_fixture.ImagePath);
        var root = card.FileSystem!.ReadDirectory(Ext4Volume.RootInode, out _)!;

        var dirs = root.Where(e => e.Kind == CardEntryKind.Directory && e.Name != "lost+found")
            .Select(e => e.Name)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "20250114",
                "20250115",
                "20250302_180000_0C3D_B8A44F998877",
                "areas",
                "music",
                "osr",
                "recording_groups",
                "ws",
            ],
            dirs);

        var rootFiles = root.Where(e => e.Kind == CardEntryKind.File).Select(e => e.Name).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(["bigfile.bin", "index.db"], rootFiles);
    }

    [Fact]
    public void ReadsChunkFileContent()
    {
        using var card = CardReader.OpenImage(_fixture.ImagePath);
        var fs = card.FileSystem!;

        var path = @"\20250114\09\20250114_093000_1A2B_ACCC8E123456\20250114_09\20250114_093000_5B35.mkv";
        var inode = fs.Resolve(path);
        Assert.NotNull(inode);
        Assert.True(fs.FileLength(inode!.Value) > 1024);

        using var stream = fs.OpenFile(inode.Value)!;
        var header = new byte[4];
        stream.ReadExactly(header);
        Assert.Equal(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, header); // EBML magic
    }

    [Fact]
    public void ReadsLargeSparseFileSizeAndTail()
    {
        using var card = CardReader.OpenImage(_fixture.ImagePath);
        var fs = card.FileSystem!;

        // File size beyond 4 GiB proves 64-bit size handling (i_size_high).
        var inode = fs.Resolve(@"\bigfile.bin")!.Value;
        Assert.Equal(BigFileSize, fs.FileLength(inode));

        using var stream = fs.OpenFile(inode)!;

        stream.Position = BigFileSize - 16;
        var tail = new byte[16];
        stream.ReadExactly(tail);
        Assert.Equal("AXIS-TAIL-MARKER", Encoding.ASCII.GetString(tail));

        // A hole in the middle of the sparse file must read as zeros.
        stream.Position = BigFileSize / 2;
        var middle = new byte[4096];
        stream.ReadExactly(middle);
        Assert.All(middle, b => Assert.Equal(0, b));
    }

    [Fact]
    public void BlankImageReportsNoFileSystem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blank-{Guid.NewGuid():N}.img");
        try
        {
            using (var f = File.Create(path))
            {
                f.SetLength(4 * 1024 * 1024);
            }

            using var card = CardReader.OpenImage(path);
            Assert.Equal(CardOpenStatus.NoExt4FileSystem, card.Status);
            Assert.Null(card.FileSystem);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
