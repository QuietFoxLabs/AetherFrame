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
        TimeSpan.FromMilliseconds(60), TimeSpan.Zero, TimeSpan.FromMilliseconds(240), TimeSpan.FromMilliseconds(20), TimeSpan.Zero, TimeSpan.FromSeconds(1));

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

        Assert.Equal(["/v1/challenge", "/v1/presence"], harness.Server.Paths.Take(2));
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
    public async Task AServerWithoutTheEndpoint_ReadsUnavailable_NeverZero_AndBacksOff()
    {
        using var harness = new Harness();
        harness.Server.StartStatus = HttpStatusCode.NotFound;
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Count.View.State == OnlineCountState.Unavailable);
        Assert.Equal(0, harness.Count.View.Count);

        // A minute, then two, then four (scaled down here): far fewer than one try a heartbeat.
        await Task.Delay(600);
        Assert.InRange(harness.Server.Paths.Count(path => path == "/v1/presence"), 1, 4);
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
        await Task.Delay(150);
        Assert.Equal(OnlineCountState.Connecting, harness.Count.View.State);
        Assert.DoesNotContain("/v1/presence", harness.Server.Paths);
        harness.SessionBusy = false;
        await harness.WaitFor(() => harness.Count.View.State == OnlineCountState.Online);
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
    public async Task AnAnswerAfterTheRunStopped_ChangesNothing_AndItsSessionIsLeft()
    {
        using var harness = new Harness();
        var release = new TaskCompletionSource();
        harness.Server.StartGate = release.Task;
        harness.Count.Update(harness.Target);
        await harness.WaitFor(() => harness.Server.Paths.Contains("/v1/presence"));
        harness.Count.Update(null);
        release.SetResult();

        // The request was stopped while it was sent; had the server answered, its session is left.
        await Task.Delay(200);
        Assert.Equal(OnlineCountState.Off, harness.Count.View.State);
        Assert.Empty(harness.Server.Beats);
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
        Assert.Contains(SharingText.OnlineCountSends, SharingText.OnlineNotice);
        Assert.Contains("only ever gives out the total", SharingText.OnlineCountSends, StringComparison.Ordinal);
        Assert.Contains("Characters that don't share send nothing", SharingText.OnlineCountSends, StringComparison.Ordinal);
    }

    /// <summary>A persona manager over memory with one character key, a presence server answered in memory, and the count over them.</summary>
    private sealed class Harness : IDisposable
    {
        internal Harness()
        {
            Personas = PersonaManager.Load(new ProtectedPersonaKeyStore(new MemoryKeyBlobs(), new MaskingProtector()), new NoBackups(), new MemoryRegistry());
            Key = Personas.Acknowledge(Personas.Create("Character key").Slot);
            Server = new PresenceServer();
            Client = new SharingClient(PresenceServer.Deployment, Server, disposeHandler: false, new Version(0, 1, 9));
            Count = new OnlineCount(Client, RunWork, () => Now, CancellationToken.None, Log.Add, Quick);
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

    /// <summary>The presence endpoints, answered in memory: starts checked as section 14.5 says for kind 9, tokens it issued, and what each request carried.</summary>
    private sealed class PresenceServer : HttpMessageHandler
    {
        internal static readonly DeploymentName Deployment = DeploymentName.Parse("plates.example.com");

        private readonly HashSet<string> challenges = new();
        private readonly HashSet<string> live = new();

        internal int Online { get; set; } = 1;

        internal HttpStatusCode StartStatus { get; set; } = HttpStatusCode.OK;

        internal HttpStatusCode BeatStatus { get; set; } = HttpStatusCode.OK;

        internal Task? StartGate { get; set; }

        internal Task? ChallengeGate { get; set; }

        private readonly List<string> paths = new();

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
            }

            switch (path)
            {
                case "/v1/challenge":
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
                        live.Add(Convert.ToHexString(token));
                    }

                    return Json($"{{\"session\":\"{Convert.ToBase64String(token)}\",\"online\":{Online}}}");

                case "/v1/presence/beat":
                    Assert.Equal(32, body.Length);
                    lock (this)
                    {
                        beats.Add(body);
                        if (!live.Contains(Convert.ToHexString(body)))
                        {
                            return new HttpResponseMessage(HttpStatusCode.NotFound);
                        }
                    }

                    return BeatStatus == HttpStatusCode.OK ? Json($"{{\"online\":{Online}}}") : new HttpResponseMessage(BeatStatus);

                case "/v1/presence/leave":
                    Assert.Equal(32, body.Length);
                    lock (this)
                    {
                        leaves.Add(body);
                        live.Remove(Convert.ToHexString(body));
                    }

                    return new HttpResponseMessage(HttpStatusCode.NoContent);

                default:
                    throw new InvalidOperationException("Only the presence paths and the challenge are expected: " + path);
            }
        }

        private static HttpResponseMessage Json(string text) =>
            new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
