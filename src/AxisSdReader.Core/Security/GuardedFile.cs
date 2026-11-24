using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AxisSdReader.Core.Security;

/// <summary>
/// File creation for an elevated process writing into folders an unelevated user controls (their profile,
/// a folder they picked). Such a user — or malware running as them — could swap a folder for a junction or
/// plant a link between the moment the path is chosen and the moment it is opened, steering the elevated
/// write onto any file on the system. So a folder is opened once, that handle is checked (it is the plain folder
/// that was chosen, not a link), kept open, and everything is created relative to it: no path is walked a second
/// time, so a link made afterwards — even an empty folder turned into a junction where it stands — can't redirect
/// anything. Nothing here follows a link it did not see, hard-linked files are refused, and what is created stays
/// pinned while in use.
/// <para>A pin is a handle opened for a data right (read data / list folder) without FILE_SHARE_DELETE. The
/// data right matters: Windows skips share checks entirely for handles that hold only attribute rights, so
/// such a handle would not stop a rename at all.</para>
/// </summary>
public static class GuardedFile
{
    /// <summary>
    /// Resolves a directory the user chose to its real location right now (following any links in it), as a
    /// plain path. <see cref="OpenFolder"/> then holds exactly that folder.
    /// </summary>
    public static string ResolveDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!OperatingSystem.IsWindows())
        {
            return full;
        }

        using var handle = CreateFileW(full, FileReadAttributes | Synchronize, FileShareAll, 0, OpenExisting,
            FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
        {
            throw new IOException($"The folder {full} cannot be opened.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return FinalPath(handle);
    }

    /// <summary>
    /// Opens the folder at <paramref name="resolvedDirectory"/> (from <see cref="ResolveDirectory"/>) to create files
    /// in: it must still be that plain folder — not a link, not moved. The result holds it open (so it can't be
    /// renamed, deleted or replaced) until disposed, and creates files relative to it.
    /// </summary>
    /// <exception cref="IOException">The folder can't be opened, or is no longer the one that was resolved.</exception>
    public static GuardedFolder OpenFolder(string resolvedDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new GuardedFolder(null, Path.GetFullPath(resolvedDirectory));
        }

        var handle = CreateFileW(resolvedDirectory, FileListDirectory | FileReadAttributes | ReadControl | Synchronize,
            FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new GuardedFileException($"The folder {resolvedDirectory} cannot be opened.", error);
        }

        try
        {
            if (!IsPlainDirectory(handle) ||
                !string.Equals(FinalPath(handle).TrimEnd('\\'), resolvedDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"{resolvedDirectory} is no longer the folder that was chosen (it was moved, or replaced by a link). Nothing was written; choose the folder again.");
            }

            return new GuardedFolder(handle, resolvedDirectory);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Appends text to <c>root\sub1\…\fileName</c>, creating the folders as needed, without following any
    /// junction, symbolic link or hard link below <paramref name="root"/>. Each folder is opened relative to the one
    /// above it, which is held open meanwhile: no path is walked twice. Returns false — and writes nothing — if
    /// anything is not what it should be. Never throws.
    /// <para>The file never grows past <paramref name="maxBytes"/>: an entry that would take it past that starts
    /// the file over, so something that keeps failing (a damaged card, say) can't fill the disk.</para>
    /// </summary>
    public static bool TryAppendText(string root, IReadOnlyList<string> subdirectories, string fileName, string text,
        long maxBytes = DefaultMaxAppendBytes)
    {
        maxBytes = Math.Max(1024, maxBytes);
        if (text.Length > maxBytes / 4)
        {
            text = text[..(int)(maxBytes / 4)] + "…\n"; // UTF-8 is at most 4 bytes a char here: the entry always fits
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var dir = Path.Combine([root, .. subdirectories]);
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, fileName), text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (!IsPlainName(fileName) || subdirectories.Any(sub => !IsPlainName(sub)))
        {
            return false;
        }

        var pinned = new List<SafeFileHandle>();
        try
        {
            var rootPath = Path.GetFullPath(root);
            if (TryPinDirectory(rootPath, pinned) is not { } current)
            {
                return false;
            }

            foreach (var sub in subdirectories)
            {
                var next = OpenRelative(current, sub, FileListDirectory | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite,
                    NtFileOpenIf, NtDirectoryFile, out _, out _);
                if (next is null)
                {
                    return false;
                }

                pinned.Add(next);
                if (!IsPlainDirectory(next))
                {
                    return false;
                }

                current = next;
            }

            using var file = OpenRelative(current, fileName, FileAppendData | FileWriteData | FileReadAttributes | Synchronize,
                FileShareRead, NtFileOpenIf, NtNonDirectoryFile, out _, out var created);
            if (file is null || !GetFileInformationByHandle(file, out var info) ||
                (info.FileAttributes & FileAttributeReparsePoint) != 0 || info.NumberOfLinks != 1)
            {
                return false;
            }

            // And where Windows says it is: in the folders just opened. (Relative opens never follow a link the folder
            // became; this is the check that they didn't.) A file made somewhere else is removed again.
            if (!string.Equals(FinalPath(file), Path.Combine([rootPath, .. subdirectories, fileName]), StringComparison.OrdinalIgnoreCase))
            {
                if (created)
                {
                    file.Dispose();
                    DeleteRelative(current, fileName, FileId.From(info));
                }

                return false;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            if (size + bytes.Length > maxBytes)
            {
                long empty = 0;
                if (!SetFileInformationByHandle(file, FileEndOfFileInfo, ref empty, sizeof(long)))
                {
                    return false;
                }
            }

            // With write-data access a write goes where the file pointer is, so put it at the end.
            return SetFilePointerEx(file, 0, out _, FileEnd) &&
                   WriteFile(file, bytes, bytes.Length, out var written, 0) && written == bytes.Length;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var handle in pinned)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// Creates a new, empty file in a directory previously resolved with <see cref="ResolveDirectory"/> (see
    /// <see cref="GuardedFolder.CreateExclusive"/>). For several files in one folder, open it once with
    /// <see cref="OpenFolder"/> instead.
    /// </summary>
    /// <exception cref="IOException">The file exists (<see cref="GuardedFileException.IsAlreadyExists"/>) or the
    /// directory no longer resolves to where it did.</exception>
    public static GuardedOutputFile CreateExclusive(string resolvedDirectory, string fileName)
    {
        if (!IsPlainName(fileName))
        {
            throw new ArgumentException($"'{fileName}' is not a plain file name.", nameof(fileName));
        }

        using var folder = OpenFolder(resolvedDirectory);
        return folder.CreateExclusive(fileName); // the file keeps its own hold on the folder
    }

    /// <summary>
    /// Whether the user this app runs for could create files in <paramref name="folder"/> without administrator
    /// rights. An elevated copy holds the administrator half of a split token, so this checks the folder's
    /// permissions (and integrity label) against the linked, unelevated half: being elevated must never decide where
    /// an export may land. Network folders never qualify: a server can redirect what an elevated process opens
    /// there (to this very machine, say), whatever its name. Volumes without permissions (FAT, exFAT) are writable by
    /// anyone. When anything can't be read, the answer is no.
    /// </summary>
    public static bool IsUserWritable(GuardedFolder folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (folder.Handle is not { } handle || folder.Path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var token = OpenUserToken();
            if (token is null)
            {
                return false;
            }

            if (GetVolumeInformationByHandleW(handle, 0, 0, out _, out _, out var volumeFlags, 0, 0) &&
                (volumeFlags & FilePersistentAcls) == 0)
            {
                return true; // no permissions on this volume
            }

            if (GetSecurityInfo(handle, SeFileObject, OwnerSecurityInformation | GroupSecurityInformation |
                    DaclSecurityInformation | LabelSecurityInformation, 0, 0, 0, 0, out var descriptor) != 0)
            {
                return false;
            }

            try
            {
                var mapping = new GenericMapping
                {
                    GenericRead = 0x120089,
                    GenericWrite = 0x120116,
                    GenericExecute = 0x1200A0,
                    GenericAll = 0x1F01FF,
                };
                var privileges = new byte[256];
                var privilegesLength = privileges.Length;
                return AccessCheck(descriptor, token, FileAddFile, ref mapping, privileges, ref privilegesLength,
                           out var granted, out var allowed) && allowed && (granted & FileAddFile) != 0;
            }
            finally
            {
                LocalFree(descriptor);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary><see cref="IsUserWritable(GuardedFolder)"/> for a resolved folder path, opened just for the check.</summary>
    public static bool IsUserWritable(string resolvedDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (resolvedDirectory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var folder = OpenFolder(resolvedDirectory);
            return IsUserWritable(folder);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The token of the user without elevation, at identification level (enough to check access with, useless for
    /// acting as them): the linked token of an elevated split token; for an elevated token without one (UAC switched
    /// off, the built-in Administrator) this process's token with Administrators deny-only at medium integrity -
    /// what FFmpeg runs on; else this process's own.
    /// </summary>
    private static TokenHandle? OpenUserToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out var process))
        {
            process.Dispose();
            return null;
        }

        using (process)
        {
            if (GetTokenInformation(process, TokenElevationTypeClass, out int elevation, sizeof(int), out _) &&
                elevation == TokenElevationTypeFull)
            {
                return GetTokenInformation(process, TokenLinkedTokenClass, out nint linked, nint.Size, out _)
                    ? new TokenHandle(linked)
                    : null;
            }

            if (OperatingSystem.IsWindows() && ProcessHardening.IsElevated())
            {
                // No unelevated half to ask: being elevated must still never decide where an export may land.
                using var restricted = Media.ChildProcess.CreateRestrictedUserToken();
                if (DuplicateToken(restricted, SecurityIdentification, out var withoutAdmin))
                {
                    return withoutAdmin;
                }

                withoutAdmin.Dispose();
                return null;
            }

            if (DuplicateToken(process, SecurityIdentification, out var duplicate))
            {
                return duplicate;
            }

            duplicate.Dispose();
            return null;
        }
    }

    /// <summary>A name with no path, stream, or device meaning: no separators, colons, control characters,
    /// trailing dots/spaces, or reserved device names (CON, NUL, COM1, …).</summary>
    public static bool IsPlainName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name is "." or "..")
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c < 32 || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                return false;
            }
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return false;
        }

        var stem = name.Split('.')[0].Trim().ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
               !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                 char.IsAsciiDigit(stem[3]));
    }

    /// <summary>Opens the folder at <paramref name="path"/> as a pin (see the class remarks) and checks it is that plain
    /// folder; the handle goes into <paramref name="pinned"/> either way. Null when it isn't.</summary>
    private static SafeFileHandle? TryPinDirectory(string path, List<SafeFileHandle> pinned)
    {
        var handle = CreateFileW(path, FileListDirectory | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite, 0, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        pinned.Add(handle);
        return IsPlainDirectory(handle) && string.Equals(FinalPath(handle).TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? handle
            : null;
    }

    private static bool IsPlainDirectory(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var info) &&
        (info.FileAttributes & FileAttributeDirectory) != 0 &&
        (info.FileAttributes & FileAttributeReparsePoint) == 0;

    /// <summary>
    /// Opens or creates <paramref name="name"/> (a plain name) inside the folder <paramref name="folder"/> is open on,
    /// relative to that handle: the folder's own path is never looked up again, and a link at the name itself is
    /// opened as the link, never followed. Null on failure, with the Win32 error.
    /// </summary>
    internal static SafeFileHandle? OpenRelative(SafeFileHandle folder, string name, uint access, uint share, uint disposition,
        uint options, out int error, out bool created)
    {
        error = 0;
        created = false;
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeName = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var added = false;
        try
        {
            folder.DangerousAddRef(ref added);
            Marshal.StructureToPtr(new UnicodeString
            {
                Length = (ushort)(name.Length * 2),
                MaximumLength = (ushort)(name.Length * 2 + 2),
                Buffer = nameBuffer,
            }, unicodeName, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = folder.DangerousGetHandle(),
                ObjectName = unicodeName,
                Attributes = ObjCaseInsensitive,
            };
            var status = NtCreateFile(out var handle, access, ref attributes, out var io, 0, FileAttributeNormal, share, disposition,
                options | NtOpenReparsePoint | NtSynchronousIoNonalert, 0, 0);
            if (status < 0)
            {
                handle.Dispose();
                error = RtlNtStatusToDosError(status);
                return null;
            }

            created = io.Information == NtFileCreated;
            return handle;
        }
        finally
        {
            if (added)
            {
                folder.DangerousRelease();
            }

            Marshal.FreeHGlobal(unicodeName);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    /// <summary>Deletes <paramref name="name"/> in <paramref name="folder"/> (relative to the handle, without following
    /// a link) only if it is still the file <paramref name="id"/>.</summary>
    internal static void DeleteRelative(SafeFileHandle folder, string name, FileId id)
    {
        using var handle = OpenRelative(folder, name, Delete | FileReadAttributes | Synchronize, FileShareAll, NtFileOpen,
            NtNonDirectoryFile, out _, out _);
        if (handle is null || !GetFileInformationByHandle(handle, out var info) || FileId.From(info) != id)
        {
            return;
        }

        byte delete = 1;
        SetFileInformationByHandle(handle, 4 /* FileDispositionInfo */, ref delete, 1);
    }

    internal static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(handle, buffer, buffer.Length, 0);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandleW(handle, buffer, buffer.Length, 0);
        }

        if (length == 0)
        {
            throw new IOException("Could not resolve the opened path.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    internal readonly record struct FileId(uint Volume, uint High, uint Low)
    {
        public static FileId From(ByHandleFileInformation info) => new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    /// <summary>The largest <see cref="TryAppendText"/> lets a file grow by default.</summary>
    public const long DefaultMaxAppendBytes = 1024 * 1024;

    internal const uint FileReadData = 0x1;
    internal const uint FileWriteData = 0x2;
    internal const uint FileListDirectory = 0x1;
    internal const uint FileAddFile = 0x2;
    internal const uint FileReadAttributes = 0x80;
    internal const uint FileAppendData = 0x4;
    internal const uint ReadControl = 0x20000;
    internal const uint Delete = 0x10000;
    internal const uint Synchronize = 0x100000;
    internal const uint FileShareRead = 1;
    internal const uint FileShareWrite = 2;
    internal const uint FileShareDelete = 4;
    internal const uint FileShareAll = 7;
    internal const uint CreateNew = 1;
    internal const uint OpenExisting = 3;
    internal const uint OpenAlways = 4;
    internal const uint FileAttributeNormal = 0x80;
    internal const uint FileAttributeDirectory = 0x10;
    internal const uint FileAttributeReparsePoint = 0x400;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const int ErrorFileExists = 80;
    internal const int ErrorAlreadyExists = 183;

    // NtCreateFile dispositions and options.
    internal const uint NtFileOpen = 1;
    internal const uint NtFileCreate = 2;
    internal const uint NtFileOpenIf = 3;
    internal const uint NtDirectoryFile = 0x1;
    internal const uint NtNonDirectoryFile = 0x40;
    private const uint NtSynchronousIoNonalert = 0x20;
    private const uint NtOpenReparsePoint = 0x200000;
    private const uint ObjCaseInsensitive = 0x40;
    private const nint NtFileCreated = 2; // IO_STATUS_BLOCK.Information when the file was created

    // FILETIME members are pairs of DWORDs (4-byte aligned), so pack at 4: a plain 'long' would be padded to 8.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock, nint allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition,
        uint createOptions, nint eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, int pathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int count, out int written, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte deleteFile, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref long endOfFile, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long newPointer, uint moveMethod);

    private const int FileEndOfFileInfo = 6;
    private const uint FileEnd = 2;

    private const uint FilePersistentAcls = 0x8;
    private const int SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x1;
    private const uint GroupSecurityInformation = 0x2;
    private const uint DaclSecurityInformation = 0x4;
    private const uint LabelSecurityInformation = 0x10;
    private const uint TokenQuery = 0x8;
    private const uint TokenDuplicate = 0x2;
    private const int TokenElevationTypeClass = 18;
    private const int TokenLinkedTokenClass = 19;
    private const int TokenElevationTypeFull = 2;
    private const int SecurityIdentification = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        public uint GenericRead;
        public uint GenericWrite;
        public uint GenericExecute;
        public uint GenericAll;
    }

    private sealed class TokenHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public TokenHandle()
            : base(true)
        {
        }

        public TokenHandle(nint handle)
            : base(true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, nint volumeName, int volumeNameSize,
        out uint serialNumber, out uint maxComponentLength, out uint fileSystemFlags, nint fileSystemName, int fileSystemNameSize);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo, nint owner, nint group,
        nint dacl, nint sacl, out nint securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out TokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(TokenHandle token, int infoClass, out int information, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(TokenHandle token, int infoClass, out nint information, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateToken(TokenHandle existing, int impersonationLevel, out TokenHandle duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateToken(SafeAccessTokenHandle existing, int impersonationLevel, out TokenHandle duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AccessCheck(nint securityDescriptor, TokenHandle clientToken, uint desiredAccess,
        ref GenericMapping genericMapping, byte[] privilegeSet, ref int privilegeSetLength, out uint grantedAccess, out bool accessStatus);
}

/// <summary>The error from <see cref="GuardedFile.CreateExclusive"/>, with the Win32 code.</summary>
public sealed class GuardedFileException : IOException
{
    public GuardedFileException(string message, int win32Error)
        : base($"{message} {new Win32Exception(win32Error).Message}")
    {
        Win32Error = win32Error;
    }

    public int Win32Error { get; }

    /// <summary>The name was already taken (by a file, folder or link).</summary>
    public bool IsAlreadyExists => Win32Error is GuardedFile.ErrorFileExists or GuardedFile.ErrorAlreadyExists;
}

/// <summary>
/// A folder opened by <see cref="GuardedFile.OpenFolder"/>: held open, so it can't be renamed, deleted or replaced
/// while in use, and the place files are created relative to — its path is never walked again. Dispose when done
/// (files created here keep their own hold on it until they are disposed).
/// </summary>
public sealed class GuardedFolder : IDisposable
{
    private readonly SafeFileHandle? _handle;

    internal GuardedFolder(SafeFileHandle? handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    /// <summary>The folder's real path.</summary>
    public string Path { get; }

    internal SafeFileHandle? Handle => _handle;

    /// <summary>
    /// Creates a new, empty file in this folder, for another process (FFmpeg) to write into. Fails if the name is
    /// taken — by a file, folder or link. The returned object keeps the file open, which pins its path: Windows will
    /// not rename or replace the file, or a folder above it, while it is open this way.
    /// </summary>
    /// <exception cref="GuardedFileException">The name is taken (<see cref="GuardedFileException.IsAlreadyExists"/>),
    /// or the file could not be created.</exception>
    public GuardedOutputFile CreateExclusive(string fileName)
    {
        if (!GuardedFile.IsPlainName(fileName))
        {
            throw new ArgumentException($"'{fileName}' is not a plain file name.", nameof(fileName));
        }

        var path = System.IO.Path.Combine(Path, fileName);
        if (_handle is null)
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
            }

            return new GuardedOutputFile(null, null, fileName, path, default);
        }

        // Read-data access makes this handle take part in share checks; sharing read and write (not delete) lets
        // FFmpeg open the file to write and read it back, while nobody can rename or delete it - or, since an
        // open file pins its folders, rename the folders above it.
        var handle = GuardedFile.OpenRelative(_handle, fileName,
            GuardedFile.FileReadData | GuardedFile.FileReadAttributes | GuardedFile.Synchronize,
            GuardedFile.FileShareRead | GuardedFile.FileShareWrite, GuardedFile.NtFileCreate, GuardedFile.NtNonDirectoryFile,
            out var error, out _);
        if (handle is null)
        {
            throw new GuardedFileException($"Could not create {path}.", error);
        }

        // A file just created relative to a checked folder is plain and in place; checked anyway, and where Windows
        // says it is, too. Anything else is deleted again (relative to the folder) and refused.
        string? problem = null;
        var id = default(GuardedFile.FileId);
        try
        {
            if (!GuardedFile.GetFileInformationByHandle(handle, out var info))
            {
                problem = $"{path} could not be checked.";
            }
            else
            {
                id = GuardedFile.FileId.From(info);
                if (info.NumberOfLinks != 1 || (info.FileAttributes & GuardedFile.FileAttributeReparsePoint) != 0)
                {
                    problem = $"{path} is not a plain new file.";
                }
                else if (!string.Equals(GuardedFile.FinalPath(handle), path, StringComparison.OrdinalIgnoreCase))
                {
                    problem = "The export folder was redirected while exporting (it now leads somewhere else). Nothing was written; choose the folder again.";
                }
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            problem = ex.Message;
        }

        if (problem is null)
        {
            return new GuardedOutputFile(handle, _handle, fileName, path, id);
        }

        handle.Dispose();
        GuardedFile.DeleteRelative(_handle, fileName, id);
        throw new IOException(problem);
    }

    public void Dispose() => _handle?.Dispose();
}

/// <summary>
/// A file created by <see cref="GuardedFolder.CreateExclusive"/>, held open so its path stays pinned while
/// another process writes to <see cref="Path"/>. Dispose when the writer is done; on failure call
/// <see cref="DeleteIfUnchanged"/>, which deletes the file only if it is still the very file created here.
/// </summary>
public sealed class GuardedOutputFile : IDisposable
{
    private SafeFileHandle? _handle;
    private SafeFileHandle? _folder; // the folder it was made in: a reference held until Dispose
    private readonly string _name;
    private readonly GuardedFile.FileId _id;

    internal GuardedOutputFile(SafeFileHandle? handle, SafeFileHandle? folder, string name, string path, GuardedFile.FileId id)
    {
        _handle = handle;
        _name = name;
        Path = path;
        _id = id;
        if (folder is not null)
        {
            var added = false;
            folder.DangerousAddRef(ref added);
            _folder = added ? folder : null;
        }
    }

    /// <summary>The real path of the created file — what the writer should open.</summary>
    public string Path { get; }

    /// <summary>The file's current length, read through the pinned handle (so it is this file's length).</summary>
    public long Length
    {
        get
        {
            if (_handle is null)
            {
                return OperatingSystem.IsWindows() ? 0 : new FileInfo(Path).Length;
            }

            return GuardedFile.GetFileInformationByHandle(_handle, out var info)
                ? ((long)info.FileSizeHigh << 32) | info.FileSizeLow
                : 0;
        }
    }

    /// <summary>
    /// Deletes the file if it is still the one created here (never a swapped-in one). The pinned handle doesn't share
    /// delete access — that is what pins the path — so it is released, and the name reopened relative to the folder
    /// the file was made in: no path is walked. Once is enough; later calls do nothing.
    /// </summary>
    public void DeleteIfUnchanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
                // best effort
            }

            return;
        }

        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null || _folder is null)
        {
            return;
        }

        handle.Dispose();
        GuardedFile.DeleteRelative(_folder, _name, _id);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _handle, null)?.Dispose();
        if (Interlocked.Exchange(ref _folder, null) is { } folder)
        {
            folder.DangerousRelease();
        }
    }
}
