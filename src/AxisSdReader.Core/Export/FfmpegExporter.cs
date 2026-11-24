using System.Globalization;
using System.Text;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Media;

namespace AxisSdReader.Core.Export;

/// <summary>Output container for a trimmed export.</summary>
public enum ExportContainer
{
    /// <summary>MP4, stream-copied (remux) — plays in most players; lossless.</summary>
    Mp4,

    /// <summary>Matroska, stream-copied — original streams, lossless.</summary>
    Mkv,
}

/// <summary>Progress for an FFmpeg export: 0..1 plus the current phase.</summary>
public sealed record FfmpegProgress(double Fraction, string Phase);

public sealed record FfmpegExportResult(string OutputPath, long Bytes, TimeSpan Duration);

/// <summary>
/// What one export file may cost: how much FFmpeg may write, and for how long it may run. A crafted card can pack any
/// amount of data into a few minutes of timestamps, so the clip's length alone bounds neither.
/// </summary>
public sealed record ExportLimits(long MaxBytes, TimeSpan Timeout)
{
    // Far beyond any camera's bitrate (Axis cameras stay well under 30 Mbit/s).
    private const double MaxBytesPerSecond = 100_000_000 / 8.0;
    private const long Overhead = 64L << 20;
    private const long FreeSpaceReserve = 1L << 30;

    /// <summary>Below this much free space on its drive, a running export is stopped - whatever else (another export,
    /// say) is filling the drive meanwhile.</summary>
    public const long MinFreeBytes = FreeSpaceReserve / 2;

    /// <summary>The folder the export is written to, watched for free space while FFmpeg runs.</summary>
    public string? Directory { get; init; }

    /// <summary>
    /// The limits for a clip <paramref name="duration"/> long, made from <paramref name="sourceBytes"/> of chunks
    /// (when known), written into <paramref name="directory"/>: no more than such footage could hold, never so much
    /// that less than 1 GiB stays free on that drive, and a run time in proportion. Null when the drive hasn't room.
    /// </summary>
    public static ExportLimits? For(TimeSpan duration, long? sourceBytes, string directory)
    {
        var seconds = Math.Clamp(duration.TotalSeconds, 1, TimeSpan.FromDays(7).TotalSeconds);
        var max = (long)(seconds * MaxBytesPerSecond) + Overhead;
        if (sourceBytes is > 0 and var source && source < long.MaxValue / 2)
        {
            max = Math.Min(max, source + source / 8 + Overhead); // a remux adds little to what it copies
        }

        // Free space that can't be read counts as none.
        max = Math.Min(max, (FreeBytes(directory) ?? 0) - FreeSpaceReserve);
        return max < (1L << 20)
            ? null
            : new ExportLimits(max, TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(seconds * 4)) { Directory = directory };
    }

    /// <summary>The bytes free to this user on the drive holding <paramref name="directory"/>, or null if unknown.</summary>
    public static long? FreeBytes(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                return new DriveInfo(directory).AvailableFreeSpace;
            }
            catch
            {
                return null;
            }
        }

        var path = directory.EndsWith('\\') ? directory : directory + "\\";
        return GetDiskFreeSpaceExW(path, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}

/// <summary>
/// Exports a time range as a single lossless file: FFmpeg reads the overlapping on-card MKV chunks over the
/// app's loopback media server (<see cref="LocalMediaServer"/>), concatenates them, and trims/remuxes with
/// stream copy (<c>-c copy</c>) — no re-encode, so it is fast and preserves quality, and nothing decrypted is
/// ever staged on disk. Trims are keyframe-accurate at the head (a copy-mode constraint).
/// </summary>
public static class FfmpegExporter
{
    /// <summary>The FFmpeg the app will use (see <see cref="FfmpegLocator.Find"/>), or null.</summary>
    public static string? FindFfmpeg() => FfmpegLocator.Find();

    public static bool IsAvailable => FindFfmpeg() is not null;

    /// <summary>
    /// Exports chunks served at <paramref name="chunkUrls"/> (in order), trimmed to
    /// <paramref name="trimStart"/>..<paramref name="trimEnd"/> measured from the start of the first chunk,
    /// into <paramref name="outputPath"/> (overwritten: callers create it exclusively first, see
    /// <see cref="Security.GuardedFolder.CreateExclusive"/>). Does not delete the output on failure — the
    /// caller owns it. FFmpeg stops at <paramref name="limits"/> (size, run time, the drive's free space) and the export
    /// then fails.
    /// </summary>
    public static async Task<FfmpegExportResult> ExportAsync(
        IReadOnlyList<Uri> chunkUrls,
        TimeSpan trimStart,
        TimeSpan trimEnd,
        ExportContainer container,
        string outputPath,
        ExportLimits limits,
        IProgress<FfmpegProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? ffmpegPath = null)
    {
        if (chunkUrls.Count == 0)
        {
            throw new ArgumentException("No chunks to export.", nameof(chunkUrls));
        }

        foreach (var url in chunkUrls)
        {
            // Only our own loopback server's lease URLs (a fixed, quote-free shape): the concat list must never
            // point FFmpeg anywhere else.
            if (!LocalMediaServer.IsLeaseUrl(url))
            {
                throw new ArgumentException("Export sources must be the app's own loopback media URLs.", nameof(chunkUrls));
            }
        }

        if (trimEnd <= trimStart)
        {
            // An empty range would leave FFmpeg without its -t: it would copy whole chunks under a 1-second clip's limits.
            throw new ArgumentException("The range to export is empty.", nameof(trimEnd));
        }

        var ffmpeg = ffmpegPath ?? FindFfmpeg()
            ?? throw new FileNotFoundException(
                "FFmpeg was not found. Place ffmpeg.exe in an 'ffmpeg' folder next to the app. " +
                "(When running as administrator, the PATH is not searched, for security.)");

        var duration = trimEnd - trimStart;
        var list = new StringBuilder("ffconcat version 1.0\n");
        foreach (var url in chunkUrls)
        {
            list.Append("file '").Append(url.AbsoluteUri).Append("'\n");
        }

        progress?.Report(new FfmpegProgress(0.02, "Starting…"));

        // Each file's size limit leaves room on the drive when it starts; exports running side by side (two agents, an
        // agent and the app) could still fill it together, so the free space is watched while FFmpeg runs. And the
        // video frames written are counted: a crafted chunk of countless tiny frames with ever later timestamps would
        // otherwise keep FFmpeg's muxer busy - and its memory growing - for as long as the run is allowed.
        using var lowSpace = new CancellationTokenSource();
        using var tooManyFrames = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lowSpace.Token, tooManyFrames.Token);
        var watch = limits.Directory is { } folder ? WatchFreeSpaceAsync(folder, lowSpace, linked.Token) : Task.CompletedTask;

        // Stalled means no progress: FFmpeg repeats its report every half second whether or not anything moves, so
        // only a later output time or a bigger output counts (a chunk of junk after a few good frames would otherwise
        // keep it "busy" until the overall timeout) - see ProgressTracker for the silent end of an MP4.
        var tracker = new ProgressTracker((long)Math.Ceiling(Math.Max(1, duration.TotalSeconds) * MaxFramesPerSecond) + 600, tooManyFrames);

        // When a chunk can't be opened or read part-way through (the card removed or damaged, its address revoked, a
        // chunk that isn't Matroska), FFmpeg ends the input there, finishes the file and exits cleanly: a playable clip
        // missing the rest of the range. Its concat input says so, and only then (an interrupted last chunk, cut short
        // where recording stopped, is just "File ended prematurely" to the chunk's own demuxer and is fine).
        var inputFailed = false;
        FfmpegRunResult result;
        try
        {
            result = await FfmpegRunner.RunAsync(ffmpeg, BuildArguments(trimStart, duration, container, outputPath, limits.MaxBytes),
                new FfmpegRunOptions
                {
                    StandardInput = Encoding.UTF8.GetBytes(list.ToString()),
                    OnStandardOutputLine = line => ReportProgress(line, duration, progress, tracker),
                    OnStandardErrorLine = line => inputFailed |= IsInputFailure(line),
                    IsActivity = tracker.Advances,
                    StallAllowance = tracker.Allowance,
                    Timeout = limits.Timeout,
                },
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lowSpace.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The drive the export was being saved to is almost full, so the export was stopped.");
        }
        catch (OperationCanceledException) when (tooManyFrames.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException(TooManyFrames);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await watch.ConfigureAwait(false);
        }

        // A short run can end before its last count is read: the same file fails however fast FFmpeg was.
        if (tooManyFrames.IsCancellationRequested)
        {
            throw new IOException(TooManyFrames);
        }

        if (result.TimedOut)
        {
            throw new IOException("FFmpeg stopped responding, or took far longer than this clip should, and was stopped.");
        }

        if (result.ExitCode != 0)
        {
            var tail = TailLines(LocalMediaServer.RedactTokens(result.ErrorTail), 6).Split('\n').Select(l => Agent.AgentService.Clean(l, 200));
            throw new IOException($"FFmpeg failed (exit {result.ExitCode}).\n{string.Join('\n', tail)}");
        }

        if (inputFailed)
        {
            throw new IOException(
                "Part of the footage couldn't be read (the card may have been removed or be damaged there, or the footage " +
                "stopped being available), so the export would be incomplete and was not kept. Try again, or a smaller range.");
        }

        var info = new FileInfo(outputPath);
        if (!info.Exists || info.Length == 0 || tracker.Frames == 0)
        {
            throw new IOException("FFmpeg produced no video. The selected range may contain no keyframes.");
        }

        if (info.Length >= limits.MaxBytes)
        {
            throw new IOException(
                $"The export reached {limits.MaxBytes / 1048576.0:0} MB — more than this footage should hold, or all the room left on the drive — and was stopped.");
        }

        progress?.Report(new FfmpegProgress(1.0, "Done"));
        return new FfmpegExportResult(outputPath, info.Length, duration);
    }

    /// <summary>Video frames per second of clip a file may hold: far above any camera's frame rate. (Counted from
    /// FFmpeg's report: the FFmpeg shipped with the app reports them; some other builds don't for stream copies, and
    /// then only the size and time limits apply.)</summary>
    private const int MaxFramesPerSecond = 240;

    private const string TooManyFrames =
        "The footage holds far more frames than its length allows (it may be damaged), so the export was stopped.";

    /// <summary>Whether an FFmpeg message says its input failed and ended early: a chunk that couldn't be opened, or a
    /// read that failed (FFmpeg 6.1 and later word it this way).</summary>
    internal static bool IsInputFailure(string line) =>
        line.Contains("Error during demuxing", StringComparison.Ordinal) ||
        line.Contains("Impossible to open", StringComparison.Ordinal);

    /// <summary>Cancels <paramref name="lowSpace"/> when <paramref name="directory"/>'s drive drops below
    /// <see cref="ExportLimits.MinFreeBytes"/>; runs until <paramref name="stop"/>.</summary>
    private static async Task WatchFreeSpaceAsync(string directory, CancellationTokenSource lowSpace, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
                if ((ExportLimits.FreeBytes(directory) ?? 0) < ExportLimits.MinFreeBytes) // unknown counts as none
                {
                    await lowSpace.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the export ended
        }
    }

    /// <summary>
    /// Convenience overload for callers that own a filesystem exclusively (tests, tools): serves the chunks
    /// through a private loopback server for the duration of the export. All reads go through one gate, so
    /// FFmpeg's concurrent range requests never touch the (non-thread-safe) filesystem at the same time.
    /// </summary>
    public static async Task<FfmpegExportResult> ExportAsync(
        Ext4Volume fileSystem,
        IReadOnlyList<RecordingChunk> chunks,
        TimeSpan trimStart,
        TimeSpan trimEnd,
        ExportContainer container,
        string outputPath,
        IProgress<FfmpegProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
        {
            throw new ArgumentException("No chunks to export.", nameof(chunks));
        }

        var limits = ExportLimits.For(trimEnd - trimStart, RecordingChunk.TotalBytes(chunks), Path.GetDirectoryName(Path.GetFullPath(outputPath))!)
            ?? throw new IOException("There isn't enough free space for this export.");
        var gate = new CardIoGate();
        using var server = LocalMediaServer.Start();
        using var lease = server.CreateLease(ChunkSources(chunks, gate, fileSystem));
        return await ExportAsync(lease.Urls, trimStart, trimEnd, container, outputPath, limits, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Media sources for chunks, read through <paramref name="gate"/>.</summary>
    public static IReadOnlyList<MediaSource> ChunkSources(IReadOnlyList<RecordingChunk> chunks, CardIoGate gate, Ext4Volume fileSystem) =>
        chunks.Select((chunk, i) => new MediaSource(
                $"c{i}.mkv",
                chunk.SizeBytes,
                () => new SynchronizedCardStream(gate, () => Recording.OpenChunk(fileSystem, chunk), chunk.SizeBytes)))
            .ToList();

    private static void ReportProgress(string line, TimeSpan duration, IProgress<FfmpegProgress>? progress, ProgressTracker tracker)
    {
        // -progress emits "out_time_us=..." lines while writing; only a later time is reported (never the same twice).
        if (progress is null || duration.TotalSeconds <= 0 || !ProgressTracker.TryValue(line, "out_time_us=", out var us) ||
            !tracker.IsNewReportedTime(us))
        {
            return;
        }

        var fraction = 0.02 + 0.96 * Math.Clamp(us / 1_000_000.0 / duration.TotalSeconds, 0, 1);
        progress.Report(new FfmpegProgress(fraction, "Writing…"));
    }

    /// <summary>
    /// What counts as progress in FFmpeg's -progress report (a later output time or a bigger output), and how long a
    /// silence may last. While FFmpeg works, its report repeats every half second: 90 seconds without progress then is
    /// a stall. When the report stops after output began, FFmpeg is finishing the file - MP4's faststart pass rewrites
    /// all of it without a word - so it gets time for that at a slow drive's pace (a USB stick, an SD card).
    /// </summary>
    private sealed class ProgressTracker(long maxFrames, CancellationTokenSource tooManyFrames)
    {
        private const double SlowDriveBytesPerSecond = 2 * 1024 * 1024;
        private static readonly TimeSpan Stall = TimeSpan.FromSeconds(90);

        private long _time = -1;
        private long _size = -1;
        private long _frames = -1;
        private long _lastReport = Environment.TickCount64;
        private long _reported = long.MinValue;

        /// <summary>The video frames FFmpeg last reported written (-1 before its first report).</summary>
        public long Frames => Volatile.Read(ref _frames);

        public bool Advances(string line)
        {
            Volatile.Write(ref _lastReport, Environment.TickCount64);
            if (line.StartsWith("progress=end", StringComparison.Ordinal))
            {
                return true;
            }

            if (TryValue(line, "frame=", out var frames))
            {
                Volatile.Write(ref _frames, frames);
                if (frames > maxFrames)
                {
                    tooManyFrames.Cancel();
                }

                return false; // a frame count on its own isn't progress (out_time and total_size say that)
            }

            if (TryValue(line, "out_time_us=", out var time) && time > _time)
            {
                _time = time;
                return true;
            }

            if (TryValue(line, "total_size=", out var size) && size > Volatile.Read(ref _size))
            {
                Volatile.Write(ref _size, size);
                return true;
            }

            return false;
        }

        public TimeSpan Allowance()
        {
            var size = Volatile.Read(ref _size);
            var quiet = Environment.TickCount64 - Volatile.Read(ref _lastReport) > 3000;
            return quiet && size > 0 ? Stall + TimeSpan.FromSeconds(size / SlowDriveBytesPerSecond) : Stall;
        }

        public bool IsNewReportedTime(long time)
        {
            if (time <= _reported)
            {
                return false;
            }

            _reported = time;
            return true;
        }

        public static bool TryValue(string line, string key, out long value)
        {
            value = 0;
            return line.StartsWith(key, StringComparison.Ordinal) &&
                   long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    internal static IReadOnlyList<string> BuildArguments(TimeSpan trimStart, TimeSpan duration, ExportContainer container, string outputPath,
        long maxBytes = 0)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-y",
            // The input may only be the concat list on stdin, Matroska chunks, and our loopback HTTP server —
            // never local files, other protocols, or other demuxers (attack surface for a crafted card).
            "-protocol_whitelist", "pipe,http,tcp",
            "-format_whitelist", "concat,matroska",
            "-f", "concat", "-safe", "0", "-i", "pipe:0",
        };

        // Output-side seek keeps the trim accurate within the concatenated stream under -c copy.
        if (trimStart > TimeSpan.Zero)
        {
            args.AddRange(["-ss", Sec(trimStart)]);
        }

        if (duration > TimeSpan.Zero)
        {
            args.AddRange(["-t", Sec(duration)]);
        }

        args.AddRange(["-c", "copy"]);
        if (container == ExportContainer.Mp4)
        {
            // Keep only the primary video/audio (MP4 can't hold arbitrary MKV tracks); make it web-friendly
            // and fix up copy-mode timestamps.
            args.AddRange(["-map", "0:v:0?", "-map", "0:a:0?", "-movflags", "+faststart", "-avoid_negative_ts", "make_zero"]);
        }
        else
        {
            args.AddRange(["-map", "0", "-ignore_unknown"]); // every track it can carry (attachments are skipped, not fatal)
        }

        if (maxBytes > 0)
        {
            args.AddRange(["-fs", maxBytes.ToString(CultureInfo.InvariantCulture)]); // stop writing past the limit
        }

        args.AddRange(["-progress", "pipe:1", "-nostats", "file:" + outputPath]);
        return args;
    }

    private static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string TailLines(string text, int count) =>
        string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(count));
}
