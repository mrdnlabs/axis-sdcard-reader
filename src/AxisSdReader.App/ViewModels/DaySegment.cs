using AxisSdReader.App.Controls;
using AxisSdReader.Core.Axis;

namespace AxisSdReader.App.ViewModels;

/// <summary>
/// One recording placed on the 24-hour day timeline. Maps a day position (seconds into the
/// day) onto the recording's chunk sequence for playback.
/// </summary>
public sealed class DaySegment
{
    private readonly double[] _chunkStartOffsets; // cumulative seconds at the start of each chunk

    public DaySegment(Recording recording, double dayStartSeconds)
    {
        Recording = recording;
        DayStartSeconds = dayStartSeconds;

        _chunkStartOffsets = new double[recording.Chunks.Count];
        var cursor = 0.0;
        for (var i = 0; i < recording.Chunks.Count; i++)
        {
            _chunkStartOffsets[i] = cursor;
            cursor += recording.Chunks[i].Duration?.TotalSeconds ?? 0;
        }

        DurationSeconds = cursor > 0 ? cursor : Math.Max(1, recording.Chunks.Count); // keep clickable if durations unknown
    }

    public Recording Recording { get; }

    public double DayStartSeconds { get; }

    public double DurationSeconds { get; }

    public double DayEndSeconds => DayStartSeconds + DurationSeconds;

    public bool Contains(double daySeconds) => daySeconds >= DayStartSeconds && daySeconds < DayEndSeconds;

    public TimelineSegment ToTimelineSegment() => new(DayStartSeconds, DayEndSeconds);

    /// <summary>Maps a day position within this segment to (chunk index, offset within chunk).</summary>
    public (int ChunkIndex, TimeSpan Offset) Locate(double daySeconds)
    {
        var rel = Math.Clamp(daySeconds - DayStartSeconds, 0, Math.Max(0, DurationSeconds - 0.001));
        for (var i = _chunkStartOffsets.Length - 1; i >= 0; i--)
        {
            if (rel >= _chunkStartOffsets[i])
            {
                return (i, TimeSpan.FromSeconds(rel - _chunkStartOffsets[i]));
            }
        }

        return (0, TimeSpan.Zero);
    }

    /// <summary>Day position at the start of the given chunk.</summary>
    public double DaySecondsAtChunkStart(int chunkIndex) =>
        DayStartSeconds + (chunkIndex >= 0 && chunkIndex < _chunkStartOffsets.Length ? _chunkStartOffsets[chunkIndex] : 0);
}
