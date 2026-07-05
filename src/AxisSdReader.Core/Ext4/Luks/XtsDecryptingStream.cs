using System.Buffers;
using DiscUtils.Streams;

namespace AxisSdReader.Core.Ext4.Luks;

/// <summary>
/// Read-only stream exposing the plaintext of a LUKS payload: wraps the ciphertext region and decrypts
/// (AES-XTS) the covering 512-byte sectors on each read. Sector tweaks are 0-based from the start of the
/// payload (LUKS1 maps the payload with iv_offset 0). The card is never written; decryption happens only
/// on the way out.
/// </summary>
internal sealed class XtsDecryptingStream : SparseStream
{
    private const int SectorSize = XtsAes.SectorSize;

    private readonly Stream _cipher; // exactly the payload ciphertext region
    private readonly XtsAes _xts;
    private readonly long _length;
    private long _position;

    public XtsDecryptingStream(Stream cipherPayload, XtsAes xts)
    {
        _cipher = cipherPayload;
        _xts = xts;
        _length = cipherPayload.Length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Position cannot be negative.");
            }

            _position = value;
        }
    }

    public override IEnumerable<StreamExtent> Extents => [new StreamExtent(0, _length)];

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length || buffer.IsEmpty)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, _length - _position);

        var firstSector = _position / SectorSize;
        var lastSector = (_position + toRead - 1) / SectorSize;
        var sectorCount = (int)(lastSector - firstSector + 1);
        var regionStart = firstSector * SectorSize;
        var regionBytes = sectorCount * SectorSize;

        var cipherBuf = ArrayPool<byte>.Shared.Rent(regionBytes);
        var plainBuf = ArrayPool<byte>.Shared.Rent(regionBytes);
        try
        {
            ReadCipher(regionStart, cipherBuf.AsSpan(0, regionBytes));

            for (var i = 0; i < sectorCount; i++)
            {
                _xts.DecryptSector(
                    (ulong)(firstSector + i),
                    cipherBuf.AsSpan(i * SectorSize, SectorSize),
                    plainBuf.AsSpan(i * SectorSize, SectorSize));
            }

            var skip = (int)(_position - regionStart);
            plainBuf.AsSpan(skip, toRead).CopyTo(buffer);
            _position += toRead;
            return toRead;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cipherBuf);
            ArrayPool<byte>.Shared.Return(plainBuf);
        }
    }

    private void ReadCipher(long offset, Span<byte> dest)
    {
        _cipher.Position = offset;
        var total = 0;
        while (total < dest.Length)
        {
            var n = _cipher.Read(dest[total..]);
            if (n == 0)
            {
                dest[total..].Clear(); // beyond the ciphertext end — treat as zero (should not happen for aligned payloads)
                break;
            }

            total += n;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
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

    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cipher.Dispose();
            _xts.Dispose();
        }

        base.Dispose(disposing);
    }
}
