using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AetherFrame.Protocol.Identity;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Network.Sharing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// N2-9c's security review: an approval counts only for what was shown, as it was shown; the Plate
/// the server shows is recorded; transient failures wait; every way out forgets what was signed;
/// loading tidies the outboxes; and the live publisher never takes a value becoming known for a
/// change.
/// </summary>
public partial class CharacterSharingTests
{
    [Fact]
    public void AnApproval_CountsOnlyForTheCandidateShown_AndOnlyForTheActivePlate()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var shown = PublicationCandidates.Simple();
        harness.Publish(shown);
        Assert.NotNull(harness.Sharing.View.Consent);

        // Another candidate for the same Plate was never shown.
        var other = PublicationCandidates.Simple(shown.PlateId, "Not what was shown");
        harness.Sharing.TryPublish(Aria, other, approved: true, other.PlateId);
        Assert.Empty(harness.Server.Publishes);
        Assert.Null(harness.Sharing.View.Consent);

        // The candidate shown, once the Plate is no longer Active.
        harness.Publish(shown);
        harness.Sharing.TryPublish(Aria, shown, approved: true, Guid.NewGuid());
        Assert.Empty(harness.Server.Publishes);
        Assert.Null(harness.Sharing.View.Consent);
    }

    [Fact]
    public void ACandidateBuiltBeforeAShowingWasWithdrawn_IsNeitherShownNorSent()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var first = PublicationCandidates.Simple();
        harness.Share(first);

        // A build starts under the generation now; a re-save withdraws, and moves it on, before
        // the candidate reaches the service.
        var built = harness.Sharing.ShowingGeneration(Aria);
        harness.Sharing.ClearConsent(Aria);
        var stale = PublicationCandidates.Simple();
        harness.Sharing.TryPublish(Aria, stale, approved: false, stale.PlateId, null, built);
        Assert.Null(harness.Sharing.View.Consent);

        var staleSave = PublicationCandidates.Simple(first.PlateId, "Older words");
        harness.Sharing.TryPublish(Aria, staleSave, approved: false, first.PlateId, null, built);
        Assert.Single(harness.Server.Publishes);

        // Under the generation now, it is shown as before.
        harness.Sharing.TryPublish(Aria, stale, approved: false, stale.PlateId, null, harness.Sharing.ShowingGeneration(Aria));
        Assert.Same(stale, harness.Sharing.View.Consent!.Candidate);
    }

    [Fact]
    public void AnApprovalThatComesTooLate_SaysSo()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var candidate = PublicationCandidates.Simple();
        harness.Publish(candidate);
        harness.Sharing.TryPublish(Aria, candidate, approved: true, Guid.NewGuid());
        Assert.Equal(SharingNoticeKind.PublishChanged, harness.Sharing.View.Notice!.Kind);
        Assert.Empty(harness.Server.Publishes);
    }

    [Fact]
    public void AnApproval_CountsOnlyUnderTheKeyItWasShownWith()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var candidate = PublicationCandidates.Simple();
        harness.Publish(candidate);

        // A new key replacing the old: nothing is signed while it is checked, and a passed check
        // withdraws a first showing from before it.
        harness.Sharing.TryStart(Aria, newKey: true);
        harness.Sharing.TryPublish(Aria, candidate, approved: true, candidate.PlateId);
        Assert.Empty(harness.Server.Publishes);
        Assert.Null(harness.Sharing.View.Consent);

        harness.Publish(candidate);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        Assert.Null(harness.Sharing.View.Consent);
    }

    [Fact]
    public void ANewBinding_NeverSendsWhatWasSignedUnderTheOldOne()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        harness.Server.PublishAnswer = () => (HttpStatusCode.ServiceUnavailable, null);
        var plate = Guid.NewGuid();
        harness.Share(PublicationCandidates.Simple(plate));
        harness.Sharing.TryTurnOff(Aria);

        var fresh = ProfileId.Parse("prf_fedcba9876543210fedcba9876543210");
        harness.Server.NextProfile = fresh;
        harness.Server.PublishAnswer = () => (HttpStatusCode.NoContent, null);
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");
        harness.Share(PublicationCandidates.Simple(plate, "Again"));
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
            harness.Share(PublicationCandidates.Simple());
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
        harness.Share(first);
        Assert.Equal(first.PlateId, harness.Sharing.View.Find(Aria)!.PublishedPlate);
        Assert.Equal(first.PlateId, harness.File.Read().Single().PublishedPlate);

        harness.Server.PublishAnswer = () => (HttpStatusCode.UnprocessableEntity, "image-refused");
        harness.Share(PublicationCandidates.Simple());
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
        harness.Share(PublicationCandidates.Simple());
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
        harness.Share(PublicationCandidates.Simple());

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
    public async Task AFirstShowing_IsWithdrawn_WhenItsPlateIsSavedAgain_OrTheCharacterChanges()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        using var live = new LiveHarness(harness);
        var plate = live.Save();
        live.Active = plate.ProfileId;
        live.Frames(2);
        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => harness.Sharing.View.Consent is not null);

        // Saved again, now with something that can't be shared: the older showing goes with it.
        live.Save(text: "Bad" + (char)0 + "text", id: plate.ProfileId);
        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => live.Publisher.View.Problems.Count > 0);
        Assert.Null(harness.Sharing.View.Consent);

        // Saved again as it can be shared, then another character logs in, then this one again.
        live.Save(id: plate.ProfileId);
        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => harness.Sharing.View.Consent is not null);
        live.LoggedIn = Bram;
        live.Frames(2);
        Assert.Null(harness.Sharing.View.Consent);
        live.LoggedIn = Aria;
        live.Frames(5);
        Assert.Null(harness.Sharing.View.Consent);
        Assert.Empty(harness.Server.Publishes);
    }

    [Fact]
    public void TheLibraryFinishingLoading_AfterArriving_PublishesNothing()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var candidate = PublicationCandidates.Simple();
        harness.Share(candidate);
        using var live = new LiveHarness(harness) { LibraryLoaded = false };

        // The Library isn't loaded: no Active Plate is known yet.
        live.Frames(5);
        live.Active = candidate.PlateId;
        live.LibraryLoaded = true;
        live.Frames(20);
        Assert.Single(harness.Server.Publishes);
        Assert.Null(harness.Sharing.View.Consent);
    }
}
