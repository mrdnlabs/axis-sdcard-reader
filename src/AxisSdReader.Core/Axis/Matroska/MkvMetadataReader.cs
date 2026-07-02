using System.Buffers.Binary;

namespace AxisSdReader.Core.Axis.Matroska;

/// <summary>
/// Minimal, defensive EBML/Matroska header reader. Parses only what the app needs
/// (segment info and the first video track) and never loads whole files. Designed for
/// surveillance-camera output: tolerates unknown-size segments (live muxing), missing
/// Duration elements, and files truncated by power loss.
/// </summary>
public static class MkvMetadataReader
{
    // EBML/Matroska element IDs.
    private const uint EbmlHeader = 0x1A45DFA3;
    private const uint Segment = 0x18538067;
    private const uint SegmentInfo = 0x1549A966;
    private const uint TimestampScale = 0x2AD7B1;
    private const uint Duration = 0x4489;
    private const uint DateUtcId = 0x4461;
    private const uint WritingAppId = 0x5741;
    private const uint Tracks = 0x1654AE6B;
    private const uint TrackEntry = 0xAE;
    private const uint TrackType = 0x83;
    private const uint CodecId = 0x86;
    private const uint VideoElement = 0xE0;
    private const uint PixelWidthId = 0xB0;
    private const uint PixelHeightId = 0xBA;
    private const uint Cluster = 0x1F43B675;
    private const uint ClusterTimestamp = 0xE7;
    private const uint SimpleBlock = 0xA3;
    private const uint BlockGroup = 0xA0;
    private const uint BlockElement = 0xA1;

    private static readonly DateTime MatroskaEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Reads MKV metadata from the stream (positioned at the file start).
    /// Returns null when the stream is not a Matroska file at all.
    /// </summary>
    /// <param name="stream">Seekable stream over the MKV file.</param>
    /// <param name="scanClustersForDuration">When the header has no Duration element (typical for
    /// camera-written files), walk the clusters to derive one. Costs a pass over the file's
    /// element structure (cheap: skips payloads), not a full read.</param>
    public static MkvMetadata? Read(Stream stream, bool scanClustersForDuration = true)
    {
        stream.Position = 0;
        if (ReadElementId(stream) != EbmlHeader || SkipElement(stream) is null)
        {
            return null;
        }

        if (ReadElementId(stream) != Segment)
        {
            return null;
        }

        ReadSize(stream); // segment size; often unknown for live-muxed files — children parsed linearly either way

        DateTime? dateUtc = null;
        double? durationTicks = null;
        long timestampScale = 1_000_000; // ns, Matroska default
        string? writingApp = null;
        string? codec = null;
        int? width = null, height = null;
        var truncated = false;
        long? firstClusterPosition = null;

        try
        {
            while (true)
            {
                var id = ReadElementId(stream);
                if (id is null)
                {
                    break; // clean EOF
                }

                if (id == Cluster)
                {
                    firstClusterPosition = stream.Position - IdLength(id.Value);
                    break;
                }

                if (id == SegmentInfo)
                {
                    var end = ElementEnd(stream);
                    while (stream.Position < end && ReadElementId(stream) is { } infoId)
                    {
                        switch (infoId)
                        {
                            case TimestampScale:
                                timestampScale = (long)(ReadUInt(stream) ?? 1_000_000);
                                break;
                            case Duration:
                                durationTicks = ReadFloat(stream);
                                break;
                            case DateUtcId:
                                dateUtc = ReadDate(stream);
                                break;
                            case WritingAppId:
                                writingApp = ReadString(stream);
                                break;
                            default:
                                SkipElement(stream);
                                break;
                        }
                    }
                }
                else if (id == Tracks)
                {
                    var end = ElementEnd(stream);
                    while (stream.Position < end && ReadElementId(stream) is { } trackId)
                    {
                        if (trackId != TrackEntry)
                        {
                            SkipElement(stream);
                            continue;
                        }

                        var entryEnd = ElementEnd(stream);
                        long? type = null;
                        string? entryCodec = null;
                        int? entryWidth = null, entryHeight = null;

                        while (stream.Position < entryEnd && ReadElementId(stream) is { } fieldId)
                        {
                            switch (fieldId)
                            {
                                case TrackType:
                                    type = (long?)ReadUInt(stream);
                                    break;
                                case CodecId:
                                    entryCodec = ReadString(stream);
                                    break;
                                case VideoElement:
                                    var videoEnd = ElementEnd(stream);
                                    while (stream.Position < videoEnd && ReadElementId(stream) is { } videoId)
                                    {
                                        switch (videoId)
                                        {
                                            case PixelWidthId:
                                                entryWidth = (int?)ReadUInt(stream);
                                                break;
                                            case PixelHeightId:
                                                entryHeight = (int?)ReadUInt(stream);
                                                break;
                                            default:
                                                SkipElement(stream);
                                                break;
                                        }
                                    }

                                    break;
                                default:
                                    SkipElement(stream);
                                    break;
                            }
                        }

                        if (type == 1 && codec is null) // first video track
                        {
                            codec = entryCodec;
                            width = entryWidth;
                            height = entryHeight;
                        }
                    }
                }
                else
                {
                    if (SkipElement(stream) is null)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
        }
        catch (EndOfStreamException)
        {
            truncated = true;
        }

        TimeSpan? duration = durationTicks is { } d
            ? TimeSpan.FromTicks((long)(d * timestampScale / 100.0))
            : null;

        if (duration is null && scanClustersForDuration && firstClusterPosition is { } clusterStart)
        {
            var (scanned, scanTruncated) = ScanClustersForDuration(stream, clusterStart, timestampScale);
            duration = scanned;
            truncated |= scanTruncated;
        }

        return new MkvMetadata(dateUtc, duration, codec, width, height, writingApp, truncated);
    }

    /// <summary>
    /// Derives a duration by walking cluster headers (skipping payloads) and, within the last
    /// cluster, block timestamps. Returns a lower bound (start of the last frame).
    /// </summary>
    private static (TimeSpan? Duration, bool Truncated) ScanClustersForDuration(
        Stream stream, long firstClusterPosition, long timestampScale)
    {
        stream.Position = firstClusterPosition;
        long lastClusterTs = 0;
        long lastBlockRelativeTs = 0;
        long? lastClusterEnd = null;
        var truncated = false;

        try
        {
            while (true)
            {
                var id = ReadElementId(stream);
                if (id is null)
                {
                    break;
                }

                if (id != Cluster)
                {
                    if (SkipElement(stream) is null)
                    {
                        break;
                    }

                    continue;
                }

                var clusterEnd = ElementEnd(stream);
                lastClusterEnd = clusterEnd;
                lastBlockRelativeTs = 0;

                while (stream.Position < clusterEnd && stream.Position < stream.Length &&
                       ReadElementId(stream) is { } inner)
                {
                    switch (inner)
                    {
                        case ClusterTimestamp:
                            lastClusterTs = (long)(ReadUInt(stream) ?? 0);
                            break;
                        case SimpleBlock:
                        {
                            var end = ElementEnd(stream);
                            var rel = ReadBlockRelativeTimestamp(stream);
                            if (rel > lastBlockRelativeTs)
                            {
                                lastBlockRelativeTs = rel;
                            }

                            stream.Position = end;
                            break;
                        }
                        case BlockGroup:
                        {
                            var groupEnd = ElementEnd(stream);
                            while (stream.Position < groupEnd && ReadElementId(stream) is { } groupInner)
                            {
                                if (groupInner == BlockElement)
                                {
                                    var end = ElementEnd(stream);
                                    var rel = ReadBlockRelativeTimestamp(stream);
                                    if (rel > lastBlockRelativeTs)
                                    {
                                        lastBlockRelativeTs = rel;
                                    }

                                    stream.Position = end;
                                }
                                else
                                {
                                    SkipElement(stream);
                                }
                            }

                            break;
                        }
                        default:
                            if (SkipElement(stream) is null)
                            {
                                truncated = true;
                                goto done;
                            }

                            break;
                    }
                }
            }
        }
        catch (EndOfStreamException)
        {
            truncated = true;
        }

        done:
        if (lastClusterEnd is { } lce && lce > stream.Length)
        {
            truncated = true;
        }

        var totalNs = (lastClusterTs + lastBlockRelativeTs) * timestampScale;
        return (totalNs > 0 ? TimeSpan.FromTicks(totalNs / 100) : null, truncated);
    }

    /// <summary>Reads the signed 16-bit relative timestamp of a (Simple)Block, positioned at its payload start.</summary>
    private static short ReadBlockRelativeTimestamp(Stream stream)
    {
        // Block payload: track number (VINT), then int16 relative timestamp (big-endian).
        var first = stream.ReadByte();
        if (first < 0)
        {
            throw new EndOfStreamException();
        }

        var extraTrackBytes = LeadingZeroLength((byte)first) - 1;
        for (var i = 0; i < extraTrackBytes; i++)
        {
            stream.ReadByte();
        }

        var hi = stream.ReadByte();
        var lo = stream.ReadByte();
        if (hi < 0 || lo < 0)
        {
            throw new EndOfStreamException();
        }

        return (short)((hi << 8) | lo);
    }

    // --- EBML primitives -----------------------------------------------------

    /// <summary>Reads an element ID; null on clean EOF.</summary>
    private static uint? ReadElementId(Stream stream)
    {
        var first = stream.ReadByte();
        if (first < 0)
        {
            return null;
        }

        var length = LeadingZeroLength((byte)first);
        if (length is < 1 or > 4)
        {
            throw new EndOfStreamException("Invalid EBML ID.");
        }

        var value = (uint)first;
        for (var i = 1; i < length; i++)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException();
            }

            value = (value << 8) | (uint)b;
        }

        return value;
    }

    private static int IdLength(uint id) => id switch
    {
        <= 0xFF => 1,
        <= 0xFFFF => 2,
        <= 0xFFFFFF => 3,
        _ => 4,
    };

    /// <summary>Reads an element size; null means "unknown size".</summary>
    private static long? ReadSize(Stream stream)
    {
        var first = stream.ReadByte();
        if (first < 0)
        {
            throw new EndOfStreamException();
        }

        var length = LeadingZeroLength((byte)first);
        if (length is < 1 or > 8)
        {
            throw new EndOfStreamException("Invalid EBML size.");
        }

        var marker = 1 << (8 - length);
        long value = (byte)first & (marker - 1);
        var allOnes = value == marker - 1;

        for (var i = 1; i < length; i++)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException();
            }

            allOnes &= b == 0xFF;
            value = (value << 8) | (uint)b;
        }

        return allOnes ? null : value;
    }

    /// <summary>Reads the size and returns the absolute end position (or long.MaxValue for unknown size).</summary>
    private static long ElementEnd(Stream stream)
    {
        var size = ReadSize(stream);
        return size is { } s ? stream.Position + s : long.MaxValue;
    }

    /// <summary>Skips over an element's size+payload; returns null if the payload extends past EOF (truncated).</summary>
    private static long? SkipElement(Stream stream)
    {
        var size = ReadSize(stream);
        if (size is null)
        {
            // Unknown-size element that we don't descend into: cannot skip reliably.
            throw new EndOfStreamException("Cannot skip unknown-size element.");
        }

        var end = stream.Position + size.Value;
        if (end > stream.Length)
        {
            stream.Position = stream.Length;
            return null;
        }

        stream.Position = end;
        return end;
    }

    private static ulong? ReadUInt(Stream stream)
    {
        var size = ReadSize(stream);
        if (size is null or > 8)
        {
            return null;
        }

        ulong value = 0;
        for (var i = 0; i < size; i++)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException();
            }

            value = (value << 8) | (uint)b;
        }

        return value;
    }

    private static double? ReadFloat(Stream stream)
    {
        var size = ReadSize(stream);
        if (size is not (4 or 8))
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[8];
        var span = buffer[..(int)size.Value];
        stream.ReadExactly(span);
        return size == 4
            ? BinaryPrimitives.ReadSingleBigEndian(span)
            : BinaryPrimitives.ReadDoubleBigEndian(span);
    }

    private static DateTime? ReadDate(Stream stream)
    {
        var size = ReadSize(stream);
        if (size != 8)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[8];
        stream.ReadExactly(buffer);
        var ns = BinaryPrimitives.ReadInt64BigEndian(buffer);
        return MatroskaEpoch.AddTicks(ns / 100);
    }

    private static string? ReadString(Stream stream)
    {
        var size = ReadSize(stream);
        if (size is null or > 1024)
        {
            return null;
        }

        var buffer = new byte[(int)size.Value];
        stream.ReadExactly(buffer);
        return System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
    }

    private static int LeadingZeroLength(byte first)
    {
        for (var i = 0; i < 8; i++)
        {
            if ((first & (0x80 >> i)) != 0)
            {
                return i + 1;
            }
        }

        return 9;
    }
}
