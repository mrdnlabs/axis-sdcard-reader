using System.Diagnostics;
using AxisSdReader.Core.Export;
using AxisSdReader.Core.Media;
using AxisSdReader.Core.Security;

namespace AxisSdReader.Core.Tests.Security;

public sealed class GuardedFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "axis-guard-" + Guid.NewGuid().ToString("N"));

    public GuardedFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                         .Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                Directory.Delete(dir); // remove junctions first so cleanup never follows them
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void AppendsCreatingFolders()
    {
        Assert.True(GuardedFile.TryAppendText(_root, ["app", "logs"], "errors.log", "one\n"));
        Assert.True(GuardedFile.TryAppendText(_root, ["app", "logs"], "errors.log", "two\n"));
        Assert.Equal("one\ntwo\n", File.ReadAllText(Path.Combine(_root, "app", "logs", "errors.log")));
    }

    [SkippableFact]
    public void RefusesAJunctionedLogFolder()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(_root, "app"));
        Junction(Path.Combine(_root, "app", "logs"), target);

        Assert.False(GuardedFile.TryAppendText(_root, ["app", "logs"], "errors.log", "x"));
        Assert.False(File.Exists(Path.Combine(target, "errors.log"))); // nothing was written through the link
    }

    [SkippableFact]
    public void RefusesAHardLinkedLogFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var victim = Path.Combine(_root, "victim.txt");
        File.WriteAllText(victim, "precious");
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        Run("cmd.exe", "/c", "mklink", "/H", Path.Combine(_root, "logs", "errors.log"), victim);

        Assert.False(GuardedFile.TryAppendText(_root, ["logs"], "errors.log", "x"));
        Assert.Equal("precious", File.ReadAllText(victim));
    }

    [Fact]
    public void CreateExclusiveNeverOverwrites()
    {
        var folder = GuardedFile.ResolveDirectory(_root);
        using (var first = GuardedFile.CreateExclusive(folder, "clip.mp4"))
        {
            Assert.Equal(Path.Combine(folder, "clip.mp4"), first.Path, ignoreCase: true);
            File.WriteAllText(first.Path, "data"); // another process (FFmpeg) writes while it is pinned
            Assert.Equal(4, first.Length);
        }

        var ex = Assert.Throws<GuardedFileException>(() => GuardedFile.CreateExclusive(folder, "clip.mp4"));
        Assert.True(ex.IsAlreadyExists);
        Assert.Equal("data", File.ReadAllText(Path.Combine(folder, "clip.mp4")));
    }

    [Fact]
    public void DeleteIfUnchangedRemovesOnlyItsOwnFile()
    {
        var folder = GuardedFile.ResolveDirectory(_root);
        var file = GuardedFile.CreateExclusive(folder, "partial.mp4");
        file.DeleteIfUnchanged();
        file.Dispose();
        Assert.False(File.Exists(Path.Combine(folder, "partial.mp4")));
    }

    [SkippableFact]
    public void DetectsAFolderSwappedForAJunction()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var chosen = Path.Combine(_root, "exports");
        Directory.CreateDirectory(chosen);
        var resolved = GuardedFile.ResolveDirectory(chosen);

        // After the user chose it, the folder is replaced by a junction to somewhere else.
        var elsewhere = Path.Combine(_root, "system");
        Directory.CreateDirectory(elsewhere);
        Directory.Delete(chosen);
        Junction(chosen, elsewhere);

        Assert.Throws<IOException>(() => GuardedFile.CreateExclusive(resolved, "clip.mp4"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere)); // and the stray file was cleaned up
    }

    [SkippableFact]
    public void PinnedOutputBlocksRenamesButLetsTheWriterIn()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var exports = Path.Combine(_root, "exports");
        Directory.CreateDirectory(exports);
        var folder = GuardedFile.ResolveDirectory(exports);
        using var pinned = GuardedFile.CreateExclusive(folder, "clip.mp4");

        // FFmpeg's way in: create-or-truncate for writing, sharing read and write; then a second, read-only open
        // to move the index to the front (faststart).
        using (var writer = new FileStream(pinned.Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write([1, 2, 3]);
            writer.Flush();
            using var reader = new FileStream(pinned.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Assert.Equal(3, reader.Length);
        }

        Assert.Equal(3, pinned.Length);

        // But while it is pinned nobody can move the file away, or the folder it is in.
        Assert.ThrowsAny<IOException>(() => File.Move(pinned.Path, Path.Combine(_root, "moved.mp4")));
        var folderMove = Record.Exception(() => Directory.Move(exports, Path.Combine(_root, "moved")));
        Assert.True(folderMove is IOException or UnauthorizedAccessException, $"the folder moved ({folderMove?.GetType().Name ?? "no error"})");
        Assert.True(File.Exists(Path.Combine(exports, "clip.mp4")));
    }

    [SkippableFact]
    public void AppendStartsTheFileOverAtItsCap()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var path = Path.Combine(_root, "logs", "errors.log");
        var entry = new string('a', 900) + "\n";
        for (var i = 0; i < 4; i++)
        {
            Assert.True(GuardedFile.TryAppendText(_root, ["logs"], "errors.log", entry, maxBytes: 4096));
        }

        Assert.Equal(3604, new FileInfo(path).Length);
        Assert.True(GuardedFile.TryAppendText(_root, ["logs"], "errors.log", entry, maxBytes: 4096)); // would pass 4096
        Assert.Equal(entry, File.ReadAllText(path));

        // An entry bigger than the cap allows is cut short rather than refused or allowed to overflow.
        Assert.True(GuardedFile.TryAppendText(_root, ["logs"], "errors.log", new string('b', 10_000), maxBytes: 4096));
        Assert.InRange(new FileInfo(path).Length, 1, 4096);
    }

    [SkippableFact]
    public void TellsFoldersTheUserCanWriteFromProtectedOnes()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Assert.True(GuardedFile.IsUserWritable(GuardedFile.ResolveDirectory(_root)));
        Assert.False(GuardedFile.IsUserWritable(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.False(GuardedFile.IsUserWritable(Environment.GetFolderPath(Environment.SpecialFolder.System)));
        Assert.False(GuardedFile.IsUserWritable(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(GuardedFile.IsUserWritable(Path.Combine(_root, "missing")));

        // A network folder never qualifies: its server decides where an elevated process's files really go (it can
        // send them back to this machine), whatever the name says. Nor does a device path.
        Assert.False(GuardedFile.IsUserWritable(@"\\localhost\C$\Windows"));
        Assert.False(GuardedFile.IsUserWritable(@"\\127.0.0.1\share\exports"));
        Assert.False(GuardedFile.IsUserWritable($@"\\{Environment.MachineName}\share"));
        Assert.False(GuardedFile.IsUserWritable(@"\\192.0.2.10\videos\exports")); // another machine: still refused
        Assert.False(GuardedFile.IsUserWritable(@"\\.\C:\Windows"));
    }

    [SkippableFact]
    public void AHeldFolderCantBeMovedAndItsFilesStayInIt()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var exports = Directory.CreateDirectory(Path.Combine(_root, "held")).FullName;
        using var folder = GuardedFile.OpenFolder(GuardedFile.ResolveDirectory(exports));
        Assert.True(GuardedFile.IsUserWritable(folder));

        var moved = Record.Exception(() => Directory.Move(exports, Path.Combine(_root, "held-moved")));
        Assert.True(moved is IOException or UnauthorizedAccessException, $"the held folder moved ({moved?.GetType().Name ?? "no error"})");

        using (var file = folder.CreateExclusive("clip.mp4"))
        {
            Assert.Equal(Path.Combine(folder.Path, "clip.mp4"), file.Path, ignoreCase: true);
        }

        Assert.True(File.Exists(Path.Combine(exports, "clip.mp4")));
        Assert.True(Assert.Throws<GuardedFileException>(() => folder.CreateExclusive("clip.mp4")).IsAlreadyExists);
    }

    [SkippableFact]
    public void OpenFolderRefusesALink()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var target = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var link = Path.Combine(_root, "link-folder");
        Junction(link, target);
        Assert.ThrowsAny<IOException>(() => GuardedFile.OpenFolder(link));
    }

    [SkippableFact]
    public void AFolderTurnedIntoAJunctionWhereItStandsCantRedirectACreate()
    {
        Skip.IfNot(OperatingSystem.IsWindows());

        // The user's empty export folder is held open; then a program running as the user turns that very folder
        // into a junction to a "protected" one (possible for an empty folder, even while it is held).
        var exports = Directory.CreateDirectory(Path.Combine(_root, "exports-inplace")).FullName;
        var protectedFolder = Directory.CreateDirectory(Path.Combine(_root, "protected")).FullName;
        using var folder = GuardedFile.OpenFolder(GuardedFile.ResolveDirectory(exports));
        MakeJunctionInPlace(exports, protectedFolder);
        Assert.True(new DirectoryInfo(exports).Attributes.HasFlag(FileAttributes.ReparsePoint));
        var untouched = Directory.GetLastWriteTimeUtc(protectedFolder);
        Thread.Sleep(50);

        // Created relative to the held folder, the file can't land in the junction's target - not even for a moment
        // (a file created there and removed again would still change the folder's last-write time).
        try
        {
            using var file = folder.CreateExclusive("clip.mp4");
            Assert.StartsWith(folder.Path, file.Path, StringComparison.OrdinalIgnoreCase);
            file.DeleteIfUnchanged();
        }
        catch (IOException)
        {
            // refused: what current Windows does
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(protectedFolder));
        Assert.Equal(untouched, Directory.GetLastWriteTimeUtc(protectedFolder));
    }

    [SkippableFact]
    public void FlagsInstallFoldersReachedThroughALink()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var real = Directory.CreateDirectory(Path.Combine(_root, "real", "app")).Parent!.FullName;
        var link = Path.Combine(_root, "install-link");
        Junction(link, real);
        var message = ProcessHardening.DescribeUnsafeInstallLocation(Path.Combine(link, "app"));
        Assert.NotNull(message);
        Assert.Contains($"{link} is a link", message);
    }

    [SkippableFact]
    public void ALinkIsNeverJudgedWritableOnItsTargetsBehalf()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var link = Path.Combine(_root, "link");
        Junction(link, _root);
        Assert.False(GuardedFile.IsUserWritable(link)); // the folder itself must be checked, never what it points to
    }

    [Theory]
    [InlineData("clip.mp4", true)]
    [InlineData("2025-01-14_09-30-00_Camera-3456_motion (2).mp4", true)]
    [InlineData("C:evil.mkv", false)]
    [InlineData(@"..\evil.mkv", false)]
    [InlineData("a:b.mkv", false)]
    [InlineData("COM1.mkv", false)]
    [InlineData("nul", false)]
    [InlineData("trailing.", false)]
    [InlineData("tab\there", false)]
    public void PlainNames(string name, bool plain) => Assert.Equal(plain, GuardedFile.IsPlainName(name));

    private static void Junction(string link, string target) => Run("cmd.exe", "/c", "mklink", "/J", link, target);

    /// <summary>Makes an existing, empty folder a junction to <paramref name="target"/> without removing it first.</summary>
    private static void MakeJunctionInPlace(string folder, string target)
    {
        var substitute = @"\??\" + Path.GetFullPath(target);
        var print = Path.GetFullPath(target);
        var pathBytes = System.Text.Encoding.Unicode.GetBytes(substitute + "\0" + print + "\0");
        var buffer = new byte[8 + 8 + pathBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)(8 + pathBytes.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 0); // substitute name offset
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)(substitute.Length * 2));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)(substitute.Length * 2 + 2));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)(print.Length * 2));
        pathBytes.CopyTo(buffer, 16);

        using var handle = CreateFileW(folder, 0x40000000 /* GENERIC_WRITE */, 3 /* share read, write */, 0, 3 /* OPEN_EXISTING */,
            0x02000000 | 0x00200000 /* backup semantics, open reparse point */, 0);
        Assert.False(handle.IsInvalid, $"could not open {folder} to make it a junction");
        Assert.True(DeviceIoControl(handle, 0x000900A4 /* FSCTL_SET_REPARSE_POINT */, buffer, buffer.Length, 0, 0, out _, 0),
            $"FSCTL_SET_REPARSE_POINT failed ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, byte[] input, int inputSize,
        nint output, int outputSize, out int returned, nint overlapped);

    private static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Skip.If(p.ExitCode != 0, $"{exe} failed: {p.StandardError.ReadToEnd()}");
    }
}

public class ProcessHardeningTests
{
    [SkippableFact]
    public void ClearsTheCrtCopyNativeLibrariesRead()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        const string name = "AXIS_TEST_PLUGIN_PATH";
        ProcessHardening.SetCrtEnvironmentVariable(name, @"C:\planted");
        Environment.SetEnvironmentVariable(name, @"C:\planted");
        Assert.Equal(@"C:\planted", ProcessHardening.GetCrtEnvironmentVariable(name));

        Assert.True(ProcessHardening.ClearEnvironmentVariable(name));
        Assert.Null(ProcessHardening.GetCrtEnvironmentVariable(name));
        Assert.Null(Environment.GetEnvironmentVariable(name));
    }

    [SkippableFact]
    public void FlagsUserWritableInstallFolders()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "axis-install-" + Guid.NewGuid().ToString("N")));
        try
        {
            Assert.NotNull(ProcessHardening.DescribeUnsafeInstallLocation(temp.FullName));

            // A small folder only administrators can change, checked through and through (folder, parent, files).
            Assert.Null(ProcessHardening.DescribeUnsafeInstallLocation(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc")));
        }
        finally
        {
            temp.Delete();
        }
    }

    [SkippableFact]
    public void SystemLocationsComeFromWindowsNotTheEnvironment()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var windir = Environment.GetEnvironmentVariable("windir");
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            // What a program running as the user could set in HKCU\Environment.
            Environment.SetEnvironmentVariable("windir", @"C:\planted");
            Environment.SetEnvironmentVariable("SystemRoot", @"C:\planted");
            Environment.SetEnvironmentVariable("LOCALAPPDATA", @"C:\planted");

            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows), ProcessHardening.TrustedWindowsDirectory(),
                ignoreCase: true);
            var local = ProcessHardening.DefaultLocalAppData();
            Assert.NotNull(local);
            Assert.EndsWith(@"\AppData\Local", local, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("planted", local, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("windir", windir);
            Environment.SetEnvironmentVariable("SystemRoot", windir);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData);
        }
    }
}

public class FfmpegArgumentTests
{
    [Fact]
    public void ExportsAreCappedInSizeAndTime()
    {
        var args = FfmpegExporter.BuildArguments(TimeSpan.Zero, TimeSpan.FromSeconds(10), ExportContainer.Mkv, @"C:\out\clip.mkv", 5_000_000);
        var fs = args.ToList().IndexOf("-fs");
        Assert.True(fs > 0 && args[fs + 1] == "5000000", string.Join(' ', args));
        Assert.Equal(@"file:C:\out\clip.mkv", args[^1]); // the limit comes before the output it applies to
        Assert.DoesNotContain("-fs", FfmpegExporter.BuildArguments(TimeSpan.Zero, TimeSpan.FromSeconds(10), ExportContainer.Mkv, "x.mkv"));

        // No per-stream frame caps: FFmpeg would cut every stream where the first capped one ends (the frame budget is
        // enforced from its progress reports instead).
        Assert.DoesNotContain(args, a => a.StartsWith("-frames", StringComparison.Ordinal));

        // No more than the footage could hold (here its 10 MB of chunks, plus a little), and a run time in proportion.
        var folder = Path.GetTempPath();
        var limits = ExportLimits.For(TimeSpan.FromMinutes(2), 10_000_000, folder);
        Assert.NotNull(limits);
        Assert.InRange(limits!.MaxBytes, 10_000_000, 10_000_000 + 10_000_000 / 8 + (64L << 20));
        Assert.Equal(TimeSpan.FromMinutes(3) + TimeSpan.FromMinutes(8), limits.Timeout);

        // Unknown chunk sizes (an agent's clip): bounded by a bitrate far above any camera's.
        var clip = ExportLimits.For(TimeSpan.FromMinutes(10), null, folder)!;
        Assert.InRange(clip.MaxBytes, 1, 600 * 12_500_000L + (64L << 20));
        Assert.True(ExportLimits.FreeBytes(folder) > clip.MaxBytes); // and never the last of the drive
    }

    [SkippableFact]
    public async Task RunsFfmpegOnARestrictedTokenWithOnlyItsOwnHandles()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var ffmpeg = FfmpegLocator.Find();
        Skip.If(ffmpeg is null, "ffmpeg not found");

        // The way an elevated app runs it for exports: a restricted copy of the token, stdin fed, stdout captured.
        var result = await FfmpegRunner.RunAsync(ffmpeg!,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=64x48:d=1", "-frames:v", "1",
             "-f", "image2pipe", "-c:v", "png", "pipe:1"],
            new FfmpegRunOptions { CaptureStandardOutput = true, RunRestricted = true, Timeout = TimeSpan.FromSeconds(60) });
        Assert.True(result.Succeeded, result.ErrorTail);
        Assert.Equal(0x89, result.StandardOutput[0]); // a PNG

        // A failing run reports its exit code and stderr through the same pipes.
        var failed = await FfmpegRunner.RunAsync(ffmpeg!, ["-hide_banner", "-f", "lavfi", "-i", "nosuchfilter", "-f", "null", "-"],
            new FfmpegRunOptions { RunRestricted = true, Timeout = TimeSpan.FromSeconds(60) });
        Assert.NotEqual(0, failed.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(failed.ErrorTail));
    }

    [SkippableTheory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("", "\"\"")]
    [InlineData("quote\"inside", "\"quote\\\"inside\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData(@"file:C:\out\clip.mp4", @"file:C:\out\clip.mp4")]
    public void QuotesArgumentsTheWayTheCRuntimeReadsThem(string argument, string expected)
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var line = new System.Text.StringBuilder();
        if (OperatingSystem.IsWindows())
        {
            ChildProcess.AppendArgument(line, argument);
        }

        Assert.Equal(expected, line.ToString());
    }

    [Fact]
    public void ExportArgumentsLockDownInputsAndUseInvariantNumbers()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var args = FfmpegExporter.BuildArguments(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.25),
                ExportContainer.Mp4, @"C:\out\clip.mp4");

            Assert.Equal("pipe,http,tcp", args[args.ToList().IndexOf("-protocol_whitelist") + 1]);
            Assert.Equal("concat,matroska", args[args.ToList().IndexOf("-format_whitelist") + 1]);
            Assert.True(args.ToList().IndexOf("-protocol_whitelist") < args.ToList().IndexOf("-i"));
            Assert.Equal("1.5", args[args.ToList().IndexOf("-ss") + 1]);
            Assert.Equal("2.25", args[args.ToList().IndexOf("-t") + 1]);
            Assert.Equal(@"file:C:\out\clip.mp4", args[^1]);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    private static readonly ExportLimits Limits = new(1 << 20, TimeSpan.FromMinutes(1));

    [Fact]
    public async Task ExportRefusesNonLoopbackSources()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => FfmpegExporter.ExportAsync(
            [new Uri("http://example.com/x.mkv")], TimeSpan.Zero, TimeSpan.FromSeconds(1), ExportContainer.Mkv, "out.mkv", Limits));
        await Assert.ThrowsAsync<ArgumentException>(() => FfmpegExporter.ExportAsync(
            [new Uri("file:///C:/Windows/win.ini")], TimeSpan.Zero, TimeSpan.FromSeconds(1), ExportContainer.Mkv, "out.mkv", Limits));

        // Loopback, but not one of our lease URLs: another local service, or a quote that would break out of the
        // concat list.
        await Assert.ThrowsAsync<ArgumentException>(() => FfmpegExporter.ExportAsync(
            [new Uri("http://127.0.0.1:8080/admin")], TimeSpan.Zero, TimeSpan.FromSeconds(1), ExportContainer.Mkv, "out.mkv", Limits));
        await Assert.ThrowsAsync<ArgumentException>(() => FfmpegExporter.ExportAsync(
            [new Uri($"http://127.0.0.1:8080/{new string('a', 32)}/c0.mkv'%0afile 'C:/x")], TimeSpan.Zero, TimeSpan.FromSeconds(1),
            ExportContainer.Mkv, "out.mkv", Limits));
    }
}
