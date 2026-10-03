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
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Requests;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.Services.Network.Transport;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Opting characters in and out (N2-9b), against a server answered in memory that checks every
/// request's proof as the real one does: each is signed by the character's own key, for its path.
/// </summary>
public partial class CharacterSharingTests
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

        Assert.Equal(new IssuedCode(Aria, entry.Slot, Code, harness.Now.AddHours(1)), harness.Sharing.View.Code);
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
        Assert.True(harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh"));

        var entry = harness.Sharing.View.Find(Aria)!;
        Assert.Equal((SharingStage.Shared, "12345678", (ProfileId?)Profile, "Aria Starfall", "Gilgamesh"), (entry.Stage, entry.LodestoneId, entry.ProfileId, entry.Name, entry.World));
        Assert.Null(harness.Sharing.View.Code);
        Assert.Equal(SharingNoticeKind.CheckPassed, harness.Sharing.View.Notice!.Kind);
        Assert.Equal([entry], new SharingStateFile(harness.Root).Read());

        var check = harness.Server.Actions.Last();
        Assert.Equal(("/v1/lodestone/check", entry.Key, "{\"lodestoneId\":\"12345678\",\"code\":\"" + Code + "\",\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}"), (check.Path, check.Signer, check.Body));
        Assert.Equal(1, harness.Server.StatusRequests);
    }

    [Fact]
    public void AFailedCheck_KeepsTheCode_SoThePlayerCanFixTheProfileAndCheckAgain()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Server.Answers["/v1/lodestone/check"] = _ => (HttpStatusCode.UnprocessableEntity, null);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");

        Assert.Equal(SharingStage.Checking, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal(SharingNoticeKind.CheckFailed, harness.Sharing.View.Notice!.Kind);
        Assert.Equal(Code, harness.Sharing.View.Code!.Code);

        harness.Server.Answers.Remove("/v1/lodestone/check");
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        Assert.Equal(SharingStage.Shared, harness.Sharing.View.Find(Aria)!.Stage);
    }

    [Fact]
    public void TurningSharingOff_DeletesOnTheServer_AndKeepsTheKeyForNextTime()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
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
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        harness.Sharing.TryStart(Bram, newKey: false);
        harness.Sharing.TryCheck(Bram, "23456789", "Bram Oakes", "Gilgamesh");
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
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
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
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        harness.Sharing.TryStart(Bram, newKey: false);
        harness.Sharing.TryCheck(Bram, "23456789", "Bram Oakes", "Gilgamesh");

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

        var challenges = harness.Server.Challenges;
        harness.Sharing.TryNewCode(Aria);
        Assert.Equal(new SharingNotice(Aria, SharingNoticeKind.KeyUnavailable), harness.Sharing.View.Notice);
        Assert.Equal((sent, challenges), (harness.Server.Actions.Count, harness.Server.Challenges));

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
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        harness.Server.Answers.Remove("/v1/lodestone/check");
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
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

    [Fact]
    public void TurningEverythingOff_GoesOnPastACharacterThatFails_AndSaysSo()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        harness.Sharing.TryStart(Bram, newKey: false);
        harness.Sharing.TryCheck(Bram, "23456789", "Bram Oakes", "Gilgamesh");
        harness.Blobs.Forget(harness.Sharing.View.Find(Aria)!.Slot);

        harness.Sharing.TryTurnOffAll();
        Assert.Equal(SharingStage.Shared, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal(SharingStage.Off, harness.Sharing.View.Find(Bram)!.Stage);
        Assert.Equal(new SharingNotice(0, SharingNoticeKind.TurnOffIncomplete), harness.Sharing.View.Notice);
    }

    [Fact]
    public void AnOutdatedAetherFrame_CanStillTurnSharingOff()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        harness.Server.MinimumPlugin = "9.9.9";
        harness.Restart();
        var statuses = harness.Server.StatusRequests;

        harness.Sharing.TryTurnOff(Aria);
        Assert.Equal(SharingStage.Off, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal("/v1/opt-out", harness.Server.Actions.Last().Path);
        Assert.Equal(statuses, harness.Server.StatusRequests);
    }

    [Fact]
    public void AServerOfAnotherProtocolOrAMalformedVersion_SendsNothingSigned()
    {
        foreach (var (minimum, notice) in new[] { ("0.1.6.0", SharingNoticeKind.Refused), (" 0.1.6", SharingNoticeKind.Refused), ("0.1.8", SharingNoticeKind.UpdateNeeded) })
        {
            using var harness = new SharingHarness();
            harness.Server.MinimumPlugin = minimum;
            harness.Sharing.TryStart(Aria, newKey: false);
            Assert.Equal(notice, harness.Sharing.View.Notice!.Kind);
            Assert.Empty(harness.Server.Actions);
        }

        using var other = new SharingHarness();
        other.Server.Protocol = 1;
        other.Sharing.TryStart(Aria, newKey: false);
        Assert.Equal(SharingNoticeKind.UpdateNeeded, other.Sharing.View.Notice!.Kind);
        Assert.Empty(other.Server.Actions);
    }

    [Fact]
    public void ARereadAnsweredTakenOver_IsNotFollowedByAnOptOut()
    {
        // The server answers a re-read that a takeover overtook with 410: the plugin records the
        // takeover and sends nothing more, so the server keeps the mark it says so with.
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        harness.Server.Answers["/v1/lodestone/reread"] = _ => (HttpStatusCode.Gone, null);

        harness.Sharing.TryReread(Aria, "Aria Moonfall", "Gilgamesh");

        Assert.Equal("/v1/lodestone/reread", harness.Server.Actions.Last().Path);
        Assert.DoesNotContain(harness.Server.Actions, action => action.Path == "/v1/opt-out");
        Assert.Equal(SharingStage.TakenOver, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal(SharingNoticeKind.TakenOver, harness.Sharing.View.Notice!.Kind);
    }

    [Fact]
    public void ARereadTheServerAnswersNotFound_OptsOutBeforeSharingIsRecordedOff()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        harness.Server.Answers["/v1/lodestone/reread"] = _ => (HttpStatusCode.NotFound, null);

        harness.Sharing.TryReread(Aria, "Aria Moonfall", "Gilgamesh");
        Assert.Equal(["/v1/lodestone/reread", "/v1/opt-out"], harness.Server.Actions.Skip(harness.Server.Actions.Count - 2).Select(action => action.Path));
        Assert.Equal(SharingStage.Off, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal(SharingNoticeKind.NoLongerBound, harness.Sharing.View.Notice!.Kind);
    }

    [Fact]
    public void ACheckAnsweredForAnotherCharacter_IsUndone_AndNothingIsRecorded()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Server.Answers["/v1/lodestone/check"] = body => (HttpStatusCode.OK, FakeSharingServer.CheckAnswer(body, "Bram Oakes"));

        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        Assert.Equal("/v1/opt-out", harness.Server.Actions.Last().Path);
        Assert.Equal(SharingStage.Checking, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal(SharingNoticeKind.CheckFailed, harness.Sharing.View.Notice!.Kind);
    }

    [Fact]
    public void ANewKeyForABoundCharacter_KeepsTheBindingUntilItsCheckPasses()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        var bound = harness.Sharing.View.Find(Aria)!;
        harness.Blobs.Forget(bound.Slot);

        harness.Sharing.TryStart(Aria, newKey: true);
        var replacing = harness.Sharing.View.Find(Aria)!;
        Assert.Equal((bound.Slot, bound.LodestoneId, bound.ProfileId, SharingStage.Shared), (replacing.Slot, replacing.LodestoneId, replacing.ProfileId, replacing.Stage));
        Assert.True(replacing.ReplacingKey);
        Assert.Equal(replacing.NewSlot, harness.Sharing.View.Code!.Slot);
        Assert.Equal(replacing.NewKey, harness.Server.Actions.Last().Signer);

        harness.Sharing.TryCancelCheck(Aria);
        Assert.Equal(bound, harness.Sharing.View.Find(Aria));
        Assert.Equal(SharingNoticeKind.NewKeyDropped, harness.Sharing.View.Notice!.Kind);
        Assert.Equal([bound], harness.File.Read());

        harness.Sharing.TryStart(Aria, newKey: true);
        var newKey = harness.Sharing.View.Find(Aria)!.NewKey;
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        var moved = harness.Sharing.View.Find(Aria)!;
        Assert.Equal((newKey, SharingStage.Shared, false), (moved.Key, moved.Stage, moved.ReplacingKey));
    }

    [Fact]
    public void TurningSharingOnAgain_AfterTheFileWasMovedAside_NeverReusesAKeyTheServerMayStillBind()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        var aria = harness.Sharing.View.Find(Aria)!;
        File.Delete(Path.Combine(harness.Root, SharingStateFile.FileName));
        harness.Restart();

        harness.Sharing.TryStart(Bram, newKey: false);
        var bram = harness.Sharing.View.Find(Bram)!;
        Assert.NotEqual(aria.Slot, bram.Slot);
        Assert.NotEqual(aria.Key, bram.Key);
        Assert.Equal(bram.Key, harness.Server.Actions.Last().Signer);

        harness.Sharing.TryCancelCheck(Bram);
        Assert.All(harness.Server.Actions.Where(action => action.Path == "/v1/opt-out"), action => Assert.NotEqual(aria.Key, action.Signer));
    }

    [Fact]
    public void TheSelectionAPersonaHad_IsPutBack()
    {
        using var harness = new SharingHarness();
        var mine = harness.Personas.Create("Mine");
        harness.Personas.Select(mine.Slot);

        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        Assert.Equal(mine.Slot, harness.Personas.Active!.Slot);

        harness.Personas.Deselect();
        harness.Sharing.TryTurnOff(Aria);
        Assert.Null(harness.Personas.Active);
    }

    [Fact]
    public void APlateNeverSharedBefore_IsSignedUnderTheBindingsProfile_AndSent_WithNothingShownFirst()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        var candidate = PublicationCandidates.Simple();

        Assert.True(harness.Publish(candidate));

        var published = Assert.Single(harness.Server.Publishes);
        Assert.Equal((entry.Key, Profile, 1), (published.Signer, published.Snapshot.ProfileId, published.Images));
        Assert.Equal(SharingNoticeKind.Published, harness.Sharing.View.Notice!.Kind);
        Assert.Equal(candidate.PlateId, harness.Sharing.View.Find(Aria)!.PublishedPlate);
        var recorded = Assert.Single(harness.Index(entry).Entries);
        Assert.Equal((candidate.PlateId, Profile, PublicationState.Published, published.Snapshot.RevisionId), (recorded.PlateId, recorded.ProfileId, recorded.State, recorded.LatestRevision));
        Assert.True(recorded.PendingEntry.IsNone);
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);
    }

    [Fact]
    public void SavingTheSharedPlateAgain_OrMakingAnotherPlateActive_SendsANewRevisionWithoutAsking()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        var plate = Guid.NewGuid();
        harness.Publish(PublicationCandidates.Simple(plate));

        harness.Publish(PublicationCandidates.Simple(plate, "Edited"));
        Assert.Equal(2, harness.Server.Publishes.Count);
        Assert.NotEqual(harness.Server.Publishes[0].Snapshot.RevisionId, harness.Server.Publishes[1].Snapshot.RevisionId);

        // Another Plate, made Active: shared at once, under the same profile, in the first one's place.
        var other = PublicationCandidates.Simple();
        harness.Publish(other);
        Assert.Equal(3, harness.Server.Publishes.Count);
        Assert.All(harness.Server.Publishes, publish => Assert.Equal(Profile, publish.Snapshot.ProfileId));
        Assert.Equal(other.PlateId, Assert.Single(harness.Index(entry).Entries).PlateId);
        Assert.Equal(other.PlateId, harness.Sharing.View.Find(Aria)!.PublishedPlate);
    }

    [Fact]
    public void ACharacterThatDoesntShare_SignsAndSendsNothing()
    {
        using var harness = new SharingHarness();
        var candidate = PublicationCandidates.Simple();

        // Never turned on, then turned on but not checked yet, then turned off.
        harness.Publish(candidate);
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Publish(candidate);
        Assert.Equal(SharingStage.Checking, harness.Sharing.View.Find(Aria)!.Stage);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        harness.Sharing.TryTurnOff(Aria);
        harness.Publish(candidate);
        harness.Sharing.TrySendWaiting(Aria);

        Assert.Empty(harness.Server.Publishes);
        Assert.Empty(harness.Index(harness.Sharing.View.Find(Aria)!).Entries);

        // Each of those ended with nothing to say, so the progress window has nothing to show.
        Assert.Equal((PublishStep.Ended, null), (harness.Sharing.View.Publish!.Step, harness.Sharing.View.Publish.Outcome));
    }

    [Fact]
    public void ARefusedPublish_IsDropped_AndSaysWhy()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        harness.Server.PublishAnswer = () => (HttpStatusCode.UnprocessableEntity, "image-refused");
        harness.Publish(PublicationCandidates.Simple());

        Assert.Equal(new SharingNotice(Aria, SharingNoticeKind.PublishRefused, "image-refused"), harness.Sharing.View.Notice);
        Assert.Empty(harness.Index(entry).Entries);
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);
    }

    [Fact]
    public void ABusyServer_LeavesTheRevisionWaiting_UntilItIsSentOrTooOld()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        harness.Server.PublishAnswer = () => (HttpStatusCode.ServiceUnavailable, null);
        harness.Publish(PublicationCandidates.Simple());
        Assert.Equal(SharingNoticeKind.PublishWaiting, harness.Sharing.View.Notice!.Kind);
        var waiting = Assert.Single(harness.Index(entry).Entries);
        Assert.Equal(PublicationState.Pending, waiting.State);

        harness.Server.PublishAnswer = () => (HttpStatusCode.NoContent, null);
        Assert.True(harness.Sharing.TrySendWaiting(Aria));
        Assert.Equal(SharingNoticeKind.Published, harness.Sharing.View.Notice!.Kind);
        Assert.Equal(waiting.LatestRevision, harness.Server.Publishes.Last().Snapshot.RevisionId);

        harness.Server.PublishAnswer = () => (HttpStatusCode.ServiceUnavailable, null);
        harness.Publish(PublicationCandidates.Simple(waiting.PlateId, "Later"));
        harness.Now = harness.Now.AddDays(2);
        harness.Sharing.TrySendWaiting(Aria);
        Assert.Equal(SharingNoticeKind.PublishStale, harness.Sharing.View.Notice!.Kind);
        var kept = Assert.Single(harness.Index(entry).Entries);
        Assert.Equal((PublicationState.Published, true), (kept.State, kept.PendingEntry.IsNone));
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);
    }

    [Fact]
    public void APublishToACharacterTakenOver_RecordsIt_AndForgetsWhatWasPublished()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        harness.Publish(PublicationCandidates.Simple());
        harness.Server.PublishAnswer = () => (HttpStatusCode.Gone, null);
        harness.Publish(PublicationCandidates.Simple(Assert.Single(harness.Index(entry).Entries).PlateId));

        Assert.Equal(SharingStage.TakenOver, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal(SharingNoticeKind.TakenOver, harness.Sharing.View.Notice!.Kind);
        Assert.Empty(harness.Index(entry).Entries);
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);
    }

    [Fact]
    public void PausingAndResuming_SendNothingWhilePaused_AndShareAgainAfter()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        var plate = Guid.NewGuid();
        harness.Publish(PublicationCandidates.Simple(plate));

        Assert.True(harness.Sharing.TryPause(Aria));
        Assert.Equal(("/v1/opt-out", "{\"mode\":\"pause\"}"), (harness.Server.Actions.Last().Path, harness.Server.Actions.Last().Body));
        Assert.Equal(SharingStage.Paused, harness.Sharing.View.Find(Aria)!.Stage);
        harness.Publish(PublicationCandidates.Simple(plate, "While paused"));
        Assert.Single(harness.Server.Publishes);

        Assert.True(harness.Sharing.TryResume(Aria));
        Assert.Equal(SharingStage.Shared, harness.Sharing.View.Find(Aria)!.Stage);
        harness.Publish(PublicationCandidates.Simple(plate, "Back"));
        Assert.Equal(2, harness.Server.Publishes.Count);
        Assert.Equal(entry.ProfileId, harness.Server.Publishes.Last().Snapshot.ProfileId);
    }

    [Fact]
    public void TurningSharingOff_ForgetsWhatThisPcPublished()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        harness.Server.PublishAnswer = () => (HttpStatusCode.ServiceUnavailable, null);
        harness.Publish(PublicationCandidates.Simple());
        Assert.NotEmpty(harness.Publications.ListOutbox(entry.Slot).Entries);

        harness.Sharing.TryTurnOff(Aria);
        Assert.Empty(harness.Index(entry).Entries);
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);
    }

    /// <summary>A persona manager over memory, a scratch persona folder, and the service over a server answered in memory.</summary>
    private sealed class SharingHarness : IDisposable
    {
        /// <param name="load">Whether to read the sharing file at once.</param>
        /// <param name="background">Whether the service's operations run on another thread, as the persona session runs them, instead of to their end inside each call.</param>
        internal SharingHarness(bool load = true, bool background = false)
        {
            Background = background;
            ActivePlateOf = ActiveIn;
            Log = new List<string>();
            Root = Path.Combine(Path.GetTempPath(), "aetherframe-sharing-" + Guid.NewGuid().ToString("N"));
            Blobs = new MemoryKeyBlobs();
            Personas = PersonaManager.Load(new ProtectedPersonaKeyStore(Blobs, new MaskingProtector()), new NoBackups(), new MemoryRegistry());
            File = new SharingStateFile(Root);
            Publications = new PublicationFiles(Root);
            Server = new FakeSharingServer();
            // The pipe's connection to the Lodestone is a stand-in: nothing a test runs reaches it.
            Client = new SharingClient(FakeSharingServer.Deployment, Server, disposeHandler: false, new Version(0, 1, 7), () =>
            {
                var link = NextLink();
                Links.Add(link);
                return link;
            });
            Sharing = new CharacterSharing(RunWork, File, Publications, Client, new Version(0, 1, 7), () =>
            {
                ClockHook?.Invoke();
                return Now;
            }, CancellationToken.None, Log.Add, contentId => ActivePlateOf(contentId));
            if (load)
            {
                Assert.True(Sharing.TryLoad());
                WaitIdle();
                Assert.True(Sharing.View.Loaded);
            }
        }

        internal bool Background { get; }

        /// <summary>The Active Plates <see cref="Actives"/> holds: the lookup until a test replaces it.</summary>
        private Guid? ActiveIn(ulong contentId) => Actives.TryGetValue(contentId, out var plate) ? plate : null;

        internal string Root { get; }

        internal MemoryKeyBlobs Blobs { get; }

        internal PersonaManager Personas { get; }

        internal SharingStateFile File { get; }

        internal PublicationFiles Publications { get; }

        internal FakeSharingServer Server { get; }

        internal SharingClient Client { get; }

        /// <summary>Makes each pipe's stand-in connection: one that reports a global address unless a test says otherwise.</summary>
        internal Func<FakeLodestoneLink> NextLink { get; set; } = () => new FakeLodestoneLink(IPAddress.Parse("104.18.32.1"));

        /// <summary>Every stand-in connection a pipe made, in order.</summary>
        internal List<FakeLodestoneLink> Links { get; } = new();

        internal CharacterSharing Sharing { get; private set; }

        /// <summary>A new session over the same files, keys and server, as after a reload: nothing is remembered but what was saved.</summary>
        internal void Restart()
        {
            Sharing = new CharacterSharing(RunWork, File, Publications, Client, new Version(0, 1, 7), () =>
            {
                ClockHook?.Invoke();
                return Now;
            }, CancellationToken.None, Log.Add, contentId => ActivePlateOf(contentId));
            Assert.True(Sharing.TryLoad());
            WaitIdle();
        }

        /// <summary>Waits until no operation of the service runs: at once when operations run inside each call.</summary>
        internal void WaitIdle()
        {
            var deadline = DateTime.UtcNow + Patience;
            while (Sharing.View.Busy)
            {
                Assert.True(DateTime.UtcNow < deadline, "timed out");
                Thread.Sleep(5);
            }
        }

        internal List<string> Log { get; }

        internal DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

        /// <summary>Runs whenever the service reads the clock, as during a commit: a test's way to act in the middle of one.</summary>
        internal Action? ClockHook { get; set; }

        /// <summary>Opts a character in and checks it, as a player would.</summary>
        internal SharingCharacter Bound(ulong contentId = Aria)
        {
            Sharing.TryStart(contentId, newKey: false);
            WaitIdle();
            Sharing.TryCheck(contentId, "12345678", "Aria Starfall", "Gilgamesh");
            WaitIdle();
            var entry = Sharing.View.Find(contentId)!;
            Assert.Equal(SharingStage.Shared, entry.Stage);
            return entry;
        }

        /// <summary>Hands a candidate for the Active Plate to the service, as the live publisher does.</summary>
        internal bool Publish(SnapshotCandidate candidate, ulong contentId = Aria)
        {
            Actives[contentId] = candidate.PlateId;
            return Sharing.TryPublish(contentId, candidate);
        }

        /// <summary>Each character's Active Plate, as the service reads it; a test may replace the lookup.</summary>
        internal Dictionary<ulong, Guid?> Actives { get; } = new();

        /// <summary>The service's lookup of a character's Active Plate.</summary>
        internal Func<ulong, Guid?> ActivePlateOf { get; set; } = null!;

        /// <summary>The persona session's stand-in: the work runs here, or on another thread.</summary>
        private bool RunWork(string name, Action<PersonaManager> work)
        {
            if (Background)
            {
                _ = Task.Run(() => work(Personas));
            }
            else
            {
                work(Personas);
            }

            return true;
        }

        /// <summary>The character's publication index, as saved.</summary>
        internal PublicationIndex Index(SharingCharacter entry) =>
            Publications.ReadIndex(entry.Slot) is { } bytes ? PublicationIndexCodec.Decode(bytes, entry.Slot) : PublicationIndex.Empty;

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

    /// <summary>One WebSocket upgrade the plugin asked for: its path, its <c>User-Agent</c>, and whether it sent an <c>Origin</c>.</summary>
    private sealed record SeenUpgrade(string Path, string UserAgent, bool Origin);

    /// <summary>One publish whose proof and document verified: its signer, its snapshot and its image count.</summary>
    private sealed record SeenPublish(PersonaId Signer, ProfileLayoutSnapshot Snapshot, int Images);

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
            ["/v1/lookup"] = RequestProofKind.Lookup,
            ["/v1/image"] = RequestProofKind.Image,
            ["/v1/report"] = RequestProofKind.Report,
        };

        private readonly HashSet<string> issued = new();

        internal Dictionary<string, Func<string, (HttpStatusCode Status, string? Body)>> Answers { get; } = new();

        /// <summary>Answers with bodies of bytes, by path: a served profile or an image.</summary>
        internal Dictionary<string, Func<string, (HttpStatusCode Status, byte[]? Body)>> Bytes { get; } = new();

        internal List<SeenAction> Actions { get; } = new();

        internal List<SeenPublish> Publishes { get; } = new();

        /// <summary>The publish's answer, as a status and a reason; 204 unless a test says otherwise.</summary>
        internal Func<(HttpStatusCode Status, string? Reason)> PublishAnswer { get; set; } = () => (HttpStatusCode.NoContent, null);

        internal string MinimumPlugin { get; set; } = "0.1.6";

        internal int Protocol { get; set; } = 32769;

        /// <summary>The profile id the next check binds a character under.</summary>
        internal ProfileId NextProfile { get; set; } = Profile;

        internal bool Unreachable { get; set; }

        internal int StatusRequests { get; private set; }

        /// <summary>Runs when the server is asked for its status, before it answers: a test's way to act during the check.</summary>
        internal Action? StatusHook { get; set; }

        /// <summary>Runs when a publish arrives, before it is taken: a test's way to act while it is sent.</summary>
        internal Action? PublishHook { get; set; }

        /// <summary>When set, a publish is taken only once it completes, or fails as cancelled when its request is.</summary>
        internal Task? PublishGate { get; set; }

        internal int Challenges { get; private set; }

        /// <summary>A fresh challenge this server issued, in base64, as a <c>409</c>'s final message carries one.</summary>
        internal string FreshChallenge()
        {
            var challenge = RandomNumberGenerator.GetBytes(32);
            issued.Add(Convert.ToHexString(challenge));
            return Convert.ToBase64String(challenge);
        }

        /// <summary>The check's and the re-read's WebSockets (ServerApi-v1.md, section 2.3), in order.</summary>
        internal List<SeenUpgrade> Upgrades { get; } = new();

        /// <summary>Whatever went wrong serving a WebSocket: a test that expects none checks it is empty.</summary>
        internal ConcurrentQueue<Exception> Faults { get; } = new();

        /// <summary>The day of last read a successful check or re-read through the pipe carries; none unless a test sets it.</summary>
        internal long? ReadDay { get; set; }

        /// <summary>Final messages written whole, by path, in place of the answer: a test's way to send what <see cref="Answers"/> can't.</summary>
        internal Dictionary<string, Func<string, string>> Finals { get; } = new();

        /// <summary>Runs on a WebSocket once its signed body is checked, before the final message: a test's way to open the pipe.</summary>
        internal Func<System.Net.WebSockets.WebSocket, Task>? Pipe { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable)
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
            }

            // A WebSocket's request keeps its wss scheme: .NET's handler connects it as https.
            Assert.Equal(WebSocketStandIn.IsUpgrade(request) ? "wss" : "https", request.RequestUri!.Scheme);
            Assert.Equal(Deployment.Value, request.RequestUri.Host);
            var path = request.RequestUri.AbsolutePath;
            if (path == "/v1/status")
            {
                StatusHook?.Invoke();
                StatusRequests++;
                return Answer(HttpStatusCode.OK, $"{{\"protocolVersion\":{Protocol},\"api\":1,\"minimumPlugin\":\"{MinimumPlugin}\"}}");
            }

            if (path == "/v1/challenge")
            {
                Challenges++;
                var challenge = RandomNumberGenerator.GetBytes(32);
                issued.Add(Convert.ToHexString(challenge));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(challenge) };
            }

            if (WebSocketStandIn.IsUpgrade(request))
            {
                Assert.True(path is "/v1/lodestone/check" or "/v1/lodestone/reread", "Only the check and the re-read come as WebSockets.");
                Upgrades.Add(new SeenUpgrade(path, request.Headers.UserAgent.ToString(), request.Headers.Contains("Origin")));
                return await WebSocketStandIn.AcceptAsync(request, socket => ServePipeAsync(path, socket), Faults);
            }

            var envelope = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var length = BinaryPrimitives.ReadUInt16BigEndian(envelope);
            var proof = envelope.AsSpan(2, length).ToArray();
            var body = envelope.AsSpan(2 + length).ToArray();
            if (path == "/v1/publish")
            {
                var documentLength = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                var document = body.AsSpan(4, documentLength).ToArray();
                var submission = RequestProofCodec.VerifySubmission(proof, document, Deployment);
                Assert.True(issued.Remove(Convert.ToHexString(submission.Challenge.ToArray())), "The publish names a challenge this server didn't issue.");
                var snapshot = Assert.IsType<ProfileLayoutSnapshot>(submission.Document.Document);
                Assert.Equal(snapshot.Images.Count, body[4 + documentLength]);

                // A request stopped while it is sent never reaches the server.
                PublishHook?.Invoke();
                if (PublishGate is { } held)
                {
                    await held.WaitAsync(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                Publishes.Add(new SeenPublish(submission.Document.Persona, snapshot, body[4 + documentLength]));
                var (published, reason) = PublishAnswer();
                return Answer(published, reason);
            }

            var text = Verify(path, proof, body);
            if (Bytes.TryGetValue(path, out var bytes))
            {
                var (byteStatus, byteAnswer) = bytes(text);
                return new HttpResponseMessage(byteStatus) { Content = new ByteArrayContent(byteAnswer ?? []) };
            }

            var (status, answer) = Answers.TryGetValue(path, out var scripted) ? scripted(text) : Default(path, text);
            return Answer(status, answer);
        }

        /// <summary>Checks an action's proof as section 14.5 says for the path's kind, under a challenge this server issued and hasn't seen used, and records it.</summary>
        private string Verify(string path, byte[] proof, byte[] body)
        {
            var verified = RequestProofCodec.VerifyAction(proof, body, Deployment, Kinds[path]);
            Assert.True(issued.Remove(Convert.ToHexString(verified.Challenge.ToArray())), "The proof names a challenge this server didn't issue, or one already used.");
            var text = Encoding.UTF8.GetString(body);
            Actions.Add(new SeenAction(path, verified.PublicKey.Id, text));
            return text;
        }

        /// <summary>
        /// The check or the re-read as a WebSocket: the signed body as its first message, checked as
        /// a POST's is, then <see cref="Pipe"/> when a test set one, then the final message.
        /// </summary>
        private async Task ServePipeAsync(string path, System.Net.WebSockets.WebSocket socket)
        {
            var (type, envelope) = await WebSocketStandIn.ReceiveAsync(socket);
            Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Binary, type);
            var length = BinaryPrimitives.ReadUInt16BigEndian(envelope);
            var text = Verify(path, envelope.AsSpan(2, length).ToArray(), envelope.AsSpan(2 + length).ToArray());
            string final;
            if (Finals.TryGetValue(path, out var written))
            {
                final = written(text);
            }
            else
            {
                if (Pipe is { } pipe)
                {
                    await pipe(socket);
                }

                var (status, answer) = Answers.TryGetValue(path, out var scripted) ? scripted(text) : Default(path, text);
                final = "{\"status\":" + ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + (answer is null ? "" : ",\"body\":" + answer)
                    + (status == HttpStatusCode.OK && answer is not null && ReadDay is { } day ? ",\"readDay\":" + day.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")
                    + "}";
            }

            await WebSocketStandIn.FinishAsync(socket, final);
        }

        private (HttpStatusCode, string?) Default(string path, string body) => path switch
        {
            "/v1/lodestone/code" => (HttpStatusCode.OK, $"{{\"code\":\"{Code}\",\"expiresInSeconds\":3600}}"),
            "/v1/lodestone/check" => (HttpStatusCode.OK, CheckAnswer(body, profile: NextProfile)),
            "/v1/lodestone/reread" => (HttpStatusCode.OK, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}"),
            "/v1/opt-out" => (HttpStatusCode.NoContent, null),
            _ => (HttpStatusCode.NotFound, null),
        };

        /// <summary>A check's answer: the binding's profile id, and the name and World the check claimed.</summary>
        internal static string CheckAnswer(string body, string? name = null, ProfileId? profile = null)
        {
            using var claimed = System.Text.Json.JsonDocument.Parse(body);
            return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["profileId"] = (profile ?? Profile).ToString(),
                ["name"] = name ?? claimed.RootElement.GetProperty("name").GetString()!,
                ["world"] = claimed.RootElement.GetProperty("world").GetString()!,
            });
        }

        private static HttpResponseMessage Answer(HttpStatusCode status, string? body) =>
            new(status) { Content = new ByteArrayContent(body is null ? [] : Encoding.UTF8.GetBytes(body)) };
    }
}
