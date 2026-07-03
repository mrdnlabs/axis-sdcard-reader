using System.IO;
using System.Windows.Threading;
using AxisSdReader.App.Services;
using AxisSdReader.Core.Axis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

namespace AxisSdReader.App.ViewModels;

/// <summary>
/// Plays a recording session as a sequence of MKV chunks streamed straight off the card.
/// Maintains a session-wide timeline: each chunk contributes its duration, seeks map to
/// (chunk, offset), and chunk boundaries advance automatically on EndReached.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly Task _initTask;
    private readonly Dispatcher _dispatcher;
    private LibVLC? _libVlc;

    private OpenCard? _card;
    private Recording? _recording;
    private TimeSpan[] _chunkStarts = [];
    private int _currentChunk = -1;
    private Stream? _currentStream;
    private MediaInput? _currentInput;
    private Media? _currentMedia;
    private bool _suppressSliderSeek;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private bool _hasRecording;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _sessionLengthSeconds;

    [ObservableProperty]
    private double _sessionPositionSeconds;

    [ObservableProperty]
    private string _positionText = "";

    [ObservableProperty]
    private float _playbackRate = 1.0f;

    /// <summary>Null until the LibVLC engine finishes initializing on a background thread
    /// (first launch generates the libvlc plugin cache, which can take many seconds).</summary>
    [ObservableProperty]
    private MediaPlayer? _mediaPlayer;

    public float[] AvailableRates { get; } = [0.5f, 1.0f, 2.0f, 4.0f, 8.0f];

    public PlayerViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _initTask = Task.Run(InitializeEngine);
    }

    private void InitializeEngine()
    {
        var libVlc = new LibVLC("--no-osd");
        var player = new MediaPlayer(libVlc) { EnableHardwareDecoding = true };

        // VLC events arrive on VLC threads: never call back into libvlc from the handler
        // itself, marshal to the UI thread first.
        player.TimeChanged += (_, e) => _dispatcher.BeginInvoke(() => OnTimeChanged(e.Time));
        player.EndReached += (_, _) => _dispatcher.BeginInvoke(OnChunkEnded);
        player.Playing += (_, _) => _dispatcher.BeginInvoke(() => IsPlaying = true);
        player.Paused += (_, _) => _dispatcher.BeginInvoke(() => IsPlaying = false);
        player.Stopped += (_, _) => _dispatcher.BeginInvoke(() => IsPlaying = false);

        _dispatcher.Invoke(() =>
        {
            _libVlc = libVlc;
            MediaPlayer = player; // VideoView picks this up through the binding
        });
    }

    /// <summary>Loads a recording whose chunk metadata is already populated.</summary>
    public async Task Load(OpenCard card, Recording recording)
    {
        await _initTask; // engine ready (no-op after first use)
        Stop();

        _card = card;
        _recording = recording;

        // Timeline: cumulative chunk start offsets. Chunks without a known duration
        // contribute zero length (still playable, timeline is then approximate).
        _chunkStarts = new TimeSpan[recording.Chunks.Count];
        var cursor = TimeSpan.Zero;
        for (var i = 0; i < recording.Chunks.Count; i++)
        {
            _chunkStarts[i] = cursor;
            cursor += recording.Chunks[i].Duration ?? TimeSpan.Zero;
        }

        SessionLengthSeconds = Math.Max(1, cursor.TotalSeconds);
        SessionPositionSeconds = 0;
        Title = $"{recording.Id.Raw}  ({recording.Chunks.Count} chunk{(recording.Chunks.Count == 1 ? "" : "s")})";
        HasRecording = recording.Chunks.Count > 0;
        UpdatePositionText(TimeSpan.Zero);

        if (HasRecording)
        {
            PlayChunk(0, TimeSpan.Zero);
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_recording is null || MediaPlayer is null)
        {
            return;
        }

        if (MediaPlayer.IsPlaying)
        {
            MediaPlayer.Pause();
        }
        else if (_currentMedia is not null)
        {
            MediaPlayer.Play();
        }
        else if (_recording.Chunks.Count > 0)
        {
            PlayChunk(0, TimeSpan.Zero);
        }
    }

    [RelayCommand]
    public void Stop()
    {
        MediaPlayer?.Stop();
        DisposeCurrentMedia();
        _currentChunk = -1;
        IsPlaying = false;
        SessionPositionSeconds = 0;
    }

    /// <summary>Seeks within the whole session (slider drag / click).</summary>
    public void SeekSession(double seconds)
    {
        if (_recording is null || _suppressSliderSeek)
        {
            return;
        }

        var target = TimeSpan.FromSeconds(seconds);
        var chunk = FindChunkAt(target);
        var offset = target - _chunkStarts[chunk];

        if (chunk == _currentChunk && _currentMedia is not null && MediaPlayer is not null)
        {
            MediaPlayer.Time = (long)offset.TotalMilliseconds;
        }
        else
        {
            PlayChunk(chunk, offset);
        }
    }

    partial void OnPlaybackRateChanged(float value) => MediaPlayer?.SetRate(value);

    private int FindChunkAt(TimeSpan position)
    {
        for (var i = _chunkStarts.Length - 1; i >= 0; i--)
        {
            if (position >= _chunkStarts[i])
            {
                return i;
            }
        }

        return 0;
    }

    private void PlayChunk(int index, TimeSpan offset)
    {
        if (_card is null || _recording is null || MediaPlayer is null || _libVlc is null ||
            index < 0 || index >= _recording.Chunks.Count)
        {
            return;
        }

        MediaPlayer.Stop();
        DisposeCurrentMedia();

        _currentChunk = index;
        _currentStream = _card.OpenChunk(_recording.Chunks[index]);
        _currentInput = new StreamMediaInput(_currentStream);
        _currentMedia = new Media(_libVlc, _currentInput);

        MediaPlayer.Play(_currentMedia);
        MediaPlayer.SetRate(PlaybackRate);

        if (offset > TimeSpan.Zero)
        {
            MediaPlayer.Time = (long)offset.TotalMilliseconds;
        }
    }

    private void OnChunkEnded()
    {
        if (_recording is null)
        {
            return;
        }

        var next = _currentChunk + 1;
        if (next < _recording.Chunks.Count)
        {
            PlayChunk(next, TimeSpan.Zero);
        }
        else
        {
            Stop();
            SessionPositionSeconds = SessionLengthSeconds;
        }
    }

    private void OnTimeChanged(long chunkTimeMs)
    {
        if (_currentChunk < 0 || _currentChunk >= _chunkStarts.Length)
        {
            return;
        }

        var position = _chunkStarts[_currentChunk] + TimeSpan.FromMilliseconds(chunkTimeMs);
        _suppressSliderSeek = true;
        SessionPositionSeconds = position.TotalSeconds;
        _suppressSliderSeek = false;
        UpdatePositionText(position);
    }

    private void UpdatePositionText(TimeSpan position)
    {
        var total = TimeSpan.FromSeconds(SessionLengthSeconds);
        PositionText = $"{position:hh\\:mm\\:ss} / {total:hh\\:mm\\:ss}";
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

    public void Dispose()
    {
        MediaPlayer?.Stop();
        DisposeCurrentMedia();
        MediaPlayer?.Dispose();
        _libVlc?.Dispose();
    }
}
