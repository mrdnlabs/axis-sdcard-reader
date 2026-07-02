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

        // Dismount works (and is useful) even if the lock could not be obtained.
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

        _log.Add($"{path}: locked={locked}, dismounted={dismounted}");

        if (locked)
        {
            _lockedVolumes.Add(handle); // hold the lock for our lifetime
        }
        else
        {
            handle.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var handle in _lockedVolumes)
        {
            NativeMethods.DeviceIoControl(handle, NativeMethods.FsctlUnlockVolume, null, 0, null, 0, out _, 0);
            handle.Dispose();
        }

        _lockedVolumes.Clear();
    }
}
