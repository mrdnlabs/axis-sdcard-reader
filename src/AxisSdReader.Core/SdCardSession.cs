using AxisSdReader.Core.Disk;
using AxisSdReader.Core.Ext4;
using DiscUtils.Streams;

namespace AxisSdReader.Core;

/// <summary>
/// A live, protected, read-only session on a physical SD card:
/// locks/dismounts the card's volumes and removes drive letters (<see cref="VolumeGuard"/>),
/// opens the raw device read-only (<see cref="RawDiskStream"/>) behind a block cache,
/// and locates the ext4 filesystem (<see cref="CardReader"/>).
/// Dispose to release the volume locks and the device handle. Requires administrator rights.
/// </summary>
public sealed class SdCardSession : IDisposable
{
    private readonly VolumeGuard? _guard;
    private readonly CardReader _reader;

    private SdCardSession(VolumeGuard? guard, CardReader reader)
    {
        _guard = guard;
        _reader = reader;
    }

    public CardReader Card => _reader;

    public IReadOnlyList<string> ProtectionLog => _guard?.Log ?? [];

    /// <summary>
    /// True when every volume on the card was locked (or there were none) — i.e. the card is genuinely
    /// protected against Windows re-mounting/formatting it. False means at least one volume could not be
    /// locked and callers must not present the card as read-only-safe. Always true when volume guarding
    /// was disabled (diagnostics) since no protection was attempted or promised.
    /// </summary>
    public bool FullyProtected => _guard is null || _guard.AllVolumesLocked;

    /// <summary>Opens a protected session on the given physical disk.</summary>
    /// <param name="diskNumber">The physical disk number (see <see cref="DiskEnumerator"/>).</param>
    /// <param name="guardVolumes">Lock/dismount volumes and remove drive letters first. Disable only for diagnostics.</param>
    public static SdCardSession Open(int diskNumber, bool guardVolumes = true)
    {
        VolumeGuard? guard = null;
        RawDiskStream? raw = null;
        try
        {
            if (guardVolumes)
            {
                guard = VolumeGuard.Acquire(diskNumber);
            }

            raw = RawDiskStream.OpenPhysicalDrive(diskNumber);

            // Cache raw-device reads: ext4 metadata access is many small scattered reads,
            // and every uncached read is a sector-aligned DeviceIoControl round trip.
            var cached = new BlockCacheStream(SparseStream.FromStream(raw, Ownership.Dispose), Ownership.Dispose);

            var reader = CardReader.Open(cached, Ownership.Dispose);
            return new SdCardSession(guard, reader);
        }
        catch
        {
            raw?.Dispose();
            guard?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _guard?.Dispose();
    }
}
