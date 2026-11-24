using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AxisSdReader.Core.Media;

/// <summary>One file a lease makes available: a URL-safe name, its length, and how to open it.</summary>
public sealed record MediaSource(string Name, long Length, Func<Stream> Open, string ContentType = "video/x-matroska");

/// <summary>
/// A set of files served under one unguessable URL prefix (<c>http://127.0.0.1:port/&lt;token&gt;/name</c>).
/// The lease expires after a period without use and can be revoked at any time, which also aborts any
/// transfer in progress.
/// </summary>
public sealed class MediaLease : IDisposable
{
    private readonly LocalMediaServer _server;
    private long _expiresTicks;
    private int _activeTransfers;

    internal MediaLease(LocalMediaServer server, string token, IReadOnlyList<MediaSource> sources, TimeSpan ttl)
    {
        _server = server;
        Token = token;
        Sources = sources;
        Ttl = ttl;
        Urls = sources.Select(s => new Uri($"http://127.0.0.1:{server.Port}/{token}/{s.Name}")).ToList();
        Touch();
    }

    public string Token { get; }

    public IReadOnlyList<MediaSource> Sources { get; }

    /// <summary>One URL per source, in order.</summary>
    public IReadOnlyList<Uri> Urls { get; }

    public TimeSpan Ttl { get; }

    internal CancellationTokenSource Revoked { get; } = new();

    internal long ExpiresTicks => Interlocked.Read(ref _expiresTicks);

    /// <summary>A transfer from it is in progress: it neither expires nor is evicted meanwhile.</summary>
    internal bool IsBusy => Volatile.Read(ref _activeTransfers) > 0;

    internal bool IsExpired => !IsBusy && DateTime.UtcNow.Ticks > ExpiresTicks;

    /// <summary>True once the lease was revoked (or evicted); its URLs no longer work.</summary>
    public bool IsRevoked => Revoked.IsCancellationRequested;

    /// <summary>True while the lease's URLs still work.</summary>
    public bool IsLive => !IsRevoked && !IsExpired;

    internal void Touch() => Interlocked.Exchange(ref _expiresTicks, (DateTime.UtcNow + Ttl).Ticks);

    internal void BeginTransfer() => Interlocked.Increment(ref _activeTransfers);

    /// <summary>The idle clock starts again when a transfer ends, not when it began.</summary>
    internal void EndTransfer()
    {
        Touch();
        Interlocked.Decrement(ref _activeTransfers);
    }

    public void Dispose() => _server.Revoke(Token);
}

/// <summary>
/// A minimal HTTP/1.1 server on 127.0.0.1 that streams card files with byte-range support, so FFmpeg can read
/// recordings straight off the card — no decrypted copies on disk — and seek within them.
/// <para>Hardening: loopback only; every URL carries a 128-bit random token; GET/HEAD only; strict request
/// size limits and timeouts; a capped connection count; the Host header must name this server (defeats DNS
/// rebinding) and requests carrying browser fetch metadata (<c>Origin</c>, <c>Sec-Fetch-*</c>) are refused,
/// so a web page cannot read footage even if it learned a URL.</para>
/// </summary>
public sealed partial class LocalMediaServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxRequestsPerConnection = 10_000;
    private const int MaxLeases = 4096; // above any one user's cache (the agent service keeps 1024), so its rule governs
    private const int MaxSlotWaiters = 32;
    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(15);
    private const int MaxPendingConnections = 64;
    private const int BodyBufferBytes = 256 * 1024;
    private static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(2); // real clients ask at once
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(60);

    // Connections that have not yet presented a valid URL, oldest first. When the budget is full the OLDEST is
    // dropped to admit the newest: a real client names its URL within moments of connecting, so idle sockets
    // held open to crowd it out are the ones that go.
    private readonly LinkedList<Connection> _pending = new();

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, MediaLease> _leases = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connections;
    private int _slotWaiters;
    private readonly Timer _sweeper;
    private readonly Task _acceptLoop;

    private LocalMediaServer(int maxConnections)
    {
        _connections = new SemaphoreSlim(maxConnections, maxConnections);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(backlog: 32);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _sweeper = new Timer(_ => SweepExpired(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Starts a server on a random free loopback port.</summary>
    public static LocalMediaServer Start(int maxConnections = 24) => new(maxConnections);

    public int Port { get; }

    /// <summary>Raised (on a pool thread) after each response body, with the bytes sent.</summary>
    public event Action<long>? BytesServed;

    public int ActiveLeaseCount => _leases.Count;

    /// <summary>Publishes <paramref name="sources"/> under a new random token.</summary>
    public MediaLease CreateLease(IReadOnlyList<MediaSource> sources, TimeSpan? ttl = null)
    {
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        foreach (var source in sources)
        {
            if (!NamePattern().IsMatch(source.Name))
            {
                throw new ArgumentException($"'{source.Name}' is not a valid media name.", nameof(sources));
            }
        }

        if (_leases.Count >= MaxLeases)
        {
            // Bounded memory: drop the leases closest to expiry first, never one that is mid-transfer.
            SweepExpired();
            foreach (var oldest in _leases.Values.Where(l => !l.IsBusy).OrderBy(l => l.ExpiresTicks)
                         .Take(Math.Max(0, _leases.Count - MaxLeases + 1)).ToList())
            {
                Revoke(oldest.Token);
            }
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var lease = new MediaLease(this, token, sources, ttl ?? TimeSpan.FromMinutes(10));
        _leases[token] = lease;
        return lease;
    }

    /// <summary>Revokes a lease: its URLs stop working and transfers in progress are aborted.</summary>
    public void Revoke(string token)
    {
        if (_leases.TryRemove(token, out var lease))
        {
            try
            {
                lease.Revoked.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void RevokeAll()
    {
        foreach (var token in _leases.Keys.ToList())
        {
            Revoke(token);
        }
    }

    private void SweepExpired()
    {
        foreach (var lease in _leases.Values.Where(l => l.IsExpired).ToList())
        {
            Revoke(lease.Token);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            // Connections are admitted in two stages, so nobody without a valid URL can occupy the streaming slots:
            // a new connection is "pending" (small budget, short deadline) until its first request names a live
            // lease; only then does it take one of the limited streaming slots.
            if (client.Client.RemoteEndPoint is not IPEndPoint { Address: var remote } || !IPAddress.IsLoopback(remote))
            {
                client.Dispose();
                continue;
            }

            var connection = new Connection();
            lock (_pending)
            {
                if (_pending.Count >= MaxPendingConnections && _pending.First is { } oldest)
                {
                    _pending.RemoveFirst();
                    oldest.Value.Node = null;
                    oldest.Value.Evict();
                }

                connection.Node = _pending.AddLast(connection);
            }

            _ = Task.Run(() => HandleConnectionAsync(client, connection));
        }
    }

    /// <summary>Takes a connection off the pending list. False if it was already dropped from it (evicted).</summary>
    private bool LeavePending(Connection connection)
    {
        lock (_pending)
        {
            if (connection.Node is not { } node)
            {
                return false;
            }

            _pending.Remove(node);
            connection.Node = null;
            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="url"/> has exactly the shape of a URL this server hands out
    /// (<c>http://127.0.0.1:port/&lt;32 hex&gt;/c&lt;n&gt;.mkv</c>, nothing else). Consumers that pass URLs
    /// received over IPC to FFmpeg check this first, so a forged reply can't point FFmpeg anywhere else.
    /// </summary>
    public static bool IsLeaseUrl(Uri url) =>
        url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttp && url.Host == "127.0.0.1" && !url.IsDefaultPort &&
        url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0 &&
        LeasePathPattern().IsMatch(url.AbsolutePath);

    [GeneratedRegex(@"^/[0-9a-f]{32}/c[0-9]{1,4}\.mkv$")]
    private static partial Regex LeasePathPattern();

    /// <summary>Masks lease tokens in text (FFmpeg quotes the URLs it read in its errors): a token is a credential
    /// for as long as its lease lives, and errors end up in dialogs, logs and agent replies.</summary>
    public static string RedactTokens(string text) => TokenPattern().Replace(text, "…");

    [GeneratedRegex("[0-9a-fA-F]{32}")]
    private static partial Regex TokenPattern();

    private sealed class Connection
    {
        public byte[] Buffer = new byte[MaxHeaderBytes];
        public int Filled;
        public MediaSource? OpenSource;
        public Stream? OpenStream;
        public bool HoldsSlot;
        public LinkedListNode<Connection>? Node; // on the pending list (guarded by it)
        public readonly CancellationTokenSource Evicted = new();

        public void Evict()
        {
            try
            {
                Evicted.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, Connection connection)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var network = client.GetStream();
                for (var i = 0; i < MaxRequestsPerConnection && !_stop.IsCancellationRequested; i++)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, connection.Evicted.Token);
                    readTimeout.CancelAfter(connection.HoldsSlot ? IdleTimeout : FirstRequestTimeout);

                    var request = await ReadRequestAsync(network, connection, readTimeout.Token).ConfigureAwait(false);
                    if (request is null)
                    {
                        break;
                    }

                    if (!await RespondAsync(network, connection, request).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
            // client went away, timed out, or sent garbage: just drop the connection
        }
        finally
        {
            try
            {
                connection.OpenStream?.Dispose();
            }
            catch
            {
                // best effort
            }

            if (connection.HoldsSlot)
            {
                _connections.Release();
            }
            else
            {
                LeavePending(connection);
            }

            connection.Evicted.Dispose();
        }
    }

    private sealed record Request(string Method, string Target, string Version, Dictionary<string, string> Headers);

    /// <summary>Reads one request head (request line + headers). Null when the client closed cleanly.</summary>
    private static async Task<Request?> ReadRequestAsync(NetworkStream network, Connection c, CancellationToken cancellationToken)
    {
        while (true)
        {
            var end = c.Buffer.AsSpan(0, c.Filled).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                var head = Encoding.ASCII.GetString(c.Buffer, 0, end);
                var consumed = end + 4;
                Buffer.BlockCopy(c.Buffer, consumed, c.Buffer, 0, c.Filled - consumed);
                c.Filled -= consumed;
                return ParseHead(head);
            }

            if (c.Filled == c.Buffer.Length)
            {
                throw new InvalidDataException("Request head too large.");
            }

            var read = await network.ReadAsync(c.Buffer.AsMemory(c.Filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (c.Filled == 0)
                {
                    return null;
                }

                throw new InvalidDataException("Truncated request.");
            }

            c.Filled += read;
        }
    }

    private static Request ParseHead(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Bad request line.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new InvalidDataException("Bad header.");
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing) ? existing + "," + value : value;
        }

        return new Request(parts[0], parts[1], parts[2], headers);
    }

    /// <summary>Writes one response. Returns whether the connection may serve another request.</summary>
    private async Task<bool> RespondAsync(NetworkStream network, Connection c, Request request)
    {
        var keepAlive = request.Version == "HTTP/1.1"
            ? !HeaderHas(request, "Connection", "close")
            : HeaderHas(request, "Connection", "keep-alive");

        if (request.Method is not ("GET" or "HEAD"))
        {
            await WriteStatusAsync(network, 405, "Method Not Allowed", "Allow: GET, HEAD\r\n").ConfigureAwait(false);
            return false;
        }

        // DNS-rebinding and browser defences: the Host must be this server, and browser fetches are refused.
        if (!request.Headers.TryGetValue("Host", out var host) ||
            !(host == $"127.0.0.1:{Port}" || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase)) ||
            request.Headers.ContainsKey("Origin") || request.Headers.ContainsKey("Sec-Fetch-Site") ||
            request.Headers.ContainsKey("Sec-Fetch-Mode"))
        {
            await WriteStatusAsync(network, 403, "Forbidden").ConfigureAwait(false);
            return false;
        }

        // Any error closes the connection: a client that guesses gets one try per connection, and can't keep
        // connections open by sending bad requests.
        var match = TargetPattern().Match(request.Target);
        if (!match.Success || !_leases.TryGetValue(match.Groups["token"].Value, out var lease) || !lease.IsLive)
        {
            await WriteStatusAsync(network, 404, "Not Found").ConfigureAwait(false);
            return false;
        }

        var source = lease.Sources.FirstOrDefault(s => s.Name == match.Groups["name"].Value);
        if (source is null)
        {
            await WriteStatusAsync(network, 404, "Not Found").ConfigureAwait(false);
            return false;
        }

        // The request names a live lease: move this connection from "pending" to a streaming slot (unless it was
        // dropped from the pending list meanwhile).
        if (!c.HoldsSlot)
        {
            if (!LeavePending(c))
            {
                return false;
            }

            if (!_connections.Wait(0))
            {
                // Every slot busy (agents' frames and an export at once): wait a little for one rather than fail a reader
                // mid-export - a few at a time, so waiting can't become a way to hold connections open.
                var admitted = false;
                if (Interlocked.Increment(ref _slotWaiters) <= MaxSlotWaiters)
                {
                    try
                    {
                        admitted = await _connections.WaitAsync(SlotWait, _stop.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _slotWaiters);
                    }
                }
                else
                {
                    Interlocked.Decrement(ref _slotWaiters);
                }

                if (!admitted)
                {
                    await WriteStatusAsync(network, 503, "Service Unavailable").ConfigureAwait(false);
                    return false;
                }
            }

            c.HoldsSlot = true;
            if (!lease.IsLive)
            {
                // It expired or was revoked while this waited for a slot: not revived by being read now.
                await WriteStatusAsync(network, 404, "Not Found").ConfigureAwait(false);
                return false;
            }
        }

        lease.Touch();
        var length = source.Length;
        long start = 0, end = length - 1;
        var partial = false;
        if (request.Headers.TryGetValue("Range", out var range) && !range.Contains(','))
        {
            if (!TryParseRange(range, length, out start, out end))
            {
                await WriteStatusAsync(network, 416, "Range Not Satisfiable", $"Content-Range: bytes */{length}\r\n").ConfigureAwait(false);
                return false;
            }

            partial = true;
        }

        var count = Math.Max(0, end - start + 1);
        var head = new StringBuilder()
            .Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: ").Append(source.ContentType).Append("\r\n")
            .Append("Content-Length: ").Append(count.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
        if (partial)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {start}-{end}/{length}\r\n");
        }

        head.Append("\r\n");

        using var write = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, lease.Revoked.Token);
        await WriteWithTimeoutAsync(network, Encoding.ASCII.GetBytes(head.ToString()), write.Token).ConfigureAwait(false);
        if (request.Method == "HEAD" || count == 0)
        {
            return keepAlive;
        }

        // Reuse the open file across requests on this connection (FFmpeg re-requests ranges as it seeks).
        if (!ReferenceEquals(c.OpenSource, source))
        {
            c.OpenStream?.Dispose();
            c.OpenStream = null;
            c.OpenSource = source;
            c.OpenStream = source.Open();
        }

        var file = c.OpenStream!;
        file.Position = start;

        // Pooled (and cleared on return: it held footage); the lease counts as busy for the whole transfer, so
        // it can't expire or be evicted under a long read.
        var buffer = ArrayPool<byte>.Shared.Rent(BodyBufferBytes);
        long sent = 0;
        lease.BeginTransfer();
        try
        {
            while (sent < count)
            {
                write.Token.ThrowIfCancellationRequested();
                var want = (int)Math.Min(BodyBufferBytes, count - sent);
                var read = file.Read(buffer, 0, want); // synchronous: card reads are serialized by the card's gate
                if (read == 0)
                {
                    throw new IOException("Source ended early."); // can't honour Content-Length; drop the connection
                }

                await WriteWithTimeoutAsync(network, buffer.AsMemory(0, read), write.Token).ConfigureAwait(false);
                sent += read;
            }
        }
        finally
        {
            lease.EndTransfer();
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        try
        {
            BytesServed?.Invoke(sent);
        }
        catch
        {
            // a subscriber's problem must not break streaming
        }

        return keepAlive;
    }

    private static async Task WriteWithTimeoutAsync(NetworkStream network, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WriteTimeout);
        await network.WriteAsync(data, timeout.Token).ConfigureAwait(false);
    }

    private static async Task WriteStatusAsync(NetworkStream network, int code, string reason, string extraHeaders = "")
    {
        // Every status-only reply ends the connection, so say so (clients then won't try to reuse it).
        var text = $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n{extraHeaders}\r\n";
        using var timeout = new CancellationTokenSource(WriteTimeout);
        await network.WriteAsync(Encoding.ASCII.GetBytes(text), timeout.Token).ConfigureAwait(false);
    }

    private static bool HeaderHas(Request request, string name, string token) =>
        request.Headers.TryGetValue(name, out var value) &&
        value.Split(',').Any(v => v.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses a single <c>bytes=</c> range against a resource length (RFC 9110 §14.1.2).</summary>
    internal static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || length <= 0)
        {
            return false;
        }

        var spec = header[6..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            // suffix range: the last N bytes
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, length - suffix);
            return true;
        }

        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length)
        {
            return false;
        }

        if (last.Length > 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
            {
                return false;
            }

            end = Math.Min(end, length - 1);
        }

        return true;
    }

    [GeneratedRegex(@"^/(?<token>[0-9a-f]{32})/(?<name>[A-Za-z0-9][A-Za-z0-9._-]{0,63})$")]
    private static partial Regex TargetPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex NamePattern();

    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        _sweeper.Dispose();
        RevokeAll();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // best effort
        }

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // best effort
        }
    }
}
