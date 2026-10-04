using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Admin;
using AetherFrame.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AetherFrame.Server.Tests;

public sealed class AdminDashboardTests
{
    [Fact]
    public async Task DisabledByDefault_HasNoDashboardOrLogin()
    {
        using var source = new TestServer();
        using var client = source.CreateClient();
        foreach (var path in new[] { "/admin", "/admin/", "/admin/login", "/admin/api/session", "/admin/assets/app.js" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("http://plates.example.com")]
    [InlineData("https://attacker.example")]
    [InlineData("https://plates.example.com:444")]
    public async Task Enabled_RefusesWrongHostOrInsecureOrigin(string origin)
    {
        using var desk = new Desk();
        using var client = desk.Factory.CreateClient(new() { BaseAddress = new Uri(origin), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/admin/")).StatusCode);
    }

    [Fact]
    public async Task AnonymousCanOnlySeeStaticShell_WithRestrictiveHeaders()
    {
        using var desk = new Desk();
        foreach (var path in new[] { "/admin/", "/admin/assets/app.js", "/admin/assets/app.css" })
        {
            using var response = await desk.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.DoesNotContain("unsafe-inline", response.Headers.GetValues("Content-Security-Policy").Single());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await desk.Client.GetAsync("/admin/api/session")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/query") { Content = JsonContent.Create(new { action = "overview" }) };
        request.Headers.Add("Origin", Desk.Origin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await desk.Client.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await desk.Client.GetAsync("/admin/assets/Program.cs")).StatusCode);
    }

    [Fact]
    public async Task OAuth_UsesPkceAndCorrelation_AndRejectsUnknownAccount()
    {
        using var desk = new Desk();
        var options = desk.Factory.Services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get(AdminAuthentication.GitHub);
        Assert.True(options.UsePkce);
        Assert.False(options.SaveTokens);
        Assert.Empty(options.Scope);
        using var login = await desk.Client.GetAsync("/admin/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var query = QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.NotEmpty(query["code_challenge"].ToString());
        Assert.Equal(Desk.Origin + "/admin/signin-github", query["redirect_uri"]);
        Assert.DoesNotContain("scope=repo", login.Headers.Location.ToString());
        using var tampered = await desk.Client.GetAsync("/admin/signin-github?code=test&state=tampered");
        Assert.Equal("/admin/?login=failed", tampered.Headers.Location!.ToString());
        Assert.Equal(0, desk.OAuth.Calls);
        using var unknown = await desk.Client.GetAsync("/admin/signin-github?code=test&state=" + Uri.EscapeDataString(query["state"].ToString()));
        Assert.Equal("/admin/?login=failed", unknown.Headers.Location!.ToString());
        Assert.Equal(2, desk.OAuth.Calls);
        Assert.Equal(HttpStatusCode.Unauthorized, (await desk.Client.GetAsync("/admin/api/session")).StatusCode);
    }

    [Fact]
    public async Task OwnerOAuthLogin_SetsShortSecureSession_WithoutSavingToken()
    {
        using var desk = new Desk();
        desk.OAuth.UserId = 1;
        using var login = await desk.Client.GetAsync("/admin/login");
        var state = QueryHelpers.ParseQuery(login.Headers.Location!.Query)["state"].ToString();
        using var callback = await desk.Client.GetAsync("/admin/signin-github?code=test&state=" + Uri.EscapeDataString(state));
        Assert.Equal("/admin/", callback.Headers.Location!.ToString());
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-AF-Staff=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        var value = cookie.Split(';')[0].Split('=', 2)[1];
        var options = desk.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AdminAuthentication.Cookie);
        var ticket = options.TicketDataFormat.Unprotect(value)!;
        Assert.Empty(ticket.Properties.GetTokens());
        Assert.False(options.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(4), ticket.Properties.ExpiresUtc - ticket.Properties.IssuedUtc);
        Assert.Equal(HttpStatusCode.OK, (await desk.Client.GetAsync("/admin/api/session")).StatusCode);
        using var replay = await desk.Client.GetAsync("/admin/signin-github?code=test&state=" + Uri.EscapeDataString(state));
        Assert.Equal("/admin/?login=failed", replay.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ReadsAndActions_RequireCsrfAndExactOrigin_AndBoundInput()
    {
        using var desk = new Desk();
        await desk.SignInAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync(new { action = "overview" }, csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync(new { action = "overview" }, origin: "https://attacker.example")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await desk.PostAsync(new { action = "overview", secret = "unknown" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await desk.PostAsync(new { action = "reports", page = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await desk.PostAsync(new { action = "overview", reason = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await desk.PostAsync(new { action = "overview", reason = new string('x', 5000) })).StatusCode);
        using var overview = await desk.PostAsync(new { action = "overview" });
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        var text = await overview.Content.ReadAsStringAsync();
        Assert.Contains("\"published\":0", text);
        Assert.DoesNotContain("online", text);
        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public async Task ModeratorCannotManageStaff_RevocationAndRegrantInvalidateOldSession()
    {
        using var desk = new Desk();
        await desk.SignInAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await desk.PostAsync(new { action = "grant", id = "2", reason = "Trusted reviewer" }, true)).StatusCode);
        var moderator = (await desk.Store.FindStaffAsync(2, default))!;
        await desk.SignInAsync(moderator);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync(new { action = "staff" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync(new { action = "grant", id = "3", reason = "Not permitted" }, true)).StatusCode);
        Assert.Equal(204, await desk.Store.ChangeAsync(Desk.Owner, new() { Action = "revoke", Id = 2, Reason = "Access removed" }, default));
        Assert.Equal(HttpStatusCode.Unauthorized, (await desk.Client.GetAsync("/admin/api/session")).StatusCode);
        Assert.Equal(204, await desk.Store.ChangeAsync(Desk.Owner, new() { Action = "grant", Id = 2, Reason = "Access restored" }, default));
        Assert.Equal(403, await desk.Store.ChangeAsync(moderator, new() { Action = "dismiss", Id = 1, Reason = "Old session" }, default));
        desk.SetCookie(moderator);
        Assert.Equal(HttpStatusCode.Unauthorized, (await desk.Client.GetAsync("/admin/api/session")).StatusCode);
        await desk.SignInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await desk.PostAsync(new { action = "revoke", id = "1", reason = "Cannot revoke owner" }, true)).StatusCode);
        Assert.Equal(3, await desk.Source.CountAsync("SELECT count(*) FROM admin_audit;"));
    }

    [Fact]
    public async Task ExpiredTamperedAndLoggedOutSessions_CannotReadData()
    {
        using var desk = new Desk();
        desk.SetCookie(Desk.Owner, expired: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await desk.Client.GetAsync("/admin/api/session")).StatusCode);
        desk.Client.DefaultRequestHeaders.Remove("Cookie");
        desk.Client.DefaultRequestHeaders.Add("Cookie", "__Host-AF-Staff=tampered");
        Assert.Equal(HttpStatusCode.Unauthorized, (await desk.Client.GetAsync("/admin/api/session")).StatusCode);
        await desk.SignInAsync();
        using var logout = await desk.PostAsync(new { action = "logout" }, true);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-AF-Staff=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HideRestore_PreserveOwnership_BlockPublicBytes_AndSurviveRepublish()
    {
        using var desk = new Desk();
        using var author = new Player(desk.Source, desk.Factory.CreateClient());
        using var viewer = new Player(desk.Source, desk.Factory.CreateClient());
        var id = (await author.BindAsync(12345678)).GetProperty("profileId").GetString()!;
        var profile = ProfileId.Parse(id);
        await viewer.BindAsync(23456789, "Bram Oakes");
        var png = Plates.Png(4, 3);
        Assert.Equal(HttpStatusCode.NoContent, (await author.PublishAsync(Plates.Snapshot(profile, "Review this", png), png)).StatusCode);
        using var before = await viewer.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}");
        var served = ServedProfile.Read(await before.Content.ReadAsByteArrayAsync());
        var detail = (await desk.Store.DetailAsync(id, default))!;
        await desk.SignInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await desk.PostAsync(new { action = "hide", profile = id, marker = detail["marker"], held = false, reason = " " }, true)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await desk.PostAsync(new { action = "hide", profile = id, marker = "stale", held = false, reason = "Review complete" }, true)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await desk.PostAsync(new { action = "hide", profile = id, marker = detail["marker"], held = false, reason = "Content requires review" }, true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.SendAsync("/v1/image", RequestProofKind.Image, $"{{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"marker\":\"{served.Marker}\",\"index\":0}}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await desk.PostAsync(new { action = "image", profile = id, marker = detail["marker"], image = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await author.PublishAsync(Plates.Snapshot(profile, "Changed", png), png)).StatusCode);
        Assert.Equal(1, await desk.Source.CountAsync("SELECT count(*) FROM admin_holds;"));
        Assert.Equal(2, await desk.Source.CountAsync("SELECT count(*) FROM bindings;"));
        Assert.Equal(HttpStatusCode.Conflict, (await desk.PostAsync(new { action = "restore", profile = id, marker = detail["marker"], held = true, reason = "Stale review" }, true)).StatusCode);
        detail = (await desk.Store.DetailAsync(id, default))!;
        Assert.Equal(HttpStatusCode.NoContent, (await desk.PostAsync(new { action = "restore", profile = id, marker = detail["marker"], held = true, reason = "Current content accepted" }, true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}")).StatusCode);
        Assert.Equal(2, await desk.Source.CountAsync("SELECT count(*) FROM admin_audit;"));
    }

    [Fact]
    public async Task ReportsUseOpaqueReviewTokens_RejectReusedIds_AndAuditExpires()
    {
        using var desk = new Desk();
        using var author = new Player(desk.Source, desk.Factory.CreateClient());
        await author.BindAsync(12345678);
        await desk.SqlAsync("INSERT INTO reports(lodestone_id,reason,reporter,day,review_token) SELECT lodestone_id,'Spam',persona,20726,'old' FROM bindings;");
        await desk.SignInAsync();
        using var list = await desk.PostAsync(new { action = "reports" });
        var json = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("reporter", json);
        Assert.Equal(HttpStatusCode.NoContent, (await desk.PostAsync(new { action = "dismiss", id = 1, token = "old", reason = "Reviewed" }, true)).StatusCode);
        await desk.SqlAsync("INSERT INTO reports(id,lodestone_id,reason,reporter,day,review_token) SELECT 1,lodestone_id,'New report',persona,20726,'new' FROM bindings;");
        Assert.Equal(HttpStatusCode.Conflict, (await desk.PostAsync(new { action = "dismiss", id = 1, token = "old", reason = "Stale page" }, true)).StatusCode);
        Assert.Equal(1, await desk.Source.CountAsync("SELECT count(*) FROM reports;"));
        desk.Source.Time.Advance(TimeSpan.FromDays(31));
        await desk.Factory.Services.GetRequiredService<ContentStore>().DropExpiredReportsAsync(default);
        Assert.Equal(0, await desk.Source.CountAsync("SELECT count(*) FROM reports;"));
        Assert.Equal(0, await desk.Source.CountAsync("SELECT count(*) FROM admin_audit;"));
    }

    [Fact]
    public async Task OptOutRemovesHoldAndContent_AndUnlinksAuditTarget()
    {
        using var desk = new Desk();
        using var author = new Player(desk.Source, desk.Factory.CreateClient());
        var id = (await author.BindAsync(12345678)).GetProperty("profileId").GetString()!;
        await author.PublishAsync(Plates.Snapshot(ProfileId.Parse(id), "Hello"));
        var detail = (await desk.Store.DetailAsync(id, default))!;
        Assert.Equal(204, await desk.Store.ChangeAsync(Desk.Owner, new() { Action = "hide", Profile = id, Marker = (string)detail["marker"]!, Held = false, Reason = "Review needed" }, default));
        using var removed = await author.SendAsync("/v1/opt-out", RequestProofKind.OptOut, "{}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, await desk.Source.CountAsync("SELECT count(*) FROM admin_holds;"));
        Assert.Equal(0, await desk.Source.CountAsync("SELECT count(*) FROM latest;"));
        Assert.Equal(1, await desk.Source.CountAsync("SELECT count(*) FROM admin_audit WHERE profile_id IS NULL;"));
    }

    [Fact]
    public async Task MigrationPreservesReports_RepairsOldBinaryWrites_AndIsIdempotent()
    {
        using var desk = new Desk();
        using var author = new Player(desk.Source, desk.Factory.CreateClient());
        await author.BindAsync(12345678);
        await desk.SqlAsync("ALTER TABLE reports DROP COLUMN review_token; INSERT INTO reports(lodestone_id,reason,reporter,day) SELECT lodestone_id,'Keep this report',persona,20726 FROM bindings;");
        var db = desk.Factory.Services.GetRequiredService<ServerDatabase>();
        await db.InitializeAsync(default);
        Assert.Equal(1, await desk.Source.CountAsync("SELECT count(*) FROM reports WHERE reason='Keep this report' AND length(review_token)=32;"));
        await db.InitializeAsync(default);
        Assert.Equal(1, await desk.Source.CountAsync("SELECT count(*) FROM reports;"));
        await desk.SqlAsync("UPDATE reports SET review_token='';");
        await db.InitializeAsync(default);
        Assert.Equal(1, await desk.Source.CountAsync("SELECT count(*) FROM reports WHERE length(review_token)=32;"));
    }

    [Fact]
    public async Task LoginAttemptsAreBounded_AndQueryStringsCannotChangeRedirect()
    {
        using var desk = new Desk();
        for (var i = 0; i < 20; i++)
        {
            using var response = await desk.Client.GetAsync("/admin/login?returnUrl=https://attacker.example");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(Desk.Origin + "/admin/signin-github", QueryHelpers.ParseQuery(response.Headers.Location!.Query)["redirect_uri"]);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await desk.Client.GetAsync("/admin/login")).StatusCode);
    }

    private sealed class Desk : IDisposable
    {
        public const string Origin = "https://plates.example.com";
        public static readonly StaffActor Owner = new(1, "owner", "owner");
        public TestServer Source { get; } = new();
        public FakeOAuth OAuth { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }
        public AdminStore Store => Factory.Services.GetRequiredService<AdminStore>();
        private string csrf = "";
        private string cookie = "";
        public Desk()
        {
            Factory = Source.WithWebHostBuilder(b =>
            {
                b.UseSetting("AetherFrame:Admin:Enabled", "true");
                b.UseSetting("AetherFrame:Admin:ClientId", "synthetic-test-client");
                b.UseSetting("AetherFrame:Admin:ClientSecret", "synthetic-test-secret");
                b.UseSetting("AetherFrame:Admin:OwnerGitHubId", "1");
                b.ConfigureServices(s =>
                {
                    s.PostConfigure<CookieAuthenticationOptions>(AdminAuthentication.Cookie, o => o.TimeProvider = TimeProvider.System);
                    s.PostConfigure<OAuthOptions>(AdminAuthentication.GitHub, o => { o.Backchannel = new HttpClient(OAuth); o.TimeProvider = TimeProvider.System; });
                });
            });
            Client = Factory.CreateClient(new() { BaseAddress = new Uri(Origin), AllowAutoRedirect = false, HandleCookies = true });
        }
        public void SetCookie(StaffActor actor, bool expired = false)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Role, actor.Role), new Claim("af:staff-generation", actor.Generation) }, AdminAuthentication.Cookie);
            var options = Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AdminAuthentication.Cookie);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
            { IssuedUtc = DateTimeOffset.UtcNow.AddHours(-1), ExpiresUtc = DateTimeOffset.UtcNow.AddHours(expired ? -0.5 : 3) }, AdminAuthentication.Cookie);
            cookie = "__Host-AF-Staff=" + options.TicketDataFormat.Protect(ticket);
            Client.DefaultRequestHeaders.Remove("Cookie");
            Client.DefaultRequestHeaders.Add("Cookie", cookie);
        }
        public async Task SignInAsync(StaffActor? actor = null)
        {
            SetCookie(actor ?? Owner);
            using var response = await Client.GetAsync("/admin/api/session");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            csrf = (await response.Content.ReadFromJsonElementAsync()).GetProperty("csrf").GetString()!;
        }
        public Task<HttpResponseMessage> PostAsync(object data, bool action = false, bool csrf = true, string origin = Origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/" + (action ? "action" : "query")) { Content = JsonContent.Create(data) };
            request.Headers.Add("Origin", origin);
            if (csrf) request.Headers.Add("X-AF-CSRF", this.csrf);
            return Client.SendAsync(request);
        }
        public async Task SqlAsync(string sql)
        {
            await using var connection = new SqliteConnection("Data Source=" + Source.DatabasePath + ";Pooling=False;Foreign Keys=True");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
        }
        public void Dispose() { Client.Dispose(); Factory.Dispose(); Source.Dispose(); }
    }
    private sealed class FakeOAuth : HttpMessageHandler
    {
        public long UserId { get; set; } = 999;
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var text = request.RequestUri!.AbsolutePath == "/login/oauth/access_token"
                ? "{\"access_token\":\"synthetic-token\",\"token_type\":\"bearer\"}"
                : "{\"id\":" + UserId.ToString(CultureInfo.InvariantCulture) + "}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") });
        }
    }
}
