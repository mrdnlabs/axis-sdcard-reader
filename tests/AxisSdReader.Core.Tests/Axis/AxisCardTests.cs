using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Axis.Matroska;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Tests.Fixtures;

namespace AxisSdReader.Core.Tests.Axis;

[Collection("CardImage")]
public class AxisCardTests
{
    private readonly CardImageFixture _fixture;

    public AxisCardTests(CardImageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void IndexesNestedAndFlatLayouts()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var card = AxisCardIndexer.Index(reader.FileSystem!);

        Assert.Equal(3, card.Recordings.Count);
        Assert.True(card.HasIndexDatabase);

        Assert.Equal(2, card.Cameras.Count);
        var cam1 = Assert.Single(card.Cameras, c => c.Serial == "ACCC8E123456");
        Assert.Equal(2, cam1.Recordings.Count);

        // Nested-layout recording discovered under \YYYYMMDD\HH\.
        var nested = card.Recordings.Single(r => r.Id.Raw == "20250114_093000_1A2B_ACCC8E123456");
        Assert.Equal(
            ["20250114_093000_5B35.mkv", "20250114_093002_9D2B.mkv", "20250114_093004_84BA.mkv"],
            nested.Chunks.Select(c => c.FileName).ToArray());
        Assert.All(nested.Chunks, c => Assert.NotNull(c.SidecarPath));

        // recording.xml parsed at index time: true UTC start, trigger, encoding.
        Assert.NotNull(nested.Info);
        Assert.Equal(new DateTime(2025, 1, 14, 14, 30, 0, DateTimeKind.Utc), nested.StartTime);
        Assert.Equal("continuous", nested.Trigger);
        Assert.Equal("V_MPEG4/ISO/AVC", nested.VideoCodecId);

        // Legacy flat-layout recording still found at the root.
        var flat = card.Recordings.Single(r => r.Id.Raw == "20250302_180000_0C3D_B8A44F998877");
        Assert.Equal(["0.mkv", "1.mkv"], flat.Chunks.Select(c => c.FileName).ToArray());
        Assert.Null(flat.Info);

        // ACAP/system dirs are surfaced as unrecognized, not scanned as recordings.
        Assert.Contains("areas", card.UnrecognizedRootEntries);
        Assert.Contains("ws", card.UnrecognizedRootEntries);
        Assert.Contains("bigfile.bin", card.UnrecognizedRootEntries);
        Assert.DoesNotContain("20250114", card.UnrecognizedRootEntries);
    }

    [Fact]
    public void SidecarsProvideTimingWithoutMkvReads()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var card = AxisCardIndexer.Index(reader.FileSystem!);

        var nested = card.Recordings.Single(r => r.Id.Raw == "20250114_093000_1A2B_ACCC8E123456");
        nested.LoadChunkMetadata(reader.FileSystem!);

        foreach (var chunk in nested.Chunks)
        {
            Assert.NotNull(chunk.Block);
            Assert.True(chunk.Block!.IsComplete);
            Assert.Equal(TimeSpan.FromSeconds(2), chunk.Duration);
            Assert.Null(chunk.Metadata); // sidecar sufficed; MKV headers untouched
        }

        Assert.Equal(TimeSpan.FromSeconds(6), nested.Duration);
        Assert.False(nested.WasInterrupted);
        Assert.Equal(new DateTime(2025, 1, 14, 14, 30, 0, DateTimeKind.Utc), nested.Chunks[0].StartTimeUtc);
    }

    [Fact]
    public void InterruptedAv1RecordingFallsBackToMkvScan()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var card = AxisCardIndexer.Index(reader.FileSystem!);

        var av1 = card.Recordings.Single(r => r.Id.Raw == "20250114_101500_77F0_ACCC8E123456");
        Assert.Equal("V_AV1", av1.VideoCodecId);

        av1.LoadChunkMetadata(reader.FileSystem!);

        // First chunk: complete, sidecar-only.
        Assert.True(av1.Chunks[0].Block!.IsComplete);
        Assert.Null(av1.Chunks[0].Metadata);

        // Last chunk: sidecar says Status=Recording (no StopTime) so the MKV was scanned.
        var last = av1.Chunks[^1];
        Assert.False(last.Block!.IsComplete);
        Assert.NotNull(last.Metadata);
        Assert.NotNull(last.Duration); // derived from cluster/block timestamps
        Assert.True(av1.WasInterrupted);
    }

    [Fact]
    public void LegacyRecordingLoadsMetadataFromMkvHeaders()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var card = AxisCardIndexer.Index(reader.FileSystem!);

        var flat = card.Recordings.Single(r => r.Id.Raw == "20250302_180000_0C3D_B8A44F998877");
        flat.LoadChunkMetadata(reader.FileSystem!);

        var meta = flat.Chunks[0].Metadata;
        Assert.NotNull(meta);
        Assert.Equal("V_MPEG4/ISO/AVC", meta!.VideoCodecId);
        Assert.Equal(320, meta.PixelWidth);
        Assert.Equal(new DateTime(2025, 3, 2, 18, 0, 0, DateTimeKind.Utc), meta.DateUtc);
        Assert.InRange(flat.Duration!.Value.TotalSeconds, 3.0, 5.0);
    }

    [Fact]
    public void ZeroDurationHeaderTriggersClusterScan()
    {
        using var reader = CardReader.OpenImage(_fixture.ImagePath);
        var fs = reader.FileSystem!;

        // The truncated live-muxed chunk: header Duration is absent or a 0 placeholder.
        var path = @"\20250114\10\20250114_101500_77F0_ACCC8E123456\20250114_10\20250114_101502_0002.mkv";
        using var stream = fs.OpenFile(path, FileMode.Open, FileAccess.Read);
        var meta = MkvMetadataReader.Read(stream);

        Assert.NotNull(meta);
        Assert.True(meta!.IsTruncated);
        Assert.NotNull(meta.Duration);
        Assert.True(meta.Duration!.Value > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(new byte[0])]                                  // empty
    [InlineData(new byte[] { 0x1A })]                          // one byte of an EBML id, then EOF
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF })]             // partial EBML id
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })]       // EBML id, no size/body
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x84 })] // id + size claiming bytes that aren't there
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })]       // garbage
    public void NeverThrowsOnTruncatedOrGarbageInput(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        // Must degrade to null, not throw — an uncaught EndOfStreamException here crashed the
        // app while scrubbing into a recording with an odd chunk.
        Assert.Null(MkvMetadataReader.Read(stream));
    }
}
