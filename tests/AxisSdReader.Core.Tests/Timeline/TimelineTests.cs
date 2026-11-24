using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Timeline;

namespace AxisSdReader.Core.Tests.Timeline;

public class TimeAxisTests
{
    [Fact]
    public void RoundTripsUtc()
    {
        var t = new DateTime(2025, 1, 14, 14, 30, 0, DateTimeKind.Utc);
        Assert.Equal(t, TimeAxis.ToUtc(TimeAxis.ToSeconds(t)));
    }

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(1e15)]
    [InlineData(-1e15)]
    public void ToDateTimeSaturatesInsteadOfThrowing(double seconds)
    {
        // Crafted cards can carry timestamps near year 1 or 9999; these conversions run inside WPF rendering,
        // where an exception repeats on every frame.
        var local = TimeAxis.ToDateTime(seconds);

        // ...and there's room left for the timeline's "+1 day" arithmetic.
        _ = local.AddDays(1);
        _ = local.AddDays(-1);
        Assert.NotNull(TimeAxis.ClockText(seconds));
        Assert.NotNull(TimeAxis.IsoLocal(seconds));
    }

    [Fact]
    public void FarFutureFolderTimeStillConverts()
    {
        var far = TimeAxis.ToSeconds(new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc));
        Assert.True(TimeAxis.ToUtc(far) <= DateTime.MaxValue.AddDays(-1));
    }

    [Theory]
    [InlineData(1e300)]
    [InlineData(-1e300)]
    [InlineData(double.NaN)]
    [InlineData(5.0)]
    public void SafeTimeNeverThrows(double seconds)
    {
        var span = SafeTime.FromSeconds(seconds);
        if (seconds == 5.0)
        {
            Assert.Equal(TimeSpan.FromSeconds(5), span);
        }
    }

    [Fact]
    public void SafeEndSaturates() =>
        Assert.Equal(DateTime.MaxValue, TimeSegment.SafeEnd(new DateTime(9999, 1, 1), TimeSpan.FromDays(1000)));
}

public class TimeSegmentTests
{
    private static readonly DateTime Start = new(2025, 1, 14, 14, 30, 0, DateTimeKind.Utc);

    private static Recording Make(string id, IEnumerable<RecordingChunk> chunks, DateTime? xmlStart = null, string trigger = "continuous") =>
        new(RecordingId.TryParse(id)!, @"\" + id, chunks.ToList(),
            new RecordingInfoXml(id, xmlStart ?? Start, "1", "video/x-h264", 320, 240, 10, trigger, trigger, trigger));

    private static RecordingChunk Chunk(string name, DateTime? utcStart = null, double seconds = 300) =>
        new(@"\r\" + name, name, 1000)
        {
            Block = utcStart is { } s ? new RecordingBlockXml(name, s, s.AddSeconds(seconds), "Complete") : null,
        };

    [Fact]
    public void EstimatesFromChunkNamesBeforeMetadata()
    {
        var rec = Make("20250114_093000_1A2B_ACCC8E123456",
            [Chunk("20250114_093000_aaaa.mkv"), Chunk("20250114_093500_bbbb.mkv"), Chunk("20250114_094000_cccc.mkv")]);
        var segment = new TimeSegment(rec);

        Assert.False(segment.IsRefined);
        Assert.Equal(TimeAxis.ToSeconds(Start), segment.StartSeconds);
        Assert.Equal(new double[] { 0, 300, 600 }, segment.ChunkOffsets);
        Assert.Equal(900, segment.DurationSeconds); // last chunk estimated from the median spacing
    }

    [Fact]
    public void UsesTrueUtcStartsOnceKnown()
    {
        // Chunk names are camera-LOCAL: across a fall-back DST change the second hour's names repeat the
        // first's. The UTC sidecar starts are the truth.
        var rec = Make("20250114_093000_1A2B_ACCC8E123456",
        [
            Chunk("20250114_093000_aaaa.mkv", Start, 300),
            Chunk("20250114_093500_bbbb.mkv", Start.AddSeconds(300), 300),
            Chunk("20250114_093000_cccc.mkv", Start.AddSeconds(3600), 300), // same local name an hour later
        ]);
        var segment = new TimeSegment(rec);

        Assert.True(segment.IsRefined);
        Assert.Equal(new double[] { 0, 300, 3600 }, segment.ChunkOffsets);
        Assert.Equal(3900, segment.DurationSeconds);
        Assert.Equal((2, TimeSpan.FromSeconds(100)), segment.Locate(TimeAxis.ToSeconds(Start) + 3700));
    }

    [Fact]
    public void SegmentStartsWhereFootageStartsWhenHeadWasPruned()
    {
        // The camera deleted the oldest blocks of a long recording: the first remaining chunk starts an hour in.
        var rec = Make("20250114_093000_1A2B_ACCC8E123456", [Chunk("20250114_103000_aaaa.mkv", Start.AddHours(1), 300)]);
        var segment = new TimeSegment(rec);

        Assert.Equal(TimeAxis.ToSeconds(Start.AddHours(1)), segment.StartSeconds);
        Assert.Equal(300, segment.DurationSeconds);
    }

    [Fact]
    public void CraftedDurationsStayBounded()
    {
        var rec = Make("20250114_093000_1A2B_ACCC8E123456", [Chunk("0.mkv"), Chunk("1.mkv")]);
        var chunks = rec.Chunks.Select(c => c with { Block = new RecordingBlockXml("x", DateTime.MinValue, DateTime.MaxValue, "Complete") });
        var crafted = Make("20250114_093000_1A2B_ACCC8E123456", chunks);
        var segment = new TimeSegment(crafted);

        Assert.True(double.IsFinite(segment.DurationSeconds));
        Assert.True(segment.DurationSeconds <= 366.0 * 200 * 86400);
        _ = SafeTime.FromSeconds(segment.DurationSeconds);
    }

    [Fact]
    public void SliceForTrimsRelativeToFirstChunk()
    {
        var rec = Make("20250114_093000_1A2B_ACCC8E123456",
        [
            Chunk("20250114_093000_aaaa.mkv", Start, 300),
            Chunk("20250114_093500_bbbb.mkv", Start.AddSeconds(300), 300),
            Chunk("20250114_094000_cccc.mkv", Start.AddSeconds(600), 300),
        ]);
        var segment = new TimeSegment(rec);
        var s0 = TimeAxis.ToSeconds(Start);

        var slice = segment.SliceFor(s0 + 400, s0 + 700)!;
        Assert.Equal(new[] { "20250114_093500_bbbb.mkv", "20250114_094000_cccc.mkv" }, slice.Chunks.Select(c => c.FileName));
        Assert.Equal(TimeSpan.FromSeconds(100), slice.TrimStart);
        Assert.Equal(TimeSpan.FromSeconds(400), slice.TrimEnd);
        Assert.Null(segment.SliceFor(s0 - 100, s0 - 10));
    }
}

public class CardCatalogTests
{
    private static readonly DateTime Start = new(2025, 1, 14, 14, 30, 0, DateTimeKind.Utc);

    private static TimeSegment Segment(string id, double offsetSeconds, double seconds, string trigger)
    {
        var start = Start.AddSeconds(offsetSeconds);
        var stamp = start.ToString("yyyyMMdd_HHmmss");
        var chunk = new RecordingChunk(@"\r", stamp + "_0000.mkv", 100)
        {
            Block = new RecordingBlockXml("b", start, start.AddSeconds(seconds), "Complete"),
        };
        var rec = new Recording(RecordingId.TryParse(id)!, @"\" + id, [chunk],
            new RecordingInfoXml(id, start, "1", null, null, null, null, trigger, trigger, trigger));
        return new TimeSegment(rec);
    }

    [Fact]
    public void LocatePrefersTheTopBand()
    {
        var continuous = Segment("20250114_093000_0001_ACCC8E123456", 0, 600, "continuous");
        var motion = Segment("20250114_093200_0002_ACCC8E123456", 120, 30, "motion");
        var s0 = TimeAxis.ToSeconds(Start);

        Assert.Same(motion, CardCatalog.Locate([continuous, motion], s0 + 130)!.Value.Segment);
        Assert.Same(continuous, CardCatalog.Locate([continuous, motion], s0 + 200)!.Value.Segment);
        Assert.Null(CardCatalog.Locate([continuous, motion], s0 + 700));
    }

    [Fact]
    public void CoverageMergesPerKindAndFindsGaps()
    {
        var a = Segment("20250114_093000_0001_ACCC8E123456", 0, 100, "continuous");
        var b = Segment("20250114_093140_0002_ACCC8E123456", 100, 50, "continuous"); // touches a
        var m = Segment("20250114_093500_0003_ACCC8E123456", 300, 20, "motion");
        var s0 = TimeAxis.ToSeconds(Start);

        var spans = CardCatalog.Coverage([a, b, m], s0, s0 + 400);
        Assert.Equal(2, spans.Count);
        Assert.Equal((RecordingKind.Continuous, 150.0), (spans[0].Kind, spans[0].Seconds));
        Assert.Equal((RecordingKind.Event, 20.0), (spans[1].Kind, spans[1].Seconds));

        var gaps = CardCatalog.Gaps([a, b, m], s0, s0 + 400);
        Assert.Equal(new List<(double, double)> { (s0 + 150, s0 + 300), (s0 + 320, s0 + 400) }, gaps);
    }

    [Fact]
    public void SamplesSkipGaps()
    {
        var a = Segment("20250114_093000_0001_ACCC8E123456", 0, 100, "continuous");
        var b = Segment("20250114_094000_0002_ACCC8E123456", 600, 100, "continuous");
        var s0 = TimeAxis.ToSeconds(Start);

        var samples = CardCatalog.SampleCovered([a, b], s0, s0 + 1000, 4);
        Assert.Equal(new List<double> { s0 + 25, s0 + 75, s0 + 625, s0 + 675 }, samples);
    }

    [Fact]
    public void FindsCamerasBySerialSuffixOrName()
    {
        var recs = new[] { "20250114_093000_0001_ACCC8E123456", "20250114_093000_0002_B8A44F998877" }
            .Select(id => new Recording(RecordingId.TryParse(id)!, @"\" + id, [], null)).ToList();
        var catalog = new CardCatalog(new AxisCard(recs, [], false));

        Assert.Equal("ACCC8E123456", catalog.FindCamera("accc8e123456", out _)!.Serial);
        Assert.Equal("ACCC8E123456", catalog.FindCamera("3456", out _)!.Serial);
        Assert.Equal("B8A44F998877", catalog.FindCamera("Camera 8877", out _)!.Serial);
        Assert.Equal("B8A44F998877", catalog.FindCamera("B8:A4:4F:99:88:77", out _)!.Serial);
        Assert.Null(catalog.FindCamera(null, out var error));
        Assert.Contains("2 cameras", error);
        Assert.Null(catalog.FindCamera("12", out _));
    }
}
