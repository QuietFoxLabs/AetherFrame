using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Requests;
using AetherFrame.Protocol.Signing;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Presence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>
/// The online count ("The online count" in the decision register; ServerApi-v1.md, section 2.4):
/// a signed start for a bound character, heartbeats and a leave that carry only the token, the
/// count of distinct characters, the expiry, and the limits that keep it apart from everything else.
/// </summary>
public class PresenceTests
{
    private const long Aria = 12345678;
    private const long Bram = 23456789;
    private static readonly string[] Names = ["Aria Starfall", "Bram Oakes", "Cora Vale"];

    [Fact]
    public async Task Status_IsByteForByteWhatOlderPluginsRead()
    {
        using var server = new TestServer();
        using var client = server.CreateClient();
        using var response = await client.GetAsync("/v1/status");
        Assert.Equal($"{{\"protocolVersion\":{ProtocolConstants.ProtocolVersion},\"api\":1,\"minimumPlugin\":\"0.1.9\"}}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OnlyABoundCharacter_StartsASession_AndItCountsAsOne()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using (var unbound = await StartAsync(aria))
        {
            Assert.Equal(HttpStatusCode.NotFound, unbound.StatusCode);
            Assert.Empty(await unbound.Content.ReadAsByteArrayAsync());
        }

        await aria.BindAsync(Aria);
        var (token, online) = await StartedAsync(aria);
        Assert.Equal(PresenceStore.TokenLength, token.Length);
        Assert.Equal(PresenceStore.Reported(1), online);
        Assert.Equal(1, server.Services.GetRequiredService<PresenceStore>().Online());
    }

    [Fact]
    public async Task TheStart_IsItsOwnKind_WithAnEmptyBody()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        await aria.BindAsync(Aria);
        using (var asLookup = await aria.SendAsync("/v1/presence", RequestProofKind.Lookup, "{}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, asLookup.StatusCode);
        }

        using (var withBody = await aria.SendAsync("/v1/presence", RequestProofKind.Presence, "{\"name\":\"Aria Starfall\"}", challenge: await PresenceChallengeAsync(aria)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, withBody.StatusCode);
        }

        // And the presence kind authorizes nothing else.
        using var asOptOut = await aria.SendAsync("/v1/opt-out", RequestProofKind.Presence, "{}");
        Assert.Equal(HttpStatusCode.Forbidden, asOptOut.StatusCode);
    }

    [Fact]
    public async Task TheCount_IsDistinctCharacters_AndANewStartReplacesTheKeysSession()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        await aria.BindAsync(Aria);
        await bram.BindAsync(Bram, "Bram Oakes");
        var (first, _) = await StartedAsync(aria);
        var (_, both) = await StartedAsync(bram);
        Assert.Equal(PresenceStore.Reported(2), both);
        Assert.Equal(2, server.Services.GetRequiredService<PresenceStore>().Online());

        // A reload of Aria's plugin starts again: still one Aria, and her first token is gone.
        var (second, again) = await StartedAsync(aria);
        Assert.Equal(PresenceStore.Reported(2), again);
        Assert.Equal(2, server.Services.GetRequiredService<PresenceStore>().Online());
        server.Time.Advance(TimeSpan.FromSeconds(60));
        using (var old = await BeatAsync(aria, first))
        {
            Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        }

        Assert.Equal(PresenceStore.Reported(2), await BeatOnlineAsync(aria, second));
        Assert.Equal(2, server.Services.GetRequiredService<PresenceStore>().Online());
    }

    [Fact]
    public async Task AHeartbeat_KeepsTheSession_AndASessionWithoutOne_ExpiresAfter180Seconds()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        await aria.BindAsync(Aria);
        await bram.BindAsync(Bram, "Bram Oakes");
        var (ariaToken, _) = await StartedAsync(aria);
        var (bramToken, _) = await StartedAsync(bram);

        // Too soon after the start: refused, and the session left as it was.
        using (var early = await BeatAsync(aria, ariaToken))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, early.StatusCode);
        }

        // Aria beats each minute; Bram stops.
        for (var minute = 0; minute < 3; minute++)
        {
            server.Time.Advance(TimeSpan.FromSeconds(60));
            await BeatOnlineAsync(aria, ariaToken);
        }

        server.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(PresenceStore.Reported(1), await BeatOnlineAsync(aria, ariaToken));
        Assert.Equal(1, server.Services.GetRequiredService<PresenceStore>().Online());
        using var expired = await BeatAsync(bram, bramToken);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }

    [Fact]
    public async Task ASession_LastsAnHourAtMost_ThenTheBindingIsCheckedAgain()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        await aria.BindAsync(Aria);
        var (token, _) = await StartedAsync(aria);
        for (var minute = 1; minute < 60; minute++)
        {
            server.Time.Advance(TimeSpan.FromSeconds(60));
            await BeatOnlineAsync(aria, token);
        }

        server.Time.Advance(TimeSpan.FromSeconds(60));
        using var over = await BeatAsync(aria, token);
        Assert.Equal(HttpStatusCode.NotFound, over.StatusCode);
    }

    [Fact]
    public async Task Leaving_EndsTheSessionAtOnce_AndAnswersTheSameForAnyToken()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        await aria.BindAsync(Aria);
        await bram.BindAsync(Bram, "Bram Oakes");
        var (ariaToken, _) = await StartedAsync(aria);
        await StartedAsync(bram);

        using (var left = await aria.PostRawAsync("/v1/presence/leave", ariaToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }

        using (var unknown = await aria.PostRawAsync("/v1/presence/leave", new byte[PresenceStore.TokenLength]))
        {
            Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);
        }

        Assert.Equal(1, server.Services.GetRequiredService<PresenceStore>().Online());
        server.Time.Advance(TimeSpan.FromSeconds(60));
        using var after = await BeatAsync(aria, ariaToken);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Theory]
    [InlineData("{\"mode\":\"pause\"}")]
    [InlineData("{}")]
    public async Task PausingOrTurningOff_StopsCountingTheCharacterAtOnce(string body)
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        await aria.BindAsync(Aria);
        var (token, _) = await StartedAsync(aria);
        using (var off = await aria.SendAsync("/v1/opt-out", RequestProofKind.OptOut, body))
        {
            Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        }

        Assert.Equal(0, server.Services.GetRequiredService<PresenceStore>().Online());
        server.Time.Advance(TimeSpan.FromSeconds(60));
        using var beat = await BeatAsync(aria, token);
        Assert.Equal(HttpStatusCode.NotFound, beat.StatusCode);
    }

    [Fact]
    public async Task ATakeover_EndsTheOldKeysSession_AndTheOldKeyCantStartAnother()
    {
        using var server = new TestServer();
        using var oldPc = server.NewPlayer();
        using var newPc = server.NewPlayer();
        await oldPc.BindAsync(Aria);
        var (oldToken, _) = await StartedAsync(oldPc);
        await newPc.BindAsync(Aria);
        Assert.Equal(0, server.Services.GetRequiredService<PresenceStore>().Online());

        server.Time.Advance(TimeSpan.FromSeconds(60));
        using (var beat = await BeatAsync(oldPc, oldToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, beat.StatusCode);
        }

        using (var start = await StartAsync(oldPc))
        {
            Assert.Equal(HttpStatusCode.Gone, start.StatusCode);
        }

        var (_, online) = await StartedAsync(newPc);
        Assert.Equal(PresenceStore.Reported(1), online);
        Assert.Equal(1, server.Services.GetRequiredService<PresenceStore>().Online());
    }

    [Fact]
    public async Task ACharacterOffTheAllowlist_StartsNothing()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        await aria.BindAsync(Aria);
        server.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ServerOptions>>().CurrentValue.AllowedLodestoneIds.Clear();
        using var start = await StartAsync(aria);
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task AHeartbeat_IsExactlyTheToken(int length)
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        using var beat = await player.PostRawAsync("/v1/presence/beat", new byte[length]);
        Assert.Equal(length > PresenceStore.TokenLength ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest, beat.StatusCode);
    }

    [Fact]
    public async Task Heartbeats_HaveTheirOwnAddressLimit_AndTakeNothingFromAnyOther()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        await aria.BindAsync(Aria);
        for (var beat = 0; beat < ServerLimits.PresenceBeatsPerAddress.Count; beat++)
        {
            using var response = await aria.PostRawAsync("/v1/presence/beat", new byte[PresenceStore.TokenLength]);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var over = await aria.PostRawAsync("/v1/presence/beat", new byte[PresenceStore.TokenLength]))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        }

        // Signed requests from the same address go on: a challenge, and a presence start with it.
        var (token, online) = await StartedAsync(aria);
        Assert.Equal(PresenceStore.Reported(1), online);
        Assert.Equal(1, server.Services.GetRequiredService<PresenceStore>().Online());

        // A minute later there is room again.
        server.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(PresenceStore.Reported(1), await BeatOnlineAsync(aria, token));
        Assert.Equal(1, server.Services.GetRequiredService<PresenceStore>().Online());
    }

    [Fact]
    public async Task AStart_TakesOnlyAPresenceChallenge_AndAPresenceChallengeAuthorizesNothingElse()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        await aria.BindAsync(Aria);

        // A challenge from /v1/challenge is refused, with no fresh one from the general store.
        using (var general = await aria.SendAsync("/v1/presence", RequestProofKind.Presence, "{}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, general.StatusCode);
            Assert.Empty(await general.Content.ReadAsByteArrayAsync());
        }

        // A presence challenge is refused by every other action, and is used once.
        var presence = await PresenceChallengeAsync(aria);
        using (var code = await aria.SendAsync("/v1/lodestone/code", RequestProofKind.LodestoneCode, "{}", challenge: presence))
        {
            Assert.Equal(HttpStatusCode.Conflict, code.StatusCode);
        }

        using (var first = await aria.SendAsync("/v1/presence", RequestProofKind.Presence, "{}", challenge: presence))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var again = await aria.SendAsync("/v1/presence", RequestProofKind.Presence, "{}", challenge: presence);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task PresenceChallenges_HaveTheirOwnAddressLimit_AndTakeNoChallengeFromAnythingElse()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        for (var issued = 0; issued < ServerLimits.PresenceChallengesPerAddress.Count; issued++)
        {
            await PresenceChallengeAsync(aria);
        }

        using (var over = await aria.PostRawAsync("/v1/presence/challenge", []))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        }

        // Every general challenge the address group has is still there.
        for (var issued = 0; issued < ServerLimits.ChallengesPerAddress.Count; issued++)
        {
            await aria.ChallengeAsync();
        }
    }

    [Fact]
    public async Task ACountUnderTheFloor_IsAnsweredAsZero_AndFromTheFloorUpAsItself()
    {
        using var server = new TestServer();
        var players = TestServer.Allowed.Select(_ => server.NewPlayer()).ToArray();
        try
        {
            for (var index = 0; index < players.Length; index++)
            {
                await players[index].BindAsync(TestServer.Allowed[index], Names[index]);
                var (_, online) = await StartedAsync(players[index]);
                Assert.Equal(0, online);
                Assert.Equal(index + 1, server.Services.GetRequiredService<PresenceStore>().Online());
            }
        }
        finally
        {
            foreach (var player in players)
            {
                player.Dispose();
            }
        }

        Assert.Equal([0, 0, 0, 0, 0, 5, 6, 100_000], new[] { 0, 1, 2, 3, PresenceStore.Floor - 1, PresenceStore.Floor, 6, 100_000 }.Select(PresenceStore.Reported));
    }

    [Fact]
    public async Task Starts_AreLimitedPerKey()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        await aria.BindAsync(Aria);
        for (var start = 0; start < ServerLimits.PresenceStartsPerKey.Count; start++)
        {
            await StartedAsync(aria);
        }

        using var over = await StartAsync(aria);
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
    }

    [Fact]
    public async Task NothingLogged_HoldsTheTokenOrTheCharacter()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        await aria.BindAsync(Aria);
        var (token, _) = await StartedAsync(aria);
        server.Time.Advance(TimeSpan.FromSeconds(60));
        await BeatOnlineAsync(aria, token);
        using (await aria.PostRawAsync("/v1/presence/leave", token))
        {
        }

        var log = server.Log.All;
        Assert.DoesNotContain(Convert.ToBase64String(token), log, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(token), log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Aria.ToString(System.Globalization.CultureInfo.InvariantCulture), log, StringComparison.Ordinal);
        Assert.DoesNotContain(aria.Key.PublicKey.Id.ToString(), log, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStore_IsBounded_AndSweepsExpiredSessionsToMakeRoom()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var store = new PresenceStore(time);
        var keys = Enumerable.Range(0, PresenceStore.MaxSessions + 1).Select(KeyOf).ToArray();
        for (var index = 0; index < PresenceStore.MaxSessions; index++)
        {
            Assert.Equal(StartResult.Started, store.Start(keys[index], index + 1, Issued(store)).Result);
        }

        Assert.Equal(StartResult.Full, store.Start(keys[^1], PresenceStore.MaxSessions + 1, Issued(store)).Result);
        Assert.Equal(PresenceStore.MaxSessions, store.Online());

        time.Advance(PresenceStore.Expiry);
        Assert.Equal(StartResult.Started, store.Start(keys[^1], PresenceStore.MaxSessions + 1, Issued(store)).Result);
        Assert.Equal(1, store.Sessions);
        Assert.Equal(1, store.Online());
    }

    [Fact]
    public void TheStore_CountsACharacterOnce_WhateverNamesIt()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var store = new PresenceStore(time);
        var first = store.Start(KeyOf(1), Aria, Issued(store));
        var second = store.Start(KeyOf(2), Aria, Issued(store));
        Assert.Equal(1, second.Online);
        store.Leave(first.Token!);
        Assert.Equal(1, store.Online());
        store.Leave(second.Token!);
        Assert.Equal(0, store.Online());
    }

    [Fact]
    public void AnExpiredSession_IsForgotten_WithNoRequestComingIn()
    {
        // GPT's regression test, October 5, 2026: nothing asks for the count, so only the background
        // sweep can keep "the server then forgets the character within about 3 minutes" true.
        var time = new ManualTime(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var store = new PresenceStore(time);
        Assert.Equal(StartResult.Started, store.Start(KeyOf(1), Aria, Issued(store)).Result);
        Assert.Equal(1, store.Sessions);

        // A challenge nobody signed under, and a session whose plugin crashed.
        Assert.NotNull(store.IssueChallenge());
        Assert.Equal(1, store.Challenges);
        time.Advance(PresenceStore.Expiry - TimeSpan.FromSeconds(1));
        store.SweepExpired();
        Assert.Equal(1, store.Sessions);

        time.Advance(TimeSpan.FromSeconds(1) + PresenceSweep.Interval);
        store.SweepExpired();
        Assert.Equal(0, store.Sessions);

        // The bound the disclosures name: the expiry and one sweep's interval, nothing longer.
        Assert.True(PresenceStore.Expiry + PresenceSweep.Interval <= TimeSpan.FromSeconds(195));

        // The challenge nobody used goes with its own lifetime, which is longer than the expiry.
        Assert.Equal(1, store.Challenges);
        time.Advance(AetherFrame.Server.Storage.ChallengeStore.Lifetime);
        store.SweepExpired();
        Assert.Equal(0, store.Challenges);
    }

    [Fact]
    public void TheSweep_RunsInTheBackground_AndClearsTheLimitersCountersToo()
    {
        // The sweep is a hosted service, so it runs on a server nobody is asking; the registration
        // is what makes that true, and the limiter drops the times it counted events at with it.
        var program = System.IO.File.ReadAllText(System.IO.Path.Combine(RepositoryRoot(), "server", "AetherFrame.Server", "Program.cs"));
        Assert.Contains("AddHostedService<PresenceSweep>()", program, StringComparison.Ordinal);

        var time = new ManualTime(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var limiter = new RateLimiter(time);
        Assert.True(limiter.TryTake(ServerLimits.PresenceStartsPerKey, KeyOf(1).ToString()));
        Assert.Equal(1, limiter.Count);
        time.Advance(ServerLimits.PresenceStartsPerKey.Window);
        limiter.SweepNow();
        Assert.Equal(0, limiter.Count);
    }

    [Fact]
    public void AStartSignedBeforeASharingChange_StartsNothing_AndAFreshOneDoes()
    {
        // GPT's regression test, October 5, 2026: a delayed start racing a revocation. The start was
        // signed while the character shared and arrives after it stopped, so it must start nothing:
        // its session would otherwise be counted for the whole expiry with no sharing behind it.
        var time = new ManualTime(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var store = new PresenceStore(time);
        var key = KeyOf(1);

        var inFlight = Issued(store);
        store.ForgetKey(key);
        Assert.Equal(StartResult.Stale, store.Start(key, Aria, inFlight).Result);
        Assert.Equal(0, store.Sessions);
        Assert.Equal(0, store.Online());

        // A challenge issued after the change is the plugin's way back in, and the endpoint's
        // binding check is what then decides whether it still shares.
        Assert.Equal(StartResult.Started, store.Start(key, Aria, Issued(store)).Result);
        Assert.Equal(1, store.Online());

        // A takeover does the same for the character, whichever key signed the start in flight.
        var other = KeyOf(2);
        var beforeTakeover = Issued(store);
        store.ForgetOtherKeys(Aria, other);
        Assert.Equal(StartResult.Stale, store.Start(KeyOf(3), Aria, beforeTakeover).Result);
        Assert.Equal(StartResult.Started, store.Start(other, Aria, Issued(store)).Result);

        // A start with no challenge of its own is stale while anything is remembered.
        var signedBefore = Issued(store);
        store.ForgetKey(key);
        Assert.Equal(StartResult.Stale, store.Start(key, Aria, 0).Result);
        Assert.Equal(StartResult.Stale, store.Start(key, Aria, signedBefore).Result);

        // The memory is kept no longer than a challenge can be: just before, a start signed before
        // the revocation is still refused; from then on the revocation is forgotten, and the sweep
        // drops it whether or not anything starts.
        time.Advance(PresenceStore.RevocationMemory - TimeSpan.FromSeconds(1));
        Assert.Equal(StartResult.Stale, store.Start(key, Aria, signedBefore).Result);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(StartResult.Started, store.Start(key, Aria, signedBefore).Result);
    }

    [Fact]
    public void PastItsBound_OneRevocationStandsForAll_AndRefusesOnlyWhatCameBeforeIt()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var store = new PresenceStore(time);
        var signedBefore = Issued(store);
        for (var index = 0; index < PresenceStore.MaxRevocations; index++)
        {
            store.ForgetKey(KeyOf(index + 10));
        }

        // The memory is full, so the next revocation is remembered as one for every key and
        // character: a start signed before it is refused, whoever signed it, and one signed after
        // it is not.
        store.ForgetKey(KeyOf(1));
        Assert.Equal(StartResult.Stale, store.Start(KeyOf(2), Bram, signedBefore).Result);
        Assert.Equal(StartResult.Started, store.Start(KeyOf(2), Bram, Issued(store)).Result);

        // And it goes with the rest once no challenge from before it can be accepted.
        time.Advance(PresenceStore.RevocationMemory);
        store.SweepExpired();
        Assert.Equal(StartResult.Started, store.Start(KeyOf(3), Aria, signedBefore).Result);
    }

    [Fact]
    public async Task AStartInFlightWhenSharingStopped_IsRefused_AndSaysSo()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        await aria.BindAsync(Aria);

        // The challenge and the signature come first, as the plugin's do, and the pause lands
        // between them and the start: the start arrives on a binding that is already paused.
        var challenge = await PresenceChallengeAsync(aria);
        using (var off = await aria.SendAsync("/v1/opt-out", RequestProofKind.OptOut, "{\"mode\":\"pause\"}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        }

        using (var late = await aria.SendAsync("/v1/presence", RequestProofKind.Presence, "{}", challenge: challenge))
        {
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Empty(await late.Content.ReadAsByteArrayAsync());
        }

        Assert.Equal(0, server.Services.GetRequiredService<PresenceStore>().Online());
        Assert.Equal(0, server.Services.GetRequiredService<PresenceStore>().Sessions);
    }

    /// <summary>The repository's root, for the few assertions about how the server is put together.</summary>
    private static string RepositoryRoot()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "AetherFrame.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>A challenge issued and consumed, as a start's own is: its number, for <see cref="PresenceStore.Start"/>.</summary>
    private static long Issued(PresenceStore store)
    {
        var challenge = store.IssueChallenge();
        Assert.NotNull(challenge);
        Assert.True(store.TryConsumeChallenge(challenge, out var issuedAs));
        return issuedAs;
    }

    private static PersonaId KeyOf(int index)
    {
        var bytes = new byte[32];
        BitConverter.TryWriteBytes(bytes, index + 1);
        return PersonaId.Parse("psn_" + Convert.ToHexString(bytes).ToLowerInvariant());
    }

    private static async Task<HttpResponseMessage> StartAsync(Player player) =>
        await player.SendAsync("/v1/presence", RequestProofKind.Presence, "{}", challenge: await PresenceChallengeAsync(player));

    private static async Task<RequestChallenge> PresenceChallengeAsync(Player player)
    {
        using var response = await player.PostRawAsync("/v1/presence/challenge", []);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return RequestChallenge.FromBytes(await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task<(byte[] Token, int Online)> StartedAsync(Player player)
    {
        using var response = await StartAsync(player);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadFromJsonElementAsync();
        Assert.Equal(["session", "online"], answer.EnumerateObject().Select(property => property.Name));
        return (Convert.FromBase64String(answer.GetProperty("session").GetString()!), answer.GetProperty("online").GetInt32());
    }

    private static Task<HttpResponseMessage> BeatAsync(Player player, byte[] token) =>
        player.PostRawAsync("/v1/presence/beat", token);

    private static async Task<int> BeatOnlineAsync(Player player, byte[] token)
    {
        using var response = await BeatAsync(player, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadFromJsonElementAsync();
        Assert.Equal(["online"], answer.EnumerateObject().Select(property => property.Name));
        return answer.GetProperty("online").GetInt32();
    }
}
