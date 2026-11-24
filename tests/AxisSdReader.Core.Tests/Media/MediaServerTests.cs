using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AxisSdReader.Core.Media;

namespace AxisSdReader.Core.Tests.Media;

public class LocalMediaServerTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

    private static MediaSource Source(string name = "c0.mkv") => new(name, Payload.Length, () => new MemoryStream(Payload, writable: false));

    [Fact]
    public async Task ServesRangesAndWholeFiles()
    {
        using var server = LocalMediaServer.Start();
        using var lease = server.CreateLease([Source()]);
        using var http = new HttpClient();

        var whole = await http.GetByteArrayAsync(lease.Urls[0]);
        Assert.Equal(Payload, whole);

        var request = new HttpRequestMessage(HttpMethod.Get, lease.Urls[0]);
        request.Headers.Range = new RangeHeaderValue(1000, 1999);
        using var partial = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(Payload[1000..2000], await partial.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes", partial.Headers.AcceptRanges.Single());

        var suffix = new HttpRequestMessage(HttpMethod.Get, lease.Urls[0]);
        suffix.Headers.Range = new RangeHeaderValue(null, 10);
        using var tail = await http.SendAsync(suffix);
        Assert.Equal(Payload[^10..], await tail.Content.ReadAsByteArrayAsync());

        var beyond = new HttpRequestMessage(HttpMethod.Get, lease.Urls[0]);
        beyond.Headers.Range = new RangeHeaderValue(Payload.Length + 5, null);
        using var unsatisfiable = await http.SendAsync(beyond);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);
    }

    [Fact]
    public async Task RejectsUnknownTokensBrowsersAndRevokedLeases()
    {
        using var server = LocalMediaServer.Start();
        var lease = server.CreateLease([Source()]);
        using var http = new HttpClient();

        var guessed = new Uri($"http://127.0.0.1:{server.Port}/{new string('0', 32)}/c0.mkv");
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(guessed)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri(lease.Urls[0], "c9.mkv"))).StatusCode);

        // A web page that somehow learned the URL is still refused (fetch metadata / Origin present).
        var fromBrowser = new HttpRequestMessage(HttpMethod.Get, lease.Urls[0]);
        fromBrowser.Headers.Add("Origin", "https://example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(fromBrowser)).StatusCode);

        var post = new HttpRequestMessage(HttpMethod.Post, lease.Urls[0]) { Content = new StringContent("x") };
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.SendAsync(post)).StatusCode);

        lease.Dispose();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(lease.Urls[0])).StatusCode);
    }

    [Fact]
    public async Task RejectsForeignHostHeader()
    {
        // DNS rebinding: a browser talking to "attacker.example" that resolves to 127.0.0.1 sends that Host.
        using var server = LocalMediaServer.Start();
        using var lease = server.CreateLease([Source()]);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        var request = $"GET {lease.Urls[0].AbsolutePath} HTTP/1.1\r\nHost: attacker.example\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer);
        Assert.StartsWith("HTTP/1.1 403", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task ClosesTheConnectionAfterAnError()
    {
        // One guess per connection: a wrong token gets a 404 and the connection is closed, so nobody can probe on
        // through a kept-alive connection, or hold one open by sending bad requests.
        using var server = LocalMediaServer.Start();
        using var lease = server.CreateLease([Source()]);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        var request = $"GET /{new string('0', 32)}/c0.mkv HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var response = await ReadUntilClosedAsync(stream, TimeSpan.FromSeconds(5));
        Assert.StartsWith("HTTP/1.1 404", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task DropsConnectionsThatNeverAsk()
    {
        using var server = LocalMediaServer.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("", await ReadUntilClosedAsync(client.GetStream(), TimeSpan.FromSeconds(20)));
        Assert.InRange(started.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task IdleConnectionsCantCrowdOutARealClient()
    {
        // More idle sockets than the pending budget: the oldest are dropped to admit new connections, so a real
        // client (which asks at once) still gets in.
        using var server = LocalMediaServer.Start();
        using var lease = server.CreateLease([Source()]);
        var idle = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 80; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, server.Port);
                idle.Add(c);
            }

            using var http = new HttpClient();
            Assert.Equal(Payload, await http.GetByteArrayAsync(lease.Urls[0]));
        }
        finally
        {
            idle.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public async Task IdleConnectionsDontTakeStreamingSlots()
    {
        // Connections that haven't named a live lease get only a small "pending" budget; the streaming slots stay
        // for clients that have. With one slot and ten idle connections, a real client still gets its footage.
        using var server = LocalMediaServer.Start(maxConnections: 1);
        using var lease = server.CreateLease([Source()]);
        var idle = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 10; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, server.Port);
                idle.Add(c);
            }

            using var http = new HttpClient();
            Assert.Equal(Payload, await http.GetByteArrayAsync(lease.Urls[0]));
        }
        finally
        {
            idle.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public async Task DropsConnectionsBeyondThePendingBudget()
    {
        using var server = LocalMediaServer.Start();
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 80; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, server.Port);
                clients.Add(c);
            }

            await Task.Delay(1000);
            var closed = clients.Count(c => c.Client.Poll(0, SelectMode.SelectRead) && c.Client.Available == 0);
            Assert.InRange(closed, 80 - 64, 80 - 60); // as many as went past the budget (the oldest), closed straight away
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public void RecognisesOnlyItsOwnLeaseUrls()
    {
        using var server = LocalMediaServer.Start();
        using var lease = server.CreateLease([Source(), Source("c1.mkv")]);
        Assert.All(lease.Urls, url => Assert.True(LocalMediaServer.IsLeaseUrl(url)));

        var token = lease.Urls[0].Segments[1].TrimEnd('/');
        var port = server.Port;
        foreach (var bad in new[]
                 {
                     $"https://127.0.0.1:{port}/{token}/c0.mkv",
                     $"http://localhost:{port}/{token}/c0.mkv",
                     $"http://127.0.0.2:{port}/{token}/c0.mkv",
                     $"http://127.0.0.1/{token}/c0.mkv",
                     $"http://user@127.0.0.1:{port}/{token}/c0.mkv",
                     $"http://127.0.0.1:{port}/{token}/c0.mkv?x=1",
                     $"http://127.0.0.1:{port}/{token}/c0.mkv#x",
                     $"http://127.0.0.1:{port}/{token.ToUpperInvariant()}/c0.mkv",
                     $"http://127.0.0.1:{port}/{token}/c0.mp4",
                     $"http://127.0.0.1:{port}/{token}/../c0.mkv",
                     $"http://127.0.0.1:{port}/{token}/sub/c0.mkv",
                     $"http://127.0.0.1:{port}/{token[..31]}/c0.mkv",
                     $"http://127.0.0.1:{port}/{token}/c0.mkv'",
                 })
        {
            Assert.False(LocalMediaServer.IsLeaseUrl(new Uri(bad)), bad);
        }
    }

    [Fact]
    public void MasksTokensInText() =>
        Assert.Equal("[http @ 0] http://127.0.0.1:5000/…/c0.mkv: Invalid data",
            LocalMediaServer.RedactTokens("[http @ 0] http://127.0.0.1:5000/0123456789abcdef0123456789abcdef/c0.mkv: Invalid data"));

    /// <summary>Everything the server sends until it closes the connection; fails if it is still open after the timeout.</summary>
    private static async Task<string> ReadUntilClosedAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var all = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, deadline.Token);
            if (read == 0)
            {
                return Encoding.ASCII.GetString(all.ToArray());
            }

            all.Write(buffer, 0, read);
        }
    }

    [Theory]
    [InlineData("bytes=0-99", 1000, 0, 99)]
    [InlineData("bytes=900-", 1000, 900, 999)]
    [InlineData("bytes=-100", 1000, 900, 999)]
    [InlineData("bytes=500-5000", 1000, 500, 999)]
    public void ParsesRanges(string header, long length, long start, long end)
    {
        Assert.True(LocalMediaServer.TryParseRange(header, length, out var s, out var e));
        Assert.Equal((start, end), (s, e));
    }

    [Theory]
    [InlineData("bytes=1000-", 1000)]
    [InlineData("bytes=5-1", 1000)]
    [InlineData("items=0-1", 1000)]
    [InlineData("bytes=-0", 1000)]
    [InlineData("bytes=abc", 1000)]
    public void RejectsBadRanges(string header, long length) =>
        Assert.False(LocalMediaServer.TryParseRange(header, length, out _, out _));
}

public class CardIoGateTests
{
    [Fact]
    public void ClosedGateFailsFastAndNeverTouchesTheInnerStream()
    {
        var gate = new CardIoGate();
        var inner = new MemoryStream(new byte[100]);
        var opened = 0;
        using var stream = new SynchronizedCardStream(gate, () => { opened++; return inner; }, 100);

        Assert.Equal(0, opened); // lazy: nothing is opened until the first read
        Assert.Equal(100, stream.Length);
        Assert.Equal(10, stream.Read(new byte[10]));
        Assert.Equal(1, opened);

        gate.Close(() => inner.Dispose(), TimeSpan.FromSeconds(1));
        Assert.Throws<CardClosedException>(() => stream.Read(new byte[10]));
        Assert.IsAssignableFrom<OperationCanceledException>(new CardClosedException());
        stream.Dispose(); // must not throw against the torn-down inner stream
    }

    [Fact]
    public async Task SerializesConcurrentReaders()
    {
        var gate = new CardIoGate();
        var data = Enumerable.Range(0, 1 << 16).Select(i => (byte)i).ToArray();
        var concurrent = 0;
        var maxConcurrent = 0;

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var stream = new SynchronizedCardStream(gate, () => new ObservingStream(data, () =>
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);
                Thread.SpinWait(2000);
                Interlocked.Decrement(ref concurrent);
            }), data.Length);
            var buffer = new byte[4096];
            while (stream.Read(buffer) > 0)
            {
            }
        }));
        await Task.WhenAll(tasks);
        Assert.Equal(1, maxConcurrent);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class ObservingStream(byte[] data, Action onRead) : MemoryStream(data, writable: false)
    {
        public override int Read(Span<byte> buffer)
        {
            onRead();
            return base.Read(buffer);
        }
    }
}
