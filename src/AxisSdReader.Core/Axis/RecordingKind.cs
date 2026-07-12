namespace AxisSdReader.Core.Axis;

/// <summary>
/// How a recording was triggered, for timeline colour-coding. Mirrors Axis's own convention:
/// continuous = blue, event/motion = red, manual = yellow. Derived from the recording.xml trigger
/// metadata (see <see cref="RecordingTypeClassifier"/>).
/// </summary>
public enum RecordingKind
{
    /// <summary>Continuous (gap-free) recording. Blue.</summary>
    Continuous,

    /// <summary>Scheduled recording (time-window rule, not event-driven).</summary>
    Scheduled,

    /// <summary>Event-triggered: motion detection, object/AXIS analytics, alarm, I/O, etc. Red.</summary>
    Event,

    /// <summary>Manually started recording. Yellow.</summary>
    Manual,

    /// <summary>Trigger present but not recognised (or absent). Neutral.</summary>
    Other,
}

/// <summary>
/// Classifies a recording into a <see cref="RecordingKind"/> from the trigger fields the camera writes
/// into <c>recording.xml</c> (<c>&lt;CustomAttributes&gt;&lt;TriggerType&gt;/&lt;TriggerName&gt;</c>).
/// Matching is deliberately tolerant/keyword-based: continuous is reliably <c>"continuous"</c>, but
/// event and manual strings vary by firmware and by the (user-named) action rule, so we match on
/// substrings of both fields. Unknown non-empty triggers fall back to <see cref="RecordingKind.Other"/>.
/// </summary>
public static class RecordingTypeClassifier
{
    // Substrings that mark an event-triggered recording (motion/analytics/alarm/etc.).
    private static readonly string[] EventHints =
    [
        "motion", "vmd", "analytic", "object", "event", "detect", "alarm", "tamper",
        "intrusion", "fence", "cross", "loiter", "trigger", "input", "activity", "rule",
    ];

    /// <summary>Classifies from the recording.xml trigger type/name. Case-insensitive; either may be null.</summary>
    public static RecordingKind Classify(string? triggerType, string? triggerName)
    {
        var text = ((triggerType ?? string.Empty) + " " + (triggerName ?? string.Empty)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return RecordingKind.Other;
        }

        // Order matters: a continuous recording's rule may still contain other words, so check the
        // specific, unambiguous kinds first.
        if (text.Contains("continuous"))
        {
            return RecordingKind.Continuous;
        }

        if (text.Contains("manual"))
        {
            return RecordingKind.Manual;
        }

        if (text.Contains("schedul"))
        {
            return RecordingKind.Scheduled;
        }

        foreach (var hint in EventHints)
        {
            if (text.Contains(hint))
            {
                return RecordingKind.Event;
            }
        }

        return RecordingKind.Other;
    }

    /// <summary>
    /// Overlay precedence when recordings of different kinds cover the same instant (ACS Edge records
    /// continuous + motion in parallel): higher wins and is painted on top — manual &gt; event &gt;
    /// scheduled &gt; continuous &gt; other — matching Axis's yellow-over-red-over-blue convention.
    /// </summary>
    public static int OverlayPriority(RecordingKind kind) => kind switch
    {
        RecordingKind.Manual => 4,
        RecordingKind.Event => 3,
        RecordingKind.Scheduled => 2,
        RecordingKind.Continuous => 1,
        _ => 0,
    };
}
