using System.Security.Cryptography;
using System.Text;
using DiscUtils.Streams;

namespace AxisSdReader.Core.Ext4.Luks;

/// <summary>Outcome of trying to unlock a LUKS volume.</summary>
public enum LuksUnlockStatus
{
    /// <summary>Unlocked; a plaintext stream over the ext4 payload is available.</summary>
    Success,

    /// <summary>The passphrase did not match any key slot.</summary>
    WrongPassphrase,

    /// <summary>A LUKS volume this reader cannot handle (e.g. LUKS2, or a non-AES/XTS cipher).</summary>
    Unsupported,

    /// <summary>Not a LUKS volume at all.</summary>
    NotLuks,
}

/// <summary>Result of <see cref="LuksVolume.TryUnlock"/>. Dispose <see cref="PlaintextStream"/> when done.</summary>
public sealed record LuksUnlockResult(LuksUnlockStatus Status, SparseStream? PlaintextStream, string? Detail)
{
    public bool IsLuks => Status != LuksUnlockStatus.NotLuks;
}

/// <summary>
/// Unlocks a LUKS1 (dm-crypt) volume — the format AXIS cameras use for encrypted SD cards — from a
/// passphrase, exposing the inner ext4 filesystem as a read-only decrypting stream. Supports
/// <c>aes / xts-plain64</c> with PBKDF2 key derivation (LUKS2 is detected and reported as unsupported).
/// Strictly read-only: nothing is ever written to the card.
/// </summary>
public static class LuksVolume
{
    private const int SectorSize = 512;
    private const int MaxStripes = 4000;         // the LUKS1 standard; reject anything larger as corrupt
    private const int MaxIterations = 10_000_000; // cap PBKDF2 cost from a crafted header (DoS guard)

    /// <summary>Reports whether the stream begins with a LUKS header, without needing a passphrase.</summary>
    public static bool IsLuks(Stream partitionStream)
    {
        try
        {
            var head = ReadExact(partitionStream, 0, LuksHeader.Magic.Length);
            return LuksHeader.HasMagic(head);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to unlock the LUKS volume at the start of <paramref name="partitionStream"/> with
    /// <paramref name="passphrase"/> (a char[] so the caller can zero it after use). On success returns a
    /// read-only plaintext stream over the payload (the ext4 filesystem). The returned stream reads through
    /// <paramref name="partitionStream"/>, so keep that alive until the plaintext stream is disposed.
    /// </summary>
    public static LuksUnlockResult TryUnlock(Stream partitionStream, char[] passphrase)
    {
        byte[] header;
        try
        {
            header = ReadExact(partitionStream, 0, LuksHeader.HeaderBytes);
        }
        catch (IOException ex)
        {
            return new LuksUnlockResult(LuksUnlockStatus.NotLuks, null, ex.Message);
        }

        if (!LuksHeader.HasMagic(header))
        {
            return new LuksUnlockResult(LuksUnlockStatus.NotLuks, null, "No LUKS header.");
        }

        var hdr = LuksHeader.Parse(header);
        if (hdr.Version != 1)
        {
            return new LuksUnlockResult(LuksUnlockStatus.Unsupported, null,
                $"This card uses LUKS{hdr.Version}, which this version cannot read (only LUKS1 is supported).");
        }

        if (!string.Equals(hdr.CipherName, "aes", StringComparison.Ordinal) ||
            !string.Equals(hdr.CipherMode, "xts-plain64", StringComparison.Ordinal))
        {
            return new LuksUnlockResult(LuksUnlockStatus.Unsupported, null,
                $"Unsupported cipher '{hdr.CipherName}-{hdr.CipherMode}' (expected aes-xts-plain64).");
        }

        // Validate header-derived sizes/costs before trusting any of them: the master key is two AES keys
        // (each 16/24/32 bytes), and the digest KDF cost must be sane. A crafted/corrupt header must never
        // crash, hang, or over-allocate.
        if (hdr.KeyBytes is not (32 or 48 or 64) ||
            hdr.MasterKeyDigestIterations is < 1 or > MaxIterations ||
            hdr.MasterKeyDigest.Length is < 1 or > 64)
        {
            return new LuksUnlockResult(LuksUnlockStatus.Unsupported, null,
                "The card's encryption header is malformed or uses unsupported parameters.");
        }

        var hashName = HashName(hdr.HashSpec);
        var passwordBytes = Encoding.UTF8.GetBytes(passphrase); // UTF-8 bytes of the passphrase chars
        try
        {
            foreach (var slot in hdr.Keyslots)
            {
                if (!slot.Active)
                {
                    continue;
                }

                var masterKey = TryKeyslot(partitionStream, hdr, slot, passwordBytes, hashName);
                if (masterKey is null)
                {
                    continue;
                }

                try
                {
                    var payloadStart = (long)hdr.PayloadOffsetSectors * SectorSize;
                    var payloadLength = partitionStream.Length - payloadStart;
                    var cipherPayload = new SubStream(partitionStream, payloadStart, payloadLength);
                    return new LuksUnlockResult(LuksUnlockStatus.Success,
                        new XtsDecryptingStream(cipherPayload, new XtsAes(masterKey)), null);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(masterKey);
                }
            }

            return new LuksUnlockResult(LuksUnlockStatus.WrongPassphrase, null, "The passphrase is incorrect.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes); // wipe the passphrase copy on every path
        }
    }

    /// <summary>Derives the master key from a key slot, or null if the passphrase does not open it (or the
    /// slot's geometry is implausible/corrupt).</summary>
    private static byte[]? TryKeyslot(Stream partition, LuksHeader hdr, LuksKeyslot slot,
        byte[] passwordBytes, HashAlgorithmName hash)
    {
        var keyBytes = (int)hdr.KeyBytes; // already validated to 32/48/64 by the caller
        var stripes = (int)slot.Stripes;

        var materialLength = keyBytes * stripes;
        var materialSectors = (materialLength + SectorSize - 1) / SectorSize;
        var materialStart = (long)slot.KeyMaterialOffsetSectors * SectorSize;

        // Reject implausible slot geometry from a corrupt header (real LUKS1 uses stripes=4000), and a key
        // material region that doesn't fit within the partition — rather than crashing, over-allocating, or
        // reading past the device. A skipped slot just means "this passphrase didn't open it".
        if (stripes is < 1 or > MaxStripes || slot.Iterations is < 1 or > MaxIterations ||
            materialStart < 0 || materialStart + (long)materialSectors * SectorSize > partition.Length)
        {
            return null;
        }

        // 1) Passphrase -> key-slot key (PBKDF2).
        var slotKey = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, slot.Salt, (int)slot.Iterations, hash, keyBytes);
        try
        {
            // 2) Read + XTS-decrypt the slot's anti-forensic key material.
            var cipher = ReadExact(partition, materialStart, materialSectors * SectorSize);
            var plain = new byte[materialSectors * SectorSize];
            using (var xts = new XtsAes(slotKey))
            {
                for (var s = 0; s < materialSectors; s++)
                {
                    xts.DecryptSector((ulong)s, cipher.AsSpan(s * SectorSize, SectorSize), plain.AsSpan(s * SectorSize, SectorSize));
                }
            }

            // 3) AF-merge to a candidate master key.
            var candidate = AntiForensic.Merge(plain.AsSpan(0, materialLength), keyBytes, stripes, hdr.HashSpec);
            CryptographicOperations.ZeroMemory(plain);

            // 4) Verify against the stored master-key digest (PBKDF2 of the candidate).
            var digest = Rfc2898DeriveBytes.Pbkdf2(candidate, hdr.MasterKeyDigestSalt, (int)hdr.MasterKeyDigestIterations,
                hash, hdr.MasterKeyDigest.Length);

            if (CryptographicOperations.FixedTimeEquals(digest, hdr.MasterKeyDigest))
            {
                return candidate;
            }

            CryptographicOperations.ZeroMemory(candidate);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(slotKey); // wipe the passphrase-derived slot key on every path
        }
    }

    private static HashAlgorithmName HashName(string hashSpec) => hashSpec.ToLowerInvariant() switch
    {
        "sha1" => HashAlgorithmName.SHA1,
        "sha256" => HashAlgorithmName.SHA256,
        "sha512" => HashAlgorithmName.SHA512,
        _ => throw new NotSupportedException($"Unsupported LUKS hash '{hashSpec}'."),
    };

    private static byte[] ReadExact(Stream s, long offset, int count)
    {
        s.Position = offset;
        var buf = new byte[count];
        var total = 0;
        while (total < count)
        {
            var n = s.Read(buf, total, count - total);
            if (n == 0)
            {
                throw new EndOfStreamException("LUKS header or key material is truncated.");
            }

            total += n;
        }

        return buf;
    }
}
