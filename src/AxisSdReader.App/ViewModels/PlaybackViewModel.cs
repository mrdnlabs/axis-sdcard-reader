using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using AxisSdReader.App.Controls;
using AxisSdReader.App.Services;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Media;
using AxisSdReader.Core.Timeline;
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
    private bool _switchingStream; // re-entrancy guard for the follow-the-top-band stream switch
    private int _seekVersion;
    private bool _disposed;
    private CancellationTokenSource? _seekCts;   // cancels a superseded seek's metadata load
    private int _mediaGeneration;               // bumped on every media swap; stale VLC events are dropped
    private int _consecutiveErrors;

    // Kept so they can be detached in Dispose (LibVLC callbacks otherwise fire after teardown).
    private EventHandler<MediaPlayerTimeChangedEventArgs>? _onTimeChanged;
    private EventHandler<EventArgs>? _onEndReached;
    private EventHandler<EventArgs>? _onPlaying;
    private EventHandler<EventArgs>? _onPaused;
    private EventHandler<EventArgs>? _onError;

    /// <summary>Set when libVLC could not start: browsing and export still work, playback doesn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private string? _engineError;

    /// <summary>A short note when part of a recording couldn't be read (shown over the video).</summary>
    [ObservableProperty]
    private string? _clipError;

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

    /// <summary>True while a seek is taking long enough to be worth showing a loading indicator
    /// (the first seek into a recording loads and decrypts its chunk metadata).</summary>
    [ObservableProperty]
    private bool _isSeeking;

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

    public string StatusLabel => EngineError is not null
        ? "Video engine unavailable"
        : IsPlaying
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
        // --ignore-config: never read a vlcrc. --no-sub-autodetect-file: never go looking for files beside the
        // input. (libVLC's Lua engine is not shipped at all — see the csproj.)
        // No OSD, no subtitles of any kind (--no-spu: subtitle tracks in a card's MKV are never decoded).
        var libVlc = new LibVLC("--no-osd", "--no-video-title-show", "--ignore-config", "--no-sub-autodetect-file", "--no-spu");
        var player = new MediaPlayer(libVlc) { EnableHardwareDecoding = true };

        // Marshal to the UI thread and drop the event if we've since been disposed — LibVLC can raise these
        // from its own thread just after teardown — or if the media it belongs to has since been swapped out
        // (events already queued for the old chunk would otherwise move the playhead on the new one).
        void Post(Action action)
        {
            var generation = Volatile.Read(ref _mediaGeneration);
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed && generation == _mediaGeneration)
                {
                    action();
                }
            });
        }

        _onTimeChanged = (_, e) =>
        {
            var t = e.Time;
            Post(() => OnChunkTimeChanged(t));
        };
        _onEndReached = (_, _) => Post(OnChunkEnded);
        _onPlaying = (_, _) => Post(() =>
        {
            IsPlaying = true;
            _consecutiveErrors = 0;
        });
        _onPaused = (_, _) => Post(() => IsPlaying = false);
        _onError = (_, _) => Post(OnChunkError);

        player.TimeChanged += _onTimeChanged;
        player.EndReached += _onEndReached;
        player.Playing += _onPlaying;
        player.Paused += _onPaused;
        player.EncounteredError += _onError;

        _dispatcher.Invoke(() =>
        {
            if (_disposed)
            {
                // Closed while the engine was starting: release it instead of leaking it.
                player.Dispose();
                libVlc.Dispose();
                return;
            }

            _libVlc = libVlc;
            player.Mute = IsMuted;
            MediaPlayer = player;
        });
    }

    /// <summary>Loads a camera's recordings onto the time axis and seeks (paused) to the newest one.</summary>
    public async Task LoadCamera(OpenCard card, string cameraName, string cameraModel,
        IReadOnlyList<Recording> recordings)
    {
        var version = ++_seekVersion; // supersede any seek still in flight for the previous camera
        try
        {
            await _initTask;
        }
        catch (Exception ex)
        {
            // No video engine: keep browsing, the timeline and export working; only playback is unavailable.
            EngineError = ex.Message;
        }

        if (version != _seekVersion || _disposed)
        {
            return; // superseded (another camera load, or the card closed) while the engine was starting
        }

        StopPlayback();

        _card = card;
        CameraName = cameraName;
        CameraModel = cameraModel;
        SelInSeconds = null;
        SelOutSeconds = null;

        // A recording with no chunks has nothing to play; leave it out of playback (it is still listed).
        _segments = recordings
            .Where(r => r.Chunks.Count > 0)
            .Select(r => new TimeSegment(r))
            .OrderBy(s => s.StartSeconds)
            .ToArray();
        _activeSegment = -1;

        RebuildTimelineSegments();
        UpdateSegCountLabel();

        if (_segments.Length > 0)
        {
            // Start at the newest recording — but not at a bogus far-future one from a corrupt or crafted card.
            var horizon = TimeAxis.ToSeconds(DateTime.UtcNow.AddDays(2));
            var newest = _segments.LastOrDefault(s => s.StartSeconds <= horizon) ?? _segments[^1];
            await SeekToSeconds(newest.StartSeconds, autoPlay: false);
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
        try
        {
            await SeekToSeconds(seconds, autoPlay: IsPlaying);
        }
        catch (OperationCanceledException)
        {
            // the card was closed or removed mid-seek
        }
    }

    /// <summary>Seeks to an axis position; plays the containing segment or shows the gap state.</summary>
    public async Task SeekToSeconds(double seconds, bool? autoPlay = null)
    {
        var version = ++_seekVersion;
        var play = autoPlay ?? IsPlaying;

        // Show a loading indicator only if this seek is still running after a short grace period, so quick
        // in-chunk seeks don't flicker it but the first (metadata-loading) seek into a recording does.
        var seekDone = false;
        ShowSeekingIfSlow(version, () => seekDone);
        try
        {
            var segIndex = BestSegmentIndex(seconds);
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
            // which can shift the estimated start/end. Re-resolve afterwards.
            if (!segment.IsRefined && _card is not null)
            {
                // A newer seek cancels this one's load (between chunks; what was loaded is kept).
                _seekCts?.Cancel();
                var cts = _seekCts = new CancellationTokenSource();
                var card = _card;
                try
                {
                    await card.LoadMetadataAsync(segment.Recording, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return; // superseded, or the card was closed/removed while metadata was loading
                }

                // The card may have been closed, or the camera switched, while metadata loaded.
                if (version != _seekVersion || !ReferenceEquals(card, _card) ||
                    segIndex >= _segments.Length || !ReferenceEquals(_segments[segIndex], segment))
                {
                    return;
                }

                var oldStart = segment.StartSeconds;
                segment.Refine();
                if (segment.StartSeconds != oldStart)
                {
                    ResortSegments();
                    RebuildTimelineSegments();
                }
                else
                {
                    UpdateTimelineSegment(segIndex);
                }

                UpdateSegCountLabel();
                segIndex = Array.IndexOf(_segments, segment);

                if (!segment.Contains(seconds))
                {
                    seconds = Math.Clamp(seconds, segment.StartSeconds, Math.Max(segment.StartSeconds, segment.EndSeconds - 0.5));
                }
            }

            // Fast path: staying inside the currently open chunk just moves the playback time — but only while
            // the player is actually holding it (an ended or errored player ignores a time change).
            if (segIndex == _activeSegment && MediaPlayer is { State: VLCState.Playing or VLCState.Paused } && _currentMedia is not null)
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
        finally
        {
            seekDone = true;
            if (version == _seekVersion)
            {
                IsSeeking = false;
            }
        }
    }

    /// <summary>Turns on <see cref="IsSeeking"/> only if the current seek is still running after a short
    /// delay (and hasn't been superseded), so brief seeks never flash the indicator.</summary>
    private async void ShowSeekingIfSlow(int version, Func<bool> done)
    {
        try
        {
            await Task.Delay(160);
        }
        catch
        {
            return;
        }

        if (!_disposed && version == _seekVersion && !done())
        {
            IsSeeking = true;
        }
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
        else
        {
            ResumeOrJump();
        }
    }

    /// <summary>Plays from the playhead when it sits on footage whose media was released (e.g. after an
    /// export), otherwise jumps to the nearest recording.</summary>
    private void ResumeOrJump()
    {
        if (_segments.Length == 0)
        {
            return;
        }

        _ = BestSegmentIndex(CurrentSeconds) >= 0
            ? Observe(SeekToSeconds(CurrentSeconds, autoPlay: true))
            : Observe(JumpNearest(play: true));
    }

    /// <summary>Fire-and-forget with the faults observed: a card closed mid-operation is expected, anything
    /// else is logged rather than lost.</summary>
    private static async Task Observe(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Log("Playback", ex);
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
        if (HasFootage && _currentMedia is not null)
        {
            MediaPlayer?.Play();
        }
        else
        {
            ResumeOrJump();
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

    /// <summary>Sets the export range (mark-in/out) programmatically.</summary>
    public void SetSelection(double inSeconds, double outSeconds)
    {
        SelInSeconds = Math.Min(inSeconds, outSeconds);
        SelOutSeconds = Math.Max(inSeconds, outSeconds);
    }

    /// <summary>
    /// Builds export jobs for the current selection: one slice per recording overlapping the range, each
    /// trimmed to the range. Recordings can overlap in time — ACS Edge records continuous and motion in
    /// parallel — so a range covered by both deliberately yields BOTH clips; the caller tells them apart by
    /// <see cref="Recording.Kind"/>. Ensures each segment's chunk metadata is exact so trims are accurate.
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
        // The chunk comes from the same mapping as the index (the recording's own list may have been replaced since).
        if (_card is null || MediaPlayer is null || _libVlc is null || segment.ChunkAt(chunkIndex) is not { } chunk)
        {
            return;
        }

        // Swap media WITHOUT an explicit Stop(): Stop() tears down libvlc's video output, which
        // exposes the host window's (white) background for a moment. Calling Play(newMedia)
        // keeps the output alive so the transition clears to black instead of flashing white.
        // The previous media/input/stream are disposed a beat later, once libvlc has released
        // them, to avoid the demux-callback race that a synchronous dispose would hit.
        // Build the new media first, so a failure leaves the player state untouched.
        Stream stream;
        MediaInput input;
        Media media;
        try
        {
            stream = _card.OpenChunk(chunk);
            input = new StreamMediaInput(stream);
            media = new Media(_libVlc, input);

            // Only the Matroska demuxer may open card data: a crafted chunk in another format must not reach
            // VLC's other parsers, all of which run inside this elevated process.
            media.AddOption(":demux=mkv");
        }
        catch (OperationCanceledException)
        {
            return; // the card was closed
        }

        var oldMedia = _currentMedia;
        var oldInput = _currentInput;
        var oldStream = _currentStream;

        Interlocked.Increment(ref _mediaGeneration);
        _activeChunk = chunkIndex;
        _currentStream = stream;
        _currentInput = input;
        _currentMedia = media;
        ClipError = null;

        MediaPlayer.Play(_currentMedia);
        MediaPlayer.SetRate(Rate);
        MediaPlayer.Mute = IsMuted;

        if (offset > TimeSpan.Zero)
        {
            MediaPlayer.Time = (long)offset.TotalMilliseconds;
        }

        if (!play)
        {
            // Show the frame but stay paused. A tiny delay lets VLC render the first frame. Guard against
            // teardown or a newer chunk having been swapped in before this deferred call runs.
            var pausedMedia = _currentMedia;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!_disposed && ReferenceEquals(_currentMedia, pausedMedia))
                {
                    MediaPlayer?.SetPause(true);
                }
            });
        }

        if (oldMedia is not null || oldInput is not null || oldStream is not null)
        {
            _ = DisposeAfterSwap(oldMedia, oldInput, oldStream);
        }
    }

    private static async Task DisposeAfterSwap(Media? media, MediaInput? input, Stream? stream)
    {
        // Off the UI thread: disposing a card stream waits for the card's I/O gate.
        await Task.Delay(250).ConfigureAwait(false);
        try { media?.Dispose(); } catch { /* best effort */ }
        try { input?.Dispose(); } catch { /* best effort */ }
        try { stream?.Dispose(); } catch { /* best effort */ }
    }

    /// <summary>
    /// The recording to play at an axis position. Recordings can overlap — ACS Edge records continuous
    /// (low profile) and motion (high profile) in parallel — so the higher-priority kind wins: playback
    /// follows the same band the timeline paints on top (manual &gt; event &gt; continuous), which is also
    /// normally the higher-quality stream. Returns -1 when nothing covers the position.
    /// </summary>
    private int BestSegmentIndex(double seconds)
    {
        var best = -1;
        var bestPriority = int.MinValue;
        for (var i = 0; i < _segments.Length; i++)
        {
            if (!_segments[i].Contains(seconds))
            {
                continue;
            }

            var priority = RecordingTypeClassifier.OverlayPriority(_segments[i].Recording.Kind);
            if (priority > bestPriority)
            {
                bestPriority = priority;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Switches playback to the best recording covering <paramref name="seconds"/>, clearing the
    /// re-entrancy guard when the swap settles.</summary>
    private async Task SwitchStreamAsync(double seconds)
    {
        try
        {
            await SeekToSeconds(seconds, autoPlay: true);
        }
        catch (OperationCanceledException)
        {
            // the card was closed/removed mid-switch — nothing to do
        }
        finally
        {
            _switchingStream = false;
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

        // Follow the top band: when a higher-priority recording (a motion event) starts covering this
        // moment, switch to it — and back to continuous when it ends — so playback always shows the stream
        // the timeline paints on top. Guarded: VLC raises TimeChanged several times a second, and the swap
        // itself is async.
        if (_switchingStream || !IsPlaying)
        {
            return;
        }

        var best = BestSegmentIndex(CurrentSeconds);
        if (best >= 0 && best != _activeSegment)
        {
            _switchingStream = true;
            _ = SwitchStreamAsync(CurrentSeconds);
        }
    }

    private void OnChunkEnded()
    {
        if (_activeSegment < 0 || _activeSegment >= _segments.Length)
        {
            return;
        }

        var segment = _segments[_activeSegment];
        if (_activeChunk + 1 < segment.ChunkCount)
        {
            PlayChunk(segment, _activeChunk + 1, TimeSpan.Zero, play: true);
            return;
        }

        // Recording finished. If another recording still covers this moment — e.g. a motion event ended but
        // the parallel continuous recording runs on — resume there at the same wall-clock time rather than
        // skipping ahead to the next recording's start.
        if (BestSegmentIndex(segment.EndSeconds) >= 0)
        {
            _ = Observe(SeekToSeconds(segment.EndSeconds, autoPlay: true));
            return;
        }

        // Otherwise jump forward to the next recording, skipping the gap. Pick it by TIME, not by index:
        // recordings overlap (continuous + motion), so the next index can start EARLIER than this one
        // ended, which would jump playback backwards and loop. _segments is ordered by start time.
        var next = _segments.FirstOrDefault(s => s.StartSeconds >= segment.EndSeconds);
        if (next is not null)
        {
            _ = Observe(SeekToSeconds(next.StartSeconds, autoPlay: true));
        }
        else
        {
            IsPlaying = false;
            CurrentSeconds = segment.EndSeconds;
        }
    }

    /// <summary>
    /// VLC could not read or decode the current chunk (damaged, truncated by a card pull, not Matroska).
    /// While playing, skip past it — like reaching its end — so one bad spot doesn't halt playback; after a
    /// few failures in a row, stop and say so rather than racing through a dead card.
    /// </summary>
    private void OnChunkError()
    {
        var wasPlaying = IsPlaying;
        IsPlaying = false;
        ClipError = "Part of this recording couldn't be read.";
        if (!wasPlaying || ++_consecutiveErrors > 3 || _activeSegment < 0 || _activeSegment >= _segments.Length)
        {
            return;
        }

        OnChunkEnded();
    }

    private void RebuildTimelineSegments()
    {
        TimelineSegments.Clear();
        foreach (var segment in _segments)
        {
            TimelineSegments.Add(new TimelineSegment(
                segment.StartSeconds, segment.EndSeconds, segment.Recording.Kind));
        }
    }

    /// <summary>Redraws one refined segment without rebuilding the whole timeline.</summary>
    private void UpdateTimelineSegment(int index)
    {
        if (index < 0 || index >= _segments.Length || index >= TimelineSegments.Count)
        {
            RebuildTimelineSegments();
            return;
        }

        var segment = _segments[index];
        TimelineSegments[index] = new TimelineSegment(segment.StartSeconds, segment.EndSeconds, segment.Recording.Kind);
    }

    /// <summary>Keeps segments in start order after refinement moved one, preserving the active segment.</summary>
    private void ResortSegments()
    {
        var active = _activeSegment >= 0 && _activeSegment < _segments.Length ? _segments[_activeSegment] : null;
        _segments = _segments.OrderBy(s => s.StartSeconds).ToArray();
        _activeSegment = active is null ? -1 : Array.IndexOf(_segments, active);
    }

    private void UpdateSegCountLabel()
    {
        var totalFootage = SafeTime.FromSeconds(_segments.Sum(s => s.DurationSeconds));
        SegCountLabel = $"{_segments.Length} clip{(_segments.Length == 1 ? "" : "s")} · {FormatDuration(totalFootage)} footage";
    }

    private void StopPlayback()
    {
        Interlocked.Increment(ref _mediaGeneration);
        if (MediaPlayer is { } player)
        {
            player.Stop();

            // Detach the media too: otherwise a later Play() (a speed button, say) would make VLC reopen it
            // through its already-disposed input.
            player.Media = null;
        }

        DisposeCurrentMedia();
        _activeChunk = -1;
        IsPlaying = false;
        IsSeeking = false;
    }

    /// <summary>
    /// Stops playback and releases the current card stream so an exclusive card operation (e.g. export)
    /// can run without the demux thread reading the shared device stream at the same time. The playhead
    /// position is kept, so pressing play resumes from where the user was.
    /// </summary>
    public void SuspendForExclusiveIo() => StopPlayback();

    /// <summary>
    /// Stops playback and forgets the card entirely — used when the card is being closed/removed so no
    /// later seek or callback dereferences the disposed card. Clears segments and footage state.
    /// </summary>
    public void DetachCard()
    {
        _seekVersion++; // any seek still awaiting metadata must not resume against the old segments
        _seekCts?.Cancel();
        StopPlayback();
        _card = null;
        _segments = [];
        _activeSegment = -1;
        TimelineSegments.Clear();
        HasFootage = false;
    }

    private void DisposeCurrentMedia()
    {
        _currentMedia?.Dispose();
        _currentMedia = null;
        _currentInput?.Dispose();
        _currentInput = null;

        // The card stream's dispose waits for the card's I/O gate; don't do that on the UI thread.
        if (_currentStream is { } stream)
        {
            _currentStream = null;
            _ = Task.Run(() =>
            {
                try
                {
                    stream.Dispose();
                }
                catch
                {
                    // best effort
                }
            });
        }
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
        _disposed = true;

        // Detach the LibVLC callbacks before tearing the player down, so none fire against a disposed
        // MediaPlayer/LibVLC.
        if (MediaPlayer is { } mp)
        {
            if (_onTimeChanged is not null) mp.TimeChanged -= _onTimeChanged;
            if (_onEndReached is not null) mp.EndReached -= _onEndReached;
            if (_onPlaying is not null) mp.Playing -= _onPlaying;
            if (_onPaused is not null) mp.Paused -= _onPaused;
            if (_onError is not null) mp.EncounteredError -= _onError;
        }

        _seekCts?.Cancel();

        StopPlayback();
        MediaPlayer?.Dispose();
        _libVlc?.Dispose();
    }
}
