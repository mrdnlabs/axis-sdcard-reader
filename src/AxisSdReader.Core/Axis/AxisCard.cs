using System.Text.RegularExpressions;
using AxisSdReader.Core.Axis.Matroska;
using DiscUtils;

namespace AxisSdReader.Core.Axis;

/// <summary>One MKV chunk file within a recording.</summary>
public sealed record RecordingChunk(string Path, string FileName, long SizeBytes)
{
    /// <summary>Sidecar XML path (same basename, .xml) if one exists on the card.</summary>
    public string? SidecarPath { get; init; }

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

    public long TotalSizeBytes => Chunks.Sum(c => c.SizeBytes);

    /// <summary>UTC start time when known (recording.xml), else the camera-local time from the folder name.</summary>
    public DateTime StartTime => Info?.StartTimeUtc ?? Id.StartTime;

    /// <summary>Trigger that started the recording (e.g. "continuous"), when recorded by the camera.</summary>
    public string? Trigger => Info?.TriggerType ?? Info?.TriggerName;

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
    /// i.e. interrupted while recording). Idempotent.
    /// </summary>
    public void LoadChunkMetadata(DiscFileSystem fileSystem)
    {
        Chunks = Chunks
            .Select(chunk =>
            {
                if (chunk.Block is not null || chunk.Metadata is not null)
                {
                    return chunk;
                }

                // One unreadable chunk (truncated, corrupt, or a filesystem read error) must not
                // abort the whole recording — degrade to no metadata and carry on.
                try
                {
                    var block = chunk.SidecarPath is { } sidecar
                        ? RecordingXml.TryParseBlock(fileSystem, sidecar)
                        : null;

                    MkvMetadata? metadata = null;
                    if (block?.Duration is null)
                    {
                        using var stream = fileSystem.OpenFile(chunk.Path, FileMode.Open, FileAccess.Read);
                        metadata = MkvMetadataReader.Read(stream);
                    }

                    return chunk with { Block = block, Metadata = metadata };
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    // Best-effort: any failure reading one chunk (DiscUtils filesystem errors, malformed
                    // MKV/XML, overflow on crafted sizes) degrades that chunk to no metadata rather than
                    // aborting the whole recording's load.
                    return chunk;
                }
            })
            .ToList();
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
    /// Indexes the card from directory/file names plus one small recording.xml read per
    /// recording. Call <see cref="Recording.LoadChunkMetadata"/> per recording for durations.
    /// </summary>
    /// <param name="fileSystem">The card's filesystem.</param>
    /// <param name="progress">Invoked with the running recording count as discovery proceeds.</param>
    /// <param name="cancellationToken">Cancels a long walk (e.g. when the card is closed mid-index).</param>
    public static AxisCard Index(DiscFileSystem fileSystem, Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var recordings = new List<Recording>();
        var unrecognized = new List<string>();
        void Report() => progress?.Invoke(recordings.Count);

        foreach (var directory in fileSystem.GetDirectories(@"\"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = DirName(directory);
            if (name is "lost+found")
            {
                continue;
            }

            if (RecordingId.TryParse(name) is { } flatId)
            {
                recordings.Add(BuildRecording(fileSystem, flatId, directory));
                Report();
            }
            else if (DateDir.IsMatch(name))
            {
                foreach (var hourDir in fileSystem.GetDirectories(directory))
                {
                    if (!HourDir.IsMatch(DirName(hourDir)))
                    {
                        continue;
                    }

                    foreach (var recordingDir in fileSystem.GetDirectories(hourDir))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (RecordingId.TryParse(DirName(recordingDir)) is { } id)
                        {
                            recordings.Add(BuildRecording(fileSystem, id, recordingDir));
                            Report();
                        }
                    }
                }
            }
            else
            {
                unrecognized.Add(name);
            }
        }

        var hasIndexDb = fileSystem.GetFiles(@"\")
            .Any(f => System.IO.Path.GetFileName(f).Equals("index.db", StringComparison.OrdinalIgnoreCase));

        unrecognized.AddRange(fileSystem.GetFiles(@"\")
            .Select(f => f.TrimStart('\\'))
            .Where(f => !f.Equals("index.db", StringComparison.OrdinalIgnoreCase)));

        return new AxisCard(
            recordings.OrderBy(r => r.StartTime).ToList(),
            unrecognized,
            hasIndexDb);
    }

    private static Recording BuildRecording(DiscFileSystem fs, RecordingId id, string recordingDir)
    {
        // Chunks live either directly in the recording dir (legacy) or in hour-bucket
        // subdirectories (YYYYMMDD_HH). Collect both.
        var chunkDirs = new List<string> { recordingDir };
        chunkDirs.AddRange(fs.GetDirectories(recordingDir));

        var chunks = new List<RecordingChunk>();
        foreach (var dir in chunkDirs)
        {
            foreach (var file in fs.GetFiles(dir))
            {
                if (!file.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sidecar = file[..^4] + ".xml";
                chunks.Add(new RecordingChunk(file, System.IO.Path.GetFileName(file), fs.GetFileLength(file))
                {
                    SidecarPath = fs.FileExists(sidecar) ? sidecar : null,
                });
            }
        }

        chunks.Sort((a, b) => CompareChunkNames(a.FileName, b.FileName));

        var infoPath = recordingDir.TrimEnd('\\') + @"\recording.xml";
        var info = RecordingXml.TryParseRecordingInfo(fs, infoPath);

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

    private static string DirName(string path) => System.IO.Path.GetFileName(path.TrimEnd('\\'));
}
