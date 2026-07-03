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
/// Plays a whole day of recordings as one seamless timeline. The day position
/// (<see cref="CurrentSeconds"/>, 0–86400) maps onto recording segments and their MKV chunks;
/// while playing, reaching the end of a recording jumps to the start of the next one, skipping
/// gaps. Also owns the export mark-in/out range for the current day.
/// </summary>
public sealed partial class DayPlayerViewModel : ObservableObject, IDisposable
{
    private const double Day = 86400.0;

    private readonly Task _initTask;
    private readonly Dispatcher _dispatcher;
    private LibVLC? _libVlc;

    private OpenCard? _card;
    private DaySegment[] _segments = [];
    private int _activeSegment = -1;
    private int _activeChunk = -1;
    private Stream? _currentStream;
    private MediaInput? _currentInput;
    private Media? _currentMedia;
    private bool _suppressPositionEvents;

    [ObservableProperty]
    private MediaPlayer? _mediaPlayer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockString))]
    [NotifyPropertyChangedFor(nameof(BurnedStamp))]
    private double _currentSeconds;

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
    private string _dateLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BurnedStamp))]
    private string _dateLabelShort = "";

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

    public string ClockString => FormatClock(CurrentSeconds);

    public string StatusLabel => IsPlaying
        ? $"Playing {RateText(Rate)}"
        : HasFootage ? "Paused" : "No footage";

    /// <summary>Burned-in overlay text: date + running clock, e.g. "Jun 28  08:47:22".</summary>
    public string BurnedStamp => $"{DateLabelShort}  {ClockString}";

    public string SelectionLabel
    {
        get
        {
            if (SelInSeconds is { } i && SelOutSeconds is null)
            {
                return $"In {FormatClock(i)[..5]} · set out";
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

    private static string RateText(float rate) => rate == 1 ? "1×" : $"{rate:0.#}×";

    public ObservableCollection<TimelineSegment> TimelineSegments { get; } = [];

    public float[] AvailableRates { get; } = [0.5f, 1.0f, 2.0f, 4.0f, 8.0f];

    /// <summary>Fires when the active recording changes, so the tree can highlight the current clip.</summary>
    public event Action<Recording?>? ActiveRecordingChanged;

    public DayPlayerViewModel()
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

    /// <summary>Loads a day's recordings (metadata already populated) and seeks to the first clip, paused.</summary>
    public async Task LoadDay(OpenCard card, string cameraName, string cameraModel, DateTime date,
        IReadOnlyList<Recording> recordings)
    {
        await _initTask;
        StopPlayback();

        _card = card;
        CameraName = cameraName;
        CameraModel = cameraModel;
        DateLabel = date.ToString("ddd, MMM d, yyyy");
        DateLabelShort = date.ToString("MMM d");
        SelInSeconds = null;
        SelOutSeconds = null;

        _segments = recordings
            .Select(r => new DaySegment(r, LocalTimeOfDaySeconds(r)))
            .OrderBy(s => s.DayStartSeconds)
            .ToArray();

        TimelineSegments.Clear();
        foreach (var segment in _segments)
        {
            TimelineSegments.Add(segment.ToTimelineSegment());
        }

        var totalFootage = TimeSpan.FromSeconds(_segments.Sum(s => s.DurationSeconds));
        SegCountLabel = $"{_segments.Length} clip{(_segments.Length == 1 ? "" : "s")} · {FormatDuration(totalFootage)} footage";

        if (_segments.Length > 0)
        {
            SeekToDaySeconds(_segments[0].DayStartSeconds, autoPlay: false);
        }
        else
        {
            CurrentSeconds = 12 * 3600;
            HasFootage = false;
            ActiveRecordingChanged?.Invoke(null);
        }
    }

    /// <summary>The recording currently under the playhead, or null in a gap.</summary>
    public Recording? ActiveRecording =>
        _activeSegment >= 0 && _activeSegment < _segments.Length ? _segments[_activeSegment].Recording : null;

    /// <summary>Seeks to a clip's start (used by the tree). Paused.</summary>
    public void SeekToRecording(Recording recording)
    {
        var index = Array.FindIndex(_segments, s => s.Recording == recording);
        if (index >= 0)
        {
            SeekToDaySeconds(_segments[index].DayStartSeconds, autoPlay: false);
        }
    }

    /// <summary>Seeks to a day position; plays the containing segment (or shows the gap state).</summary>
    public void SeekToDaySeconds(double daySeconds, bool? autoPlay = null)
    {
        daySeconds = Math.Clamp(daySeconds, 0, Day - 1);
        var play = autoPlay ?? IsPlaying;

        var segIndex = Array.FindIndex(_segments, s => s.Contains(daySeconds));
        if (segIndex < 0)
        {
            // Landed in a gap: stop the picture, keep the playhead where the user put it.
            StopPlayback();
            CurrentSeconds = daySeconds;
            HasFootage = false;
            _activeSegment = -1;
            ActiveRecordingChanged?.Invoke(null);
            return;
        }

        // Fast path: seeking within the chunk that's already open (e.g. dragging the playhead)
        // just moves the playback time — no stream churn, which keeps libvlc stable under drags.
        if (segIndex == _activeSegment && MediaPlayer is not null && _currentMedia is not null)
        {
            var (chunkIndex, offset) = _segments[segIndex].Locate(daySeconds);
            if (chunkIndex == _activeChunk)
            {
                CurrentSeconds = daySeconds;
                MediaPlayer.Time = (long)offset.TotalMilliseconds;
                return;
            }
        }

        StartAt(segIndex, daySeconds, play);
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
            // In a gap or nothing loaded: jump to the nearest footage and play.
            JumpNearest(play: true);
        }
    }

    [RelayCommand]
    private void StepPrev()
    {
        var prev = _segments.Where(s => s.DayStartSeconds < CurrentSeconds - 1).ToArray();
        if (prev.Length > 0)
        {
            SeekToDaySeconds(prev[^1].DayStartSeconds, autoPlay: IsPlaying);
        }
    }

    [RelayCommand]
    private void StepNext()
    {
        var next = _segments.FirstOrDefault(s => s.DayStartSeconds > CurrentSeconds + 0.5);
        if (next is not null)
        {
            SeekToDaySeconds(next.DayStartSeconds, autoPlay: IsPlaying);
        }
    }

    [RelayCommand]
    private void GoToNearest() => JumpNearest(play: true);

    private void JumpNearest(bool play)
    {
        if (_segments.Length == 0)
        {
            return;
        }

        var next = _segments.FirstOrDefault(s => s.DayStartSeconds > CurrentSeconds);
        var prev = _segments.LastOrDefault(s => s.DayEndSeconds <= CurrentSeconds);

        double target;
        if (next is not null && prev is not null)
        {
            target = CurrentSeconds - prev.DayEndSeconds < next.DayStartSeconds - CurrentSeconds
                ? prev.DayEndSeconds - 1
                : next.DayStartSeconds;
        }
        else
        {
            target = next?.DayStartSeconds ?? (prev is not null ? prev.DayEndSeconds - 1 : CurrentSeconds);
        }

        SeekToDaySeconds(target, autoPlay: play);
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
            JumpNearest(play: true);
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
            SelOutSeconds = null; // out before in no longer valid
        }
    }

    [RelayCommand]
    private void MarkOut() => SelOutSeconds = CurrentSeconds;

    public void ClearSelection()
    {
        SelInSeconds = null;
        SelOutSeconds = null;
    }

    /// <summary>Recordings whose footage overlaps the current export selection.</summary>
    public IReadOnlyList<Recording> RecordingsInSelection()
    {
        if (SelInSeconds is not { } inSec || SelOutSeconds is not { } outSec || outSec <= inSec)
        {
            return [];
        }

        return _segments
            .Where(s => s.DayEndSeconds > inSec && s.DayStartSeconds < outSec)
            .Select(s => s.Recording)
            .ToList();
    }

    /// <summary>Clock string for the current selection range, e.g. "08:30:00 – 09:05:00".</summary>
    public string SelectionRangeText => HasSelection
        ? $"{FormatClock(SelInSeconds!.Value)} – {FormatClock(SelOutSeconds!.Value)}"
        : "—";

    public string SelectionDurationText => HasSelection
        ? FormatDuration(TimeSpan.FromSeconds(SelOutSeconds!.Value - SelInSeconds!.Value))
        : "";

    private void StartAt(int segIndex, double daySeconds, bool play)
    {
        var segment = _segments[segIndex];
        var (chunkIndex, offset) = segment.Locate(daySeconds);

        var recordingChanged = segIndex != _activeSegment;
        _activeSegment = segIndex;
        HasFootage = true;
        CurrentSeconds = daySeconds;

        PlayChunk(segment, chunkIndex, offset, play);

        if (recordingChanged)
        {
            ActiveRecordingChanged?.Invoke(segment.Recording);
        }
    }

    private void PlayChunk(DaySegment segment, int chunkIndex, TimeSpan offset, bool play)
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
        if (_suppressPositionEvents || _activeSegment < 0 || _activeSegment >= _segments.Length)
        {
            return;
        }

        var segment = _segments[_activeSegment];
        CurrentSeconds = segment.DaySecondsAtChunkStart(_activeChunk) + chunkTimeMs / 1000.0;
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

        // Recording finished — seamlessly jump to the next one, skipping any gap.
        var nextIndex = _activeSegment + 1;
        if (nextIndex < _segments.Length)
        {
            StartAt(nextIndex, _segments[nextIndex].DayStartSeconds, play: true);
        }
        else
        {
            IsPlaying = false;
            CurrentSeconds = segment.DayEndSeconds;
        }
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

    private static double LocalTimeOfDaySeconds(Recording recording)
    {
        var start = recording.StartTime;
        var local = start.Kind == DateTimeKind.Utc ? start.ToLocalTime() : start;
        return local.TimeOfDay.TotalSeconds;
    }

    public static string FormatClock(double daySeconds)
    {
        var s = (int)Math.Clamp(daySeconds, 0, Day - 1);
        return $"{s / 3600:00}:{s % 3600 / 60:00}:{s % 60:00}";
    }

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
