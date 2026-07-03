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

/// <summary>One camera on the card, with its recordings grouped by local date.</summary>
public sealed class CameraNode
{
    public CameraNode(string serial, string name, string model, IReadOnlyList<DateNode> dates)
    {
        Serial = serial;
        Name = name;
        Model = model;
        Dates = dates;
    }

    public string Serial { get; }
    public string Name { get; }
    public string Model { get; set; }
    public IReadOnlyList<DateNode> Dates { get; }
    public int TotalClips => Dates.Sum(d => d.ClipCount);
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
