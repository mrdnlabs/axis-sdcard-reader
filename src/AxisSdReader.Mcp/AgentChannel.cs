using System.IO.Pipes;
using System.Security.Principal;
using AxisSdReader.Core.Agent;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Media;
using AxisSdReader.Core.Timeline;

namespace AxisSdReader.Mcp;

/// <summary>Where agent requests go: the running app (named pipe) or a card image opened in-process.</summary>
public interface IAgentChannel : IDisposable
{
    /// <summary>"app" or "image".</summary>
    string Mode { get; }

    Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken cancellationToken);
}

/// <summary>Talks to the running app over its named pipe — one connection per request.</summary>
public sealed class PipeChannel(bool allowUnelevatedApp) : IAgentChannel
{
    public const string NotReachable =
        "The Axis SD Card Reader app isn't reachable. Start the app (AxisSdReader.exe; it asks for administrator " +
        "rights), open a card, and switch on \"AI agents\" in its status bar (bottom right). Agent access is off by " +
        "default and turns off again whenever the card or the app closes.";

    private readonly SemaphoreSlim _serial = new(1, 1);

    public string Mode => "app";

    public async Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!PipeExists())
            {
                return AgentResponse.Failure(NotReachable);
            }

            // Identification only: whoever holds the pipe name may learn who we are, but can never act as us.
            // (An agent host that runs elevated would otherwise hand an impostor server an elevated token.)
            using var client = new NamedPipeClientStream(".", AgentPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            try
            {
                await client.ConnectAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                return AgentResponse.Failure(NotReachable);
            }

            // The real app runs elevated, so its pipe is owned by Administrators. An unelevated program that
            // grabbed the name first can't fake that — so don't feed an agent whatever an impostor says. A
            // development build may run unelevated (--allow-unelevated-app); its pipe must then at least be owned by
            // this user, which no other account (the pipe namespace is machine-wide) can arrange.
            if (OperatingSystem.IsWindows() && !AgentPipe.IsOwnedByAdministrators(client) &&
                !(allowUnelevatedApp && AgentPipe.IsOwnedByCurrentUser(client)))
            {
                return AgentResponse.Failure(
                    "The connection named for the Axis SD Card Reader app wasn't created by the app running with " +
                    "administrator rights, so it was not trusted. Close other programs that might impersonate it, start " +
                    "the app with AxisSdReader.exe (it asks for administrator rights), and switch on \"AI agents\" again.");
            }

            await AgentPipe.WriteMessageAsync(client, AgentPipe.Serialize(request), cancellationToken).ConfigureAwait(false);
            var reply = await AgentPipe.ReadMessageAsync(client, AgentPipe.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            return reply is null
                ? AgentResponse.Failure("The app closed the connection without answering.")
                : AgentPipe.Deserialize<AgentResponse>(reply) ?? AgentResponse.Failure("The app sent an empty reply.");
        }
        catch (IOException ex)
        {
            return AgentResponse.Failure($"Lost the connection to the app: {AgentService.Clean(ex.Message, 200)}");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            return AgentResponse.Failure("The app sent a reply that could not be read.");
        }
        finally
        {
            _serial.Release();
        }
    }

    private static bool PipeExists()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var name = AgentPipe.Name;
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => p.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return true; // can't tell — let the connect attempt decide
        }
    }

    public void Dispose() => _serial.Dispose();
}

/// <summary>
/// Serves an unencrypted card image (.img/.dd) directly, without the app: opened read-only and indexed on the
/// first request. Encrypted images are refused (a passphrase must never pass through an agent): open those in
/// the app and use app mode.
/// </summary>
public sealed class ImageChannel(string imagePath) : IAgentChannel
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task<AgentService?>? _opening;
    private CardReader? _reader;
    private LocalMediaServer? _media;
    private AgentService? _service;
    private string? _failure;
    private int _disposed;

    public string Mode => "image";

    public async Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        if (request.Op is "view" or "show")
        {
            return AgentResponse.Failure("That needs the app: this server is reading a card image directly (--image).");
        }

        var service = await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        if (service is null)
        {
            return request.Op == "status"
                ? AgentResponse.Success(new AgentStatus(Mode, false, null, null, _failure))
                : AgentResponse.Failure(_failure ?? "The image could not be opened.");
        }

        return await service.HandleAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The image opened and indexed - once, by one task that only the server's closing stops: a request that gives up
    /// waiting (a client's own timeout) leaves the walk running for the next one, rather than starting it over each time.
    /// </summary>
    private Task<AgentService?> EnsureOpenAsync(CancellationToken cancellationToken)
    {
        Task<AgentService?> opening;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var lifetime = _lifetime.Token;
            opening = _opening ??= Task.Run(() => OpenAsync(lifetime));
        }

        return opening.WaitAsync(cancellationToken);
    }

    private async Task<AgentService?> OpenAsync(CancellationToken lifetime)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                _failure = $"The card image '{imagePath}' doesn't exist.";
                return null;
            }

            var reader = CardReader.OpenImage(imagePath);
            _reader = reader; // owned from here on (disposed with the channel, whatever happens next)
            if (reader.Status != CardOpenStatus.Ok || reader.FileSystem is not { } fs)
            {
                _failure = reader.Status is CardOpenStatus.EncryptedNeedsPassphrase or CardOpenStatus.Encrypted
                    ? "This card image is encrypted. Open it in the Axis SD Card Reader app (which asks for the passphrase), " +
                      "switch on \"AI agents\" there, and run this server without --image."
                    : $"The card image could not be read: {AgentService.Clean(reader.FailureDetail, 200) ?? reader.Status.ToString()}";
                reader.Dispose();
                _reader = null;
                return null;
            }

            var gate = new CardIoGate();
            var index = await gate.RunAsync(() => AxisCardIndexer.Index(reader, cancellationToken: lifetime), lifetime)
                .ConfigureAwait(false);
            var media = LocalMediaServer.Start(maxConnections: 6);
            _media = media; // owned from here on, like the reader
            var name = AgentService.Clean(Path.GetFileName(imagePath)) ?? "card image";
            var info = Describe(name, index);

            _service = new AgentService(new CardCatalog(index), () => info,
                (recordings, ct) => Task.Run(() =>
                {
                    foreach (var recording in recordings)
                    {
                        recording.LoadChunkMetadata(fs, gate, ct);
                    }
                }, ct),
                chunk => new SynchronizedCardStream(gate, () => Recording.OpenChunk(fs, chunk), chunk.SizeBytes),
                media, Mode);
            return _service;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Library messages can quote names from the image: short, single-line, like all card text.
            _failure = $"The card image could not be opened: {AgentService.Clean(ex.Message, 200) ?? ex.GetType().Name}";
            return null;
        }
    }

    private static AgentCardInfo Describe(string name, AxisCard index)
    {
        DateTimeOffset? first = null, last = null;
        if (index.Recordings.Count > 0)
        {
            first = AgentService.At(TimeAxis.ToSeconds(index.Recordings.Min(r => r.StartTime)));
            last = AgentService.At(TimeAxis.ToSeconds(index.Recordings.Max(r => r.StartTime)));
        }

        return new AgentCardInfo(name, "card image", false, true, "Read-only image file; it is never modified.",
            index.Recordings.Count, index.Cameras.Count, first, last, index.UnreadableEntries);
    }

    public void Dispose()
    {
        Task? opening;
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            opening = _opening;
        }

        // Stop the walk (it checks between folders), and let it end before what it reads from is closed.
        _lifetime.Cancel();
        try
        {
            opening?.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // cancelled, or failed: either way it is over
        }

        _service?.Dispose();
        _media?.Dispose();
        _reader?.Dispose();
        _lifetime.Dispose();
    }
}
