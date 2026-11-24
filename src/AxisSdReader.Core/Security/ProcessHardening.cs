using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AxisSdReader.Core.Security;

/// <summary>
/// Process-wide hardening for the elevated app. The app runs as administrator (raw disk access) but
/// inherits the user's environment and runs native code (libVLC, FFmpeg) that trusts it, so ordinary
/// software running as the same, unelevated user could otherwise steer what the elevated process loads
/// or writes. Everything here is best-effort and safe to call on any Windows version.
/// </summary>
public static class ProcessHardening
{
    /// <summary>libVLC 3 environment variables that redirect where it loads plugins (DLLs) and data (Lua
    /// scripts) from. libvlccore reads them through the C runtime's own copy of the environment, so they
    /// are cleared there as well as in the Win32 environment.</summary>
    private static readonly string[] VlcVariables = ["VLC_PLUGIN_PATH", "VLC_DATA_PATH", "VLC_LIB_PATH", "VLC_VERBOSE"];

    /// <summary>
    /// True when the current process runs with more than a standard user's rights: as an administrator, or on any
    /// elevated or high-integrity token (an elevated Backup Operator, say, holds backup and restore privileges without
    /// being in the Administrators group). Whatever is unsure counts as elevated: the callers use this to decide
    /// when to be careful.
    /// </summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return true;
            }

            var token = identity.AccessToken;
            if (!GetTokenInformation(token, TokenElevationClass, out int elevated, sizeof(int), out _) || elevated != 0)
            {
                return true;
            }

            // The integrity level: the last sub-authority of the label's SID (high is 0x3000).
            GetTokenInformation(token, TokenIntegrityLevelClass, 0, 0, out var needed);
            if (needed <= 0 || needed > 4096)
            {
                return true;
            }

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, needed, out _))
                {
                    return true;
                }

                var sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 || (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)count - 1)) >= HighIntegrity;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return true;
        }
    }

    private const int TokenElevationClass = 20;
    private const int TokenIntegrityLevelClass = 25;
    private const uint HighIntegrity = 0x3000;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int information, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, nint information, int length, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint index);

    /// <summary>
    /// Applies the hardening that must happen before any native media code loads: removes the user-controlled
    /// libVLC search-path variables, drops the current directory from the DLL search order, and turns on the
    /// process mitigations that stop the process following junctions planted by non-administrators
    /// (RedirectionGuard) and loading images from remote or low-integrity locations.
    /// Returns a short description of what took effect, for diagnostics.
    /// </summary>
    public static IReadOnlyList<string> Apply()
    {
        var applied = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return applied;
        }

        foreach (var name in VlcVariables)
        {
            if (ClearEnvironmentVariable(name))
            {
                applied.Add($"cleared {name}");
            }
        }

        try
        {
            if (SetDllDirectoryW(""))
            {
                applied.Add("current directory removed from DLL search");
            }
        }
        catch
        {
            // best effort
        }

        if (TrySetMitigation(ProcessRedirectionTrustPolicy, RedirectionTrustEnforce))
        {
            applied.Add("redirection guard on");
        }

        if (TrySetMitigation(ProcessImageLoadPolicy, ImageLoadNoRemote | ImageLoadNoLowLabel | ImageLoadPreferSystem32))
        {
            applied.Add("image-load policy on");
        }

        return applied;
    }

    /// <summary>
    /// Removes a variable from both copies of the process environment: the Win32 block and the C runtime
    /// (msvcrt) table that native libraries such as libvlccore read with <c>getenv</c>. The CRT copy is taken
    /// when msvcrt initialises, so clearing only the Win32 copy would not reach those libraries.
    /// Returns true if the variable was set.
    /// </summary>
    public static bool ClearEnvironmentVariable(string name)
    {
        var wasSet = Environment.GetEnvironmentVariable(name) is not null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // An empty value removes the entry from the CRT's narrow and wide tables (and the Win32 block).
                _putenv_s(name, "");
                _wputenv_s(name, "");
            }
            catch
            {
                // msvcrt unavailable — nothing native can read it then either
            }
        }

        Environment.SetEnvironmentVariable(name, null);
        return wasSet;
    }

    /// <summary>
    /// The Windows folder as the kernel reports it (<c>GetSystemWindowsDirectoryW</c>), never from the
    /// environment: %SystemRoot% and %windir% can be overridden per user, which is a classic way to steer an
    /// elevated process. Null off Windows or if the call fails.
    /// </summary>
    public static string? TrustedWindowsDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = new char[260];
        var length = GetSystemWindowsDirectoryW(buffer, (uint)buffer.Length);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetSystemWindowsDirectoryW(buffer, (uint)buffer.Length);
        }

        return length == 0 || length > buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    /// <summary>
    /// The user's local application-data folder at its standard place in their profile. Not the redirected location
    /// (the per-user "User Shell Folders" registry key, or %LOCALAPPDATA%), which any program running as the user can
    /// change - and so aim an elevated process's writes somewhere else. Null if it can't be determined.
    /// </summary>
    public static string? DefaultLocalAppData()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        const uint KfFlagDefaultPath = 0x400;
        const uint KfFlagDontVerify = 0x4000;
        var localAppData = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        if (SHGetKnownFolderPath(localAppData, KfFlagDefaultPath | KfFlagDontVerify, 0, out var path) != 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    /// <summary>Reads a variable from the C runtime's (msvcrt) environment table — what native code sees.</summary>
    public static string? GetCrtEnvironmentVariable(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable(name);
        }

        var value = _wgetenv(name);
        return value == IntPtr.Zero ? null : Marshal.PtrToStringUni(value);
    }

    /// <summary>Sets a variable in the C runtime's (msvcrt) environment table. Test support.</summary>
    public static void SetCrtEnvironmentVariable(string name, string value)
    {
        if (OperatingSystem.IsWindows())
        {
            _wputenv_s(name, value);
        }
    }

    /// <summary>
    /// Describes why the given install directory is unsafe for an elevated app, or returns null when it is
    /// safe. A directory that a non-administrator can modify — e.g. a folder unzipped into Downloads, or the
    /// %TEMP% cache a single-file build extracts itself to — lets any program running as the user replace the
    /// DLLs or ffmpeg.exe the elevated app loads next. Everything below it is checked too: the app loads DLLs and
    /// plugins, runs ffmpeg.exe and reads its runtime configuration from those files, so one a user can change
    /// (an ffmpeg folder moved in from Downloads keeps its owner and permissions, say) is just as bad, and so is a
    /// link that leads out of the folder.
    /// </summary>
    public static string? DescribeUnsafeInstallLocation(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return $"{full} is a network folder (whoever controls that share controls what this app loads).";
            }

            if (new DriveInfo(Path.GetPathRoot(full)!).DriveFormat is not ("NTFS" or "ReFS"))
            {
                return $"{full} is on a drive without file permissions (FAT or exFAT, say): any program can change it.";
            }

            // A link anywhere on the way (a junction, a symbolic link, a mounted folder) can be changed to lead
            // elsewhere, and every DLL the app loads later is found through it again - so the checks below, made on
            // the path as written, are only meaningful when it has none.
            for (var component = full; Path.GetDirectoryName(component) is not null; component = Path.GetDirectoryName(component)!)
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                {
                    return $"{component} is a link (it may lead to a folder other programs can change).";
                }
            }

            if (WritableByNonAdmins(new DirectoryInfo(full)) is { } who)
            {
                return $"{full} can be modified by {who}.";
            }

            if (Path.GetDirectoryName(full) is { } parent && WritableByNonAdmins(new DirectoryInfo(parent)) is { } parentWho)
            {
                return $"Its parent folder {parent} can be modified by {parentWho}.";
            }

            // Every folder further up: whoever can rename or replace one of them can swap the whole tree.
            for (var ancestor = Path.GetDirectoryName(Path.GetDirectoryName(full) ?? ""); ancestor is { Length: > 0 };
                 ancestor = Path.GetDirectoryName(ancestor))
            {
                if (WritableByNonAdmins(new DirectoryInfo(ancestor), AncestorRights) is { } ancestorWho)
                {
                    return $"The folder {ancestor} above it can be changed by {ancestorWho}.";
                }
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
            };
            foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos("*", options))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return $"{entry.FullName} is a link (it may lead to a folder other programs can change).";
                }

                if (WritableByNonAdmins(entry) is { } entryWho)
                {
                    return $"{entry.FullName} can be modified by {entryWho}.";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            // Unknown is not safe.
            return $"Part of {directory} could not be checked ({ex.GetType().Name}: {ex.Message}).";
        }
    }

    private static readonly HashSet<string> TrustedSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18",                                                         // SYSTEM
        "S-1-5-32-544",                                                     // Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",   // TrustedInstaller
        "S-1-3-0",                                                          // CREATOR OWNER (applies to children only)
    };

    [SupportedOSPlatform("windows")]
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>On folders further up, what lets someone rename or replace what is below: deleting entries in it,
    /// deleting (so renaming) it, or taking over its permissions.</summary>
    [SupportedOSPlatform("windows")]
    private const FileSystemRights AncestorRights =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    [SupportedOSPlatform("windows")]
    private static string? WritableByNonAdmins(FileSystemInfo item, FileSystemRights dangerous = WriteRights)
    {
        if (!OperatingSystem.IsWindows() || !item.Exists)
        {
            return null;
        }

        const AccessControlSections Sections = AccessControlSections.Access | AccessControlSections.Owner;
        FileSystemSecurity security = item is DirectoryInfo folder ? folder.GetAccessControl(Sections) : ((FileInfo)item).GetAccessControl(Sections);
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !TrustedSids.Contains(owner.Value))
        {
            return Describe(owner) + " (owner)";
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                TrustedSids.Contains(sid.Value) ||
                (Effective(rule.FileSystemRights) & dangerous) == 0)
            {
                continue;
            }

            return Describe(sid);
        }

        return null;
    }

    /// <summary>An ACE's rights with any generic bits mapped to the file rights they stand for.</summary>
    [SupportedOSPlatform("windows")]
    private static FileSystemRights Effective(FileSystemRights rights)
    {
        var mask = (uint)rights;
        if ((mask & 0x10000000) != 0)
        {
            mask |= 0x1F01FF; // GENERIC_ALL -> FILE_ALL_ACCESS
        }

        if ((mask & 0x40000000) != 0)
        {
            mask |= 0x120116; // GENERIC_WRITE -> FILE_GENERIC_WRITE
        }

        return (FileSystemRights)mask;
    }

    [SupportedOSPlatform("windows")]
    private static string Describe(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch
        {
            return sid.Value;
        }
    }

    private const int ProcessImageLoadPolicy = 10;
    private const int ProcessRedirectionTrustPolicy = 16;
    private const uint RedirectionTrustEnforce = 0x1;
    private const uint ImageLoadNoRemote = 0x1;
    private const uint ImageLoadNoLowLabel = 0x2;
    private const uint ImageLoadPreferSystem32 = 0x4;

    private static bool TrySetMitigation(int policy, uint flags)
    {
        try
        {
            var value = flags;
            return SetProcessMitigationPolicy(policy, ref value, sizeof(uint));
        }
        catch
        {
            return false; // not supported on this Windows build
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, nint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string pathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetSystemWindowsDirectoryW([Out] char[] buffer, uint size);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, out nint path);

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int _putenv_s(string name, string value);

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int _wputenv_s(string name, string value);

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern IntPtr _wgetenv(string name);
}
