using System.Globalization;
using System.Text.RegularExpressions;

namespace AxisSdReader.Core.Axis;

/// <summary>
/// An Axis edge-storage recording identifier, as used for recording directory names on the
/// SD card: <c>YYYYMMDD_HHMMSS_&lt;4 hex&gt;_&lt;camera serial/MAC&gt;</c>,
/// e.g. <c>20110812_081211_016F_00408C1834FD</c>. The timestamp is in the camera's clock
/// (usually UTC on Axis firmware, but not guaranteed), so it is kept as unspecified-kind.
/// </summary>
public sealed partial record RecordingId(string Raw, DateTime StartTime, string Suffix, string CameraSerial)
{
    [GeneratedRegex(@"^(?<date>\d{8})_(?<time>\d{6})_(?<suffix>[0-9A-Fa-f]{2,8})_(?<serial>[0-9A-Fa-f]{6,16})$")]
    private static partial Regex Pattern();

    /// <summary>Parses a recording directory name; returns null when the name is not a recording ID.</summary>
    public static RecordingId? TryParse(string directoryName)
    {
        var match = Pattern().Match(directoryName);
        if (!match.Success)
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                match.Groups["date"].Value + match.Groups["time"].Value,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var startTime))
        {
            return null;
        }

        return new RecordingId(
            directoryName,
            startTime,
            match.Groups["suffix"].Value.ToUpperInvariant(),
            match.Groups["serial"].Value.ToUpperInvariant());
    }
}
