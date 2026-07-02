using System.IO;
using AxisSdReader.Core;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Ext4;
using DiscUtils;

namespace AxisSdReader.App.Services;

/// <summary>
/// An opened card (physical device with volume protection, or an image file) plus its
/// indexed Axis content. All card I/O in the app funnels through <see cref="RunExclusive{T}"/>
/// because DiscUtils streams share one underlying device stream and are not thread-safe.
/// </summary>
public sealed class OpenCard : IDisposable
{
    private readonly SdCardSession? _session;   // physical device (owns reader + guard)
    private readonly CardReader _reader;        // image files: owned directly
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private OpenCard(SdCardSession? session, CardReader reader, string description, bool isPhysicalDevice)
    {
        _session = session;
        _reader = reader;
        Description = description;
        IsPhysicalDevice = isPhysicalDevice;
    }

    public string Description { get; }

    public bool IsPhysicalDevice { get; }

    public CardOpenStatus Status => _reader.Status;

    public string? FailureDetail => _reader.FailureDetail;

    public DiscFileSystem? FileSystem => _reader.FileSystem;

    public IReadOnlyList<string> ProtectionLog => _session?.ProtectionLog ?? [];

    public AxisCard? Index { get; private set; }

    public static OpenCard FromDevice(int diskNumber, string description)
    {
        var session = SdCardSession.Open(diskNumber);
        return new OpenCard(session, session.Card, description, isPhysicalDevice: true);
    }

    public static OpenCard FromImage(string imagePath)
    {
        var reader = CardReader.OpenImage(imagePath);
        return new OpenCard(null, reader, Path.GetFileName(imagePath), isPhysicalDevice: false);
    }

    /// <summary>Runs a card I/O operation with exclusive access, off the calling thread.</summary>
    public async Task<T> RunExclusive<T>(Func<T> operation)
    {
        await _ioLock.WaitAsync();
        try
        {
            return await Task.Run(operation);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<AxisCard> IndexAsync()
    {
        var fs = FileSystem ?? throw new InvalidOperationException("No filesystem is open.");
        Index = await RunExclusive(() => AxisCardIndexer.Index(fs));
        return Index;
    }

    public Task LoadMetadataAsync(Recording recording)
    {
        var fs = FileSystem ?? throw new InvalidOperationException("No filesystem is open.");
        return RunExclusive<object?>(() =>
        {
            recording.LoadChunkMetadata(fs);
            return null;
        });
    }

    /// <summary>Opens a chunk for playback. The caller owns the stream; while it is in use
    /// (VLC reads from its own thread), no other card I/O may run — the UI enforces this
    /// by loading all metadata before playback starts.</summary>
    public Stream OpenChunk(RecordingChunk chunk)
    {
        var fs = FileSystem ?? throw new InvalidOperationException("No filesystem is open.");
        return fs.OpenFile(chunk.Path, FileMode.Open, FileAccess.Read);
    }

    public void Dispose()
    {
        if (_session is not null)
        {
            _session.Dispose(); // disposes reader + releases volume locks
        }
        else
        {
            _reader.Dispose();
        }

        _ioLock.Dispose();
    }
}
