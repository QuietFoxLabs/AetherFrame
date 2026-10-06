using System;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Hosting;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Presence;
using AetherFrame.Server.Requests;
using AetherFrame.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AetherFrame.Server.Endpoints;

/// <summary>
/// The online count ("The online count" in docs/networking/DecisionRegister.md; ServerApi-v1.md,
/// section 2.4): a signed start for a bound character on the allowlist, then heartbeats and a
/// leave that carry only the session's token. Every answer holds the count and nothing else about
/// anyone: the window's snapshot of it (<see cref="PresenceStore.SnapshotWindow"/>), with a count
/// under <see cref="PresenceStore.Floor"/> answered as 0, "fewer than that". A start signs a
/// challenge from <c>/v1/presence/challenge</c>, never one from <c>/v1/challenge</c>. These paths
/// are their own: <c>/v1/status</c> is unchanged, since older plugins read it strictly. The token
/// travels in the body only, never in a path, query or header, so nothing that logs a request can
/// hold it; and a presence request that succeeds leaves no line in the request log at all
/// (<see cref="UnloggedWhenSuccessful"/>), while one that fails leaves its usual line.
/// </summary>
internal static class PresenceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // A start's challenge comes from here alone, under presence's own limit: presence never takes
        // from the challenges that publishing, looking up and checking from the same network need.
        app.MapPost("/v1/presence/challenge", async (HttpContext http, RateLimiter limiter, PresenceStore presence) =>
        {
            if (!limiter.TryTakeAddress(ServerLimits.PresenceChallengesPerAddress, http.Connection.RemoteIpAddress))
            {
                return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:" + ServerLimits.PresenceChallengesPerAddress.Name);
            }

            var (body, failure) = await SignedRequests.ReadBoundedAsync(http, 0, http.RequestAborted);
            if (body is null)
            {
                return SignedRequests.Fail(http, failure, "body:unread");
            }

            return presence.IssueChallenge() is { } challenge
                ? Results.Bytes(challenge.ToArray(), "application/octet-stream")
                : SignedRequests.Fail(http, StatusCodes.Status503ServiceUnavailable, "presence:full");
        }).WithMetadata(UnloggedWhenSuccessful.Instance);

        app.MapPost("/v1/presence", (HttpContext http, SignedRequests requests, RateLimiter limiter, BindingStore bindings, Allowlist allowlist, PresenceStore presence) =>
        {
            // What the start's challenge admits, taken as it is consumed and weighed by the store
            // when the session would be made: a start signed before a pause, an opt-out or a
            // takeover, and arriving after it, starts nothing (StartResult.Stale), and nor does one
            // whose challenge is too old for the store to still remember such a change
            // (StartResult.Expired), however long the request took on its way here.
            var admission = default(Admission);
            return requests.RunActionAsync(http, RequestProofKind.Presence, ServerLimits.PresenceStartsPerAddress, challenge => presence.TryConsumeChallenge(challenge, out admission), async call =>
            {
                if (ActionBody.Read(call.Action.Body, []) is null)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:json");
                }

                if (!limiter.TryTake(ServerLimits.PresenceStartsPerKey, call.Persona.ToString()))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:presence-start/key");
                }

                var binding = await bindings.FindByPersonaAsync(call.Persona, http.RequestAborted);
                if (binding is null)
                {
                    return await bindings.WasTakenOverAsync(call.Persona, http.RequestAborted)
                        ? SignedRequests.Fail(http, StatusCodes.Status410Gone, "presence:taken-over")
                        : SignedRequests.Fail(http, StatusCodes.Status404NotFound, "presence:not-bound");
                }

                if (!allowlist.Allows(binding.LodestoneId))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status404NotFound, "presence:not-bound");
                }

                var started = presence.Start(call.Persona, binding.LodestoneId, admission);
                return started switch
                {
                    { Result: StartResult.Started, Token: { } token } => Results.Json(new StartAnswer(Convert.ToBase64String(token), PresenceStore.Reported(started.Online)), ServerJson.Options),

                    // The plugin asks for a fresh challenge and signs again: while the character
                    // still shares that passes, and otherwise the binding check refuses it.
                    { Result: StartResult.Stale } => SignedRequests.Fail(http, StatusCodes.Status409Conflict, "presence:stale"),
                    { Result: StartResult.Expired } => SignedRequests.Fail(http, StatusCodes.Status409Conflict, "presence:expired"),
                    _ => SignedRequests.Fail(http, StatusCodes.Status503ServiceUnavailable, "presence:full"),
                };
            });
        }).WithMetadata(UnloggedWhenSuccessful.Instance);

        app.MapPost("/v1/presence/beat", async (HttpContext http, RateLimiter limiter, PresenceStore presence) =>
        {
            var (token, refusal) = await ReadTokenAsync(http, limiter);
            if (token is null)
            {
                return refusal!;
            }

            var (result, online) = presence.Beat(token);
            return result switch
            {
                BeatResult.Counted => Results.Json(new BeatAnswer(PresenceStore.Reported(online)), ServerJson.Options),
                BeatResult.TooSoon => SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:presence/session"),
                _ => SignedRequests.Fail(http, StatusCodes.Status404NotFound, "presence:unknown"),
            };
        }).WithMetadata(UnloggedWhenSuccessful.Instance);

        // Answered the same whether or not the token named a session, so it tells nothing.
        app.MapPost("/v1/presence/leave", async (HttpContext http, RateLimiter limiter, PresenceStore presence) =>
        {
            var (token, refusal) = await ReadTokenAsync(http, limiter);
            if (token is null)
            {
                return refusal!;
            }

            presence.Leave(token);
            return Results.NoContent();
        }).WithMetadata(UnloggedWhenSuccessful.Instance);
    }

    /// <summary>
    /// A heartbeat's or a leave's body: exactly the token's 32 bytes, read after the address group's
    /// presence limit, which no other request takes from. The token, or the answer refusing it.
    /// </summary>
    private static async System.Threading.Tasks.Task<(byte[]? Token, IResult? Refusal)> ReadTokenAsync(HttpContext http, RateLimiter limiter)
    {
        if (!limiter.TryTakeAddress(ServerLimits.PresenceBeatsPerAddress, http.Connection.RemoteIpAddress))
        {
            return (null, SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:" + ServerLimits.PresenceBeatsPerAddress.Name));
        }

        var (body, failure) = await SignedRequests.ReadBoundedAsync(http, PresenceStore.TokenLength, http.RequestAborted);
        if (body is null)
        {
            return (null, SignedRequests.Fail(http, failure, "body:unread"));
        }

        return body.Length == PresenceStore.TokenLength
            ? (body, null)
            : (null, SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:token"));
    }

    internal sealed record StartAnswer(string Session, int Online);

    internal sealed record BeatAnswer(int Online);
}
