using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using AxisSdReader.Core.Agent;

namespace AxisSdReader.App.Services;

/// <summary>
/// Serves agent requests from the MCP bridge over the app's named pipe (see <see cref="AgentPipe"/>): one
/// request and one reply per connection, handled one at a time. Everything a client sends is treated as
/// untrusted — the bridge runs unelevated, so anything running as the user can talk to this pipe while
/// agent access is switched on — so requests are size- and time-limited and answered only with metadata
/// and short-lived loopback media URLs.
/// </summary>
public sealed class AgentHost : IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandleTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The operations an agent may request; anything else is refused before it reaches the app.</summary>
    public static readonly IReadOnlySet<string> KnownOps =
        new HashSet<string>(StringComparer.Ordinal) { "status", "cameras", "recordings", "coverage", "locate", "slices", "view", "show" };

    // Token bucket, charged per connection before anything is read: an agent makes a handful of requests per
    // action, so a flood (empty connections and garbage included) is slowed to the refill rate instead of making
    // the elevated app read, parse and work on demand.
    private const double BucketCapacity = 40;
    private const double RefillPerSecond = 20;
    private double _tokens = BucketCapacity;
    private long _lastRefill = Environment.TickCount64;

    // A connection that sent nothing usable costs a pause on top, and errors reach the crash log at most once a
    // minute, so a client can neither spin the loop nor churn the log.
    private static readonly TimeSpan UnusablePause = TimeSpan.FromMilliseconds(100);
    private long _lastLogged = long.MinValue / 2; // so "now - this" can't overflow

    private readonly NamedPipeServerStream _server;
    private readonly Func<AgentRequest, CancellationToken, Task<AgentResponse>> _handler;
    private readonly CancellationTokenSource _stop = new();

    private AgentHost(NamedPipeServerStream server, Func<AgentRequest, CancellationToken, Task<AgentResponse>> handler)
    {
        _server = server;
        _handler = handler;
        _ = Task.Run(LoopAsync); // never faults: every failure inside is caught
    }

    /// <summary>Raised (on a pool thread) after each known request that was served, with its operation name.</summary>
    public event Action<string>? RequestHandled;

    /// <summary>Starts listening. Throws <see cref="IOException"/> when another program holds the pipe name.</summary>
    public static AgentHost Start(Func<AgentRequest, CancellationToken, Task<AgentResponse>> handler)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return new AgentHost(AgentPipe.CreateServer(), handler);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                break; // stopping (the pipe may already be disposed)
            }
            catch (Exception)
            {
                // A client that connected and vanished before we accepted it, or an instance left broken: reset
                // it and listen again (pausing, so a persistent fault can't spin).
                TryDisconnect();
                try
                {
                    await Task.Delay(50, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            string? served = null;
            try
            {
                await WaitForTokenAsync().ConfigureAwait(false);
                served = await ServeOneAsync().ConfigureAwait(false);
            }
            catch
            {
                // a misbehaving client, or stopping: drop it
            }
            finally
            {
                TryDisconnect(); // the same instance is reused, so the name is never released to anyone else
            }

            if (_stop.IsCancellationRequested)
            {
                break;
            }

            if (served is null)
            {
                try
                {
                    await Task.Delay(UnusablePause, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                RequestHandled?.Invoke(served);
            }
            catch
            {
                // a subscriber's problem
            }
        }
    }

    /// <summary>Serves one connection. Returns the operation when a known one was handled, else null.</summary>
    private async Task<string?> ServeOneAsync()
    {
        byte[]? message;
        using (var read = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            read.CancelAfter(ReadTimeout);
            message = await AgentPipe.ReadMessageAsync(_server, AgentPipe.MaxRequestBytes, read.Token).ConfigureAwait(false);
        }

        if (message is null)
        {
            return null;
        }

        AgentResponse response;
        AgentRequest? request = null;
        try
        {
            request = AgentPipe.Deserialize<AgentRequest>(message);
        }
        catch (JsonException)
        {
        }

        if (request is null || request.Op is null || !KnownOps.Contains(request.Op))
        {
            response = AgentResponse.Failure("Malformed request.");
            request = null; // never echo an unknown op anywhere (it is attacker text)
        }
        else
        {
            using var handle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            handle.CancelAfter(HandleTimeout);
            try
            {
                response = await _handler(request, handle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                response = AgentResponse.Failure("The app took too long to answer.");
            }
            catch (Exception ex)
            {
                if (Environment.TickCount64 - _lastLogged > 60_000)
                {
                    _lastLogged = Environment.TickCount64;
                    App.Log("Agent", ex);
                }

                response = AgentResponse.Failure("The app could not handle that request.");
            }
        }

        var payload = AgentPipe.Serialize(response);
        if (payload.Length > AgentPipe.MaxResponseBytes)
        {
            payload = AgentPipe.Serialize(AgentResponse.Failure("The result is too large; narrow the time range or page size."));
        }

        using var write = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        write.CancelAfter(WriteTimeout);
        await AgentPipe.WriteMessageAsync(_server, payload, write.Token).ConfigureAwait(false);
        return request?.Op;
    }

    /// <summary>Takes a token for the connection just accepted, waiting for one to refill if the bucket is empty.</summary>
    private async Task WaitForTokenAsync()
    {
        while (true)
        {
            var now = Environment.TickCount64;
            _tokens = Math.Min(BucketCapacity, _tokens + (now - _lastRefill) / 1000.0 * RefillPerSecond);
            _lastRefill = now;
            if (_tokens >= 1)
            {
                _tokens -= 1;
                return;
            }

            var wait = TimeSpan.FromSeconds((1 - _tokens) / RefillPerSecond);
            await Task.Delay(wait < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : wait, _stop.Token)
                .ConfigureAwait(false);
        }
    }

    private void TryDisconnect()
    {
        try
        {
            // Also for a Broken instance (the client went away mid-request): IsConnected is false then, but the
            // instance must still be disconnected before it can accept again.
            _server.Disconnect();
        }
        catch
        {
            // never connected, already disconnected, or disposed
        }
    }

    /// <summary>
    /// Stops serving at once. Doesn't wait for a request in progress: it may be waiting for the UI thread this
    /// is usually called on. Cancelling makes it give up, and with the pipe closed no reply can reach the client.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _server.Dispose();
        // _stop is left to the GC: the loop may still be unwinding and reading its token.
    }

    private int _disposed;
}
