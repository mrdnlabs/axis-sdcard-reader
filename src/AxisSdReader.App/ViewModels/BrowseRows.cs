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
/// lens as a separate VAPIX source; single-sensor cameras have exactly one.</summary>
public sealed class LensNode
{
    public LensNode(int number, string sourceToken, IReadOnlyList<DateNode> dates)
    {
        Number = number;
        SourceToken = sourceToken;
        Dates = dates;
    }

    public int Number { get; }
    public string SourceToken { get; }
    public string Label => $"Lens {Number}";
    public IReadOnlyList<DateNode> Dates { get; }
    public IEnumerable<Recording> Recordings => Dates.SelectMany(d => d.Recordings);
    public int ClipCount => Dates.Sum(d => d.ClipCount);
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

/// <summary>A lens tab in the lens bar.</summary>
public sealed partial class LensTab : ObservableObject
{
    public LensTab(LensNode node, ICommand command)
    {
        Node = node;
        Command = command;
    }

    public LensNode Node { get; }
    public ICommand Command { get; }
    public string Number => Node.Number.ToString();
    public string Label => Node.Label;

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>Base for the flattened browse-tree rows bound to the sidebar ItemsControl.</summary>
public abstract partial class BrowseRow : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded;
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

public sealed partial class DateRow : BrowseRow
{
    public DateRow(CameraNode camera, DateNode node, ICommand command)
    {
        Camera = camera;
        Node = node;
        Command = command;
    }

    public CameraNode Camera { get; }
    public DateNode Node { get; }
    public ICommand Command { get; }
    public string Label => Node.LongLabel;
    public string ClipsLabel => $"{Node.ClipCount} clip{(Node.ClipCount == 1 ? "" : "s")}";
}

public sealed partial class ClipRow : BrowseRow
{
    public ClipRow(CameraNode camera, DateNode date, Recording recording, ICommand command)
    {
        Camera = camera;
        Date = date;
        Recording = recording;
        Command = command;
        (_timeRange, _duration) = ComputeLabels(recording);
    }

    public CameraNode Camera { get; }
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
        var end = start + duration;
        return ($"{start:HH:mm} – {end:HH:mm}", PlaybackViewModel.FormatDuration(duration));
    }
}
