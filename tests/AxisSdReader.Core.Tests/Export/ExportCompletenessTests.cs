using AxisSdReader.Core.Export;
using AxisSdReader.Core.Media;

namespace AxisSdReader.Core.Tests.Export;

/// <summary>
/// Exports must keep all of their video however the other tracks behave: an audio track that ends early, or one that
/// starts late, must not cut the video short (as per-stream frame caps once did, silently).
/// </summary>
public sealed class ExportCompletenessTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "axis-export-complete-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [SkippableTheory]
    [InlineData(ExportContainer.Mp4, "audio ends early")]
    [InlineData(ExportContainer.Mkv, "audio ends early")]
    [InlineData(ExportContainer.Mp4, "audio starts late")]
    [InlineData(ExportContainer.Mkv, "audio starts late")]
    public async Task KeepsAllTheVideoWhateverTheAudioDoes(ExportContainer container, string audio)
    {
        // The FFmpeg the app ships with and whichever one this machine would otherwise use: they report progress
        // differently (some leave out frame counts for stream copies), and an export must come out whole with either.
        var ffmpegs = new[] { BundledFfmpeg(), FfmpegExporter.FindFfmpeg() }.OfType<string>()
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Skip.IfNot(OperatingSystem.IsWindows() && ffmpegs.Count > 0, "ffmpeg not found");

        // 20 s of video at 10 fps, with 5 s of audio at the start or the end.
        var source = Path.Combine(_dir, "source.mkv");
        string[] audioInput = audio == "audio ends early"
            ? ["-f", "lavfi", "-i", "sine=d=5"]
            : ["-itsoffset", "15", "-f", "lavfi", "-i", "sine=d=5"];
        var make = await FfmpegRunner.RunAsync(ffmpegs[0],
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=160x120:r=10:d=20", .. audioInput,
             "-map", "0:v", "-map", "1:a", "-c:v", "mjpeg", "-g", "1", "-c:a", "pcm_s16le", "file:" + source],
            new FfmpegRunOptions { Timeout = TimeSpan.FromSeconds(60) });
        Assert.True(make.Succeeded, make.ErrorTail);

        using var server = LocalMediaServer.Start();
        for (var i = 0; i < ffmpegs.Count; i++)
        {
            var ffmpeg = ffmpegs[i];
            using var lease = server.CreateLease([new MediaSource("c0.mkv", new FileInfo(source).Length, () => File.OpenRead(source))]);
            var output = Path.Combine(_dir, $"clip{i}." + (container == ExportContainer.Mp4 ? "mp4" : "mkv"));
            File.WriteAllBytes(output, []);
            var limits = ExportLimits.For(TimeSpan.FromSeconds(10), new FileInfo(source).Length, _dir)!;
            await FfmpegExporter.ExportAsync(lease.Urls, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(18), container, output, limits,
                ffmpegPath: ffmpeg);

            // Count the video frames that came out (10 s at 10 fps): one framemd5 line per packet.
            var frames = 0;
            var count = await FfmpegRunner.RunAsync(ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-i", "file:" + output, "-map", "0:v", "-c", "copy", "-f", "framemd5", "pipe:1"],
                new FfmpegRunOptions
                {
                    Timeout = TimeSpan.FromSeconds(60),
                    OnStandardOutputLine = line =>
                    {
                        if (line.Length > 0 && line[0] != '#')
                        {
                            frames++;
                        }
                    },
                });
            Assert.True(count.Succeeded, $"{ffmpeg}: {count.ErrorTail}");
            Assert.True(frames is >= 98 and <= 102, $"{ffmpeg}: {frames} frames in {new FileInfo(output).Length} bytes");
        }
    }

    [SkippableFact]
    public async Task FootageWithFarMoreFramesThanItsLengthAllowsIsStopped()
    {
        // A crafted card can pack countless frames into a few seconds. The FFmpeg the app ships with reports how many it
        // has written, and the export stops past what any camera could record in the clip's length.
        var ffmpeg = BundledFfmpeg();
        Skip.IfNot(OperatingSystem.IsWindows() && ffmpeg is not null, "the app's own ffmpeg isn't here");

        var source = Path.Combine(_dir, "dense.mkv");
        var make = await FfmpegRunner.RunAsync(ffmpeg!,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=32x32:r=1000:d=10",
             "-c:v", "mjpeg", "-g", "1", "file:" + source],
            new FfmpegRunOptions { Timeout = TimeSpan.FromSeconds(60) });
        Assert.True(make.Succeeded, make.ErrorTail);

        using var server = LocalMediaServer.Start();
        using var lease = server.CreateLease([new MediaSource("c0.mkv", new FileInfo(source).Length, () => File.OpenRead(source))]);
        var output = Path.Combine(_dir, "dense-clip.mkv");
        File.WriteAllBytes(output, []);
        var limits = ExportLimits.For(TimeSpan.FromSeconds(5), new FileInfo(source).Length, _dir)!;
        var error = await Assert.ThrowsAsync<IOException>(() => FfmpegExporter.ExportAsync(lease.Urls, TimeSpan.Zero,
            TimeSpan.FromSeconds(5), ExportContainer.Mkv, output, limits, ffmpegPath: ffmpeg));
        Assert.Contains("far more frames", error.Message);
    }

    [SkippableTheory]
    [InlineData("its address no longer answers")]
    [InlineData("the card fails part-way through it")]
    [InlineData("it isn't Matroska")]
    public async Task AnExportFailsWhenAChunkInTheMiddleCantBeRead(string failure)
    {
        // Three 10-second chunks; the middle one can't be read. FFmpeg would otherwise end the input there, finish the
        // file and exit cleanly: a playable clip with the rest of the footage missing, reported as complete.
        var ffmpeg = BundledFfmpeg();
        Skip.IfNot(OperatingSystem.IsWindows() && ffmpeg is not null, "the app's own ffmpeg isn't here");

        var chunk = Path.Combine(_dir, "chunk.mkv");
        var make = await FfmpegRunner.RunAsync(ffmpeg!,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=160x120:r=10:d=10",
             "-c:v", "mjpeg", "-g", "1", "file:" + chunk],
            new FfmpegRunOptions { Timeout = TimeSpan.FromSeconds(60) });
        Assert.True(make.Succeeded, make.ErrorTail);
        var size = new FileInfo(chunk).Length;
        var garbage = Path.Combine(_dir, "garbage.bin");
        File.WriteAllBytes(garbage, Enumerable.Range(0, (int)size).Select(i => (byte)(i * 7)).ToArray());

        using var server = LocalMediaServer.Start();
        using var first = server.CreateLease([new MediaSource("c0.mkv", size, () => File.OpenRead(chunk))]);
        var middle = server.CreateLease([failure switch
        {
            "the card fails part-way through it" => new MediaSource("c1.mkv", size, () => new FailingStream(File.OpenRead(chunk), size / 2)),
            "it isn't Matroska" => new MediaSource("c1.mkv", size, () => File.OpenRead(garbage)),
            _ => new MediaSource("c1.mkv", size, () => File.OpenRead(chunk)),
        }]);
        using var last = server.CreateLease([new MediaSource("c2.mkv", size, () => File.OpenRead(chunk))]);
        if (failure == "its address no longer answers")
        {
            middle.Dispose();
        }

        try
        {
            var output = Path.Combine(_dir, "gap.mkv");
            File.WriteAllBytes(output, []);
            var limits = ExportLimits.For(TimeSpan.FromSeconds(30), size * 3, _dir)!;
            var error = await Record.ExceptionAsync(() => FfmpegExporter.ExportAsync([first.Urls[0], middle.Urls[0], last.Urls[0]],
                TimeSpan.Zero, TimeSpan.FromSeconds(30), ExportContainer.Mkv, output, limits, ffmpegPath: ffmpeg));
            Assert.True(error is IOException, $"{error?.GetType().Name ?? "no error"}: {error?.Message}; output {new FileInfo(output).Length} bytes");
        }
        finally
        {
            middle.Dispose();
        }
    }

    [SkippableFact]
    public async Task ARecordingInterruptedMidChunkStillExports()
    {
        // A camera that lost power leaves its last chunk cut off mid-file: its own demuxer reports the early end, but
        // that is where the footage ends, not a failure - the export keeps all there is.
        var ffmpeg = BundledFfmpeg();
        Skip.IfNot(OperatingSystem.IsWindows() && ffmpeg is not null, "the app's own ffmpeg isn't here");

        var chunk = Path.Combine(_dir, "chunk.mkv");
        var make = await FfmpegRunner.RunAsync(ffmpeg!,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=160x120:r=10:d=10",
             "-c:v", "mjpeg", "-g", "1", "file:" + chunk],
            new FfmpegRunOptions { Timeout = TimeSpan.FromSeconds(60) });
        Assert.True(make.Succeeded, make.ErrorTail);
        var bytes = await File.ReadAllBytesAsync(chunk);
        var cut = Path.Combine(_dir, "cut.mkv");
        await File.WriteAllBytesAsync(cut, bytes.AsMemory(0, bytes.Length * 7 / 10).ToArray());

        using var server = LocalMediaServer.Start();
        using var lease = server.CreateLease([
            new MediaSource("c0.mkv", bytes.Length, () => File.OpenRead(chunk)),
            new MediaSource("c1.mkv", new FileInfo(cut).Length, () => File.OpenRead(cut))]);
        var output = Path.Combine(_dir, "interrupted.mkv");
        File.WriteAllBytes(output, []);
        var limits = ExportLimits.For(TimeSpan.FromSeconds(20), bytes.Length * 2, _dir)!;
        var result = await FfmpegExporter.ExportAsync(lease.Urls, TimeSpan.Zero, TimeSpan.FromSeconds(20), ExportContainer.Mkv,
            output, limits, ffmpegPath: ffmpeg);
        Assert.True(result.Bytes > bytes.Length, $"{result.Bytes} bytes");
    }

    /// <summary>A stream that reads normally up to <c>failAfter</c> bytes, then fails like a card read error.</summary>
    private sealed class FailingStream(Stream inner, long failAfter) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (inner.Position >= failAfter)
            {
                throw new IOException("The card could not be read.");
            }

            return inner.Read(buffer, offset, (int)Math.Min(count, failAfter - inner.Position));
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>The FFmpeg the app ships with, when a local build has it (it isn't in the repository).</summary>
    private static string? BundledFfmpeg()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AxisSdReader.App", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
