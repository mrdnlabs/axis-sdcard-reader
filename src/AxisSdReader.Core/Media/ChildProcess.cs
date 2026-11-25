using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AxisSdReader.Core.Media;

/// <summary>
/// Starts a child process (FFmpeg) with exactly the handles, environment and rights it needs. Used instead of
/// <see cref="System.Diagnostics.Process"/> because the elevated app must not hand its administrator token to a
/// program that parses card data: with <c>restricted</c>, the child runs on a restricted copy of this process's
/// token — Administrators deny-only, every privilege removed, medium integrity — so it has the user's own rights
/// and nothing more (a bug in a demuxer, or an output path swapped for a link to a system file, gains nothing).
/// It inherits only its three standard handles, starts suspended so it is in the kill-on-close job before it
/// runs, and gets no console window.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ChildProcess : IDisposable
{
    private readonly SafeProcessHandle _process;

    private ChildProcess(SafeProcessHandle process, int id, Stream? stdin, Stream stdout, Stream stderr)
    {
        _process = process;
        Id = id;
        StandardInput = stdin;
        StandardOutput = stdout;
        StandardError = stderr;
    }

    public int Id { get; }

    public Stream? StandardInput { get; }

    public Stream StandardOutput { get; }

    public Stream StandardError { get; }

    public SafeProcessHandle Handle => _process;

    public static ChildProcess Start(string exe, IEnumerable<string> arguments, IReadOnlyDictionary<string, string> environment,
        string workingDirectory, bool redirectStandardInput, bool restricted, Action<SafeProcessHandle>? beforeResume = null)
    {
        var commandLine = new StringBuilder();
        AppendArgument(commandLine, exe);
        foreach (var argument in arguments)
        {
            commandLine.Append(' ');
            AppendArgument(commandLine, argument);
        }

        var pipes = new List<SafeFileHandle>();
        nint attributeList = 0;
        SafeAccessTokenHandle? token = null;
        try
        {
            // Child ends are inheritable (and listed explicitly below, so they are all it inherits); ours aren't.
            var (stdinRead, stdinWrite) = redirectStandardInput ? CreatePipePair(childReads: true, pipes) : (null, null);
            var (stdoutRead, stdoutWrite) = CreatePipePair(childReads: false, pipes);
            var (stderrRead, stderrWrite) = CreatePipePair(childReads: false, pipes);
            var nul = redirectStandardInput ? null : OpenNulForChild(pipes);
            var childStdin = stdinRead ?? nul!;

            var inherit = new[] { childStdin.DangerousGetHandle(), stdoutWrite.DangerousGetHandle(), stderrWrite.DangerousGetHandle() };
            var inheritList = GCHandle.Alloc(inherit, GCHandleType.Pinned);
            try
            {
                nint size = 0;
                InitializeProcThreadAttributeList(0, 1, 0, ref size);
                attributeList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
                }

                if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, inheritList.AddrOfPinnedObject(),
                        nint.Size * inherit.Length, 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");
                }

                var startup = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        cb = Marshal.SizeOf<StartupInfoEx>(),
                        dwFlags = StartfUseStdHandles,
                        hStdInput = childStdin.DangerousGetHandle(),
                        hStdOutput = stdoutWrite.DangerousGetHandle(),
                        hStdError = stderrWrite.DangerousGetHandle(),
                    },
                    lpAttributeList = attributeList,
                };

                var flags = CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow | CreateSuspended;
                var block = EnvironmentBlock(environment);
                ProcessInformation info;
                bool started;
                if (restricted)
                {
                    token = CreateRestrictedUserToken();
                    started = CreateProcessAsUserW(token, exe, commandLine, 0, 0, true, flags, block, workingDirectory, ref startup, out info);
                }
                else
                {
                    started = CreateProcessW(exe, commandLine, 0, 0, true, flags, block, workingDirectory, ref startup, out info);
                }

                if (!started)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start {Path.GetFileName(exe)}");
                }

                var process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
                try
                {
                    beforeResume?.Invoke(process);
                    if (ResumeThread(info.hThread) == unchecked((uint)-1))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread");
                    }
                }
                catch
                {
                    TerminateProcess(process, 1); // never leave it suspended (and holding its pipes) behind
                    process.Dispose();
                    throw;
                }
                finally
                {
                    CloseHandle(info.hThread);
                }

                // The child has its copies now; close ours so a pipe ends when the child exits.
                childStdin.Dispose();
                stdoutWrite.Dispose();
                stderrWrite.Dispose();
                return new ChildProcess(process, info.dwProcessId,
                    stdinWrite is null ? null : new FileStream(stdinWrite, FileAccess.Write, 4096, isAsync: false),
                    new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false),
                    new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false));
            }
            finally
            {
                inheritList.Free();
            }
        }
        catch
        {
            foreach (var pipe in pipes)
            {
                pipe.Dispose();
            }

            throw;
        }
        finally
        {
            if (attributeList != 0)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            token?.Dispose();
        }
    }

    /// <summary>Completes when the process has exited.</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new ProcessWaitHandle(_process);
        var registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        return Wait();

        async Task Wait()
        {
            try
            {
                await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                registration.Unregister(null);
                handle.Dispose();
            }
        }
    }

    public int ExitCode => GetExitCodeProcess(_process, out var code) ? (int)code : -1;

    public void Kill()
    {
        try
        {
            TerminateProcess(_process, 1);
        }
        catch
        {
            // already gone
        }
    }

    public void Dispose()
    {
        StandardInput?.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _process.Dispose();
    }

    /// <summary>Waits on the process handle (borrowed: the process object keeps owning it).</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    /// <summary>
    /// A restricted copy of this process's token: what the user could do without elevation. Every group UAC itself
    /// filters out of the user's unelevated token (Administrators, and also e.g. Domain Admins, Backup Operators,
    /// "local account and member of Administrators") is made deny-only, every privilege is removed, and the
    /// integrity level is lowered to medium. (Also what <see cref="Security.GuardedFile.IsUserWritable(Security.GuardedFolder)"/>
    /// checks folders against when there is no unelevated token to ask.)
    /// </summary>
    internal static SafeAccessTokenHandle CreateRestrictedUserToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery | TokenAssignPrimary | TokenAdjustDefault, out var own))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");
        }

        using (own)
        {
            var sids = new List<byte[]> { SidBytes(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) };
            sids.AddRange(UacFilteredGroups(own));

            var pins = sids.Select(b => GCHandle.Alloc(b, GCHandleType.Pinned)).ToList();
            try
            {
                var disable = pins.Select(pin => new SidAndAttributes { Sid = pin.AddrOfPinnedObject(), Attributes = 0 }).ToArray();
                if (!CreateRestrictedToken(own, DisableMaxPrivilege, disable.Length, disable, 0, 0, 0, 0, out var restricted))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRestrictedToken");
                }

                try
                {
                    SetIntegrity(restricted, MediumIntegritySid);
                    return restricted;
                }
                catch
                {
                    restricted.Dispose();
                    throw;
                }
            }
            finally
            {
                pins.ForEach(pin => pin.Free());
            }
        }
    }

    private static byte[] SidBytes(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    /// <summary>The groups that are deny-only in the linked (unelevated) token of an elevated split token: exactly
    /// what UAC keeps away from the user's normal programs. Empty when there is no linked token.</summary>
    private static List<byte[]> UacFilteredGroups(SafeAccessTokenHandle own)
    {
        var result = new List<byte[]>();
        if (!GetTokenInformation(own, TokenElevationTypeClass, out int elevation, sizeof(int), out _) || elevation != TokenElevationTypeFull ||
            !GetTokenInformation(own, TokenLinkedTokenClass, out nint linkedHandle, nint.Size, out _))
        {
            return result;
        }

        using var linked = new SafeAccessTokenHandle(linkedHandle);
        GetTokenInformation(linked, TokenGroupsClass, 0, 0, out var needed);
        if (needed <= 0 || needed > 1 << 20)
        {
            return result;
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(linked, TokenGroupsClass, buffer, needed, out _))
            {
                return result;
            }

            var count = Marshal.ReadInt32(buffer);
            var entrySize = Marshal.SizeOf<SidAndAttributes>();
            var first = buffer + nint.Size; // TOKEN_GROUPS: DWORD GroupCount, then (aligned) SID_AND_ATTRIBUTES[]
            for (var i = 0; i < count && i < 4096; i++)
            {
                var entry = Marshal.PtrToStructure<SidAndAttributes>(first + i * entrySize);
                if ((entry.Attributes & SeGroupUseForDenyOnly) != 0 && entry.Sid != 0)
                {
                    result.Add(SidBytes(new SecurityIdentifier(entry.Sid)));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private static void SetIntegrity(SafeAccessTokenHandle token, string sddlSid)
    {
        var sid = new SecurityIdentifier(sddlSid);
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        var pinned = GCHandle.Alloc(sidBytes, GCHandleType.Pinned);
        try
        {
            var label = new TokenMandatoryLabel { Label = new SidAndAttributes { Sid = pinned.AddrOfPinnedObject(), Attributes = SeGroupIntegrity } };
            var size = Marshal.SizeOf<TokenMandatoryLabel>() + sidBytes.Length;
            if (!SetTokenInformation(token, TokenIntegrityLevelClass, ref label, size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation");
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) CreatePipePair(bool childReads, List<SafeFileHandle> owned)
    {
        var inheritable = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
        if (!CreatePipe(out var read, out var write, ref inheritable, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe");
        }

        owned.Add(read);
        owned.Add(write);
        var ours = childReads ? write : read;
        if (!SetHandleInformation(ours, HandleFlagInherit, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation");
        }

        return (read, write);
    }

    private static SafeFileHandle OpenNulForChild(List<SafeFileHandle> owned)
    {
        var inheritable = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
        var nul = CreateFileW("NUL", GenericRead, FileShareRead | FileShareWrite, ref inheritable, OpenExisting, 0, 0);
        if (nul.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile(NUL)");
        }

        owned.Add(nul);
        return nul;
    }

    /// <summary>name=value\0...\0\0, sorted by name (case-insensitive ordinal), as Windows expects.</summary>
    private static char[] EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0 || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
            {
                continue;
            }

            block.Append(name).Append('=').Append(value).Append('\0');
        }

        block.Append('\0');
        if (block.Length == 1)
        {
            block.Append('\0');
        }

        return block.ToString().ToCharArray();
    }

    /// <summary>Quotes one argument so the C runtime's parser (which FFmpeg uses) reads it back unchanged.</summary>
    internal static void AppendArgument(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        for (var i = 0; ; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                i++;
                backslashes++;
            }

            if (i == argument.Length)
            {
                line.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                line.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                line.Append('\\', backslashes).Append(argument[i]);
            }
        }

        line.Append('"');
    }

    private const uint CreateSuspended = 0x4;
    private const uint CreateUnicodeEnvironment = 0x400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StartfUseStdHandles = 0x100;
    private const uint HandleFlagInherit = 0x1;
    private const nint ProcThreadAttributeHandleList = 0x20002;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint TokenDuplicate = 0x2;
    private const uint TokenAssignPrimary = 0x1;
    private const uint TokenQuery = 0x8;
    private const uint TokenAdjustDefault = 0x80;
    private const uint DisableMaxPrivilege = 0x1;
    private const int TokenIntegrityLevelClass = 25;
    private const int TokenGroupsClass = 2;
    private const int TokenElevationTypeClass = 18;
    private const int TokenLinkedTokenClass = 19;
    private const int TokenElevationTypeFull = 2;
    private const uint SeGroupUseForDenyOnly = 0x10;
    private const uint SeGroupIntegrity = 0x20;
    private const string MediumIntegritySid = "S-1-16-8192";

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SecurityAttributes attributes,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        bool inheritHandles, uint flags, char[] environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string application, StringBuilder commandLine,
        nint processAttributes, nint threadAttributes, bool inheritHandles, uint flags, char[] environment, string currentDirectory,
        ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags, int disableSidCount,
        [In] SidAndAttributes[] sidsToDisable, int deletePrivilegeCount, nint privilegesToDelete, int restrictedSidCount,
        nint sidsToRestrict, out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int information, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out nint information, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, nint information, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int infoClass, ref TokenMandatoryLabel label, int length);
}
