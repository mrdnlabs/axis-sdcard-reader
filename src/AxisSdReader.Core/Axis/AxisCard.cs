using AxisSdReader.Core.Axis.Matroska;
using DiscUtils;

namespace AxisSdReader.Core.Axis;

/// <summary>One MKV chunk file within a recording.</summary>
public sealed record RecordingChunk(string Path, string FileName, long SizeBytes)
{
    /// <summary>Header metadata; populated by <see cref="Recording.LoadChunkMetadata"/>.</summary>
    public MkvMetadata? Metadata { get; init; }
}

/// <summary>
/// One recording session: a recording-ID directory on the card containing sequential MKV chunks.
/// </summary>
public sealed class Recording
{
    public Recording(RecordingId id, string directoryPath, IReadOnlyList<RecordingChunk> chunks)
    {
        Id = id;
        DirectoryPath = directoryPath;
        Chunks = chunks;
    }

    public RecordingId Id { get; }

    public string DirectoryPath { get; }

    /// <summary>Chunks in playback order.</summary>
    public IReadOnlyList<RecordingChunk> Chunks { get; private set; }

    public long TotalSizeBytes => Chunks.Sum(c => c.SizeBytes);

    /// <summary>Start time from the recording ID (camera clock).</summary>
    public DateTime StartTime => Id.StartTime;

    /// <summary>Total duration across chunks; null until metadata is loaded or when no chunk reports one.</summary>
    public TimeSpan? Duration
    {
        get
        {
            TimeSpan? total = null;
            foreach (var chunk in Chunks)
            {
                if (chunk.Metadata?.Duration is { } d)
                {
                    total = (total ?? TimeSpan.Zero) + d;
                }
            }

            return total;
        }
    }

    /// <summary>Codec of the first chunk that reported one (chunks of a session share encoding).</summary>
    public string? VideoCodecId => Chunks.Select(c => c.Metadata?.VideoCodecId).FirstOrDefault(c => c is not null);

    /// <summary>
    /// Reads the MKV headers of every chunk (cheap: header-sized reads, plus a structural
    /// walk when a chunk lacks a Duration element). Idempotent.
    /// </summary>
    public void LoadChunkMetadata(DiscFileSystem fileSystem)
    {
        Chunks = Chunks
            .Select(chunk =>
            {
                if (chunk.Metadata is not null)
                {
                    return chunk;
                }

                using var stream = fileSystem.OpenFile(chunk.Path, FileMode.Open, FileAccess.Read);
                return chunk with { Metadata = MkvMetadataReader.Read(stream) };
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

    /// <summary>Root directories/files that did not match the Axis layout (excluding lost+found).</summary>
    public IReadOnlyList<string> UnrecognizedRootEntries { get; }

    /// <summary>Whether the proprietary Axis index database was present at the card root.</summary>
    public bool HasIndexDatabase { get; }
}

/// <summary>Builds an <see cref="AxisCard"/> model from a card's filesystem.</summary>
public static class AxisCardIndexer
{
    /// <summary>
    /// Indexes the card from directory names alone (fast — no file contents are read).
    /// Call <see cref="Recording.LoadChunkMetadata"/> per recording for durations/codecs.
    /// </summary>
    public static AxisCard Index(DiscFileSystem fileSystem)
    {
        var recordings = new List<Recording>();
        var unrecognized = new List<string>();

        foreach (var directory in fileSystem.GetDirectories(@"\"))
        {
            var name = directory.TrimStart('\\');
            if (name is "lost+found")
            {
                continue;
            }

            var id = RecordingId.TryParse(name);
            if (id is null)
            {
                unrecognized.Add(name);
                continue;
            }

            var chunks = fileSystem.GetFiles(directory)
                .Where(f => f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                .Select(f => new RecordingChunk(f, System.IO.Path.GetFileName(f), fileSystem.GetFileLength(f)))
                .OrderBy(c => c.FileName, ChunkNameComparer.Instance)
                .ToList();

            recordings.Add(new Recording(id, directory, chunks));
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

    /// <summary>Orders chunk file names numerically when possible (0.mkv, 1.mkv, ..., 10.mkv), lexically otherwise.</summary>
    private sealed class ChunkNameComparer : IComparer<string>
    {
        public static readonly ChunkNameComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var nx = TryNumeric(x);
            var ny = TryNumeric(y);
            if (nx is not null && ny is not null)
            {
                return nx.Value.CompareTo(ny.Value);
            }

            return string.CompareOrdinal(x, y);
        }

        private static long? TryNumeric(string? name) =>
            long.TryParse(System.IO.Path.GetFileNameWithoutExtension(name), out var n) ? n : null;
    }
}
