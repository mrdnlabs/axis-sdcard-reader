using System.Text.RegularExpressions;
using AxisSdReader.Core.Disk;
using AxisSdReader.Core.Ext4;
using DiscUtils.Streams;

namespace AxisSdReader.Core.Axis;

/// <summary>Result of a quick, read-only probe of a physical disk for Axis camera content.</summary>
public sealed record AxisCardProbeResult(int DiskNumber, bool IsExt4, string? VolumeLabel, bool HasAxisContent)
{
    /// <summary>Strong indication this is an Axis camera SD card: an ext4 filesystem that
    /// either carries recognizable recording structures or the label Axis cameras write.</summary>
    public bool IsLikelyAxisCard =>
        IsExt4 && (HasAxisContent || string.Equals(VolumeLabel?.Trim(), "Axis", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Probes a disk for Axis-camera cues without locking or mounting anything: opens the raw
/// device read-only, looks for an ext4 filesystem, and peeks at the volume label and root
/// entries (index.db, date directories, recording-ID directories). A few KB of reads total.
/// Requires administrator rights (raw device access).
/// </summary>
public static class AxisCardDetector
{
    private static readonly Regex DateDir = new(@"^\d{8}$", RegexOptions.Compiled);

    public static AxisCardProbeResult Probe(int diskNumber)
    {
        try
        {
            var raw = RawDiskStream.OpenPhysicalDrive(diskNumber);
            using var reader = CardReader.Open(raw, Ownership.Dispose);

            if (reader.Status != CardOpenStatus.Ok || reader.FileSystem is not { } fs)
            {
                return new AxisCardProbeResult(diskNumber, IsExt4: false, null, false);
            }

            var hasContent = fs.FileExists(@"\index.db");
            if (!hasContent)
            {
                foreach (var dir in fs.GetDirectories(@"\"))
                {
                    var name = System.IO.Path.GetFileName(dir.TrimEnd('\\'));
                    if (RecordingId.TryParse(name) is not null || DateDir.IsMatch(name))
                    {
                        hasContent = true;
                        break;
                    }
                }
            }

            return new AxisCardProbeResult(diskNumber, IsExt4: true, fs.VolumeLabel, hasContent);
        }
        catch
        {
            // Unreadable/empty slot/no access: simply not an Axis card for our purposes.
            return new AxisCardProbeResult(diskNumber, IsExt4: false, null, false);
        }
    }
}
