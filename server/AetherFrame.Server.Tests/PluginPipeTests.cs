using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Lodestone;
using AetherFrame.Services.Network.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>
/// The plugin's own pipe (<see cref="LodestonePipe"/>, through <see cref="SharingClient"/>) against
/// the server as deployed, on Kestrel: the check and the re-read through the player's own
/// connection ("Checking a character through the player's own connection" in the decision
/// register; ServerApi-v1.md, section 2.3), end to end. The plugin's requests for the deployment
/// are sent to the local Kestrel, and its connection to the Lodestone reaches the TLS stand-in on
/// loopback, reporting a global address as the real one would. Nothing reaches the real Lodestone.
/// </summary>
public sealed class PluginPipeTests
{
    private const long Aria = 12345678;
    private static readonly Version PluginVersion = new(0, 1, 9);

    [Fact]
    public async Task ThePluginsPipe_Checks_Rereads_AndExplainsTheLodestonesRefusal()
    {
        using var server = new TestServer();
        server.UseKestrel(0);
        server.Services.GetRequiredService<PipedPages>().TrustForTests = TestAuthority.Trust();
        var origin = new Uri(server.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First());
        await using var lodestone = new TlsLodestone();
        using var player = new Player(server, new HttpClient { BaseAddress = origin });
        var code = await player.CodeAsync();
        using var handler = new ToKestrel(origin);
        var links = 0;
        using var client = new SharingClient(DeploymentName.Parse(TestServer.Deployment), handler, disposeHandler: false, PluginVersion, () =>
        {
            Interlocked.Increment(ref links);
            return new LoopbackLink(lodestone.EndPoint);
        });

        // The check: the page is read through the plugin's pipe, and the binding's day comes back.
        lodestone.Answer = _ => TlsLodestone.Chunked(200, LodestoneHtml.Character("Aria Starfall", "Gilgamesh", "Hello! " + code).Html!);
        var check = await client.PipedActionAsync(RequestProofKind.LodestoneCheck, Encoding.UTF8.GetBytes($"{{\"lodestoneId\":\"{Aria}\",\"code\":\"{code}\",\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}}"), player.Key, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, check.Status);
        using (var body = JsonDocument.Parse(check.Body))
        {
            Assert.Equal("Aria Starfall", body.RootElement.GetProperty("name").GetString());
            Assert.Equal(3, body.RootElement.EnumerateObject().Count());
        }

        Assert.Equal(Today(server), check.ReadDay);
        Assert.Equal(1, links);
        Assert.StartsWith("GET /lodestone/character/12345678/ HTTP/1.1\r\n", Assert.Single(lodestone.Requests), StringComparison.Ordinal);
        Assert.Empty(server.Lodestone.Fetched);

        // The re-read follows a rename through a pipe of its own.
        lodestone.Answer = _ => TlsLodestone.Framed(200, LodestoneHtml.Character("Aria Moonfall", "Gilgamesh", "").Html!);
        var reread = await client.PipedActionAsync(RequestProofKind.LodestoneReread, "{}"u8.ToArray(), player.Key, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, reread.Status);
        using (var body = JsonDocument.Parse(reread.Body))
        {
            Assert.Equal("Aria Moonfall", body.RootElement.GetProperty("name").GetString());
        }

        Assert.Equal(2, links);

        // The Lodestone turning the player's connection away is told apart from "try again later".
        lodestone.Answer = _ => TlsLodestone.Framed(403, "");
        var refused = await client.PipedActionAsync(RequestProofKind.LodestoneReread, "{}"u8.ToArray(), player.Key, CancellationToken.None);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.Status);
        Assert.True(refused.LodestoneRefused);
        Assert.Null(refused.ReadDay);
        Assert.Equal(3, links);
    }

    private static long Today(TestServer server) => server.Time.Now.ToUnixTimeSeconds() / 86_400;

    /// <summary>
    /// Sends the plugin's requests for the deployment to the local Kestrel instead: <c>https</c> as
    /// <c>http</c> and <c>wss</c> as <c>ws</c>, the path unchanged, through a handler with R2's
    /// settings.
    /// </summary>
    private sealed class ToKestrel(Uri origin) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(TestServer.Deployment, request.RequestUri!.Host);
            Assert.Contains(request.RequestUri.Scheme, new[] { "https", "wss" });
            Assert.False(request.Headers.Contains("Origin"));
            request.RequestUri = new UriBuilder(request.RequestUri)
            {
                Scheme = request.RequestUri.Scheme == "wss" ? "ws" : "http",
                Host = origin.Host,
                Port = origin.Port,
            }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>The pipe's connection, to the TLS stand-in on loopback, reporting a global address as a connection to the real Lodestone would.</summary>
    private sealed class LoopbackLink(IPEndPoint target) : LodestonePipe.Link
    {
        private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        internal override async Task<IPAddress?> ConnectAsync(CancellationToken cancellation)
        {
            await socket.ConnectAsync(target, cancellation);
            return IPAddress.Parse("104.18.32.1");
        }

        internal override async ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
        {
            while (!bytes.IsEmpty)
            {
                bytes = bytes[await socket.SendAsync(bytes, SocketFlags.None, cancellation)..];
            }
        }

        internal override ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellation) =>
            socket.ReceiveAsync(buffer, SocketFlags.None, cancellation);

        public override void Dispose() => socket.Dispose();
    }
}
