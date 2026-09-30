using System;
using System.Buffers.Binary;
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
/// Opting characters in and out (N2-9b), against a server answered in memory that checks every
/// request's proof as the real one does: each is signed by the character's own key, for its path.
/// </summary>
public class CharacterSharingTests
{
    private const ulong Aria = 0x0040_0000_1234_5678;
    private const ulong Bram = 0x0040_0000_8765_4321;
    private const string Code = "AF-0123456789";
    private static readonly ProfileId Profile = ProfileId.Parse("prf_0123456789abcdef0123456789abcdef");

    [Fact]
    public void TurningSharingOn_MakesTheCharactersOwnKey_ThenAsksForACode()
    {
        using var harness = new SharingHarness();
        Assert.True(harness.Sharing.TryStart(Aria, newKey: false));

        var entry = Assert.IsType<SharingCharacter>(harness.Sharing.View.Find(Aria));
        Assert.Equal(SharingStage.Checking, entry.Stage);
        Assert.True(harness.Personas.TryGet(entry.Slot, out var persona));
        Assert.Equal(CharacterSharing.KeyLabel, persona!.Label);
        Assert.True(persona.Acknowledged);
        Assert.Equal(entry.Key, persona.PublicKey.Id);

        Assert.Equal(new IssuedCode(Aria, Code, harness.Now.AddHours(1)), harness.Sharing.View.Code);
        Assert.Equal(SharingNoticeKind.CodeReady, harness.Sharing.View.Notice!.Kind);
        Assert.Equal([entry], harness.File.Read());

        var request = Assert.Single(harness.Server.Actions);
        Assert.Equal(("/v1/lodestone/code", entry.Key, "{}"), (request.Path, request.Signer, request.Body));
        Assert.Equal(1, harness.Server.StatusRequests);
    }

    [Fact]
    public void ThePassedCheck_BindsTheCharacter_AndIsSaved()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        Assert.True(harness.Sharing.TryCheck(Aria, "12345678"));

        var entry = harness.Sharing.View.Find(Aria)!;
        Assert.Equal((SharingStage.Shared, "12345678", (ProfileId?)Profile, "Aria Starfall", "Gilgamesh"), (entry.Stage, entry.LodestoneId, entry.ProfileId, entry.Name, entry.World));
        Assert.Null(harness.Sharing.View.Code);
        Assert.Equal(SharingNoticeKind.CheckPassed, harness.Sharing.View.Notice!.Kind);
        Assert.Equal([entry], new SharingStateFile(harness.Root).Read());

        var check = harness.Server.Actions.Last();
        Assert.Equal(("/v1/lodestone/check", entry.Key, "{\"lodestoneId\":\"12345678\",\"code\":\"" + Code + "\"}"), (check.Path, check.Signer, check.Body));
        Assert.Equal(1, harness.Server.StatusRequests);
    }

    [Fact]
    public void AFailedCheck_KeepsTheCode_SoThePlayerCanFixTheProfileAndCheckAgain()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Server.Answers["/v1/lodestone/check"] = _ => (HttpStatusCode.UnprocessableEntity, null);
        harness.Sharing.TryCheck(Aria, "12345678");

        Assert.Equal(SharingStage.Checking, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal(SharingNoticeKind.CheckFailed, harness.Sharing.View.Notice!.Kind);
        Assert.Equal(Code, harness.Sharing.View.Code!.Code);

        harness.Server.Answers.Remove("/v1/lodestone/check");
        harness.Sharing.TryCheck(Aria, "12345678");
        Assert.Equal(SharingStage.Shared, harness.Sharing.View.Find(Aria)!.Stage);
    }

    [Fact]
    public void TurningSharingOff_DeletesOnTheServer_AndKeepsTheKeyForNextTime()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678");
        var slot = harness.Sharing.View.Find(Aria)!.Slot;

        Assert.True(harness.Sharing.TryTurnOff(Aria));
        var off = harness.Sharing.View.Find(Aria)!;
        Assert.Equal(new SharingCharacter(Aria, slot, off.Key, SharingStage.Off), off);
        Assert.Equal(SharingNoticeKind.TurnedOff, harness.Sharing.View.Notice!.Kind);
        Assert.Equal(("/v1/opt-out", "{}"), (harness.Server.Actions.Last().Path, harness.Server.Actions.Last().Body));
        Assert.Equal([off], harness.File.Read());

        harness.Sharing.TryStart(Aria, newKey: false);
        Assert.Equal(slot, harness.Sharing.View.Find(Aria)!.Slot);
        Assert.Single(harness.Personas.Personas);
    }

    [Fact]
    public void EachCharacter_HasAKeyOfItsOwn_AndTurningEverythingOffUsesEach()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678");
        harness.Sharing.TryStart(Bram, newKey: false);
        harness.Sharing.TryCheck(Bram, "23456789");
        var aria = harness.Sharing.View.Find(Aria)!;
        var bram = harness.Sharing.View.Find(Bram)!;
        Assert.NotEqual(aria.Slot, bram.Slot);
        Assert.NotEqual(aria.Key, bram.Key);

        Assert.True(harness.Sharing.TryTurnOffAll());
        Assert.All(harness.Sharing.View.Characters, character => Assert.Equal(SharingStage.Off, character.Stage));
        var optOuts = harness.Server.Actions.Where(action => action.Path == "/v1/opt-out").Select(action => action.Signer).ToList();
        Assert.Equal([aria.Key, bram.Key], optOuts);
    }

    [Fact]
    public void AnOutdatedAetherFrame_SendsNothingSigned()
    {
        using var harness = new SharingHarness();
        harness.Server.MinimumPlugin = "9.9.9";
        harness.Sharing.TryStart(Aria, newKey: false);

        Assert.Equal(SharingNoticeKind.UpdateNeeded, harness.Sharing.View.Notice!.Kind);
        Assert.Empty(harness.Server.Actions);
        Assert.Equal(0, harness.Server.Challenges);
        Assert.Null(harness.Sharing.View.Code);
    }

    [Fact]
    public void AnUnreachableServer_IsANotice_AndNothingIsLost()
    {
        using var harness = new SharingHarness();
        harness.Server.Unreachable = true;
        harness.Sharing.TryStart(Aria, newKey: false);

        Assert.Equal(SharingNoticeKind.Unreachable, harness.Sharing.View.Notice!.Kind);
        Assert.Equal(SharingStage.Checking, harness.Sharing.View.Find(Aria)!.Stage);

        harness.Server.Unreachable = false;
        Assert.True(harness.Sharing.TryNewCode(Aria));
        Assert.Equal(Code, harness.Sharing.View.Code!.Code);
    }

    [Fact]
    public void ARereadFollowsARename_OnlyWhenTheGameShowsAnotherNameOrWorld()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678");
        var before = harness.Server.Actions.Count;

        harness.Sharing.TryReread(Aria, "aria  STARFALL", "gilgamesh");
        Assert.Equal(before, harness.Server.Actions.Count);

        harness.Server.Answers["/v1/lodestone/reread"] = _ => (HttpStatusCode.OK, "{\"name\":\"Aria Moonfall\",\"world\":\"Cactuar\"}");
        harness.Sharing.TryReread(Aria, "Aria Moonfall", "Cactuar");
        var entry = harness.Sharing.View.Find(Aria)!;
        Assert.Equal(("Aria Moonfall", "Cactuar", SharingStage.Shared), (entry.Name, entry.World, entry.Stage));
        Assert.Equal(SharingNoticeKind.Renamed, harness.Sharing.View.Notice!.Kind);
    }

    [Fact]
    public void ARereadTheServerCantPlace_EndsSharingHere()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678");
        harness.Sharing.TryStart(Bram, newKey: false);
        harness.Sharing.TryCheck(Bram, "23456789");

        harness.Server.Answers["/v1/lodestone/reread"] = _ => (HttpStatusCode.NotFound, null);
        harness.Sharing.TryReread(Aria, "Aria Moonfall", "Gilgamesh");
        Assert.Equal(SharingStage.Off, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal(SharingNoticeKind.NoLongerBound, harness.Sharing.View.Notice!.Kind);

        harness.Server.Answers["/v1/lodestone/reread"] = _ => (HttpStatusCode.Gone, null);
        harness.Sharing.TryReread(Bram, "Bram Oaks", "Gilgamesh");
        var bram = harness.Sharing.View.Find(Bram)!;
        Assert.Equal(new SharingCharacter(Bram, bram.Slot, bram.Key, SharingStage.TakenOver), bram);
        Assert.Equal(new SharingNotice(Bram, SharingNoticeKind.TakenOver), harness.Sharing.View.Notice);
        Assert.Equal(harness.Sharing.View.Characters, harness.File.Read());
    }

    [Fact]
    public void AKeyThatCantBeOpened_SendsNothing_AndANewKeyStartsAgain()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        var lost = harness.Sharing.View.Find(Aria)!;
        harness.Blobs.Forget(lost.Slot);
        var sent = harness.Server.Actions.Count;

        harness.Sharing.TryNewCode(Aria);
        Assert.Equal(new SharingNotice(Aria, SharingNoticeKind.KeyUnavailable), harness.Sharing.View.Notice);
        Assert.Equal(sent, harness.Server.Actions.Count);

        harness.Sharing.TryStart(Aria, newKey: true);
        var fresh = harness.Sharing.View.Find(Aria)!;
        Assert.NotEqual(lost.Slot, fresh.Slot);
        Assert.Equal(SharingStage.Checking, fresh.Stage);
        Assert.Equal(fresh.Key, harness.Server.Actions.Last().Signer);
    }

    [Fact]
    public void AnUnreadableSharingFile_KeepsSharingOff_AndIsNeverWrittenOver()
    {
        using var harness = new SharingHarness(load: false);
        Directory.CreateDirectory(harness.Root);
        var path = Path.Combine(harness.Root, SharingStateFile.FileName);
        File.WriteAllText(path, "not json");

        Assert.True(harness.Sharing.TryLoad());
        Assert.True(harness.Sharing.View.Unreadable);
        Assert.False(harness.Sharing.TryStart(Aria, newKey: false));
        Assert.Equal("not json", File.ReadAllText(path));
        Assert.Empty(harness.Server.Actions);
    }

    [Fact]
    public void ASaveThatFails_SendsNothing()
    {
        using var harness = new SharingHarness();
        Directory.CreateDirectory(Path.Combine(harness.Root, SharingStateFile.FileName + ".tmp"));

        harness.Sharing.TryStart(Aria, newKey: false);
        Assert.Equal(SharingNoticeKind.SaveFailed, harness.Sharing.View.Notice!.Kind);
        Assert.Null(harness.Sharing.View.Find(Aria));
        Assert.Empty(harness.Server.Actions);
    }

    [Fact]
    public void TheLog_NamesNoCharacterCodeOrKey()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Server.Answers["/v1/lodestone/check"] = _ => (HttpStatusCode.UnprocessableEntity, null);
        harness.Sharing.TryCheck(Aria, "12345678");
        harness.Server.Answers.Remove("/v1/lodestone/check");
        harness.Sharing.TryCheck(Aria, "12345678");
        harness.Server.Unreachable = true;
        harness.Sharing.TryTurnOff(Aria);
        harness.Server.Unreachable = false;
        harness.Sharing.TryTurnOff(Aria);

        var entry = harness.Sharing.View.Find(Aria)!;
        var log = string.Join("\n", harness.Log);
        Assert.NotEmpty(harness.Log);
        foreach (var secret in new[] { Aria.ToString(), "12345678", Code, "Aria", "Gilgamesh", entry.Key.ToString(), entry.Slot.ToString(), Profile.ToString() })
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A persona manager over memory, a scratch persona folder, and the service over a server answered in memory.</summary>
    private sealed class SharingHarness : IDisposable
    {
        internal SharingHarness(bool load = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "aetherframe-sharing-" + Guid.NewGuid().ToString("N"));
            Blobs = new MemoryKeyBlobs();
            Personas = PersonaManager.Load(new ProtectedPersonaKeyStore(Blobs, new MaskingProtector()), new NoBackups(), new MemoryRegistry());
            File = new SharingStateFile(Root);
            Server = new FakeSharingServer();
            Client = new SharingClient(FakeSharingServer.Deployment, Server, disposeHandler: false, new Version(0, 1, 7));
            Sharing = new CharacterSharing((_, work) =>
            {
                work(Personas);
                return true;
            }, File, Client, new Version(0, 1, 7), () => Now, CancellationToken.None, Log.Add);
            if (load)
            {
                Assert.True(Sharing.TryLoad());
                Assert.True(Sharing.View.Loaded);
            }
        }

        internal string Root { get; }

        internal MemoryKeyBlobs Blobs { get; }

        internal PersonaManager Personas { get; }

        internal SharingStateFile File { get; }

        internal FakeSharingServer Server { get; }

        internal SharingClient Client { get; }

        internal CharacterSharing Sharing { get; }

        internal List<string> Log { get; } = new();

        internal DateTimeOffset Now { get; } = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    /// <summary>One signed action the server accepted: its path, its signer and its body.</summary>
    private sealed record SeenAction(string Path, PersonaId Signer, string Body);

    /// <summary>
    /// The sharing server's interface, answered in memory: challenges it issued, proofs checked as
    /// section 14.5 says for the path's kind, and each path's answer, which a test may replace.
    /// </summary>
    private sealed class FakeSharingServer : HttpMessageHandler
    {
        internal static readonly DeploymentName Deployment = DeploymentName.Parse("plates.example.com");

        private static readonly Dictionary<string, RequestProofKind> Kinds = new()
        {
            ["/v1/lodestone/code"] = RequestProofKind.LodestoneCode,
            ["/v1/lodestone/check"] = RequestProofKind.LodestoneCheck,
            ["/v1/lodestone/reread"] = RequestProofKind.LodestoneReread,
            ["/v1/opt-out"] = RequestProofKind.OptOut,
        };

        private readonly HashSet<string> issued = new();

        internal Dictionary<string, Func<string, (HttpStatusCode Status, string? Body)>> Answers { get; } = new();

        internal List<SeenAction> Actions { get; } = new();

        internal string MinimumPlugin { get; set; } = "0.1.6";

        internal bool Unreachable { get; set; }

        internal int StatusRequests { get; private set; }

        internal int Challenges { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable)
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
            }

            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal(Deployment.Value, request.RequestUri.Host);
            var path = request.RequestUri.AbsolutePath;
            if (path == "/v1/status")
            {
                StatusRequests++;
                return Answer(HttpStatusCode.OK, $"{{\"protocolVersion\":32769,\"api\":1,\"minimumPlugin\":\"{MinimumPlugin}\"}}");
            }

            if (path == "/v1/challenge")
            {
                Challenges++;
                var challenge = RandomNumberGenerator.GetBytes(32);
                issued.Add(Convert.ToHexString(challenge));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(challenge) };
            }

            var envelope = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var length = BinaryPrimitives.ReadUInt16BigEndian(envelope);
            var proof = envelope.AsSpan(2, length).ToArray();
            var body = envelope.AsSpan(2 + length).ToArray();
            var verified = RequestProofCodec.VerifyAction(proof, body, Deployment, Kinds[path]);
            Assert.True(issued.Remove(Convert.ToHexString(verified.Challenge.ToArray())), "The proof names a challenge this server didn't issue, or one already used.");

            var text = Encoding.UTF8.GetString(body);
            Actions.Add(new SeenAction(path, verified.PublicKey.Id, text));
            var (status, answer) = Answers.TryGetValue(path, out var scripted) ? scripted(text) : Default(path);
            return Answer(status, answer);
        }

        private static (HttpStatusCode, string?) Default(string path) => path switch
        {
            "/v1/lodestone/code" => (HttpStatusCode.OK, $"{{\"code\":\"{Code}\",\"expiresInSeconds\":3600}}"),
            "/v1/lodestone/check" => (HttpStatusCode.OK, $"{{\"profileId\":\"{Profile}\",\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}}"),
            "/v1/lodestone/reread" => (HttpStatusCode.OK, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}"),
            "/v1/opt-out" => (HttpStatusCode.NoContent, null),
            _ => (HttpStatusCode.NotFound, null),
        };

        private static HttpResponseMessage Answer(HttpStatusCode status, string? body) =>
            new(status) { Content = new ByteArrayContent(body is null ? [] : Encoding.UTF8.GetBytes(body)) };
    }
}
