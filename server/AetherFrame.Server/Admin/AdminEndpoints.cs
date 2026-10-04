using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AetherFrame.Protocol.Remote;
using AetherFrame.Server.Hosting;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Requests;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Admin;

internal static class AdminEndpoints
{
    private static readonly Limit Requests = new("admin/address", 240, TimeSpan.FromMinutes(1));
    private static readonly Limit Logins = new("admin/login", 20, TimeSpan.FromMinutes(10));
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        MaxDepth = 8,
    };

    public static void UseGuard(WebApplication app, AdminOptions options)
    {
        app.Use(async (http, next) =>
        {
            if (!http.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)) { await next(http); return; }
            if (!options.Enabled) { http.Response.StatusCode = 404; return; }
            var host = http.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value.DeploymentName;
            if (!http.Request.IsHttps || !string.Equals(http.Request.Host.Host, host, StringComparison.OrdinalIgnoreCase) ||
                (http.Request.Host.Port is not null and not 443)) { http.Response.StatusCode = 400; return; }
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
            var limits = http.RequestServices.GetRequiredService<RateLimiter>();
            if (!limits.TryTakeAddress(Requests, http.Connection.RemoteIpAddress)) { http.Response.StatusCode = 429; return; }
            if (http.Request.Method == "POST")
            {
                if (http.Request.Headers.Origin.ToString() != "https://" + host || !http.Request.HasJsonContentType())
                { http.Response.StatusCode = 403; return; }
            }
            await next(http);
        });
    }

    public static void Map(WebApplication app, AdminOptions options)
    {
        if (!options.Enabled) return;
        app.MapGet("/admin", () => Asset("index.html"));
        app.MapGet("/admin/assets/{name}", (string name) => name is "app.css" or "app.js" ? Asset(name) : Results.NotFound());
        app.MapGet("/admin/login", (HttpContext http, RateLimiter limits) =>
            limits.TryTakeAddress(Logins, http.Connection.RemoteIpAddress)
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/admin/" }, [AdminAuthentication.GitHub])
                : Results.StatusCode(429));
        app.MapGet("/admin/api/session", (HttpContext http, IAntiforgery anti) =>
        {
            var actor = AdminAuthentication.Actor(http.User)!;
            return Results.Json(new { id = actor.Id.ToString(CultureInfo.InvariantCulture), actor.Role,
                csrf = anti.GetAndStoreTokens(http).RequestToken, ownerId = options.OwnerGitHubId.ToString(CultureInfo.InvariantCulture) });
        }).RequireAuthorization(AdminAuthentication.Policy);
        app.MapPost("/admin/api/query", QueryAsync).RequireAuthorization(AdminAuthentication.Policy);
        app.MapPost("/admin/api/action", ChangeAsync).RequireAuthorization(AdminAuthentication.Policy);
    }

    private static IResult Asset(string name)
    {
        var stream = typeof(AdminEndpoints).Assembly.GetManifestResourceStream("AetherFrame.Server.Admin.Web." + name);
        if (stream is null) return Results.NotFound();
        return Results.Stream(stream, name.EndsWith(".html", StringComparison.Ordinal) ? "text/html; charset=utf-8" :
            name.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8");
    }

    private static async Task<DeskInput?> ReadAsync(HttpContext http, IAntiforgery anti)
    {
        try { await anti.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { http.Response.StatusCode = 403; return null; }
        var (bytes, failure) = await SignedRequests.ReadBoundedAsync(http, 4096, http.RequestAborted);
        if (bytes is null) { http.Response.StatusCode = failure; return null; }
        try
        {
            var input = JsonSerializer.Deserialize<DeskInput>(bytes, InputJson);
            if (input is null || input.Action is null || input.Reason is null || input.Profile is null || input.Marker is null ||
                input.Token is null || input.Filter is null || input.Page is < 0 or > 10000) throw new JsonException();
            return input;
        }
        catch (JsonException) { http.Response.StatusCode = 400; return null; }
    }

    private static async Task<IResult> QueryAsync(HttpContext http, AdminStore store, IAntiforgery anti, ServerHealth health)
    {
        var input = await ReadAsync(http, anti);
        if (input is null) return Results.StatusCode(http.Response.StatusCode);
        var ct = http.RequestAborted;
        if (input.Action == "overview") return Results.Json(new { counts = await store.OverviewAsync(ct), health = health.Current });
        if (input.Action is "reports" or "plates" or "audit" or "staff")
        {
            if (input.Action == "staff" && !http.User.IsInRole("owner")) return Results.StatusCode(403);
            if (input.Filter is not "" and not "hidden") return Results.BadRequest();
            return Results.Json(await store.ListAsync(input.Action, input.Page, input.Filter, ct));
        }
        if (input.Action == "detail")
        {
            var row = await store.DetailAsync(input.Profile, ct);
            if (row is null) return Results.NotFound();
            var bytes = row["served"] as byte[];
            row.Remove("served");
            if (bytes is not null)
            {
                var profile = ServedProfile.Read(bytes);
                row["plate"] = new
                {
                    profile.Name, profile.CanvasWidth, profile.CanvasHeight, profile.Background,
                    backgroundImage = profile.ImageIndexOf(profile.Background.ImageAssetId),
                    items = profile.Items.Select(i => new { kind = i.Kind.ToString(), data = (object)i,
                        image = i switch { LayoutImage image => profile.ImageIndexOf(image.AssetId),
                            LayoutImageQuad quad => profile.ImageIndexOf(quad.AssetId), _ => -1 } }),
                    images = profile.Images.Select((image, index) => new { index, image.Width, image.Height }),
                };
            }
            return Results.Json(row);
        }
        if (input.Action == "image")
        {
            var image = await store.ImageAsync(input.Profile, input.Marker, input.Image, ct);
            return image is null ? Results.NotFound() : Results.Bytes(image.Bytes, image.Format switch { ImageFormat.Png => "image/png", ImageFormat.WebP => "image/webp", _ => "image/jpeg" });
        }
        return Results.BadRequest();
    }

    private static async Task<IResult> ChangeAsync(HttpContext http, AdminStore store, IAntiforgery anti)
    {
        var input = await ReadAsync(http, anti);
        if (input is null) return Results.StatusCode(http.Response.StatusCode);
        if (input.Action == "logout")
        {
            await http.SignOutAsync(AdminAuthentication.Cookie);
            return Results.NoContent();
        }
        return Results.StatusCode(await store.ChangeAsync(AdminAuthentication.Actor(http.User)!, input, http.RequestAborted));
    }
}
