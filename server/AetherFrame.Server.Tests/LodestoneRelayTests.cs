using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.LodestoneRelay;
using AetherFrame.Server.Lodestone;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>
/// The Lodestone relay (docs/networking/Runbook.md, "The Lodestone relay") and the server's side of
/// it. Everything runs on loopback: the relay's "Lodestone" is a local listener, and nothing here
/// connects to the real Lodestone.
/// </summary>
public sealed class LodestoneRelayTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("CONNECT na.finalfantasyxiv.com:443 HTTP/1.1", true)]
    [InlineData("CONNECT na.finalfantasyxiv.com:443 HTTP/1.0", true)]
    [InlineData("CONNECT NA.FinalFantasyXIV.com:443 HTTP/1.1", true)]
    [InlineData("connect na.finalfantasyxiv.com:443 HTTP/1.1", false)]
    [InlineData("CONNECT na.finalfantasyxiv.com:80 HTTP/1.1", false)]
    [InlineData("CONNECT na.finalfantasyxiv.com.:443 HTTP/1.1", false)]
    [InlineData("CONNECT eu.finalfantasyxiv.com:443 HTTP/1.1", false)]
    [InlineData("CONNECT na.finalfantasyxiv.com:443@example.com:443 HTTP/1.1", false)]
    [InlineData("CONNECT example.com:443 HTTP/1.1", false)]
    [InlineData("CONNECT  na.finalfantasyxiv.com:443 HTTP/1.1", false)]
    [InlineData("CONNECT na.finalfantasyxiv.com:443 HTTP/2", false)]
    [InlineData("GET https://na.finalfantasyxiv.com/ HTTP/1.1", false)]
    [InlineData("", false)]
    public void OnlyTheLodestonesConnect_IsServed(string line, bool served)
    {
        Assert.Equal(served, Relay.IsLodestoneConnect(line));
    }

    [Fact]
    public void AWildcardAddress_IsNeverListenedOn()
    {
        Assert.Throws<ArgumentException>(() => new Relay(new RelayOptions { Listen = new IPEndPoint(IPAddress.Any, 8443), Client = IPAddress.Loopback }, _ => { }));
        Assert.Throws<ArgumentException>(() => new Relay(new RelayOptions { Listen = new IPEndPoint(IPAddress.IPv6Any, 8443), Client = IPAddress.Loopback }, _ => { }));
    }

    [Fact]
    public async Task TheLodestonesConnect_OpensATunnel_ThatCarriesBytesBothWays()
    {
        await using var harness = await RelayHarness.StartAsync();
        using var client = await harness.ConnectAsync();
        var stream = client.GetStream();
        await Send(stream, "CONNECT na.finalfantasyxiv.com:443 HTTP/1.1\r\nHost: na.finalfantasyxiv.com:443\r\n\r\n");
        Assert.Equal("HTTP/1.1 200 Connection Established\r\n\r\n", await ReadAnswer(stream));

        await stream.WriteAsync("hello"u8.ToArray());
        Assert.Equal("hello", await ReadExactly(stream, 5));
        Assert.Equal(1, harness.Upstream.Connections);
    }

    [Fact]
    public async Task BytesSentRightAfterTheHead_GoOnToTheLodestone()
    {
        await using var harness = await RelayHarness.StartAsync();
        using var client = await harness.ConnectAsync();
        var stream = client.GetStream();
        await Send(stream, "CONNECT na.finalfantasyxiv.com:443 HTTP/1.1\r\n\r\nearly");
        Assert.Equal("HTTP/1.1 200 Connection Established\r\n\r\n", await ReadAnswer(stream));
        Assert.Equal("early", await ReadExactly(stream, 5));
    }

    [Theory]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT na.finalfantasyxiv.com:80 HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://na.finalfantasyxiv.com/ HTTP/1.1\r\nHost: na.finalfantasyxiv.com\r\n\r\n")]
    [InlineData(null)]
    public async Task AnyOtherRequest_Is403_AndConnectsNothing(string? request)
    {
        // Null stands for a request line holding a control character.
        request ??= "CONNECT na.finalfantasyxiv.com:443 HTTP/1.1" + (char)0x01 + "\r\n\r\n";
        await using var harness = await RelayHarness.StartAsync();
        using var client = await harness.ConnectAsync();
        var stream = client.GetStream();
        await Send(stream, request);
        Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", await ReadToEnd(stream));
        Assert.Equal(0, harness.Upstream.Connections);
    }

    [Fact]
    public async Task AnotherClientsConnection_IsClosedUnanswered()
    {
        await using var harness = await RelayHarness.StartAsync(client: IPAddress.Parse("100.64.0.9"));
        using var client = await harness.ConnectAsync();
        var stream = client.GetStream();
        await Send(stream, "CONNECT na.finalfantasyxiv.com:443 HTTP/1.1\r\n\r\n");
        Assert.Equal("", await ReadToEnd(stream));
        Assert.Equal(0, harness.Upstream.Connections);
        Assert.Contains(harness.Log, line => line.Contains("refused a connection from 127.0.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHeadThatNeverEnds_IsClosedAfterItsTime()
    {
        await using var harness = await RelayHarness.StartAsync(headTimeout: TimeSpan.FromMilliseconds(300));
        using var client = await harness.ConnectAsync();
        var stream = client.GetStream();
        await Send(stream, "CONNECT na.finalfantasyxiv.com:443 HTTP/1.1\r\n");
        Assert.Equal("", await ReadToEnd(stream));
        Assert.Equal(0, harness.Upstream.Connections);
    }

    [Fact]
    public async Task AHeadTooLong_IsClosed()
    {
        await using var harness = await RelayHarness.StartAsync();
        using var client = await harness.ConnectAsync();
        var stream = client.GetStream();
        await Send(stream, "CONNECT na.finalfantasyxiv.com:443 HTTP/1.1\r\nX: " + new string('a', Relay.MaxHeadBytes) + "\r\n\r\n");
        Assert.Equal("", await ReadToEnd(stream));
        Assert.Equal(0, harness.Upstream.Connections);
    }

    [Fact]
    public async Task ALodestoneThatCantBeReached_Is502()
    {
        await using var harness = await RelayHarness.StartAsync(unreachable: true);
        using var client = await harness.ConnectAsync();
        var stream = client.GetStream();
        await Send(stream, "CONNECT na.finalfantasyxiv.com:443 HTTP/1.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 502 Bad Gateway\r\n", await ReadToEnd(stream));
    }

    [Fact]
    public async Task AnIdleTunnel_IsClosed()
    {
        await using var harness = await RelayHarness.StartAsync(idleTimeout: TimeSpan.FromMilliseconds(300));
        using var client = await harness.ConnectAsync();
        var stream = client.GetStream();
        await Send(stream, "CONNECT na.finalfantasyxiv.com:443 HTTP/1.1\r\n\r\n");
        Assert.Equal("HTTP/1.1 200 Connection Established\r\n\r\n", await ReadAnswer(stream));
        Assert.Equal("", await ReadToEnd(stream));
        Assert.Contains(harness.Log, line => line.Contains("tunnel closed (idle)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATunnelCarryingTooMuch_IsClosed()
    {
        await using var harness = await RelayHarness.StartAsync(maxBytesDown: 8);
        using var client = await harness.ConnectAsync();
        var stream = client.GetStream();
        await Send(stream, "CONNECT na.finalfantasyxiv.com:443 HTTP/1.1\r\n\r\n");
        Assert.Equal("HTTP/1.1 200 Connection Established\r\n\r\n", await ReadAnswer(stream));
        await stream.WriteAsync("more than eight bytes"u8.ToArray());
        Assert.Equal("", await ReadToEnd(stream));
        Assert.Contains(harness.Log, line => line.Contains("tunnel closed (too many bytes down)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TunnelsBeyondTheLimit_Are503()
    {
        await using var harness = await RelayHarness.StartAsync(maxTunnels: 1);
        using var first = await harness.ConnectAsync();
        await Send(first.GetStream(), "CONNECT na.finalfantasyxiv.com:443 HTTP/1.1\r\n\r\n");
        Assert.Equal("HTTP/1.1 200 Connection Established\r\n\r\n", await ReadAnswer(first.GetStream()));

        using var second = await harness.ConnectAsync();
        await Send(second.GetStream(), "CONNECT na.finalfantasyxiv.com:443 HTTP/1.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 503 Service Unavailable\r\n", await ReadToEnd(second.GetStream()));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("100.101.102.103:8443", true)]
    [InlineData("[fd7a:115c:a1e0::1]:8443", true)]
    [InlineData("100.101.102.103", false)]
    [InlineData("100.101.102.103:0", false)]
    [InlineData("http://100.101.102.103:8443", false)]
    [InlineData("relay.example:8443", false)]
    [InlineData(" 100.101.102.103:8443", false)]
    [InlineData("1:8443", false)]
    [InlineData("0.0.0.0:8443", false)]
    [InlineData("[::]:8443", false)]
    [InlineData("255.255.255.255:8443", false)]
    [InlineData("224.0.0.1:8443", false)]
    [InlineData("[ff02::1]:8443", false)]
    public void TheRelaySetting_IsExactlyAnAddressAndPort(string text, bool accepted)
    {
        Assert.Equal(accepted, ServerOptions.TryParseRelay(text, out var relay));
        Assert.Equal(accepted && text.Length > 0, relay is not null);
    }

    [Fact]
    public void WithoutARelay_TheLodestoneIsReachedDirectly_AndWithOne_ThroughItAlone()
    {
        using var direct = LodestoneHttpPages.CreateHandler(null);
        Assert.False(direct.UseProxy);
        Assert.Null(direct.Proxy);

        using var relayed = LodestoneHttpPages.CreateHandler(IPEndPoint.Parse("100.101.102.103:8443"));
        Assert.True(relayed.UseProxy);
        Assert.Equal(new Uri("http://100.101.102.103:8443/"), relayed.Proxy!.GetProxy(LodestoneHttpPages.Origin));
        Assert.False(relayed.Proxy.IsBypassed(LodestoneHttpPages.Origin));

        // The Lodestone's certificate is checked as .NET checks any: nothing overrides it.
        Assert.Null(relayed.SslOptions.RemoteCertificateValidationCallback);
        Assert.False(relayed.AllowAutoRedirect);
        Assert.False(relayed.UseCookies);
    }

    [Fact]
    public async Task TheServersLodestoneFetch_GoesThroughTheRelay_AsTlsToTheLodestone()
    {
        // The relay's "Lodestone" here only records what arrives and closes: the server's client
        // opens TLS to na.finalfantasyxiv.com through the tunnel, gets no certificate, and reports
        // the fetch as failed, which a check treats as "try later".
        await using var harness = await RelayHarness.StartAsync(echo: false);
        using var handler = LodestoneHttpPages.CreateHandler(harness.Relay.LocalEndPoint);
        var pages = new LodestoneHttpPages(new OneClient(handler));

        var response = await pages.GetAsync(12345678, CancellationToken.None);

        Assert.Equal(0, response.Status);
        Assert.Null(response.Html);
        Assert.Equal(1, harness.Upstream.Connections);
        var hello = await harness.Upstream.FirstBytes.Task.WaitAsync(Wait);
        Assert.Equal(0x16, hello[0]);
        Assert.Contains("na.finalfantasyxiv.com", Encoding.ASCII.GetString(hello), StringComparison.Ordinal);
        Assert.DoesNotContain("lodestone/character", Encoding.ASCII.GetString(hello), StringComparison.Ordinal);
    }

    private static Task Send(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    private static async Task<string> ReadAnswer(NetworkStream stream)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        using var timeout = new CancellationTokenSource(Wait);
        var one = new byte[1];
        while (!Encoding.ASCII.GetString(bytes.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, timeout.Token) == 0)
            {
                break;
            }

            bytes.Add(one[0]);
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static async Task<string> ReadExactly(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        using var timeout = new CancellationTokenSource(Wait);
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return Encoding.ASCII.GetString(buffer);
    }

    private static async Task<string> ReadToEnd(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(Wait);
        using var buffer = new MemoryStream();
        try
        {
            await stream.CopyToAsync(buffer, timeout.Token);
        }
        catch (IOException)
        {
            // A reset ends the stream as a close does.
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A stand-in for the Lodestone: echoes what it gets, or records the first bytes and closes.</summary>
    private sealed class FakeUpstream : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly bool echo;
        private int connections;

        public FakeUpstream(bool echo)
        {
            this.echo = echo;
            listener.Start();
            _ = AcceptAsync();
        }

        public int Connections => Volatile.Read(ref connections);

        public TaskCompletionSource<byte[]> FirstBytes { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPEndPoint EndPoint => (IPEndPoint)listener.LocalEndpoint;

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                try
                {
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer, stop.Token);
                        if (read == 0)
                        {
                            return;
                        }

                        if (!echo)
                        {
                            FirstBytes.TrySetResult(buffer[..read]);
                            return;
                        }

                        await stream.WriteAsync(buffer.AsMemory(0, read), stop.Token);
                    }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }

        public async ValueTask<Stream> ConnectAsync(CancellationToken cancellation)
        {
            Interlocked.Increment(ref connections);
            var client = new TcpClient();
            await client.ConnectAsync(EndPoint, cancellation);
            return client.GetStream();
        }
    }

    private sealed class RelayHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private Task running = Task.CompletedTask;

        private RelayHarness(FakeUpstream upstream) => Upstream = upstream;

        public FakeUpstream Upstream { get; }

        public Relay Relay { get; private set; } = null!;

        public ConcurrentQueue<string> Log { get; } = new();

        public static async Task<RelayHarness> StartAsync(
            IPAddress? client = null,
            TimeSpan? headTimeout = null,
            TimeSpan? idleTimeout = null,
            long maxBytesDown = 16 * 1024 * 1024,
            int maxTunnels = 8,
            bool unreachable = false,
            bool echo = true)
        {
            var harness = new RelayHarness(new FakeUpstream(echo));
            var options = new RelayOptions
            {
                Listen = new IPEndPoint(IPAddress.Loopback, 0),
                Client = client ?? IPAddress.Loopback,
                HeadTimeout = headTimeout ?? Wait,
                IdleTimeout = idleTimeout ?? Wait,
                MaxBytesDown = maxBytesDown,
                MaxTunnels = maxTunnels,
            };
            Func<CancellationToken, ValueTask<Stream>> connect = unreachable
                ? _ => ValueTask.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused))
                : harness.Upstream.ConnectAsync;
            harness.Relay = new Relay(options, harness.Log.Enqueue, connect);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.running = harness.Relay.RunAsync(started.SetResult, harness.stop.Token);
            await started.Task.WaitAsync(Wait);
            return harness;
        }

        public async Task<TcpClient> ConnectAsync()
        {
            var client = new TcpClient();
            await client.ConnectAsync(Relay.LocalEndPoint!);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await running.WaitAsync(Wait);
            stop.Dispose();
            await Upstream.DisposeAsync();
        }
    }
}
