namespace AxisSdReader.Core.Media;

/// <summary>Thrown when card I/O is attempted after the card was closed (removed, or the app is shutting down).
/// Derives from <see cref="OperationCanceledException"/>: a card pulled mid-operation is an expected
/// cancellation, not an error.</summary>
public sealed class CardClosedException : OperationCanceledException
{
    public CardClosedException()
        : base("The card was closed.")
    {
    }
}

/// <summary>
/// Serializes access to an open card's stream stack, which is shared by every file on the card and is not
/// thread-safe: the DiscUtils ext4 reader, the LUKS decrypting stream, the block cache and the raw device
/// all sit on one stream. Every card read must be inside <see cref="Enter"/> / <see cref="Exit"/>.
/// <see cref="Close"/> fences teardown behind any in-flight read, after which every entry fails fast with
/// <see cref="CardClosedException"/> rather than touching disposed streams.
/// </summary>
public sealed class CardIoGate
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _closed;

    public bool IsClosed => _closed;

    /// <summary>Acquires exclusive card access (blocking). Throws <see cref="CardClosedException"/> once closed.</summary>
    public void Enter()
    {
        if (_closed)
        {
            throw new CardClosedException();
        }

        _lock.Wait();
        if (_closed)
        {
            _lock.Release();
            throw new CardClosedException();
        }
    }

    /// <summary>Acquires exclusive card access without blocking a thread.</summary>
    public async Task EnterAsync(CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            throw new CardClosedException();
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_closed)
        {
            _lock.Release();
            throw new CardClosedException();
        }
    }

    /// <summary>Like <see cref="Enter"/> but returns false instead of throwing when the card is closed.</summary>
    public bool TryEnter()
    {
        if (_closed)
        {
            return false;
        }

        _lock.Wait();
        if (_closed)
        {
            _lock.Release();
            return false;
        }

        return true;
    }

    public void Exit() => _lock.Release();

    /// <summary>Runs <paramref name="operation"/> on the thread pool while holding the gate.</summary>
    public async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(operation, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Marks the card closed, waits (bounded) for an in-flight read to finish, then runs
    /// <paramref name="teardown"/> while holding the gate so no read can overlap it. Waiters queued behind the
    /// teardown wake up, see the closed flag and bail out. The semaphore is deliberately never disposed: it
    /// holds no OS handle, and disposing it would turn those waiters' clean bail-out into a crash.
    /// </summary>
    public void Close(Action teardown, TimeSpan timeout)
    {
        _closed = true;
        var entered = _lock.Wait(timeout);
        try
        {
            teardown();
        }
        finally
        {
            if (entered)
            {
                _lock.Release();
            }
        }
    }
}

/// <summary>
/// A read-only view of one card file in which every read and seek holds the card's <see cref="CardIoGate"/>,
/// so the file can be read from any thread (VLC's demux thread, the loopback media server) while other card
/// I/O runs elsewhere. The underlying file is opened lazily, on the first read, by whichever thread reads
/// first — so creating one never blocks the UI thread behind a long metadata load. Length is known up front
/// from the index and the position is tracked locally, so neither needs the gate.
/// </summary>
public sealed class SynchronizedCardStream : Stream
{
    private readonly CardIoGate _gate;
    private readonly Func<Stream> _open;
    private readonly long _length;
    private Stream? _inner;
    private long _position;
    private bool _disposed;

    /// <param name="gate">The card's I/O gate.</param>
    /// <param name="open">Opens the underlying card file; invoked once, under the gate.</param>
    /// <param name="length">The file length from the card index.</param>
    public SynchronizedCardStream(CardIoGate gate, Func<Stream> open, long length)
    {
        _gate = gate;
        _open = open;
        _length = Math.Max(0, length);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || _position >= _length)
        {
            return 0;
        }

        if (buffer.Length > _length - _position)
        {
            buffer = buffer[..(int)(_length - _position)];
        }

        _gate.Enter();
        try
        {
            var inner = _inner ??= _open();
            inner.Position = _position;
            var read = inner.Read(buffer);
            _position += read;
            return read;
        }
        finally
        {
            _gate.Exit();
        }
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
        {
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            // Once the card is closed its whole stream stack is gone; the inner DiscUtils file stream holds no
            // OS resources of its own, so there is nothing to release and nothing must touch it.
            if (_inner is not null && _gate.TryEnter())
            {
                try
                {
                    _inner.Dispose();
                }
                catch
                {
                    // best effort
                }
                finally
                {
                    _gate.Exit();
                }
            }

            _inner = null;
        }

        base.Dispose(disposing);
    }
}
