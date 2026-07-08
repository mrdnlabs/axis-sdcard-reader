using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using AxisSdReader.Core.Axis;
using DiscUtils;

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
/// Exports a time range as a single lossless file by copying the overlapping on-card MKV
/// chunks to a temp folder, concatenating them, and trimming/remuxing with FFmpeg using
/// stream copy (<c>-c copy</c>) — no re-encode, so it is fast and preserves quality.
/// Trims are keyframe-accurate at the head (a copy-mode constraint). FFmpeg is located next
/// to the app, then on PATH.
/// </summary>
public static class FfmpegExporter
{
    /// <summary>Locates <c>ffmpeg.exe</c>: an "ffmpeg" folder beside the app, then the app dir. Falls back
    /// to PATH only when NOT running elevated (see below).</summary>
    public static string? FindFfmpeg()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg.exe"),
        ];
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // When running elevated, do NOT fall back to %PATH%: a standard user could plant a malicious
        // ffmpeg.exe on a writable PATH directory that would then execute with administrator rights. An
        // elevated build trusts only an ffmpeg co-located with the app (installed to a protected folder).
        if (IsElevated())
        {
            return null;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore malformed PATH entries
            }
        }

        return null;
    }

    /// <summary>True when the current process is running with administrator rights.</summary>
    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAvailable => FindFfmpeg() is not null;

    /// <summary>Best-effort removal of leftover export temp folders from a previous run that was killed
    /// mid-export (power loss / crash), so multi-GB chunk copies don't accumulate in %TEMP%. Safe to call
    /// once at startup, before any export begins.</summary>
    public static void SweepStaleTempDirs()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "axis-export-*"))
            {
                TryDeleteDir(dir);
            }
        }
        catch
        {
            // best effort — the temp dir may be inaccessible; nothing depends on this succeeding
        }
    }

    /// <summary>
    /// Exports the given chunks (already narrowed to those overlapping the range and in order),
    /// trimmed to <paramref name="trimStart"/>..<paramref name="trimEnd"/> measured from the start
    /// of the first chunk, into <paramref name="outputPath"/>.
    /// </summary>
    public static async Task<FfmpegExportResult> ExportAsync(
        DiscFileSystem fileSystem,
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

        var ffmpeg = FindFfmpeg()
            ?? throw new FileNotFoundException(
                "FFmpeg was not found. Place ffmpeg.exe in an 'ffmpeg' folder next to the app. " +
                "(When running as administrator, the PATH is not searched, for security.)");

        var duration = trimEnd - trimStart;
        var tempDir = Path.Combine(Path.GetTempPath(), "axis-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // Only clean up the output on failure if this call is what created it (don't delete a
        // pre-existing user file if something goes wrong).
        var outputPreExisted = File.Exists(outputPath);

        try
        {
            // 1) Copy the needed chunks off the read-only card to temp files FFmpeg can seek.
            var listBuilder = new StringBuilder();
            for (var i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new FfmpegProgress(0.05 + 0.15 * i / chunks.Count, "Copying source…"));

                var tempPath = Path.Combine(tempDir, $"{i:D4}.mkv");
                using (var source = fileSystem.OpenFile(chunks[i].Path, FileMode.Open, FileAccess.Read))
                using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    await source.CopyToAsync(target, 1 << 20, cancellationToken);
                }

                // concat demuxer: single-quote the path and escape embedded quotes.
                listBuilder.Append("file '").Append(tempPath.Replace("'", @"'\''")).Append("'\n");
            }

            var listPath = Path.Combine(tempDir, "concat.txt");
            await File.WriteAllTextAsync(listPath, listBuilder.ToString(), cancellationToken);

            // 2) Concat + trim + remux with stream copy.
            var args = BuildArguments(listPath, trimStart, duration, container, outputPath);
            await RunFfmpegAsync(ffmpeg, args, duration, progress, cancellationToken);

            var info = new FileInfo(outputPath);
            if (!info.Exists || info.Length == 0)
            {
                throw new IOException("FFmpeg produced no output. The selected range may contain no keyframes.");
            }

            progress?.Report(new FfmpegProgress(1.0, "Done"));
            return new FfmpegExportResult(outputPath, info.Length, duration);
        }
        catch
        {
            if (!outputPreExisted)
            {
                TryDelete(outputPath);
            }

            throw;
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static string BuildArguments(string listPath, TimeSpan trimStart, TimeSpan duration,
        ExportContainer container, string outputPath)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -y -f concat -safe 0 -i ").Append(Quote(listPath));

        // Output-side seek keeps the trim accurate within the concatenated stream under -c copy.
        if (trimStart > TimeSpan.Zero)
        {
            sb.Append(" -ss ").Append(Sec(trimStart));
        }

        if (duration > TimeSpan.Zero)
        {
            sb.Append(" -t ").Append(Sec(duration));
        }

        sb.Append(" -c copy");

        if (container == ExportContainer.Mp4)
        {
            // Keep only the primary video/audio (MP4 can't hold arbitrary MKV tracks); make it
            // web-friendly and fix up copy-mode timestamps.
            sb.Append(" -map 0:v:0? -map 0:a:0? -movflags +faststart -avoid_negative_ts make_zero");
        }
        else
        {
            sb.Append(" -map 0");
        }

        sb.Append(' ').Append(Quote(outputPath));
        sb.Append(" -progress pipe:1 -nostats");
        return sb.ToString();
    }

    private static async Task RunFfmpegAsync(string ffmpeg, string args, TimeSpan duration,
        IProgress<FfmpegProgress>? progress, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Don't inherit the elevated app's current directory (a possibly-writable DLL-search path).
            // Run in ffmpeg's own directory, where its expected DLLs live.
            WorkingDirectory = Path.GetDirectoryName(ffmpeg) ?? Path.GetTempPath(),
        };

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            // -progress emits "out_time_us=..." lines during encode.
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(e.Data["out_time_us=".Length..], out var us) && duration.TotalSeconds > 0)
            {
                var fraction = 0.20 + 0.78 * Math.Clamp(us / 1_000_000.0 / duration.TotalSeconds, 0, 1);
                progress?.Report(new FfmpegProgress(fraction, "Writing…"));
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            // Kill is asynchronous on Windows; wait for the process to actually exit so it releases its
            // handles to the temp input files before the caller's finally deletes the temp folder.
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch
            {
                // best effort — we are already unwinding a cancellation
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            var tail = string.Join('\n', stderr.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(6));
            throw new IOException($"FFmpeg failed (exit {process.ExitCode}).\n{tail}");
        }
    }

    private static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quote(string path) => "\"" + path + "\"";

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDeleteDir(string dir)
    {
        // Retry briefly: a just-killed ffmpeg can still hold input-file handles for a short window (Windows
        // releases them asynchronously), so the first Delete may hit a sharing violation.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }

                return;
            }
            catch when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch
            {
                return; // give up after retries — best effort
            }
        }
    }
}
