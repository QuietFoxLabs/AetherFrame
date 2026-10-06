using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Requests;

/// <summary>A verified, challenge-consumed action request, as its handler receives it.</summary>
internal sealed record ActionCall(HttpContext Http, VerifiedAction Action)
{
    /// <summary>The signer: the identity of the key that signed the proof.</summary>
    public PersonaId Persona => Action.PublicKey.Id;

    /// <summary>The client's address, after the trusted proxy's forwarded header.</summary>
    public IPAddress? Address => Http.Connection.RemoteIpAddress;
}

/// <summary>
/// The pipeline every signed request goes through (ServerApi-v1.md, section 2; the specification's
/// sections 13 and 14): the address limit, the body read within its bound, the envelope, the proof
/// checked for the kind this endpoint expects (never a kind the request names), then the challenge
/// consumed, and only then the handler. A failure is a status with no body, and its kind is recorded
/// for the request log, never the bytes. A Lodestone check or re-read sent as a WebSocket (section
/// 2.3) takes its address limit before the upgrade and passes its first message through
/// <see cref="VerifyAsync"/>, the same checks in the same order.
/// </summary>
internal sealed class SignedRequests(IOptions<ServerOptions> options, ChallengeStore challenges, RateLimiter limiter)
{
    /// <summary>The largest action request body: the length, the largest proof and the largest action body.</summary>
    public const int MaxActionRequestBytes = 2 + ProtocolLimits.MaxRequestProofBytes + ProtocolLimits.MaxActionBodyBytes;

    /// <summary>The <see cref="HttpContext.Items"/> key under which a failure's kind is left for the request log.</summary>
    public const string ErrorKindItem = "AetherFrame.ErrorKind";

    public Task<IResult> RunActionAsync(HttpContext http, RequestProofKind kind, Limit? addressLimit, Func<ActionCall, Task<IResult>> handler) =>
        RunActionAsync(http, kind, addressLimit, null, handler);

    /// <summary>
    /// As <see cref="RunActionAsync(HttpContext, RequestProofKind, Limit?, Func{ActionCall, Task{IResult}})"/>,
    /// with the challenge consumed by <paramref name="consume"/> instead of the challenge store when
    /// it is given (a presence start's, from <c>/v1/presence/challenge</c>): a challenge it refuses
    /// is answered <c>409</c> with none, so a refusal never issues one from the general store.
    /// </summary>
    public async Task<IResult> RunActionAsync(HttpContext http, RequestProofKind kind, Limit? addressLimit, Func<RequestChallenge, bool>? consume, Func<ActionCall, Task<IResult>> handler)
    {
        if (addressLimit is not null && !limiter.TryTakeAddress(addressLimit, http.Connection.RemoteIpAddress))
        {
            return Fail(http, StatusCodes.Status429TooManyRequests, "limit:" + addressLimit.Name);
        }

        var (body, bodyFailure) = await ReadBoundedAsync(http, MaxActionRequestBytes, http.RequestAborted);
        if (body is null)
        {
            return Fail(http, bodyFailure, "body:unread");
        }

        var (action, refusal) = await VerifyAsync(body, kind, http.Connection.RemoteIpAddress, consume, http.RequestAborted);
        if (action is null)
        {
            return refusal!.ToResult(http);
        }

        return await handler(new ActionCall(http, action));
    }

    /// <summary>
    /// Checks a signed action's body, read whole: the envelope, then the proof for
    /// <paramref name="kind"/>, then its challenge consumed. The action, or the answer that refuses
    /// it: <c>400</c> for the envelope, <c>413</c> or <c>403</c> for the proof, and <c>409</c> with a
    /// fresh challenge (or <c>429</c>) for the challenge.
    /// </summary>
    public Task<(VerifiedAction? Action, ActionAnswer? Refusal)> VerifyAsync(byte[] body, RequestProofKind kind, IPAddress? address, CancellationToken cancellation) =>
        VerifyAsync(body, kind, address, null, cancellation);

    private async Task<(VerifiedAction? Action, ActionAnswer? Refusal)> VerifyAsync(byte[] body, RequestProofKind kind, IPAddress? address, Func<RequestChallenge, bool>? consume, CancellationToken cancellation)
    {
        if (!TrySplit(body, out var proof, out var payload))
        {
            return (null, ActionAnswer.Fail(StatusCodes.Status400BadRequest, "body:envelope"));
        }

        VerifiedAction action;
        try
        {
            action = RequestProofCodec.VerifyAction(proof.Span, payload.Span, options.Value.Deployment, kind);
        }
        catch (ProtocolException e)
        {
            return (null, ActionAnswer.Fail(e.Error == ProtocolError.LimitExceeded ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status403Forbidden, "proof:" + e.Error));
        }

        if (consume is not null)
        {
            if (!consume(action.Challenge))
            {
                return (null, ActionAnswer.Fail(StatusCodes.Status409Conflict, "challenge:presence"));
            }
        }
        else if (!await challenges.TryConsumeAsync(action.Challenge, cancellation))
        {
            return (null, await RefuseChallengeAsync(address, cancellation));
        }

        return (action, null);
    }

    /// <summary>
    /// Reads the request body, refusing any that is over <paramref name="bound"/> bytes before
    /// buffering more than the bound: Kestrel's own limit is set to the bound first, and the read
    /// stops at it whatever the request's framing says. A refusal is null with its status: 413 for a
    /// body over the bound, 400 for one Kestrel can't read.
    /// </summary>
    public static async Task<(byte[]? Body, int Failure)> ReadBoundedAsync(HttpContext http, int bound, CancellationToken cancellation)
    {
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = bound;
        }

        if (http.Request.ContentLength > bound)
        {
            return (null, StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, cancellation)) > 0)
            {
                if (buffer.Length + read > bound)
                {
                    return (null, StatusCodes.Status413PayloadTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException e)
        {
            return (null, e.StatusCode == StatusCodes.Status413PayloadTooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest);
        }

        return (buffer.ToArray(), 0);
    }

    /// <summary>Splits a signed request's body into its proof and its payload (ServerApi-v1.md, section 2).</summary>
    public static bool TrySplit(byte[] body, out ReadOnlyMemory<byte> proof, out ReadOnlyMemory<byte> payload)
    {
        proof = default;
        payload = default;
        if (body.Length < 2)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(body);
        if (length < ProtocolLimits.RequestProofOverheadBytes + 1 || length > ProtocolLimits.MaxRequestProofBytes || body.Length < 2 + length)
        {
            return false;
        }

        proof = body.AsMemory(2, length);
        payload = body.AsMemory(2 + length);
        return true;
    }

    /// <summary>
    /// A refused challenge: 409, with a fresh challenge to sign again under (rule 10). The fresh one
    /// counts against the address's challenge limit like any other, so refusals can't mint them
    /// without limit; over it, the answer is 429 with none.
    /// </summary>
    public async Task<IResult> RefuseChallengeAsync(HttpContext http) =>
        (await RefuseChallengeAsync(http.Connection.RemoteIpAddress, http.RequestAborted)).ToResult(http);

    /// <summary>A status with no body, its kind left for the request log.</summary>
    public static IResult Fail(HttpContext http, int status, string kind)
    {
        http.Items[ErrorKindItem] = kind;
        return Results.StatusCode(status);
    }

    /// <summary>The refusal of a challenge, as an answer: 409 with a fresh challenge, or 429 past the address's challenge limit.</summary>
    private async Task<ActionAnswer> RefuseChallengeAsync(IPAddress? address, CancellationToken cancellation)
    {
        if (!limiter.TryTakeAddress(ServerLimits.ChallengesPerAddress, address))
        {
            return ActionAnswer.Fail(StatusCodes.Status429TooManyRequests, "limit:challenge");
        }

        var fresh = await challenges.IssueAsync(cancellation);
        return new ActionAnswer(StatusCodes.Status409Conflict, "challenge", Challenge: fresh.ToArray());
    }
}
