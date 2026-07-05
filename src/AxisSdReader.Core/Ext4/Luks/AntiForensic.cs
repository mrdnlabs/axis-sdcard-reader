using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AxisSdReader.Core.Ext4.Luks;

/// <summary>
/// LUKS anti-forensic information splitter — the merge (reconstruct) direction only. A keyslot stores
/// the master key spread across <c>stripes</c> hash-diffused blocks so that wiping any part destroys it;
/// merging folds those blocks back into the master key.
/// </summary>
internal static class AntiForensic
{
    /// <summary>Reconstructs the key from its split material (<paramref name="stripes"/> blocks of
    /// <paramref name="keyBytes"/> each).</summary>
    public static byte[] Merge(ReadOnlySpan<byte> material, int keyBytes, int stripes, string hashSpec)
    {
        var buffer = new byte[keyBytes];
        for (var i = 0; i < stripes - 1; i++)
        {
            XorInPlace(buffer, material.Slice(i * keyBytes, keyBytes));
            Diffuse(buffer, hashSpec);
        }

        var result = new byte[keyBytes];
        var last = material.Slice((stripes - 1) * keyBytes, keyBytes);
        for (var k = 0; k < keyBytes; k++)
        {
            result[k] = (byte)(buffer[k] ^ last[k]);
        }

        return result;
    }

    /// <summary>The LUKS diffuse: replace each digest-sized chunk with Hash(BE32(chunkIndex) || chunk).</summary>
    private static void Diffuse(Span<byte> block, string hashSpec)
    {
        var digestSize = DigestSize(hashSpec);
        Span<byte> input = stackalloc byte[4 + 64]; // BE32 index + up to a 64-byte (SHA-512) chunk
        Span<byte> digest = stackalloc byte[64];

        var offset = 0;
        var index = 0u;
        while (offset < block.Length)
        {
            var chunk = Math.Min(digestSize, block.Length - offset);
            BinaryPrimitives.WriteUInt32BigEndian(input, index);
            block.Slice(offset, chunk).CopyTo(input[4..]);
            Hash(hashSpec, input[..(4 + chunk)], digest[..digestSize]);
            digest[..chunk].CopyTo(block.Slice(offset, chunk));
            offset += chunk;
            index++;
        }
    }

    private static void Hash(string hashSpec, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        _ = hashSpec.ToLowerInvariant() switch
        {
            "sha1" => SHA1.HashData(data, destination),
            "sha256" => SHA256.HashData(data, destination),
            "sha512" => SHA512.HashData(data, destination),
            _ => throw new NotSupportedException($"Unsupported LUKS hash '{hashSpec}'."),
        };
    }

    private static int DigestSize(string hashSpec) => hashSpec.ToLowerInvariant() switch
    {
        "sha1" => 20,
        "sha256" => 32,
        "sha512" => 64,
        _ => throw new NotSupportedException($"Unsupported LUKS hash '{hashSpec}'."),
    };

    private static void XorInPlace(Span<byte> a, ReadOnlySpan<byte> b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            a[i] ^= b[i];
        }
    }
}
