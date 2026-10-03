using AetherFrame.Protocol;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Hosting;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Requests;
using AetherFrame.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Endpoints;

/// <summary>
/// The unsigned requests, and the four actions about the signer's own character: a code, a check,
/// a re-read and opting out (ServerApi-v1.md; decisions C1, C2, C4 and C6). The check and the re-read
/// are <see cref="LodestoneActions"/>, as a <c>POST</c> or as a WebSocket (<see cref="LodestoneSockets"/>).
/// </summary>
internal static class CharacterEndpoints
{
    private static readonly string[] PauseFields = ["mode"];

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

            var (body, failure) = await SignedRequests.ReadBoundedAsync(http, 0, http.RequestAborted);
            if (body is null)
            {
                return SignedRequests.Fail(http, failure, "body:unread");
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

        // The check and the re-read (C1, C2): as a POST, the page is read by the server's own client,
        // through the operator's relay when one is set; as a WebSocket at the same path (a GET that
        // upgrades), through the player's own connection (ServerApi-v1.md, section 2.3).
        app.MapPost("/v1/lodestone/check", (HttpContext http, SignedRequests requests, LodestoneActions actions) =>
            requests.RunActionAsync(http, RequestProofKind.LodestoneCheck, ServerLimits.LodestonePerAddress, async call =>
                (await actions.CheckAsync(call.Action, call.Address, null, http.RequestAborted)).ToResult(http)));

        app.MapGet("/v1/lodestone/check", (HttpContext http, LodestoneSockets sockets) => sockets.RunAsync(http, RequestProofKind.LodestoneCheck));

        app.MapPost("/v1/lodestone/reread", (HttpContext http, SignedRequests requests, LodestoneActions actions) =>
            requests.RunActionAsync(http, RequestProofKind.LodestoneReread, ServerLimits.LodestonePerAddress, async call =>
                (await actions.RereadAsync(call.Action, null, http.RequestAborted)).ToResult(http)));

        app.MapGet("/v1/lodestone/reread", (HttpContext http, LodestoneSockets sockets) => sockets.RunAsync(http, RequestProofKind.LodestoneReread));

        // Opting out (C4) is {}; pausing (C3) is {"mode":"pause"}: the Plate is deleted and the
        // binding kept, so the next publish shares again without a new check.
        app.MapPost("/v1/opt-out", (HttpContext http, SignedRequests requests, RateLimiter limiter, BindingStore bindings, ContentStore content) =>
            requests.RunActionAsync(http, RequestProofKind.OptOut, ServerLimits.OptOutsPerAddress, async call =>
            {
                var pause = ActionBody.Read(call.Action.Body, PauseFields);
                if (ActionBody.Read(call.Action.Body, []) is null && pause?.String("mode") != "pause")
                {
                    return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "body:json");
                }

                if (!limiter.TryTake(ServerLimits.OptOutsPerKey, call.Persona.ToString()))
                {
                    return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:opt-out/key");
                }

                if (pause is not null)
                {
                    await content.PauseAsync(call.Persona, http.RequestAborted);
                }
                else
                {
                    await bindings.OptOutAsync(call.Persona, http.RequestAborted);
                }

                return Results.NoContent();
            }));
    }

    internal sealed record StatusAnswer(int ProtocolVersion, int Api, string MinimumPlugin);

    internal sealed record CodeAnswer(string Code, int ExpiresInSeconds);

    internal sealed record CheckAnswer(string ProfileId, string Name, string World);

    internal sealed record RereadAnswer(string Name, string World);
}
