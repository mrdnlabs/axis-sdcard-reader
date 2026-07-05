using System.Buffers.Binary;
using System.Text;

namespace AxisSdReader.Core.Ext4.Luks;

/// <summary>One LUKS1 key slot: an independently-derivable copy of the master key.</summary>
internal sealed class LuksKeyslot
{
    public bool Active { get; init; }
    public uint Iterations { get; init; }
    public byte[] Salt { get; init; } = [];
    public uint KeyMaterialOffsetSectors { get; init; }
    public uint Stripes { get; init; }
}

/// <summary>
/// Parsed LUKS1 partition header (the 592-byte phdr at the start of the partition). All multi-byte
/// fields are big-endian on disk. LUKS2 headers share the magic but a different layout; this parser
/// records the version so callers can reject LUKS2 cleanly.
/// </summary>
internal sealed class LuksHeader
{
    public static readonly byte[] Magic = [0x4C, 0x55, 0x4B, 0x53, 0xBA, 0xBE];
    private const uint KeyEnabled = 0x00AC71F3;
    private const int SlotCount = 8;

    /// <summary>Minimum bytes needed to parse the full LUKS1 phdr (208 + 8 * 48).</summary>
    public const int HeaderBytes = 592;

    public int Version { get; private init; }
    public string CipherName { get; private init; } = "";
    public string CipherMode { get; private init; } = "";
    public string HashSpec { get; private init; } = "";
    public uint PayloadOffsetSectors { get; private init; }
    public uint KeyBytes { get; private init; }
    public byte[] MasterKeyDigest { get; private init; } = [];
    public byte[] MasterKeyDigestSalt { get; private init; } = [];
    public uint MasterKeyDigestIterations { get; private init; }
    public IReadOnlyList<LuksKeyslot> Keyslots { get; private init; } = [];

    public static bool HasMagic(ReadOnlySpan<byte> header) =>
        header.Length >= Magic.Length && header[..Magic.Length].SequenceEqual(Magic);

    public static LuksHeader Parse(ReadOnlySpan<byte> h)
    {
        var version = BinaryPrimitives.ReadUInt16BigEndian(h.Slice(6, 2));
        if (version != 1)
        {
            return new LuksHeader { Version = version };
        }

        var slots = new LuksKeyslot[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            var b = 208 + i * 48;
            slots[i] = new LuksKeyslot
            {
                Active = BinaryPrimitives.ReadUInt32BigEndian(h.Slice(b, 4)) == KeyEnabled,
                Iterations = BinaryPrimitives.ReadUInt32BigEndian(h.Slice(b + 4, 4)),
                Salt = h.Slice(b + 8, 32).ToArray(),
                KeyMaterialOffsetSectors = BinaryPrimitives.ReadUInt32BigEndian(h.Slice(b + 40, 4)),
                Stripes = BinaryPrimitives.ReadUInt32BigEndian(h.Slice(b + 44, 4)),
            };
        }

        return new LuksHeader
        {
            Version = version,
            CipherName = AsciiZ(h.Slice(8, 32)),
            CipherMode = AsciiZ(h.Slice(40, 32)),
            HashSpec = AsciiZ(h.Slice(72, 32)),
            PayloadOffsetSectors = BinaryPrimitives.ReadUInt32BigEndian(h.Slice(104, 4)),
            KeyBytes = BinaryPrimitives.ReadUInt32BigEndian(h.Slice(108, 4)),
            MasterKeyDigest = h.Slice(112, 20).ToArray(),
            MasterKeyDigestSalt = h.Slice(132, 32).ToArray(),
            MasterKeyDigestIterations = BinaryPrimitives.ReadUInt32BigEndian(h.Slice(164, 4)),
            Keyslots = slots,
        };
    }

    private static string AsciiZ(ReadOnlySpan<byte> span)
    {
        var end = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(span[..(end < 0 ? span.Length : end)]);
    }
}
