using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Axis.Matroska;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Tests.Fixtures;

namespace AxisSdReader.Core.Tests.Axis;

public class AxisCardTests : IClassFixture<CardImageFixture>
{
    private readonly CardImageFixture _fixture;

    public AxisCardTests(CardImageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void IndexesRecordingsAndCameras()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var card = AxisCardIndexer.Index(reader.FileSystem!);

        Assert.Equal(3, card.Recordings.Count);
        Assert.True(card.HasIndexDatabase);

        // Two cameras, distinguished by serial embedded in the recording IDs.
        Assert.Equal(2, card.Cameras.Count);
        var cam1 = Assert.Single(card.Cameras, c => c.Serial == "ACCC8E123456");
        Assert.Equal(2, cam1.Recordings.Count);
        Assert.Equal(new DateTime(2025, 1, 14, 9, 30, 0), cam1.EarliestRecording);
        Assert.Equal(new DateTime(2025, 1, 14, 10, 15, 0), cam1.LatestRecording);

        // Recordings ordered by start time; chunks in numeric order.
        var first = card.Recordings[0];
        Assert.Equal("20250114_093000_1A2B_ACCC8E123456", first.Id.Raw);
        Assert.Equal(["0.mkv", "1.mkv", "2.mkv"], first.Chunks.Select(c => c.FileName).ToArray());
        Assert.All(first.Chunks, c => Assert.True(c.SizeBytes > 0));

        // The 5 GiB scratch file is surfaced as unrecognized, not silently dropped.
        Assert.Contains("bigfile.bin", card.UnrecognizedRootEntries);
    }

    [Fact]
    public void ReadsMkvMetadataFromNormalChunk()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var card = AxisCardIndexer.Index(reader.FileSystem!);

        var recording = card.Recordings.Single(r => r.Id.Raw == "20250114_093000_1A2B_ACCC8E123456");
        recording.LoadChunkMetadata(reader.FileSystem!);

        var meta = recording.Chunks[0].Metadata;
        Assert.NotNull(meta);
        Assert.Equal("V_MPEG4/ISO/AVC", meta!.VideoCodecId);
        Assert.True(meta.IsH264);
        Assert.Equal(320, meta.PixelWidth);
        Assert.Equal(240, meta.PixelHeight);
        Assert.Equal(new DateTime(2025, 1, 14, 9, 30, 0, DateTimeKind.Utc), meta.DateUtc);
        Assert.NotNull(meta.Duration);
        Assert.InRange(meta.Duration!.Value.TotalSeconds, 1.5, 2.5);
        Assert.False(meta.IsTruncated);

        // Session duration aggregates chunk durations.
        Assert.NotNull(recording.Duration);
        Assert.InRange(recording.Duration!.Value.TotalSeconds, 4.5, 7.5);
        Assert.Equal("V_MPEG4/ISO/AVC", recording.VideoCodecId);
    }

    [Fact]
    public void DerivesDurationForLiveMuxedChunkByScanningClusters()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var fs = reader.FileSystem!;

        // 1.mkv was piped out of the muxer: unknown segment size, no Duration element.
        using var stream = fs.OpenFile(@"\20250114_101500_77F0_ACCC8E123456\1.mkv", FileMode.Open, FileAccess.Read);
        var meta = MkvMetadataReader.Read(stream);

        Assert.NotNull(meta);
        Assert.Equal("V_MPEG4/ISO/AVC", meta!.VideoCodecId);
        Assert.NotNull(meta.Duration);
        Assert.InRange(meta.Duration!.Value.TotalSeconds, 1.0, 2.5);
    }

    [Fact]
    public void ToleratesTruncatedChunk()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var fs = reader.FileSystem!;

        // 2.mkv is the first 60% of a live-muxed file (simulated power loss).
        using var stream = fs.OpenFile(@"\20250114_101500_77F0_ACCC8E123456\2.mkv", FileMode.Open, FileAccess.Read);
        var meta = MkvMetadataReader.Read(stream);

        // Headers live at the front, so identity survives truncation.
        Assert.NotNull(meta);
        Assert.Equal("V_MPEG4/ISO/AVC", meta!.VideoCodecId);
        Assert.True(meta.IsTruncated);
    }

    [Fact]
    public void ReturnsNullForNonMkvContent()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var fs = reader.FileSystem!;

        using var stream = fs.OpenFile(@"\20250302_180000_0C3D_B8A44F998877\0.mkv", FileMode.Open, FileAccess.Read);
        Assert.Null(MkvMetadataReader.Read(stream));
    }
}
