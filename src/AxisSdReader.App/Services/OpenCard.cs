using System.IO;
using AxisSdReader.Core;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Disk;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Media;

namespace AxisSdReader.App.Services;

/// <summary>
/// An opened card (physical device with volume protection, or an image file) plus its indexed Axis content.
/// All card I/O funnels through one <see cref="CardIoGate"/>, because the DiscUtils streams share one
/// underlying device stream and are not thread-safe. The gate is taken per read (playback, export streaming)
/// or per chunk (metadata), never for long stretches, so nothing — least of all the UI thread — waits long.
/// </summary>
public sealed class OpenCard : IDisposable
{
    private readonly SdCardSession? _session;   // physical device (owns reader + guard)
    private readonly CardReader _reader;        // image files: owned directly
    private readonly CancellationTokenSource _teardownCts = new(); // cancels long ops (index, metadata) on dispose
    private volatile bool _disposed;

    private OpenCard(SdCardSession? session, CardReader reader, string description, bool isPhysicalDevice,
        long? capacityBytes)
    {
        _session = session;
        _reader = reader;
        Description = description;
        IsPhysicalDevice = isPhysicalDevice;
        CapacityBytes = capacityBytes;
        if (session is not null)
        {
            session.MediaLost += () => MediaLost?.Invoke();
            session.ProtectionChanged += () => ProtectionChanged?.Invoke();
        }
    }

    /// <summary>Friendly card name — device model (e.g. "SanDisk Extreme") or image file name.</summary>
    public string Description { get; }

    public bool IsPhysicalDevice { get; }

    /// <summary>Physical disk number for a device card, else null (image mode).</summary>
    public int? DiskNumber { get; private init; }

    /// <summary>Card capacity in bytes, when known.</summary>
    public long? CapacityBytes { get; }

    public CardOpenStatus Status => _reader.Status;

    public string? FailureDetail => _reader.FailureDetail;

    public Ext4Volume? FileSystem => _reader.FileSystem;

    /// <summary>ext4 volume label (Axis cameras write "Axis").</summary>
    public string? VolumeLabel => _reader.VolumeLabel;

    public IReadOnlyList<string> ProtectionLog => _session?.ProtectionLog ?? [];

    /// <summary>
    /// For a physical card, true only when every volume was locked (genuinely protected). For an image
    /// file there is nothing to protect, so this is true. When false, the UI must not claim the card is
    /// locked/read-only-safe.
    /// </summary>
    public bool FullyProtected => _session?.FullyProtected ?? true;

    /// <summary>True when the card was LUKS-encrypted and had to be unlocked with a passphrase to read.</summary>
    public bool IsEncrypted => _reader.IsEncrypted;

    public AxisCard? Index { get; private set; }

    /// <summary>The gate every card read goes through.</summary>
    public CardIoGate Gate { get; } = new();

    /// <summary>The physical card was pulled or swapped (raised on a pool thread).</summary>
    public event Action? MediaLost;

    /// <summary>The protection state changed (a late volume was locked, or could not be); pool thread.</summary>
    public event Action? ProtectionChanged;

    public static OpenCard FromDevice(int diskNumber, string description, long? capacityBytes = null,
        char[]? passphrase = null, VolumeGuard? adoptGuard = null)
    {
        var session = SdCardSession.Open(diskNumber, passphrase: passphrase, adoptGuard: adoptGuard);
        return new OpenCard(session, session.Card, description, isPhysicalDevice: true, capacityBytes)
        {
            DiskNumber = diskNumber,
        };
    }

    public static OpenCard FromImage(string imagePath, char[]? passphrase = null)
    {
        var reader = CardReader.OpenImage(imagePath, passphrase);
        long? size = null;
        try
        {
            size = new FileInfo(imagePath).Length;
        }
        catch
        {
            // best effort
        }

        return new OpenCard(null, reader, Path.GetFileName(imagePath), isPhysicalDevice: false, size);
    }

    /// <summary>Begins watching a physical card for removal/swap and late volumes (no-op for images).</summary>
    public void StartMonitoring() => _session?.StartMonitoring();

    /// <summary>Takes the card's volume lock out of this (failed or locked) open, to hand to the next attempt.</summary>
    public VolumeGuard? DetachGuard() => _session?.DetachGuard();

    /// <summary>Runs a card I/O operation with exclusive access, on the thread pool.</summary>
    public Task<T> RunExclusive<T>(Func<T> operation)
    {
        // A card pulled (or closed) mid-operation is expected: cancel cleanly rather than throw.
        ThrowIfDisposed();
        return Gate.RunAsync(operation);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new CardClosedException();
        }
    }

    public async Task<AxisCard> IndexAsync(Action<int>? progress = null)
    {
        _ = FileSystem ?? throw new InvalidOperationException("No filesystem is open.");
        // Observe the teardown token so closing the card mid-index unwinds the walk promptly and frees the
        // gate, letting Dispose acquire it before tearing down the shared stream.
        Index = await RunExclusive(() => AxisCardIndexer.Index(_reader, progress, _teardownCts.Token));
        return Index;
    }

    /// <summary>Loads one recording's chunk metadata, taking the gate per chunk.</summary>
    public Task LoadMetadataAsync(Recording recording, CancellationToken cancellationToken = default) =>
        LoadMetadataAsync([recording], cancellationToken);

    /// <summary>Loads chunk metadata for many recordings (e.g. a day's worth), taking the gate per chunk so
    /// playback keeps reading in between. Cancellable between chunks; progress so far is kept.</summary>
    public Task LoadMetadataAsync(IEnumerable<Recording> recordings, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var fs = FileSystem ?? throw new InvalidOperationException("No filesystem is open.");
        var list = recordings.Where(r => !r.MetadataLoaded).ToList();
        if (list.Count == 0)
        {
            return Task.CompletedTask;
        }

        var token = _teardownCts.Token;
        return Task.Run(() =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            foreach (var recording in list)
            {
                recording.LoadChunkMetadata(fs, Gate, linked.Token);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Opens a chunk for reading from any thread (VLC's demux thread, the loopback media server). Returns at
    /// once: the file is opened on first read, by the reading thread, under the gate — so this never blocks
    /// the UI thread behind other card I/O.
    /// </summary>
    public Stream OpenChunk(RecordingChunk chunk)
    {
        ThrowIfDisposed();
        var fs = FileSystem ?? throw new InvalidOperationException("No filesystem is open.");
        return new SynchronizedCardStream(Gate, () => Recording.OpenChunk(fs, chunk), chunk.SizeBytes);
    }

    /// <summary>Loopback media sources for chunks (named c0.mkv, c1.mkv, … in order).</summary>
    public IReadOnlyList<MediaSource> MediaSources(IReadOnlyList<RecordingChunk> chunks) =>
        chunks.Select((chunk, i) => new MediaSource($"c{i}.mkv", chunk.SizeBytes, () => OpenChunk(chunk))).ToList();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Mark disposed first so new callers cancel cleanly; cancel long operations (index, metadata) so they
        // let go of the gate; then tear down behind any in-flight read — bounded, so a stuck read can't hang
        // shutdown forever.
        _disposed = true;
        _teardownCts.Cancel();
        Gate.Close(() =>
        {
            if (_session is not null)
            {
                _session.Dispose(); // disposes reader + releases volume locks
            }
            else
            {
                _reader.Dispose();
            }
        }, TimeSpan.FromSeconds(20));
        _teardownCts.Dispose();
    }
}
