using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
using Microsoft.AspNetCore.Routing;
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

                var key = call.Persona.ToString();
                if (!limiter.TryTake(ServerLimits.LookupsPerKeyHour, key) || !limiter.TryTake(ServerLimits.LookupsPerKeyDay, key))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:lookup/key");
                }

                var (refusal, target) = await viewing.FindAsync(http, call.Persona, body.String("name"), body.String("world"));
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

                if (!limiter.TryTake(ServerLimits.ImagesPerKey, call.Persona.ToString()))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:image/key");
                }

                var (refusal, target) = await viewing.FindAsync(http, call.Persona, body.String("name"), body.String("world"));
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
            requests.RunActionAsync(http, RequestProofKind.Report, null, async call =>
            {
                var body = ActionBody.Read(call.Action.Body, ReportFields);
                if (body is null || !Reasons.Contains(body.String("reason")))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:json");
                }

                if (!limiter.TryTake(ServerLimits.ReportsPerKey, call.Persona.ToString()))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:report/key");
                }

                var (refusal, target) = await viewing.FindAsync(http, call.Persona, body.String("name"), body.String("world"));
                if (refusal is not null)
                {
                    return refusal;
                }

                await content.ReportAsync(target!.LodestoneId, body.String("reason"), call.Persona, http.RequestAborted);
                return Results.NoContent();
            }));
    }

    /// <summary>
    /// Publishing (ServerApi-v1.md, section 2.2): the proof as section 14.4 checks it, then the
    /// challenge, then the binding and the allowlist, the character's limit, the snapshot's schema,
    /// profile id and clock, rule 4, and each image, which must match its declaration and pass the
    /// image worker before anything is stored.
    /// </summary>
    private static async Task<IResult> PublishAsync(HttpContext http, IOptions<ServerOptions> options, SignedRequests requests, ChallengeStore challenges, RateLimiter limiter, BindingStore bindings, Allowlist allowlist, ContentStore content, IImageProcessor processor, PublishSlots slots, TimeProvider time)
    {
        if (!limiter.TryTakeAddress(ServerLimits.PublishesPerAddress, http.Connection.RemoteIpAddress))
        {
            return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:publish/address");
        }

        if (!await slots.Gate.WaitAsync(0, http.RequestAborted))
        {
            return SignedRequests.Fail(http, StatusCodes.Status503ServiceUnavailable, "publish:busy");
        }

        try
        {
            var (body, failure) = await SignedRequests.ReadBoundedAsync(http, MaxPublishRequestBytes, http.RequestAborted);
            if (body is null)
            {
                return SignedRequests.Fail(http, failure, "body:unread");
            }

            if (!SignedRequests.TrySplit(body, out var proof, out var payload) || !TryReadPublish(payload, out var document, out var imageBytes))
            {
                return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:envelope");
            }

            VerifiedSubmission submission;
            try
            {
                submission = RequestProofCodec.VerifySubmission(proof.Span, document.Span, options.Value.Deployment);
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

                var output = await processor.ProcessAsync(declared, imageBytes[index], http.RequestAborted);
                if (output is null || !ProcessedImages.Check(output, declared))
                {
                    return Refuse(http, "image-refused");
                }

                stored.Add(new StoredImage(declared.Format, output));
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
        finally
        {
            slots.Gate.Release();
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

/// <summary>Two publishes at a time at most, so buffered bodies stay bounded; a third is told to retry.</summary>
internal sealed class PublishSlots
{
    public SemaphoreSlim Gate { get; } = new(2, 2);
}

/// <summary>
/// Who may view, and whom (decisions C5 and C8): the requester must be bound to a character on the
/// allowlist, and the character looked up must be shown under that exact name and World, bound and on
/// the allowlist too. Every other case is the same "not found".
/// </summary>
internal sealed class Viewing(BindingStore bindings, Allowlist allowlist, Worlds worlds)
{
    public async Task<(IResult? Refusal, Binding? Target)> FindAsync(HttpContext http, PersonaId requester, string name, string world)
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
