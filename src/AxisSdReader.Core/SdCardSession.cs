using System.Runtime.InteropServices;
using AxisSdReader.Core.Disk;
using AxisSdReader.Core.Ext4;
using DiscUtils.Streams;
using Microsoft.Win32.SafeHandles;

namespace AxisSdReader.Core;

/// <summary>
/// A live, protected, read-only session on a physical SD card:
/// opens the raw device read-only (<see cref="RawDiskStream"/>) behind a block cache, locates the ext4
/// filesystem (<see cref="CardReader"/>), then locks/dismounts that partition's volume and removes its
/// drive letters (<see cref="VolumeGuard"/>). An encrypted card's partition is locked as soon as it is found,
/// before the passphrase is asked for (Windows offers to format a partition it can't read, and the prompt must not
/// be able to act while the user types), and the lock can be handed on to the session that unlocks it. Nothing is
/// locked for a card that can't be read, and other partitions on the disk are never touched.
/// While open, <see cref="StartMonitoring"/> watches for the card being pulled or swapped and for a volume
/// that appears late. Dispose to release the volume locks and the device handle. Requires administrator rights.
/// </summary>
public sealed class SdCardSession : IDisposable
{
    private VolumeGuard? _guard;
    private readonly CardReader _reader;
    private readonly int _diskNumber;
    private Timer? _monitor;
    private SafeFileHandle? _probe;
    private int _ticks;
    private int _mediaLostRaised;
    private readonly object _tickLock = new(); // a tick never overlaps disposal (it could lock a volume afterwards)
    private bool _disposed;

    private SdCardSession(VolumeGuard? guard, CardReader reader, int diskNumber)
    {
        _guard = guard;
        _reader = reader;
        _diskNumber = diskNumber;
    }

    public CardReader Card => _reader;

    public IReadOnlyList<string> ProtectionLog => _guard?.Log ?? [];

    /// <summary>
    /// True when every volume on the card was locked (or there were none) — i.e. the card is genuinely
    /// protected against Windows re-mounting/formatting it. False means at least one volume could not be
    /// locked and callers must not present the card as read-only-safe. Always true when volume guarding
    /// was disabled (diagnostics) or not needed (unreadable card), since no protection was promised.
    /// </summary>
    public bool FullyProtected => _guard is null || _guard.AllVolumesLocked;

    /// <summary>The card was removed or replaced in its reader (raised once, on a pool thread).</summary>
    public event Action? MediaLost;

    /// <summary>A volume for the card appeared after opening and was locked (or failed to lock); re-read
    /// <see cref="FullyProtected"/>. Raised on a pool thread.</summary>
    public event Action? ProtectionChanged;

    /// <summary>Opens a protected session on the given physical disk.</summary>
    /// <param name="diskNumber">The physical disk number (see <see cref="DiskEnumerator"/>).</param>
    /// <param name="guardVolumes">Lock/dismount the card's volume and remove its drive letters. Disable only for diagnostics.</param>
    /// <param name="passphrase">Passphrase to unlock a LUKS-encrypted card, or null for an unencrypted card.</param>
    /// <param name="adoptGuard">The lock taken by an earlier session on the same card (see <see cref="DetachGuard"/>):
    /// used if it covers the volume being opened, so the card is never unlocked in between; disposed otherwise.</param>
    public static SdCardSession Open(int diskNumber, bool guardVolumes = true, char[]? passphrase = null,
        VolumeGuard? adoptGuard = null)
    {
        RawDiskStream? raw = null;
        CardReader? reader = null;
        try
        {
            raw = RawDiskStream.OpenPhysicalDrive(diskNumber);

            // Cache raw-device reads: ext4 metadata access is many small scattered reads,
            // and every uncached read is a sector-aligned DeviceIoControl round trip.
            var cached = new BlockCacheStream(SparseStream.FromStream(raw, Ownership.Dispose), Ownership.Dispose);
            raw = null; // owned by the cache stream now

            reader = CardReader.Open(cached, Ownership.Dispose, passphrase);

            // Lock once there is something to protect: the partition being read, or the encrypted one a passphrase
            // is being asked for.
            VolumeGuard? guard = null;
            if (guardVolumes &&
                (reader.Status is CardOpenStatus.Ok or CardOpenStatus.EncryptedNeedsPassphrase or CardOpenStatus.IncorrectPassphrase) &&
                reader.Volume is { } volume)
            {
                if (adoptGuard is not null && adoptGuard.Covers(diskNumber, volume.FirstByte, volume.LengthBytes))
                {
                    guard = adoptGuard;
                    adoptGuard = null;
                    guard.StopTrying(); // this session's own monitor takes over the re-checks
                    guard.RecheckNewVolumes();
                }
                else
                {
                    guard = VolumeGuard.Acquire(diskNumber, volume.FirstByte, volume.LengthBytes);
                }
            }

            adoptGuard?.Dispose();
            return new SdCardSession(guard, reader, diskNumber);
        }
        catch
        {
            adoptGuard?.Dispose();
            reader?.Dispose();
            raw?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Takes this session's volume lock away from it (so disposing the session doesn't release it), to be handed to
    /// the next session on the same card - from the one that found an encrypted card to the one that unlocks it.
    /// The caller owns the result: pass it to <see cref="Open"/> or dispose it.
    /// </summary>
    public VolumeGuard? DetachGuard()
    {
        lock (_tickLock)
        {
            var guard = _guard;
            _guard = null;
            return guard;
        }
    }

    /// <summary>
    /// Starts watching the card: every couple of seconds, asks the reader whether the medium is still there
    /// (a card pulled or swapped in a reader that stays plugged in raises no device event), and re-checks for
    /// a late-arriving volume to lock.
    /// </summary>
    public void StartMonitoring(TimeSpan? interval = null)
    {
        if (_monitor is not null)
        {
            return;
        }

        _probe = NativeMethods.CreateFile($@"\\.\PhysicalDrive{_diskNumber}", 0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite, 0, NativeMethods.OpenExisting, 0, 0);
        var period = interval ?? TimeSpan.FromSeconds(2);
        _monitor = new Timer(_ => Tick(), null, period, period);
    }

    private void Tick()
    {
        if (!Monitor.TryEnter(_tickLock))
        {
            return; // the previous tick is still running, or disposal has begun
        }

        try
        {
            if (!_disposed)
            {
                TickCore();
            }
        }
        finally
        {
            Monitor.Exit(_tickLock);
        }
    }

    private void TickCore()
    {
        if (Volatile.Read(ref _mediaLostRaised) != 0)
        {
            return;
        }

        if (_probe is { IsInvalid: false } probe &&
            !NativeMethods.DeviceIoControl(probe, IoctlStorageCheckVerify2, null, 0, null, 0, out _, 0) &&
            Marshal.GetLastWin32Error() is ErrorNotReady or ErrorMediaChanged or ErrorDeviceRemoved or
                ErrorNoSuchDevice or ErrorDeviceNotConnected or ErrorFileNotFound or ErrorNoMediaInDrive or ErrorDevNotExist)
        {
            if (Interlocked.Exchange(ref _mediaLostRaised, 1) == 0)
            {
                try
                {
                    MediaLost?.Invoke();
                }
                catch
                {
                    // a subscriber's problem
                }
            }

            return;
        }

        if (_guard is not null && Interlocked.Increment(ref _ticks) % 2 == 0 && _guard.RecheckNewVolumes())
        {
            try
            {
                ProtectionChanged?.Invoke();
            }
            catch
            {
                // a subscriber's problem
            }
        }
    }

    private const uint IoctlStorageCheckVerify2 = 0x002D0800;
    private const int ErrorFileNotFound = 2;
    private const int ErrorDevNotExist = 55;
    private const int ErrorNotReady = 21;
    private const int ErrorNoSuchDevice = 433;
    private const int ErrorMediaChanged = 1110;
    private const int ErrorNoMediaInDrive = 1112;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorDeviceRemoved = 1617;

    public void Dispose()
    {
        lock (_tickLock)
        {
            _disposed = true;
        }

        _monitor?.Dispose();
        _monitor = null;
        _probe?.Dispose();
        _reader.Dispose();
        _guard?.Dispose();
    }
}
