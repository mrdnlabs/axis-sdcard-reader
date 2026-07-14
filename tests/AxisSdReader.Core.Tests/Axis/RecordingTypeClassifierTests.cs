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

    // The real trigger metadata from an AXIS Camera Station Edge card (verified against hardware
    // 2026-07-14). TriggerType is "triggered" for BOTH kinds, so only the action-rule fields tell them
    // apart — this is the regression that matters most.
    [Theory]
    [InlineData("triggered", "ACC_Continuous_E827251FFB8D_0", "ACC_ContinuousAction", RecordingKind.Continuous)]
    [InlineData("triggered", "ACC_Motion_E827251FFB8D_0", "ACC_MotionAction", RecordingKind.Event)]
    public void ClassifiesRealAcsEdgeTriggers(string type, string name, string trigger, RecordingKind expected)
    {
        Assert.Equal(expected, RecordingTypeClassifier.Classify(type, name, trigger));
    }

    [Fact]
    public void TriggeredAloneIsNotTreatedAsAnEvent()
    {
        // ACS Edge stamps TriggerType="triggered" on every recording, continuous included, so the word must
        // carry no weight on its own — otherwise unrecognised continuous footage would paint red.
        Assert.Equal(RecordingKind.Other, RecordingTypeClassifier.Classify("triggered", null));
    }

    [Fact]
    public void ClassifiesRealVapixContinuousTrigger()
    {
        // A VAPIX-configured card (the other real card we have) states the type directly.
        Assert.Equal(RecordingKind.Continuous,
            RecordingTypeClassifier.Classify("continuous", "continuous", "continuous"));
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
