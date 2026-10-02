using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Network.Sharing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// N2-9c's security review, as the owner's direction of October 2, 2026 leaves it: only a candidate
/// for the Active Plate, built from its latest build, is signed, under the key that binds the
/// character now; the Plate the server shows is recorded; transient failures wait; every way out
/// forgets what was signed; loading tidies the outboxes; and the live publisher never takes a value
/// becoming known for a change.
/// </summary>
public partial class CharacterSharingTests
{
    [Fact]
    public void OnlyACandidateForTheActivePlate_IsSigned()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        var candidate = PublicationCandidates.Simple();

        // Another Plate is Active by the time the candidate reaches the service, or none is.
        Assert.True(harness.Sharing.TryPublish(Aria, candidate, Guid.NewGuid()));
        Assert.True(harness.Sharing.TryPublish(Aria, candidate, null));

        Assert.Empty(harness.Server.Publishes);
        Assert.Empty(harness.Index(entry).Entries);
        Assert.Null(harness.Sharing.View.Publish!.Outcome);
    }

    [Fact]
    public void ACandidateBuiltBeforeANewerBuild_IsNeverSent()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var first = PublicationCandidates.Simple();
        harness.Publish(first);

        // A build starts under the generation now; a re-save starts a newer one, and moves it on,
        // before the candidate reaches the service.
        var built = harness.Sharing.BuildGeneration(Aria);
        harness.Sharing.Supersede(Aria);
        var stale = PublicationCandidates.Simple(first.PlateId, "Older words");
        harness.Sharing.TryPublish(Aria, stale, first.PlateId, built);
        Assert.Single(harness.Server.Publishes);

        // Under the generation now, it is sent as before.
        harness.Sharing.TryPublish(Aria, stale, first.PlateId, harness.Sharing.BuildGeneration(Aria));
        Assert.Equal(2, harness.Server.Publishes.Count);
    }

    [Fact]
    public void ANewerBuild_DuringTheStatusCheck_LeavesTheOlderCandidateUnsigned()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();

        // The first publish of a session asks for the server's status; a newer build starts then.
        harness.Restart();
        harness.Server.StatusHook = () =>
        {
            harness.Server.StatusHook = null;
            harness.Sharing.Supersede(Aria);
        };
        var candidate = PublicationCandidates.Simple();
        Assert.True(harness.Sharing.TryPublish(Aria, candidate, candidate.PlateId, harness.Sharing.BuildGeneration(Aria)));

        Assert.Empty(harness.Server.Publishes);
        Assert.Empty(harness.Index(entry).Entries);
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);
        Assert.Null(harness.Sharing.View.Publish!.Outcome);
    }

    [Fact]
    public void ANewerBuild_DuringTheCommit_KeepsTheRevisionWaiting_AndTheNewerOneReplacesIt()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        var plate = Guid.NewGuid();
        harness.Publish(PublicationCandidates.Simple(plate));

        // The second save's commit is under way when the third save's build starts, as the live
        // publisher starts it: the second is signed, but never sent.
        long? third = null;
        harness.ClockHook = () =>
        {
            if (third is null)
            {
                harness.Sharing.Supersede(Aria);
                third = harness.Sharing.BuildGeneration(Aria);
            }
        };
        harness.Sharing.TryPublish(Aria, PublicationCandidates.Simple(plate, "Second words"), plate, harness.Sharing.BuildGeneration(Aria));
        harness.ClockHook = null;
        Assert.Single(harness.Server.Publishes);
        Assert.False(Assert.Single(harness.Index(entry).Entries).PendingEntry.IsNone);
        Assert.Null(harness.Sharing.View.Publish!.Outcome);

        // The third, under the generation now, replaces the waiting revision and is sent.
        harness.Sharing.TryPublish(Aria, PublicationCandidates.Simple(plate, "Third words"), plate, third);
        Assert.Equal(2, harness.Server.Publishes.Count);
        Assert.Equal("Third words", TextOf(harness.Server.Publishes[1]));
        var recorded = Assert.Single(harness.Index(entry).Entries);
        Assert.Equal((PublicationState.Published, true, harness.Server.Publishes[1].Snapshot.RevisionId), (recorded.State, recorded.PendingEntry.IsNone, recorded.LatestRevision));
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);
    }

    [Fact]
    public void ANewerCandidate_StopsAnOlderSendUnderWay_WithNothingToSay_AndReplacesIt()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        var plate = Guid.NewGuid();
        harness.Server.PublishHook = () =>
        {
            harness.Server.PublishHook = null;
            harness.Sharing.StopOlderSend(Aria);
        };
        harness.Publish(PublicationCandidates.Simple(plate, "Older words"));

        Assert.Empty(harness.Server.Publishes);
        Assert.Null(harness.Sharing.View.Notice);
        Assert.Null(harness.Sharing.View.Publish!.Outcome);
        Assert.Contains("Sharing: the send gave way to a newer build of the Active Plate.", harness.Log);
        Assert.Equal(PublicationState.Pending, Assert.Single(harness.Index(entry).Entries).State);

        harness.Publish(PublicationCandidates.Simple(plate, "Newer words"));
        Assert.Equal("Newer words", TextOf(Assert.Single(harness.Server.Publishes)));
        var recorded = Assert.Single(harness.Index(entry).Entries);
        Assert.Equal((PublicationState.Published, true), (recorded.State, recorded.PendingEntry.IsNone));
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);
    }

    [Fact]
    public void AnotherCharactersSend_DoesntGiveWay_ButThePlayersStopStopsAnySend()
    {
        using var harness = new SharingHarness();
        harness.Bound();

        // A newer candidate for another character leaves this one's send alone.
        harness.Server.PublishHook = () => harness.Sharing.StopOlderSend(Bram);
        harness.Publish(PublicationCandidates.Simple());
        Assert.Single(harness.Server.Publishes);
        Assert.Equal(SharingNoticeKind.Published, harness.Sharing.View.Notice!.Kind);

        // The player's Stop sending stops it, and says so.
        harness.Server.PublishHook = harness.Sharing.StopSending;
        harness.Publish(PublicationCandidates.Simple());
        Assert.Single(harness.Server.Publishes);
        Assert.Equal(SharingNoticeKind.PublishStopped, harness.Sharing.View.Notice!.Kind);
        Assert.Equal(SharingNoticeKind.PublishStopped, harness.Sharing.View.Publish!.Outcome!.Kind);
    }

    [Fact]
    public void ASendWhosePlateIsNoLongerActive_Stops_AndSaysSo_ButOneOfTheActivePlateGoesOn()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        var first = PublicationCandidates.Simple();
        harness.Publish(first);
        Assert.Equal(first.PlateId, harness.Sharing.View.Find(Aria)!.PublishedPlate);

        // The Active Plate is unset while another Plate is being sent: it stops, says so, and its
        // revision is dropped rather than left to be sent later.
        harness.Server.PublishHook = () => harness.Sharing.StopStaleSend(Aria, null);
        harness.Publish(PublicationCandidates.Simple());
        Assert.Single(harness.Server.Publishes);
        Assert.Equal(SharingNoticeKind.PublishWithdrawn, harness.Sharing.View.Notice!.Kind);
        Assert.Equal(SharingNoticeKind.PublishWithdrawn, harness.Sharing.View.Publish!.Outcome!.Kind);
        Assert.Equal(first.PlateId, harness.Sharing.View.Find(Aria)!.PublishedPlate);
        Assert.All(harness.Index(entry).Entries, recorded => Assert.True(recorded.PendingEntry.IsNone));
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);

        // A send of the Active Plate itself goes on, and so does one while another character's
        // Active Plate changes.
        var active = PublicationCandidates.Simple();
        harness.Server.PublishHook = () =>
        {
            harness.Sharing.StopStaleSend(Aria, active.PlateId);
            harness.Sharing.StopStaleSend(Bram, null);
        };
        harness.Publish(active);
        Assert.Equal(2, harness.Server.Publishes.Count);
        Assert.Equal(active.PlateId, harness.Sharing.View.Find(Aria)!.PublishedPlate);
    }

    [Fact]
    public void AWaitingRevisionSentAgain_StopsToo_WhenItsPlateIsNoLongerActive()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        harness.Server.PublishAnswer = () => (HttpStatusCode.ServiceUnavailable, null);
        var waiting = PublicationCandidates.Simple();
        harness.Publish(waiting);
        Assert.Equal(SharingNoticeKind.PublishWaiting, harness.Sharing.View.Notice!.Kind);

        harness.Server.PublishAnswer = () => (HttpStatusCode.NoContent, null);
        harness.Server.PublishHook = () => harness.Sharing.StopStaleSend(Aria, Guid.NewGuid());
        harness.Sharing.TrySendWaiting(Aria);

        // Only the first try reached the server, which couldn't take it.
        Assert.Single(harness.Server.Publishes);
        Assert.Equal(SharingNoticeKind.PublishWithdrawn, harness.Sharing.View.Notice!.Kind);
        Assert.Empty(harness.Index(entry).Entries);
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);
    }

    [Fact]
    public void NothingIsSigned_WhileANewKeyIsChecked_ThenTheNewKeySigns()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        var candidate = PublicationCandidates.Simple();

        // A new key replacing the old: nothing is signed while it is checked.
        harness.Sharing.TryStart(Aria, newKey: true);
        harness.Publish(candidate);
        Assert.Empty(harness.Server.Publishes);

        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        var moved = harness.Sharing.View.Find(Aria)!;
        Assert.NotEqual(entry.Key, moved.Key);
        harness.Publish(candidate);
        Assert.Equal(moved.Key, Assert.Single(harness.Server.Publishes).Signer);
    }

    [Fact]
    public void ANewBinding_NeverSendsWhatWasSignedUnderTheOldOne()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        harness.Server.PublishAnswer = () => (HttpStatusCode.ServiceUnavailable, null);
        var plate = Guid.NewGuid();
        harness.Publish(PublicationCandidates.Simple(plate));
        harness.Sharing.TryTurnOff(Aria);

        var fresh = ProfileId.Parse("prf_fedcba9876543210fedcba9876543210");
        harness.Server.NextProfile = fresh;
        harness.Server.PublishAnswer = () => (HttpStatusCode.NoContent, null);
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        harness.Publish(PublicationCandidates.Simple(plate, "Again"));
        harness.Sharing.TrySendWaiting(Aria);

        var accepted = harness.Server.Publishes.Skip(1).ToList();
        Assert.NotEmpty(accepted);
        Assert.All(accepted, publish => Assert.Equal(fresh, publish.Snapshot.ProfileId));
    }

    [Fact]
    public void AServerThatFailsForAMoment_KeepsTheRevisionToSendAgain()
    {
        foreach (var status in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.GatewayTimeout, HttpStatusCode.RequestTimeout })
        {
            using var harness = new SharingHarness();
            var entry = harness.Bound();
            harness.Server.PublishAnswer = () => (status, null);
            harness.Publish(PublicationCandidates.Simple());
            Assert.Equal(SharingNoticeKind.PublishWaiting, harness.Sharing.View.Notice!.Kind);
            Assert.NotEmpty(harness.Publications.ListOutbox(entry.Slot).Entries);

            harness.Server.PublishAnswer = () => (HttpStatusCode.NoContent, null);
            harness.Sharing.TrySendWaiting(Aria);
            Assert.Equal(SharingNoticeKind.Published, harness.Sharing.View.Notice!.Kind);
        }
    }

    [Fact]
    public void ThePlateTheServerShows_IsRecorded_KeptThroughARefusal_AndForgottenOnPause()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var first = PublicationCandidates.Simple();
        harness.Publish(first);
        Assert.Equal(first.PlateId, harness.Sharing.View.Find(Aria)!.PublishedPlate);
        Assert.Equal(first.PlateId, harness.File.Read().Single().PublishedPlate);

        harness.Server.PublishAnswer = () => (HttpStatusCode.UnprocessableEntity, "image-refused");
        harness.Publish(PublicationCandidates.Simple());
        Assert.Equal(first.PlateId, harness.Sharing.View.Find(Aria)!.PublishedPlate);

        harness.Sharing.TryPause(Aria);
        Assert.Null(harness.Sharing.View.Find(Aria)!.PublishedPlate);
    }

    [Fact]
    public void ARereadNotFound_ForgetsEverythingTheKeySigned()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        harness.Server.PublishAnswer = () => (HttpStatusCode.ServiceUnavailable, null);
        harness.Publish(PublicationCandidates.Simple());
        Assert.NotEmpty(harness.Publications.ListOutbox(entry.Slot).Entries);

        harness.Server.Answers["/v1/lodestone/reread"] = _ => (HttpStatusCode.NotFound, null);
        harness.Sharing.TryReread(Aria, "Aria Moonfall", "Gilgamesh");
        Assert.Equal(SharingStage.Off, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Empty(harness.Index(entry).Entries);
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);
    }

    [Fact]
    public void Loading_DropsTheShareChecksSignings_DeletesStrayOutboxFiles_AndSurvivesAnIndexItCantRead()
    {
        using var harness = new SharingHarness();
        var aria = harness.Bound();
        harness.Publish(PublicationCandidates.Simple());

        // The share check's signing, under a persona that is no character's key.
        var old = harness.Personas.Create("Old persona");
        harness.Personas.Select(old.Slot);
        old = harness.Personas.Acknowledge(old.Slot);
        Assert.Equal(PublishResult.Stored, PublicationCommit.Commit(harness.Personas, harness.Publications, new PublishConsent(PublicationCandidates.Simple(), old.Slot, old.PublicKey), () => harness.Now).Result);

        // An outbox file no index names, left under Aria's key.
        var stray = Path.Combine(harness.Publications.OutboxPath(aria.Slot), OutboxEntryName.NewName().FileName);
        File.WriteAllBytes(stray, [1, 2, 3]);

        // An index too large for any persona to read.
        var huge = harness.Personas.Create("Huge");
        Directory.CreateDirectory(Path.GetDirectoryName(harness.Publications.IndexPath(huge.Slot))!);
        File.WriteAllBytes(harness.Publications.IndexPath(huge.Slot), new byte[PublicationIndexCodec.MaxBytes + 1]);

        harness.Restart();
        Assert.False(harness.Sharing.View.Unreadable);
        Assert.Equal(SharingStage.Shared, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Empty(PublicationIndexCodec.Decode(harness.Publications.ReadIndex(old.Slot)!, old.Slot).Entries);
        Assert.Empty(harness.Publications.ListOutbox(old.Slot).Entries);
        Assert.False(File.Exists(stray));
        Assert.NotEmpty(harness.Index(aria).Entries);
    }

    [Fact]
    public void ACommitUnderABinding_DropsOtherProfilesAndWhatWaitedForThem()
    {
        using var fixture = new PublicationFixture();
        var plate = Guid.NewGuid();
        Assert.Equal(PublishResult.Stored, fixture.Commit(PublicationCandidates.Simple(plate)).Result);
        var before = PublicationIndexCodec.Decode(fixture.Files.ReadIndex(fixture.Persona.Slot)!, fixture.Persona.Slot).Entries.Single();
        Assert.NotEqual(Profile, before.ProfileId);

        var outcome = PublicationCommit.Commit(fixture.Personas, fixture.Files, new PublishConsent(PublicationCandidates.Simple(), fixture.Persona.Slot, fixture.Persona.PublicKey, Profile), () => fixture.Now);
        Assert.Equal(PublishResult.Stored, outcome.Result);
        var entries = PublicationIndexCodec.Decode(fixture.Files.ReadIndex(fixture.Persona.Slot)!, fixture.Persona.Slot).Entries;
        Assert.Equal(Profile, Assert.Single(entries).ProfileId);
        Assert.Equal(entries[0].PendingEntry, Assert.Single(fixture.Files.ListOutbox(fixture.Persona.Slot).Entries));
    }

    [Fact]
    public async Task ABuildThatGoesOutOfDate_IsNeverSent()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        using var live = new LiveHarness(harness);
        var first = live.Save();
        live.Active = first.ProfileId;
        live.Frames(2);

        // Another character logs in while the build is under way, then this one again.
        live.Publisher.PlateSaved(first.ProfileId);
        live.Frames(1);
        live.LoggedIn = Bram;
        live.Frames(2);
        live.LoggedIn = Aria;
        live.Frames(5);
        await Task.Delay(50);
        live.Frames(5);
        Assert.Empty(harness.Server.Publishes);

        // Another Plate is made Active while the first one is built: only that one is sent.
        live.Publisher.PlateSaved(first.ProfileId);
        live.Frames(1);
        var second = live.Save(text: "Another Plate");
        live.Active = second.ProfileId;
        await live.Until(() => harness.Server.Publishes.Count > 0);
        live.Frames(20);
        Assert.Single(harness.Server.Publishes);
        Assert.Equal(second.ProfileId, Assert.Single(harness.Index(entry).Entries).PlateId);
    }

    [Fact]
    public void TheLibraryFinishingLoading_AfterArriving_PublishesNothing()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var candidate = PublicationCandidates.Simple();
        harness.Publish(candidate);
        using var live = new LiveHarness(harness) { LibraryLoaded = false };

        // The Library isn't loaded: no Active Plate is known yet.
        live.Frames(5);
        live.Active = candidate.PlateId;
        live.LibraryLoaded = true;
        live.Frames(20);
        Assert.Single(harness.Server.Publishes);
    }

    /// <summary>The one text a test candidate's snapshot carries.</summary>
    private static string TextOf(SeenPublish publish) => publish.Snapshot.Items.OfType<LayoutText>().Single().Text;
}
