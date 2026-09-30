using System;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Hosting;
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
/// The unsigned requests, and the four actions about the signer's own character: a code, a check,
/// a re-read and opting out (ServerApi-v1.md; decisions C1, C2, C4 and C6).
/// </summary>
internal static class CharacterEndpoints
{
    private static readonly string[] CheckFields = ["lodestoneId", "code"];

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/status", (IOptions<ServerOptions> options) => Results.Json(
            new StatusAnswer(ProtocolConstants.ProtocolVersion, 1, options.Value.MinimumPlugin), ServerJson.Options));

        app.MapPost("/v1/challenge", async (HttpContext http, ChallengeStore challenges, RateLimiter limiter) =>
        {
            if (!limiter.TryTakeAddress(ServerLimits.ChallengesPerAddress, http.Connection.RemoteIpAddress))
            {
                return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:challenge");
            }

            var challenge = await challenges.IssueAsync(http.RequestAborted);
            return Results.Bytes(challenge.ToArray(), "application/octet-stream");
        });

        app.MapPost("/v1/lodestone/code", (HttpContext http, SignedRequests requests, RateLimiter limiter, BindingStore bindings) =>
            requests.RunActionAsync(http, RequestProofKind.LodestoneCode, ServerLimits.LodestonePerAddress, async call =>
            {
                if (ActionBody.Read(call.Action.Body, []) is null)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:json");
                }

                if (!limiter.TryTake(ServerLimits.LodestonePerKey, call.Persona.ToString()))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:lodestone/key");
                }

                var code = await bindings.IssueCodeAsync(call.Persona, http.RequestAborted);
                return Results.Json(new CodeAnswer(code, (int)BindingStore.CodeLifetime.TotalSeconds), ServerJson.Options);
            }));

        app.MapPost("/v1/lodestone/check", (HttpContext http, SignedRequests requests, RateLimiter limiter, BindingStore bindings, Allowlist allowlist, LodestoneReader lodestone) =>
            requests.RunActionAsync(http, RequestProofKind.LodestoneCheck, ServerLimits.LodestonePerAddress, async call =>
            {
                var body = ActionBody.Read(call.Action.Body, CheckFields);
                if (body is null || !LodestoneIds.TryParse(body.String("lodestoneId"), out var lodestoneId) || !LodestoneCodes.IsWellFormed(body.String("code")))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:json");
                }

                if (!limiter.TryTake(ServerLimits.LodestonePerKey, call.Persona.ToString()) || !TakeCheckForId(limiter, lodestoneId, call))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:lodestone");
                }

                // Every failure below is the same "check failed" (decision C2); only the log's kind differs.
                var code = body.String("code");
                if (!await bindings.HasCodeAsync(call.Persona, code, http.RequestAborted))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status422UnprocessableEntity, "check:code");
                }

                if (!allowlist.Allows(lodestoneId))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status422UnprocessableEntity, "check:allowlist");
                }

                if (await bindings.FindByPersonaAsync(call.Persona, http.RequestAborted) is { } own && own.LodestoneId != lodestoneId)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status422UnprocessableEntity, "check:second-character");
                }

                var read = await lodestone.ReadAsync(lodestoneId, reread: false, http.RequestAborted);
                if (read.Outcome == LodestoneOutcome.Busy)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status503ServiceUnavailable, "check:busy");
                }

                var character = read.Character;
                if (character is null || !character.SelfIntroduction.Contains(code, StringComparison.Ordinal))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status422UnprocessableEntity, "check:page");
                }

                var bound = await bindings.BindCharacterAsync(call.Persona, lodestoneId, character, http.RequestAborted);
                if (bound is null)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status422UnprocessableEntity, "check:second-character");
                }

                return Results.Json(new CheckAnswer(bound.ProfileId.ToString(), character.Name, character.World), ServerJson.Options);
            }));

        app.MapPost("/v1/lodestone/reread", (HttpContext http, SignedRequests requests, RateLimiter limiter, BindingStore bindings, Allowlist allowlist, LodestoneReader lodestone) =>
            requests.RunActionAsync(http, RequestProofKind.LodestoneReread, ServerLimits.LodestonePerAddress, async call =>
            {
                if (ActionBody.Read(call.Action.Body, []) is null)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:json");
                }

                if (!limiter.TryTake(ServerLimits.LodestonePerKey, call.Persona.ToString()))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:lodestone/key");
                }

                var binding = await bindings.FindByPersonaAsync(call.Persona, http.RequestAborted);
                if (binding is null || !allowlist.Allows(binding.LodestoneId))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status404NotFound, "reread:not-bound");
                }

                var read = await lodestone.ReadAsync(binding.LodestoneId, reread: false, http.RequestAborted);
                if (read.Outcome is LodestoneOutcome.Busy or LodestoneOutcome.Failed)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status503ServiceUnavailable, "reread:" + read.Outcome);
                }

                var result = await bindings.ApplyRereadAsync(call.Persona, read.Character, http.RequestAborted);
                if (result is RereadResult.Removed or RereadResult.NotBound)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status404NotFound, "reread:" + result);
                }

                var current = await bindings.FindByPersonaAsync(call.Persona, http.RequestAborted);
                return current is null
                    ? SignedRequests.Fail(http, StatusCodes.Status404NotFound, "reread:not-bound")
                    : Results.Json(new RereadAnswer(current.Name, current.World), ServerJson.Options);
            }));

        app.MapPost("/v1/opt-out", (HttpContext http, SignedRequests requests, RateLimiter limiter, BindingStore bindings) =>
            requests.RunActionAsync(http, RequestProofKind.OptOut, null, async call =>
            {
                if (ActionBody.Read(call.Action.Body, []) is null)
                {
                    return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:json");
                }

                if (!limiter.TryTake(ServerLimits.OptOutsPerKey, call.Persona.ToString()))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:opt-out/key");
                }

                await bindings.OptOutAsync(call.Persona, http.RequestAborted);
                return Results.NoContent();
            }));
    }

    /// <summary>C6's "10 a day per Lodestone id and address range", counted for each of the address's groups.</summary>
    private static bool TakeCheckForId(RateLimiter limiter, long lodestoneId, ActionCall call)
    {
        foreach (var (group, multiple) in AddressGroups.Of(call.Address))
        {
            if (!limiter.TryTake(ServerLimits.ChecksPerLodestoneId, lodestoneId + "|" + group, multiple))
            {
                return false;
            }
        }

        return true;
    }

    internal sealed record StatusAnswer(int ProtocolVersion, int Api, string MinimumPlugin);

    internal sealed record CodeAnswer(string Code, int ExpiresInSeconds);

    internal sealed record CheckAnswer(string ProfileId, string Name, string World);

    internal sealed record RereadAnswer(string Name, string World);
}
