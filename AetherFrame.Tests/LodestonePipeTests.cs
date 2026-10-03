using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Requests;
using AetherFrame.Services.Network.Transport;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Lodestone pipe on its own ("Checking a character through the player's own connection" in
/// the decision register; ServerApi-v1.md, section 2.3): the addresses it carries bytes to, the
/// exchange's order and limits, and how strictly the final answer is read. The server is a
/// WebSocket answered in memory, and the Lodestone a stand-in that never connects anywhere.
/// </summary>
public class LodestonePipeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly DeploymentName Deployment = DeploymentName.Parse("plates.example.com");
    private static readonly byte[] Hello = [0x16, 0x03, 0x01, 0x00, 0x02, 0x01, 0x00];
    private static readonly byte[] Body = [0x00, 0x01, 0x2A, 0x7B, 0x7D];

    [Theory]
    [InlineData("104.18.32.1", true)]
    [InlineData("23.45.67.89", true)]
    [InlineData("2606:4700::6812:2001", true)]
    [InlineData("::ffff:104.18.32.1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("0.1.2.3", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("192.0.0.8", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2001:0:4136:e378::1", false)]
    [InlineData("2002:a00:1::1", false)]
    [InlineData("3fff::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    public void OnlyGlobalUnicastAddressesOnTheAllowedList_Pass(string address, bool allowed)
    {
        Assert.Equal(allowed, LodestonePipe.Allows(IPAddress.Parse(address)));
    }

    [Fact]
    public void NoAddress_DoesntPass() => Assert.False(LodestonePipe.Allows(null));

    [Fact]
    public async Task AnAnswerThatNeedsNoPage_IsTheFinalMessage_AndTheSignedBodyIsTheFirst()
    {
        using var server = new PipeServer(async socket =>
        {
            var (type, bytes) = await WebSocketStandIn.ReceiveAsync(socket);
            Assert.Equal(WebSocketMessageType.Binary, type);
            Assert.Equal(Body, bytes);
            await WebSocketStandIn.FinishAsync(socket, "{\"status\":200,\"body\":{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"},\"readDay\":20729}");
        });

        var answer = await server.Pipe.RunAsync(RequestProofKind.LodestoneReread, Body, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal("{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}", Encoding.UTF8.GetString(answer.Body));
        Assert.Equal(20729, answer.ReadDay);
        Assert.False(answer.LodestoneRefused);
        server.AssertNoFaults();
        Assert.Equal("/v1/lodestone/reread", server.Path);
        Assert.Equal("AetherFrame/0.1.7", server.UserAgent);
        Assert.False(server.Origin);
        Assert.Empty(server.Links);
    }

    [Fact]
    public async Task ARefusedAddress_IsAnsweredFailed_AndCarriesNothing()
    {
        using var server = new PipeServer(async socket =>
        {
            await WebSocketStandIn.ReceiveAsync(socket);
            await WebSocketStandIn.SendTextAsync(socket, "open");
            await WebSocketStandIn.ExpectTextAsync(socket, "failed");
            await WebSocketStandIn.SendTextAsync(socket, "close");
            await WebSocketStandIn.FinishAsync(socket, "{\"status\":503}");
        }, () => new FakeLodestoneLink(IPAddress.Parse("::ffff:192.168.0.10")));

        var answer = await server.Pipe.RunAsync(RequestProofKind.LodestoneCheck, Body, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.Status);
        server.AssertNoFaults();
        var link = Assert.Single(server.Links);
        Assert.True(link.Connected);
        Assert.True(link.Disposed);
        Assert.Empty(link.Received);
    }

    [Fact]
    public async Task AConnectionThatDoesntComeInTime_IsAnsweredFailed()
    {
        var never = new TaskCompletionSource();
        using var server = new PipeServer(async socket =>
        {
            await WebSocketStandIn.ReceiveAsync(socket);
            await WebSocketStandIn.SendTextAsync(socket, "open");
            await WebSocketStandIn.ExpectTextAsync(socket, "failed");
            await WebSocketStandIn.SendTextAsync(socket, "close");
            await WebSocketStandIn.FinishAsync(socket, "{\"status\":503}");
        }, () => new FakeLodestoneLink(IPAddress.Parse("104.18.32.1")) { ConnectGate = never.Task }, connectTimeout: TimeSpan.FromMilliseconds(200));

        var answer = await server.Pipe.RunAsync(RequestProofKind.LodestoneCheck, Body, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.Status);
        server.AssertNoFaults();
    }

    [Fact]
    public async Task FirstBytesThatArentATlsHandshake_EndTheExchange_AndReachNothing()
    {
        using var server = new PipeServer(async socket =>
        {
            await WebSocketStandIn.ReceiveAsync(socket);
            await WebSocketStandIn.SendTextAsync(socket, "open");
            await WebSocketStandIn.ExpectTextAsync(socket, "opened");
            await WebSocketStandIn.SendBinaryAsync(socket, "GET / HTTP/1.1\r\n\r\n"u8.ToArray());
            await WebSocketStandIn.ReceiveAsync(socket);
        });

        await Assert.ThrowsAsync<SharingException>(() => server.Pipe.RunAsync(RequestProofKind.LodestoneCheck, Body, CancellationToken.None));

        var link = Assert.Single(server.Links);
        Assert.Empty(link.Received);
        Assert.True(link.Disposed);
    }

    [Fact]
    public async Task BytesBeforeThePipeOpens_EndTheExchange()
    {
        using var server = new PipeServer(async socket =>
        {
            await WebSocketStandIn.ReceiveAsync(socket);
            await WebSocketStandIn.SendBinaryAsync(socket, Hello);
            await WebSocketStandIn.ReceiveAsync(socket);
        });

        await Assert.ThrowsAsync<SharingException>(() => server.Pipe.RunAsync(RequestProofKind.LodestoneCheck, Body, CancellationToken.None));
        Assert.Empty(server.Links);
    }

    [Fact]
    public async Task ASecondOpen_EndsTheExchange()
    {
        using var server = new PipeServer(async socket =>
        {
            await WebSocketStandIn.ReceiveAsync(socket);
            await WebSocketStandIn.SendTextAsync(socket, "open");
            await WebSocketStandIn.ExpectTextAsync(socket, "opened");
            await WebSocketStandIn.SendTextAsync(socket, "open");
            await WebSocketStandIn.ReceiveAsync(socket);
        });

        await Assert.ThrowsAsync<SharingException>(() => server.Pipe.RunAsync(RequestProofKind.LodestoneCheck, Body, CancellationToken.None));
        Assert.Single(server.Links);
    }

    [Fact]
    public async Task MoreThan16KiBTowardTheLodestone_EndsTheExchange()
    {
        using var server = new PipeServer(async socket =>
        {
            await WebSocketStandIn.ReceiveAsync(socket);
            await WebSocketStandIn.SendTextAsync(socket, "open");
            await WebSocketStandIn.ExpectTextAsync(socket, "opened");
            var first = new byte[LodestonePipe.MaxBytesToLodestone];
            Hello.CopyTo(first, 0);
            await WebSocketStandIn.SendBinaryAsync(socket, first);
            await WebSocketStandIn.SendBinaryAsync(socket, [1]);
            await WebSocketStandIn.ReceiveAsync(socket);
        });

        await Assert.ThrowsAsync<SharingException>(() => server.Pipe.RunAsync(RequestProofKind.LodestoneCheck, Body, CancellationToken.None));
        Assert.Equal(LodestonePipe.MaxBytesToLodestone, Assert.Single(server.Links).Received.Length);
    }

    [Fact]
    public async Task MoreThan2MiBFromTheLodestone_EndsTheExchange()
    {
        var carried = 0L;
        using var server = new PipeServer(async socket =>
        {
            await WebSocketStandIn.ReceiveAsync(socket);
            await WebSocketStandIn.SendTextAsync(socket, "open");
            await WebSocketStandIn.ExpectTextAsync(socket, "opened");
            while (true)
            {
                var (type, bytes) = await WebSocketStandIn.ReceiveAsync(socket);
                if (type != WebSocketMessageType.Binary)
                {
                    return;
                }

                carried += bytes.Length;
            }
        }, () =>
        {
            var link = new FakeLodestoneLink(IPAddress.Parse("104.18.32.1"));
            for (var piece = 0; piece <= LodestonePipe.MaxBytesFromLodestone / (16 * 1024); piece++)
            {
                link.Answer(new byte[16 * 1024]);
            }

            return link;
        });

        await Assert.ThrowsAsync<SharingException>(() => server.Pipe.RunAsync(RequestProofKind.LodestoneCheck, Body, CancellationToken.None));
        await server.Served;
        Assert.True(carried <= LodestonePipe.MaxBytesFromLodestone, "the pipe never carries more than 2 MiB from the Lodestone");
    }

    [Fact]
    public async Task AServerThatEndsWithoutAFinalMessage_GivesNoAnswer()
    {
        using var server = new PipeServer(async socket =>
        {
            await WebSocketStandIn.ReceiveAsync(socket);
            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, null, CancellationToken.None);
        });

        await Assert.ThrowsAsync<SharingException>(() => server.Pipe.RunAsync(RequestProofKind.LodestoneCheck, Body, CancellationToken.None));
    }

    [Fact]
    public async Task AnExchangeThatRunsTooLong_Ends()
    {
        using var server = new PipeServer(async socket =>
        {
            await WebSocketStandIn.ReceiveAsync(socket);
            await WebSocketStandIn.ReceiveAsync(socket);
        }, exchangeTimeout: TimeSpan.FromMilliseconds(300));

        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<SharingException>(() => server.Pipe.RunAsync(RequestProofKind.LodestoneCheck, Body, CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < Patience);
    }

    [Fact]
    public async Task TheCallersStop_IsACancellation()
    {
        using var server = new PipeServer(async socket =>
        {
            await WebSocketStandIn.ReceiveAsync(socket);
            await WebSocketStandIn.ReceiveAsync(socket);
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.Pipe.RunAsync(RequestProofKind.LodestoneCheck, Body, stop.Token));
    }

    [Fact]
    public async Task OnlyTheCheckAndTheRereadGoThroughThePipe()
    {
        using var server = new PipeServer(_ => Task.CompletedTask);
        foreach (var kind in Enum.GetValues<RequestProofKind>().Where(kind => kind is not (RequestProofKind.LodestoneCheck or RequestProofKind.LodestoneReread)))
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => server.Pipe.RunAsync(kind, Body, CancellationToken.None));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => server.Pipe.RunAsync(RequestProofKind.LodestoneCheck, new byte[4553], CancellationToken.None));
        Assert.Null(server.Path);
    }

    [Theory]
    [InlineData("{\"status\":422}", 422, "", null, null)]
    [InlineData("{\"status\":503,\"reason\":\"lodestone:refused\"}", 503, "", "lodestone:refused", null)]
    [InlineData("{\"status\":200,\"body\":{\"name\":\"A B\",\"world\":\"C\"},\"readDay\":0}", 200, "{\"name\":\"A B\",\"world\":\"C\"}", null, 0L)]
    [InlineData("{\"status\":200,\"body\":{\"name\":\"A B\",\"world\":\"C\"}}", 200, "{\"name\":\"A B\",\"world\":\"C\"}", null, null)]
    public void TheFinalMessage_IsReadAsItsStatusBodyReasonAndDay(string final, int status, string body, string? reason, long? readDay)
    {
        var answer = LodestonePipe.ReadFinal(Encoding.UTF8.GetBytes(final));
        Assert.Equal((HttpStatusCode)status, answer.Status);
        Assert.Equal(body, Encoding.UTF8.GetString(answer.Body));
        Assert.Equal(reason, answer.Reason);
        Assert.Equal(readDay, answer.ReadDay);
    }

    [Fact]
    public void AConflictsFinalMessage_CarriesItsFreshChallenge_AsTheBody()
    {
        var challenge = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var answer = LodestonePipe.ReadFinal(Encoding.UTF8.GetBytes("{\"status\":409,\"challenge\":\"" + Convert.ToBase64String(challenge) + "\"}"));
        Assert.Equal(HttpStatusCode.Conflict, answer.Status);
        Assert.Equal(challenge, answer.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"status\":\"200\"}")]
    [InlineData("{\"status\":99}")]
    [InlineData("{\"status\":200,\"status\":200}")]
    [InlineData("{\"status\":200,\"extra\":1}")]
    [InlineData("{\"status\":200,\"readDay\":20729}")]
    [InlineData("{\"status\":422,\"body\":{},\"readDay\":20729}")]
    [InlineData("{\"status\":200,\"body\":{},\"readDay\":-1}")]
    [InlineData("{\"status\":200,\"body\":[]}")]
    [InlineData("{\"status\":422,\"reason\":\"lodestone:refused\"}")]
    [InlineData("{\"status\":503,\"reason\":\"other\"}")]
    [InlineData("{\"status\":409,\"challenge\":\"AAAA\"}")]
    [InlineData("{\"status\":200,\"challenge\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\"}")]
    [InlineData("{\"status\":409,\"challenge\":\"not base64!\"}")]
    [InlineData("{\"status\":200} trailing")]
    public void AFinalMessageThatIsntOne_IsRefused(string final)
    {
        Assert.Throws<SharingException>(() => LodestonePipe.ReadFinal(Encoding.UTF8.GetBytes(final)));
    }

    /// <summary>A handler that accepts the pipe's WebSocket and serves it with a test's script, and the pipe over it.</summary>
    private sealed class PipeServer : HttpMessageHandler
    {
        private readonly Func<WebSocket, Task> serve;
        private readonly ConcurrentQueue<Exception> faults = new();
        private readonly TaskCompletionSource served = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PipeServer(Func<WebSocket, Task> serve, Func<FakeLodestoneLink>? links = null, TimeSpan? connectTimeout = null, TimeSpan? exchangeTimeout = null)
        {
            this.serve = serve;
            var make = links ?? (() => new FakeLodestoneLink(IPAddress.Parse("104.18.32.1")));
            Pipe = new LodestonePipe(Deployment, this, new Version(0, 1, 7), () =>
            {
                var link = make();
                Links.Add(link);
                return link;
            })
            {
                ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(8),
                ExchangeTimeout = exchangeTimeout ?? Patience,
            };
        }

        internal LodestonePipe Pipe { get; }

        internal ConcurrentBag<FakeLodestoneLink> Links { get; } = new();

        internal string? Path { get; private set; }

        internal string? UserAgent { get; private set; }

        internal bool Origin { get; private set; }

        /// <summary>Completes once the test's script has run to its end, whatever it threw.</summary>
        internal Task Served => served.Task;

        internal void AssertNoFaults() => Assert.True(faults.IsEmpty, string.Join("; ", faults));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(WebSocketStandIn.IsUpgrade(request));
            Assert.Equal("wss", request.RequestUri!.Scheme);
            Assert.Equal(Deployment.Value, request.RequestUri.Host);
            Path = request.RequestUri.AbsolutePath;
            UserAgent = request.Headers.UserAgent.ToString();
            Origin = request.Headers.Contains("Origin");
            Assert.False(request.Headers.Contains("Sec-WebSocket-Extensions"), "compression stays off");
            return WebSocketStandIn.AcceptAsync(request, async socket =>
            {
                try
                {
                    await serve(socket);
                }
                finally
                {
                    served.TrySetResult();
                }
            }, faults);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Pipe.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
