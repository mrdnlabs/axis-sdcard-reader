using System.Globalization;
using System.Xml.Linq;
using DiscUtils;

namespace AxisSdReader.Core.Axis;

/// <summary>Metadata from a recording's <c>recording.xml</c> (ONVIF-style, written by the camera).</summary>
public sealed record RecordingInfoXml(
    string? RecordingToken,
    DateTime? StartTimeUtc,
    string? SourceToken,
    string? Encoding,
    int? Width,
    int? Height,
    double? Framerate,
    string? TriggerType,
    string? TriggerName);

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

    public TimeSpan? Duration => StartTimeUtc is { } start && StopTimeUtc is { } stop && stop >= start
        ? stop - start
        : null;
}

/// <summary>Defensive parsers for the camera-written XML metadata files.</summary>
public static class RecordingXml
{
    public static RecordingInfoXml? TryParseRecordingInfo(DiscFileSystem fs, string path)
    {
        var root = TryLoad(fs, path);
        if (root is null || root.Name.LocalName != "Recording")
        {
            return null;
        }

        var video = root.Elements("Track")
            .Select(t => t.Element("VideoAttributes"))
            .FirstOrDefault(v => v is not null);
        var custom = root.Element("CustomAttributes");

        return new RecordingInfoXml(
            root.Attribute("RecordingToken")?.Value,
            ParseTime(root.Element("StartTime")?.Value),
            root.Element("SourceToken")?.Value,
            video?.Element("Encoding")?.Value,
            ParseInt(video?.Element("Width")?.Value),
            ParseInt(video?.Element("Height")?.Value),
            ParseDouble(video?.Element("Framerate")?.Value),
            custom?.Element("TriggerType")?.Value,
            custom?.Element("TriggerName")?.Value);
    }

    public static RecordingBlockXml? TryParseBlock(DiscFileSystem fs, string path)
    {
        var root = TryLoad(fs, path);
        if (root is null || root.Name.LocalName != "RecordingBlock")
        {
            return null;
        }

        return new RecordingBlockXml(
            root.Attribute("RecordingBlockToken")?.Value,
            ParseTime(root.Element("StartTime")?.Value),
            ParseTime(root.Element("StopTime")?.Value),
            root.Element("Status")?.Value);
    }

    private static XElement? TryLoad(DiscFileSystem fs, string path)
    {
        try
        {
            if (!fs.FileExists(path) || fs.GetFileLength(path) > 1024 * 1024)
            {
                return null;
            }

            using var stream = fs.OpenFile(path, FileMode.Open, FileAccess.Read);
            return XDocument.Load(stream).Root;
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
