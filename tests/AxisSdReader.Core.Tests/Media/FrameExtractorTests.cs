using System.Diagnostics;
using System.Globalization;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Export;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Media;
using AxisSdReader.Core.Tests.Fixtures;

namespace AxisSdReader.Core.Tests.Media;

[Collection("CardImage")]
public sealed class FrameExtractorTests(CardImageFixture fixture)
{
    private static string Ffmpeg()
    {
        var ffmpeg = FfmpegLocator.Find();
        Skip.If(ffmpeg is null, "ffmpeg not found");
        return ffmpeg!;
    }

    [SkippableFact]
    public async Task ContactSheetKeepsEveryCellWhenFormatsDiffer()
    {
        // Regression: a placeholder cell (4:2:0) among 4:4:4 video cells made FFmpeg restart the tile filter,
        // dropping every cell before it. Here the placeholder comes first and the last cell must still be video.
        var ffmpeg = Ffmpeg();
        using var reader = CardReader.OpenImage(fixture.ImagePath);
        var fs = reader.FileSystem!;
        var recording = AxisCardIndexer.Index(reader).Recordings.Single(r => r.Id.Raw == "20250114_093000_1A2B_ACCC8E123456");
        using var server = LocalMediaServer.Start();
        using var lease = server.CreateLease(FfmpegExporter.ChunkSources(recording.Chunks, new CardIoGate(), fs));

        var cells = new List<SheetCell> { new(null, TimeSpan.Zero, "1 gap") };
        cells.AddRange(Enumerable.Range(0, 5).Select(i => new SheetCell(lease.Urls[i % 3], TimeSpan.FromSeconds(0.5), $"{i + 2} video")));
        var sheet = await FrameExtractor.ContactSheetAsync(ffmpeg, cells, columns: 3, cellWidth: 192);

        // 3 x 2 grid of 192x108 cells, 4 px margin and padding: the last cell's centre must hold the test pattern,
        // not the empty background.
        var path = Path.Combine(Path.GetTempPath(), $"axis-sheet-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, sheet);
        try
        {
            var lastCell = MeanLuma(ffmpeg, path, x: 4 + 2 * (192 + 4) + 60, y: 4 + 108 + 4 + 20, w: 72, h: 40);
            var background = MeanLuma(ffmpeg, path, x: 0, y: 0, w: 4, h: 4);
            Assert.True(lastCell > background + 40, $"last cell luma {lastCell} vs background {background}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public async Task FrameNearAChunkEndFallsBackToTheFrameBefore()
    {
        var ffmpeg = Ffmpeg();
        using var reader = CardReader.OpenImage(fixture.ImagePath);
        var fs = reader.FileSystem!;
        var recording = AxisCardIndexer.Index(reader).Recordings.Single(r => r.Id.Raw == "20250114_093000_1A2B_ACCC8E123456");
        using var server = LocalMediaServer.Start();
        using var lease = server.CreateLease(FfmpegExporter.ChunkSources(recording.Chunks, new CardIoGate(), fs));

        // The chunk is 2 s at 10 fps: 1.95 s is past its last frame.
        var jpeg = await FrameExtractor.ExtractFrameAsync(ffmpeg, lease.Urls[1], TimeSpan.FromSeconds(1.95), 320, FrameFormat.Jpeg, "x");
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
    }

    private static double MeanLuma(string ffmpeg, string image, int x, int y, int w, int h)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-i", image, "-vf", $"crop={w}:{h}:{x}:{y},signalstats,metadata=print", "-f", "null", "-" })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var log = p.StandardError.ReadToEnd();
        p.WaitForExit();
        var line = log.Split('\n').First(l => l.Contains("lavfi.signalstats.YAVG="));
        return double.Parse(line[(line.IndexOf("YAVG=", StringComparison.Ordinal) + 5)..].Trim(), CultureInfo.InvariantCulture);
    }
}
