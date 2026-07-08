using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AxisSdReader.Core.Ext4.Luks;

/// <summary>
/// AES-XTS decryption — the block cipher mode LUKS uses for both its payload and its key material —
/// built on the BCL's AES-ECB primitive. Decrypt-only: this app never encrypts. Uses the LUKS
/// "plain64" tweak (the 64-bit sector number, little-endian, as the 128-bit tweak input) over
/// 512-byte sectors.
/// </summary>
internal sealed class XtsAes : IDisposable
{
    internal const int SectorSize = 512;
    private const int BlockSize = 16;
    private const int BlocksPerSector = SectorSize / BlockSize;

    private readonly Aes _dataKey;   // key1 — the data blocks
    private readonly Aes _tweakKey;  // key2 — the tweak

    /// <param name="key">XTS key; first half is the data key, second half the tweak key
    /// (e.g. a 64-byte key = two AES-256 keys).</param>
    public XtsAes(ReadOnlySpan<byte> key)
    {
        var half = key.Length / 2;
        _dataKey = CreateEcb(key[..half]);
        _tweakKey = CreateEcb(key[half..]);
    }

    private static Aes CreateEcb(ReadOnlySpan<byte> key)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        // The Key setter clones its argument internally (cleared on aes.Dispose), so zero our transient
        // copy immediately rather than abandoning raw key material to the GC heap.
        var k = key.ToArray();
        try
        {
            aes.Key = k;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(k);
        }

        return aes;
    }

    /// <summary>Decrypts one 512-byte sector: <paramref name="cipher"/> in, plaintext out to <paramref name="plain"/>.</summary>
    public void DecryptSector(ulong sectorNumber, ReadOnlySpan<byte> cipher, Span<byte> plain)
    {
        // tweak_0 = AES-ECB-encrypt(tweakKey, plain64(sector)); subsequent block tweaks multiply by alpha.
        Span<byte> ivInput = stackalloc byte[BlockSize];
        BinaryPrimitives.WriteUInt64LittleEndian(ivInput, sectorNumber); // high 8 bytes stay zero

        Span<byte> tweaks = stackalloc byte[SectorSize];
        _tweakKey.EncryptEcb(ivInput, tweaks[..BlockSize], PaddingMode.None);
        for (var j = 1; j < BlocksPerSector; j++)
        {
            GfMulAlpha(tweaks.Slice((j - 1) * BlockSize, BlockSize), tweaks.Slice(j * BlockSize, BlockSize));
        }

        // plain = ECB-decrypt(cipher XOR tweaks) XOR tweaks. ECB decrypts each 16-byte block independently,
        // so the whole 512-byte sector can go through one call.
        Span<byte> tmp = stackalloc byte[SectorSize];
        Xor(cipher, tweaks, tmp);
        _dataKey.DecryptEcb(tmp, plain, PaddingMode.None);
        XorInPlace(plain, tweaks);
    }

    /// <summary>Multiply a 128-bit little-endian value by the primitive element alpha (x) in GF(2^128).</summary>
    private static void GfMulAlpha(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var carry = 0;
        for (var k = 0; k < BlockSize; k++)
        {
            var b = src[k];
            dst[k] = (byte)(((b << 1) | carry) & 0xFF);
            carry = (b >> 7) & 1;
        }

        if (carry != 0)
        {
            dst[0] ^= 0x87; // reduction polynomial for GF(2^128)
        }
    }

    private static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst)
    {
        for (var i = 0; i < a.Length; i++)
        {
            dst[i] = (byte)(a[i] ^ b[i]);
        }
    }

    private static void XorInPlace(Span<byte> a, ReadOnlySpan<byte> b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            a[i] ^= b[i];
        }
    }

    public void Dispose()
    {
        _dataKey.Dispose();
        _tweakKey.Dispose();
    }
}
