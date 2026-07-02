using System.Diagnostics;

namespace AxisSdReader.Core.Tests.Fixtures;

/// <summary>
/// Generates (once per test run) an Axis-like ext4 card image by invoking
/// tools/make-fixture.sh under WSL. The image is cached in tests/fixtures/ and
/// only regenerated when missing, so normal runs are fast.
/// </summary>
public sealed class CardImageFixture
{
    public CardImageFixture()
    {
        var repoRoot = FindRepoRoot();
        // Bump the version suffix whenever make-fixture.sh changes, to invalidate cached images.
        ImagePath = Path.Combine(repoRoot, "tests", "fixtures", "axis-card-v2.img");

        if (!File.Exists(ImagePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ImagePath)!);
            var script = ToWslPath(Path.Combine(repoRoot, "tools", "make-fixture.sh"));
            var output = ToWslPath(ImagePath);
            RunWsl($"bash '{script}' '{output}'");
        }
    }

    public string ImagePath { get; }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxisSdReader.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root (AxisSdReader.sln).");
    }

    private static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        var drive = char.ToLowerInvariant(full[0]);
        return $"/mnt/{drive}{full[2..].Replace('\\', '/')}";
    }

    private static void RunWsl(string command)
    {
        var psi = new ProcessStartInfo("wsl.exe", $"-e bash -c \"{command}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start wsl.exe. WSL is required to generate the ext4 test fixture.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Fixture generation failed (exit {process.ExitCode}).\nstdout: {stdout}\nstderr: {stderr}");
        }
    }
}
