using System.Windows;
using System.Windows.Input;
using AxisSdReader.Core.Axis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxisSdReader.App.ViewModels;

/// <summary>A day of one camera's recordings, as grouped for the browse tree.</summary>
public sealed class DateNode
{
    public DateNode(DateTime date, IReadOnlyList<Recording> recordings)
    {
        Date = date;
        Recordings = recordings;
        Key = date.ToString("yyyyMMdd");
        ShortLabel = date.ToString("MMM d");
        LongLabel = date.ToString("ddd, MMM d, yyyy");
    }

    public DateTime Date { get; }
    public string Key { get; }
    public string ShortLabel { get; }
    public string LongLabel { get; }
    public IReadOnlyList<Recording> Recordings { get; }
    public int ClipCount => Recordings.Count;
}

/// <summary>One lens (recording source) of a camera. Multi-sensor Axis cameras record each
/// lens as a separate VAPIX source; single-sensor cameras have exactly one. Labelled by the
/// real VAPIX source token so gaps (un-recorded sensors) are visible.</summary>
public sealed class LensNode
{
    public LensNode(string sourceToken, IReadOnlyList<DateNode> dates)
    {
        SourceToken = sourceToken;
        Dates = dates;
        SourceNumber = int.TryParse(sourceToken, out var n) ? n : null;
        Resolution = DescribeResolution(dates.SelectMany(d => d.Recordings).FirstOrDefault());
    }

    /// <summary>VAPIX source token exactly as recorded (e.g. "1", "3", "5").</summary>
    public string SourceToken { get; }

    /// <summary>Numeric form of the source token, when it is a plain number.</summary>
    public int? SourceNumber { get; }

    public string Label => $"Source {SourceToken}";

    /// <summary>Friendly resolution ("4K" / "1080p" / "WxH"), for telling lenses apart.</summary>
    public string Resolution { get; }

    public IReadOnlyList<DateNode> Dates { get; }
    public IEnumerable<Recording> Recordings => Dates.SelectMany(d => d.Recordings);
    public int ClipCount => Dates.Sum(d => d.ClipCount);

    private static string DescribeResolution(Recording? recording)
    {
        if (recording?.Info is not { Width: { } w, Height: { } h })
        {
            return "";
        }

        return (w, h) switch
        {
            (3840, 2160) => "4K",
            (2560, 1440) => "1440p",
            (1920, 1080) => "1080p",
            (1280, 720) => "720p",
            _ => $"{w}×{h}",
        };
    }
}

/// <summary>One camera on the card: its lenses (recording sources), each with recordings
/// grouped by local date. The browse tree shows the active lens's dates.</summary>
public sealed class CameraNode
{
    public CameraNode(string serial, string name, string model, IReadOnlyList<LensNode> lenses)
    {
        Serial = serial;
        Name = name;
        Model = model;
        Lenses = lenses;
    }

    public string Serial { get; }
    public string Name { get; }
    public string Model { get; set; }
    public IReadOnlyList<LensNode> Lenses { get; }
    public int ActiveLensIndex { get; set; }
    public LensNode ActiveLens => Lenses[Math.Clamp(ActiveLensIndex, 0, Lenses.Count - 1)];
    public bool IsMultiLens => Lenses.Count > 1;
    public IReadOnlyList<DateNode> Dates => ActiveLens.Dates;
    public int TotalClips => Lenses.Sum(l => l.ClipCount);
}

/// <summary>A tab in the lens bar. Recorded sources are selectable; a source the camera
/// supports but that wasn't recorded appears as a disabled placeholder so the user can see
/// exactly which lenses have footage.</summary>
public sealed partial class LensTab : ObservableObject
{
    private LensTab(string badge, string name, LensNode? node, ICommand? command, bool isRecorded)
    {
        Badge = badge;
        Name = name;
        Node = node;
        Command = command;
        IsRecorded = isRecorded;
    }

    /// <summary>The number badge — the real VAPIX source token.</summary>
    public string Badge { get; }

    /// <summary>Secondary label: resolution for recorded lenses, "not recorded" for placeholders.</summary>
    public string Name { get; }

    public LensNode? Node { get; }
    public ICommand? Command { get; }
    public bool IsRecorded { get; }

    [ObservableProperty]
    private bool _isActive;

    public static LensTab Recorded(LensNode node, ICommand command) =>
        new(node.SourceToken, node.Resolution.Length > 0 ? node.Resolution : "recorded", node, command, isRecorded: true);

    public static LensTab Missing(int sourceNumber) =>
        new(sourceNumber.ToString(), "not recorded", null, null, isRecorded: false);
}

/// <summary>Base for the flattened browse-tree rows bound to the sidebar ItemsControl.</summary>
public abstract partial class BrowseRow : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Left indent for the tree depth (Camera 0, Lens 20, Date 20/40, Clip 40/60).</summary>
    public Thickness Indent { get; init; }
}

public sealed partial class CameraRow : BrowseRow
{
    public CameraRow(CameraNode node, ICommand command)
    {
        Node = node;
        Command = command;
    }

    public CameraNode Node { get; }
    public ICommand Command { get; }
    public string Name => Node.Name;
    public string Model => Node.Model;
    public string Badge => Node.TotalClips.ToString();
    public bool IsMultiLens => Node.IsMultiLens;
    public string LensTag => $"{Node.Lenses.Count} lenses";
}

/// <summary>A lens (recording source) level in the tree, shown only for multi-sensor cameras.</summary>
public sealed partial class LensRow : BrowseRow
{
    public LensRow(CameraNode camera, LensNode node, ICommand command)
    {
        Camera = camera;
        Node = node;
        Command = command;
    }

    public CameraNode Camera { get; }
    public LensNode Node { get; }
    public ICommand Command { get; }
    public string Label => Node.Label;                 // "Source 3"
    public string Sub => Node.Resolution;              // "4K"
    public string Badge => $"{Node.ClipCount} clip{(Node.ClipCount == 1 ? "" : "s")}";
}

public sealed partial class DateRow : BrowseRow
{
    public DateRow(CameraNode camera, LensNode lens, DateNode node, ICommand command)
    {
        Camera = camera;
        Lens = lens;
        Node = node;
        Command = command;
    }

    public CameraNode Camera { get; }
    public LensNode Lens { get; }
    public DateNode Node { get; }
    public ICommand Command { get; }
    public string Label => Node.LongLabel;
    public string ClipsLabel => $"{Node.ClipCount} clip{(Node.ClipCount == 1 ? "" : "s")}";
}

public sealed partial class ClipRow : BrowseRow
{
    public ClipRow(CameraNode camera, LensNode lens, DateNode date, Recording recording, ICommand command)
    {
        Camera = camera;
        Lens = lens;
        Date = date;
        Recording = recording;
        Command = command;
        (_timeRange, _duration) = ComputeLabels(recording);
    }

    public CameraNode Camera { get; }
    public LensNode Lens { get; }
    public DateNode Date { get; }
    public Recording Recording { get; }
    public ICommand Command { get; }

    [ObservableProperty]
    private string _timeRange;

    [ObservableProperty]
    private string _duration;

    /// <summary>Recomputes labels once exact metadata has replaced the chunk-name estimate.</summary>
    public void RefreshLabels() => (TimeRange, Duration) = ComputeLabels(Recording);

    private static (string TimeRange, string Duration) ComputeLabels(Recording recording)
    {
        var start = recording.StartTime.Kind == DateTimeKind.Utc
            ? recording.StartTime.ToLocalTime()
            : recording.StartTime;
        var duration = recording.Duration ?? TimeSegment.EstimateDuration(recording);
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        // TimeAxis.SafeEnd saturates start + duration so a pathological (corrupt/crafted) on-card duration
        // can never overflow DateTime and crash the browse tree.
        var end = TimeSegment.SafeEnd(start, duration);
        return ($"{start:HH:mm} – {end:HH:mm}", PlaybackViewModel.FormatDuration(duration));
    }
}
