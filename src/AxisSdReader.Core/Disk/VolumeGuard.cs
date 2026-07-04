using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AxisSdReader.Core.Disk;

/// <summary>
/// Protects the volumes of a physical disk from interference while the card is being read:
/// locks each volume (<c>FSCTL_LOCK_VOLUME</c> — excludes all other user-mode writers),
/// dismounts any mounted (RAW) filesystem, and removes drive letters so Explorer's
/// "format this disk?" prompt cannot appear. The locks are held until disposal.
/// None of these operations write to the disk contents. Requires administrator rights.
/// </summary>
public sealed class VolumeGuard : IDisposable
{
    private readonly List<SafeFileHandle> _lockedVolumes = [];

    /// <summary>Per-volume outcome messages, for display/diagnostics.</summary>
    public IReadOnlyList<string> Log => _log;

    private readonly List<string> _log = [];

    /// <summary>
    /// True only when every volume on the disk was successfully locked (or the disk had no volumes to
    /// lock). When false, at least one volume could not be locked, so the card is NOT protected against
    /// Windows re-mounting it and offering to format it — callers must not present it as read-only-safe.
    /// </summary>
    public bool AllVolumesLocked { get; private set; } = true;

    private VolumeGuard()
    {
    }

    /// <summary>Guards all volumes currently associated with the given disk.</summary>
    public static VolumeGuard Acquire(int diskNumber)
    {
        var guard = new VolumeGuard();
        var disks = DiskEnumerator.GetPhysicalDisks();
        var disk = disks.FirstOrDefault(d => d.DiskNumber == diskNumber)
            ?? throw new IOException($"Physical disk {diskNumber} was not found.");

        foreach (var volume in disk.Volumes)
        {
            guard.GuardVolume(volume);
        }

        if (disk.Volumes.Count == 0)
        {
            guard._log.Add("No volumes are mounted for this disk; nothing to lock.");
        }

        return guard;
    }

    private void GuardVolume(VolumeOnDisk volume)
    {
        var path = volume.VolumeGuidPath.TrimEnd('\\');

        var handle = NativeMethods.CreateFile(path,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            0, NativeMethods.OpenExisting, 0, 0);

        if (handle.IsInvalid)
        {
            AllVolumesLocked = false;
            _log.Add($"{path}: could not open volume (Win32 error {Marshal.GetLastWin32Error()}).");
            return;
        }

        var locked = false;
        for (var attempt = 0; attempt < 5 && !locked; attempt++)
        {
            locked = NativeMethods.DeviceIoControl(handle, NativeMethods.FsctlLockVolume,
                null, 0, null, 0, out _, 0);
            if (!locked)
            {
                Thread.Sleep(100);
            }
        }

        if (!locked)
        {
            // The lock is the ONLY thing that keeps Windows from re-mounting the volume and offering to
            // format it. Without it, dismounting or removing the drive letter provides no lasting
            // protection and would only thrash the volume — so leave it untouched and report the card as
            // not fully protected. The device handle (shared, no lock) protects nothing, so release it.
            AllVolumesLocked = false;
            _log.Add($"{path}: could NOT lock (in use); left untouched — card is not protected.");
            handle.Dispose();
            return;
        }

        // Locked: safe to dismount the current (RAW) filesystem and remove drive letters so Explorer's
        // "format this disk?" prompt cannot appear, then hold the lock for our whole lifetime.
        var dismounted = NativeMethods.DeviceIoControl(handle, NativeMethods.FsctlDismountVolume,
            null, 0, null, 0, out _, 0);

        foreach (var mountPoint in volume.MountPoints)
        {
            if (NativeMethods.DeleteVolumeMountPoint(mountPoint))
            {
                _log.Add($"Removed mount point {mountPoint}");
            }
            else
            {
                _log.Add($"{mountPoint}: could not remove mount point (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }

        _log.Add($"{path}: locked=true, dismounted={dismounted}");
        _lockedVolumes.Add(handle); // hold the lock for our lifetime
    }

    public void Dispose()
    {
        // Drive letters removed during the session are intentionally NOT restored here: reassigning a
        // letter to a still-RAW (ext4) card would make Windows immediately offer to format it again.
        // The user can safely re-insert the card to get a fresh mount if they want one.
        foreach (var handle in _lockedVolumes)
        {
            NativeMethods.DeviceIoControl(handle, NativeMethods.FsctlUnlockVolume, null, 0, null, 0, out _, 0);
            handle.Dispose();
        }

        _lockedVolumes.Clear();
    }
}
