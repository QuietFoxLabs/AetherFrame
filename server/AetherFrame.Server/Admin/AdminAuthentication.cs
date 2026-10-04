using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AetherFrame.Server.Admin;

/// <summary>Platform OAuth with PKCE and correlation, followed by a short, non-sliding staff cookie.</summary>
internal static class AdminAuthentication
{
    public const string Cookie = "CommunityDesk";
    public const string GitHub = "CommunityDeskGitHub";
    public const string Policy = "CommunityDeskStaff";
    private const string GenerationClaim = "af:staff-generation";

    public static void Add(IServiceCollection services, AdminOptions options)
    {
        // No new on-disk credential store. A restart invalidates all staff sessions and pending logins.
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-AF-CSRF";
            o.Cookie.Name = "__Host-AF-CSRF";
            o.Cookie.Path = "/";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddAuthorization(o => o.AddPolicy(Policy, p => p.AddAuthenticationSchemes(Cookie).RequireAuthenticatedUser()));
        services.AddAuthentication(Cookie)
            .AddCookie(Cookie, o =>
            {
                o.Cookie.Name = "__Host-AF-Staff";
                o.Cookie.Path = "/";
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromHours(4);
                o.SlidingExpiration = false;
                o.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; },
                    OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; },
                    OnValidatePrincipal = async c =>
                    {
                        var actor = Actor(c.Principal);
                        var current = actor is null ? null : await c.HttpContext.RequestServices.GetRequiredService<AdminStore>()
                            .FindStaffAsync(actor.Id, c.HttpContext.RequestAborted);
                        if (actor is null || current != actor)
                        {
                            c.RejectPrincipal();
                            await c.HttpContext.SignOutAsync(Cookie);
                        }
                    },
                };
            })
            .AddOAuth(GitHub, o =>
            {
                o.SignInScheme = Cookie;
                o.ClientId = options.ClientId;
                o.ClientSecret = options.ClientSecret;
                o.CallbackPath = "/admin/signin-github";
                o.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
                o.TokenEndpoint = "https://github.com/login/oauth/access_token";
                o.UserInformationEndpoint = "https://api.github.com/user";
                o.UsePkce = true;
                o.SaveTokens = false;
                o.Scope.Clear();
                o.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
                o.BackchannelTimeout = TimeSpan.FromSeconds(10);
                o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                o.CorrelationCookie.HttpOnly = true;
                o.CorrelationCookie.SameSite = SameSiteMode.Lax;
                o.Events.OnCreatingTicket = async c =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, c.Options.UserInformationEndpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.AccessToken);
                    request.Headers.UserAgent.ParseAdd("AetherFrame-CommunityDesk/1");
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using var response = await c.Backchannel.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, c.HttpContext.RequestAborted);
                    response.EnsureSuccessStatusCode();
                    await response.Content.LoadIntoBufferAsync(65536, c.HttpContext.RequestAborted);
                    using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync(c.HttpContext.RequestAborted));
                    var id = user.RootElement.GetProperty("id").GetInt64();
                    var actor = await c.HttpContext.RequestServices.GetRequiredService<AdminStore>().FindStaffAsync(id, c.HttpContext.RequestAborted);
                    if (actor is null)
                    {
                        throw new AuthenticationFailureException("Staff access is required.");
                    }
                    c.Identity!.AddClaim(new Claim(ClaimTypes.NameIdentifier, id.ToString(CultureInfo.InvariantCulture)));
                    c.Identity.AddClaim(new Claim(ClaimTypes.Role, actor.Role));
                    c.Identity.AddClaim(new Claim(GenerationClaim, actor.Generation));
                    c.Properties.IsPersistent = false;
                };
                o.Events.OnRemoteFailure = c =>
                {
                    c.HandleResponse();
                    c.Response.Redirect("/admin/?login=failed");
                    return Task.CompletedTask;
                };
            });
    }

    internal static StaffActor? Actor(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true ||
            !long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return null;
        var role = principal.FindFirstValue(ClaimTypes.Role);
        var generation = principal.FindFirstValue(GenerationClaim);
        return role is "owner" or "moderator" && generation is not null ? new(id, role, generation) : null;
    }
}
