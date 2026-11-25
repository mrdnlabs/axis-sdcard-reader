using System.Text.RegularExpressions;
using AxisSdReader.Core.Axis.Matroska;
using AxisSdReader.Core.Ext4;

namespace AxisSdReader.Core.Axis;

/// <summary>One MKV chunk file within a recording.</summary>
/// <param name="Folder">The folder it is in on the card (one string shared by the chunks of a folder: a card can hold a
/// million of them).</param>
public sealed record RecordingChunk(string Folder, string FileName, long SizeBytes)
{
    /// <summary>Where it is on the card, for display (files are opened by <see cref="Inode"/>).</summary>
    public string Path => Folder + "\\" + FileName;

    /// <summary>The chunk's inode number: what it is opened by.</summary>
    public long Inode { get; init; }

    /// <summary>The sidecar XML's (same basename, .xml) inode number, when the card has one.</summary>
    public long? SidecarInode { get; init; }

    /// <summary>The chunks' sizes added up, stopping at <see cref="long.MaxValue"/> (sizes are what the card claims).</summary>
    public static long TotalBytes(IEnumerable<RecordingChunk> chunks)
    {
        long total = 0;
        foreach (var chunk in chunks)
        {
            total = chunk.SizeBytes > long.MaxValue - total ? long.MaxValue : total + Math.Max(chunk.SizeBytes, 0);
        }

        return total;
    }

    /// <summary>Parsed sidecar block info; populated by <see cref="Recording.LoadChunkMetadata"/>.</summary>
    public RecordingBlockXml? Block { get; init; }

    /// <summary>MKV header metadata; populated when the sidecar alone is not sufficient.</summary>
    public MkvMetadata? Metadata { get; init; }

    /// <summary>Best-known duration: sidecar start/stop first, then MKV headers.</summary>
    public TimeSpan? Duration => Block?.Duration ?? Metadata?.Duration;

    /// <summary>Best-known UTC start time: sidecar first, then MKV DateUTC.</summary>
    public DateTime? StartTimeUtc => Block?.StartTimeUtc ?? Metadata?.DateUtc;
}

/// <summary>
/// One recording session: a recording-ID directory on the card containing sequential MKV chunks.
/// </summary>
public sealed class Recording
{
    public Recording(RecordingId id, string directoryPath, IReadOnlyList<RecordingChunk> chunks, RecordingInfoXml? info)
    {
        Id = id;
        DirectoryPath = directoryPath;
        Chunks = chunks;
        Info = info;
    }

    public RecordingId Id { get; }

    public string DirectoryPath { get; }

    /// <summary>Camera-written recording.xml metadata (UTC start, trigger, codec/resolution), if present.</summary>
    public RecordingInfoXml? Info { get; }

    /// <summary>Chunks in playback order.</summary>
    public IReadOnlyList<RecordingChunk> Chunks { get; private set; }

    public long TotalSizeBytes => RecordingChunk.TotalBytes(Chunks);

    /// <summary>UTC start time when known (recording.xml), else the camera-local time from the folder name.</summary>
    public DateTime StartTime => Info?.StartTimeUtc ?? Id.StartTime;

    /// <summary>Trigger that started the recording (e.g. "continuous"), when recorded by the camera.</summary>
    public string? Trigger => Info?.TriggerType ?? Info?.TriggerName;

    /// <summary>Recording type for timeline colour-coding (continuous / event / manual / …), classified
    /// from the trigger metadata. <see cref="RecordingKind.Other"/> when no trigger info is present.</summary>
    public RecordingKind Kind => _kind ??=
        RecordingTypeClassifier.Classify(Info?.TriggerType, Info?.TriggerName, Info?.TriggerTrigger);

    private RecordingKind? _kind;

    /// <summary>True once <see cref="LoadChunkMetadata(Ext4Volume)"/> has visited every chunk (even if some
    /// could not be read), so callers don't retry unreadable chunks on every seek.</summary>
    public bool MetadataLoaded { get; private set; }

    /// <summary>VAPIX source (lens) this recording came from; multi-sensor cameras use 1..N.</summary>
    public string SourceToken => Info?.SourceToken is { Length: > 0 } s ? s : "1";

    /// <summary>True when the last chunk was still being written when the card was removed.</summary>
    public bool WasInterrupted =>
        Chunks.Count > 0 &&
        (Chunks[^1].Block is { IsComplete: false } || Chunks[^1].Metadata?.IsTruncated == true);

    /// <summary>Total duration across chunks; null until metadata is loaded or when no chunk reports one.</summary>
    public TimeSpan? Duration
    {
        get
        {
            long? totalTicks = null;
            foreach (var chunk in Chunks)
            {
                if (chunk.Duration is { } d)
                {
                    var acc = totalTicks ?? 0L;
                    // Saturate instead of throwing OverflowException: a crafted/corrupt sidecar can report an
                    // enormous per-chunk duration, and this getter runs on the WPF UI thread (label refresh).
                    totalTicks = d.Ticks > long.MaxValue - acc ? long.MaxValue : acc + d.Ticks;
                }
            }

            return totalTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null;
        }
    }

    /// <summary>Matroska codec ID, from recording.xml encoding or the first chunk that reported one.</summary>
    public string? VideoCodecId => Info?.Encoding switch
    {
        "video/x-av1" => "V_AV1",
        "video/x-h264" or "video/h264" => "V_MPEG4/ISO/AVC",
        "video/x-h265" or "video/h265" or "video/x-hevc" => "V_MPEGH/ISO/HEVC",
        _ => Chunks.Select(c => c.Metadata?.VideoCodecId).FirstOrDefault(c => c is not null),
    };

    /// <summary>
    /// Loads per-chunk timing metadata. Sidecar XMLs are preferred (two tiny reads per chunk);
    /// MKV headers are only parsed for chunks the sidecar cannot fully describe (no stop time —
    /// i.e. interrupted while recording). Idempotent. The caller must hold exclusive access to the card.
    /// </summary>
    public void LoadChunkMetadata(Ext4Volume fileSystem) =>
        LoadChunkMetadata(fileSystem, null, CancellationToken.None);

    /// <summary>
    /// Like <see cref="LoadChunkMetadata(Ext4Volume)"/>, but takes the card's I/O gate one chunk at a time
    /// (when <paramref name="gate"/> is given) instead of for the whole recording, so playback reads interleave
    /// with a long load rather than stalling behind it, and stops between chunks when cancelled (progress so far
    /// is kept). Once every chunk's true start is known, chunks are put in time order — camera-local file names
    /// sort the repeated hour of a daylight-saving change out of order.
    /// </summary>
    public void LoadChunkMetadata(Ext4Volume fileSystem, Media.CardIoGate? gate, CancellationToken cancellationToken)
    {
        // One load of a recording at a time (the player and an agent may ask together): each would otherwise work on
        // its own copy and publish it over the other's. A second caller waits, then finds the work done.
        while (!Monitor.TryEnter(_metadataLoad, 100))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            LoadChunkMetadataCore(fileSystem, gate, cancellationToken);
        }
        finally
        {
            Monitor.Exit(_metadataLoad);
        }
    }

    private readonly object _metadataLoad = new();

    private void LoadChunkMetadataCore(Ext4Volume fileSystem, Media.CardIoGate? gate, CancellationToken cancellationToken)
    {
        // The chunk list is replaced, never changed in place (readers keep consistent snapshots), so loaded chunks
        // go into a private copy that is published every so often - at most ~16 times however long the list, not
        // once per chunk, which would make a recording with very many chunks quadratic to load.
        RecordingChunk[]? pending = null;
        var updates = 0;
        var publishEvery = Math.Max(PublishEvery, Chunks.Count / 16);
        var readBudget = MaxMetadataBytesPerLoad; // sidecars and MKV headers/scans, across this whole load
        var text = new TextPool();
        try
        {
            for (var i = 0; i < Chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunks = (IReadOnlyList<RecordingChunk>?)pending ?? Chunks;
                if (i >= chunks.Count || chunks[i].Block is not null || chunks[i].Metadata is not null)
                {
                    continue;
                }

                var loaded = LoadOne(fileSystem, gate, chunks[i], text, ref readBudget, cancellationToken);
                if (!ReferenceEquals(loaded, chunks[i]))
                {
                    pending ??= chunks.ToArray();
                    pending[i] = loaded;
                    if (++updates % publishEvery == 0)
                    {
                        Chunks = pending;
                        pending = null;
                    }
                }
            }
        }
        finally
        {
            if (pending is not null)
            {
                gate?.Enter();
                try
                {
                    Chunks = pending; // progress so far is kept, cancelled or not
                }
                finally
                {
                    gate?.Exit();
                }
            }
        }

        gate?.Enter();
        try
        {
            var all = Chunks;
            if (all.Count > 1 && all.All(c => c.StartTimeUtc is not null))
            {
                Chunks = all.OrderBy(c => c.StartTimeUtc!.Value.ToUniversalTime()).ThenBy(c => c.FileName, StringComparer.Ordinal).ToArray();
            }

            MetadataLoaded = true;
        }
        finally
        {
            gate?.Exit();
        }
    }

    private const int PublishEvery = 256;

    /// <summary>A chunk's contents, by its inode; IOException when it can't be read.</summary>
    public static Stream OpenChunk(Ext4Volume fileSystem, RecordingChunk chunk) =>
        fileSystem.OpenFile(chunk.Inode) ?? throw new IOException($"{chunk.FileName} can't be read.");

    // What one load may read for metadata in all: sidecars (about 1 KiB each on a real card, at most 1 MiB) and, for
    // chunks without a sidecar duration (normally only an interrupted last one), the MKV header and cluster walk. A
    // year of real chunks needs a small fraction of it; a crafted card can make every chunk expensive, so past it
    // the remaining chunks keep the times their names give.
    private const long MaxMetadataBytesPerLoad = 4L << 30;

    /// <summary>Loads one chunk's sidecar and, when that has no duration, its MKV header. With a
    /// <paramref name="gate"/>, the card's I/O gate is taken for each read - the small sidecar, then each 64 KiB of
    /// MKV - never for the whole chunk, so playback reads interleave with even a slow one. Everything it reads is
    /// charged to <paramref name="readBudget"/>, opening each file (its extent tree) included.</summary>
    private static RecordingChunk LoadOne(Ext4Volume fileSystem, Media.CardIoGate? gate, RecordingChunk chunk, TextPool text,
        ref long readBudget, CancellationToken cancellationToken)
    {
        if (readBudget <= 0)
        {
            return chunk;
        }

        // One unreadable chunk (truncated, corrupt, or a filesystem read error) must not abort the whole
        // recording — degrade to no metadata and carry on.
        try
        {
            RecordingBlockXml? block = null;
            if (chunk.SidecarInode is { } sidecar)
            {
                gate?.Enter();
                try
                {
                    block = RecordingXml.TryParseBlock(fileSystem, sidecar, out var sidecarCost);
                    readBudget -= sidecarCost;
                }
                finally
                {
                    gate?.Exit();
                }
            }

            MkvMetadata? metadata = null;
            if (block?.Duration is null && readBudget > 0)
            {
                long openCost = 0;
                Stream Open()
                {
                    var file = fileSystem.OpenFile(chunk.Inode, out var cost);
                    openCost += cost;
                    return file ?? throw new IOException($"{chunk.FileName} can't be read.");
                }

                // Buffered: the EBML reader consumes headers a byte at a time, and every unbuffered byte is a
                // full trip through the filesystem (and, on encrypted cards, a sector decrypt).
                using Stream stream = gate is null ? Open() : new Media.SynchronizedCardStream(gate, Open, chunk.SizeBytes);
                using var buffered = new BufferedStream(stream, 64 * 1024);
                try
                {
                    metadata = MkvMetadataReader.Read(buffered, Math.Min(MkvMetadataReader.MaxClusterScanBytes, readBudget),
                        cancellationToken);
                }
                finally
                {
                    // What this chunk's walk covered (it only moves forward), and opening it.
                    readBudget -= Math.Max(buffered.Position, 1024) + openCost;
                }
            }

            // A recording keeps this for each of its chunks: card text is shared, not copied per chunk.
            return chunk with
            {
                Block = block is null || block.IsComplete ? block : block with { Status = text.Share(block.Status) }, // "Complete" is one literal already
                Metadata = metadata is null ? null : metadata with
                {
                    VideoCodecId = text.Share(metadata.VideoCodecId), WritingApp = text.Share(metadata.WritingApp),
                },
            };
        }
        catch (OperationCanceledException)
        {
            throw; // the load stops here; this chunk is left for the next one
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Best-effort: any failure reading one chunk (card read errors, malformed MKV/XML, overflow on crafted
            // sizes) degrades that chunk to no metadata rather than aborting the load.
            return chunk;
        }
    }

    /// <summary>
    /// Card text kept for every chunk of a recording (codec, writing application, status), shared: a real recording
    /// repeats a few values in all its chunks, so each is kept once. Values are cut to 64 characters, and past 64
    /// distinct ones - only a crafted card has them - new ones are dropped, so a recording's chunks can't hold much
    /// text however many they are.
    /// </summary>
    private sealed class TextPool
    {
        private const int MaxValues = 64;
        private const int MaxLength = 64;
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Share(string? value)
        {
            if (value is null)
            {
                return null;
            }

            if (value.Length > MaxLength)
            {
                value = value[..MaxLength];
            }

            if (_values.TryGetValue(value, out var shared))
            {
                return shared;
            }

            if (_values.Count >= MaxValues)
            {
                return null;
            }

            _values[value] = value;
            return value;
        }
    }
}

/// <summary>A camera identified on the card (by the serial/MAC embedded in recording IDs).</summary>
public sealed record CameraInfo(string Serial, IReadOnlyList<Recording> Recordings)
{
    public DateTime? EarliestRecording => Recordings.Count > 0 ? Recordings.Min(r => r.StartTime) : null;
    public DateTime? LatestRecording => Recordings.Count > 0 ? Recordings.Max(r => r.StartTime) : null;
}

/// <summary>The indexed contents of an Axis SD card.</summary>
public sealed class AxisCard
{
    public AxisCard(IReadOnlyList<Recording> recordings, IReadOnlyList<string> unrecognizedRootEntries, bool hasIndexDatabase)
    {
        Recordings = recordings;
        UnrecognizedRootEntries = unrecognizedRootEntries;
        HasIndexDatabase = hasIndexDatabase;
        Cameras = recordings
            .GroupBy(r => r.Id.CameraSerial)
            .Select(g => new CameraInfo(g.Key, g.OrderBy(r => r.StartTime).ToList()))
            .OrderBy(c => c.Serial)
            .ToList();
    }

    /// <summary>All recordings, ordered by start time.</summary>
    public IReadOnlyList<Recording> Recordings { get; }

    /// <summary>Recordings grouped by camera serial.</summary>
    public IReadOnlyList<CameraInfo> Cameras { get; }

    /// <summary>Root directories/files that are not part of the recording layout (excluding lost+found).</summary>
    public IReadOnlyList<string> UnrecognizedRootEntries { get; }

    /// <summary>Whether the proprietary Axis index database was present at the card root.</summary>
    public bool HasIndexDatabase { get; }

    /// <summary>Folders or recordings that could not be read while indexing (damaged card areas) and were skipped.</summary>
    public int UnreadableEntries { get; init; }
}

/// <summary>Builds an <see cref="AxisCard"/> model from a card's filesystem.</summary>
public static class AxisCardIndexer
{
    // Two known on-card layouts:
    //   legacy/flat:  \<recordingId>\*.mkv
    //   AXIS OS 10+:  \<YYYYMMDD>\<HH>\<recordingId>\<YYYYMMDD_HH>\<timestamp>.mkv (+ .xml sidecars,
    //                 recording.xml in the recording dir)
    // Discovery descends only through date/hour-shaped directories, so unrelated card content
    // (ACAP application data etc.) is never scanned.
    private static readonly Regex DateDir = new(@"^\d{8}$", RegexOptions.Compiled);
    private static readonly Regex HourDir = new(@"^\d{1,2}$", RegexOptions.Compiled);

    /// <summary>
    /// Indexes the card from its folder and file names plus one small recording.xml read per recording. Call
    /// <see cref="Recording.LoadChunkMetadata"/> per recording for durations.
    /// <para>The card is read with the app's own ext4 reader (<see cref="Ext4Volume"/>): folders and files by inode, no
    /// link ever followed. Each folder is read once, and everything the walk reads - folders, the extent trees of what it
    /// opens, each recording.xml - is charged to one budget for the whole card.</para>
    /// </summary>
    /// <param name="reader">The opened card.</param>
    /// <param name="progress">Invoked with the running recording count as discovery proceeds.</param>
    /// <param name="cancellationToken">Cancels a long walk (e.g. when the card is closed mid-index).</param>
    public static AxisCard Index(CardReader reader, Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var fileSystem = reader.FileSystem ?? throw new InvalidOperationException("The card is not open.");
        var recordings = new List<Recording>();
        var unrecognized = new List<string>();
        var unreadable = 0;
        var full = false; // a cap was reached
        void Report() => progress?.Invoke(recordings.Count);

        // All the walk's work is charged to one budget for the whole card - every folder read, the names in it, the
        // recording.xml files read - so no shape of folders can make it run on: past it, the card opens with what was found.
        var walk = new WalkBudget();

        // A folder's entries; null when it was read already (a crafted image can link one folder under many names), the
        // budget is spent, or it can't be read (a worn card's bad spot costs only what is under it).
        IReadOnlyList<CardDirEntry>? List(long inode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!walk.Visit(inode))
            {
                return null;
            }

            IReadOnlyList<CardDirEntry>? entries;
            long cost;
            try
            {
                entries = fileSystem.ReadDirectory(inode, out cost);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                entries = null;
                cost = fileSystem.BlockSize;
            }

            // Charged whatever came of it: a folder that is refused still cost its reads.
            walk.Charge(Math.Max(cost, 4096), entries?.Count ?? 0);
            if (entries is null)
            {
                unreadable++;
                return null;
            }

            return entries;
        }

        var budget = new ChunkBudget { Remaining = MaxTotalChunks };
        void Add(RecordingId id, string directory, long inode)
        {
            if (recordings.Count >= MaxRecordings || budget.Remaining <= 0 || walk.Spent)
            {
                full = true; // a card claiming more than any real one holds: index what fits
                return;
            }

            try
            {
                if (BuildRecording(fileSystem, id, directory, inode, budget, walk, List, cancellationToken) is { } recording)
                {
                    recordings.Add(recording);
                    Report();
                }
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                unreadable++;
            }
        }

        var root = List(Ext4Volume.RootInode) ?? [];
        foreach (var entry in root)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind != CardEntryKind.Directory || entry.Name is "lost+found")
            {
                continue;
            }

            if (full || walk.Spent)
            {
                unreadable++;
                break;
            }

            var path = @"\" + entry.Name;
            if (RecordingId.TryParse(entry.Name) is { } flatId)
            {
                Add(flatId, path, entry.Inode);
            }
            else if (DateDir.IsMatch(entry.Name))
            {
                foreach (var hour in List(entry.Inode) ?? [])
                {
                    if (hour.Kind != CardEntryKind.Directory || !HourDir.IsMatch(hour.Name) || walk.Spent)
                    {
                        continue;
                    }

                    foreach (var folder in List(hour.Inode) ?? [])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (folder.Kind == CardEntryKind.Directory && RecordingId.TryParse(folder.Name) is { } id)
                        {
                            Add(id, $@"{path}\{hour.Name}\{folder.Name}", folder.Inode);
                        }
                    }
                }
            }
            else if (unrecognized.Count < MaxUnrecognized)
            {
                unrecognized.Add(entry.Name);
            }
        }

        var rootFiles = root.Where(e => e.Kind == CardEntryKind.File).Select(e => e.Name).ToList();
        var hasIndexDb = rootFiles.Any(f => f.Equals("index.db", StringComparison.OrdinalIgnoreCase));
        unrecognized.AddRange(rootFiles
            .Where(f => !f.Equals("index.db", StringComparison.OrdinalIgnoreCase))
            .Take(Math.Max(0, MaxUnrecognized - unrecognized.Count)));

        return new AxisCard(recordings.OrderBy(r => r.StartTime).ToList(), unrecognized, hasIndexDb)
        {
            UnreadableEntries = unreadable,
        };
    }

    // Far beyond any real card (a year of 5-minute chunks is ~105,000 per camera view), so only a crafted one is cut short.
    private const int MaxRecordings = 500_000;
    private const int MaxChunksPerRecording = 500_000;
    private const int MaxTotalChunks = 1_000_000; // across the card: what memory allows, whatever the shape

    /// <summary>The longest chunk or chunk-folder name indexed. A camera's are about 25 characters
    /// (<c>20250114_093000_5B35.mkv</c>, <c>20250114_09</c>); longer ones only make every chunk cost more memory.</summary>
    private const int MaxNameLength = 64;

    /// <summary>The smallest chunk file indexed: a chunk's headers alone take about a kilobyte, and a single frame of
    /// video is more than this.</summary>
    private const long MinChunkBytes = 4096;

    private sealed class ChunkBudget
    {
        public int Remaining;
    }

    // Folder work for one card: a real card's year of recordings reads a few hundred thousand names in some ten thousand
    // folders - a few hundred megabytes of folder data at most, and a few kilobytes per recording.xml.
    private const long MaxListedNames = 5_000_000;
    private const int MaxListings = 1_000_000;
    private const long MaxWalkBytes = 2L << 30;

    private sealed class WalkBudget
    {
        private readonly HashSet<long> _visited = [];
        private long _names = MaxListedNames;
        private int _listings = MaxListings;
        private long _bytes = MaxWalkBytes;

        public bool Spent => _names <= 0 || _listings <= 0 || _bytes <= 0;

        /// <summary>Records that the folder with inode <paramref name="inode"/> is about to be read; false when it was read
        /// before or the budget is spent.</summary>
        public bool Visit(long inode) => !Spent && --_listings >= 0 && _visited.Add(inode);

        /// <summary>Charges what a read cost: its bytes, and the names it produced.</summary>
        public void Charge(long bytes, int names = 0)
        {
            _bytes -= Math.Max(bytes, 0);
            _names -= Math.Max(names, 0);
        }
    }

    private const int MaxChunkFolders = 100_000;
    private const int MaxUnrecognized = 1_000;

    private static bool IsReadFailure(Exception ex) =>
        ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException);

    /// <summary>The recording in the folder <paramref name="recordingDir"/> (inode <paramref name="inode"/>), with its
    /// chunks (in it, or in its hour folders); null when that folder can't be read, or was read already.</summary>
    private static Recording? BuildRecording(Ext4Volume fs, RecordingId id, string recordingDir, long inode,
        ChunkBudget budget, WalkBudget walk, Func<long, IReadOnlyList<CardDirEntry>?> list, CancellationToken cancellationToken)
    {
        if (list(inode) is not { } top)
        {
            return null;
        }

        var chunks = new List<RecordingChunk>();
        void Collect(string folder, IReadOnlyList<CardDirEntry> entries)
        {
            var files = entries.Where(e => e.Kind == CardEntryKind.File).ToDictionary(e => e.Name, e => e.Inode, StringComparer.Ordinal);
            foreach (var file in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (chunks.Count >= MaxChunksPerRecording || budget.Remaining <= 0)
                {
                    break;
                }

                if (file.Kind != CardEntryKind.File || file.Name.Length > MaxNameLength ||
                    !file.Name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long? length;
                try
                {
                    length = fs.FileLength(file.Inode);
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    continue; // an unreadable chunk inode: skip that chunk
                }

                if (length is null)
                {
                    continue; // not a regular file after all
                }

                if (length < MinChunkBytes)
                {
                    // No footage in it - at most part of its headers: a camera that lost power just after starting the
                    // file (ext4 often leaves it empty). Left out, it neither stretches the recording's timeline by a
                    // chunk's worth of nothing nor fails an export that reaches it (FFmpeg can't open it).
                    continue;
                }

                var hasSidecar = files.TryGetValue(file.Name[..^4] + ".xml", out var sidecarInode);
                chunks.Add(new RecordingChunk(folder, file.Name, length.Value) // the folder's one string, shared
                {
                    Inode = file.Inode,
                    SidecarInode = hasSidecar ? sidecarInode : null,
                });
                budget.Remaining--;
            }
        }

        // Chunks live either directly in the recording folder (legacy) or in hour-bucket folders (YYYYMMDD_HH).
        Collect(recordingDir, top);
        foreach (var bucket in top.Where(e => e.Kind == CardEntryKind.Directory && e.Name.Length <= MaxNameLength).Take(MaxChunkFolders))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunks.Count >= MaxChunksPerRecording || budget.Remaining <= 0 || walk.Spent)
            {
                break; // a cap is reached: read no further folders
            }

            if (list(bucket.Inode) is { } entries)
            {
                Collect($@"{recordingDir}\{bucket.Name}", entries);
            }
        }

        chunks.Sort((a, b) => CompareChunkNames(a.FileName, b.FileName));

        RecordingInfoXml? info = null;
        if (top.FirstOrDefault(e => e.Kind == CardEntryKind.File && e.Name == "recording.xml") is { } infoEntry)
        {
            info = RecordingXml.TryParseRecordingInfo(fs, infoEntry.Inode, out var infoCost);
            walk.Charge(infoCost); // its contents and its extent tree, whatever came of it
        }

        return new Recording(id, recordingDir, chunks, info);
    }

    /// <summary>Orders chunk names numerically when both are plain numbers (legacy 0.mkv, 1.mkv,
    /// ..., 10.mkv), lexically otherwise (timestamped names sort correctly as text).</summary>
    private static int CompareChunkNames(string x, string y)
    {
        var nx = TryNumeric(x);
        var ny = TryNumeric(y);
        if (nx is not null && ny is not null)
        {
            return nx.Value.CompareTo(ny.Value);
        }

        return string.CompareOrdinal(x, y);
    }

    private static long? TryNumeric(string name) =>
        long.TryParse(System.IO.Path.GetFileNameWithoutExtension(name), out var n) ? n : null;

}
