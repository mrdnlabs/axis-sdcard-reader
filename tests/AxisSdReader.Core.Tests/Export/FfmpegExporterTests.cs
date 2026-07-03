using System.Diagnostics;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Export;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Tests.Fixtures;

namespace AxisSdReader.Core.Tests.Export;

[Collection("CardImage")]
public class FfmpegExporterTests
{
    private readonly CardImageFixture _fixture;

    public FfmpegExporterTests(CardImageFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task ExportsTrimmedMp4FromCardChunks()
    {
        Skip.IfNot(FfmpegExporter.IsAvailable, "ffmpeg.exe not found on PATH.");

        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var fs = reader.FileSystem!;
        var card = AxisCardIndexer.Index(fs);

        // The H.264 recording with three ~2s chunks (total ~6s).
        var recording = card.Recordings.Single(r => r.Id.Raw == "20250114_093000_1A2B_ACCC8E123456");
        recording.LoadChunkMetadata(fs);

        var output = Path.Combine(Path.GetTempPath(), $"axis-mp4-{Guid.NewGuid():N}.mp4");
        try
        {
            // Trim to roughly the middle 3 seconds across all chunks.
            var result = await FfmpegExporter.ExportAsync(
                fs, recording.Chunks, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4),
                ExportContainer.Mp4, output);

            Assert.True(File.Exists(output));
            Assert.True(result.Bytes > 0);

            var (container, codec, durationSeconds) = Probe(output);
            Assert.Contains("mp4", container); // ffprobe reports "mov,mp4,m4a,3gp,3g2,mj2"
            Assert.Equal("h264", codec);
            Assert.InRange(durationSeconds, 1.0, 4.5); // keyframe-copy trim is approximate
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [SkippableFact]
    public async Task ExportsTrimmedMkvPreservingCodec()
    {
        Skip.IfNot(FfmpegExporter.IsAvailable, "ffmpeg.exe not found on PATH.");

        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var fs = reader.FileSystem!;
        var card = AxisCardIndexer.Index(fs);

        // The AV1 recording — MKV remux must keep the av1 stream.
        var recording = card.Recordings.Single(r => r.Id.Raw == "20250114_101500_77F0_ACCC8E123456");
        recording.LoadChunkMetadata(fs);

        var output = Path.Combine(Path.GetTempPath(), $"axis-mkv-{Guid.NewGuid():N}.mkv");
        try
        {
            var result = await FfmpegExporter.ExportAsync(
                fs, recording.Chunks, TimeSpan.Zero, TimeSpan.FromSeconds(2),
                ExportContainer.Mkv, output);

            Assert.True(result.Bytes > 0);
            var (container, codec, _) = Probe(output);
            Assert.Contains("matroska", container);
            Assert.Equal("av1", codec);
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    private static (string Container, string Codec, double DurationSeconds) Probe(string path)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(FfmpegExporter.FindFfmpeg()!)!, "ffprobe.exe");
        Skip.IfNot(File.Exists(ffprobe), "ffprobe.exe not found next to ffmpeg.");

        var psi = new ProcessStartInfo(ffprobe,
            $"-v error -show_entries format=format_name,duration:stream=codec_name -of default=noprint_wrappers=1 \"{path}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        string container = "", codec = "";
        double duration = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            switch (parts[0].Trim())
            {
                case "format_name":
                    container = parts[1].Trim();
                    break;
                case "codec_name" when codec.Length == 0:
                    codec = parts[1].Trim();
                    break;
                case "duration":
                    double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out duration);
                    break;
            }
        }

        return (container, codec, duration);
    }
}
