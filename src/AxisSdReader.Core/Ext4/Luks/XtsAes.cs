using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace AxisSdReader.Core.Ext4.Luks;

/// <summary>
/// AES-XTS decryption — the block cipher mode LUKS uses for both its payload and its key material —
/// built on the BCL's AES-ECB primitive. Decrypt-only: this app never encrypts. Uses the LUKS
/// "plain64" tweak (the 64-bit sector number, little-endian, as the 128-bit tweak input) over
/// 512-byte sectors.
/// <para>The two AES keys are imported once, as reusable ECB transforms, and runs of sectors are processed
/// in a single pass: the BCL's one-shot <c>EncryptEcb</c>/<c>DecryptEcb</c> re-import the key — and leave a
/// fresh, never-zeroed copy of it on the heap — on every call, which at two calls per 512-byte sector meant
/// millions of key copies per gigabyte read.</para>
/// </summary>
internal sealed class XtsAes : IDisposable
{
    internal const int SectorSize = 512;
    private const int BlockSize = 16;
    private const int BlocksPerSector = SectorSize / BlockSize;

    private readonly Aes _aes;
    private readonly ICryptoTransform _dataDecryptor;  // key1 — the data blocks
    private readonly ICryptoTransform _tweakEncryptor; // key2 — the tweak

    /// <param name="key">XTS key; first half is the data key, second half the tweak key
    /// (e.g. a 64-byte key = two AES-256 keys).</param>
    public XtsAes(ReadOnlySpan<byte> key)
    {
        var half = key.Length / 2;
        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _dataDecryptor = CreateTransform(key[..half], encrypt: false);
        _tweakEncryptor = CreateTransform(key[half..], encrypt: true);
    }

    private ICryptoTransform CreateTransform(ReadOnlySpan<byte> keyPart, bool encrypt)
    {
        var k = keyPart.ToArray();
        try
        {
            return encrypt ? _aes.CreateEncryptor(k, null) : _aes.CreateDecryptor(k, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(k);
        }
    }

    /// <summary>Decrypts one 512-byte sector: <paramref name="cipher"/> in, plaintext out to <paramref name="plain"/>.</summary>
    public void DecryptSector(ulong sectorNumber, ReadOnlySpan<byte> cipher, Span<byte> plain)
    {
        var c = ArrayPool<byte>.Shared.Rent(SectorSize);
        var p = ArrayPool<byte>.Shared.Rent(SectorSize);
        try
        {
            cipher[..SectorSize].CopyTo(c);
            DecryptSectors(sectorNumber, c, 0, p, 0, 1);
            p.AsSpan(0, SectorSize).CopyTo(plain);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(c);
            ArrayPool<byte>.Shared.Return(p, clearArray: true);
        }
    }

    /// <summary>
    /// Decrypts <paramref name="sectorCount"/> consecutive sectors starting at <paramref name="firstSector"/>,
    /// from <paramref name="cipher"/> at <paramref name="cipherOffset"/> into <paramref name="plain"/> at
    /// <paramref name="plainOffset"/> (the two may not overlap).
    /// </summary>
    public void DecryptSectors(ulong firstSector, byte[] cipher, int cipherOffset, byte[] plain, int plainOffset, int sectorCount)
    {
        if (sectorCount <= 0)
        {
            return;
        }

        var bytes = sectorCount * SectorSize;
        var tweaks = ArrayPool<byte>.Shared.Rent(bytes);
        var scratch = ArrayPool<byte>.Shared.Rent(bytes);
        var ivs = ArrayPool<byte>.Shared.Rent(sectorCount * BlockSize);
        var ivOut = ArrayPool<byte>.Shared.Rent(sectorCount * BlockSize);
        try
        {
            // 1) tweak_0 for every sector in one ECB pass: AES-encrypt(key2, plain64(sector)).
            ivs.AsSpan(0, sectorCount * BlockSize).Clear();
            for (var s = 0; s < sectorCount; s++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(ivs.AsSpan(s * BlockSize), firstSector + (ulong)s); // high 8 bytes stay zero
            }

            Transform(_tweakEncryptor, ivs, sectorCount * BlockSize, ivOut);

            // 2) Expand each sector's 32 block tweaks: tweak_j = tweak_{j-1} * alpha in GF(2^128).
            for (var s = 0; s < sectorCount; s++)
            {
                var sector = tweaks.AsSpan(s * SectorSize, SectorSize);
                var lo = BinaryPrimitives.ReadUInt64LittleEndian(ivOut.AsSpan(s * BlockSize));
                var hi = BinaryPrimitives.ReadUInt64LittleEndian(ivOut.AsSpan(s * BlockSize + 8));
                for (var j = 0; j < BlocksPerSector; j++)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(sector[(j * BlockSize)..], lo);
                    BinaryPrimitives.WriteUInt64LittleEndian(sector[(j * BlockSize + 8)..], hi);
                    var carry = hi >> 63;
                    hi = (hi << 1) | (lo >> 63);
                    lo = (lo << 1) ^ (carry * 0x87); // reduction polynomial x^128 + x^7 + x^2 + x + 1
                }
            }

            // 3) plain = ECB-decrypt(key1, cipher XOR tweak) XOR tweak, for the whole run at once.
            Xor(cipher.AsSpan(cipherOffset, bytes), tweaks.AsSpan(0, bytes), scratch.AsSpan(0, bytes));
            Transform(_dataDecryptor, scratch, bytes, plain, plainOffset);
            Xor(plain.AsSpan(plainOffset, bytes), tweaks.AsSpan(0, bytes), plain.AsSpan(plainOffset, bytes));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tweaks, clearArray: true);
            ArrayPool<byte>.Shared.Return(scratch, clearArray: true);
            ArrayPool<byte>.Shared.Return(ivs, clearArray: true);
            ArrayPool<byte>.Shared.Return(ivOut, clearArray: true);
        }
    }

    private static void Transform(ICryptoTransform transform, byte[] input, int count, byte[] output, int outputOffset = 0)
    {
        var done = 0;
        while (done < count)
        {
            var n = transform.TransformBlock(input, done, count - done, output, outputOffset + done);
            if (n <= 0)
            {
                throw new CryptographicException("AES transform made no progress.");
            }

            done += n;
        }
    }

    private static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> destination)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var width = Vector<byte>.Count;
            for (; i <= a.Length - width; i += width)
            {
                (new Vector<byte>(a[i..]) ^ new Vector<byte>(b[i..])).CopyTo(destination[i..]);
            }
        }

        for (; i < a.Length; i++)
        {
            destination[i] = (byte)(a[i] ^ b[i]);
        }
    }

    public void Dispose()
    {
        _dataDecryptor.Dispose();
        _tweakEncryptor.Dispose();
        _aes.Dispose();
    }
}
