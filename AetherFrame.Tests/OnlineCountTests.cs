using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Personas;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Requests;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.Services.Network.Transport;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The online count's client ("The online count" in the decision register): whose presence is
/// sent, the signed start, heartbeats, the restart after a server restart, the leave, what My
/// Plates is shown (never a zero for a failure), and that nothing is sent for anyone who doesn't
/// share or after the run stopped.
/// </summary>
public class OnlineCountTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly OnlineCountPace Quick = new(
        TimeSpan.FromMilliseconds(60), TimeSpan.Zero, TimeSpan.FromMilliseconds(240), TimeSpan.FromMilliseconds(20), TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

    [Fact]
    public void ATarget_IsOnlyTheLoggedInCharacterThatShares_OnceTheNoticeIsSeen()
    {
        var key = PersonaSlotId.NewId();
        var keyId = PersonaId.Parse("psn_" + new string('1', 64));
        var shared = new SharingCharacter(7, key, keyId, SharingStage.Shared, "12345678", ProfileId.Parse("prf_" + new string('2', 32)), "Aria Starfall", "Gilgamesh");
        CharacterSharingView View(SharingCharacter character, bool loaded = true, bool unreadable = false, bool notice = false) =>
            new(loaded, unreadable, false, [character], null, null, null, onlineNotice: notice);

        Assert.Equal(new PresenceTarget(key, keyId), OnlineCount.TargetOf(View(shared), 7));
        Assert.Null(OnlineCount.TargetOf(View(shared), null));
        Assert.Null(OnlineCount.TargetOf(View(shared), 8));
        Assert.Null(OnlineCount.TargetOf(View(shared, loaded: false), 7));
        Assert.Null(OnlineCount.TargetOf(View(shared, unreadable: true), 7));
        Assert.Null(OnlineCount.TargetOf(View(shared, notice: true), 7));
        foreach (var stage in new[] { SharingStage.Off, SharingStage.Checking, SharingStage.Paused, SharingStage.TakenOver })
        {
            Assert.Null(OnlineCount.TargetOf(View(shared with { Stage = stage }), 7));
        }

        Assert.Null(OnlineCount.TargetOf(View(shared with { NewSlot = PersonaSlotId.NewId(), NewKey = PersonaId.Parse("psn_" + new string('3', 64)) }), 7));
    }

    [Fact]
    public async Task NoTarget_SendsNothing()
    {
        using var harness = new Harness();
        harness.Count.Update(null);
        await Task.Delay(200);
        Assert.Empty(harness.Server.Paths);
        Assert.Equal(OnlineCountState.Off, harness.Count.View.State);
    }

    [Fact]
    public async Task AStart_IsSignedByTheCharactersKey_ThenHeartbeatsCarryOnlyTheToken()
    {
        using var harness = new Harness();
        harness.Server.Online = 4;
        harness.Count.Update(harness.Target);
        Assert.Equal(OnlineCountState.Connecting, harness.Count.View.State);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 2);

        Assert.Equal(["/v1/presence/challenge", "/v1/presence"], harness.Server.Paths.Take(2));
        Assert.Equal(harness.Key.PublicKey.Id, harness.Server.Signers.Single());
        Assert.All(harness.Server.Beats, beat => Assert.Equal(harness.Server.Tokens.Single(), beat));
        Assert.Equal(new OnlineCountView(OnlineCountState.Online, 4, harness.Now), harness.Count.View);
    }

    [Fact]
    public async Task ASessionTheServerForgot_IsStartedAgain_Once()
    {
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        harness.Server.Forget();
        await harness.WaitFor(() => harness.Server.Tokens.Count == 2 && harness.Server.Beats.Count(beat => beat.SequenceEqual(harness.Server.Tokens[1])) >= 1);
        Assert.Equal(OnlineCountState.Online, harness.Count.View.State);
    }

    [Fact]
    public async Task ASession_IsRenewedBeforeItsHour_ByANewStartThatReplacesIt_WithNoHeartbeatRefused()
    {
        // The rechecks of 28be0e2: the server counts a session's heartbeats for an hour, and the
        // refusal at the hour left a line in its log an hour apart for as long as a character played.
        // The plugin replaces the session first, 50 to 54 minutes in, while it is live.
        using var harness = new Harness();
        Assert.Equal((TimeSpan.FromMinutes(52), TimeSpan.FromMinutes(2)), (OnlineCountPace.Default.Renewal, OnlineCountPace.Default.RenewalJitter));
        Assert.Equal((OnlineCountPace.Default.Renewal, OnlineCountPace.Default.RenewalJitter), (Quick.Renewal, Quick.RenewalJitter));
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 2);
        var started = harness.Now;

        // Not before 50 minutes, whatever the jitter.
        harness.Now = started + TimeSpan.FromMinutes(50) - TimeSpan.FromSeconds(1);
        var beats = harness.Server.Beats.Count;
        await harness.WaitFor(() => harness.Server.Beats.Count >= beats + 3);
        Assert.Single(harness.Server.Tokens);

        // By 54 minutes it is due: a new start signed by the same key replaces the session, and the
        // heartbeats carry its token from then on.
        harness.Now = started + TimeSpan.FromMinutes(54);
        await harness.WaitFor(() => harness.Server.Tokens.Count == 2 && harness.Server.Beats.Count(beat => beat.SequenceEqual(harness.Server.Tokens[1])) >= 2);
        Assert.Equal([harness.Key.PublicKey.Id, harness.Key.PublicKey.Id], harness.Server.Signers);
        Assert.False(harness.Server.IsLive(harness.Server.Tokens[0]));

        // And again, 50 to 54 minutes after the renewal.
        harness.Now = started + TimeSpan.FromMinutes(54 + 54);
        await harness.WaitFor(() => harness.Server.Tokens.Count == 3 && harness.Server.Beats.Count(beat => beat.SequenceEqual(harness.Server.Tokens[2])) >= 1);
        Assert.Equal(0, harness.Server.UnknownBeats);
        Assert.Empty(harness.Server.Leaves);

        // A renewal takes the place of its heartbeat: the next goes a heartbeat's wait after it, so
        // the server never sees one too soon.
        Assert.True(harness.Server.ShortestGap >= Quick.Beat * 0.8 - TimeSpan.FromMilliseconds(15), $"A heartbeat came {harness.Server.ShortestGap.TotalMilliseconds} ms after its session's start or last heartbeat.");
        Assert.Equal(3, harness.Server.Paths.Count(path => path == "/v1/presence/challenge"));
        Assert.Equal(OnlineCountState.Online, harness.Count.View.AsOf(harness.Now).State);
    }

    [Fact]
    public async Task ARenewalThatFails_IsNotTriedAgain_TheSessionBeatsOn_AndItsHourStartsItAgain()
    {
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        harness.Server.StartStatus = HttpStatusCode.ServiceUnavailable;
        harness.Now += TimeSpan.FromMinutes(54);

        // One try, which fails: the session's own heartbeats go on, each counted, and no other try.
        await harness.WaitFor(() => harness.Server.StartTimes.Count == 2);
        var beats = harness.Server.Beats.Count;
        await harness.WaitFor(() => harness.Server.Beats.Count >= beats + 4);
        Assert.Equal(2, harness.Server.StartTimes.Count);
        Assert.All(harness.Server.Beats, beat => Assert.Equal(harness.Server.Tokens.Single(), beat));
        Assert.Equal(0, harness.Server.UnknownBeats);
        Assert.Equal(OnlineCountState.Online, harness.Count.View.AsOf(harness.Now).State);

        // At its hour the server refuses its heartbeat, and the session is started again, as after
        // a restart of the server.
        harness.Server.StartStatus = HttpStatusCode.OK;
        harness.Server.Forget();
        await harness.WaitFor(() => harness.Server.Tokens.Count == 2 && harness.Server.Beats.Count(beat => beat.SequenceEqual(harness.Server.Tokens[1])) >= 1);
        Assert.Equal(1, harness.Server.UnknownBeats);
        Assert.Equal(3, harness.Server.StartTimes.Count);
    }

    [Fact]
    public async Task ARenewalThePersonaSessionIsTooBusyFor_BeatsMeanwhile_AndRenewsOnceItIsFree()
    {
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        harness.SessionBusy = true;
        harness.Now += TimeSpan.FromMinutes(54);

        // The renewal's challenge is asked for once and kept, and the session's heartbeats go on.
        await harness.WaitFor(() => harness.Server.Paths.Count(path => path == "/v1/presence/challenge") == 2);
        var beats = harness.Server.Beats.Count;
        await harness.WaitFor(() => harness.Server.Beats.Count >= beats + 3);
        Assert.Single(harness.Server.Tokens);
        Assert.Equal(2, harness.Server.Paths.Count(path => path == "/v1/presence/challenge"));

        harness.SessionBusy = false;
        await harness.WaitFor(() => harness.Server.Tokens.Count == 2 && harness.Server.Beats.Count(beat => beat.SequenceEqual(harness.Server.Tokens[1])) >= 1);
        Assert.Equal(2, harness.Server.Paths.Count(path => path == "/v1/presence/challenge"));
        Assert.Equal(0, harness.Server.UnknownBeats);
        Assert.False(harness.Server.IsLive(harness.Server.Tokens[0]));
    }

    [Fact]
    public async Task ARenewalRefusedForATakeover_EndsTheRun_AndSendsNothingMore()
    {
        // The takeover that refuses the start has already ended the session, so not even a leave goes.
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        harness.Server.StartStatus = HttpStatusCode.Gone;
        harness.Now += TimeSpan.FromMinutes(54);
        await harness.WaitFor(() => harness.Server.StartTimes.Count == 2);
        await Task.Delay(300);
        Assert.Equal("/v1/presence", harness.Server.Paths[^1]);
        Assert.Empty(harness.Server.Leaves);
    }

    [Fact]
    public async Task AClockSetBack_RenewsTheSessionOnce_AtOnce_RatherThanAfterItsHour()
    {
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        harness.Now -= TimeSpan.FromDays(1);
        await harness.WaitFor(() => harness.Server.Tokens.Count == 2);
        var beats = harness.Server.Beats.Count;
        await harness.WaitFor(() => harness.Server.Beats.Count >= beats + 3);
        Assert.Equal(2, harness.Server.Tokens.Count);
        Assert.Equal(0, harness.Server.UnknownBeats);
    }

    [Fact]
    public async Task AClockSetBackByLessThanTheSessionsAge_StillRenewsItAtOnce()
    {
        // The rechecks of beaea99: only a clock set back to before the session's start made its
        // renewal due, so one set back 10 minutes at minute 45 put the renewal after the hour.
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        harness.Now += TimeSpan.FromMinutes(45);
        var beats = harness.Server.Beats.Count;
        await harness.WaitFor(() => harness.Server.Beats.Count >= beats + 2);
        Assert.Single(harness.Server.Tokens);

        harness.Now -= TimeSpan.FromMinutes(10);
        await harness.WaitFor(() => harness.Server.Tokens.Count == 2);
        beats = harness.Server.Beats.Count;
        await harness.WaitFor(() => harness.Server.Beats.Count >= beats + 3);
        Assert.Equal(2, harness.Server.Tokens.Count);
        Assert.Equal(0, harness.Server.UnknownBeats);
    }

    [Fact]
    public async Task ASessionWhoseHeartbeatWasRefused_IsLeftWhenTheRunStops_UnlessANewStartReplacedIt()
    {
        // The rechecks of beaea99: past its hour the server refuses a session's heartbeats and still
        // counts it until its expiry, but the run let go of its token, so a logout before a new start
        // was made left the character counted for up to 3 minutes more.
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        var first = harness.Server.Tokens.Single();
        harness.Server.StartStatus = HttpStatusCode.ServiceUnavailable;
        harness.Server.EndHour(first);
        await harness.WaitFor(() => harness.Server.UnknownBeats >= 1 && harness.Server.StartTimes.Count >= 3);
        Assert.True(harness.Server.IsLive(first));
        harness.Count.Update(null);
        await harness.WaitFor(() => harness.Server.Leaves.Count == 1);
        Assert.Equal(first, harness.Server.Leaves.Single());
        Assert.False(harness.Server.IsLive(first));

        // When a new start does replace it, only the new session is left.
        harness.Server.StartStatus = HttpStatusCode.OK;
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Tokens.Count == 2 && harness.Server.Beats.Count(beat => beat.SequenceEqual(harness.Server.Tokens[1])) >= 1);
        var second = harness.Server.Tokens[1];
        harness.Server.EndHour(second);
        await harness.WaitFor(() => harness.Server.Tokens.Count == 3 && harness.Server.Beats.Count(beat => beat.SequenceEqual(harness.Server.Tokens[2])) >= 1);
        Assert.False(harness.Server.IsLive(second));
        harness.Count.Update(null);
        await harness.WaitFor(() => harness.Server.Leaves.Count == 2);
        Assert.Equal(harness.Server.Tokens[2], harness.Server.Leaves[1]);
        await Task.Delay(200);
        Assert.Equal(2, harness.Server.Leaves.Count);
    }

    [Fact]
    public async Task ANewStartRefusedForATakeover_AfterARefusedHeartbeat_LeavesNotEvenTheRefusedSession()
    {
        // The takeover ended every session of the key's, the refused one with them.
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        harness.Server.StartStatus = HttpStatusCode.Gone;
        harness.Server.EndHour(harness.Server.Tokens.Single());
        await harness.WaitFor(() => harness.Server.StartTimes.Count == 2);
        await Task.Delay(300);
        Assert.Equal("/v1/presence", harness.Server.Paths[^1]);
        Assert.Empty(harness.Server.Leaves);
    }

    [Fact]
    public async Task AServerWithoutTheEndpoint_ReadsUnavailable_NeverZero_AndBacksOff()
    {
        using var harness = new Harness();
        harness.Server.StartStatus = HttpStatusCode.NotFound;
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Count.View.State == OnlineCountState.Unavailable);
        Assert.Equal(0, harness.Count.View.Count);

        // A minute, then two, then four (scaled down here): each wait at least 80% of its step,
        // however slowly the machine runs, so far fewer than one try a heartbeat.
        await harness.WaitFor(() => harness.Server.StartTimes.Count >= 4);
        var times = harness.Server.StartTimes;
        for (var gap = 1; gap < 4; gap++)
        {
            var step = Quick.Beat * Math.Pow(2, gap - 1);
            Assert.True(times[gap] - times[gap - 1] >= step * 0.8 - TimeSpan.FromMilliseconds(15), $"Try {gap + 1} came {(times[gap] - times[gap - 1]).TotalMilliseconds} ms after the one before.");
        }
    }

    [Fact]
    public async Task AFailedHeartbeat_KeepsAFreshCount_AndAnOldOneReadsUnavailable()
    {
        using var harness = new Harness();
        harness.Server.Online = 9;
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Count.View.State == OnlineCountState.Online);
        harness.Server.BeatStatus = HttpStatusCode.ServiceUnavailable;
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        await Task.Delay(100);
        Assert.Equal(9, harness.Count.View.Count);
        Assert.Equal(OnlineCountState.Online, harness.Count.View.AsOf(harness.Now).State);
        Assert.Equal(OnlineCountState.Unavailable, harness.Count.View.AsOf(harness.Now + OnlineCount.Fresh).State);

        // Once the count is old, the next failure says so too.
        harness.Now += OnlineCount.Fresh;
        await harness.WaitFor(() => harness.Count.View.State == OnlineCountState.Unavailable);
    }

    [Fact]
    public async Task ABusySession_OnlyDelaysTheStart()
    {
        using var harness = new Harness();
        harness.SessionBusy = true;
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Paths.Count >= 1);
        await Task.Delay(150);
        Assert.Equal(OnlineCountState.Connecting, harness.Count.View.State);
        Assert.DoesNotContain("/v1/presence", harness.Server.Paths);

        // Several tries at signing, under the one challenge it asked for.
        Assert.Equal(["/v1/presence/challenge"], harness.Server.Paths);
        harness.SessionBusy = false;
        await harness.WaitFor(() => harness.Count.View.State == OnlineCountState.Online);
        Assert.Equal(1, harness.Server.Paths.Count(path => path == "/v1/presence/challenge"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALimitsRefusal_WaitsTheLongestBackoff(bool atTheChallenge)
    {
        using var harness = new Harness(Quick with { LongestBackoff = TimeSpan.FromSeconds(30) });
        if (atTheChallenge)
        {
            harness.Server.ChallengeStatus = HttpStatusCode.TooManyRequests;
        }
        else
        {
            harness.Server.StartStatus = HttpStatusCode.TooManyRequests;
        }

        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Count.View.State == OnlineCountState.Unavailable);

        // A doubling from a minute (60 ms here) would have tried again several times by now.
        await Task.Delay(500);
        Assert.Equal(1, harness.Server.Paths.Count(path => path == "/v1/presence/challenge"));
    }

    [Fact]
    public async Task ATakeover_EndsTheRun_AndNothingMoreIsSent()
    {
        using var harness = new Harness();
        harness.Server.StartStatus = HttpStatusCode.Gone;
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Count.View.State == OnlineCountState.Unavailable);
        await Task.Delay(500);
        Assert.Equal(["/v1/presence/challenge", "/v1/presence"], harness.Server.Paths);
    }

    [Fact]
    public async Task Stopping_LeavesTheSession_AndSendsNothingMore()
    {
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        harness.Count.Update(null);
        Assert.Equal(OnlineCountState.Off, harness.Count.View.State);
        await harness.WaitFor(() => harness.Server.Leaves.Count == 1);
        Assert.Equal(harness.Server.Tokens.Single(), harness.Server.Leaves.Single());
        var sent = harness.Server.Paths.Count;
        await Task.Delay(250);
        Assert.Equal(sent, harness.Server.Paths.Count);
    }

    [Fact]
    public async Task AStartStoppedBeforeTheServerActed_LeavesNothingBehind()
    {
        // A grace shorter than the hold, so the start is cancelled before the server makes anything.
        using var harness = new Harness(Quick with { StartGrace = TimeSpan.FromMilliseconds(50) });
        var release = new TaskCompletionSource();
        harness.Server.StartGate = release.Task;
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Paths.Contains("/v1/presence"));
        harness.Count.Update(null);
        await Task.Delay(200);
        release.SetResult();

        await Task.Delay(200);
        Assert.Equal(OnlineCountState.Off, harness.Count.View.State);
        Assert.Empty(harness.Server.Tokens);
        Assert.Empty(harness.Server.Leaves);
        Assert.Empty(harness.Server.Beats);
    }

    [Fact]
    public async Task AStartTheServerAnswered_IsLeft_EvenWhenItsRunStoppedWhileItWasInFlight()
    {
        // GPT's regression test, October 5, 2026: the answer comes back after the run stopped, and
        // the session the server already made would otherwise stay counted until its expiry.
        using var harness = new Harness();
        var answer = new TaskCompletionSource();
        harness.Server.StartAnswerGate = answer.Task;
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.StartAnswerHeld);

        harness.Count.Update(null);
        Assert.Equal(OnlineCountState.Off, harness.Count.View.State);
        answer.SetResult();

        await harness.WaitFor(() => harness.Server.Leaves.Count == 1);
        var token = Assert.Single(harness.Server.Tokens);
        Assert.Equal(token, harness.Server.Leaves.Single());
        Assert.False(harness.Server.IsLive(token));
        await Task.Delay(150);
        Assert.Equal(OnlineCountState.Off, harness.Count.View.State);
        Assert.Empty(harness.Server.Beats);
    }

    [Fact]
    public async Task ALogout_StopsTheRun_WithNoFrameDrawn()
    {
        // GPT's regression test, October 5, 2026: Dalamud draws no plugin window while it hides them
        // (a hidden interface, a cutscene, group pose), so the count follows the game's tick instead.
        using var harness = new Harness();
        var loggedIn = true;
        var driver = new PresenceDriver(harness.Count, () => loggedIn, () => harness.Target);
        driver.Tick();
        await harness.WaitFor(() => harness.Count.View.State == OnlineCountState.Online);

        loggedIn = false;
        driver.Tick();
        await harness.WaitFor(() => harness.Server.Leaves.Count == 1);
        Assert.Equal(harness.Server.Tokens.Single(), harness.Server.Leaves.Single());
        Assert.Equal(OnlineCountState.Off, harness.Count.View.State);

        // And nothing starts again while nobody is logged in, however many ticks come.
        var sent = harness.Server.Paths.Count;
        driver.Tick();
        driver.Tick();
        await Task.Delay(150);
        Assert.Equal(sent, harness.Server.Paths.Count);
    }

    [Fact]
    public async Task StoppingARun_SendsItsLeaveOffTheCallersThread_AndNeverWaitsForIt()
    {
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Count.View.State == OnlineCountState.Online && harness.Server.Tokens.Count == 1);
        await Task.Delay(150);

        // The leave is held before its first await: a leave sent on the frame's own thread would
        // hold that thread here, and a network request from the frame is what R2's rule 8 forbids.
        using var held = new ManualResetEventSlim(false);
        harness.Server.LeaveHold = held;
        var frame = new Thread(() => harness.Count.Update(null)) { IsBackground = true };
        frame.Start();
        Assert.True(frame.Join(TimeSpan.FromSeconds(5)), "Update waited on the leave.");

        held.Set();
        await harness.WaitFor(() => harness.Server.Leaves.Count == 1);
        Assert.NotSame(frame, harness.Server.LeaveThread);
    }

    [Fact]
    public async Task Unloading_LeavesTheSession_AndStartsNothingAfter()
    {
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        await harness.Count.StopAsync(Patience);
        Assert.Single(harness.Server.Leaves);
        harness.Count.Update(harness.Target);
        await Task.Delay(150);
        Assert.Single(harness.Server.Tokens);
        Assert.Equal(OnlineCountState.Off, harness.Count.View.State);
    }

    [Fact]
    public async Task AnotherCharacter_LeavesTheFirstSession_AndStartsItsOwn()
    {
        using var harness = new Harness();
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Beats.Count >= 1);
        var other = harness.Personas.Acknowledge(harness.Personas.Create("Character key").Slot);
        harness.Count.Update(new PresenceTarget(other.Slot, other.PublicKey.Id));
        await harness.WaitFor(() => harness.Server.Leaves.Count == 1 && harness.Server.Signers.Count == 2);
        Assert.Equal(other.PublicKey.Id, harness.Server.Signers[1]);
    }

    [Fact]
    public void Update_NeverWaitsOnTheNetwork()
    {
        using var harness = new Harness();
        var never = new TaskCompletionSource();
        harness.Server.ChallengeGate = never.Task;
        var started = DateTime.UtcNow;
        harness.Count.Update(harness.Target);
        harness.Count.Update(null);
        harness.Count.Update(harness.Target);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        never.SetResult();
    }

    [Fact]
    public void TheWords_SayWhatIsSent_AndTheConsentHasThem()
    {
        Assert.Contains(SharingText.OnlineCountSends, SharingText.Consent);
        Assert.Contains("about once a minute", SharingText.OnlineCountSends, StringComparison.Ordinal);
        Assert.Contains("refreshed every 5 minutes", SharingText.OnlineCountSends, StringComparison.Ordinal);
        Assert.Contains("can sometimes tell", SharingText.OnlineCountSends, StringComparison.Ordinal);
        Assert.Contains("even below 5 if they keep characters of their own counted", SharingText.OnlineCountSends, StringComparison.Ordinal);
        Assert.True(Words(SharingText.OnlineCountSends) <= 80, "The consent's statement of the count is one statement among many.");
    }

    [Fact]
    public void TheNotice_IsShortPoints_WithWhatTheServerKeepsBehindAControl()
    {
        // GPT's review of fa51214: the notice's 200-word paragraph was too much. What is sent, the
        // refresh, what stops it and what others can still tell are short visible points.
        var points = SharingText.OnlineNoticePoints;
        Assert.All(points, point => Assert.True(Words(point) <= 40, point));
        Assert.True(points.Sum(Words) <= 130, "The notice's points stay short in all.");
        Assert.Contains(points, point => point.StartsWith("What is sent:", StringComparison.Ordinal) && point.Contains("about once a minute", StringComparison.Ordinal) && point.Contains("Characters that don't share send nothing", StringComparison.Ordinal));
        Assert.Contains(points, point => point.StartsWith("What you see:", StringComparison.Ordinal) && point.Contains("refreshed every 5 minutes", StringComparison.Ordinal) && point.Contains("Fewer than 5", StringComparison.Ordinal));
        Assert.Contains(points, point => point.StartsWith("When it stops:", StringComparison.Ordinal) && point.Contains("log out", StringComparison.Ordinal) && point.Contains("pause", StringComparison.Ordinal) && point.Contains("close the game", StringComparison.Ordinal) && point.Contains("about 3 minutes", StringComparison.Ordinal) && point.Contains("at the next refresh", StringComparison.Ordinal));
        Assert.Contains(points, point => point.StartsWith("What others can tell:", StringComparison.Ordinal) && point.Contains("can sometimes tell", StringComparison.Ordinal) && point.Contains("even below 5 if they keep characters of their own counted", StringComparison.Ordinal));
        Assert.Equal("Nothing is sent until you choose Got it.", points[^1]);

        // The retention details are there for whoever opens them, and none of them is a visible point.
        var details = string.Join(" ", SharingText.OnlineCountDetails);
        foreach (var said in new[] { "memory only", "about 3 minutes after the last signal", "counts each character once", "Apart from its rate limits (below), it keeps no record of who was online", "the same to everyone for each 5 minutes", "aren't logged", "14 days", "never whose", "network address", "never writes down or logs", "up to an hour", "adds no characters of their own", "about every 52 minutes while it plays", "use those limits up on purpose", "to the second" })
        {
            Assert.Contains(said, details, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(points, point => point.Contains("14 days", StringComparison.Ordinal) || point.Contains("network address", StringComparison.Ordinal));

        Assert.StartsWith("Online count details", SharingText.OnlineDetailsLabel, StringComparison.Ordinal);

        // The window draws the points as bullets, and the details only once the control is opened,
        // under the notice and under the consent alike.
        var window = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Windows", "Network", "SharingWindow.cs"));
        var notice = Body(window, "private void DrawOnlineNotice(");
        Assert.Contains("foreach (var point in SharingText.OnlineNoticePoints)", notice, StringComparison.Ordinal);
        Assert.Contains("DrawOnlineDetails(", notice, StringComparison.Ordinal);
        Assert.Contains("DrawOnlineDetails(", Body(window, "private void DrawConsent("), StringComparison.Ordinal);
        var detailsDrawn = Body(window, "private static void DrawOnlineDetails(");
        var opened = detailsDrawn.IndexOf("if (!ImGui.CollapsingHeader(SharingText.OnlineDetailsLabel", StringComparison.Ordinal);
        Assert.True(opened >= 0 && opened < detailsDrawn.IndexOf("SharingText.OnlineCountDetails", StringComparison.Ordinal), "The details are drawn only behind their control.");
    }

    private static int Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>A method's text in a source file, from its signature to the next member's.</summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    [Fact]
    public void TheFooter_ShowsTheCount_OrConnectingOrUnavailable_NeverAZeroForAFailure()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        Assert.Null(OnlineCountFooter.ItemFor(OnlineCountView.Off, now, noticeDue: false));
        Assert.Equal(OnlineCountFooter.NoticeDue, OnlineCountFooter.ItemFor(OnlineCountView.Off, now, noticeDue: true));
        Assert.Equal(OnlineCountFooter.Connecting, OnlineCountFooter.ItemFor(OnlineCountView.Connecting, now, noticeDue: false));
        Assert.Equal(OnlineCountFooter.Unavailable, OnlineCountFooter.ItemFor(OnlineCountView.Unavailable, now, noticeDue: false));

        var counted = new OnlineCountView(OnlineCountState.Online, 1234, now);
        Assert.Equal(new MyPlatesFooterItemProbe("1,234 online", OnlineCountFooter.Scope), Probe(OnlineCountFooter.ItemFor(counted, now + TimeSpan.FromSeconds(179), noticeDue: false)));
        Assert.Equal(OnlineCountFooter.Unavailable, OnlineCountFooter.ItemFor(counted, now + OnlineCount.Fresh, noticeDue: false));
        Assert.Equal("Fewer than 5 online", OnlineCountFooter.Text(0));
        Assert.Equal("Fewer than 5 online", OnlineCountFooter.Text(OnlineCountFooter.Floor - 1));
        Assert.Equal("5 online", OnlineCountFooter.Text(OnlineCountFooter.Floor));
        Assert.Contains("fewer than 5", OnlineCountFooter.Scope, StringComparison.Ordinal);
        Assert.Contains("counted once each", OnlineCountFooter.Scope, StringComparison.Ordinal);

        // The tooltip says how often the server refreshes the total (GPT's privacy decision of
        // October 6, 2026), which is the server's snapshot window, not the plugin's heartbeat.
        Assert.Contains("refreshes this total every 5 minutes", OnlineCountFooter.Scope, StringComparison.Ordinal);
        Assert.DoesNotContain("once a minute", OnlineCountFooter.Scope, StringComparison.Ordinal);

        // The rechecks of 28be0e2: the first window after the server restarts began with no session.
        Assert.Contains("for the first 5 minutes after the server restarts, it says fewer than 5", OnlineCountFooter.Scope, StringComparison.Ordinal);
        Assert.All(new[] { OnlineCountFooter.Connecting, OnlineCountFooter.Unavailable, OnlineCountFooter.NoticeDue }, item => Assert.DoesNotContain("0", item.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void TheFooter_SaysWhereToTurnItOn_OnlyForASharingCharacterWhoseNoticeIsDue()
    {
        var shared = new SharingCharacter(7, PersonaSlotId.NewId(), PersonaId.Parse("psn_" + new string('1', 64)), SharingStage.Shared, "12345678", ProfileId.Parse("prf_" + new string('2', 32)), "Aria Starfall", "Gilgamesh");
        CharacterSharingView View(SharingCharacter character, bool notice) => new(true, false, false, [character], null, null, null, onlineNotice: notice);
        Assert.True(OnlineCountFooter.WaitsForNotice(View(shared, notice: true), 7));
        Assert.False(OnlineCountFooter.WaitsForNotice(View(shared, notice: false), 7));
        Assert.False(OnlineCountFooter.WaitsForNotice(View(shared, notice: true), null));
        Assert.False(OnlineCountFooter.WaitsForNotice(View(shared with { Stage = SharingStage.Paused }, notice: true), 7));
    }

    private sealed record MyPlatesFooterItemProbe(string Text, string Tooltip);

    private static MyPlatesFooterItemProbe? Probe(AetherFrame.UI.Library.MyPlatesFooterItem? item) => item is null ? null : new(item.Text, item.Tooltip);

    /// <summary>A persona manager over memory with one character key, a presence server answered in memory, and the count over them.</summary>
    private sealed class Harness : IDisposable
    {
        internal Harness(OnlineCountPace? pace = null)
        {
            Personas = PersonaManager.Load(new ProtectedPersonaKeyStore(new MemoryKeyBlobs(), new MaskingProtector()), new NoBackups(), new MemoryRegistry());
            Key = Personas.Acknowledge(Personas.Create("Character key").Slot);
            Server = new PresenceServer();
            Client = new SharingClient(PresenceServer.Deployment, Server, disposeHandler: false, new Version(0, 1, 9));
            Count = new OnlineCount(Client, RunWork, () => Now, CancellationToken.None, Log.Add, pace ?? Quick);
        }

        internal PersonaManager Personas { get; }

        internal PersonaRecord Key { get; }

        internal PresenceTarget Target => new(Key.Slot, Key.PublicKey.Id);

        internal PresenceServer Server { get; }

        internal SharingClient Client { get; }

        internal OnlineCount Count { get; }

        internal ConcurrentBag<string> Log { get; } = new();

        internal DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

        internal volatile bool SessionBusy;

        internal async Task WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "timed out");
                await Task.Delay(5);
            }
        }

        /// <summary>The persona session's stand-in: refused while busy, otherwise run on another thread, one at a time.</summary>
        private bool RunWork(string name, Action<PersonaManager> work)
        {
            if (SessionBusy)
            {
                return false;
            }

            _ = Task.Run(() =>
            {
                lock (Personas)
                {
                    work(Personas);
                }
            });
            return true;
        }

        public void Dispose()
        {
            Count.StopAsync(Patience).GetAwaiter().GetResult();
            Client.Dispose();
            Server.Dispose();
        }
    }

    /// <summary>
    /// The presence endpoints, answered in memory: starts checked as section 14.5 says for kind 9,
    /// tokens it issued, a key's new start replacing its earlier session as the server's does, a
    /// session past its hour, and what each request carried.
    /// </summary>
    private sealed class PresenceServer : HttpMessageHandler
    {
        internal static readonly DeploymentName Deployment = DeploymentName.Parse("plates.example.com");

        private readonly HashSet<string> challenges = new();
        private readonly HashSet<string> live = new();
        private readonly Dictionary<PersonaId, string> bySigner = new();
        private readonly Dictionary<string, TimeSpan> lastSeen = new();
        private readonly HashSet<string> pastTheHour = new();
        private int unknownBeats;
        private TimeSpan shortestGap = TimeSpan.MaxValue;

        /// <summary>
        /// The shortest time from a session's start or counted heartbeat to its next heartbeat, on a
        /// monotonic clock: the server refuses a heartbeat too soon after either.
        /// </summary>
        internal TimeSpan ShortestGap
        {
            get
            {
                lock (this)
                {
                    return shortestGap;
                }
            }
        }

        /// <summary>How many heartbeats named no session the server counts, each answered 404.</summary>
        internal int UnknownBeats
        {
            get
            {
                lock (this)
                {
                    return unknownBeats;
                }
            }
        }

        internal int Online { get; set; } = 1;

        internal HttpStatusCode StartStatus { get; set; } = HttpStatusCode.OK;

        internal HttpStatusCode BeatStatus { get; set; } = HttpStatusCode.OK;

        internal HttpStatusCode ChallengeStatus { get; set; } = HttpStatusCode.OK;

        internal Task? StartGate { get; set; }

        /// <summary>Held after the session is made and before the answer goes out: a start the server has already acted on.</summary>
        internal Task? StartAnswerGate { get; set; }

        /// <summary>Whether a start is waiting on <see cref="StartAnswerGate"/> with its session already made.</summary>
        internal volatile bool StartAnswerHeld;

        internal Task? ChallengeGate { get; set; }

        /// <summary>Blocks each leave before its first await, so a leave sent on the caller's own thread would hold that thread.</summary>
        internal ManualResetEventSlim? LeaveHold { get; set; }

        /// <summary>The thread the last leave was handled on.</summary>
        internal Thread? LeaveThread { get; set; }

        /// <summary>Whether the server still counts the session with <paramref name="token"/>.</summary>
        internal bool IsLive(byte[] token)
        {
            lock (this)
            {
                return live.Contains(Convert.ToHexString(token));
            }
        }

        private readonly List<string> paths = new();

        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        private readonly List<TimeSpan> startTimes = new();

        /// <summary>When each start arrived, on a monotonic clock.</summary>
        internal List<TimeSpan> StartTimes
        {
            get
            {
                lock (this)
                {
                    return startTimes.ToList();
                }
            }
        }

        internal List<string> Paths
        {
            get
            {
                lock (this)
                {
                    return paths.ToList();
                }
            }
        }

        private readonly List<PersonaId> signers = new();

        internal List<PersonaId> Signers
        {
            get
            {
                lock (this)
                {
                    return signers.ToList();
                }
            }
        }

        private readonly List<byte[]> tokens = new();

        internal List<byte[]> Tokens
        {
            get
            {
                lock (this)
                {
                    return tokens.ToList();
                }
            }
        }

        private readonly List<byte[]> beats = new();

        internal List<byte[]> Beats
        {
            get
            {
                lock (this)
                {
                    return beats.ToList();
                }
            }
        }

        private readonly List<byte[]> leaves = new();

        internal List<byte[]> Leaves
        {
            get
            {
                lock (this)
                {
                    return leaves.ToList();
                }
            }
        }

        /// <summary>
        /// The session with <paramref name="token"/> is past its hour, as the server sees it: its
        /// heartbeats are answered 404, and it is counted until a leave ends it or a new start of its
        /// key's replaces it.
        /// </summary>
        internal void EndHour(byte[] token)
        {
            lock (this)
            {
                pastTheHour.Add(Convert.ToHexString(token));
            }
        }

        /// <summary>A restart: every session is forgotten.</summary>
        internal void Forget()
        {
            lock (this)
            {
                live.Clear();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal(Deployment.Value, request.RequestUri.Host);
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            lock (this)
            {
                paths.Add(path);
                if (path == "/v1/presence")
                {
                    startTimes.Add(clock.Elapsed);
                }
            }

            switch (path)
            {
                case "/v1/presence/challenge":
                    if (ChallengeStatus != HttpStatusCode.OK)
                    {
                        return new HttpResponseMessage(ChallengeStatus);
                    }

                    if (ChallengeGate is { } challengeGate)
                    {
                        await challengeGate.WaitAsync(cancellationToken);
                    }

                    var challenge = RandomNumberGenerator.GetBytes(32);
                    lock (this)
                    {
                        challenges.Add(Convert.ToHexString(challenge));
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(challenge) };

                case "/v1/presence":
                    if (StartGate is { } startGate)
                    {
                        await startGate.WaitAsync(cancellationToken);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var length = BinaryPrimitives.ReadUInt16BigEndian(body);
                    var verified = RequestProofCodec.VerifyAction(body.AsSpan(2, length), body.AsSpan(2 + length), Deployment, RequestProofKind.Presence);
                    Assert.Equal("{}", Encoding.UTF8.GetString(body.AsSpan(2 + length)));
                    if (StartStatus != HttpStatusCode.OK)
                    {
                        return new HttpResponseMessage(StartStatus);
                    }

                    var token = RandomNumberGenerator.GetBytes(32);
                    lock (this)
                    {
                        Assert.True(challenges.Remove(Convert.ToHexString(verified.Challenge.ToArray())), "The start names a challenge this server didn't issue.");
                        signers.Add(verified.PublicKey.Id);
                        tokens.Add(token);
                        if (bySigner.TryGetValue(verified.PublicKey.Id, out var replaced))
                        {
                            live.Remove(replaced);
                        }

                        bySigner[verified.PublicKey.Id] = Convert.ToHexString(token);
                        live.Add(Convert.ToHexString(token));
                        lastSeen[Convert.ToHexString(token)] = clock.Elapsed;
                    }

                    // The session is made: from here the server counts the character whatever becomes
                    // of the answer, which is what the client's grace is for.
                    if (StartAnswerGate is { } answerGate)
                    {
                        StartAnswerHeld = true;
                        await answerGate.WaitAsync(cancellationToken);
                        StartAnswerHeld = false;
                    }

                    return Json($"{{\"session\":\"{Convert.ToBase64String(token)}\",\"online\":{Online}}}");

                case "/v1/presence/beat":
                    Assert.Equal(32, body.Length);
                    lock (this)
                    {
                        beats.Add(body);
                        if (!live.Contains(Convert.ToHexString(body)) || pastTheHour.Contains(Convert.ToHexString(body)))
                        {
                            unknownBeats++;
                            return new HttpResponseMessage(HttpStatusCode.NotFound);
                        }

                        var now = clock.Elapsed;
                        shortestGap = TimeSpan.FromTicks(Math.Min(shortestGap.Ticks, (now - lastSeen[Convert.ToHexString(body)]).Ticks));
                        lastSeen[Convert.ToHexString(body)] = now;
                    }

                    return BeatStatus == HttpStatusCode.OK ? Json($"{{\"online\":{Online}}}") : new HttpResponseMessage(BeatStatus);

                case "/v1/presence/leave":
                    LeaveThread = Thread.CurrentThread;
                    LeaveHold?.Wait(cancellationToken);
                    Assert.Equal(32, body.Length);
                    lock (this)
                    {
                        leaves.Add(body);
                        live.Remove(Convert.ToHexString(body));
                    }

                    return new HttpResponseMessage(HttpStatusCode.NoContent);

                default:
                    throw new InvalidOperationException("Only the presence paths are expected, never the general challenge: " + path);
            }
        }

        private static HttpResponseMessage Json(string text) =>
            new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
