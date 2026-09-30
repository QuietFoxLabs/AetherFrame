using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Requests;
using AetherFrame.Protocol.Signing;
using AetherFrame.Services.Network.Transport;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>
/// The plugin's client (N2-9a) against the real server in memory: every request it makes, signed
/// as the plugin will sign them, and what it does with answers it can't use.
/// </summary>
public class SharingClientTests
{
    private const long Aria = 12345678;
    private const long Bram = 23456789;

    [Fact]
    public async Task TheClient_BindsPublishesAndViews_EndToEnd()
    {
        using var server = new TestServer();
        using var aria = NewClient(server);
        using var bram = NewClient(server);
        using var ariaKey = EcdsaPersonaSigner.CreateEphemeral();
        using var bramKey = EcdsaPersonaSigner.CreateEphemeral();

        var status = await aria.StatusAsync(default);
        Assert.Equal(HttpStatusCode.OK, status.Status);
        Assert.Equal(32769, JsonDocument.Parse(status.Body).RootElement.GetProperty("protocolVersion").GetInt32());

        var ariaProfile = await BindAsync(server, aria, ariaKey, Aria, "Aria Starfall");
        await BindAsync(server, bram, bramKey, Bram, "Bram Oakes");

        // Publish a Plate with an image, as the plugin will.
        var png = Plates.Png(4, 3);
        var snapshot = Plates.Snapshot(ariaProfile, "Aria's Plate", png);
        var published = await aria.PublishAsync(SignedDocumentCodec.Sign(snapshot, ariaKey), [png], ariaKey, default);
        Assert.Equal(HttpStatusCode.NoContent, published.Status);

        // View it from the other character, by name and World, then its image by marker.
        var lookup = await bram.ActionAsync(RequestProofKind.Lookup, Json("{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}"), bramKey, SharingClient.MaxServedProfileBytes, default);
        Assert.Equal(HttpStatusCode.OK, lookup.Status);
        var served = ServedProfile.Read(lookup.Body);
        Assert.Equal("Aria's Plate", served.Name);

        var image = await bram.ActionAsync(RequestProofKind.Image, Json($"{{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"marker\":\"{served.Marker}\",\"index\":0}}"), bramKey, SharingClient.MaxImageBytes, default);
        Assert.Equal(HttpStatusCode.OK, image.Status);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(png, image.Body);

        // Report it, then pause and opt out from Aria's side.
        var report = await bram.ActionAsync(RequestProofKind.Report, Json("{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"reason\":\"other\"}"), bramKey, SharingClient.MaxJsonAnswerBytes, default);
        Assert.Equal(HttpStatusCode.NoContent, report.Status);
        var paused = await aria.ActionAsync(RequestProofKind.OptOut, Json("{\"mode\":\"pause\"}"), ariaKey, SharingClient.MaxJsonAnswerBytes, default);
        Assert.Equal(HttpStatusCode.NoContent, paused.Status);
        var gone = await bram.ActionAsync(RequestProofKind.Lookup, Json("{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}"), bramKey, SharingClient.MaxServedProfileBytes, default);
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);
    }

    [Fact]
    public async Task ARefusedChallenge_IsRetriedOnceUnderTheFreshOne()
    {
        using var server = new TestServer();
        using var handler = new SwapFirstChallenge(server.Server.CreateHandler());
        using var client = new SharingClient(DeploymentName.Parse(TestServer.Deployment), handler, disposeHandler: false);
        using var key = EcdsaPersonaSigner.CreateEphemeral();

        // The first challenge the client sees is one the server never issued: the server refuses it
        // with a fresh one, and the client signs again under that.
        var code = await client.ActionAsync(RequestProofKind.LodestoneCode, Json("{}"), key, SharingClient.MaxJsonAnswerBytes, default);
        Assert.Equal(HttpStatusCode.OK, code.Status);
        Assert.Equal(1, handler.Swapped);
    }

    [Fact]
    public async Task AnAnswerOverItsBound_OrNoAnswer_IsASharingException()
    {
        using (var tooLong = new SharingClient(DeploymentName.Parse("plates.example.com"), new Answering(HttpStatusCode.OK, new byte[5000]), disposeHandler: true))
        {
            var failure = await Assert.ThrowsAsync<SharingException>(() => tooLong.StatusAsync(default));
            Assert.Equal(HttpStatusCode.OK, failure.Status);
        }

        using (var unreachable = new SharingClient(DeploymentName.Parse("plates.example.com"), new Failing(), disposeHandler: true))
        {
            var failure = await Assert.ThrowsAsync<SharingException>(() => unreachable.StatusAsync(default));
            Assert.Null(failure.Status);
        }

        using (var broken = new SharingClient(DeploymentName.Parse("plates.example.com"), new Broken(), disposeHandler: true))
        {
            var failure = await Assert.ThrowsAsync<SharingException>(() => broken.StatusAsync(default));
            Assert.Null(failure.Status);
        }

        using var silent = new SharingClient(DeploymentName.Parse("plates.example.com"), new Silent(), disposeHandler: true) { RequestTimeout = TimeSpan.FromMilliseconds(200) };
        var timeout = await Assert.ThrowsAsync<SharingException>(() => silent.StatusAsync(default));
        Assert.Contains("in time", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClient_AlwaysAsksItsOwnDeployment_OverHttps()
    {
        var recording = new Answering(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{}"));
        using var client = new SharingClient(DeploymentName.Parse("plates.example.com"), recording, disposeHandler: true);
        await client.StatusAsync(default);
        Assert.Equal(new Uri("https://plates.example.com/v1/status"), recording.LastUri);
        Assert.Equal(["v1/lodestone/code", "v1/lodestone/check", "v1/lodestone/reread", "v1/opt-out", "v1/lookup", "v1/image", "v1/report"],
            new[] { RequestProofKind.LodestoneCode, RequestProofKind.LodestoneCheck, RequestProofKind.LodestoneReread, RequestProofKind.OptOut, RequestProofKind.Lookup, RequestProofKind.Image, RequestProofKind.Report }.Select(SharingClient.PathOf));
        Assert.Throws<ArgumentOutOfRangeException>(() => SharingClient.PathOf(RequestProofKind.DocumentSubmission));
    }

    private static SharingClient NewClient(TestServer server) =>
        new(DeploymentName.Parse(TestServer.Deployment), server.Server.CreateHandler(), disposeHandler: true);

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<ProfileId> BindAsync(TestServer server, SharingClient client, EcdsaPersonaSigner key, long lodestoneId, string name)
    {
        var codeAnswer = await client.ActionAsync(RequestProofKind.LodestoneCode, Json("{}"), key, SharingClient.MaxJsonAnswerBytes, default);
        var code = JsonDocument.Parse(codeAnswer.Body).RootElement.GetProperty("code").GetString()!;
        server.Lodestone.Pages[lodestoneId] = LodestoneHtml.Character(name, "Gilgamesh", "AetherFrame " + code);
        var check = await client.ActionAsync(RequestProofKind.LodestoneCheck, Json($"{{\"lodestoneId\":\"{lodestoneId}\",\"code\":\"{code}\"}}"), key, SharingClient.MaxJsonAnswerBytes, default);
        Assert.Equal(HttpStatusCode.OK, check.Status);
        return ProfileId.Parse(JsonDocument.Parse(check.Body).RootElement.GetProperty("profileId").GetString()!);
    }

    /// <summary>Replaces the first challenge the server issues with one it never issued.</summary>
    private sealed class SwapFirstChallenge(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Swapped { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (Swapped == 0 && request.RequestUri!.AbsolutePath == "/v1/challenge")
            {
                Swapped++;
                response.Content = new ByteArrayContent(RequestChallenge.NewRandom().ToArray());
            }

            return response;
        }
    }

    private sealed class Answering(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class Failing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
    }

    /// <summary>Answers, then drops the connection partway through the body.</summary>
    private sealed class Broken : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream()) });
    }

    private sealed class BreakingStream : System.IO.MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new System.IO.IOException("reset");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new System.IO.IOException("reset"));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(new System.IO.IOException("reset"));
    }

    private sealed class Silent : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
