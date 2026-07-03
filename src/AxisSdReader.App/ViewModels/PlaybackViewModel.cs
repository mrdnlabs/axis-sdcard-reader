using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using AxisSdReader.App.Controls;
using AxisSdReader.App.Services;
using AxisSdReader.Core.Axis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

namespace AxisSdReader.App.ViewModels;

/// <summary>
/// Plays a camera's recordings on a continuous time axis (see <see cref="TimeAxis"/>).
/// The position maps onto recording segments and their MKV chunks; while playing, reaching
/// the end of a recording jumps to the start of the next, skipping gaps — across midnight
/// or across days, since time is continuous. Segments are placed from chunk-name estimates
/// and refined with exact metadata the first time the playhead enters them.
/// Also owns the export mark-in/out range.
/// </summary>
public sealed partial class PlaybackViewModel : ObservableObject, IDisposable
{
    private readonly Task _initTask;
    private readonly Dispatcher _dispatcher;
    private LibVLC? _libVlc;

    private OpenCard? _card;
    private TimeSegment[] _segments = [];
    private int _activeSegment = -1;
    private int _activeChunk = -1;
    private Stream? _currentStream;
    private MediaInput? _currentInput;
    private Media? _currentMedia;
    private bool _isScrubbing;
    private int _seekVersion;

    [ObservableProperty]
    private MediaPlayer? _mediaPlayer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockString))]
    [NotifyPropertyChangedFor(nameof(BurnedStamp))]
    [NotifyPropertyChangedFor(nameof(PositionDateShort))]
    [NotifyPropertyChangedFor(nameof(PositionDateLong))]
    [NotifyPropertyChangedFor(nameof(WindowLabel))]
    private double _currentSeconds;

    /// <summary>Detail-track zoom: seconds across the visible window (one of <see cref="Controls.TimelineControl.Spans"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowLabel))]
    [NotifyPropertyChangedFor(nameof(SpanLabel))]
    [NotifyPropertyChangedFor(nameof(IsFullDay))]
    private double _spanSeconds = 1800;

    /// <summary>Lens indicator for multi-lens cameras (e.g. "Lens 2"); empty for single-lens.</summary>
    [ObservableProperty]
    private string _lensLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoFootage))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private bool _hasFootage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private float _rate = 1.0f;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private string _cameraName = "";

    [ObservableProperty]
    private string _cameraModel = "";

    [ObservableProperty]
    private string _segCountLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionRangeText))]
    [NotifyPropertyChangedFor(nameof(SelectionDurationText))]
    [NotifyPropertyChangedFor(nameof(SelectionLabel))]
    private double? _selInSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionRangeText))]
    [NotifyPropertyChangedFor(nameof(SelectionDurationText))]
    [NotifyPropertyChangedFor(nameof(SelectionLabel))]
    private double? _selOutSeconds;

    public bool NoFootage => !HasFootage;

    public bool HasSelection => SelInSeconds is { } i && SelOutSeconds is { } o && o > i;

    public string ClockString => TimeAxis.ClockText(CurrentSeconds);

    /// <summary>Date at the playhead, short form (e.g. "Jul 2") — updates crossing midnight.</summary>
    public string PositionDateShort => TimeAxis.DateShort(CurrentSeconds);

    public string PositionDateLong => TimeAxis.DateLong(CurrentSeconds);

    public string StatusLabel => IsPlaying
        ? $"Playing {RateText(Rate)}"
        : HasFootage ? "Paused" : "No footage";

    public string BurnedStamp => $"{PositionDateShort}  {ClockString}";

    public string SelectionRangeText => HasSelection
        ? $"{TimeAxis.ClockText(SelInSeconds!.Value)} – {TimeAxis.ClockText(SelOutSeconds!.Value)}"
        : "—";

    public string SelectionDurationText => HasSelection
        ? FormatDuration(TimeSpan.FromSeconds(SelOutSeconds!.Value - SelInSeconds!.Value))
        : "";

    public string SelectionLabel
    {
        get
        {
            if (SelInSeconds is { } i && SelOutSeconds is null)
            {
                return $"In {TimeAxis.ClockText(i)[..5]} · set out";
            }

            if (HasSelection)
            {
                return $"{SelectionDurationText} selected";
            }

            if (SelInSeconds is not null && SelOutSeconds is not null)
            {
                return "Out is before in";
            }

            return "No range set";
        }
    }

    public bool IsFullDay => SpanSeconds >= 86400;

    /// <summary>Zoom-window readout, e.g. "08:32 – 09:02 · 30 min window" or "Full-day view · 00:00 – 24:00".</summary>
    public string WindowLabel
    {
        get
        {
            if (IsFullDay)
            {
                return "Full-day view · 00:00 – 24:00";
            }

            var from = TimeAxis.ToDateTime(CurrentSeconds - SpanSeconds / 2);
            var to = TimeAxis.ToDateTime(CurrentSeconds + SpanSeconds / 2);
            return $"{from:HH:mm} – {to:HH:mm} · {SpanLabel} window";
        }
    }

    public string SpanLabel => SpanSeconds switch
    {
        <= 300 => "5 min",
        <= 900 => "15 min",
        <= 1800 => "30 min",
        <= 3600 => "1 hour",
        <= 10800 => "3 hours",
        <= 21600 => "6 hours",
        _ => "Full day",
    };

    [RelayCommand]
    private void ZoomIn() => StepSpan(-1);

    [RelayCommand]
    private void ZoomOut() => StepSpan(+1);

    [RelayCommand]
    private void FitDay() => SpanSeconds = 86400;

    private void StepSpan(int direction)
    {
        var spans = Controls.TimelineControl.Spans;
        var index = Math.Clamp(Controls.TimelineControl.NearestSpanIndex(SpanSeconds) + direction, 0, spans.Length - 1);
        SpanSeconds = spans[index];
    }

    public ObservableCollection<TimelineSegment> TimelineSegments { get; } = [];

    public float[] AvailableRates { get; } = [0.5f, 1.0f, 2.0f, 4.0f, 8.0f];

    /// <summary>Fires when the active recording changes, so the tree can highlight the current clip.</summary>
    public event Action<Recording?>? ActiveRecordingChanged;

    public PlaybackViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _initTask = Task.Run(InitializeEngine);
    }

    private void InitializeEngine()
    {
        var libVlc = new LibVLC("--no-osd", "--no-video-title-show");
        var player = new MediaPlayer(libVlc) { EnableHardwareDecoding = true };

        player.TimeChanged += (_, e) => _dispatcher.BeginInvoke(() => OnChunkTimeChanged(e.Time));
        player.EndReached += (_, _) => _dispatcher.BeginInvoke(OnChunkEnded);
        player.Playing += (_, _) => _dispatcher.BeginInvoke(() => IsPlaying = true);
        player.Paused += (_, _) => _dispatcher.BeginInvoke(() => IsPlaying = false);

        _dispatcher.Invoke(() =>
        {
            _libVlc = libVlc;
            player.Mute = IsMuted;
            MediaPlayer = player;
        });
    }

    /// <summary>Loads a camera's recordings onto the time axis and seeks (paused) to the newest one.</summary>
    public async Task LoadCamera(OpenCard card, string cameraName, string cameraModel,
        IReadOnlyList<Recording> recordings)
    {
        await _initTask;
        StopPlayback();

        _card = card;
        CameraName = cameraName;
        CameraModel = cameraModel;
        SelInSeconds = null;
        SelOutSeconds = null;

        _segments = recordings
            .Select(r => new TimeSegment(r))
            .OrderBy(s => s.StartSeconds)
            .ToArray();
        _activeSegment = -1;

        RebuildTimelineSegments();
        UpdateSegCountLabel();

        if (_segments.Length > 0)
        {
            await SeekToSeconds(_segments[^1].StartSeconds, autoPlay: false);
        }
        else
        {
            HasFootage = false;
            ActiveRecordingChanged?.Invoke(null);
        }
    }

    public Recording? ActiveRecording =>
        _activeSegment >= 0 && _activeSegment < _segments.Length ? _segments[_activeSegment].Recording : null;

    /// <summary>Seeks (paused) to a recording's start — used by the browse tree.</summary>
    public async Task SeekToRecording(Recording recording)
    {
        var segment = _segments.FirstOrDefault(s => s.Recording == recording);
        if (segment is not null)
        {
            await SeekToSeconds(segment.StartSeconds, autoPlay: false);
        }
    }

    /// <summary>Live scrub preview: update the clock/date readouts without touching the media.</summary>
    public void ScrubPreview(double seconds)
    {
        _isScrubbing = true;
        CurrentSeconds = seconds;
    }

    /// <summary>Commits a scrub/click: seek the media to the time now under the playhead.</summary>
    public async void ScrubCommit(double seconds)
    {
        _isScrubbing = false;
        await SeekToSeconds(seconds, autoPlay: IsPlaying);
    }

    /// <summary>Seeks to an axis position; plays the containing segment or shows the gap state.</summary>
    public async Task SeekToSeconds(double seconds, bool? autoPlay = null)
    {
        var version = ++_seekVersion;
        var play = autoPlay ?? IsPlaying;

        var segIndex = Array.FindIndex(_segments, s => s.Contains(seconds));
        if (segIndex < 0)
        {
            StopPlayback();
            CurrentSeconds = seconds;
            HasFootage = false;
            _activeSegment = -1;
            ActiveRecordingChanged?.Invoke(null);
            return;
        }

        var segment = _segments[segIndex];

        // First entry into this recording: load exact chunk metadata (a few small reads),
        // which can shift the estimated end. Re-resolve afterwards.
        if (!segment.IsRefined && _card is not null)
        {
            await _card.LoadMetadataAsync(segment.Recording);
            if (version != _seekVersion)
            {
                return; // a newer seek superseded this one while metadata loaded
            }

            segment.Refine();
            RebuildTimelineSegments();
            UpdateSegCountLabel();

            if (!segment.Contains(seconds))
            {
                seconds = Math.Clamp(seconds, segment.StartSeconds, Math.Max(segment.StartSeconds, segment.EndSeconds - 0.5));
            }
        }

        // Fast path: staying inside the currently open chunk just moves the playback time.
        if (segIndex == _activeSegment && MediaPlayer is not null && _currentMedia is not null)
        {
            var (chunkIndex, offset) = segment.Locate(seconds);
            if (chunkIndex == _activeChunk)
            {
                CurrentSeconds = seconds;
                MediaPlayer.Time = (long)offset.TotalMilliseconds;
                return;
            }
        }

        StartAt(segIndex, seconds, play);
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (MediaPlayer is null)
        {
            return;
        }

        if (IsPlaying)
        {
            MediaPlayer.Pause();
            return;
        }

        if (HasFootage && _currentMedia is not null)
        {
            MediaPlayer.Play();
        }
        else if (_segments.Length > 0)
        {
            _ = JumpNearest(play: true);
        }
    }

    [RelayCommand]
    private async Task StepPrev()
    {
        var prev = _segments.LastOrDefault(s => s.StartSeconds < CurrentSeconds - 1);
        if (prev is not null)
        {
            await SeekToSeconds(prev.StartSeconds, autoPlay: IsPlaying);
        }
    }

    [RelayCommand]
    private async Task StepNext()
    {
        var next = _segments.FirstOrDefault(s => s.StartSeconds > CurrentSeconds + 0.5);
        if (next is not null)
        {
            await SeekToSeconds(next.StartSeconds, autoPlay: IsPlaying);
        }
    }

    [RelayCommand]
    private async Task GoToNearest() => await JumpNearest(play: true);

    private async Task JumpNearest(bool play)
    {
        if (_segments.Length == 0)
        {
            return;
        }

        var next = _segments.FirstOrDefault(s => s.StartSeconds > CurrentSeconds);
        var prev = _segments.LastOrDefault(s => s.EndSeconds <= CurrentSeconds);

        double target;
        if (next is not null && prev is not null)
        {
            target = CurrentSeconds - prev.EndSeconds < next.StartSeconds - CurrentSeconds
                ? prev.EndSeconds - 1
                : next.StartSeconds;
        }
        else
        {
            target = next?.StartSeconds ?? (prev is not null ? prev.EndSeconds - 1 : CurrentSeconds);
        }

        await SeekToSeconds(target, autoPlay: play);
    }

    [RelayCommand]
    private void SetRate(string rate)
    {
        if (!float.TryParse(rate, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return;
        }

        Rate = value;
        MediaPlayer?.SetRate(value);
        if (HasFootage)
        {
            MediaPlayer?.Play();
        }
        else
        {
            _ = JumpNearest(play: true);
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        if (MediaPlayer is not null)
        {
            MediaPlayer.Mute = IsMuted;
        }
    }

    [RelayCommand]
    private void MarkIn()
    {
        SelInSeconds = CurrentSeconds;
        if (SelOutSeconds is { } o && o <= CurrentSeconds)
        {
            SelOutSeconds = null;
        }
    }

    [RelayCommand]
    private void MarkOut() => SelOutSeconds = CurrentSeconds;

    public void ClearSelection()
    {
        SelInSeconds = null;
        SelOutSeconds = null;
    }

    /// <summary>
    /// Builds export jobs for the current selection: one slice per overlapping recording
    /// (recordings are gap-separated, so each becomes its own trimmed output). Ensures each
    /// segment's chunk metadata is exact so trim offsets are accurate.
    /// </summary>
    public async Task<IReadOnlyList<ExportSlice>> BuildExportSlices()
    {
        if (_card is null || SelInSeconds is not { } inSec || SelOutSeconds is not { } outSec || outSec <= inSec)
        {
            return [];
        }

        var overlapping = _segments.Where(s => s.EndSeconds > inSec && s.StartSeconds < outSec).ToList();
        foreach (var segment in overlapping.Where(s => !s.IsRefined))
        {
            await _card.LoadMetadataAsync(segment.Recording);
            segment.Refine();
        }

        return overlapping
            .Select(s => s.SliceFor(inSec, outSec))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
    }

    private void StartAt(int segIndex, double seconds, bool play)
    {
        var segment = _segments[segIndex];
        var (chunkIndex, offset) = segment.Locate(seconds);

        var recordingChanged = segIndex != _activeSegment;
        _activeSegment = segIndex;
        HasFootage = true;
        CurrentSeconds = seconds;

        PlayChunk(segment, chunkIndex, offset, play);

        if (recordingChanged)
        {
            ActiveRecordingChanged?.Invoke(segment.Recording);
        }
    }

    private void PlayChunk(TimeSegment segment, int chunkIndex, TimeSpan offset, bool play)
    {
        if (_card is null || MediaPlayer is null || _libVlc is null ||
            chunkIndex < 0 || chunkIndex >= segment.Recording.Chunks.Count)
        {
            return;
        }

        MediaPlayer.Stop();
        DisposeCurrentMedia();

        _activeChunk = chunkIndex;
        _currentStream = _card.OpenChunk(segment.Recording.Chunks[chunkIndex]);
        _currentInput = new StreamMediaInput(_currentStream);
        _currentMedia = new Media(_libVlc, _currentInput);

        MediaPlayer.Play(_currentMedia);
        MediaPlayer.SetRate(Rate);
        MediaPlayer.Mute = IsMuted;

        if (offset > TimeSpan.Zero)
        {
            MediaPlayer.Time = (long)offset.TotalMilliseconds;
        }

        if (!play)
        {
            // Show the frame but stay paused. A tiny delay lets VLC render the first frame.
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () => MediaPlayer.SetPause(true));
        }
    }

    private void OnChunkTimeChanged(long chunkTimeMs)
    {
        if (_isScrubbing || _activeSegment < 0 || _activeSegment >= _segments.Length)
        {
            return;
        }

        var segment = _segments[_activeSegment];
        CurrentSeconds = segment.SecondsAtChunkStart(_activeChunk) + chunkTimeMs / 1000.0;
    }

    private void OnChunkEnded()
    {
        if (_activeSegment < 0 || _activeSegment >= _segments.Length)
        {
            return;
        }

        var segment = _segments[_activeSegment];
        if (_activeChunk + 1 < segment.Recording.Chunks.Count)
        {
            PlayChunk(segment, _activeChunk + 1, TimeSpan.Zero, play: true);
            return;
        }

        // Recording finished — seamlessly jump to the next one, skipping the gap.
        var nextIndex = _activeSegment + 1;
        if (nextIndex < _segments.Length)
        {
            _ = SeekToSeconds(_segments[nextIndex].StartSeconds, autoPlay: true);
        }
        else
        {
            IsPlaying = false;
            CurrentSeconds = segment.EndSeconds;
        }
    }

    private void RebuildTimelineSegments()
    {
        TimelineSegments.Clear();
        foreach (var segment in _segments)
        {
            TimelineSegments.Add(new TimelineSegment(segment.StartSeconds, segment.EndSeconds));
        }
    }

    private void UpdateSegCountLabel()
    {
        var totalFootage = TimeSpan.FromSeconds(_segments.Sum(s => s.DurationSeconds));
        SegCountLabel = $"{_segments.Length} clip{(_segments.Length == 1 ? "" : "s")} · {FormatDuration(totalFootage)} footage";
    }

    private void StopPlayback()
    {
        MediaPlayer?.Stop();
        DisposeCurrentMedia();
        _activeChunk = -1;
        IsPlaying = false;
    }

    private void DisposeCurrentMedia()
    {
        _currentMedia?.Dispose();
        _currentMedia = null;
        _currentInput?.Dispose();
        _currentInput = null;
        _currentStream?.Dispose();
        _currentStream = null;
    }

    private static string RateText(float rate) => rate == 1 ? "1×" : $"{rate:0.#}×";

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{span.Minutes}m {span.Seconds:00}s";
    }

    public void Dispose()
    {
        StopPlayback();
        MediaPlayer?.Dispose();
        _libVlc?.Dispose();
    }
}
