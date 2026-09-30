using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol;
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
    private static readonly Version PluginVersion = new(0, 1, 7);
    private static readonly DeploymentName Example = DeploymentName.Parse("plates.example.com");

    [Fact]
    public async Task APublishUnderARefusedChallenge_IsRetriedOnce()
    {
        using var server = new TestServer();
        using var binding = NewClient(server);
        using var key = EcdsaPersonaSigner.CreateEphemeral();
        var profile = await BindAsync(server, binding, key, Aria, "Aria Starfall");

        // The publish's first challenge is one the server never issued: the server refuses it as
        // soon as it reads the proof, before the body, and the client publishes again under the
        // fresh one.
        using var handler = new SwapFirstChallenge(server.Server.CreateHandler());
        using var client = new SharingClient(DeploymentName.Parse(TestServer.Deployment), handler, disposeHandler: false, PluginVersion);
        var png = Plates.Png(4, 3);
        var published = await client.PublishAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Aria's Plate", png), key), [png], key, default);
        Assert.Equal(HttpStatusCode.NoContent, published.Status);
        Assert.Equal(1, handler.Swapped);
    }

    [Fact]
    public async Task AServerThatAlwaysRefusesTheChallenge_GetsOneRetryOnly()
    {
        var server = new Scripted((request, _) => request.RequestUri!.AbsolutePath == "/v1/challenge"
            ? (HttpStatusCode.OK, RequestChallenge.NewRandom().ToArray())
            : (HttpStatusCode.Conflict, RequestChallenge.NewRandom().ToArray()));
        using var client = new SharingClient(Example, server, disposeHandler: true, PluginVersion);
        using var key = EcdsaPersonaSigner.CreateEphemeral();

        var answer = await client.ActionAsync(RequestProofKind.LodestoneCode, Json("{}"), key, default);
        Assert.Equal(HttpStatusCode.Conflict, answer.Status);
        Assert.Equal(["/v1/challenge", "/v1/lodestone/code", "/v1/lodestone/code"], server.Paths);
    }

    [Fact]
    public async Task AChallengeIsOnlyEverTakenFromASuccess()
    {
        var server = new Scripted((_, _) => (HttpStatusCode.Conflict, RequestChallenge.NewRandom().ToArray()));
        using var client = new SharingClient(Example, server, disposeHandler: true, PluginVersion);
        await Assert.ThrowsAsync<SharingException>(() => client.ChallengeAsync(default));
    }

    [Fact]
    public async Task AnAnswerWithNoLength_IsBoundedWhileItIsRead()
    {
        long? declared = -1;
        var server = new Scripted((_, _) => (HttpStatusCode.OK, null), (_, response) =>
        {
            response.Content = new StreamContent(new Unseekable(new byte[5000]));
            declared = response.Content.Headers.ContentLength;
        });
        using var client = new SharingClient(Example, server, disposeHandler: true, PluginVersion);

        var failure = await Assert.ThrowsAsync<SharingException>(() => client.StatusAsync(default));
        Assert.Equal(HttpStatusCode.OK, failure.Status);
        Assert.Null(declared);
    }

    [Fact]
    public async Task ASlowDripAnswer_IsCutOffAtTheTimeout()
    {
        var server = new Scripted((_, _) => (HttpStatusCode.OK, null), (_, response) => response.Content = new StreamContent(new Dripping()));
        using var client = new SharingClient(Example, server, disposeHandler: true, PluginVersion) { RequestTimeout = TimeSpan.FromMilliseconds(300) };

        var failure = await Assert.ThrowsAsync<SharingException>(() => client.StatusAsync(default));
        Assert.Contains("in time", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallersCancellation_IsACancellation_NotATimeout()
    {
        using var client = new SharingClient(Example, new Silent(), disposeHandler: true, PluginVersion);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.StatusAsync(cancel.Token));
        Assert.Equal(cancel.Token, cancelled.CancellationToken);
    }

    [Fact]
    public async Task EveryRequest_NamesThePluginsVersion_AndABodyOverItsLimitSpendsNoChallenge()
    {
        var server = new Scripted((_, _) => (HttpStatusCode.OK, Json("{}")));
        using var client = new SharingClient(Example, server, disposeHandler: true, PluginVersion);
        await client.StatusAsync(default);
        Assert.Equal("AetherFrame/0.1.7", Assert.Single(server.UserAgents));

        using var key = EcdsaPersonaSigner.CreateEphemeral();
        await Assert.ThrowsAsync<ArgumentException>(() => client.ActionAsync(RequestProofKind.Report, new byte[4097], key, default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.PublishAsync(new byte[ProtocolLimits.MaxDocumentBytes + 1], [], key, default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.PublishAsync([1], new byte[9][], key, default));
        var tooMuch = Enumerable.Range(0, 6).Select(_ => new byte[SharingClient.MaxImageBytes]).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => client.PublishAsync([1], tooMuch, key, default));
        Assert.Single(server.Paths);
    }

    [Fact]
    public async Task APublishBody_IsTheInterfacesLayout_WithItsLength()
    {
        byte[] proof = [1, 2, 3];
        byte[] document = [4, 5];
        byte[][] images = [[6], [7, 8, 9]];
        using var content = SharingClient.PublishContent.Create(proof, document, images);

        Assert.Equal(2 + 3 + 4 + 2 + 1 + (4 + 1) + (4 + 3), content.Headers.ContentLength);
        byte[] expected = [0, 3, 1, 2, 3, 0, 0, 0, 2, 4, 5, 2, 0, 0, 0, 1, 6, 0, 0, 0, 3, 7, 8, 9];
        // Copied twice, unbuffered, as a retried send would: the stream seeks back to its start.
        for (var copy = 0; copy < 2; copy++)
        {
            using var sent = new System.IO.MemoryStream();
            await content.CopyToAsync(sent);
            Assert.Equal(expected, sent.ToArray());
        }
    }

    [Fact]
    public void EachKind_IsReadToItsOwnBound()
    {
        Assert.Equal(SharingClient.MaxServedProfileBytes, SharingClient.AnswerBoundOf(RequestProofKind.Lookup));
        Assert.Equal(SharingClient.MaxImageBytes, SharingClient.AnswerBoundOf(RequestProofKind.Image));
        foreach (var kind in new[] { RequestProofKind.LodestoneCode, RequestProofKind.LodestoneCheck, RequestProofKind.LodestoneReread, RequestProofKind.OptOut, RequestProofKind.Report })
        {
            Assert.Equal(SharingClient.MaxJsonAnswerBytes, SharingClient.AnswerBoundOf(kind));
        }
    }

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
        var lookup = await bram.ActionAsync(RequestProofKind.Lookup, Json("{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}"), bramKey, default);
        Assert.Equal(HttpStatusCode.OK, lookup.Status);
        var served = ServedProfile.Read(lookup.Body);
        Assert.Equal("Aria's Plate", served.Name);

        var image = await bram.ActionAsync(RequestProofKind.Image, Json($"{{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"marker\":\"{served.Marker}\",\"index\":0}}"), bramKey, default);
        Assert.Equal(HttpStatusCode.OK, image.Status);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(png, image.Body);

        // Report it, then pause and opt out from Aria's side.
        var report = await bram.ActionAsync(RequestProofKind.Report, Json("{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"reason\":\"other\"}"), bramKey, default);
        Assert.Equal(HttpStatusCode.NoContent, report.Status);
        var paused = await aria.ActionAsync(RequestProofKind.OptOut, Json("{\"mode\":\"pause\"}"), ariaKey, default);
        Assert.Equal(HttpStatusCode.NoContent, paused.Status);
        var gone = await bram.ActionAsync(RequestProofKind.Lookup, Json("{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}"), bramKey, default);
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);
    }

    [Fact]
    public async Task ARefusedChallenge_IsRetriedOnceUnderTheFreshOne()
    {
        using var server = new TestServer();
        using var handler = new SwapFirstChallenge(server.Server.CreateHandler());
        using var client = new SharingClient(DeploymentName.Parse(TestServer.Deployment), handler, disposeHandler: false, PluginVersion);
        using var key = EcdsaPersonaSigner.CreateEphemeral();

        // The first challenge the client sees is one the server never issued: the server refuses it
        // with a fresh one, and the client signs again under that.
        var code = await client.ActionAsync(RequestProofKind.LodestoneCode, Json("{}"), key, default);
        Assert.Equal(HttpStatusCode.OK, code.Status);
        Assert.Equal(1, handler.Swapped);
    }

    [Fact]
    public async Task AnAnswerOverItsBound_OrNoAnswer_IsASharingException()
    {
        using (var tooLong = new SharingClient(DeploymentName.Parse("plates.example.com"), new Answering(HttpStatusCode.OK, new byte[5000]), disposeHandler: true, PluginVersion))
        {
            var failure = await Assert.ThrowsAsync<SharingException>(() => tooLong.StatusAsync(default));
            Assert.Equal(HttpStatusCode.OK, failure.Status);
        }

        using (var unreachable = new SharingClient(DeploymentName.Parse("plates.example.com"), new Failing(), disposeHandler: true, PluginVersion))
        {
            var failure = await Assert.ThrowsAsync<SharingException>(() => unreachable.StatusAsync(default));
            Assert.Null(failure.Status);
        }

        using (var broken = new SharingClient(DeploymentName.Parse("plates.example.com"), new Broken(), disposeHandler: true, PluginVersion))
        {
            var failure = await Assert.ThrowsAsync<SharingException>(() => broken.StatusAsync(default));
            Assert.Null(failure.Status);
        }

        using var silent = new SharingClient(DeploymentName.Parse("plates.example.com"), new Silent(), disposeHandler: true, PluginVersion) { RequestTimeout = TimeSpan.FromMilliseconds(200) };
        var timeout = await Assert.ThrowsAsync<SharingException>(() => silent.StatusAsync(default));
        Assert.Contains("in time", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClient_AlwaysAsksItsOwnDeployment_OverHttps()
    {
        var recording = new Answering(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{}"));
        using var client = new SharingClient(DeploymentName.Parse("plates.example.com"), recording, disposeHandler: true, PluginVersion);
        await client.StatusAsync(default);
        Assert.Equal(new Uri("https://plates.example.com/v1/status"), recording.LastUri);
        Assert.Equal(["v1/lodestone/code", "v1/lodestone/check", "v1/lodestone/reread", "v1/opt-out", "v1/lookup", "v1/image", "v1/report"],
            new[] { RequestProofKind.LodestoneCode, RequestProofKind.LodestoneCheck, RequestProofKind.LodestoneReread, RequestProofKind.OptOut, RequestProofKind.Lookup, RequestProofKind.Image, RequestProofKind.Report }.Select(SharingClient.PathOf));
        Assert.Throws<ArgumentOutOfRangeException>(() => SharingClient.PathOf(RequestProofKind.DocumentSubmission));
    }

    private static SharingClient NewClient(TestServer server) =>
        new(DeploymentName.Parse(TestServer.Deployment), server.Server.CreateHandler(), disposeHandler: true, PluginVersion);

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<ProfileId> BindAsync(TestServer server, SharingClient client, EcdsaPersonaSigner key, long lodestoneId, string name)
    {
        var codeAnswer = await client.ActionAsync(RequestProofKind.LodestoneCode, Json("{}"), key, default);
        var code = JsonDocument.Parse(codeAnswer.Body).RootElement.GetProperty("code").GetString()!;
        server.Lodestone.Pages[lodestoneId] = LodestoneHtml.Character(name, "Gilgamesh", "AetherFrame " + code);
        var check = await client.ActionAsync(RequestProofKind.LodestoneCheck, Json($"{{\"lodestoneId\":\"{lodestoneId}\",\"code\":\"{code}\",\"name\":\"{name}\",\"world\":\"Gilgamesh\"}}"), key, default);
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

    /// <summary>Answers each request as a script says, recording its path and User-Agent; <c>shape</c> may replace the answer's content.</summary>
    private sealed class Scripted(Func<HttpRequestMessage, int, (HttpStatusCode Status, byte[]? Body)> answer, Action<HttpRequestMessage, HttpResponseMessage>? shape = null) : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();

        public List<string> UserAgents { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            var (status, body) = answer(request, Paths.Count);
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) };
            shape?.Invoke(request, response);
            return Task.FromResult(response);
        }
    }

    /// <summary>A body that can't tell its length.</summary>
    private sealed class Unseekable(byte[] bytes) : System.IO.Stream
    {
        private readonly System.IO.MemoryStream inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that sends one byte every 100 milliseconds, forever.</summary>
    private sealed class Dripping : System.IO.Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(100, cancellationToken);
            buffer.Span[0] = 1;
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
