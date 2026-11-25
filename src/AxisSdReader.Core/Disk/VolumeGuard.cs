using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AxisSdReader.Core.Disk;

/// <summary>
/// Protects the card's volume from interference while it is being read: locks it
/// (<c>FSCTL_LOCK_VOLUME</c> — excludes all other user-mode writers), dismounts any mounted (RAW)
/// filesystem, and removes its drive letters so Explorer's "format this disk?" prompt cannot appear.
/// The locks are held until disposal. None of these operations write to the disk contents.
/// Only volumes that overlap the card's own partition are touched — other partitions on the same disk
/// (say, a data partition on a USB drive) are left alone. Requires administrator rights.
/// <para>A lock that fails (Explorer or a virus scanner often has a new volume open for a moment) is tried again on
/// every check; a lock whose device has gone (the reader re-plugged) is dropped and taken again on the new device.
/// Checks and disposal never overlap, so nothing is locked after the guard is disposed.</para>
/// </summary>
public sealed class VolumeGuard : IDisposable
{
    private readonly int _diskNumber;
    private readonly long _rangeStart;
    private readonly long _rangeLength;
    private readonly Dictionary<string, SafeFileHandle> _locked = new(StringComparer.OrdinalIgnoreCase); // held by us
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase); // other partitions
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);  // tried, retried each check
    private readonly List<string> _log = [];
    private readonly object _sync = new();  // the sets above and the log
    private readonly object _life = new();  // a check, or disposal: never both at once
    private readonly SafeFileHandle _disk;  // the disk itself, to tell whether the device is still the same one
    private Timer? _retry;
    private bool _disposed;

    /// <summary>Per-volume outcome messages, for display/diagnostics.</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_sync)
            {
                return _log.ToList();
            }
        }
    }

    /// <summary>
    /// True only when every volume on the card's partition was successfully locked (or Windows has created no
    /// volume for it). When false, at least one volume could not be locked, so the card is NOT protected
    /// against Windows re-mounting it and offering to format it — callers must not present it as safe.
    /// </summary>
    public bool AllVolumesLocked
    {
        get
        {
            lock (_sync)
            {
                return _failed.Count == 0;
            }
        }
    }

    private VolumeGuard(int diskNumber, long rangeStart, long rangeLength)
    {
        _diskNumber = diskNumber;
        _rangeStart = rangeStart;
        _rangeLength = rangeLength;
        _disk = NativeMethods.CreateFile($@"\\.\PhysicalDrive{diskNumber}", 0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite, 0, NativeMethods.OpenExisting, 0, 0);
    }

    /// <summary>Guards the volumes of <paramref name="diskNumber"/> that overlap the byte range of the card's
    /// filesystem partition (pass the whole disk for a partitionless card).</summary>
    public static VolumeGuard Acquire(int diskNumber, long rangeStart, long rangeLength)
    {
        var guard = new VolumeGuard(diskNumber, rangeStart, rangeLength);

        // Windows creates the partition's volume a moment after the disk itself appears, so a card opened
        // straight after insertion may have none yet. Wait briefly for it rather than declaring "nothing to
        // lock" and leaving it to appear unlocked a second later.
        IReadOnlyList<VolumeOnDisk> volumes = [];
        for (var attempt = 0; attempt < 12; attempt++)
        {
            volumes = DiskEnumerator.GetVolumesOnDisk(diskNumber);
            if (volumes.Any(guard.IsCardVolume))
            {
                break;
            }

            Thread.Sleep(250);
        }

        lock (guard._life)
        {
            guard.GuardNew(volumes);
        }

        lock (guard._sync)
        {
            if (guard._locked.Count == 0 && guard._failed.Count == 0)
            {
                guard._log.Add("Windows has created no volume for the card's partition; nothing to lock (checked again while open).");
            }
        }

        return guard;
    }

    /// <summary>Whether this guard protects exactly the given byte range of a device that is still there (so a
    /// session for it may adopt it).</summary>
    public bool Covers(int diskNumber, long rangeStart, long rangeLength) =>
        diskNumber == _diskNumber && rangeStart == _rangeStart && rangeLength == _rangeLength && !IsStale();

    /// <summary>The disk this guard was made for is gone, or isn't the same device any more (it was re-plugged).</summary>
    public bool IsStale() => _disk.IsInvalid || !IsDeviceAlive(_disk);

    /// <summary>
    /// Keeps re-checking on a timer until disposed or adopted (<see cref="StopTrying"/>) — for the time a
    /// passphrase prompt is up and no session is watching the card.
    /// </summary>
    public void KeepTrying(TimeSpan period)
    {
        lock (_life)
        {
            if (_disposed || _retry is not null)
            {
                return;
            }

            _retry = new Timer(_ => RecheckNewVolumes(), null, period, period);
        }
    }

    /// <summary>Stops <see cref="KeepTrying"/> (a session's own monitor takes over).</summary>
    public void StopTrying()
    {
        lock (_life)
        {
            _retry?.Dispose();
            _retry = null;
        }
    }

    /// <summary>
    /// Re-enumerates the disk: locks any card volume that has appeared since, tries again to lock any that
    /// couldn't be before, drops locks whose device has gone (so the new device gets locked), and forgets failures
    /// for volumes that no longer exist. Returns true when the protection state changed.
    /// </summary>
    public bool RecheckNewVolumes()
    {
        if (!Monitor.TryEnter(_life))
        {
            return false; // a check is already running, or the guard is being disposed
        }

        try
        {
            if (_disposed)
            {
                return false;
            }

            var changed = DropDeadLocks();
            var volumes = DiskEnumerator.GetVolumesOnDisk(_diskNumber);
            changed |= GuardNew(volumes);
            lock (_sync)
            {
                var present = volumes.Select(v => v.VolumeGuidPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                changed |= _failed.RemoveWhere(path => !present.Contains(path)) > 0;
            }

            return changed;
        }
        catch
        {
            return false;
        }
        finally
        {
            Monitor.Exit(_life);
        }
    }

    private bool IsCardVolume(VolumeOnDisk volume) =>
        volume.Extents.Count == 0 || volume.Overlaps(_diskNumber, _rangeStart, _rangeLength);

    /// <summary>Called with <see cref="_life"/> held.</summary>
    private bool DropDeadLocks()
    {
        List<KeyValuePair<string, SafeFileHandle>> dead;
        lock (_sync)
        {
            dead = _locked.Where(kv => !IsDeviceAlive(kv.Value)).ToList();
            foreach (var (path, _) in dead)
            {
                _locked.Remove(path);
                _log.Add($"{path.TrimEnd('\\')}: its device went away; locking the new one.");
            }
        }

        foreach (var (_, handle) in dead)
        {
            handle.Dispose();
        }

        return dead.Count > 0;
    }

    /// <summary>Called with <see cref="_life"/> held.</summary>
    private bool GuardNew(IReadOnlyList<VolumeOnDisk> volumes)
    {
        var changed = false;
        foreach (var volume in volumes)
        {
            var path = volume.VolumeGuidPath;
            bool retry;
            lock (_sync)
            {
                if (_locked.ContainsKey(path) || _ignored.Contains(path))
                {
                    continue;
                }

                if (!IsCardVolume(volume))
                {
                    _ignored.Add(path);
                    _log.Add($"{path.TrimEnd('\\')}: another partition on this disk; left alone.");
                    continue;
                }

                retry = _failed.Contains(path);
            }

            var handle = GuardVolume(volume, logFailure: !retry);
            lock (_sync)
            {
                if (handle is not null)
                {
                    _locked[path] = handle;
                    changed |= _failed.Remove(path) || !retry;
                }
                else
                {
                    changed |= _failed.Add(path);
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Locks one card volume and returns the handle that holds the lock, or null. Read access first (a lock needs no
    /// write access, and this app never writes). FSCTL_LOCK_VOLUME reports a volume someone else has open as
    /// ERROR_ACCESS_DENIED — the same code as a handle without enough access — so that error switches to read/write
    /// access once, and either way the lock is tried again for a moment.
    /// </summary>
    private SafeFileHandle? GuardVolume(VolumeOnDisk volume, bool logFailure)
    {
        var path = volume.VolumeGuidPath.TrimEnd('\\');
        var access = NativeMethods.GenericRead;
        var handle = Open(path, access);
        var locked = false;
        var error = 0;
        for (var attempt = 0; attempt < 10 && !locked; attempt++)
        {
            if (handle.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                break;
            }

            locked = NativeMethods.DeviceIoControl(handle, NativeMethods.FsctlLockVolume, null, 0, null, 0, out _, 0);
            if (locked)
            {
                break;
            }

            error = Marshal.GetLastWin32Error();
            if (error == ErrorAccessDenied && access == NativeMethods.GenericRead)
            {
                handle.Dispose();
                access = NativeMethods.GenericRead | NativeMethods.GenericWrite;
                handle = Open(path, access);
                continue;
            }

            Thread.Sleep(100);
        }

        if (!locked)
        {
            // The lock is the ONLY thing that keeps Windows from re-mounting the volume and offering to
            // format it. Without it, dismounting or removing the drive letter provides no lasting
            // protection and would only thrash the volume — so leave it untouched and report the card as
            // not fully protected (it is tried again on every check). The handle protects nothing; release it.
            handle.Dispose();
            if (logFailure)
            {
                lock (_sync)
                {
                    _log.Add($"{path}: could NOT lock (Win32 error {error}); left untouched — card is not protected yet (retrying).");
                }
            }

            return null;
        }

        // Locked: safe to dismount the current (RAW) filesystem and remove drive letters so Explorer's
        // "format this disk?" prompt cannot appear, then hold the lock for our whole lifetime.
        var dismounted = NativeMethods.DeviceIoControl(handle, NativeMethods.FsctlDismountVolume,
            null, 0, null, 0, out _, 0);

        var messages = new List<string>();
        foreach (var mountPoint in volume.MountPoints)
        {
            messages.Add(NativeMethods.DeleteVolumeMountPoint(mountPoint)
                ? $"Removed mount point {mountPoint}"
                : $"{mountPoint}: could not remove mount point (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        lock (_sync)
        {
            _log.AddRange(messages);
            _log.Add($"{path}: locked=true, dismounted={dismounted}");
        }

        return handle;
    }

    private static SafeFileHandle Open(string path, uint access) =>
        NativeMethods.CreateFile(path, access, NativeMethods.FileShareRead | NativeMethods.FileShareWrite, 0,
            NativeMethods.OpenExisting, 0, 0);

    /// <summary>Whether a handle's device still answers (a removed or re-plugged device's old handles don't).</summary>
    private static bool IsDeviceAlive(SafeFileHandle handle)
    {
        if (NativeMethods.DeviceIoControl(handle, IoctlStorageCheckVerify2, null, 0, null, 0, out _, 0))
        {
            return true;
        }

        return Marshal.GetLastWin32Error() is not (ErrorDeviceRemoved or ErrorNoSuchDevice or ErrorDeviceNotConnected or
            ErrorDevNotExist or ErrorFileNotFound or ErrorInvalidHandle or ErrorNoMediaInDrive or ErrorMediaChanged);
    }

    public void Dispose()
    {
        // Drive letters removed during the session are intentionally NOT restored here: reassigning a
        // letter to a still-RAW (ext4) card would make Windows immediately offer to format it again.
        // The user can safely re-insert the card to get a fresh mount if they want one.
        lock (_life)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _retry?.Dispose();
            _retry = null;
            lock (_sync)
            {
                foreach (var handle in _locked.Values)
                {
                    NativeMethods.DeviceIoControl(handle, NativeMethods.FsctlUnlockVolume, null, 0, null, 0, out _, 0);
                    handle.Dispose();
                }

                _locked.Clear();
            }

            _disk.Dispose();
        }
    }

    private const uint IoctlStorageCheckVerify2 = 0x002D0800;
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorDevNotExist = 55;
    private const int ErrorNoSuchDevice = 433;
    private const int ErrorMediaChanged = 1110;
    private const int ErrorNoMediaInDrive = 1112;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorDeviceRemoved = 1617;
}
