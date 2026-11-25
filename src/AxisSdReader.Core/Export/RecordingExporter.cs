using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Ext4;

namespace AxisSdReader.Core.Export;

/// <summary>Progress snapshot for an export operation.</summary>
public sealed record ExportProgress(string CurrentFile, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

/// <summary>Result of exporting one recording.</summary>
public sealed record ExportResult(string TargetDirectory, int FilesExported, long BytesExported);

/// <summary>
/// Copies recordings off the card to local folders, by path. Not for the elevated app: it opens and deletes files by
/// name in folders the user controls, which a link could redirect (exports there go through
/// <see cref="Security.GuardedFolder"/>). Used by tests and tools over trusted folders only.
/// </summary>
internal static class RecordingExporter
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Exports all chunks of a recording into <c>targetDirectory\&lt;recordingId&gt;\</c>.
    /// Each copied file's length is verified against the source. Throws
    /// <see cref="OperationCanceledException"/> on cancellation (partially written files
    /// are removed).
    /// </summary>
    public static ExportResult Export(
        Ext4Volume fileSystem,
        Recording recording,
        string targetDirectory,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.Combine(targetDirectory, recording.Id.Raw);
        Directory.CreateDirectory(destination);

        var bytesTotal = recording.TotalSizeBytes;
        long bytesDone = 0;
        var filesDone = 0;

        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var chunk in recording.Chunks)
        {
            // Chunk names come from the card. A crafted name ("C:x.mkv", "..\x.mkv", "a:b.mkv", "COM1.mkv") must
            // not resolve outside the destination folder, into an alternate data stream, or onto a device.
            var targetPath = Path.GetFullPath(Path.Combine(destination, chunk.FileName));
            if (!Security.GuardedFile.IsPlainName(chunk.FileName) ||
                !targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Refusing to export a chunk with an unsafe file name ('{chunk.FileName}').");
            }

            try
            {
                CopyChunk(fileSystem, chunk, targetPath, ref bytesDone, bytesTotal, filesDone,
                    recording.Chunks.Count, progress, cancellationToken);
            }
            catch
            {
                TryDelete(targetPath); // don't leave truncated exports behind
                throw;
            }

            filesDone++;
        }

        progress?.Report(new ExportProgress("", filesDone, recording.Chunks.Count, bytesDone, bytesTotal));
        return new ExportResult(destination, filesDone, bytesDone);
    }

    private static void CopyChunk(
        Ext4Volume fileSystem,
        RecordingChunk chunk,
        string targetPath,
        ref long bytesDone,
        long bytesTotal,
        int filesDone,
        int filesTotal,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var source = Recording.OpenChunk(fileSystem, chunk);
        using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);

        var buffer = new byte[BufferSize];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.Write(buffer, 0, read);
            bytesDone += read;
            progress?.Report(new ExportProgress(chunk.FileName, filesDone, filesTotal, bytesDone, bytesTotal));
        }

        target.Flush();
        if (target.Length != chunk.SizeBytes)
        {
            throw new IOException(
                $"Export verification failed for {chunk.FileName}: wrote {target.Length:N0} bytes, expected {chunk.SizeBytes:N0}.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
