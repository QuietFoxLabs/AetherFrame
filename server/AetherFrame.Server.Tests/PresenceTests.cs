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
    public async Task Heartbeats_HaveTheirOwnAddressLimit_WhichOnlyRequestsNamingNoSessionTakeFrom()
    {
        // The rechecks of 28be0e2: every heartbeat and leave took from the address group's limit, so
        // anyone sharing a network could count the others' heartbeats, minute by minute and under
        // the floor too, by how soon their own met it, and use it up to stop them being counted.
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        await aria.BindAsync(Aria);
        await bram.BindAsync(Bram, "Bram Oakes");
        var (token, _) = await StartedAsync(aria);
        var (other, _) = await StartedAsync(bram);

        // Counted heartbeats take nothing: after them, every one of the group's 120 that name no
        // session is still answered, and only the next is refused.
        server.Time.Advance(TimeSpan.FromSeconds(21));
        Assert.Equal(PresenceStore.Reported(2), await BeatOnlineAsync(aria, token));
        Assert.Equal(PresenceStore.Reported(2), await BeatOnlineAsync(bram, other));
        for (var beat = 0; beat < ServerLimits.PresenceBeatsPerAddress.Count; beat++)
        {
            using var response = await aria.PostRawAsync("/v1/presence/beat", new byte[PresenceStore.TokenLength]);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var over = await aria.PostRawAsync("/v1/presence/beat", new byte[PresenceStore.TokenLength]))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        }

        using (var junk = await aria.PostRawAsync("/v1/presence/leave", new byte[PresenceStore.TokenLength]))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, junk.StatusCode);
        }

        // With the limit used up, a live session's heartbeat is still counted, and its leave still
        // ends it: no one on the network can stop them.
        server.Time.Advance(TimeSpan.FromSeconds(21));
        Assert.Equal(PresenceStore.Reported(2), await BeatOnlineAsync(aria, token));
        using (var left = await bram.PostRawAsync("/v1/presence/leave", other))
        {
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }

        Assert.Equal(1, server.Services.GetRequiredService<PresenceStore>().Online());

        // Signed requests from the same address go on: a challenge, and a presence start with it.
        var (again, online) = await StartedAsync(aria);
        Assert.Equal(PresenceStore.Reported(1), online);
        Assert.Equal(1, server.Services.GetRequiredService<PresenceStore>().Online());

        // A minute later there is room again. A heartbeat too soon after the last names no session
        // that counts, so it takes from the limit too: after 119 that name none, it takes the last
        // place, and the next is refused. (The rechecks of beaea99: this was tested only with the
        // limit already used up, where the answer is the same whether it takes from it or not.)
        server.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(PresenceStore.Reported(1), await BeatOnlineAsync(aria, again));
        for (var beat = 1; beat < ServerLimits.PresenceBeatsPerAddress.Count; beat++)
        {
            using var room = await aria.PostRawAsync("/v1/presence/beat", new byte[PresenceStore.TokenLength]);
            Assert.Equal(HttpStatusCode.NotFound, room.StatusCode);
        }

        using (var early = await aria.PostRawAsync("/v1/presence/beat", again))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, early.StatusCode);
        }

        using (var full = await aria.PostRawAsync("/v1/presence/beat", new byte[PresenceStore.TokenLength]))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        }
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
        Assert.Equal(1, store.Online());
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
    public void ALeave_TakesNothingFromTheLimit_OnlyForASessionStillCounted()
    {
        // The rechecks of beaea99: a leave for a session that had expired, before the sweep dropped
        // it, was taken as one for a live session, and took nothing from the address group's limit
        // though the server no longer counted it.
        var time = new ManualTime(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var store = new PresenceStore(time);
        var live = store.Start(KeyOf(1), Aria, Issued(store)).Token!;
        var expired = store.Start(KeyOf(2), Bram, Issued(store)).Token!;
        time.Advance(PresenceStore.Expiry - TimeSpan.FromSeconds(1));
        Assert.Equal(BeatResult.Counted, store.Beat(live).Result);
        time.Advance(TimeSpan.FromSeconds(1));

        // Expired and still held until the sweep: its leave drops it, as naming no session counted.
        Assert.Equal(2, store.Sessions);
        Assert.False(store.Leave(expired));
        Assert.Equal(1, store.Sessions);
        Assert.False(store.Leave(expired));

        // A session still counted leaves as one.
        Assert.True(store.Leave(live));
        Assert.Equal(0, store.Sessions);
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

        // A start with no challenge of its own admits nothing.
        var signedBefore = Issued(store);
        store.ForgetKey(key);
        Assert.Equal(StartResult.Stale, store.Start(key, Aria, default).Result);
        Assert.Equal(StartResult.Stale, store.Start(key, Aria, signedBefore).Result);

        // Just before the revocation is forgotten, a start signed before it is still refused as
        // stale. From then on its own admission has ended too, so it is still refused, as expired
        // (GPT's review of fa51214: this start was accepted here before), and the sweep drops the
        // revocation whether or not anything starts.
        time.Advance(PresenceStore.RevocationMemory - TimeSpan.FromSeconds(1));
        Assert.Equal(StartResult.Stale, store.Start(key, Aria, signedBefore).Result);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(StartResult.Expired, store.Start(key, Aria, signedBefore).Result);
        store.SweepExpired();
        Assert.Equal(0, store.Revocations);
        Assert.Equal(StartResult.Started, store.Start(key, Aria, Issued(store)).Result);
    }

    [Fact]
    public void AnAdmission_EndsWithItsChallengesLifetime_HoweverLateTheStartIsMade()
    {
        // GPT's regression test, October 6, 2026: a consumed challenge's admission was bounded only
        // by the revocation memory, so a start whose signature was checked and whose request was then
        // held up (a suspended process, a slow database) past the 300 seconds started a session a
        // revocation in between should have refused.
        var time = new ManualTime(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var store = new PresenceStore(time);
        var key = KeyOf(1);

        // The challenge is consumed and the signature checked; then the request stalls, and the
        // character's sharing stops 10 seconds in.
        var admitted = Issued(store);
        time.Advance(TimeSpan.FromSeconds(10));
        store.ForgetKey(key);

        // While the revocation is remembered the start is stale; from the challenge's 300 seconds
        // it is expired, before the revocation is forgotten and after.
        time.Advance(PresenceStore.RevocationMemory - TimeSpan.FromSeconds(11));
        Assert.Equal(StartResult.Stale, store.Start(key, Aria, admitted).Result);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(StartResult.Expired, store.Start(key, Aria, admitted).Result);
        time.Advance(TimeSpan.FromSeconds(10));
        store.SweepExpired();
        Assert.Equal(0, store.Revocations);
        Assert.Equal(StartResult.Expired, store.Start(key, Aria, admitted).Result);
        Assert.Equal(0, store.Sessions);

        // With nothing revoked at all the bound is the same: up to just before the challenge's 300
        // seconds a start is made, and from then on it isn't.
        var inTime = Issued(store);
        time.Advance(PresenceStore.RevocationMemory - TimeSpan.FromMilliseconds(1));
        Assert.Equal(StartResult.Started, store.Start(KeyOf(2), Bram, inTime).Result);
        var late = Issued(store);
        time.Advance(PresenceStore.RevocationMemory);
        Assert.Equal(StartResult.Expired, store.Start(KeyOf(3), Bram, late).Result);
        Assert.Equal(PresenceStore.RevocationMemory, AetherFrame.Server.Storage.ChallengeStore.Lifetime);
    }

    [Fact]
    public void SettingTheWallClock_MovesNoExpiryAdmissionRevocationOrWindow()
    {
        // The rechecks of 7843256: with ages read from the wall clock, setting it an hour forward let
        // a sweep forget a revocation, and setting it back again left the start it should refuse
        // still admitted. The store's clock is the monotonic timestamp, which neither moves.
        var start = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTime(start);
        var store = new PresenceStore(time);
        var key = KeyOf(1);
        var admitted = Issued(store);
        store.ForgetKey(key);

        time.Now = start + TimeSpan.FromHours(1);
        store.SweepExpired();
        Assert.Equal(1, store.Revocations);
        time.Now = start + TimeSpan.FromSeconds(30);
        Assert.Equal(StartResult.Stale, store.Start(key, Aria, admitted).Result);

        // Set back instead, the clock shortens nothing: a fresh challenge still admits its start.
        time.Now = start - TimeSpan.FromDays(1);
        var session = store.Start(KeyOf(2), Bram, Issued(store));
        Assert.Equal(StartResult.Started, session.Result);

        // A session's spacing and expiry go by the time that passed, whatever the clock reads.
        time.Now = start + TimeSpan.FromHours(2);
        Assert.Equal(BeatResult.TooSoon, store.Beat(session.Token!).Result);
        time.Advance(PresenceStore.MinimumBeatSpacing);
        Assert.Equal(BeatResult.Counted, store.Beat(session.Token!).Result);
        time.Now = start - TimeSpan.FromDays(2);
        time.Advance(PresenceStore.Expiry);
        Assert.Equal(BeatResult.Unknown, store.Beat(session.Token!).Result);

        // And the admission and the revocation end together, 300 seconds after the challenge by the
        // time that passed.
        time.Advance(PresenceStore.RevocationMemory - PresenceStore.Expiry - PresenceStore.MinimumBeatSpacing);
        Assert.Equal(StartResult.Expired, store.Start(key, Aria, admitted).Result);
        store.SweepExpired();
        Assert.Equal(0, store.Revocations);

        // The windows too (the rechecks of 28be0e2): a session started as the second window begins
        // is counted from the third, however the wall clock is set in between, to times that fall in
        // other 5-minute windows of its own.
        var counted = store.Start(KeyOf(3), Aria, Issued(store)).Token!;
        time.Now = start + TimeSpan.FromDays(3) + TimeSpan.FromSeconds(137);
        time.Advance(TimeSpan.FromSeconds(150));
        Assert.Equal((BeatResult.Counted, 0), store.Beat(counted));
        time.Advance(TimeSpan.FromSeconds(149));
        Assert.Equal((BeatResult.Counted, 0), store.Beat(counted));
        time.Now = start - TimeSpan.FromDays(1) + TimeSpan.FromSeconds(181);
        Assert.Equal(0, store.Snapshot());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, store.Snapshot());
        time.Now = start + TimeSpan.FromSeconds(7);
        Assert.Equal(1, store.Snapshot());
    }

    [Fact]
    public void TheCount_IsOneSnapshotPerFiveMinuteWindow_ExactlyAsTheWindowBegan()
    {
        // GPT's privacy decision, October 6, 2026: one shared snapshot for each fixed 5-minute window,
        // the same for starts and heartbeats, while leaving, revocation and expiry still end a session
        // at once. The rechecks of 28be0e2: the count as it stood when the window began, whichever
        // request comes first and whenever, so no request chooses the moment it shows.
        var time = new ManualTime(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var store = new PresenceStore(time);
        Assert.Equal(TimeSpan.FromMinutes(5), PresenceStore.SnapshotWindow);

        // The first window began with the store, before any session: six characters start in it,
        // and each is answered its snapshot, none.
        time.Advance(TimeSpan.FromSeconds(290));
        var tokens = new byte[6][];
        for (var index = 0; index < tokens.Length; index++)
        {
            var started = store.Start(KeyOf(index), 1_000 + index, Issued(store));
            Assert.Equal((StartResult.Started, 0), (started.Result, started.Online));
            tokens[index] = started.Token!;
        }

        // The second window's first request replaces one of those sessions, before any sweep: it is
        // still answered all six, live as the window began.
        time.Advance(TimeSpan.FromSeconds(10));
        var renewed = store.Start(KeyOf(0), 1_000, Issued(store));
        Assert.Equal((StartResult.Started, 6), (renewed.Result, renewed.Online));
        tokens[0] = renewed.Token!;
        Assert.Equal(6, store.Online());

        // A leave and a revocation end their sessions at once, and change the sessions, not the snapshot.
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(store.Leave(tokens[5]));
        store.ForgetKey(KeyOf(4));
        Assert.Equal(4, store.Online());
        Assert.Equal(6, store.Snapshot());
        Assert.Equal(BeatResult.Unknown, store.Beat(tokens[5]).Result);
        Assert.Equal(BeatResult.Unknown, store.Beat(tokens[4]).Result);

        // Two sessions with no heartbeat expire inside the window, 180 seconds after their start,
        // and the window's heartbeats are still answered its snapshot.
        time.Advance(TimeSpan.FromSeconds(25));
        Assert.Equal((BeatResult.Counted, 6), store.Beat(tokens[0]));
        Assert.Equal((BeatResult.Counted, 6), store.Beat(tokens[1]));
        time.Advance(TimeSpan.FromSeconds(150));
        Assert.Equal(BeatResult.Unknown, store.Beat(tokens[2]).Result);
        Assert.Equal((BeatResult.Counted, 6), store.Beat(tokens[0]));
        Assert.Equal(2, store.Online());

        // The third window: only the first character was still live as it began. A start at its
        // very first moment doesn't count itself.
        time.Advance(TimeSpan.FromSeconds(120));
        var seventh = store.Start(KeyOf(6), 1_006, Issued(store));
        Assert.Equal((StartResult.Started, 1), (seventh.Result, seventh.Online));
        Assert.Equal(2, store.Online());
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal((BeatResult.Counted, 1), store.Beat(tokens[0]));
        Assert.Equal((BeatResult.Counted, 1), store.Beat(seventh.Token!));
        time.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal((BeatResult.Counted, 1), store.Beat(tokens[0]));

        // The fourth window began with the first character live; nothing asks until after it has
        // expired, and the sweep that drops it takes the window's snapshot first: it is counted.
        time.Advance(TimeSpan.FromSeconds(250));
        store.SweepExpired();
        Assert.Equal(0, store.Online());
        Assert.Equal(1, store.Snapshot());

        // And the fifth began with no one.
        time.Advance(TimeSpan.FromSeconds(200));
        Assert.Equal(0, store.Snapshot());
    }

    [Fact]
    public void ALeaveOrARevocation_AsAWindowsFirstRequest_LeavesTheWindowAsItBegan()
    {
        // The rechecks of 28be0e2: a session ended before anything took the window's snapshot was
        // left out of it, though it was live as the window began. Every operation takes the snapshot
        // first, so whichever comes first, the window shows the sessions as it began.
        var time = new ManualTime(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var store = new PresenceStore(time);
        var tokens = new byte[3][];
        for (var index = 0; index < tokens.Length; index++)
        {
            tokens[index] = store.Start(KeyOf(index), 1_000 + index, Issued(store)).Token!;
        }

        // Before each window begins, the sessions still held beat, so all are live as it begins.
        void BeatTheRest(int from)
        {
            for (var beat = 0; beat < 3; beat++)
            {
                time.Advance(TimeSpan.FromSeconds(90));
                for (var index = from; index < tokens.Length; index++)
                {
                    Assert.Equal(BeatResult.Counted, store.Beat(tokens[index]).Result);
                }
            }

            time.Advance(TimeSpan.FromSeconds(30));
        }

        // The second window's first request is a leave.
        BeatTheRest(0);
        Assert.True(store.Leave(tokens[0]));
        Assert.Equal(3, store.Snapshot());
        Assert.Equal(2, store.Online());

        // The third's is a pause.
        BeatTheRest(1);
        store.ForgetKey(KeyOf(1));
        Assert.Equal(2, store.Snapshot());
        Assert.Equal(1, store.Online());

        // The fourth's is a takeover.
        BeatTheRest(2);
        store.ForgetOtherKeys(1_002, KeyOf(9));
        Assert.Equal(1, store.Snapshot());
        Assert.Equal(0, store.Online());

        // And the fifth began with no one.
        time.Advance(PresenceStore.SnapshotWindow);
        Assert.Equal(0, store.Snapshot());
    }

    [Fact]
    public void PastItsHour_ASessionIsCountedUntilItsExpiry_OrUntilANewStartReplacesIt()
    {
        // The rechecks of 28be0e2: a session that stopped being counted at its hour left its character
        // out of a window that began before the plugin's new start, and the dip told when the
        // character had logged in to within that gap. Past the hour only its heartbeats stop counting.
        var time = new ManualTime(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var store = new PresenceStore(time);
        var renewing = store.Start(KeyOf(1), Aria, Issued(store)).Token!;
        var left = store.Start(KeyOf(2), Bram, Issued(store)).Token!;
        for (var minute = 1; minute < 60; minute++)
        {
            time.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(BeatResult.Counted, store.Beat(renewing).Result);
            Assert.Equal(BeatResult.Counted, store.Beat(left).Result);
        }

        // The hour, which is also where a window begins: no heartbeat is counted, both characters are.
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.Zero, TimeSpan.FromTicks(PresenceStore.SessionLifetime.Ticks % PresenceStore.SnapshotWindow.Ticks));
        Assert.Equal(BeatResult.Unknown, store.Beat(renewing).Result);
        Assert.Equal(BeatResult.Unknown, store.Beat(left).Result);
        Assert.Equal(2, store.Online());
        Assert.Equal(2, store.Snapshot());

        // One plugin's new start replaces its session at once; the other's plugin has gone, and its
        // session expires 180 seconds after its last counted heartbeat, as any does.
        time.Advance(TimeSpan.FromSeconds(10));
        var renewed = store.Start(KeyOf(1), Aria, Issued(store));
        Assert.Equal((StartResult.Started, 2), (renewed.Result, renewed.Online));
        Assert.Equal(BeatResult.Unknown, store.Beat(renewing).Result);
        Assert.Equal(2, store.Sessions);
        time.Advance(PresenceStore.Expiry - TimeSpan.FromSeconds(71));
        Assert.Equal(2, store.Online());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, store.Online());
        Assert.Equal(BeatResult.Counted, store.Beat(renewed.Token!).Result);
    }

    [Fact]
    public async Task APresenceRequestThatSucceeds_LeavesNoLogLine_AndOneThatFails_OnlyItsUsualLine()
    {
        // GPT's direction, October 6, 2026: successful presence requests are left out of the request
        // log, and a failure keeps its line, which holds no token, key, address or body.
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        await aria.BindAsync(Aria);
        var (token, _) = await StartedAsync(aria);
        server.Time.Advance(TimeSpan.FromSeconds(60));
        await BeatOnlineAsync(aria, token);
        using (var left = await aria.PostRawAsync("/v1/presence/leave", token))
        {
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }

        using (var status = await server.CreateClient().GetAsync("/v1/status"))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }

        // The challenge, the start, the heartbeat and the leave left no line; every other request
        // still logs its success.
        var requestLog = typeof(AetherFrame.Server.Hosting.RequestLog).FullName + " Information: ";
        string[] Lines() => [.. server.Log.Lines.Where(line => line.StartsWith(requestLog, StringComparison.Ordinal))];
        Assert.DoesNotContain(Lines(), line => line.Contains("/v1/presence", StringComparison.Ordinal));
        Assert.Contains(Lines(), line => line.Contains(" /v1/status 200 ", StringComparison.Ordinal));

        // A heartbeat for the session just left fails, and leaves its line: the request's id, the
        // route, the status, the duration and the failure's kind, and nothing else.
        using (var unknown = await BeatAsync(aria, token))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        using (var wrongLength = await aria.PostRawAsync("/v1/presence/beat", new byte[PresenceStore.TokenLength - 1]))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongLength.StatusCode);
        }

        var failures = Lines().Where(line => line.Contains("/v1/presence", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, failures.Length);
        Assert.Matches(@"^\S+ Information: [0-9a-f]{16} /v1/presence/beat 404 \d+ presence:unknown $", failures[0]);
        Assert.Matches(@"^\S+ Information: [0-9a-f]{16} /v1/presence/beat 400 \d+ body:token $", failures[1]);
        var log = server.Log.All;
        Assert.DoesNotContain(Convert.ToBase64String(token), log, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(token), log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(aria.Key.PublicKey.Id.ToString(), log, StringComparison.Ordinal);

        // The mark that leaves a success unlogged is on the four presence endpoints and nothing else.
        var endpoints = server.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<AetherFrame.Server.Hosting.UnloggedWhenSuccessful>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["/v1/presence", "/v1/presence/beat", "/v1/presence/challenge", "/v1/presence/leave"], endpoints);
    }

    [Fact]
    public async Task TheRequestLog_LeavesOutOnlyAFinishedSuccess_AndGivesEachRequestARandomIdOfItsOwn()
    {
        // The rechecks of 7843256: a request cut short leaves its line even with a success's status,
        // and named as such; and the id is random, not Kestrel's connection id and request number,
        // which behind the proxy's shared connections would count the unlogged requests before it.
        var captured = new CapturedLog();
        using var factory = new Microsoft.Extensions.Logging.LoggerFactory([captured]);
        var logger = new Microsoft.Extensions.Logging.Logger<AetherFrame.Server.Hosting.RequestLog>(factory);
        static Microsoft.AspNetCore.Routing.RouteEndpoint Endpoint(string route, params object[] metadata) =>
            new(_ => Task.CompletedTask, Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(route), 0, new Microsoft.AspNetCore.Http.EndpointMetadataCollection(metadata), route);
        var marked = Endpoint("/v1/presence/beat", AetherFrame.Server.Hosting.UnloggedWhenSuccessful.Instance);
        var unmarked = Endpoint("/v1/status");

        async Task<string[]> Run(Microsoft.AspNetCore.Http.Endpoint endpoint, int status, Exception? thrown = null, bool aborted = false)
        {
            var before = captured.Lines.Count;
            using var abort = new System.Threading.CancellationTokenSource();
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { TraceIdentifier = "0HN7AETHER:00000002", RequestAborted = abort.Token };
            if (aborted)
            {
                abort.Cancel();
            }

            Microsoft.AspNetCore.Http.EndpointHttpContextExtensions.SetEndpoint(http, endpoint);
            var log = new AetherFrame.Server.Hosting.RequestLog(
                context =>
                {
                    context.Response.StatusCode = status;
                    return thrown is null ? Task.CompletedTask : Task.FromException(thrown);
                },
                logger);
            if (aborted)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => log.InvokeAsync(http));
            }
            else
            {
                await log.InvokeAsync(http);
            }

            return [.. captured.Lines.Skip(before)];
        }

        Assert.Empty(await Run(marked, 200));
        Assert.Empty(await Run(marked, 204));
        var lines = new[]
        {
            Assert.Single(await Run(marked, 404)),
            Assert.Single(await Run(marked, 200, new OperationCanceledException(), aborted: true)),
            Assert.Single(await Run(marked, 200, new InvalidOperationException("a body, say"))),
            Assert.Single(await Run(unmarked, 200)),

            // A cancellation with the client still there, a timeout's say, is an exception like any.
            Assert.Single(await Run(marked, 200, new TaskCanceledException())),
        };

        var prefix = typeof(AetherFrame.Server.Hosting.RequestLog).FullName + " Information: ";
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(prefix) + "[0-9a-f]{16} /v1/presence/beat 404 \\d+ - $", lines[0]);
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(prefix) + "[0-9a-f]{16} /v1/presence/beat 200 \\d+ cancelled $", lines[1]);
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(prefix) + "[0-9a-f]{16} /v1/presence/beat 500 \\d+ exception:InvalidOperationException $", lines[2]);
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(prefix) + "[0-9a-f]{16} /v1/status 200 \\d+ - $", lines[3]);
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(prefix) + "[0-9a-f]{16} /v1/presence/beat 500 \\d+ exception:TaskCanceledException $", lines[4]);
        Assert.DoesNotContain("a body, say", captured.All, StringComparison.Ordinal);
        Assert.DoesNotContain("0HN7AETHER", captured.All, StringComparison.Ordinal);
        Assert.Equal(lines.Length, lines.Select(line => line.Split(' ')[2]).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void LoggingScopes_StayOff_WhateverTheConfigurationSays()
    {
        // The rechecks of 28be0e2: hosting's scope for each request holds its path, and Kestrel's its
        // connection id and request number, so a configuration that turned scopes on would put back
        // into every failure's line the count the random request id took out.
        using var server = new TestServer();
        using var scoped = server.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Logging:Console:IncludeScopes", "true");
            builder.UseSetting("Logging:Console:FormatterOptions:IncludeScopes", "true");
        });
        var services = scoped.Services;
        Assert.False(services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Logging.Console.SimpleConsoleFormatterOptions>>().CurrentValue.IncludeScopes);
        Assert.False(services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Logging.Console.JsonConsoleFormatterOptions>>().CurrentValue.IncludeScopes);
        Assert.False(services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>>().CurrentValue.IncludeScopes);
#pragma warning disable CS0618 // The console logger's own switch, which older configuration sets.
        Assert.False(services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>>().CurrentValue.IncludeScopes);
#pragma warning restore CS0618
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

        // And it goes with the rest once no start from before it can be admitted.
        Assert.Equal(PresenceStore.MaxRevocations + 1, store.Revocations);
        time.Advance(PresenceStore.RevocationMemory);
        store.SweepExpired();
        Assert.Equal(0, store.Revocations);
        Assert.Equal(StartResult.Expired, store.Start(KeyOf(3), Aria, signedBefore).Result);
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

    /// <summary>A challenge issued and consumed, as a start's own is: what it admits, for <see cref="PresenceStore.Start"/>.</summary>
    private static Admission Issued(PresenceStore store)
    {
        var challenge = store.IssueChallenge();
        Assert.NotNull(challenge);
        Assert.True(store.TryConsumeChallenge(challenge, out var admission));
        return admission;
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
