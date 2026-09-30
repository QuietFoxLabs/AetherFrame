using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Requests;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Services.Network.Transport;

/// <summary>One answer from the sharing server: its status and, within the request's bound, its body.</summary>
internal sealed record SharingResponse(HttpStatusCode Status, byte[] Body, string? MediaType)
{
    /// <summary>Whether the server answered with success.</summary>
    public bool Succeeded => (int)Status is >= 200 and < 300;
}

/// <summary>
/// The plugin's side of the sharing server's interface (docs/networking/ServerApi-v1.md; decision
/// R2): HTTPS to the one deployment name the plugin is configured with, never to a name any server
/// sends. Every signed request is made here: a fresh challenge, a request proof of the kind the
/// path takes (section 14 of the specification), and, when the server refuses the challenge, one
/// retry under the fresh challenge its answer carries. Answers are read within a bound for each
/// request. Nothing here runs on the framework thread, and nothing is logged: the caller reports
/// outcomes by their kind. Compiled only in the networking preview flavour (decision R3).
/// </summary>
internal sealed class SharingClient : IDisposable
{
    /// <summary>The largest JSON answer read: the server's are a few hundred bytes.</summary>
    public const int MaxJsonAnswerBytes = 4096;

    /// <summary>The largest served profile read (section 8.6).</summary>
    public const int MaxServedProfileBytes = ProtocolLimits.MaxServedProfileBytes;

    /// <summary>The largest image read (section 8.2).</summary>
    public const int MaxImageBytes = (int)ProtocolLimits.MaxImageBytes;

    private readonly HttpClient client;
    private readonly DeploymentName deployment;

    /// <summary>
    /// A client for <paramref name="deployment"/>, over <paramref name="handler"/>. The plugin's
    /// handler connects dual-stack through Dalamud's callback and follows no redirect
    /// (<c>SharingHandler</c>); tests pass the server's in-process handler.
    /// </summary>
    public SharingClient(DeploymentName deployment, HttpMessageHandler handler, bool disposeHandler)
    {
        this.deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        ArgumentNullException.ThrowIfNull(handler);
        client = new HttpClient(handler, disposeHandler)
        {
            BaseAddress = new Uri("https://" + deployment.Value + "/", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AetherFrame", "1"));
    }

    /// <summary>The deployment this client talks to.</summary>
    public DeploymentName Deployment => deployment;

    /// <summary>How long one request may take, from sending it to reading the answer.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a publish may take: its body can be tens of megabytes.</summary>
    public TimeSpan PublishTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The path of each action (ServerApi-v1.md, section 2.1), fixed by its kind.</summary>
    public static string PathOf(RequestProofKind kind) => kind switch
    {
        RequestProofKind.LodestoneCode => "v1/lodestone/code",
        RequestProofKind.LodestoneCheck => "v1/lodestone/check",
        RequestProofKind.LodestoneReread => "v1/lodestone/reread",
        RequestProofKind.OptOut => "v1/opt-out",
        RequestProofKind.Lookup => "v1/lookup",
        RequestProofKind.Image => "v1/image",
        RequestProofKind.Report => "v1/report",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "Only an action has a path of its own."),
    };

    /// <summary><c>GET /v1/status</c>: the server's protocol version, API and oldest plugin.</summary>
    public Task<SharingResponse> StatusAsync(CancellationToken cancellation) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "v1/status"), MaxJsonAnswerBytes, RequestTimeout, cancellation);

    /// <summary>A fresh challenge (<c>POST /v1/challenge</c>).</summary>
    /// <exception cref="SharingException">The server answered with anything but a challenge.</exception>
    public async Task<RequestChallenge> ChallengeAsync(CancellationToken cancellation)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "v1/challenge") { Content = new ByteArrayContent([]) }, ProtocolConstants.ChallengeLength, RequestTimeout, cancellation).ConfigureAwait(false);
        return ChallengeFrom(response) ?? throw new SharingException(response.Status, "The server didn't answer with a challenge.");
    }

    /// <summary>
    /// Sends one action, signed by <paramref name="signer"/> as <paramref name="kind"/>, with
    /// <paramref name="body"/> (at most 4,096 bytes) to that kind's path. A refused challenge is
    /// retried once under the fresh challenge the refusal carries.
    /// </summary>
    public async Task<SharingResponse> ActionAsync(RequestProofKind kind, byte[] body, IPersonaSigner signer, int answerBound, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(signer);
        var path = PathOf(kind);
        var challenge = await ChallengeAsync(cancellation).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var proof = RequestProofCodec.SignAction(kind, body, deployment, challenge, signer);
            var envelope = Envelope(proof, body);
            var response = await SendAsync(() => Post(path, envelope), answerBound, RequestTimeout, cancellation).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.Conflict || attempt > 0 || ChallengeFrom(response) is not { } fresh)
            {
                return response;
            }

            challenge = fresh;
        }
    }

    /// <summary>
    /// Publishes a signed snapshot with its prepared images, in the snapshot's image order
    /// (ServerApi-v1.md, section 2.2), proved by <paramref name="signer"/>, which signed the
    /// document. A refused challenge is retried once.
    /// </summary>
    public async Task<SharingResponse> PublishAsync(byte[] document, IReadOnlyList<byte[]> images, IPersonaSigner signer, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(signer);
        var payload = PublishPayload(document, images);
        var challenge = await ChallengeAsync(cancellation).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var proof = RequestProofCodec.Sign(document, deployment, challenge, signer);
            var envelope = Envelope(proof, payload);
            var response = await SendAsync(() => Post("v1/publish", envelope), MaxJsonAnswerBytes, PublishTimeout, cancellation).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.Conflict || attempt > 0 || ChallengeFrom(response) is not { } fresh)
            {
                return response;
            }

            challenge = fresh;
        }
    }

    public void Dispose() => client.Dispose();

    /// <summary>A signed request's body: a <c>u16</c> proof length, the proof, then the payload.</summary>
    internal static byte[] Envelope(byte[] proof, byte[] payload)
    {
        var body = new byte[2 + proof.Length + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, checked((ushort)proof.Length));
        proof.CopyTo(body, 2);
        payload.CopyTo(body, 2 + proof.Length);
        return body;
    }

    /// <summary>A publish payload: the document, then each image, each with a length (ServerApi-v1.md, section 2.2).</summary>
    internal static byte[] PublishPayload(byte[] document, IReadOnlyList<byte[]> images)
    {
        if (images.Count > ProtocolLimits.MaxImagesPerProfile)
        {
            throw new ArgumentException("A snapshot carries at most eight images.", nameof(images));
        }

        var length = 4 + document.Length + 1;
        foreach (var image in images)
        {
            length += 4 + image.Length;
        }

        var payload = new byte[length];
        var position = 0;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(position), (uint)document.Length);
        position += 4;
        document.CopyTo(payload, position);
        position += document.Length;
        payload[position++] = (byte)images.Count;
        foreach (var image in images)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(position), (uint)image.Length);
            position += 4;
            image.CopyTo(payload, position);
            position += image.Length;
        }

        return payload;
    }

    private static HttpRequestMessage Post(string path, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
    }

    private static RequestChallenge? ChallengeFrom(SharingResponse response)
    {
        if (!(response.Succeeded || response.Status == HttpStatusCode.Conflict) || response.Body.Length != ProtocolConstants.ChallengeLength)
        {
            return null;
        }

        try
        {
            return RequestChallenge.FromBytes(response.Body);
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends a request and reads its answer, at most <paramref name="bound"/> bytes of it, within
    /// <paramref name="timeout"/>. A longer answer is refused, not truncated.
    /// </summary>
    private async Task<SharingResponse> SendAsync(Func<HttpRequestMessage> request, int bound, TimeSpan timeout, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try
        {
            using var message = request();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > bound)
            {
                throw new SharingException(response.StatusCode, "The server's answer is longer than this request allows.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > bound)
                {
                    throw new SharingException(response.StatusCode, "The server's answer is longer than this request allows.");
                }

                buffer.Write(chunk, 0, read);
            }

            return new SharingResponse(response.StatusCode, buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new SharingException(null, "The server didn't answer in time.");
        }
        catch (HttpRequestException e)
        {
            throw new SharingException(null, "The server can't be reached (" + e.HttpRequestError + ").");
        }
        catch (IOException)
        {
            throw new SharingException(null, "The connection to the server broke.");
        }
    }
}

/// <summary>A request to the sharing server that didn't get an answer the client could use. Its message names no identifier.</summary>
internal sealed class SharingException(HttpStatusCode? status, string message) : Exception(message)
{
    /// <summary>The server's status, when it answered at all.</summary>
    public HttpStatusCode? Status { get; } = status;
}
