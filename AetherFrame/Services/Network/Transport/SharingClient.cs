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

    /// <summary>Through the pipe only: <c>lodestone:refused</c> with a <c>503</c>, when the Lodestone turned the player's connection away (ServerApi-v1.md, section 2.3).</summary>
    public string? Reason { get; init; }

    /// <summary>Through the pipe only: with a successful check or re-read, the binding's day of last successful read, in UTC days since the Unix epoch.</summary>
    public long? ReadDay { get; init; }

    /// <summary>Whether the Lodestone turned the player's own connection away.</summary>
    public bool LodestoneRefused => Status == HttpStatusCode.ServiceUnavailable && Reason == "lodestone:refused";
}

/// <summary>
/// The plugin's side of the sharing server's interface (docs/networking/ServerApi-v1.md; decision
/// R2): HTTPS to the one deployment name the plugin is configured with, never to a name any server
/// sends. Every signed request is made here: a fresh challenge, a request proof of the kind the
/// path takes (section 14 of the specification), and, when the server refuses the challenge, one
/// retry under the fresh challenge its answer carries. Every request names the plugin's version
/// and nothing else about the player or the machine.
/// <para>
/// Answers are read within a bound fixed by each request's kind, and within a time limit. The
/// callers run every call off the framework thread, in a background task: a publish's body is
/// streamed from the document and images the caller holds, never copied whole. Nothing here is
/// logged: the caller reports outcomes by their kind. Compiled only in the networking preview
/// flavour (decision R3).
/// </para>
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
    private readonly LodestonePipe pipe;

    /// <summary>
    /// A client for <paramref name="deployment"/>, over <paramref name="handler"/>, naming
    /// <paramref name="pluginVersion"/> in every request. The plugin's handler connects dual-stack
    /// through Dalamud's callback and follows no redirect (<c>SharingHandler</c>); tests pass their own.
    /// </summary>
    public SharingClient(DeploymentName deployment, HttpMessageHandler handler, bool disposeHandler, Version pluginVersion)
        : this(deployment, handler, disposeHandler, pluginVersion, null)
    {
    }

    /// <summary>
    /// A client whose Lodestone pipe connects through <paramref name="lodestoneLinks"/>, when it is
    /// given: for tests, which never reach the Lodestone. The plugin's connects as
    /// <see cref="LodestonePipe"/> says.
    /// </summary>
    internal SharingClient(DeploymentName deployment, HttpMessageHandler handler, bool disposeHandler, Version pluginVersion, Func<LodestonePipe.Link>? lodestoneLinks)
    {
        this.deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(pluginVersion);
        client = new HttpClient(handler, disposeHandler)
        {
            BaseAddress = new Uri("https://" + deployment.Value + "/", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AetherFrame", pluginVersion.ToString(3)));
        pipe = lodestoneLinks is null ? new LodestonePipe(deployment, handler, pluginVersion) : new LodestonePipe(deployment, handler, pluginVersion, lodestoneLinks);
    }

    /// <summary>The deployment this client talks to.</summary>
    public DeploymentName Deployment => deployment;

    /// <summary>How long one request other than a publish may take, from sending it to reading the answer.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What a publish may take before its answer, besides its images' turns in the server's image
    /// worker. The server reads the proof within 10 seconds and the rest within 300, its challenge's
    /// life (the server's <c>PlateEndpoints.ProofDeadline</c> and <c>BodyDeadline</c>); the largest
    /// publish (about 43 MB) needs some 140 KiB/s upstream for that. 30 seconds more cover the
    /// server's own work after the body, storing the images included, and the network both ways.
    /// </summary>
    public TimeSpan PublishBaseTimeout { get; init; } = TimeSpan.FromSeconds(10 + 300 + 30);

    /// <summary>
    /// What each image a publish carries adds. Once the body is read, the server passes the images
    /// through its image worker one at a time, and gives each up to 30 seconds to get its turn and a
    /// worker run, then 30 seconds for the run's answer (the server's
    /// <c>ImageWorkerClient.WorkerPatience</c> and <c>JobDeadline</c>), before it answers.
    /// </summary>
    public TimeSpan PublishImageTimeout { get; init; } = TimeSpan.FromSeconds(30 + 30);

    /// <summary>
    /// How long a publish carrying <paramref name="images"/> images may take, from sending it to
    /// reading the answer: the server's own worst case for it, so the plugin never stops waiting
    /// while the server would still answer. Eight images, the most a publish carries, give 820
    /// seconds; a stop by the player or by unloading ends it sooner.
    /// </summary>
    public TimeSpan PublishTimeout(int images) => PublishBaseTimeout + (PublishImageTimeout * Math.Max(0, images));

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

    /// <summary>The largest answer an action's kind is read to: a served profile, an image, or a small JSON object.</summary>
    public static int AnswerBoundOf(RequestProofKind kind) => kind switch
    {
        RequestProofKind.Lookup => MaxServedProfileBytes,
        RequestProofKind.Image => MaxImageBytes,
        _ => MaxJsonAnswerBytes,
    };

    /// <summary><c>GET /v1/status</c>: the server's protocol version, API and oldest plugin.</summary>
    public Task<SharingResponse> StatusAsync(CancellationToken cancellation) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "v1/status"), MaxJsonAnswerBytes, RequestTimeout, cancellation);

    /// <summary>A fresh challenge (<c>POST /v1/challenge</c>).</summary>
    /// <exception cref="SharingException">The server answered with anything but a challenge.</exception>
    public async Task<RequestChallenge> ChallengeAsync(CancellationToken cancellation)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "v1/challenge") { Content = new ByteArrayContent([]) }, ProtocolConstants.ChallengeLength, RequestTimeout, cancellation).ConfigureAwait(false);
        return ChallengeFrom(response, HttpStatusCode.OK) ?? throw new SharingException(response.Status, "The server didn't answer with a challenge.");
    }

    /// <summary>
    /// Sends one action, signed by <paramref name="signer"/> as <paramref name="kind"/>, with
    /// <paramref name="body"/> (at most 4,096 bytes, checked before a challenge is asked for) to
    /// that kind's path, and reads the answer within that kind's bound. A refused challenge is
    /// retried once under the fresh challenge the refusal carries.
    /// </summary>
    public async Task<SharingResponse> ActionAsync(RequestProofKind kind, byte[] body, IPersonaSigner signer, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(signer);
        var path = PathOf(kind);
        if (body.Length > ProtocolLimits.MaxActionBodyBytes)
        {
            throw new ArgumentException("An action's body is at most 4,096 bytes.", nameof(body));
        }

        var bound = AnswerBoundOf(kind);
        var challenge = await ChallengeAsync(cancellation).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var proof = RequestProofCodec.SignAction(kind, body, deployment, challenge, signer);
            var envelope = Envelope(proof, body);
            var response = await SendAsync(() => Post(path, new ByteArrayContent(envelope)), bound, RequestTimeout, cancellation).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.Conflict || attempt > 0 || ChallengeFrom(response, HttpStatusCode.Conflict) is not { } fresh)
            {
                return response;
            }

            challenge = fresh;
        }
    }

    /// <summary>
    /// Sends the Lodestone check or re-read as <see cref="ActionAsync"/> does, but as a WebSocket
    /// whose page the server reads through the player's own connection (<see cref="LodestonePipe"/>;
    /// ServerApi-v1.md, section 2.3). A refused challenge is signed again under the fresh one the
    /// final answer carries, on a new WebSocket, once.
    /// </summary>
    public async Task<SharingResponse> PipedActionAsync(RequestProofKind kind, byte[] body, IPersonaSigner signer, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(signer);
        if (kind is not (RequestProofKind.LodestoneCheck or RequestProofKind.LodestoneReread))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Only the check and the re-read go through the pipe.");
        }

        if (body.Length > ProtocolLimits.MaxActionBodyBytes)
        {
            throw new ArgumentException("An action's body is at most 4,096 bytes.", nameof(body));
        }

        var challenge = await ChallengeAsync(cancellation).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var proof = RequestProofCodec.SignAction(kind, body, deployment, challenge, signer);
            var response = await pipe.RunAsync(kind, Envelope(proof, body), cancellation).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.Conflict || attempt > 0 || ChallengeFrom(response, HttpStatusCode.Conflict) is not { } fresh)
            {
                return response;
            }

            challenge = fresh;
        }
    }

    /// <summary>
    /// Publishes a signed snapshot with its prepared images, in the snapshot's image order
    /// (ServerApi-v1.md, section 2.2), proved by <paramref name="signer"/>, which signed the
    /// document. Its size and image count are checked before a challenge is asked for. The body is
    /// streamed from <paramref name="document"/> and <paramref name="images"/>, which must not
    /// change until this returns. A refused challenge is retried once.
    /// </summary>
    public async Task<SharingResponse> PublishAsync(byte[] document, IReadOnlyList<byte[]> images, IPersonaSigner signer, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(signer);
        PublishContent.Check(document, images);
        var challenge = await ChallengeAsync(cancellation).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var proof = RequestProofCodec.Sign(document, deployment, challenge, signer);
            var response = await SendAsync(() => Post("v1/publish", PublishContent.Create(proof, document, images)), MaxJsonAnswerBytes, PublishTimeout(images.Count), cancellation).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.Conflict || attempt > 0 || ChallengeFrom(response, HttpStatusCode.Conflict) is not { } fresh)
            {
                return response;
            }

            challenge = fresh;
        }
    }

    public void Dispose()
    {
        pipe.Dispose();
        client.Dispose();
    }

    /// <summary>A signed request's body: a <c>u16</c> proof length, the proof, then the payload.</summary>
    internal static byte[] Envelope(byte[] proof, byte[] payload)
    {
        var body = new byte[2 + proof.Length + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, checked((ushort)proof.Length));
        proof.CopyTo(body, 2);
        payload.CopyTo(body, 2 + proof.Length);
        return body;
    }

    private static HttpRequestMessage Post(string path, HttpContent content)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
    }

    /// <summary>The challenge an answer carries: 32 bytes, with <paramref name="expected"/> as its status (<c>200</c> from <c>/v1/challenge</c>, <c>409</c> from a refusal).</summary>
    private static RequestChallenge? ChallengeFrom(SharingResponse response, HttpStatusCode expected)
    {
        if (response.Status != expected || response.Body.Length != ProtocolConstants.ChallengeLength)
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
    /// <paramref name="timeout"/>. A longer answer is refused, not truncated, whether or not it
    /// declares its length.
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
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException("The request was cancelled.", cancellation);
        }
        catch (OperationCanceledException)
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

    /// <summary>
    /// A publish's body (ServerApi-v1.md, sections 2 and 2.2), read straight from the proof, the
    /// document and the images, with its length known before it is sent: a <c>u16</c> proof length,
    /// the proof, a <c>u32</c> document length, the document, the image count, then each image with
    /// a <c>u32</c> length. Only the small length fields are copied.
    /// </summary>
    internal static class PublishContent
    {
        internal static HttpContent Create(byte[] proof, byte[] document, IReadOnlyList<byte[]> images)
        {
            ArgumentNullException.ThrowIfNull(proof);
            Check(document, images);
            var head = new byte[2 + proof.Length + 4];
            BinaryPrimitives.WriteUInt16BigEndian(head, checked((ushort)proof.Length));
            proof.CopyTo(head, 2);
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(2 + proof.Length), (uint)document.Length);
            var segments = new List<byte[]> { head, document, new[] { (byte)images.Count } };
            foreach (var image in images)
            {
                var length = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(length, (uint)image.Length);
                segments.Add(length);
                segments.Add(image);
            }

            return new StreamContent(new JoinedStream(segments));
        }

        /// <summary>Refuses a document or an image list no publish can carry, before anything is sent.</summary>
        internal static void Check(byte[] document, IReadOnlyList<byte[]> images)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(images);
            if (document.Length is 0 or > ProtocolLimits.MaxDocumentBytes)
            {
                throw new ArgumentException("A snapshot is 1 to 1,048,576 bytes.", nameof(document));
            }

            if (images.Count > ProtocolLimits.MaxImagesPerProfile)
            {
                throw new ArgumentException("A snapshot carries at most eight images.", nameof(images));
            }

            long total = 0;
            foreach (var image in images)
            {
                if (image is null || image.Length is 0 or > MaxImageBytes)
                {
                    throw new ArgumentException("Each image is 1 byte to its limit.", nameof(images));
                }

                total += image.Length;
            }

            if (total > ProtocolLimits.MaxProfileImageBytes)
            {
                throw new ArgumentException("A snapshot's images are 40 MiB at most, together.", nameof(images));
            }
        }
    }

    /// <summary>A read-only, seekable stream over byte arrays read one after another, none of them copied.</summary>
    internal sealed class JoinedStream(IReadOnlyList<byte[]> segments) : Stream
    {
        private readonly long length = Total(segments);
        private long position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => position;
            set => position = value is >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var written = 0;
            var start = 0L;
            foreach (var segment in segments)
            {
                if (written == buffer.Length)
                {
                    break;
                }

                var end = start + segment.Length;
                if (position < end)
                {
                    var from = (int)(position - start);
                    var take = Math.Min(segment.Length - from, buffer.Length - written);
                    segment.AsSpan(from, take).CopyTo(buffer[written..]);
                    written += take;
                    position += take;
                }

                start = end;
            }

            return written;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            cancellationToken.IsCancellationRequested ? ValueTask.FromCanceled<int>(cancellationToken) : ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static long Total(IReadOnlyList<byte[]> segments)
        {
            var total = 0L;
            foreach (var segment in segments)
            {
                total += segment.Length;
            }

            return total;
        }
    }
}

/// <summary>A request to the sharing server that didn't get an answer the client could use. Its message names no identifier.</summary>
internal sealed class SharingException(HttpStatusCode? status, string message) : Exception(message)
{
    /// <summary>The server's status, when it answered at all.</summary>
    public HttpStatusCode? Status { get; } = status;
}
