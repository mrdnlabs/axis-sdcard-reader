using AxisSdReader.Core.Axis;
using Xunit;

namespace AxisSdReader.Core.Tests.Axis;

public class RecordingTypeClassifierTests
{
    [Theory]
    [InlineData("continuous", "continuous", RecordingKind.Continuous)]
    [InlineData("continuous", null, RecordingKind.Continuous)]
    [InlineData(null, "manual", RecordingKind.Manual)]
    [InlineData("manual", "Manual recording", RecordingKind.Manual)]
    [InlineData("scheduled", null, RecordingKind.Scheduled)]
    [InlineData("motion", null, RecordingKind.Event)]
    [InlineData(null, "Motion detection", RecordingKind.Event)]
    [InlineData("com.axis.analytics.vmd", "VMD4", RecordingKind.Event)]
    [InlineData("object", "AXIS Object Analytics", RecordingKind.Event)]
    [InlineData(null, null, RecordingKind.Other)]
    [InlineData("", "  ", RecordingKind.Other)]
    [InlineData("foobar", "xyzzy", RecordingKind.Other)]
    public void ClassifiesTriggerMetadata(string? type, string? name, RecordingKind expected)
    {
        Assert.Equal(expected, RecordingTypeClassifier.Classify(type, name));
    }

    [Fact]
    public void ClassificationIsCaseInsensitive()
    {
        Assert.Equal(RecordingKind.Continuous, RecordingTypeClassifier.Classify("CONTINUOUS", null));
        Assert.Equal(RecordingKind.Event, RecordingTypeClassifier.Classify(null, "MOTION"));
    }

    [Fact]
    public void OverlayPriorityMatchesYellowOverRedOverBlue()
    {
        // manual (yellow) > event (red) > scheduled > continuous (blue) > other
        Assert.True(RecordingTypeClassifier.OverlayPriority(RecordingKind.Manual)
            > RecordingTypeClassifier.OverlayPriority(RecordingKind.Event));
        Assert.True(RecordingTypeClassifier.OverlayPriority(RecordingKind.Event)
            > RecordingTypeClassifier.OverlayPriority(RecordingKind.Scheduled));
        Assert.True(RecordingTypeClassifier.OverlayPriority(RecordingKind.Scheduled)
            > RecordingTypeClassifier.OverlayPriority(RecordingKind.Continuous));
        Assert.True(RecordingTypeClassifier.OverlayPriority(RecordingKind.Continuous)
            > RecordingTypeClassifier.OverlayPriority(RecordingKind.Other));
    }
}
