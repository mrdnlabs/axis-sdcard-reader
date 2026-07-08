using System.IO;
using AxisSdReader.Core;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Ext4;
using DiscUtils;

namespace AxisSdReader.App.Services;

/// <summary>
/// An opened card (physical device with volume protection, or an image file) plus its
/// indexed Axis content. All card I/O in the app funnels through <see cref="RunExclusive{T}"/>
/// because DiscUtils streams share one underlying device stream and are not thread-safe.
/// </summary>
public sealed class OpenCard : IDisposable
{
    private readonly SdCardSession? _session;   // physical device (owns reader + guard)
    private readonly CardReader _reader;        // image files: owned directly
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly CancellationTokenSource _teardownCts = new(); // cancels long ops (index) on dispose
    private volatile bool _disposed;

    private OpenCard(SdCardSession? session, CardReader reader, string description, bool isPhysicalDevice,
        long? capacityBytes)
    {
        _session = session;
        _reader = reader;
        Description = description;
        IsPhysicalDevice = isPhysicalDevice;
        CapacityBytes = capacityBytes;
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

    public DiscFileSystem? FileSystem => _reader.FileSystem;

    /// <summary>ext4 volume label (Axis cameras write "Axis").</summary>
    public string? VolumeLabel => _reader.FileSystem?.VolumeLabel;

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

    public static OpenCard FromDevice(int diskNumber, string description, long? capacityBytes = null,
        char[]? passphrase = null)
    {
        var session = SdCardSession.Open(diskNumber, passphrase: passphrase);
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

    /// <summary>Runs a card I/O operation with exclusive access, off the calling thread.</summary>
    public async Task<T> RunExclusive<T>(Func<T> operation)
    {
        // If the card was (or is being) torn down, cancel cleanly instead of throwing
        // ObjectDisposedException off a disposed lock — a card pulled mid-operation is expected, not an error.
        ThrowIfDisposed();

        // ConfigureAwait(false): the lock release must run off the UI thread. OpenChunk takes this same
        // lock with a synchronous Wait() on the UI thread, so if the release continuation were marshaled
        // back to a blocked UI thread the two would deadlock.
        await WaitLockAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await Task.Run(operation).ConfigureAwait(false);
        }
        finally
        {
            ReleaseLock();
        }
    }

    /// <summary>Runs an async card I/O operation with exclusive access held for its whole duration
    /// (e.g. copy chunks off the card then run FFmpeg), so nothing else touches the shared stream.</summary>
    public async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await WaitLockAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Start the operation on the thread pool (not the calling UI thread) so none of its internal
            // continuations can be marshaled back to a UI thread that may be blocked in OpenChunk's Wait().
            return await Task.Run(() => operation(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseLock();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new OperationCanceledException("The card was closed.");
        }
    }

    /// <summary>Waits on the I/O lock, translating a dispose that races this call into a cancellation.</summary>
    private async Task WaitLockAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new OperationCanceledException("The card was closed.");
        }
    }

    /// <summary>Releases the I/O lock, tolerating a dispose that already tore it down.</summary>
    private void ReleaseLock()
    {
        try
        {
            _ioLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // Card was disposed while this operation was running; nothing to release.
        }
    }

    public async Task<AxisCard> IndexAsync(Action<int>? progress = null)
    {
        var fs = FileSystem ?? throw new InvalidOperationException("No filesystem is open.");
        // Observe the teardown token so closing the card mid-index unwinds the walk promptly and frees the
        // I/O lock, letting Dispose acquire it before tearing down the shared stream.
        Index = await RunExclusive(() => AxisCardIndexer.Index(fs, progress, _teardownCts.Token));
        return Index;
    }

    public Task LoadMetadataAsync(Recording recording)
    {
        var fs = FileSystem ?? throw new InvalidOperationException("No filesystem is open.");
        return RunExclusive<object?>(() =>
        {
            recording.LoadChunkMetadata(fs);
            return null;
        });
    }

    /// <summary>Loads chunk metadata for many recordings in one exclusive pass (a day's worth).</summary>
    public Task LoadMetadataAsync(IEnumerable<Recording> recordings)
    {
        var fs = FileSystem ?? throw new InvalidOperationException("No filesystem is open.");
        var list = recordings.ToList();
        return RunExclusive<object?>(() =>
        {
            foreach (var recording in list)
            {
                recording.LoadChunkMetadata(fs);
            }

            return null;
        });
    }

    /// <summary>
    /// Opens a chunk for playback. VLC reads the returned stream from its own demux thread, so the
    /// stream is wrapped to take the same exclusive I/O lock on every read/seek — the underlying
    /// DiscUtils streams all share one device stream and are not thread-safe, so playback reads must be
    /// serialized against indexing/metadata/export just like every other card operation.
    /// </summary>
    public Stream OpenChunk(RecordingChunk chunk)
    {
        ThrowIfDisposed();
        var fs = FileSystem ?? throw new InvalidOperationException("No filesystem is open.");
        _ioLock.Wait();
        try
        {
            var inner = fs.OpenFile(chunk.Path, FileMode.Open, FileAccess.Read);
            return new SynchronizedCardStream(inner, _ioLock);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public void Dispose()
    {
        // Mark disposed FIRST so any RunExclusive/OpenChunk caller that races this teardown cancels cleanly
        // rather than throwing ObjectDisposedException off the disposed lock.
        _disposed = true;

        // Cancel a long in-flight index so it unwinds and frees the I/O lock promptly (the export is
        // cancelled by the caller; this covers the non-cancellable-looking index path).
        _teardownCts.Cancel();

        // Then serialize teardown BEHIND any in-flight exclusive I/O (indexing / metadata / a running
        // export), so the shared, non-thread-safe DiscUtils/decrypt streams are never disposed out from
        // under an active read — on ANY teardown path (card removal, window close, shutdown). Callers
        // cancel long operations (export) first, so this wait is normally instant; it is bounded so a stuck
        // operation cannot hang shutdown forever.
        var entered = false;
        try
        {
            entered = _ioLock.Wait(TimeSpan.FromSeconds(20));
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            if (_session is not null)
            {
                _session.Dispose(); // disposes reader + releases volume locks
            }
            else
            {
                _reader.Dispose();
            }
        }
        finally
        {
            if (entered)
            {
                try { _ioLock.Release(); }
                catch (ObjectDisposedException) { }
            }

            _ioLock.Dispose();
            _teardownCts.Dispose();
        }
    }
}

/// <summary>
/// Wraps a card file stream so that every read/seek is serialized through the card's I/O lock. This lets
/// VLC read a chunk from its own thread while indexing/metadata/export I/O runs elsewhere without the
/// underlying shared, non-thread-safe DiscUtils/device stream ever being touched by two threads at once.
/// Read-only; the lock is a synchronous <see cref="SemaphoreSlim.Wait()"/> (fast when uncontended).
/// </summary>
internal sealed class SynchronizedCardStream : Stream
{
    private readonly Stream _inner;
    private readonly SemaphoreSlim _ioLock;

    public SynchronizedCardStream(Stream inner, SemaphoreSlim ioLock)
    {
        _inner = inner;
        _ioLock = ioLock;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            _ioLock.Wait();
            try { return _inner.Length; }
            finally { _ioLock.Release(); }
        }
    }

    public override long Position
    {
        get
        {
            _ioLock.Wait();
            try { return _inner.Position; }
            finally { _ioLock.Release(); }
        }
        set
        {
            _ioLock.Wait();
            try { _inner.Position = value; }
            finally { _ioLock.Release(); }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _ioLock.Wait();
        try { return _inner.Read(buffer, offset, count); }
        finally { _ioLock.Release(); }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _ioLock.Wait();
        try { return _inner.Seek(offset, origin); }
        finally { _ioLock.Release(); }
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // A deferred playback-stream disposal can race card teardown, by which point the lock is
            // already gone. If so, just release the inner stream without serialization.
            var entered = false;
            try
            {
                _ioLock.Wait();
                entered = true;
            }
            catch (ObjectDisposedException)
            {
            }

            try { _inner.Dispose(); }
            finally { if (entered) _ioLock.Release(); }
        }

        base.Dispose(disposing);
    }
}
