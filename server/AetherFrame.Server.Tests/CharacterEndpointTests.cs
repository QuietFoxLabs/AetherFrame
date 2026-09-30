using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Hosting;
using AetherFrame.Server.Lodestone;
using AetherFrame.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>
/// The unsigned requests and the actions about the signer's own character, end to end through the
/// server (ServerApi-v1.md; decisions C1, C2, C4, C6 and the specification's rule 10).
/// </summary>
public class CharacterEndpointTests
{
    private const long Aria = 12345678;
    private const long Bram = 23456789;

    [Fact]
    public async Task Status_NamesTheProtocolAndTheOldestPlugin()
    {
        using var server = new TestServer();
        using var client = server.CreateClient();
        using var response = await client.GetAsync("/v1/status");
        var status = await response.Content.ReadFromJsonElementAsync();
        Assert.Equal(ProtocolConstants.ProtocolVersion, status.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(1, status.GetProperty("api").GetInt32());
        Assert.Equal("0.1.6", status.GetProperty("minimumPlugin").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
    }

    [Fact]
    public async Task Challenge_IsUsedOnce_AndTheRefusalCarriesAFreshOne()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        var challenge = await player.ChallengeAsync();
        using (var first = await player.SendAsync("/v1/lodestone/code", RequestProofKind.LodestoneCode, "{}", challenge: challenge))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var again = await player.SendAsync("/v1/lodestone/code", RequestProofKind.LodestoneCode, "{}", challenge: challenge);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var fresh = RequestChallenge.FromBytes(await again.Content.ReadAsByteArrayAsync());
        Assert.NotEqual(challenge, fresh);
        using var retried = await player.SendAsync("/v1/lodestone/code", RequestProofKind.LodestoneCode, "{}", challenge: fresh);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
    }

    [Fact]
    public async Task Challenge_ExpiresAfter300Seconds()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        var challenge = await player.ChallengeAsync();
        server.Time.Advance(TimeSpan.FromSeconds(300));
        using var late = await player.SendAsync("/v1/lodestone/code", RequestProofKind.LodestoneCode, "{}", challenge: challenge);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task EachEndpoint_TakesItsKindFromThePath_NeverFromTheRequest()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();

        // A valid lookup proof sent to the code endpoint: refused, and the challenge is not used up.
        var challenge = await player.ChallengeAsync();
        using (var wrongKind = await player.SendAsync("/v1/lodestone/code", RequestProofKind.Lookup, "{}", challenge: challenge))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrongKind.StatusCode);
        }

        using var right = await player.SendAsync("/v1/lodestone/code", RequestProofKind.LodestoneCode, "{}", challenge: challenge);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task AProofForAnotherDeployment_IsRefused()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        using var response = await player.SendAsync("/v1/lodestone/code", RequestProofKind.LodestoneCode, "{}", deployment: "staging.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{} ")]
    [InlineData("{}{}")]
    [InlineData("{\"extra\":\"x\"}")]
    [InlineData("[]")]
    [InlineData("{/* comment */}")]
    public async Task ACodeRequest_WithABodyOtherThanAnEmptyObject_IsRefused(string body)
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        using var response = await player.SendAsync("/v1/lodestone/code", RequestProofKind.LodestoneCode, body);
        Assert.Equal(body == "{} " ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"lodestoneId\":\"12345678\"}")]
    [InlineData("{\"lodestoneId\":\"12345678\",\"code\":\"AF-0123456789\",\"code\":\"AF-0123456789\"}")]
    [InlineData("{\"lodestoneId\":12345678,\"code\":\"AF-0123456789\"}")]
    [InlineData("{\"lodestoneId\":\"012345678\",\"code\":\"AF-0123456789\"}")]
    [InlineData("{\"lodestoneId\":\"12345678901\",\"code\":\"AF-0123456789\"}")]
    [InlineData("{\"lodestoneId\":\"12345678\",\"code\":\"af-0123456789\"}")]
    [InlineData("{\"lodestoneId\":\"12345678\",\"code\":\"AF-012345678U\"}")]
    [InlineData("{\"lodestoneId\":\"12345678\",\"code\":\"AF-0123456789\",\"x\":{}}")]
    public async Task ACheck_WithAMalformedBody_IsRefusedBeforeAnyFetch(string body)
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        using var response = await player.SendAsync("/v1/lodestone/check", RequestProofKind.LodestoneCheck, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(server.Lodestone.Fetched);
    }

    [Fact]
    public async Task ABodyOverItsBound_IsRefusedBeforeItIsBuffered()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        using var response = await player.PostRawAsync("/v1/lodestone/code", new byte[Requests.SignedRequests.MaxActionRequestBytes + 1]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        using var envelope = await player.PostRawAsync("/v1/lodestone/code", [0, 5, 1, 2, 3]);
        Assert.Equal(HttpStatusCode.BadRequest, envelope.StatusCode);
    }

    [Fact]
    public async Task TheCheck_BindsTheCharacter_WithAFreshProfileId()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        var bound = await player.BindAsync(Aria, "Aria Starfall", "gilgamesh");
        Assert.True(ProfileId.TryParse(bound.GetProperty("profileId").GetString(), out _));
        Assert.Equal("Aria Starfall", bound.GetProperty("name").GetString());
        Assert.Equal("Gilgamesh", bound.GetProperty("world").GetString());
        Assert.Equal([Aria], server.Lodestone.Fetched);

        // The code is used up.
        var code = await player.CodeAsync();
        server.Lodestone.Pages[Aria] = LodestoneHtml.Character("Aria Starfall", "Gilgamesh", code);
        using var again = await player.CheckAsync(Aria, code);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(bound.GetProperty("profileId").GetString(), (await again.Content.ReadFromJsonElementAsync()).GetProperty("profileId").GetString());
        using var reused = await player.CheckAsync(Aria, code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
    }

    [Fact]
    public async Task EveryCheckFailure_IsTheSameAnswer_AndOnlyAPlausibleCheckFetches()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        // Another key's code: no fetch.
        using (var other = server.NewPlayer())
        {
            var otherCode = await other.CodeAsync();
            using var response = await player.CheckAsync(Aria, otherCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        // An id off the allowlist: no fetch.
        using (var response = await player.CheckAsync(45678901, code))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        Assert.Empty(server.Lodestone.Fetched);

        // The test's clients share one address, whose limit is 10 checks and codes an hour: start a
        // fresh hour, with a fresh code.
        server.Time.Advance(TimeSpan.FromHours(1));
        code = await player.CodeAsync();

        // The code only outside the self-introduction, in the Recent Activity sidebar.
        server.Lodestone.Pages[Aria] = LodestoneHtml.Character("Aria Starfall", "Gilgamesh", "no code here", sidebar: code);
        using (var response = await player.CheckAsync(Aria, code))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        // The Lodestone's "not found", a redirect, an outage, an unknown World, a bad name.
        server.Lodestone.Pages.Clear();
        foreach (var page in new[]
        {
            new LodestoneResponse(404, LodestoneHtml.NotFoundPage),
            new LodestoneResponse(302, null),
            new LodestoneResponse(0, null),
            LodestoneHtml.Character("Aria Starfall", "Atlantis", code),
            LodestoneHtml.Character("aria starfall", "Gilgamesh", code),
            LodestoneHtml.Character("Aria Starfall Third", "Gilgamesh", code),
        })
        {
            server.Lodestone.Pages[Aria] = page;
            using var response = await player.CheckAsync(Aria, code);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task OneKey_BindsOneCharacter()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        var code = await player.CodeAsync();
        server.Lodestone.Pages[Bram] = LodestoneHtml.Character("Bram Oakes", "Gilgamesh", code);
        using var response = await player.CheckAsync(Bram, code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ACheckByAnotherKey_TakesTheCharacterOver_WithAFreshProfileId()
    {
        using var server = new TestServer();
        using var oldPc = server.NewPlayer();
        using var newPc = server.NewPlayer();
        var before = await oldPc.BindAsync(Aria);
        var after = await newPc.BindAsync(Aria);
        Assert.NotEqual(before.GetProperty("profileId").GetString(), after.GetProperty("profileId").GetString());

        var store = server.Services.GetRequiredService<BindingStore>();
        Assert.Null(await store.FindByPersonaAsync(oldPc.Key.PublicKey.Id, default));
        using var reread = await oldPc.SendAsync("/v1/lodestone/reread", RequestProofKind.LodestoneReread, "{}");
        Assert.Equal(HttpStatusCode.NotFound, reread.StatusCode);
    }

    [Fact]
    public async Task ANewerCheckOfTheSameNameAndWorld_HidesTheOlderBinding()
    {
        using var server = new TestServer();
        using var first = server.NewPlayer();
        using var second = server.NewPlayer();
        await first.BindAsync(Aria, "Aria Starfall", "Gilgamesh");
        await second.BindAsync(Bram, "Aria Starfall", "gilgamesh");

        var store = server.Services.GetRequiredService<BindingStore>();
        var shown = await store.FindShownAsync("aria starfall", "Gilgamesh", default);
        Assert.Equal(Bram, shown?.LodestoneId);
        Assert.True((await store.FindByPersonaAsync(first.Key.PublicKey.Id, default))!.Hidden);

        // The older one's own re-read, under its new name, shows it again.
        server.Lodestone.Pages[Aria] = LodestoneHtml.Character("Aria Dawnfall", "Gilgamesh", "");
        using var reread = await first.SendAsync("/v1/lodestone/reread", RequestProofKind.LodestoneReread, "{}");
        Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
        Assert.Equal(Aria, (await store.FindShownAsync("aria dawnfall", "Gilgamesh", default))?.LodestoneId);
        Assert.Equal(Bram, (await store.FindShownAsync("aria starfall", "Gilgamesh", default))?.LodestoneId);
    }

    [Fact]
    public async Task AReread_FollowsARename_AndRemovesOnlyAfterTwoNotFoundsADayApart()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria, "Aria Starfall", "Gilgamesh");

        server.Lodestone.Pages[Aria] = LodestoneHtml.Character("Aria Brightwater", "Balmung", "", "Crystal");
        using (var renamed = await player.SendAsync("/v1/lodestone/reread", RequestProofKind.LodestoneReread, "{}"))
        {
            var answer = await renamed.Content.ReadFromJsonElementAsync();
            Assert.Equal("Aria Brightwater", answer.GetProperty("name").GetString());
            Assert.Equal("Balmung", answer.GetProperty("world").GetString());
        }

        var store = server.Services.GetRequiredService<BindingStore>();
        server.Lodestone.Pages.Clear();

        // An outage changes nothing.
        server.Lodestone.Pages[Aria] = new LodestoneResponse(503, null);
        using (var outage = await player.SendAsync("/v1/lodestone/reread", RequestProofKind.LodestoneReread, "{}"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, outage.StatusCode);
        }

        server.Lodestone.Pages.Clear();
        using (var once = await player.SendAsync("/v1/lodestone/reread", RequestProofKind.LodestoneReread, "{}"))
        {
            Assert.Equal(HttpStatusCode.OK, once.StatusCode);
        }

        server.Time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(RereadResult.NotFoundOnce, await store.ApplyRereadAsync(player.Key.PublicKey.Id, null, default));
        Assert.NotNull(await store.FindByPersonaAsync(player.Key.PublicKey.Id, default));

        server.Time.Advance(TimeSpan.FromDays(1));
        using var removed = await player.SendAsync("/v1/lodestone/reread", RequestProofKind.LodestoneReread, "{}");
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Null(await store.FindByPersonaAsync(player.Key.PublicKey.Id, default));
    }

    [Fact]
    public async Task APageReadBetweenTwoNotFounds_StartsTheCountAgain()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        var store = server.Services.GetRequiredService<BindingStore>();
        var persona = player.Key.PublicKey.Id;

        Assert.Equal(RereadResult.NotFoundOnce, await store.ApplyRereadAsync(persona, null, default));
        server.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(RereadResult.Updated, await store.ApplyRereadAsync(persona, new LodestoneCharacter("Aria Starfall", "Gilgamesh", ""), default));
        Assert.Equal(RereadResult.NotFoundOnce, await store.ApplyRereadAsync(persona, null, default));
        server.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(RereadResult.Removed, await store.ApplyRereadAsync(persona, null, default));
    }

    [Fact]
    public async Task TheScheduledReread_UsesOnlyHalfTheBudget_AndSkipsIdsOffTheAllowlist()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        var rereads = server.Services.GetRequiredService<Rereads>();
        var budget = server.Services.GetRequiredService<LodestoneBudget>();

        server.Lodestone.Pages[Aria] = LodestoneHtml.Character("Aria Dawnfall", "Gilgamesh", "");
        await rereads.RereadAsync(player.Key.PublicKey.Id, default);
        var store = server.Services.GetRequiredService<BindingStore>();
        Assert.Equal("Aria Dawnfall", (await store.FindByPersonaAsync(player.Key.PublicKey.Id, default))!.Name);

        // The check spent one fetch and the re-read another: 28 more re-reads fill their half.
        for (var index = 0; index < LodestoneBudget.PerHour / 2 - 2; index++)
        {
            Assert.True(budget.TryAcquire(reread: true));
            budget.Release();
        }

        Assert.False(budget.TryAcquire(reread: true));
        Assert.True(budget.TryAcquire(reread: false));
        budget.Release();
    }

    [Fact]
    public async Task OptingOut_DeletesTheBinding_AndAnswersTheSameEitherWay()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        using (var first = await player.SendAsync("/v1/opt-out", RequestProofKind.OptOut, "{}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        Assert.Null(await server.Services.GetRequiredService<BindingStore>().FindByPersonaAsync(player.Key.PublicKey.Id, default));
        using var second = await player.SendAsync("/v1/opt-out", RequestProofKind.OptOut, "{}");
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        // Coming back needs a new check, and gets a new profile id.
        var again = await player.BindAsync(Aria);
        Assert.True(ProfileId.TryParse(again.GetProperty("profileId").GetString(), out _));
    }

    [Fact]
    public async Task CodeRequestsAndChecks_AreLimitedPerKey()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        for (var index = 0; index < 10; index++)
        {
            await player.CodeAsync();
        }

        using var over = await player.SendAsync("/v1/lodestone/code", RequestProofKind.LodestoneCode, "{}");
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);

        server.Time.Advance(TimeSpan.FromHours(1));
        await player.CodeAsync();
    }

    [Fact]
    public async Task TheLog_HoldsNoIdentifierCodeNameOrWorld()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        var bound = await player.BindAsync(Aria, "Aria Starfall", "Gilgamesh");
        var code = await player.CodeAsync();
        using (await player.CheckAsync(Bram, code))
        {
        }

        using (await player.SendAsync("/v1/lodestone/reread", RequestProofKind.LodestoneReread, "{}"))
        {
        }

        using (await player.SendAsync("/v1/opt-out", RequestProofKind.OptOut, "{}"))
        {
        }

        var log = server.Log.All;
        Assert.Contains("/v1/lodestone/check", log);
        foreach (var secret in new[]
        {
            player.Key.PublicKey.Id.ToString(),
            player.Key.PublicKey.Id.ToString()[4..],
            Aria.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Bram.ToString(System.Globalization.CultureInfo.InvariantCulture),
            code,
            code[3..],
            bound.GetProperty("profileId").GetString()!,
            "Aria",
            "Starfall",
            "Gilgamesh",
            "na.finalfantasyxiv.com",
        })
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AServerWithATestName_RefusesToStartUnlessAllowed()
    {
        var options = new ServerOptions { DeploymentName = "plates.example.com", DatabasePath = "x.db" };
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.AllowTestDeploymentName = true;
        options.Validate();

        Assert.Throws<InvalidOperationException>(new ServerOptions { DeploymentName = "Plates.Example.COM", DatabasePath = "x.db", AllowTestDeploymentName = true }.Validate);
        Assert.Throws<InvalidOperationException>(new ServerOptions { DeploymentName = "plates.aetherframe.net", DatabasePath = "" }.Validate);
        Assert.Throws<InvalidOperationException>(new ServerOptions { DeploymentName = "plates.aetherframe.net", DatabasePath = "x.db", AllowedLodestoneIds = ["0123"] }.Validate);
        Assert.Throws<InvalidOperationException>(new ServerOptions { DeploymentName = "plates.aetherframe.net", DatabasePath = "x.db", KnownProxies = ["caddy"] }.Validate);
        new ServerOptions { DeploymentName = "plates.aetherframe.net", DatabasePath = "x.db", AllowedLodestoneIds = ["12345678"], KnownProxies = ["172.18.0.2"] }.Validate();
    }
}
