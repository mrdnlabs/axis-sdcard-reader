using System.Security.Cryptography;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Export;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Tests.Fixtures;

namespace AxisSdReader.Core.Tests.Export;

public class RecordingExporterTests : IClassFixture<CardImageFixture>
{
    private readonly CardImageFixture _fixture;

    public RecordingExporterTests(CardImageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ExportsAllChunksWithMatchingContent()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var fs = reader.FileSystem!;
        var card = AxisCardIndexer.Index(fs);
        var recording = card.Recordings.Single(r => r.Id.Raw == "20250114_093000_1A2B_ACCC8E123456");

        var targetRoot = Path.Combine(Path.GetTempPath(), $"axis-export-{Guid.NewGuid():N}");
        try
        {
            var progressReports = new List<ExportProgress>();
            var result = RecordingExporter.Export(fs, recording, targetRoot,
                new SynchronousProgress(progressReports));

            Assert.Equal(3, result.FilesExported);
            Assert.Equal(recording.TotalSizeBytes, result.BytesExported);
            Assert.True(progressReports.Count > 0);
            Assert.Equal(3, progressReports[^1].FilesDone);

            foreach (var chunk in recording.Chunks)
            {
                var exported = Path.Combine(result.TargetDirectory, chunk.FileName);
                Assert.True(File.Exists(exported));
                Assert.Equal(chunk.SizeBytes, new FileInfo(exported).Length);

                using var source = fs.OpenFile(chunk.Path, FileMode.Open, FileAccess.Read);
                using var target = File.OpenRead(exported);
                Assert.Equal(SHA256.HashData(source), SHA256.HashData(target));
            }
        }
        finally
        {
            if (Directory.Exists(targetRoot))
            {
                Directory.Delete(targetRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CancellationRemovesPartialFile()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var fs = reader.FileSystem!;
        var card = AxisCardIndexer.Index(fs);
        var recording = card.Recordings[0];

        var targetRoot = Path.Combine(Path.GetTempPath(), $"axis-export-{Guid.NewGuid():N}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                RecordingExporter.Export(fs, recording, targetRoot, progress: null, cts.Token));

            var destination = Path.Combine(targetRoot, recording.Id.Raw);
            Assert.Empty(Directory.GetFiles(destination));
        }
        finally
        {
            if (Directory.Exists(targetRoot))
            {
                Directory.Delete(targetRoot, recursive: true);
            }
        }
    }

    /// <summary>IProgress that records synchronously (Progress&lt;T&gt; posts to a sync context, racing the test).</summary>
    private sealed class SynchronousProgress(List<ExportProgress> reports) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => reports.Add(value);
    }
}
