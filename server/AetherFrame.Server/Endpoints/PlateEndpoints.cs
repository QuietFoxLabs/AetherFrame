using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Hosting;
using AetherFrame.Server.Images;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Lodestone;
using AetherFrame.Server.Requests;
using AetherFrame.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Endpoints;

/// <summary>
/// Publishing a character's Plate, and viewing, fetching images of and reporting another's
/// (ServerApi-v1.md, sections 2.1 and 2.2; decisions N2, N6, D6, I2, C3 to C8).
/// </summary>
internal static class PlateEndpoints
{
    /// <summary>The largest publish request (ServerApi-v1.md, section 5).</summary>
    public const int MaxPublishRequestBytes = 43_000_000;

    /// <summary>How far ahead of the server's clock a snapshot's <c>createdAt</c> may be (decision N6).</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(300);

    /// <summary>How long a publish has to send its proof, before anything else is looked at.</summary>
    public static readonly TimeSpan ProofDeadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long an authenticated publish has to send the rest: its challenge's life (rule 10), since a
    /// body that takes longer fails when its challenge is consumed anyway, so no slot is held past it.
    /// </summary>
    public static readonly TimeSpan BodyDeadline = TimeSpan.FromSeconds(300);

    /// <summary>The slowest an authenticated publish may send, after a 10-second grace.</summary>
    public static readonly MinDataRate MinBodyRate = new(bytesPerSecond: 16 * 1024, gracePeriod: TimeSpan.FromSeconds(10));

    private static readonly string[] CharacterFields = ["name", "world"];
    private static readonly string[] ImageFields = ["name", "world", "marker"];
    private static readonly string[] ImageNumbers = ["index"];
    private static readonly string[] ReportFields = ["name", "world", "reason"];
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal) { "offensive", "impersonation", "spam", "other" };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/publish", PublishAsync);

        app.MapPost("/v1/lookup", (HttpContext http, SignedRequests requests, RateLimiter limiter, Viewing viewing, ContentStore content) =>
            requests.RunActionAsync(http, RequestProofKind.Lookup, ServerLimits.LookupsPerAddress, async call =>
            {
                var body = ActionBody.Read(call.Action.Body, CharacterFields);
                if (body is null)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:json");
                }

                var (refusal, target) = await viewing.FindAsync(http, call.Persona, body.String("name"), body.String("world"), () =>
                    limiter.TryTake(ServerLimits.LookupsPerKeyHour, call.Persona.ToString()) && limiter.TryTake(ServerLimits.LookupsPerKeyDay, call.Persona.ToString()));
                if (refusal is not null)
                {
                    return refusal;
                }

                var served = await content.ServedAsync(target!.Persona, target.ProfileId, http.RequestAborted);
                return served is null
                    ? SignedRequests.Fail(http, StatusCodes.Status404NotFound, "lookup:nothing")
                    : Results.Bytes(served, "application/octet-stream");
            }));

        app.MapPost("/v1/image", (HttpContext http, SignedRequests requests, RateLimiter limiter, Viewing viewing, ContentStore content) =>
            requests.RunActionAsync(http, RequestProofKind.Image, ServerLimits.ImagesPerAddress, async call =>
            {
                var body = ActionBody.Read(call.Action.Body, ImageFields, ImageNumbers);
                if (body is null || !RevisionMarker.TryParse(body.String("marker"), out var marker) || body.Number("index") is < 0 or >= ProtocolLimits.MaxImagesPerProfile)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:json");
                }

                var (refusal, target) = await viewing.FindAsync(http, call.Persona, body.String("name"), body.String("world"), () =>
                    limiter.TryTake(ServerLimits.ImagesPerKey, call.Persona.ToString()));
                if (refusal is not null)
                {
                    return refusal;
                }

                var image = await content.ImageAsync(target!.Persona, target.ProfileId, marker, (int)body.Number("index"), http.RequestAborted);
                return image is null
                    ? SignedRequests.Fail(http, StatusCodes.Status404NotFound, "image:nothing")
                    : Results.Bytes(image.Bytes, image.Format == ImageFormat.Png ? "image/png" : "image/jpeg");
            }));

        app.MapPost("/v1/report", (HttpContext http, SignedRequests requests, RateLimiter limiter, Viewing viewing, ContentStore content) =>
            requests.RunActionAsync(http, RequestProofKind.Report, ServerLimits.ReportsPerAddress, async call =>
            {
                var body = ActionBody.Read(call.Action.Body, ReportFields);
                if (body is null || !Reasons.Contains(body.String("reason")))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:json");
                }

                var (refusal, target) = await viewing.FindAsync(http, call.Persona, body.String("name"), body.String("world"), () =>
                    limiter.TryTake(ServerLimits.ReportsPerKey, call.Persona.ToString()));
                if (refusal is not null)
                {
                    return refusal;
                }

                await content.ReportAsync(target!.LodestoneId, body.String("reason"), call.Persona, http.RequestAborted);
                return Results.NoContent();
            }));
    }

    /// <summary>
    /// Publishing (ServerApi-v1.md, section 2.2). Before a publish slot is taken or the body is read
    /// past its proof, the proof must pass section 14.4's first two steps, its challenge must be live,
    /// and its signer bound to a character on the allowlist, so no one else can hold a slot. Then,
    /// under a deadline and a minimum data rate, the rest: the whole of section 14.4 with the
    /// document, the challenge consumed, the binding again, the character's limit, the snapshot's
    /// schema, profile id and clock, rule 4, and each image, which must match its declaration and pass
    /// the image worker before anything is stored.
    /// </summary>
    private static async Task<IResult> PublishAsync(HttpContext http, IOptions<ServerOptions> options, SignedRequests requests, ChallengeStore challenges, RateLimiter limiter, BindingStore bindings, Allowlist allowlist, ContentStore content, IImageProcessor processor, PublishSlots slots, TimeProvider time)
    {
        if (!limiter.TryTakeAddress(ServerLimits.PublishesPerAddress, http.Connection.RemoteIpAddress))
        {
            return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:publish/address");
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = MaxPublishRequestBytes;
        }

        if (http.Request.ContentLength > MaxPublishRequestBytes)
        {
            return SignedRequests.Fail(http, StatusCodes.Status413PayloadTooLarge, "body:too-large");
        }

        // Kestrel fixes the minimum rate when the body is first read, so it is set before the proof.
        if (http.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate)
        {
            rate.MinDataRate = MinBodyRate;
        }

        // The proof first, within a short deadline.
        byte[]? proof;
        using (var proofDeadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted))
        {
            proofDeadline.CancelAfter(ProofDeadline);
            proof = await ReadProofAsync(http.Request.Body, proofDeadline.Token);
        }

        if (proof is null)
        {
            return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:envelope");
        }

        VerifiedRequestProof early;
        try
        {
            early = RequestProofCodec.CheckSubmissionProof(proof, options.Value.Deployment);
        }
        catch (ProtocolException e)
        {
            return SignedRequests.Fail(http, StatusCodes.Status403Forbidden, "proof:" + e.Error);
        }

        if (!await challenges.IsLiveAsync(early.Challenge, http.RequestAborted))
        {
            return await requests.RefuseChallengeAsync(http);
        }

        var signer = await bindings.FindByPersonaAsync(early.Persona, http.RequestAborted);
        if (signer is null)
        {
            return await bindings.WasTakenOverAsync(early.Persona, http.RequestAborted)
                ? SignedRequests.Fail(http, StatusCodes.Status410Gone, "publish:taken-over")
                : Refuse(http, "not-bound");
        }

        if (!allowlist.Allows(signer.LodestoneId))
        {
            return Refuse(http, "not-bound");
        }

        using var slot = slots.TryTake(signer.LodestoneId, http.Connection.RemoteIpAddress);
        if (slot is null)
        {
            return SignedRequests.Fail(http, StatusCodes.Status503ServiceUnavailable, "publish:busy");
        }

        {
            byte[]? payloadBytes;
            using (var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted))
            {
                bodyDeadline.CancelAfter(BodyDeadline);
                payloadBytes = await ReadRestAsync(http, 2 + proof.Length, MaxPublishRequestBytes - 2 - proof.Length, bodyDeadline.Token);
            }

            if (payloadBytes is null || !TryReadPublish(payloadBytes, out var document, out var imageBytes))
            {
                return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:payload");
            }

            VerifiedSubmission submission;
            try
            {
                submission = RequestProofCodec.VerifySubmission(proof, document.Span, options.Value.Deployment);
            }
            catch (ProtocolException e)
            {
                return SignedRequests.Fail(http, e.Error == ProtocolError.LimitExceeded ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status403Forbidden, "proof:" + e.Error);
            }

            if (!await challenges.TryConsumeAsync(submission.Challenge, http.RequestAborted))
            {
                return await requests.RefuseChallengeAsync(http);
            }

            var persona = submission.Proof.Persona;
            var binding = await bindings.FindByPersonaAsync(persona, http.RequestAborted);
            if (binding is null)
            {
                return await bindings.WasTakenOverAsync(persona, http.RequestAborted)
                    ? SignedRequests.Fail(http, StatusCodes.Status410Gone, "publish:taken-over")
                    : Refuse(http, "not-bound");
            }

            if (!allowlist.Allows(binding.LodestoneId))
            {
                return Refuse(http, "not-bound");
            }

            if (!limiter.TryTake(ServerLimits.PublishesPerCharacter, binding.LodestoneId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:publish/character");
            }

            if (submission.Document.Document is not ProfileLayoutSnapshot snapshot)
            {
                return Refuse(http, "not-a-layout");
            }

            if (snapshot.ProfileId != binding.ProfileId)
            {
                return Refuse(http, "wrong-profile");
            }

            if (snapshot.CreatedAt > time.GetUtcNow() + MaxClockSkew)
            {
                return Refuse(http, "clock-ahead");
            }

            var documentBytes = submission.DocumentBytes.ToArray();
            var sha = SHA256.HashData(documentBytes);
            switch (await content.CheckRevisionAsync(persona, binding.ProfileId, snapshot.RevisionId, sha, http.RequestAborted))
            {
                case PublishResult.AlreadyKnown:
                    return Results.NoContent();
                case PublishResult.Conflict:
                    return Refuse(http, "revision-conflict");
            }

            if (imageBytes.Count != snapshot.Images.Count)
            {
                return Refuse(http, "image-refused");
            }

            // The worker is one pipeline for everyone (I2): a character whose images failed in it
            // three times this hour waits the hour out, and each character has an hour's image budget.
            var character = binding.LodestoneId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (imageBytes.Count > 0 && !limiter.HasRoom(ServerLimits.WorkerFailuresPerCharacter, character))
            {
                return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:publish/worker-failures");
            }

            var stored = new List<StoredImage>(imageBytes.Count);
            for (var index = 0; index < imageBytes.Count; index++)
            {
                var declared = snapshot.Images[index];
                try
                {
                    ImageSniffer.CheckDeclared(imageBytes[index].Span, declared);
                }
                catch (ProtocolException)
                {
                    return Refuse(http, "image-refused");
                }

                if (!limiter.TryTake(ServerLimits.ImageJobsPerCharacter, character))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:publish/images");
                }

                var processed = await processor.ProcessAsync(declared, imageBytes[index], http.RequestAborted);
                if (processed.IsBusy)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status503ServiceUnavailable, "publish:images-busy");
                }

                if (processed.Bytes is null || !ProcessedImages.Check(processed.Bytes, declared))
                {
                    limiter.TryTake(ServerLimits.WorkerFailuresPerCharacter, character);
                    return Refuse(http, "image-refused");
                }

                stored.Add(new StoredImage(declared.Format, processed.Bytes));
            }

            var marker = RevisionMarker.NewMarker();
            var served = ServedProfile.Build(snapshot, marker);
            return await content.PublishAsync(persona, binding.ProfileId, snapshot.RevisionId, documentBytes, sha, served, marker, stored, http.RequestAborted) switch
            {
                PublishResult.Published or PublishResult.AlreadyKnown => Results.NoContent(),
                PublishResult.Conflict => Refuse(http, "revision-conflict"),
                _ => Refuse(http, "not-bound"),
            };
        }
    }

    /// <summary>Reads the envelope's proof: its <c>u16</c> length, then that many bytes. Null for anything else.</summary>
    private static async Task<byte[]?> ReadProofAsync(Stream body, CancellationToken cancellation)
    {
        try
        {
            var length = new byte[2];
            await body.ReadExactlyAsync(length, cancellation);
            var proofLength = BinaryPrimitives.ReadUInt16BigEndian(length);
            if (proofLength < ProtocolLimits.RequestProofOverheadBytes + 1 || proofLength > ProtocolLimits.MaxRequestProofBytes)
            {
                return null;
            }

            var proof = new byte[proofLength];
            await body.ReadExactlyAsync(proof, cancellation);
            return proof;
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or OperationCanceledException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the rest of the body, at most <paramref name="bound"/> bytes: into one buffer of the
    /// exact size when the request declares its length, so a publish holds its payload once. Null
    /// when it is longer, cut short, too slow or unreadable.
    /// </summary>
    private static async Task<byte[]?> ReadRestAsync(HttpContext http, int alreadyRead, int bound, CancellationToken cancellation)
    {
        try
        {
            if (http.Request.ContentLength is { } total)
            {
                var remaining = total - alreadyRead;
                if (remaining < 0 || remaining > bound)
                {
                    return null;
                }

                var exact = new byte[remaining];
                await http.Request.Body.ReadExactlyAsync(exact, cancellation);
                return exact;
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, cancellation)) > 0)
            {
                if (buffer.Length + read > bound)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or OperationCanceledException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a publish payload (ServerApi-v1.md, section 2.2): a <c>u32</c> document length and the
    /// document, a <c>u8</c> image count, and each image as a <c>u32</c> length and its bytes, with
    /// nothing after.
    /// </summary>
    internal static bool TryReadPublish(ReadOnlyMemory<byte> payload, out ReadOnlyMemory<byte> document, out List<ReadOnlyMemory<byte>> images)
    {
        document = default;
        images = [];
        var span = payload.Span;
        if (span.Length < 4)
        {
            return false;
        }

        var documentLength = BinaryPrimitives.ReadUInt32BigEndian(span);
        if (documentLength is 0 or > ProtocolLimits.MaxDocumentBytes || span.Length < 4 + (int)documentLength + 1)
        {
            return false;
        }

        document = payload.Slice(4, (int)documentLength);
        var position = 4 + (int)documentLength;
        var count = span[position++];
        if (count > ProtocolLimits.MaxImagesPerProfile)
        {
            return false;
        }

        for (var index = 0; index < count; index++)
        {
            if (span.Length - position < 4)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
            position += 4;
            if (length is 0 or > (uint)ProtocolLimits.MaxImageBytes || span.Length - position < length)
            {
                return false;
            }

            images.Add(payload.Slice(position, (int)length));
            position += (int)length;
        }

        return position == span.Length;
    }

    /// <summary>A refused publish: 422 with its reason code as plain text (ServerApi-v1.md, section 4).</summary>
    private static IResult Refuse(HttpContext http, string reason)
    {
        http.Items[SignedRequests.ErrorKindItem] = "publish:" + reason;
        return Results.Text(reason, "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status422UnprocessableEntity);
    }
}

/// <summary>
/// The publish slots, held while a publish's body uploads, which also keeps buffered bodies bounded:
/// <see cref="Total"/> in all, and at most one per character and one per address range (an IPv4
/// address, or an IPv6 /48, the widest group a site is likely to hold), so no one player can hold
/// them all (the open alpha, October 1, 2026). A slot is released when its holder is disposed.
/// </summary>
internal sealed class PublishSlots
{
    /// <summary>The most publishes uploading at once.</summary>
    public const int Total = 4;

    private readonly object gate = new();
    private readonly HashSet<string> holders = new(StringComparer.Ordinal);
    private int held;

    /// <summary>A slot for a publish by the character <paramref name="lodestoneId"/> from <paramref name="address"/>, or null when none may be taken.</summary>
    public IDisposable? TryTake(long lodestoneId, System.Net.IPAddress? address)
    {
        var keys = new[]
        {
            "character/" + lodestoneId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "address/" + System.Linq.Enumerable.Last(AetherFrame.Server.Limits.AddressGroups.Of(address)).Group,
        };

        lock (gate)
        {
            if (held >= Total || holders.Contains(keys[0]) || holders.Contains(keys[1]))
            {
                return null;
            }

            held++;
            holders.Add(keys[0]);
            holders.Add(keys[1]);
        }

        return new Slot(this, keys);
    }

    private void Release(string[] keys)
    {
        lock (gate)
        {
            held--;
            holders.Remove(keys[0]);
            holders.Remove(keys[1]);
        }
    }

    private sealed class Slot(PublishSlots slots, string[] keys) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                slots.Release(keys);
            }
        }
    }
}

/// <summary>
/// Who may view, and whom (decisions C5 and C8): the requester must be bound to a character on the
/// allowlist, and the character looked up must be shown under that exact name and World, bound and on
/// the allowlist too. Every other case is the same "not found". The requester's own limits are taken
/// after the requester passes and before the target is looked for, so a key with no character costs
/// the server nothing, and a "not found" counts as a find does (C6).
/// </summary>
internal sealed class Viewing(BindingStore bindings, Allowlist allowlist, Worlds worlds)
{
    public async Task<(IResult? Refusal, Binding? Target)> FindAsync(HttpContext http, PersonaId requester, string name, string world, Func<bool> takeLimits)
    {
        var own = await bindings.FindByPersonaAsync(requester, http.RequestAborted);
        if (own is null)
        {
            return (await bindings.WasTakenOverAsync(requester, http.RequestAborted)
                ? SignedRequests.Fail(http, StatusCodes.Status410Gone, "view:taken-over")
                : SignedRequests.Fail(http, StatusCodes.Status404NotFound, "view:not-bound"), null);
        }

        if (!allowlist.Allows(own.LodestoneId))
        {
            return (SignedRequests.Fail(http, StatusCodes.Status404NotFound, "view:not-bound"), null);
        }

        if (!takeLimits())
        {
            return (SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:view/key"), null);
        }

        if (CharacterNames.Key(name) is not { } nameKey || !worlds.TryFind(world, out var canonicalWorld))
        {
            return (SignedRequests.Fail(http, StatusCodes.Status404NotFound, "view:no-character"), null);
        }

        var target = await bindings.FindShownAsync(nameKey, canonicalWorld, http.RequestAborted);
        if (target is null || !allowlist.Allows(target.LodestoneId))
        {
            return (SignedRequests.Fail(http, StatusCodes.Status404NotFound, "view:no-character"), null);
        }

        return (null, target);
    }
}
