using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AxisSdReader.Core.Ext4;

namespace AxisSdReader.Core.Axis;

/// <summary>Metadata from a recording's <c>recording.xml</c> (ONVIF-style, written by the camera).</summary>
/// <param name="TriggerType">Broad trigger class. NOTE: not a reliable discriminator on its own — an
/// AXIS Camera Station Edge card writes <c>triggered</c> here for BOTH continuous and motion recordings.</param>
/// <param name="TriggerName">The action-rule name, e.g. <c>ACC_Motion_&lt;serial&gt;_0</c> /
/// <c>ACC_Continuous_&lt;serial&gt;_0</c> on ACS Edge — usually the real discriminator.</param>
/// <param name="TriggerTrigger">The rule's trigger, e.g. <c>ACC_MotionAction</c> / <c>ACC_ContinuousAction</c>.</param>
public sealed record RecordingInfoXml(
    string? RecordingToken,
    DateTime? StartTimeUtc,
    string? SourceToken,
    string? Encoding,
    int? Width,
    int? Height,
    double? Framerate,
    string? TriggerType,
    string? TriggerName,
    string? TriggerTrigger = null);

/// <summary>Metadata from a chunk's sidecar XML (<c>&lt;RecordingBlock&gt;</c>).</summary>
/// <param name="Status"><c>Complete</c>, or <c>Recording</c> when the camera was still writing
/// this block when the card was removed.</param>
public sealed record RecordingBlockXml(
    string? BlockToken,
    DateTime? StartTimeUtc,
    DateTime? StopTimeUtc,
    string? Status)
{
    public bool IsComplete => string.Equals(Status, "Complete", StringComparison.OrdinalIgnoreCase);

    // A single recording can't plausibly last centuries; clamp so a crafted/corrupt sidecar (e.g.
    // StartTime=0001, StopTime=9999) can't propagate an absurd duration into UI/date math. Mirrors the
    // MKV reader's ceiling.
    private static readonly TimeSpan MaxDuration = TimeSpan.FromTicks(TimeSpan.TicksPerDay * 366 * 200);

    public TimeSpan? Duration => StartTimeUtc is { } start && StopTimeUtc is { } stop && stop >= start
        ? (stop - start is var d && d > MaxDuration ? MaxDuration : d)
        : null;
}

/// <summary>Defensive parsers for the camera-written XML metadata files.</summary>
public static class RecordingXml
{
    /// <summary>The recording.xml with inode <paramref name="inode"/>, or null.</summary>
    public static RecordingInfoXml? TryParseRecordingInfo(Ext4Volume volume, long inode) => TryParseRecordingInfo(volume, inode, out _);

    /// <summary>The recording.xml with inode <paramref name="inode"/>, or null; <paramref name="cost"/> is what reading it
    /// took in bytes (its map and its contents), even when it fails.</summary>
    public static RecordingInfoXml? TryParseRecordingInfo(Ext4Volume volume, long inode, out long cost)
    {
        var root = TryLoad(volume, inode, out cost);
        if (root is null || root.Name.LocalName != "Recording")
        {
            return null;
        }

        var video = root.Elements("Track")
            .Select(t => t.Element("VideoAttributes"))
            .FirstOrDefault(v => v is not null);
        var custom = root.Element("CustomAttributes");

        return new RecordingInfoXml(
            Short(root.Attribute("RecordingToken")?.Value),
            ParseTime(root.Element("StartTime")?.Value),
            CanonicalSource(root.Element("SourceToken")?.Value),
            Short(video?.Element("Encoding")?.Value),
            ParseInt(video?.Element("Width")?.Value),
            ParseInt(video?.Element("Height")?.Value),
            ParseDouble(video?.Element("Framerate")?.Value),
            Short(custom?.Element("TriggerType")?.Value),
            Short(custom?.Element("TriggerName")?.Value),
            Short(custom?.Element("TriggerTrigger")?.Value));
    }

    /// <summary>The longest card-written text value kept; real values are a few dozen characters.</summary>
    public const int MaxTextLength = 256;

    /// <summary>
    /// A source token as the rest of the app may use it. Cameras number their video sources ("1", "2", ...), and
    /// the token travels on into the UI, agent replies and exported file names, so anything other than a short
    /// plain token (letters, digits, '_', '-', '.'; at most 16) is replaced by a stand-in derived from it: stable
    /// across recordings and runs, so a camera's recordings still group by source.
    /// </summary>
    public static string? CanonicalSource(string? token)
    {
        var trimmed = token?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length <= 16 && trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            return trimmed;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        return "src-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>Card text, trimmed and cut to <see cref="MaxTextLength"/>: bounded wherever it is shown or matched.</summary>
    private static string? Short(string? value) => Short(value, MaxTextLength);

    /// <summary>The largest XML file read from a card (real ones are about a kilobyte).</summary>
    public const long MaxFileBytes = 64 * 1024;

    /// <summary>The chunk sidecar with inode <paramref name="inode"/>, or null; <paramref name="cost"/> is what reading it
    /// took in bytes (its map and its contents), even when it fails. Only the times and a short status are kept (a
    /// recording holds hundreds of thousands of these).</summary>
    public static RecordingBlockXml? TryParseBlock(Ext4Volume volume, long inode, out long cost)
    {
        var root = TryLoad(volume, inode, out cost);
        if (root is null || root.Name.LocalName != "RecordingBlock")
        {
            return null;
        }

        var status = root.Element("Status")?.Value.Trim();
        return new RecordingBlockXml(
            null,
            ParseTime(root.Element("StartTime")?.Value),
            ParseTime(root.Element("StopTime")?.Value),
            string.Equals(status, "Complete", StringComparison.OrdinalIgnoreCase) ? "Complete" : Short(status, 32));
    }

    /// <summary>Card text, trimmed and cut to <paramref name="max"/> characters.</summary>
    private static string? Short(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static XElement? TryLoad(Ext4Volume volume, long inode, out long cost)
    {
        cost = 1024; // its inode, at least
        try
        {
            if (volume.FileLength(inode) is not (>= 0 and <= MaxFileBytes and var length))
            {
                return null;
            }

            var file = volume.OpenFile(inode, out var mapCost);
            cost = mapCost + Math.Max(length, 1024);
            if (file is null)
            {
                return null;
            }

            using var stream = file;
            // No DTDs: an entity-expansion document could otherwise inflate a 1 MB file ~10x per parse.
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 2 * MaxFileBytes,
                MaxCharactersFromEntities = 0,
            });
            return XDocument.Load(reader).Root;
        }
        catch
        {
            return null; // malformed/partial XML (power loss) — callers fall back to other sources
        }
    }

    private static DateTime? ParseTime(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
