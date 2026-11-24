using System.Globalization;
using System.Text.RegularExpressions;
using AxisSdReader.Core.Axis;

namespace AxisSdReader.Core.Timeline;

/// <summary>
/// The continuous time axis: seconds (double) since a fixed <em>UTC</em> epoch.
/// All timeline math — segments, playhead, scrubbing, selection, agent queries — runs on this axis. Using UTC
/// keeps the axis strictly monotonic, so footage that spans a daylight-saving change still orders,
/// seeks and slices correctly; conversion to the user's local time happens only in
/// <see cref="ToDateTime"/> (and the display helpers built on it), so a value is still shown as the
/// local wall-clock time. Midnight is just another value and recordings can span days.
/// </summary>
public static class TimeAxis
{
    // Arbitrary fixed origin, comfortably before any camera footage.
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // The axis positions that map onto a representable DateTime (with a day of slack so the local-time
    // conversion and "+ one day" arithmetic in the timeline controls never overflow either).
    private static readonly double MinSeconds = (DateTime.MinValue.AddDays(2) - Epoch).TotalSeconds;
    private static readonly double MaxSeconds = (DateTime.MaxValue.AddDays(-2) - Epoch).TotalSeconds;

    public static double ToSeconds(DateTime time)
    {
        // Treat non-UTC values (Unspecified folder-name times, or Local) as local and normalise to UTC.
        var utc = time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime();
        return (utc - Epoch).TotalSeconds;
    }

    /// <summary>
    /// The UTC instant for an axis position. Saturates at the ends of the representable range (and maps a
    /// non-finite value to the epoch) instead of throwing: a crafted or corrupt card can carry timestamps near
    /// year 9999, and these conversions run inside WPF rendering, where an exception would repeat on every frame.
    /// </summary>
    public static DateTime ToUtc(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            return Epoch;
        }

        return Epoch.AddSeconds(Math.Clamp(seconds, MinSeconds, MaxSeconds));
    }

    /// <summary>The local wall-clock time for an axis position (inverse of <see cref="ToSeconds"/> for display).</summary>
    public static DateTime ToDateTime(double seconds) => ToUtc(seconds).ToLocalTime();

    public static string ClockText(double seconds) => ToDateTime(seconds).ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public static string DateShort(double seconds) => ToDateTime(seconds).ToString("MMM d", CultureInfo.CurrentCulture);

    public static string DateLong(double seconds) => ToDateTime(seconds).ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture);

    /// <summary>Machine-readable local time with offset (ISO 8601), e.g. for agent-facing output.</summary>
    public static string IsoLocal(double seconds) =>
        new DateTimeOffset(ToDateTime(seconds)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
}

/// <summary>TimeSpan construction that saturates instead of throwing on corrupt or crafted values.</summary>
public static class SafeTime
{
    /// <summary><see cref="TimeSpan.FromSeconds(double)"/>, clamped to the representable range; NaN becomes zero.</summary>
    public static TimeSpan FromSeconds(double seconds)
    {
        if (double.IsNaN(seconds))
        {
            return TimeSpan.Zero;
        }

        var max = TimeSpan.MaxValue.TotalSeconds - 1;
        return seconds >= max ? TimeSpan.MaxValue
            : seconds <= -max ? TimeSpan.MinValue
            : TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>An export job for one recording: the chunks overlapping the range plus the trim
/// offsets relative to the first chunk.</summary>
public sealed record ExportSlice(
    Recording Recording,
    IReadOnlyList<RecordingChunk> Chunks,
    TimeSpan TrimStart,
    TimeSpan TrimEnd,
    DateTime WallClockStart)
{
    public TimeSpan Duration => TrimEnd - TrimStart;
}

/// <summary>
/// One recording placed on the continuous time axis, mapping axis positions onto the
/// recording's chunk sequence for playback.
///
/// Built initially from data that costs no extra card I/O: the recording's start time plus
/// per-chunk start offsets parsed from chunk file names (cameras name chunks with their
/// start timestamp). The final chunk's length — and with it the exact end — is estimated
/// until <see cref="Refine"/> runs with loaded metadata.
/// </summary>
public sealed class TimeSegment
{
    private static readonly Regex ChunkNameStamp = new(@"^(\d{8})_(\d{6})_", RegexOptions.Compiled);
    private const double NominalChunkSeconds = 300; // Axis writes ~5-minute chunks

    // A single recording can't plausibly last centuries; matches the metadata parsers' ceiling.
    private const double MaxDurationSeconds = 366.0 * 200 * 86400;

    /// <summary>
    /// Everything the mapping is built from, swapped in as one object: <see cref="Refine"/> may run on the UI thread
    /// while an agent request reads on another, and the recording's chunk list may be replaced meanwhile, so offsets
    /// are always paired with the very chunk list they were computed from.
    /// </summary>
    private sealed record Layout(double Start, double[] Offsets, double Duration, bool Refined, IReadOnlyList<RecordingChunk> Chunks);

    private Layout _layout;

    public TimeSegment(Recording recording)
    {
        Recording = recording;
        _layout = Build(recording);
    }

    public Recording Recording { get; }

    /// <summary>Where the footage starts on the axis. May move slightly when <see cref="Refine"/> replaces the
    /// recording's nominal start with its first chunk's true UTC start.</summary>
    public double StartSeconds => _layout.Start;

    public double DurationSeconds => _layout.Duration;

    public double EndSeconds
    {
        get
        {
            var layout = _layout;
            return layout.Start + layout.Duration;
        }
    }

    /// <summary>False while the end/chunk mapping is estimated from file names only.</summary>
    public bool IsRefined => _layout.Refined;

    /// <summary>Per-chunk start offsets in seconds from the recording start (a copy).</summary>
    public IReadOnlyList<double> ChunkOffsets => _layout.Offsets.ToArray();

    public bool Contains(double seconds)
    {
        var layout = _layout;
        return seconds >= layout.Start && seconds < layout.Start + layout.Duration;
    }

    /// <summary>Recomputes the mapping from loaded chunk metadata. Idempotent.</summary>
    public void Refine() => _layout = Build(Recording);

    /// <summary>Maps an axis position within this segment to (chunk index, offset within chunk).</summary>
    public (int ChunkIndex, TimeSpan Offset) Locate(double seconds)
    {
        var (index, offset, _) = LocateChunk(seconds);
        return (index, offset);
    }

    /// <summary>Like <see cref="Locate"/>, plus the chunk itself, all from one consistent mapping. Chunk is null only
    /// for a recording without chunks.</summary>
    public (int ChunkIndex, TimeSpan Offset, RecordingChunk? Chunk) LocateChunk(double seconds)
    {
        var layout = _layout;
        var rel = Math.Clamp(seconds - layout.Start, 0, Math.Max(0, layout.Duration - 0.001));
        for (var i = layout.Offsets.Length - 1; i >= 0; i--)
        {
            if (rel >= layout.Offsets[i])
            {
                return (i, TimeSpan.FromSeconds(rel - layout.Offsets[i]), layout.Chunks[i]);
            }
        }

        return (0, TimeSpan.Zero, layout.Chunks.Count > 0 ? layout.Chunks[0] : null);
    }

    /// <summary>How many chunks the current mapping has (its own snapshot of the recording's list).</summary>
    public int ChunkCount => _layout.Chunks.Count;

    /// <summary>A chunk of the current mapping by index (the index <see cref="Locate"/> returns), or null.</summary>
    public RecordingChunk? ChunkAt(int index)
    {
        var layout = _layout;
        return index >= 0 && index < layout.Chunks.Count ? layout.Chunks[index] : null;
    }

    public double SecondsAtChunkStart(int chunkIndex)
    {
        var layout = _layout;
        return layout.Start + (chunkIndex >= 0 && chunkIndex < layout.Offsets.Length ? layout.Offsets[chunkIndex] : 0);
    }

    /// <summary>The shortest overlap <see cref="SliceFor"/> makes a slice of: under a frame at any camera's rate.</summary>
    public const double MinSliceSeconds = 0.01;

    /// <summary>
    /// For an absolute time range [absStart, absEnd), returns the chunks of this recording that
    /// overlap it (in order) plus the trim offsets relative to the first returned chunk. Null when
    /// the range doesn't intersect this recording (or only by less than <see cref="MinSliceSeconds"/>).
    /// Requires <see cref="Refine"/> for exact trims.
    /// </summary>
    public ExportSlice? SliceFor(double absStart, double absEnd)
    {
        var layout = _layout;
        var offsets = layout.Offsets;
        var start = Math.Max(absStart, layout.Start);
        var end = Math.Min(absEnd, layout.Start + layout.Duration);
        if (end - start < MinSliceSeconds || offsets.Length == 0)
        {
            return null; // no overlap, or a sliver shorter than any frame (its trims could round to an empty range)
        }

        var relStart = start - layout.Start;
        var relEnd = end - layout.Start;

        var firstIndex = -1;
        var lastIndex = -1;
        for (var i = 0; i < offsets.Length; i++)
        {
            var chunkStart = offsets[i];
            var chunkEnd = i + 1 < offsets.Length ? offsets[i + 1] : layout.Duration;
            if (chunkStart < relEnd && chunkEnd > relStart)
            {
                if (firstIndex < 0)
                {
                    firstIndex = i;
                }

                lastIndex = i;
            }
        }

        if (firstIndex < 0)
        {
            return null;
        }

        var firstOffset = offsets[firstIndex];
        var chunks = layout.Chunks.Skip(firstIndex).Take(lastIndex - firstIndex + 1).ToList();
        return new ExportSlice(
            Recording,
            chunks,
            TimeSpan.FromSeconds(Math.Max(0, relStart - firstOffset)),
            TimeSpan.FromSeconds(relEnd - firstOffset),
            TimeAxis.ToDateTime(start));
    }

    /// <summary><paramref name="start"/> + <paramref name="duration"/>, saturating at
    /// <see cref="DateTime.MaxValue"/> instead of throwing on a pathological (corrupt/crafted) duration.
    /// Negative durations are floored to zero. Shared by the browse-tree labels and the card-summary span
    /// so a single bad recording can never overflow DateTime and abort the whole card open.</summary>
    public static DateTime SafeEnd(DateTime start, TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var max = DateTime.MaxValue - start;
        return duration > max ? DateTime.MaxValue : start + duration;
    }

    /// <summary>Best-known duration without requiring metadata (used for labels and span math).</summary>
    public static TimeSpan EstimateDuration(Recording recording)
    {
        if (recording.Duration is { } exact)
        {
            return exact;
        }

        return SafeTime.FromSeconds(Build(recording).Duration);
    }

    private static Layout Build(Recording recording)
    {
        var start = TimeAxis.ToSeconds(recording.StartTime);
        var chunks = recording.Chunks; // read once: the list may be replaced meanwhile, never changed in place
        if (chunks.Count == 0)
        {
            return new Layout(start, [], 1, recording.MetadataLoaded, chunks);
        }

        var offsets = new double[chunks.Count];
        var lastKnown = chunks[^1].Duration is not null;

        // Exact, once metadata is loaded: each chunk's true UTC start (sidecar or MKV header), measured from the
        // first chunk, which is also where the segment starts. Chunk file names are camera-LOCAL time, so
        // offsets taken from them are an hour out after a daylight-saving change inside a long recording, and
        // the nominal start is wrong when the camera has pruned the recording's oldest blocks.
        var utc = chunks.Select(c => c.StartTimeUtc).ToArray();
        if (utc.All(u => u is not null))
        {
            var first = AsUtc(utc[0]!.Value);
            for (var i = 0; i < chunks.Count; i++)
            {
                offsets[i] = Math.Max(i == 0 ? 0 : offsets[i - 1], (AsUtc(utc[i]!.Value) - first).TotalSeconds);
            }

            var lastSeconds = chunks[^1].Duration?.TotalSeconds ?? EstimateChunkSeconds(offsets);
            return new Layout(TimeAxis.ToSeconds(first), offsets, Bounded(offsets[^1] + Math.Max(1, lastSeconds)),
                lastKnown || recording.MetadataLoaded, chunks);
        }

        // Estimate: chunk start offsets from the timestamps in chunk file names.
        var stamps = chunks.Select(c => TryParseStamp(c.FileName)).ToArray();
        if (stamps.All(s => s is not null))
        {
            var first = stamps[0]!.Value;
            for (var i = 0; i < chunks.Count; i++)
            {
                offsets[i] = Math.Max(0, (stamps[i]!.Value - first).TotalSeconds);
            }

            var lastSeconds = chunks[^1].Duration?.TotalSeconds ?? EstimateChunkSeconds(offsets);
            return new Layout(start, offsets, Bounded(offsets[^1] + Math.Max(1, lastSeconds)), lastKnown || recording.MetadataLoaded, chunks);
        }

        // Fallback (legacy numeric chunk names): cumulative known durations.
        var cursor = 0.0;
        for (var i = 0; i < chunks.Count; i++)
        {
            offsets[i] = cursor;
            var seconds = chunks[i].Duration?.TotalSeconds ?? NominalChunkSeconds;
            cursor = Bounded(cursor + (double.IsFinite(seconds) ? seconds : NominalChunkSeconds));
        }

        return new Layout(start, offsets, Bounded(Math.Max(1, cursor)), chunks.All(c => c.Duration is not null) || recording.MetadataLoaded, chunks);
    }

    private static DateTime AsUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };

    // Keep a crafted/corrupt duration from producing NaN or absurd spans that would break timeline layout.
    private static double Bounded(double seconds) =>
        double.IsFinite(seconds) ? Math.Clamp(seconds, 1, MaxDurationSeconds) : 1;

    private static double EstimateChunkSeconds(double[] offsets)
    {
        if (offsets.Length < 2)
        {
            return NominalChunkSeconds;
        }

        var deltas = new List<double>();
        for (var i = 1; i < offsets.Length; i++)
        {
            deltas.Add(offsets[i] - offsets[i - 1]);
        }

        deltas.Sort();
        return Math.Clamp(deltas[deltas.Count / 2], 1, 3600);
    }

    private static DateTime? TryParseStamp(string fileName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var match = ChunkNameStamp.Match(name);
        if (!match.Success)
        {
            return null;
        }

        return DateTime.TryParseExact(
            match.Groups[1].Value + match.Groups[2].Value,
            "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;
    }
}
