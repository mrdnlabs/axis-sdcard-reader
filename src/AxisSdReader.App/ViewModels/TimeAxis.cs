using System.Globalization;
using System.Text.RegularExpressions;
using AxisSdReader.Core.Axis;

namespace AxisSdReader.App.ViewModels;

/// <summary>
/// The app's continuous time axis: seconds (double) since a fixed <em>UTC</em> epoch.
/// All timeline math — segments, playhead, scrubbing, selection — runs on this axis. Using UTC
/// keeps the axis strictly monotonic, so footage that spans a daylight-saving change still orders,
/// seeks and slices correctly; conversion to the user's local time happens only in
/// <see cref="ToDateTime"/> (and the display helpers built on it), so a value is still shown as the
/// local wall-clock time. Midnight is just another value and recordings can span days.
/// </summary>
public static class TimeAxis
{
    // Arbitrary fixed origin, comfortably before any camera footage.
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static double ToSeconds(DateTime time)
    {
        // Treat non-UTC values (Unspecified folder-name times, or Local) as local and normalise to UTC.
        var utc = time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime();
        return (utc - Epoch).TotalSeconds;
    }

    /// <summary>The local wall-clock time for an axis position (inverse of <see cref="ToSeconds"/> for display).</summary>
    public static DateTime ToDateTime(double seconds) => Epoch.AddSeconds(seconds).ToLocalTime();

    public static string ClockText(double seconds) => ToDateTime(seconds).ToString("HH:mm:ss");

    public static string DateShort(double seconds) => ToDateTime(seconds).ToString("MMM d");

    public static string DateLong(double seconds) => ToDateTime(seconds).ToString("ddd, MMM d, yyyy");
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

    private double[] _chunkOffsets; // seconds from recording start, per chunk

    public TimeSegment(Recording recording)
    {
        Recording = recording;
        StartSeconds = TimeAxis.ToSeconds(recording.StartTime);
        (_chunkOffsets, DurationSeconds, IsRefined) = Build(recording);
    }

    public Recording Recording { get; }

    public double StartSeconds { get; }

    public double DurationSeconds { get; private set; }

    public double EndSeconds => StartSeconds + DurationSeconds;

    /// <summary>False while the end/chunk mapping is estimated from file names only.</summary>
    public bool IsRefined { get; private set; }

    public bool Contains(double seconds) => seconds >= StartSeconds && seconds < EndSeconds;

    /// <summary>Recomputes the mapping from loaded chunk metadata. Idempotent.</summary>
    public void Refine()
    {
        (_chunkOffsets, DurationSeconds, IsRefined) = Build(Recording);
    }

    /// <summary>Maps an axis position within this segment to (chunk index, offset within chunk).</summary>
    public (int ChunkIndex, TimeSpan Offset) Locate(double seconds)
    {
        var rel = Math.Clamp(seconds - StartSeconds, 0, Math.Max(0, DurationSeconds - 0.001));
        for (var i = _chunkOffsets.Length - 1; i >= 0; i--)
        {
            if (rel >= _chunkOffsets[i])
            {
                return (i, TimeSpan.FromSeconds(rel - _chunkOffsets[i]));
            }
        }

        return (0, TimeSpan.Zero);
    }

    public double SecondsAtChunkStart(int chunkIndex) =>
        StartSeconds + (chunkIndex >= 0 && chunkIndex < _chunkOffsets.Length ? _chunkOffsets[chunkIndex] : 0);

    /// <summary>
    /// For an absolute time range [absStart, absEnd), returns the chunks of this recording that
    /// overlap it (in order) plus the trim offsets relative to the first returned chunk. Null when
    /// the range doesn't intersect this recording. Requires <see cref="Refine"/> for exact trims.
    /// </summary>
    public ExportSlice? SliceFor(double absStart, double absEnd)
    {
        var start = Math.Max(absStart, StartSeconds);
        var end = Math.Min(absEnd, EndSeconds);
        if (end <= start || _chunkOffsets.Length == 0)
        {
            return null;
        }

        var relStart = start - StartSeconds;
        var relEnd = end - StartSeconds;

        var firstIndex = -1;
        var lastIndex = -1;
        for (var i = 0; i < _chunkOffsets.Length; i++)
        {
            var chunkStart = _chunkOffsets[i];
            var chunkEnd = i + 1 < _chunkOffsets.Length ? _chunkOffsets[i + 1] : DurationSeconds;
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

        var firstOffset = _chunkOffsets[firstIndex];
        var chunks = Recording.Chunks.Skip(firstIndex).Take(lastIndex - firstIndex + 1).ToList();
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

        var (_, duration, _) = Build(recording);
        return TimeSpan.FromSeconds(duration);
    }

    private static (double[] Offsets, double Duration, bool Refined) Build(Recording recording)
    {
        var chunks = recording.Chunks;
        if (chunks.Count == 0)
        {
            return ([], 1, false);
        }

        var offsets = new double[chunks.Count];
        var haveAllDurations = chunks.All(c => c.Duration is not null);

        // Preferred: exact chunk start offsets from the timestamps in chunk file names.
        var stamps = chunks.Select(c => TryParseStamp(c.FileName)).ToArray();
        if (stamps.All(s => s is not null) && chunks.Count >= 1)
        {
            var first = stamps[0]!.Value;
            for (var i = 0; i < chunks.Count; i++)
            {
                offsets[i] = (stamps[i]!.Value - first).TotalSeconds;
            }

            var lastDuration = chunks[^1].Duration?.TotalSeconds ?? EstimateChunkSeconds(offsets);
            return (offsets, offsets[^1] + Math.Max(1, lastDuration), chunks[^1].Duration is not null);
        }

        // Fallback (legacy numeric chunk names): cumulative known durations.
        var cursor = 0.0;
        foreach (var (chunk, i) in chunks.Select((c, i) => (c, i)))
        {
            offsets[i] = cursor;
            cursor += chunk.Duration?.TotalSeconds ?? NominalChunkSeconds;
        }

        return (offsets, Math.Max(1, cursor), haveAllDurations);
    }

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
