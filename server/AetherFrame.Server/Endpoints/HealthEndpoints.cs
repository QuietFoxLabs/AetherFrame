using AetherFrame.Server.Hosting;
using AetherFrame.Server.Requests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AetherFrame.Server.Endpoints;

/// <summary>
/// <c>GET /v1/health</c> (ServerApi-v1.md, section 3; known bug 14), for the operator's monitor and
/// never the plugin, whose only background traffic is the online count's heartbeat (decision R2;
/// <see cref="PresenceEndpoints"/>). It answers <c>200</c> with
/// <see cref="HealthAnswer"/>'s three booleans whatever they are, so a server that answers at all is
/// up, and one that answers <c>5xx</c> or nothing isn't. It reads only what
/// <see cref="ServerHealth"/> holds in memory: no database, worker, Lodestone or relay work, and so
/// no limit, like <c>/v1/status</c>. A body is refused before it is buffered, as for a challenge.
/// </summary>
internal static class HealthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/health", async (HttpContext http, ServerHealth health) =>
        {
            var (body, failure) = await SignedRequests.ReadBoundedAsync(http, 0, http.RequestAborted);
            if (body is null)
            {
                return SignedRequests.Fail(http, failure, "body:unread");
            }

            return Results.Json(health.Current, ServerJson.Options);
        });
    }
}
