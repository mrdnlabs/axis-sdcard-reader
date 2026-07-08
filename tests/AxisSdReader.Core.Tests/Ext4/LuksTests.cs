using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Ext4.Luks;
using DiscUtils.Streams;

namespace AxisSdReader.Core.Tests.Ext4;

/// <summary>
/// Exercises the LUKS1 unlock pipeline end to end by building a minimal LUKS1 container in-test — with
/// independent AES-XTS encrypt and anti-forensic split helpers — then unlocking it through the real
/// <see cref="LuksVolume"/> code. Because the encrypt side here is written independently of the decrypt
/// side under test, a mismatch in tweak/AF/PBKDF2 logic fails the round trip. (Absolute correctness was
/// additionally verified once against a real cryptsetup-produced Axis card.)
/// </summary>
public class LuksTests
{
    private const int Sector = 512;
    private const int KeyBytes = 64; // AES-256 XTS master key
    private const int Stripes = 4;
    private const string Passphrase = "unlock-me-123";

    [Fact]
    public void UnlocksWithCorrectPassphraseAndDecryptsPayload()
    {
        var (image, plaintext) = BuildLuks1Image(Passphrase);

        var result = LuksVolume.TryUnlock(new MemoryStream(image), Passphrase.ToCharArray());

        Assert.Equal(LuksUnlockStatus.Success, result.Status);
        Assert.NotNull(result.PlaintextStream);

        using var stream = result.PlaintextStream!;
        Assert.Equal(plaintext.Length, stream.Length);

        // Read the whole payload and a mid-stream slice to exercise sector mapping.
        var all = new byte[plaintext.Length];
        stream.Position = 0;
        stream.ReadExactly(all);
        Assert.Equal(plaintext, all);

        var slice = new byte[100];
        stream.Position = 517; // straddles a sector boundary
        stream.ReadExactly(slice);
        Assert.Equal(plaintext.AsSpan(517, 100).ToArray(), slice);
    }

    [Fact]
    public void RejectsWrongPassphrase()
    {
        var (image, _) = BuildLuks1Image(Passphrase);

        var result = LuksVolume.TryUnlock(new MemoryStream(image), "not-the-passphrase".ToCharArray());

        Assert.Equal(LuksUnlockStatus.WrongPassphrase, result.Status);
        Assert.Null(result.PlaintextStream);
    }

    [Fact]
    public void ReportsNotLuksForPlainData()
    {
        var result = LuksVolume.TryUnlock(new MemoryStream(new byte[4096]), Passphrase.ToCharArray());
        Assert.Equal(LuksUnlockStatus.NotLuks, result.Status);
    }

    [Fact]
    public void Luks2CardIsReportedUnsupportedWithoutPromptingForPassphrase()
    {
        // A LUKS2 card (magic + version 2) must be reported as unsupported-encryption up front, NOT as
        // EncryptedNeedsPassphrase (which would pop a passphrase prompt for a card we can't read).
        var image = new byte[2 * 1024 * 1024];
        new byte[] { 0x4C, 0x55, 0x4B, 0x53, 0xBA, 0xBE }.CopyTo(image, 0);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(6), 2); // LUKS version 2

        using var reader = CardReader.Open(new MemoryStream(image), Ownership.None);

        Assert.Equal(CardOpenStatus.Encrypted, reader.Status);
    }

    [Theory]
    [InlineData(6, new byte[] { 0x00, 0x02 })]              // version -> LUKS2 (unsupported)
    [InlineData(108, new byte[] { 0x00, 0x00, 0x03, 0xE7 })] // KeyBytes -> 999 (invalid AES-XTS size)
    [InlineData(252, new byte[] { 0x00, 0x00, 0x00, 0x00 })] // keyslot 0 stripes -> 0
    [InlineData(212, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })] // keyslot 0 iterations -> ~4.3 billion
    public void HostileHeaderFieldsDegradeCleanlyNeverCrash(int offset, byte[] value)
    {
        // A crafted/corrupt LUKS header must never crash, hang, or over-allocate — it degrades to a
        // non-Success result (Unsupported or WrongPassphrase) with no plaintext stream.
        var (image, _) = BuildLuks1Image(Passphrase);
        value.CopyTo(image, offset);

        var result = LuksVolume.TryUnlock(new MemoryStream(image), Passphrase.ToCharArray());

        Assert.NotEqual(LuksUnlockStatus.Success, result.Status);
        Assert.Null(result.PlaintextStream);
    }

    // --- minimal LUKS1 container builder (encrypt side, written independently) ---

    private static (byte[] Image, byte[] Plaintext) BuildLuks1Image(string passphrase)
    {
        const uint kmOffsetSectors = 4;   // key material starts at sector 4
        const uint payloadSectors = 8;    // payload starts at sector 8
        const int payloadLen = 4 * Sector;
        const int slotIterations = 2000;
        const int mkIterations = 2000;

        var rng = new Random(12345);
        var masterKey = RandomBytes(rng, KeyBytes);
        var slotSalt = RandomBytes(rng, 32);
        var mkSalt = RandomBytes(rng, 32);

        var image = new byte[(int)(payloadSectors * Sector) + payloadLen];

        // --- header ---
        new byte[] { 0x4C, 0x55, 0x4B, 0x53, 0xBA, 0xBE }.CopyTo(image, 0);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(6), 1);
        WriteAscii(image, 8, "aes");
        WriteAscii(image, 40, "xts-plain64");
        WriteAscii(image, 72, "sha256");
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(104), payloadSectors);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(108), KeyBytes);
        mkSalt.CopyTo(image, 132);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(164), mkIterations);
        WriteAscii(image, 168, "00000000-0000-0000-0000-000000000000");

        // master-key digest = PBKDF2(masterKey, mkSalt, mkIterations, 20, sha256)
        var mkDigest = Rfc2898DeriveBytes.Pbkdf2(masterKey, mkSalt, mkIterations, HashAlgorithmName.SHA256, 20);
        mkDigest.CopyTo(image, 112);

        // key slot 0 (active); the rest stay disabled (zeroed).
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(208), 0x00AC71F3);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(212), slotIterations);
        slotSalt.CopyTo(image, 216);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(248), kmOffsetSectors);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(252), Stripes);

        // --- key material: AF-split the master key into a sector-aligned buffer, then XTS-encrypt it
        //     with the slot key (the reader decrypts whole sectors, so pad to a full sector). ---
        var split = AfSplit(masterKey, Stripes, rng);
        var materialSectors = (KeyBytes * Stripes + Sector - 1) / Sector;
        var material = new byte[materialSectors * Sector];
        split.CopyTo(material, 0);
        var slotKey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), slotSalt, slotIterations, HashAlgorithmName.SHA256, KeyBytes);
        XtsEncrypt(slotKey, material, startSector: 0);
        material.CopyTo(image, (int)(kmOffsetSectors * Sector));

        // --- payload: known plaintext, XTS-encrypted with the master key (sectors 0-based from payload) ---
        var plaintext = RandomBytes(rng, payloadLen);
        var cipherPayload = (byte[])plaintext.Clone();
        XtsEncrypt(masterKey, cipherPayload, startSector: 0);
        cipherPayload.CopyTo(image, (int)(payloadSectors * Sector));

        return (image, plaintext);
    }

    /// <summary>Anti-forensic split: spread <paramref name="key"/> across <paramref name="stripes"/>
    /// hash-diffused blocks so that <see cref="AntiForensic"/>.Merge reconstructs it.</summary>
    private static byte[] AfSplit(byte[] key, int stripes, Random rng)
    {
        var material = new byte[key.Length * stripes];
        var buffer = new byte[key.Length];
        for (var i = 0; i < stripes - 1; i++)
        {
            var block = RandomBytes(rng, key.Length);
            block.CopyTo(material, i * key.Length);
            for (var k = 0; k < key.Length; k++)
            {
                buffer[k] ^= block[k];
            }

            Diffuse(buffer);
        }

        for (var k = 0; k < key.Length; k++)
        {
            material[(stripes - 1) * key.Length + k] = (byte)(buffer[k] ^ key[k]);
        }

        return material;
    }

    private static void Diffuse(byte[] block)
    {
        const int digest = 32; // sha256
        Span<byte> input = stackalloc byte[4 + digest];
        Span<byte> d = stackalloc byte[digest];
        var offset = 0;
        var index = 0u;
        while (offset < block.Length)
        {
            var chunk = Math.Min(digest, block.Length - offset);
            BinaryPrimitives.WriteUInt32BigEndian(input, index);
            block.AsSpan(offset, chunk).CopyTo(input[4..]);
            SHA256.HashData(input[..(4 + chunk)], d);
            d[..chunk].CopyTo(block.AsSpan(offset, chunk));
            offset += chunk;
            index++;
        }
    }

    /// <summary>Independent AES-XTS (plain64) encrypt, in place, 512-byte sectors.</summary>
    private static void XtsEncrypt(byte[] key, byte[] data, ulong startSector)
    {
        using var data1 = Aes.Create();
        using var tweak1 = Aes.Create();
        data1.Mode = tweak1.Mode = CipherMode.ECB;
        data1.Padding = tweak1.Padding = PaddingMode.None;
        data1.Key = key[..(key.Length / 2)];
        tweak1.Key = key[(key.Length / 2)..];

        Span<byte> iv = stackalloc byte[16];
        Span<byte> t = stackalloc byte[16];
        Span<byte> enc = stackalloc byte[16];

        var sectors = data.Length / Sector;
        for (var s = 0; s < sectors; s++)
        {
            iv.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(iv, startSector + (ulong)s);
            tweak1.EncryptEcb(iv, t, PaddingMode.None);

            for (var j = 0; j < Sector / 16; j++)
            {
                var block = data.AsSpan(s * Sector + j * 16, 16);
                for (var k = 0; k < 16; k++)
                {
                    block[k] ^= t[k];
                }

                data1.EncryptEcb(block, enc, PaddingMode.None);
                for (var k = 0; k < 16; k++)
                {
                    block[k] = (byte)(enc[k] ^ t[k]);
                }

                // advance tweak: multiply by alpha in GF(2^128), little-endian.
                var carry = 0;
                for (var k = 0; k < 16; k++)
                {
                    var b = t[k];
                    t[k] = (byte)(((b << 1) | carry) & 0xFF);
                    carry = (b >> 7) & 1;
                }

                if (carry != 0)
                {
                    t[0] ^= 0x87;
                }
            }
        }
    }

    private static byte[] RandomBytes(Random rng, int n)
    {
        var b = new byte[n];
        rng.NextBytes(b);
        return b;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value) =>
        Encoding.ASCII.GetBytes(value).CopyTo(buffer, offset);
}
