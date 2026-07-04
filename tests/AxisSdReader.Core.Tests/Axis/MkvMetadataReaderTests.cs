using System.Buffers.Binary;
using AxisSdReader.Core.Axis.Matroska;

namespace AxisSdReader.Core.Tests.Axis;

/// <summary>
/// Hardening tests for the EBML/Matroska reader against crafted/hostile input (a pulled card can hold
/// anything). These build minimal MKV byte streams by hand to exercise specific parser edge cases.
/// </summary>
public class MkvMetadataReaderTests
{
    // Element IDs as their canonical EBML bytes.
    private static readonly byte[] Ebml = [0x1A, 0x45, 0xDF, 0xA3];
    private static readonly byte[] Segment = [0x18, 0x53, 0x80, 0x67];
    private static readonly byte[] SegmentInfo = [0x15, 0x49, 0xA9, 0x66];
    private static readonly byte[] TimestampScale = [0x2A, 0xD7, 0xB1];
    private static readonly byte[] Duration = [0x44, 0x89];
    private static readonly byte[] Cluster = [0x1F, 0x43, 0xB6, 0x75];
    private static readonly byte[] ClusterTimestamp = [0xE7];
    private static readonly byte[] SimpleBlock = [0xA3];

    [Fact]
    public void InfinityHeaderDurationIsRejected()
    {
        // A crafted 8-byte float Duration of +Infinity must not slip past the sanity guard and become
        // a huge/garbage TimeSpan — it degrades to "no duration".
        var mkv = Concat(
            Elem(Ebml, [0x42, 0x87, 0x81, 0x01]),
            Elem(Segment, Concat(
                Elem(SegmentInfo, Concat(
                    Elem(TimestampScale, UIntBytes(1_000_000)),
                    Elem(Duration, DoubleBe(double.PositiveInfinity)))))));

        var meta = MkvMetadataReader.Read(new MemoryStream(mkv), scanClustersForDuration: false);

        Assert.NotNull(meta);
        Assert.Null(meta!.Duration);
    }

    [Fact]
    public void FiniteButHugeHeaderDurationIsRejected()
    {
        // A finite but enormous Duration would overflow the (long) cast in the tick math and wrap to a
        // negative/garbage TimeSpan — it must be rejected, like the non-finite case.
        var mkv = Concat(
            Elem(Ebml, [0x42, 0x87, 0x81, 0x01]),
            Elem(Segment, Concat(
                Elem(SegmentInfo, Concat(
                    Elem(TimestampScale, UIntBytes(1_000_000)),
                    Elem(Duration, DoubleBe(1e300)))))));

        var meta = MkvMetadataReader.Read(new MemoryStream(mkv), scanClustersForDuration: false);

        Assert.NotNull(meta);
        Assert.Null(meta!.Duration);
    }

    [Fact]
    public void FiveByteElementIdIsSkippedNotAborted()
    {
        // A legal but unhandled 5-byte element ID used to be misread as EOF, aborting the whole parse.
        // It must instead be skipped so a Duration that follows it is still read.
        byte[] fiveByteId = [0x08, 0x11, 0x22, 0x33, 0x44];
        var mkv = Concat(
            Elem(Ebml, [0x42, 0x87, 0x81, 0x01]),
            Elem(Segment, Concat(
                Elem(fiveByteId, [0xDE, 0xAD]),
                Elem(SegmentInfo, Concat(
                    Elem(TimestampScale, UIntBytes(1_000_000)),
                    Elem(Duration, DoubleBe(2000.0))))))); // 2000 * 1e6 ns => 2 s

        var meta = MkvMetadataReader.Read(new MemoryStream(mkv), scanClustersForDuration: false);

        Assert.NotNull(meta);
        Assert.NotNull(meta!.Duration);
        Assert.InRange(meta.Duration!.Value.TotalSeconds, 1.9, 2.1);
    }

    [Fact]
    public void UndersizedBlockDoesNotCorruptOrCrashClusterScan()
    {
        // A SimpleBlock whose declared payload is smaller than a timestamp header (< 3 bytes) must be
        // skipped, not read past its boundary — and never throw.
        var mkv = Concat(
            Elem(Ebml, [0x42, 0x87, 0x81, 0x01]),
            Elem(Segment, Concat(
                Elem(SegmentInfo, Elem(TimestampScale, UIntBytes(1_000_000))),
                Elem(Cluster, Concat(
                    Elem(ClusterTimestamp, UIntBytes(1000)),
                    Elem(SimpleBlock, [0xAA]))))));

        var meta = MkvMetadataReader.Read(new MemoryStream(mkv));

        Assert.NotNull(meta);
        // Duration comes from the cluster timestamp alone (1000 * 1e6 ns = 1 s); the tiny block is skipped.
        Assert.NotNull(meta!.Duration);
        Assert.InRange(meta.Duration!.Value.TotalSeconds, 0.9, 1.1);
    }

    [Fact]
    public void OverflowingClusterTimestampsDegradeToNullNotThrow()
    {
        // A near-Int64.MaxValue cluster timestamp times a large scale would overflow the duration
        // multiply; it must saturate to "no duration" rather than wrap to a negative/garbage TimeSpan.
        var mkv = Concat(
            Elem(Ebml, [0x42, 0x87, 0x81, 0x01]),
            Elem(Segment, Concat(
                Elem(SegmentInfo, Elem(TimestampScale, UIntBytes(1_000_000_000))),
                Elem(Cluster, Elem(ClusterTimestamp, UIntBytes(0x7FFF_FFFF_FFFF_FFFF))))));

        var meta = MkvMetadataReader.Read(new MemoryStream(mkv));

        Assert.NotNull(meta);
        Assert.Null(meta!.Duration);
    }

    // --- tiny EBML builder ---------------------------------------------------

    private static byte[] Elem(byte[] id, byte[] payload) => Concat(id, Vint(payload.Length), payload);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Minimal-length EBML size VINT (data-size with the length marker set).</summary>
    private static byte[] Vint(long size) => size switch
    {
        <= 0x7E => [(byte)(0x80 | size)],
        <= 0x3FFE => [(byte)(0x40 | (size >> 8)), (byte)size],
        <= 0x1F_FFFE => [(byte)(0x20 | (size >> 16)), (byte)(size >> 8), (byte)size],
        _ => [(byte)(0x10 | (size >> 24)), (byte)(size >> 16), (byte)(size >> 8), (byte)size],
    };

    private static byte[] UIntBytes(ulong v)
    {
        if (v == 0)
        {
            return [0];
        }

        var bytes = new List<byte>();
        while (v > 0)
        {
            bytes.Insert(0, (byte)(v & 0xFF));
            v >>= 8;
        }

        return bytes.ToArray();
    }

    private static byte[] DoubleBe(double d)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, d);
        return b;
    }
}
