using System.Buffers.Binary;
using System.Text;

namespace AxisSdReader.Core.Ext4;

/// <summary>What a folder entry on the card is, as the card's own structures say.</summary>
public enum CardEntryKind
{
    Directory,
    File,

    /// <summary>A symbolic link, device, pipe or anything else: never followed, never opened.</summary>
    Other,
}

/// <summary>One entry of a folder on the card.</summary>
public sealed record CardDirEntry(string Name, long Inode, CardEntryKind Kind);

/// <summary>
/// A read-only ext4 (or ext3/ext2) volume, read by the app itself. Everything on a card is untrusted, so this reads only
/// what the app needs - folders and regular files, by inode number - and checks every structure it uses: block and
/// inode numbers against the volume, extent trees for order, overlap, depth and size, block maps for range, folder
/// records for fit. Nothing is ever followed by name (a symbolic link is just an entry of kind
/// <see cref="CardEntryKind.Other"/>), nothing is ever written, and every read is bounded. Not thread-safe: use it only
/// where the card's stream may be read (under the card's I/O gate once the card is shared).
/// </summary>
public sealed class Ext4Volume
{
    /// <summary>The largest folder read. A camera's biggest - a recording's folder of chunks in the old flat layout -
    /// stays well below it.</summary>
    public const long MaxDirectoryBytes = 16L << 20;

    /// <summary>The top folder's inode number.</summary>
    public const long RootInode = 2;

    private const int MaxEntriesPerDirectory = 1_000_000;
    private const int MaxExtentNodes = 4096;     // per file or folder
    private const int MaxRuns = 1_000_000;       // per file or folder
    private const long MaxBlockMappedBlocks = 1L << 22; // old-style block maps: up to 4M blocks (16 GiB at 4 KiB)
    private const int TypeMask = 0xF000;
    private const int DirectoryMode = 0x4000;
    private const int RegularMode = 0x8000;
    private const uint ExtentsFlag = 0x80000;
    private const uint InlineDataFlag = 0x10000000;

    // INCOMPAT features this reader can't honour: compression, a separate journal device, a different descriptor layout (META_BG),
    // data in directory entries, data inside inodes, encrypted names and contents.
    private const uint UnsupportedIncompat = 0x1 | 0x8 | 0x10 | 0x1000 | 0x8000 | 0x10000;
    private const uint FileTypeFeature = 0x2;

    private readonly Stream _volume;
    private readonly long _length;
    private readonly long _blockSize;
    private readonly ulong _blocks;
    private readonly uint _inodesPerGroup;
    private readonly uint _inodeCount;
    private readonly int _inodeSize;
    private readonly long _descriptorTable;
    private readonly int _descriptorSize;
    private readonly bool _is64Bit;
    private readonly ulong _groups;

    private Ext4Volume(Stream volume, long length, long blockSize, ulong blocks, uint inodesPerGroup, uint inodeCount, int inodeSize,
        long descriptorTable, int descriptorSize, bool is64Bit, ulong groups, string? label)
    {
        _volume = volume;
        _length = length;
        _blockSize = blockSize;
        _blocks = blocks;
        _inodesPerGroup = inodesPerGroup;
        _inodeCount = inodeCount;
        _inodeSize = inodeSize;
        _descriptorTable = descriptorTable;
        _descriptorSize = descriptorSize;
        _is64Bit = is64Bit;
        _groups = groups;
        Label = label;
    }

    /// <summary>The volume's label, if it has one.</summary>
    public string? Label { get; }

    /// <summary>The block size in bytes.</summary>
    public long BlockSize => _blockSize;

    /// <summary>
    /// The volume in <paramref name="volume"/> (a stream over just this volume, <paramref name="length"/> bytes), or
    /// null when its superblock can't be read sensibly or it uses a feature this reader can't honour. Call
    /// <see cref="CardLayout.CheckSuperblock"/> first.
    /// </summary>
    public static Ext4Volume? TryCreate(Stream volume, long length)
    {
        var sb = new byte[1024];
        if (length < 2048 || !TryReadAt(volume, 1024, sb, length) || BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(56)) != 0xEF53)
        {
            return null;
        }

        var inodeCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0));
        var blocks = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4));
        var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20));
        var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24));
        var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32));
        var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(40));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(76));
        var incompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(96));
        var is64Bit = (incompat & 0x80) != 0;
        if (is64Bit)
        {
            blocks |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x150)) << 32;
        }

        int inodeSize = revision == 0 ? 128 : BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(88));
        int descriptorSize = is64Bit ? BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(0xFE)) : 32;
        if (logBlockSize > 6 || blocksPerGroup == 0 || inodesPerGroup == 0 || inodeCount == 0 || firstDataBlock >= blocks ||
            inodeSize is < 128 or > 4096 || (inodeSize & (inodeSize - 1)) != 0 ||
            descriptorSize is < 32 or > 1024 || (descriptorSize & (descriptorSize - 1)) != 0 ||
            (incompat & UnsupportedIncompat) != 0 || (incompat & FileTypeFeature) == 0)
        {
            return null;
        }

        long blockSize = 1024L << (int)logBlockSize;
        if (blocks > (ulong)(length / blockSize) + blocksPerGroup)
        {
            return null; // CheckSuperblock's bound, kept here too: every block number checked below is within the volume
        }

        var groups = (blocks - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup;
        var label = Encoding.UTF8.GetString(sb, 120, 16).TrimEnd('\0').Trim();
        return new Ext4Volume(volume, length, blockSize, Math.Min(blocks, (ulong)(length / blockSize)), inodesPerGroup, inodeCount,
            inodeSize, (firstDataBlock + 1L) * blockSize, descriptorSize, is64Bit, groups, label.Length > 0 ? label : null);
    }

    /// <summary>The length in bytes of the regular file with inode number <paramref name="inode"/>, or null when that
    /// inode can't be read, isn't a regular file, or claims more than its map could hold.</summary>
    public long? FileLength(long inode) =>
        ReadInode(inode) is { } node && (node.Mode & TypeMask) == RegularMode && BlockCount(node) is not null ? node.Size : null;

    /// <summary>
    /// The regular file with inode number <paramref name="inode"/>, as a read-only stream (holes read as zeros); null when
    /// it can't be read, isn't a regular file, or its block map or extent tree is not one ext4 would write.
    /// </summary>
    public Stream? OpenFile(long inode) => OpenFile(inode, out _);

    /// <summary>
    /// <see cref="OpenFile(long)"/>, also saying what opening it took in bytes read (its inode and the nodes of its
    /// extent tree or block map), even when it fails: callers that open many files charge it to their budget.
    /// </summary>
    public Stream? OpenFile(long inode, out long cost)
    {
        cost = _blockSize;
        if (ReadInode(inode) is not { } node || (node.Mode & TypeMask) != RegularMode || (node.Flags & InlineDataFlag) != 0 ||
            BlockCount(node) is not { } blockCount)
        {
            return null;
        }

        return MapRuns(node, blockCount, ref cost) is { } runs ? new Ext4FileStream(this, node.Size, runs) : null;
    }

    /// <summary>The blocks a file's size spans, or null when that is more than its map can address (2^32 blocks for an
    /// extent tree, <see cref="MaxBlockMappedBlocks"/> for a block map) - a size no real file has.</summary>
    private long? BlockCount(InodeData node)
    {
        var blocks = node.Size / _blockSize + (node.Size % _blockSize != 0 ? 1 : 0);
        return blocks <= ((node.Flags & ExtentsFlag) != 0 ? 1L << 32 : MaxBlockMappedBlocks) ? blocks : null;
    }

    /// <summary>
    /// The entries of the folder with inode number <paramref name="inode"/> ("." and ".." left out), read from its own
    /// blocks with nothing followed; null when it can't be read, is bigger than <see cref="MaxDirectoryBytes"/>, has
    /// holes, unwritten blocks or records that don't fit, holds a name twice, or is otherwise not what ext4 writes.
    /// <paramref name="cost"/> is what reading it took, in bytes (its blocks and its extent tree), even when it fails.
    /// </summary>
    public IReadOnlyList<CardDirEntry>? ReadDirectory(long inode, out long cost)
    {
        cost = _blockSize;
        if (ReadInode(inode) is not { } node || (node.Mode & TypeMask) != DirectoryMode || node.Size > MaxDirectoryBytes ||
            node.Size % _blockSize != 0 || (node.Flags & InlineDataFlag) != 0)
        {
            return null;
        }

        var blockCount = node.Size / _blockSize;
        if (MapRuns(node, blockCount, ref cost) is not { } runs)
        {
            return null;
        }

        // Every block present and written: a folder has no holes.
        long expected = 0;
        foreach (var run in runs)
        {
            if (run.Logical != expected || run.Physical < 0)
            {
                return null;
            }

            expected = run.Logical + run.Count;
        }

        if (expected < blockCount)
        {
            return null;
        }

        var entries = new List<CardDirEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var block = new byte[_blockSize];
        foreach (var run in runs)
        {
            for (long i = 0; i < run.Count && run.Logical + i < blockCount; i++)
            {
                cost += _blockSize;
                if (!TryReadBlock(run.Physical + i, block) || !ParseEntries(block, entries, names))
                {
                    return null;
                }
            }
        }

        return entries;
    }

    /// <summary>The inode of the entry at <paramref name="path"/> (backslash-separated, from the top folder), found
    /// folder by folder with <see cref="ReadDirectory"/>; null when any part is missing or unreadable. For tools.</summary>
    public long? Resolve(string path)
    {
        long inode = RootInode;
        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ReadDirectory(inode, out _)?.FirstOrDefault(e => e.Name == part) is not { } entry)
            {
                return null;
            }

            inode = entry.Inode;
        }

        return inode;
    }

    private bool ParseEntries(byte[] block, List<CardDirEntry> entries, HashSet<string> names)
    {
        var position = 0;
        while (position < block.Length)
        {
            if (position + 8 > block.Length)
            {
                return false;
            }

            var inode = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(position));
            var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(position + 4));
            int nameLength = block[position + 6];
            int fileType = block[position + 7];
            if (recordLength < 8 || recordLength % 4 != 0 || position + recordLength > block.Length || 8 + nameLength > recordLength)
            {
                return false; // a record that doesn't fit its block: not a folder ext4 wrote
            }

            if (inode != 0 && nameLength > 0)
            {
                var name = Encoding.UTF8.GetString(block, position + 8, nameLength);
                if (name is not ("." or ".."))
                {
                    if (inode > _inodeCount || !names.Add(name) || entries.Count >= MaxEntriesPerDirectory)
                    {
                        return false;
                    }

                    // A name that couldn't be part of a path is kept out of any path: never opened as a file or folder.
                    var plain = name.AsSpan().IndexOfAny('\\', '/', '\0') < 0;
                    entries.Add(new CardDirEntry(name, inode, !plain ? CardEntryKind.Other : fileType switch
                    {
                        1 => CardEntryKind.File,
                        2 => CardEntryKind.Directory,
                        _ => CardEntryKind.Other,
                    }));
                }
            }

            position += recordLength;
        }

        return true; // the records end exactly at the block's end
    }

    /// <summary>A file's or folder's blocks as runs (logical start, count, physical start or -1 for a hole/unwritten),
    /// in order, covering logical blocks below <paramref name="blockCount"/>; null when its map is not one ext4 writes.</summary>
    private List<Run>? MapRuns(InodeData node, long blockCount, ref long cost)
    {
        var runs = new List<Run>();
        if ((node.Flags & ExtentsFlag) != 0)
        {
            var walk = new ExtentWalk(new byte[6][], []);
            var nodes = 0;
            var last = -1L;
            return MapExtents(node.Block, expectedDepth: null, 0, uint.MaxValue + 1L, blockCount, runs, walk, ref nodes, ref last, ref cost) ? runs : null;
        }

        return blockCount <= MaxBlockMappedBlocks && MapBlockList(node.Block, blockCount, runs, ref cost) ? runs : null;
    }

    /// <summary>One walk of an extent tree: a block buffer per level (reused by siblings) and the node blocks read so far.</summary>
    private sealed record ExtentWalk(byte[]?[] Buffers, HashSet<long> Seen);

    /// <summary>
    /// One extent-tree node: entries in strictly increasing order, every extent within [<paramref name="low"/>,
    /// <paramref name="high"/>) and after the one before it, children exactly one level lower. Only the part of the
    /// tree below the file's size is walked, each node block at most once, and an index node always has entries - so
    /// a walk reads a few nodes per block of the file at most, however the tree was built (one node referenced from
    /// thousands of entries, entries past the end of a one-block file).
    /// </summary>
    private bool MapExtents(ReadOnlySpan<byte> node, int? expectedDepth, long low, long high, long blockCount, List<Run> runs,
        ExtentWalk walk, ref int nodes, ref long last, ref long cost)
    {
        if (node.Length < 12 || BinaryPrimitives.ReadUInt16LittleEndian(node) != 0xF30A || ++nodes > MaxExtentNodes)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(node[2..]);
        int max = BinaryPrimitives.ReadUInt16LittleEndian(node[4..]);
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(node[6..]);
        if (count > max || 12 + max * 12 > node.Length || depth > 5 || (expectedDepth is { } wanted && depth != wanted) ||
            (depth > 0 && count == 0))
        {
            return false;
        }

        long previousKey = -1;
        for (var i = 0; i < count; i++)
        {
            var entry = node.Slice(12 + i * 12, 12);
            long key = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (key <= previousKey || key < low || key >= high)
            {
                return false;
            }

            if (key >= blockCount)
            {
                break; // the rest lies past the end of the file: never read
            }

            previousKey = key;
            if (depth == 0)
            {
                int length = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
                var unwritten = length > 32768;
                if (unwritten)
                {
                    length -= 32768;
                }

                var physical = (long)BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
                if (length < 1 || key < last || key + length > high || (ulong)(physical + length) > _blocks)
                {
                    return false; // empty, overlapping, out of its range, or outside the volume
                }

                last = key + length;
                if (runs.Count >= MaxRuns)
                {
                    return false;
                }

                runs.Add(new Run(key, Math.Min(length, blockCount - key), unwritten ? -1 : physical));
            }
            else
            {
                var next = i + 1 < count ? (long)BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(12 + (i + 1) * 12)) : high;
                var child = (long)BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                var buffer = walk.Buffers[depth - 1] ??= new byte[_blockSize]; // this level's: the parent's stays intact
                cost += _blockSize;
                if ((ulong)child >= _blocks || !walk.Seen.Add(child) || !TryReadBlock(child, buffer) ||
                    !MapExtents(buffer, depth - 1, key, Math.Min(next, high), blockCount, runs, walk, ref nodes, ref last, ref cost))
                {
                    return false; // outside the volume, a node met twice, unreadable, or not a node ext4 writes
                }
            }
        }

        return true;
    }

    /// <summary>The old block map: 12 direct blocks, then single, double and triple indirect ones; zero is a hole.</summary>
    private bool MapBlockList(ReadOnlySpan<byte> iBlock, long blockCount, List<Run> runs, ref long cost)
    {
        long logical = 0;
        for (var i = 0; i < 12 && logical < blockCount; i++, logical++)
        {
            if (!AddBlock(runs, logical, BinaryPrimitives.ReadUInt32LittleEndian(iBlock[(i * 4)..])))
            {
                return false;
            }
        }

        var perBlock = _blockSize / 4;
        long span = perBlock;
        for (var level = 1; level <= 3 && logical < blockCount; level++, span *= perBlock)
        {
            var root = BinaryPrimitives.ReadUInt32LittleEndian(iBlock[((11 + level) * 4)..]);
            if (root != 0 && !MapIndirect(root, level, logical, blockCount, runs, ref cost))
            {
                return false;
            }

            logical += span;
        }

        return true;
    }

    private bool MapIndirect(uint pointerBlock, int level, long firstLogical, long blockCount, List<Run> runs, ref long cost)
    {
        if (pointerBlock >= _blocks)
        {
            return false;
        }

        var buffer = new byte[_blockSize];
        cost += _blockSize;
        if (!TryReadBlock(pointerBlock, buffer))
        {
            return false;
        }

        var perBlock = _blockSize / 4;
        long span = 1;
        for (var l = 1; l < level; l++)
        {
            span *= perBlock;
        }

        for (var i = 0; i < perBlock && firstLogical + i * span < blockCount; i++)
        {
            var target = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan((int)(i * 4)));
            if (target == 0)
            {
                continue; // a hole
            }

            if (level == 1 ? !AddBlock(runs, firstLogical + i, target) : !MapIndirect(target, level - 1, firstLogical + i * span, blockCount, runs, ref cost))
            {
                return false;
            }
        }

        return true;
    }

    private bool AddBlock(List<Run> runs, long logical, uint physical)
    {
        if (physical == 0)
        {
            return true; // a hole
        }

        if (physical >= _blocks)
        {
            return false;
        }

        if (runs.Count > 0 && runs[^1] is var last && last.Physical >= 0 && last.Logical + last.Count == logical &&
            last.Physical + last.Count == physical)
        {
            runs[^1] = last with { Count = last.Count + 1 };
            return true;
        }

        if (runs.Count >= MaxRuns)
        {
            return false;
        }

        runs.Add(new Run(logical, 1, physical));
        return true;
    }

    internal readonly record struct Run(long Logical, long Count, long Physical);

    private sealed record InodeData(int Mode, uint Flags, long Size, byte[] Block);

    private InodeData? ReadInode(long inode)
    {
        if (inode < 1 || inode > _inodeCount)
        {
            return null;
        }

        var group = (ulong)(inode - 1) / _inodesPerGroup;
        var index = (ulong)(inode - 1) % _inodesPerGroup;
        if (group >= _groups)
        {
            return null;
        }

        var descriptor = new byte[Math.Min(_descriptorSize, 64)];
        if (!TryReadAt(_volume, _descriptorTable + (long)group * _descriptorSize, descriptor, _length))
        {
            return null;
        }

        var table = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(8));
        if (_is64Bit && _descriptorSize >= 64)
        {
            table |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(0x28)) << 32;
        }

        if (table >= _blocks)
        {
            return null;
        }

        var raw = new byte[0x70];
        if (!TryReadAt(_volume, (long)table * _blockSize + (long)index * _inodeSize, raw, _length))
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)) | (long)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x6C)) << 32;
        return size < 0
            ? null
            : new InodeData(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0)), BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x20)),
                size, raw.AsSpan(0x28, 60).ToArray());
    }

    private bool TryReadBlock(long block, Span<byte> buffer) =>
        block >= 0 && (ulong)block < _blocks && TryReadAt(_volume, block * _blockSize, buffer, _length);

    /// <summary>Reads <paramref name="buffer"/> at <paramref name="offset"/> of the volume, or nothing: false if any of it
    /// lies outside the volume or the read comes up short.</summary>
    internal bool ReadVolume(long offset, Span<byte> buffer) => TryReadAt(_volume, offset, buffer, _length);

    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer, long length)
    {
        if (offset < 0 || offset > length - buffer.Length || offset + buffer.Length > stream.Length)
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

    /// <summary>A regular file's contents: its runs read from the volume, holes and unwritten blocks as zeros.</summary>
    private sealed class Ext4FileStream : Stream
    {
        private readonly Ext4Volume _volume;
        private readonly long _length;
        private readonly Run[] _runs;
        private long _position;

        public Ext4FileStream(Ext4Volume volume, long length, List<Run> runs)
        {
            _volume = volume;
            _length = length;
            _runs = runs.ToArray();
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _length || buffer.IsEmpty)
            {
                return 0;
            }

            var blockSize = _volume.BlockSize;
            var wanted = (int)Math.Min(buffer.Length, _length - _position);
            var logical = _position / blockSize;
            var inBlock = _position % blockSize;

            // The run holding this block, or the next one after a hole.
            int low = 0, high = _runs.Length;
            while (low < high)
            {
                var mid = (low + high) >>> 1;
                if (_runs[mid].Logical + _runs[mid].Count <= logical)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            int done;
            if (low < _runs.Length && _runs[low].Logical <= logical)
            {
                var run = _runs[low];
                var available = (run.Logical + run.Count - logical) * blockSize - inBlock;
                done = (int)Math.Min(wanted, available);
                if (run.Physical < 0)
                {
                    buffer[..done].Clear(); // allocated but never written
                }
                else if (!_volume.ReadVolume((run.Physical + logical - run.Logical) * blockSize + inBlock, buffer[..done]))
                {
                    throw new IOException("The card could not be read.");
                }
            }
            else
            {
                // A hole, up to the next run (or the end).
                var holeEnd = low < _runs.Length ? _runs[low].Logical * blockSize : long.MaxValue;
                done = (int)Math.Min(wanted, holeEnd - _position);
                buffer[..done].Clear();
            }

            _position += done;
            return done;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
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
    }
}
