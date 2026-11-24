using AxisSdReader.Core.Axis;

namespace AxisSdReader.Core.Timeline;

/// <summary>One camera on the card, with its recording sources (lenses).</summary>
public sealed class CatalogCamera
{
    internal CatalogCamera(string serial, IReadOnlyList<Recording> recordings)
    {
        Serial = serial;
        Name = $"Camera {serial[^Math.Min(4, serial.Length)..]}";
        Recordings = recordings;
        Sources = recordings
            .Select(r => r.SourceToken)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => int.TryParse(s, out var n) ? n : int.MaxValue)
            .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string Serial { get; }

    /// <summary>The name the app shows, e.g. "Camera 3F2A" (last four of the serial).</summary>
    public string Name { get; }

    public IReadOnlyList<Recording> Recordings { get; }

    /// <summary>Recording sources (VAPIX source tokens), numeric order first.</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>The source used when a query doesn't name one: the lowest-numbered.</summary>
    public string DefaultSource => Sources.Count > 0 ? Sources[0] : "1";

    /// <summary>The camera model from the MKV headers, once any chunk metadata has been read.</summary>
    public string? Model => Recordings
        .SelectMany(r => r.Chunks)
        .Select(c => c.Metadata?.WritingApp)
        .FirstOrDefault(w => !string.IsNullOrWhiteSpace(w));
}

/// <summary>A stretch of footage of one kind (overlapping recordings of that kind merged).</summary>
public readonly record struct CoverageSpan(RecordingKind Kind, double StartSeconds, double EndSeconds)
{
    public double Seconds => EndSeconds - StartSeconds;
}

/// <summary>
/// A queryable view of an indexed card on the continuous time axis: cameras, their sources, and each
/// recording placed as a <see cref="TimeSegment"/>. Answers the questions an agent asks — what was recorded
/// when, what covers a given moment, which chunks make up a range — with the same rules the player uses
/// (overlapping recordings resolve manual &gt; event &gt; scheduled &gt; continuous).
/// Not thread-safe: callers serialize access. Segment placement is an estimate until
/// <see cref="TimeSegment.Refine"/> runs after the recording's metadata is loaded.
/// </summary>
public sealed class CardCatalog
{
    private readonly Dictionary<string, TimeSegment> _byRecordingId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Serial, string Source), List<TimeSegment>> _byLens = [];

    public CardCatalog(AxisCard index)
    {
        Index = index;
        Cameras = index.Cameras.Select(c => new CatalogCamera(c.Serial, c.Recordings)).ToList();
        foreach (var camera in Cameras)
        {
            foreach (var recording in camera.Recordings)
            {
                var segment = new TimeSegment(recording);
                _byRecordingId.TryAdd(recording.Id.Raw, segment);
                var key = (camera.Serial, recording.SourceToken);
                if (!_byLens.TryGetValue(key, out var list))
                {
                    _byLens[key] = list = [];
                }

                list.Add(segment);
            }
        }

        foreach (var list in _byLens.Values)
        {
            list.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        }
    }

    public AxisCard Index { get; }

    public IReadOnlyList<CatalogCamera> Cameras { get; }

    /// <summary>
    /// Finds a camera by full serial, by a serial suffix of at least four characters, or by its app name
    /// ("Camera 3F2A"). With no query, the only camera on the card. Returns null (with the reason) otherwise.
    /// </summary>
    public CatalogCamera? FindCamera(string? query, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(query))
        {
            if (Cameras.Count == 1)
            {
                return Cameras[0];
            }

            error = Cameras.Count == 0
                ? "The card has no recordings."
                : $"The card has {Cameras.Count} cameras; say which one ({Agent.AgentService.ListSome(Cameras.Select(c => c.Name))}).";
            return null;
        }

        var q = query.Trim();
        if (q.StartsWith("Camera ", StringComparison.OrdinalIgnoreCase))
        {
            q = q[7..].Trim();
        }

        q = q.Replace(":", "").Replace("-", "");
        var exact = Cameras.FirstOrDefault(c => c.Serial.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var matches = q.Length >= 4
            ? Cameras.Where(c => c.Serial.EndsWith(q, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        if (matches.Count == 1)
        {
            return matches[0];
        }

        var shown = Agent.AgentService.Clean(query, 40);
        error = matches.Count > 1
            ? $"'{shown}' matches several cameras: {Agent.AgentService.ListSome(matches.Select(c => c.Serial))}."
            : $"No camera '{shown}'. Cameras on this card: {Agent.AgentService.ListSome(Cameras.Select(c => $"{c.Name} ({c.Serial})"))}.";
        return null;
    }

    /// <summary>The recording segments of one camera source, ordered by start (an empty list if none).</summary>
    public IReadOnlyList<TimeSegment> Segments(CatalogCamera camera, string source)
    {
        if (!_byLens.TryGetValue((camera.Serial, source), out var list))
        {
            return [];
        }

        return list;
    }

    /// <summary>All segments of a camera across sources, ordered by start.</summary>
    public IReadOnlyList<TimeSegment> AllSegments(CatalogCamera camera) =>
        camera.Sources.SelectMany(s => Segments(camera, s)).OrderBy(s => s.StartSeconds).ToList();

    public TimeSegment? FindRecording(string recordingId) =>
        _byRecordingId.TryGetValue(recordingId.Trim(), out var segment) ? segment : null;

    /// <summary>Re-sorts segments after refinement may have moved their starts.</summary>
    public void Resort(CatalogCamera camera, string source)
    {
        if (_byLens.TryGetValue((camera.Serial, source), out var list))
        {
            list.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        }
    }

    /// <summary>Segments whose (estimated) span comes within <paramref name="margin"/> seconds of the range.</summary>
    public static List<TimeSegment> Overlapping(IEnumerable<TimeSegment> segments, double from, double to, double margin = 0) =>
        segments.Where(s => s.EndSeconds > from - margin && s.StartSeconds < to + margin).ToList();

    /// <summary>
    /// The recording that plays at <paramref name="seconds"/> — among those covering it, the highest
    /// overlay priority wins (as on the app's timeline) — and where within it. Null in a gap.
    /// </summary>
    public static (TimeSegment Segment, int ChunkIndex, TimeSpan Offset, RecordingChunk Chunk)? Locate(IEnumerable<TimeSegment> segments, double seconds)
    {
        TimeSegment? best = null;
        var bestPriority = int.MinValue;
        foreach (var segment in segments)
        {
            if (segment.Recording.Chunks.Count == 0 || !segment.Contains(seconds))
            {
                continue;
            }

            var priority = RecordingTypeClassifier.OverlayPriority(segment.Recording.Kind);
            if (priority > bestPriority)
            {
                bestPriority = priority;
                best = segment;
            }
        }

        if (best is null)
        {
            return null;
        }

        var (index, offset, chunk) = best.LocateChunk(seconds);
        return chunk is null ? null : (best, index, offset, chunk);
    }

    /// <summary>Per-kind coverage within [from, to]: overlapping recordings of a kind merged into spans.</summary>
    public static List<CoverageSpan> Coverage(IEnumerable<TimeSegment> segments, double from, double to)
    {
        var spans = new List<CoverageSpan>();
        foreach (var group in segments.Where(s => s.Recording.Chunks.Count > 0).GroupBy(s => s.Recording.Kind))
        {
            spans.AddRange(Merge(group.Select(s => (s.StartSeconds, s.EndSeconds)), from, to)
                .Select(m => new CoverageSpan(group.Key, m.Start, m.End)));
        }

        spans.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        return spans;
    }

    /// <summary>Stretches within [from, to] that no recording covers, at least <paramref name="minSeconds"/> long.</summary>
    public static List<(double Start, double End)> Gaps(IEnumerable<TimeSegment> segments, double from, double to, double minSeconds = 1)
    {
        var covered = Merge(segments.Where(s => s.Recording.Chunks.Count > 0).Select(s => (s.StartSeconds, s.EndSeconds)), from, to);
        var gaps = new List<(double, double)>();
        var cursor = from;
        foreach (var (start, end) in covered)
        {
            if (start - cursor >= minSeconds)
            {
                gaps.Add((cursor, start));
            }

            cursor = Math.Max(cursor, end);
        }

        if (to - cursor >= minSeconds)
        {
            gaps.Add((cursor, to));
        }

        return gaps;
    }

    /// <summary>Clips intervals to [from, to] and merges overlapping or touching ones (within half a second).</summary>
    public static List<(double Start, double End)> Merge(IEnumerable<(double Start, double End)> intervals, double from, double to)
    {
        var clipped = intervals
            .Select(i => (Start: Math.Max(i.Start, from), End: Math.Min(i.End, to)))
            .Where(i => i.End > i.Start)
            .OrderBy(i => i.Start)
            .ToList();

        var merged = new List<(double Start, double End)>();
        foreach (var interval in clipped)
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End + 0.5)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }

    /// <summary>
    /// Evenly spaced sample times across the footage within [from, to], skipping gaps: the covered time is
    /// treated as one continuous strip and divided into <paramref name="count"/> equal parts, each sampled at
    /// its middle. Falls back to even spacing over the whole range when nothing is covered.
    /// </summary>
    public static List<double> SampleCovered(IEnumerable<TimeSegment> segments, double from, double to, int count)
    {
        count = Math.Max(1, count);
        var covered = Merge(segments.Where(s => s.Recording.Chunks.Count > 0).Select(s => (s.StartSeconds, s.EndSeconds)), from, to);
        var total = covered.Sum(c => c.End - c.Start);
        var samples = new List<double>(count);
        if (total <= 0)
        {
            for (var i = 0; i < count; i++)
            {
                samples.Add(from + (to - from) * (i + 0.5) / count);
            }

            return samples;
        }

        for (var i = 0; i < count; i++)
        {
            var target = total * (i + 0.5) / count;
            foreach (var (start, end) in covered)
            {
                var length = end - start;
                if (target <= length)
                {
                    samples.Add(start + target);
                    break;
                }

                target -= length;
            }
        }

        return samples;
    }
}
