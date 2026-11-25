using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using AxisSdReader.Core.Security;

namespace AxisSdReader.Core.Media;

/// <summary>Finds the FFmpeg executable the app may run.</summary>
public static class FfmpegLocator
{
    /// <summary>
    /// An "ffmpeg" folder beside the app, then the app folder itself. Falls back to PATH only when NOT
    /// elevated: a standard user could plant an ffmpeg.exe on a writable PATH directory that would then run
    /// with administrator rights, so an elevated process trusts only the copy shipped with the app.
    /// </summary>
    public static string? Find(string? overridePath = null)
    {
        // An explicit path is for unelevated use (the MCP bridge's --ffmpeg); an elevated process runs only its own copy.
        if (!string.IsNullOrWhiteSpace(overridePath) && !ProcessHardening.IsElevated())
        {
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;
        }

        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"), Path.Combine(baseDir, "ffmpeg.exe") })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (ProcessHardening.IsElevated())
        {
            return null;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore malformed PATH entries
            }
        }

        return null;
    }
}

/// <summary>How to run one FFmpeg process.</summary>
public sealed class FfmpegRunOptions
{
    /// <summary>Bytes written to FFmpeg's stdin (then closed), e.g. a concat list. Null: no stdin.</summary>
    public byte[]? StandardInput { get; init; }

    /// <summary>Capture stdout as binary (e.g. an image written to <c>pipe:1</c>).</summary>
    public bool CaptureStandardOutput { get; init; }

    /// <summary>Cap on captured stdout; FFmpeg is killed if it writes more.</summary>
    public int MaxStandardOutputBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Receives stdout line by line instead (e.g. <c>-progress pipe:1</c>).</summary>
    public Action<string>? OnStandardOutputLine { get; init; }

    /// <summary>Also receives every stderr line (at most 400 characters each), not just the tail the result keeps; all
    /// of them have been seen by the time the run returns.</summary>
    public Action<string>? OnStandardErrorLine { get; init; }

    /// <summary>Kill FFmpeg if it runs longer than this.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Kill FFmpeg if it writes nothing to stdout or stderr for this long (a hung read).</summary>
    public TimeSpan? StallTimeout { get; init; }

    /// <summary>Instead of <see cref="StallTimeout"/>: the silence allowed, asked each time it is checked (so it can
    /// depend on what the run has done so far).</summary>
    public Func<TimeSpan>? StallAllowance { get; init; }

    /// <summary>
    /// When set, only the stdout lines this returns true for count as activity for <see cref="StallTimeout"/>, and
    /// stderr doesn't count at all: FFmpeg's -progress report repeats every half second even when nothing moves (and a
    /// damaged stream can keep stderr busy), so the caller says what real progress is.
    /// </summary>
    public Func<string, bool>? IsActivity { get; init; }

    /// <summary>Run FFmpeg on a restricted copy of this process's token (no administrator rights, no privileges,
    /// medium integrity). Default: whenever this process is elevated.</summary>
    public bool? RunRestricted { get; init; }
}

/// <summary>The outcome of an FFmpeg run.</summary>
public sealed record FfmpegRunResult(int ExitCode, byte[] StandardOutput, string ErrorTail, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Runs FFmpeg safely: arguments passed as a list (no shell quoting), a scrubbed environment (no
/// <c>FFREPORT</c> report-file redirection, no proxy that would route loopback media elsewhere), FFmpeg's own
/// folder as the working directory, the process tied to a kill-on-close job so it can never outlive the app,
/// and bounded runtime/stall timeouts.
/// </summary>
public static class FfmpegRunner
{
    private const int StderrLinesKept = 12;

    public static async Task<FfmpegRunResult> RunAsync(string ffmpeg, IEnumerable<string> arguments, FfmpegRunOptions options,
        CancellationToken cancellationToken = default)
    {
        // Elevated (exports): FFmpeg parses card data, so it never gets the administrator token, only the user's
        // own rights (see ChildProcess). It starts suspended and joins the kill-on-close job before it runs.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("FFmpeg is run on Windows only.");
        }

#pragma warning disable CA1416 // guarded just above; the analyzer doesn't follow the guard into the lambdas below

        var restricted = options.RunRestricted ?? ProcessHardening.IsElevated();
        using var child = ChildProcess.Start(ffmpeg, arguments, BuildEnvironment(),
            Path.GetDirectoryName(ffmpeg) ?? AppContext.BaseDirectory, options.StandardInput is not null, restricted,
            ChildProcessJob.Assign);

        var errorLines = new Queue<string>();
        var lastActivity = Stopwatch.StartNew();
        var timedOut = false;

        var stderrTask = Task.Run(async () =>
        {
            await foreach (var line in ReadLinesAsync(child.StandardError, 400, CancellationToken.None).ConfigureAwait(false))
            {
                options.OnStandardErrorLine?.Invoke(line);
                lock (errorLines)
                {
                    if (options.IsActivity is null)
                    {
                        lastActivity.Restart();
                    }

                    errorLines.Enqueue(line);
                    while (errorLines.Count > StderrLinesKept)
                    {
                        errorLines.Dequeue();
                    }
                }
            }
        });

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Timeout is { } timeout)
        {
            lifetime.CancelAfter(timeout);
        }

        // Stall watchdog: FFmpeg reports progress/errors continuously while working; silence means a hung read.
        using var watchdog = options.StallTimeout is not null || options.StallAllowance is not null
            ? new Timer(_ =>
            {
                var allowed = options.StallAllowance?.Invoke() ?? options.StallTimeout!.Value;
                lock (errorLines)
                {
                    if (lastActivity.Elapsed > allowed)
                    {
                        timedOut = true;
                        child.Kill();
                    }
                }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
            : null;

        var stdinTask = options.StandardInput is { } input && child.StandardInput is { } stdin
            ? WriteStdinAsync(stdin, input)
            : Task.CompletedTask;
        var stdoutTask = ReadStdoutAsync(child.StandardOutput, options, () =>
        {
            lock (errorLines)
            {
                lastActivity.Restart();
            }
        }, lifetime.Token);

        try
        {
            await child.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            child.Kill();
            await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true; // our own timeout, not the caller's cancellation
        }

        byte[] stdout;
        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            child.Kill();
            throw;
        }
        catch
        {
            stdout = [];
        }

        try
        {
            await stdinTask.ConfigureAwait(false);
        }
        catch
        {
            // FFmpeg may exit before reading all of stdin (e.g. on an error); its exit code says what happened
        }

        try
        {
            // The pipe closes with the process, so this ends promptly; don't let a stuck reader hold the result.
            await stderrTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch when (options.OnStandardErrorLine is null)
        {
            // keep whatever was read
        }
        catch (Exception ex)
        {
            // The caller watches every line (for errors FFmpeg survives): a run whose lines weren't all seen isn't one it
            // can vouch for.
            throw new IOException("FFmpeg's messages could not be read to the end.", ex);
        }

        string tail;
        lock (errorLines)
        {
            tail = string.Join('\n', errorLines);
        }

        return new FfmpegRunResult(child.ExitCode, stdout, tail, timedOut);
#pragma warning restore CA1416
    }

    /// <summary>Lines of at most <paramref name="max"/> characters (the rest of a longer line is dropped), so nothing
    /// FFmpeg writes can make this process hold an endless line in memory.</summary>
    private static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream, int max,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    yield return line.ToString().TrimEnd('\r');
                    line.Clear();
                }
                else if (line.Length < max)
                {
                    line.Append(c);
                }
            }
        }

        if (line.Length > 0)
        {
            yield return line.ToString().TrimEnd('\r');
        }
    }

    private static async Task WriteStdinAsync(Stream stdin, byte[] input)
    {
        try
        {
            await stdin.WriteAsync(input).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                stdin.Dispose(); // end of input
            }
            catch
            {
                // already gone
            }
        }
    }

    private static async Task<byte[]> ReadStdoutAsync(Stream stdout, FfmpegRunOptions options, Action onActivity,
        CancellationToken cancellationToken)
    {
        if (options.OnStandardOutputLine is { } onLine)
        {
            await foreach (var line in ReadLinesAsync(stdout, 1024, cancellationToken).ConfigureAwait(false))
            {
                if (options.IsActivity?.Invoke(line) ?? true)
                {
                    onActivity();
                }

                onLine(line);
            }

            return [];
        }

        var buffer = new byte[64 * 1024];
        using var captured = new MemoryStream();
        int read;
        while ((read = await stdout.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            onActivity();
            if (!options.CaptureStandardOutput)
            {
                continue;
            }

            if (captured.Length + read > options.MaxStandardOutputBytes)
            {
                throw new InvalidDataException("FFmpeg produced more output than expected.");
            }

            captured.Write(buffer, 0, read);
        }

        return captured.ToArray();
    }

    /// <summary>
    /// Starts from an empty environment and adds back only what FFmpeg needs. This drops variables that would
    /// let the user's environment redirect an elevated FFmpeg — <c>FFREPORT</c> writes a log to any path it
    /// names — and the proxy variables, which FFmpeg's HTTP client honours even for 127.0.0.1 and which would
    /// send the app's loopback media (and its access token) through someone else's proxy.
    /// </summary>
    private static Dictionary<string, string> BuildEnvironment()
    {
        var elevated = ProcessHardening.IsElevated();
        var values = new List<(string Name, string? Value)>();
        if (elevated)
        {
            // Elevated (exports): nothing taken from the user's environment at all — even SystemRoot and windir
            // can be overridden per user. The Windows folder comes from the kernel; no TEMP/TMP (FFmpeg needs no
            // scratch space to remux, and a user-chosen one is somewhere an elevated process shouldn't write).
            var windows = ProcessHardening.TrustedWindowsDirectory();
            values.Add(("SystemRoot", windows));
            values.Add(("windir", windows));
            values.Add(("SystemDrive", windows is { Length: >= 2 } w && w[1] == ':' ? w[..2] : null));
            values.Add(("NUMBER_OF_PROCESSORS", Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            // Unelevated (the MCP bridge): the user's own values, which can't raise anything above the user.
            // fontconfig, used by the timestamp overlay, wants a profile for its cache.
            foreach (var name in new[]
                     {
                         "SystemRoot", "windir", "SystemDrive", "TEMP", "TMP", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
                         "USERPROFILE", "LOCALAPPDATA", "APPDATA", "HOMEDRIVE", "HOMEPATH",
                     })
            {
                values.Add((name, Environment.GetEnvironmentVariable(name)));
            }
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (value is not null)
            {
                environment[name] = value;
            }
        }

        environment["no_proxy"] = "*";
        return environment;
    }

}

/// <summary>
/// A kill-on-close Windows job that every FFmpeg child is placed in, so a crash or forced exit of the app
/// takes its FFmpeg processes with it instead of leaving them running (and reading the card).
/// </summary>
internal static class ChildProcessJob
{
    private static readonly Lazy<nint> Job = new(CreateJob);

    /// <summary>Puts the (still suspended) process in the job, or throws: its limits are a safeguard, so a process that
    /// can't be given them never runs (the caller then ends it).</summary>
    public static void Assign(SafeProcessHandle process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var job = Job.Value;
        if (job == 0 || !AssignProcessToJobObject(job, process.DangerousGetHandle()))
        {
            throw new IOException("FFmpeg could not be started with its limits.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    private static nint CreateJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            return 0;
        }

        // Each FFmpeg gets at most a sixth of the machine's memory (and never more than 4 GiB: a crafted stream can declare
        // enormous frames; an MP4 export of days of footage keeps a large index), and all of them together at most a
        // third (never more than 8 GiB); whatever happens, they die with this process.
        var memory = (ulong)Math.Max(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, 2L << 30);
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitProcessMemory | JobObjectLimitJobMemory,
            },
            ProcessMemoryLimit = (nuint)Math.Min(Math.Min(memory / 6, 4UL << 30), (ulong)nuint.MaxValue),
            JobMemoryLimit = (nuint)Math.Min(Math.Min(memory / 3, 8UL << 30), (ulong)nuint.MaxValue),
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            CloseHandle(job);
            return 0;
        }

        return job; // intentionally never closed: the job lives exactly as long as the process
    }

    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint JobObjectLimitProcessMemory = 0x100;
    private const uint JobObjectLimitJobMemory = 0x200;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
