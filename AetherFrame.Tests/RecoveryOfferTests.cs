using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Recovery checkpoints left by a crash, offered at the next load through the existing offer: one
/// entry per editing however many points it kept, its older points in a list on that entry, Resume
/// Editing into the editor unsaved, Recover as New Plate, and every conflict with the Library judged
/// as kept changes are. Nothing is saved, shared or made Active by an answer.
/// </summary>
public class RecoveryOfferTests
{
    [Fact]
    public async Task OneEditing_IsOneEntry_ItsPointsInAList_NamedAndTimed()
    {
        using var fixture = new LibraryFixture();
        var plateId = await CrashWithEditsAsync(fixture, "Evening Look", "one", "two", "three");

        var next = await GameSession.StartAsync(fixture);
        var offered = Assert.Single(await next.LoadKeptChangesAsync());
        Assert.True(offered.IsCheckpoint);
        Assert.Equal(plateId, offered.PlateId);
        Assert.Equal(2, offered.Older.Count);
        Assert.Equal(KeptChangesVariant.Restore, next.Offer.CurrentVariant);
        Assert.Equal(1, next.Offer.Count);
        Assert.Equal(3, next.Offer.CheckpointOptions!.Count);
        Assert.Contains("newest", next.Offer.CheckpointOptions[0], StringComparison.Ordinal);
        Assert.StartsWith(KeptChangesOffer.Quoted("Evening Look") + " had unsaved changes when AetherFrame last stopped (Advanced editor, recovery checkpoint ", next.Offer.Body, StringComparison.Ordinal);
        Assert.Contains(offered.Draft.WrittenAtUtc.ToLocalTime().ToString("G", System.Globalization.CultureInfo.CurrentCulture), next.Offer.Body, StringComparison.Ordinal);
        Assert.Equal(KeptChangesOffer.RestoreLabel, next.Offer.PrimaryLabel);
        Assert.True(next.Offer.OffersNewPlateToo);
    }

    [Fact]
    public async Task ResumeEditing_OpensTheNewestPointUnsaved_AndSavesSharesAndActivatesNothing()
    {
        using var fixture = new LibraryFixture();
        var plateId = await CrashWithEditsAsync(fixture, "Resumed", "one", "two");
        var platePath = fixture.Paths.GetPlatePath(plateId);
        var savedBytes = File.ReadAllBytes(platePath);

        var next = await GameSession.StartAsync(fixture);
        var activeBefore = next.Library.GetActivePlateId(1UL);
        await next.LoadKeptChangesAsync();
        next.Offer.Choose();

        Assert.Null(next.Offer.Error);
        Assert.Equal(plateId, next.Profiles.OpenPlateId);
        Assert.True(next.Session.IsDirty);
        Assert.Contains(next.Document.Elements, e => e is TextProfileElement { Text: "two" });
        Assert.Equal(savedBytes, File.ReadAllBytes(platePath));
        Assert.Equal(activeBefore, next.Library.GetActivePlateId(1UL));
        Assert.False(next.Offer.HasCurrent);

        // The token is in the trash; the editing's other points are gone, so nothing is offered twice.
        Assert.Single(KeptFiles.Trashed(fixture.Paths));
        Assert.All(KeptFiles.Checkpoints(fixture.Paths), p => Assert.Contains(next.Recovery.Store.SessionId.ToString("N"), p, StringComparison.Ordinal));

        // The resumed changes are protected again by this run's own checkpoints.
        await next.Recovery.RunAsync(6);
        Assert.Single(next.Recovery.OwnCheckpoints());
    }

    [Fact]
    public async Task AnOlderPoint_CanBeChosen_AndIsWhatResumes()
    {
        using var fixture = new LibraryFixture();
        await CrashWithEditsAsync(fixture, "Older", "one", "two", "three");

        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();
        next.Offer.SelectCheckpoint(2);
        Assert.Equal(2, next.Offer.SelectedCheckpoint);
        next.Offer.Choose();

        var texts = next.Document.Elements.OfType<TextProfileElement>().Select(t => t.Text).ToArray();
        Assert.Equal(new[] { "one" }, texts);
        Assert.True(next.Session.IsDirty);
        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
    }

    [Fact]
    public async Task RecoverAsNewPlate_BesideResume_LeavesTheSavedPlateAsItIs()
    {
        using var fixture = new LibraryFixture();
        var plateId = await CrashWithEditsAsync(fixture, "Kept Apart", "recovered");
        var savedBytes = File.ReadAllBytes(fixture.Paths.GetPlatePath(plateId));

        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();
        next.Offer.ChooseNewPlate();
        await next.SettleAsync();

        Assert.Null(next.Offer.Error);
        Assert.Equal(savedBytes, File.ReadAllBytes(fixture.Paths.GetPlatePath(plateId)));
        var created = Assert.Single(next.Library.GetOrderedPlates(), p => p.PlateId != plateId);
        Assert.Contains("recovered", fixture.ReadPlateJson(created.PlateId), StringComparison.Ordinal);
        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
        Assert.False(next.Offer.HasCurrent);
    }

    [Fact]
    public async Task APlateSavedAgainAfterTheCrash_IsOnlyRecoveredAsANewPlate()
    {
        using var fixture = new LibraryFixture();
        var plateId = await CrashWithEditsAsync(fixture, "Saved Elsewhere", "from the crash");

        // Another client saves the Plate after the crash.
        var other = await GameSession.StartAsync(fixture);
        other.Open(plateId);
        other.Edit("saved later");
        Assert.True(await other.Session.SaveProfileAsync());
        var savedBytes = File.ReadAllBytes(fixture.Paths.GetPlatePath(plateId));

        var next = await GameSession.StartAsync(fixture);
        Assert.Equal(KeptChangesChoice.SavedAgain, Assert.Single(await next.LoadKeptChangesAsync()).Choice);
        Assert.Equal(KeptChangesVariant.SavedAgain, next.Offer.CurrentVariant);
        Assert.False(next.Offer.OffersNewPlateToo);
        next.Offer.Choose();
        await next.SettleAsync();

        Assert.Equal(savedBytes, File.ReadAllBytes(fixture.Paths.GetPlatePath(plateId)));
        Assert.Equal(2, next.Library.GetOrderedPlates().Count);
    }

    [Fact]
    public async Task ADeletedPlatesCheckpoints_AreOnlyRecoveredAsANewPlate()
    {
        using var fixture = new LibraryFixture();
        var plateId = await CrashWithEditsAsync(fixture, "Gone", "still wanted");
        var deleter = await GameSession.StartAsync(fixture);
        await deleter.Library.DeletePlateAsync(plateId);

        var next = await GameSession.StartAsync(fixture);
        Assert.Equal(KeptChangesChoice.Deleted, Assert.Single(await next.LoadKeptChangesAsync()).Choice);
        next.Offer.Choose();
        await next.SettleAsync();

        Assert.Null(next.Library.FindPlate(plateId));
        var created = Assert.Single(next.Library.GetOrderedPlates());
        Assert.Contains("still wanted", fixture.ReadPlateJson(created.PlateId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecideLater_KeepsEveryPoint_ForTheNextStart()
    {
        using var fixture = new LibraryFixture();
        await CrashWithEditsAsync(fixture, "Later", "one", "two");
        var files = KeptFiles.Checkpoints(fixture.Paths);

        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();
        next.Offer.DecideLater();
        Assert.Equal(files, KeptFiles.Checkpoints(fixture.Paths));

        var after = await GameSession.StartAsync(fixture);
        Assert.Single(Assert.Single(await after.LoadKeptChangesAsync()).Older);
    }

    [Fact]
    public async Task Discard_MovesTheNewestPointToTheTrash_AndRemovesTheRest()
    {
        using var fixture = new LibraryFixture();
        await CrashWithEditsAsync(fixture, "Thrown", "one", "two", "three");

        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();
        next.Offer.Discard();

        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
        Assert.Single(KeptFiles.Trashed(fixture.Paths));
        Assert.Empty(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
    }

    [Fact]
    public async Task ManyEditings_AreOfferedNewestTwentyFirst_EachOnce()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        for (var i = 0; i < 24; i++)
        {
            game.Open(await game.CreatePlateAsync(name: $"Plate {i}"));
            await game.Recovery.FrameAsync();
            for (var p = 0; p < 3; p++)
            {
                game.Edit($"{i}.{p}");
                fixture.Clock.Tick();
                await game.Recovery.RunAsync(6);
            }
        }

        game.Recovery.Files.Crash();
        Assert.Equal(24 * 3, KeptFiles.Checkpoints(fixture.Paths).Length);

        var next = await GameSession.StartAsync(fixture);
        var offered = await next.LoadKeptChangesAsync();
        Assert.Equal(DraftStore.MaxDraftsRead, offered.Count);
        Assert.Equal(DraftStore.MaxDraftsRead, offered.Select(o => o.PlateId).Distinct().Count());
        Assert.All(offered, o => Assert.Equal(2, o.Older.Count));
        Assert.DoesNotContain(offered, o => o.Draft.PlateName == "Plate 0");
        Assert.Contains(offered, o => o.Draft.PlateName == "Plate 23");
    }

    [Fact]
    public async Task UnknownData_IsKeptInTheCheckpoint_AndSurvivesResumeAndSave()
    {
        using var fixture = new LibraryFixture();
        var setup = await GameSession.StartAsync(fixture);
        var plateId = await setup.CreatePlateAsync(name: "Future");
        var saved = JsonNode.Parse(fixture.ReadPlateJson(plateId))!.AsObject();
        saved["FutureField"] = "kept by the Plate";
        fixture.WritePlateJson(plateId, saved.ToJsonString());

        var game = await GameSession.StartAsync(fixture);
        game.Open(plateId);
        game.Edit("with the future");
        await game.Recovery.RunAsync(6);
        Assert.Contains("FutureField", File.ReadAllText(Assert.Single(game.Recovery.OwnCheckpoints())), StringComparison.Ordinal);
        game.Recovery.Files.Crash();

        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();
        next.Offer.Choose();
        Assert.True(await next.Session.SaveProfileAsync());

        var json = fixture.ReadPlateJson(plateId);
        Assert.Contains("\"FutureField\"", json, StringComparison.Ordinal);
        Assert.Contains("with the future", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewerVersionsCheckpoint_IsNeverOffered_OrRemoved()
    {
        using var fixture = new LibraryFixture();
        await CrashWithEditsAsync(fixture, "Newer", "one", "two");
        var newest = KeptFiles.Checkpoints(fixture.Paths).Max(StringComparer.Ordinal)!;
        var envelope = JsonNode.Parse(File.ReadAllText(newest))!.AsObject();
        envelope["Version"] = 99;
        File.WriteAllText(newest, envelope.ToJsonString());
        var before = KeptFiles.Checkpoints(fixture.Paths);

        var next = await GameSession.StartAsync(fixture);
        next.Recovery.Store.Sweep();
        Assert.Empty(await next.LoadKeptChangesAsync());
        Assert.Equal(before, KeptFiles.Checkpoints(fixture.Paths));
    }

    /// <summary>A Plate opened in the Advanced editor, one checkpoint per edit, then the game crashes. Returns the Plate.</summary>
    private static async Task<Guid> CrashWithEditsAsync(LibraryFixture fixture, string name, params string[] edits)
    {
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: name);
        game.Open(plateId);
        await game.Recovery.FrameAsync();
        foreach (var text in edits)
        {
            game.Edit(text);
            fixture.Clock.Tick();
            await game.Recovery.RunAsync(6);
        }

        Assert.Equal(edits.Length, game.Recovery.OwnCheckpoints().Length);
        game.Recovery.Files.Crash();
        return plateId;
    }
}
