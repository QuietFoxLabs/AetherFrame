using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Kept changes judged against the Library at the next load: restored over their Plate only while
/// it is saved exactly as they started from, and then only into the editor, unsaved; otherwise only
/// as a new Plate, with the Plate itself never written. Changes a save already holds are retired.
/// </summary>
public class KeptChangesConflictTests
{
    [Fact]
    public async Task AMatchingBase_RestoresIntoTheEditorUnsaved_FileUnchangedUntilSave_OneUndoBack_RenameKept()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: "Before Rename");
        game.Open(plateId, EditorSurfaceKind.Advanced);
        var textId = game.Edit("Kept across the reload");

        // Renamed from the Plate menu while the edits are unsaved: only the name and modified time change.
        fixture.Clock.Tick();
        await game.Library.RenamePlateAsync(plateId, "After Rename");
        await game.UnloadAsync();
        var platePath = fixture.Paths.GetPlatePath(plateId);
        var savedBytes = File.ReadAllBytes(platePath);

        var next = await GameSession.StartAsync(fixture);
        Assert.Equal(KeptChangesChoice.Restore, Assert.Single(await next.LoadKeptChangesAsync()).Choice);
        Assert.Equal(KeptChangesVariant.Restore, next.Offer.CurrentVariant);
        next.Offer.Choose();

        Assert.Null(next.Offer.Error);
        Assert.Equal(plateId, next.Profiles.OpenPlateId);
        Assert.True(next.Session.IsDirty);
        Assert.Equal("After Rename", next.Document.Name);
        Assert.Contains(next.Document.Elements, e => e.Id == textId && e is TextProfileElement { Text: "Kept across the reload" });
        Assert.Equal(new[] { EditorSurfaceKind.Advanced }, next.Shown);
        Assert.Equal(savedBytes, File.ReadAllBytes(platePath));
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
        Assert.Single(KeptFiles.Trashed(fixture.Paths));
        Assert.False(next.Offer.HasCurrent);

        // One Undo is the saved Plate; Redo brings the changes back.
        next.Session.Undo();
        Assert.False(next.Session.IsDirty);
        Assert.DoesNotContain(next.Document.Elements, e => e.Id == textId);
        Assert.False(next.Session.CanUndo);
        next.Session.Redo();
        Assert.True(next.Session.IsDirty);

        Assert.True(await next.Session.SaveProfileAsync());
        var saved = fixture.ReadPlateJson(plateId);
        Assert.Contains("Kept across the reload", saved, StringComparison.Ordinal);
        Assert.Contains("After Rename", saved, StringComparison.Ordinal);
        next.Session.SyncWithCurrentProfile();
        Assert.False(next.Session.IsDirty);
    }

    [Fact]
    public async Task RevertToSaved_AfterARestore_DiscardsTheKeptChanges()
    {
        using var fixture = new LibraryFixture();
        var plateId = await KeepAnEditAsync(fixture);

        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();
        next.Offer.Choose();

        Assert.True(next.Session.RevertToSaved(undoable: true));
        Assert.False(next.Session.IsDirty);
        Assert.DoesNotContain(next.Document.Elements, e => e is TextProfileElement { Text: "Kept text" });
        Assert.Equal(plateId, next.Profiles.OpenPlateId);
    }

    [Fact]
    public async Task AChangedBase_OffersOnlyANewPlate_AndTheOriginalStaysByteIdentical()
    {
        using var fixture = new LibraryFixture();
        var plateId = await KeepAnEditAsync(fixture);

        // Another game client over the same folder saves the Plate first.
        var other = await GameSession.StartAsync(fixture);
        other.Open(plateId);
        other.Edit("Saved elsewhere");
        Assert.True(await other.Session.SaveProfileAsync());
        var platePath = fixture.Paths.GetPlatePath(plateId);
        var savedBytes = File.ReadAllBytes(platePath);

        var next = await GameSession.StartAsync(fixture);
        Assert.Equal(KeptChangesChoice.SavedAgain, Assert.Single(await next.LoadKeptChangesAsync()).Choice);
        Assert.Equal(KeptChangesVariant.SavedAgain, next.Offer.CurrentVariant);
        Assert.Equal(KeptChangesOffer.RestoreAsNewLabel, next.Offer.PrimaryLabel);

        next.Offer.Choose();
        await next.SettleAsync();

        Assert.Equal(savedBytes, File.ReadAllBytes(platePath));
        var created = next.Library.GetOrderedPlates().Single(p => p.PlateId != plateId);
        Assert.Contains("Kept text", fixture.ReadPlateJson(created.PlateId), StringComparison.Ordinal);
        Assert.DoesNotContain("Kept text", File.ReadAllText(platePath), StringComparison.Ordinal);
        Assert.Equal(created.PlateId, next.Profiles.OpenPlateId);
        Assert.False(next.Session.IsDirty);
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
    }

    [Fact]
    public async Task ASaveThisSession_MakesARestoreOfferANewPlateOne_AndNothingIsWritten()
    {
        using var fixture = new LibraryFixture();
        var plateId = await KeepAnEditAsync(fixture);
        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();
        Assert.Equal(KeptChangesVariant.Restore, next.Offer.CurrentVariant);

        next.Open(plateId);
        next.Edit("Saved meanwhile");
        Assert.True(await next.Session.SaveProfileAsync());
        next.Session.SyncWithCurrentProfile();
        var savedBytes = File.ReadAllBytes(fixture.Paths.GetPlatePath(plateId));

        next.Offer.Choose();

        Assert.Equal(KeptChangesOffer.ChangedMeanwhileMessage, next.Offer.Error);
        Assert.Equal(KeptChangesVariant.SavedAgain, next.Offer.CurrentVariant);
        Assert.False(next.Session.IsDirty);
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Equal(savedBytes, File.ReadAllBytes(fixture.Paths.GetPlatePath(plateId)));
    }

    [Fact]
    public async Task ADeletedPlate_IsNeverBroughtBack_OnlyANewPlateIsOffered()
    {
        using var fixture = new LibraryFixture();
        var plateId = await KeepAnEditAsync(fixture);
        var deleter = await GameSession.StartAsync(fixture);
        await deleter.Library.DeletePlateAsync(plateId);

        var next = await GameSession.StartAsync(fixture);
        Assert.Equal(KeptChangesChoice.Deleted, Assert.Single(await next.LoadKeptChangesAsync()).Choice);
        Assert.Equal(KeptChangesOffer.DeletedNote, next.Offer.VariantNote);
        next.Offer.Choose();
        await next.SettleAsync();

        Assert.False(File.Exists(fixture.Paths.GetPlatePath(plateId)));
        Assert.Null(next.Library.FindPlate(plateId));
        var created = Assert.Single(next.Library.GetOrderedPlates());
        Assert.NotEqual(plateId, created.PlateId);
        Assert.Equal("Edited (kept changes)", created.DisplayName);
    }

    [Fact]
    public async Task ANewerVersionsPlate_IsNeverWritten()
    {
        using var fixture = new LibraryFixture();
        var plateId = await KeepAnEditAsync(fixture);
        var platePath = fixture.Paths.GetPlatePath(plateId);
        File.WriteAllText(platePath, File.ReadAllText(platePath).Replace("\"Version\": 2,", "\"Version\": 99,", StringComparison.Ordinal));
        var savedBytes = File.ReadAllBytes(platePath);

        var next = await GameSession.StartAsync(fixture);
        Assert.Equal(KeptChangesChoice.SavedAgain, Assert.Single(await next.LoadKeptChangesAsync()).Choice);
        next.Offer.Choose();
        await next.SettleAsync();

        Assert.Equal(savedBytes, File.ReadAllBytes(platePath));
        Assert.Equal(2, next.Library.GetOrderedPlates().Count);
        Assert.NotEqual(plateId, next.Profiles.OpenPlateId);
    }

    [Fact]
    public async Task ADamagedPlate_IsNeverWritten()
    {
        using var fixture = new LibraryFixture();
        var plateId = await KeepAnEditAsync(fixture);
        var platePath = fixture.Paths.GetPlatePath(plateId);
        File.WriteAllText(platePath, "{ \"Version\": 2, \"ProfileId\": ");
        var damagedBytes = File.ReadAllBytes(platePath);

        var next = await GameSession.StartAsync(fixture);
        Assert.Equal(KeptChangesChoice.SavedAgain, Assert.Single(await next.LoadKeptChangesAsync()).Choice);
        next.Offer.Choose();
        await next.SettleAsync();

        Assert.Equal(damagedBytes, File.ReadAllBytes(platePath));
        Assert.Contains(next.Library.GetOrderedPlates(), p => p.PlateId != plateId && p.IsReady);
    }

    [Fact]
    public async Task ALockedPlate_OffersANewPlateOrLater_KeepsTheDraft_AndIsNeverWritten()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = await KeepAnEditAsync(fixture);
        var platePath = fixture.Paths.GetPlatePath(plateId);
        var savedBytes = File.ReadAllBytes(platePath);
        store.FailRead = path => path == platePath;

        var next = await GameSession.StartAsync(fixture);
        Assert.Equal(KeptChangesChoice.Unavailable, Assert.Single(await next.LoadKeptChangesAsync()).Choice);
        Assert.Equal(KeptChangesVariant.Unavailable, next.Offer.CurrentVariant);
        Assert.Equal(KeptChangesOffer.UnavailableNote, next.Offer.VariantNote);
        Assert.Equal(KeptChangesOffer.RestoreAsNewLabel, next.Offer.PrimaryLabel);
        Assert.False(next.Offer.OffersDiscard);

        next.Offer.Discard();
        Assert.True(next.Offer.HasCurrent);
        next.Offer.DecideLater();

        Assert.False(next.Offer.HasCurrent);
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Empty(KeptFiles.Trashed(fixture.Paths));
        Assert.Equal(savedBytes, File.ReadAllBytes(platePath));

        next.Offer.Review();
        next.Offer.Choose();
        await next.SettleAsync();
        Assert.Equal(savedBytes, File.ReadAllBytes(platePath));
        Assert.Contains(next.Library.GetOrderedPlates(), p => p.PlateId != plateId && p.IsReady);
    }

    [Fact]
    public async Task ChangesASaveAlreadyHolds_AreRetiredSilently()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync();
        game.Open(plateId);
        game.Edit();
        await game.UnloadAsync();
        var draft = Assert.Single(KeptFiles.Drafts(fixture.Paths));

        // The same edit saved afterwards (another window did it, say): the draft holds nothing new.
        var drafted = DraftDocuments.Parse(File.ReadAllText(draft)).Draft!;
        var other = await GameSession.StartAsync(fixture);
        other.Open(plateId);
        Assert.True(other.Session.ApplyRecoveredState(ProfileService.DocumentState.Capture(drafted.Document)));
        Assert.True(await other.Session.SaveProfileAsync());

        var next = await GameSession.StartAsync(fixture);
        Assert.Empty(await next.LoadKeptChangesAsync());

        Assert.False(next.Offer.HasCurrent);
        Assert.Null(next.Offer.ReminderText);
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
        Assert.Equal(Path.GetFileName(draft), Path.GetFileName(Assert.Single(KeptFiles.Trashed(fixture.Paths))));
        Assert.Contains(fixture.Log.Messages, m => m.Contains("they are its saved version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARestoredPlate_IsUnbound_NeverActive_AndNamedUniquely()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var keep = await game.Library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Edited (kept changes)");
        var plateId = (await game.Library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Edited")).PlateId;
        game.Open(plateId);
        game.Edit();
        await game.UnloadAsync();
        await (await GameSession.StartAsync(fixture)).Library.DeletePlateAsync(plateId);

        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();
        next.Offer.Choose();
        await next.SettleAsync();

        var created = next.Library.GetOrderedPlates().Single(p => p.PlateId != keep.PlateId);
        Assert.Equal("Edited (kept changes) 2", created.DisplayName);
        Assert.Empty(created.ActiveForContentIds);
        Assert.DoesNotContain(created.PlateId, next.Library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Equal(keep.PlateId, next.Library.GetActivePlateId(Characters.Alice.ContentId));
    }

    [Fact]
    public void KeptChangesNames_FitTheCap_AndAreUnique()
    {
        Assert.Equal("Look (kept changes)", PlateNaming.MakeKeptChangesName("Look", []));
        Assert.Equal("Look (kept changes) 3", PlateNaming.MakeKeptChangesName("  Look ", ["look (KEPT changes)", "Look (kept changes) 2"]));
        Assert.Equal(PlateNaming.DefaultName + " (kept changes)", PlateNaming.MakeKeptChangesName(" ", []));

        var longName = PlateNaming.MakeKeptChangesName(new string('x', 200), []);
        Assert.Equal(PlateNaming.MaxNameLength, longName.Length);
        Assert.EndsWith(" (kept changes)", longName, StringComparison.Ordinal);
        Assert.Equal("a b (kept changes)", PlateNaming.MakeKeptChangesName("a" + (char)0x000A + "b", []));
    }

    /// <summary>One session: a Plate named "Edited", opened, given one unsaved edit, and unloaded with it.</summary>
    private static async Task<Guid> KeepAnEditAsync(LibraryFixture fixture)
    {
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: "Edited");
        game.Open(plateId);
        game.Edit();
        await game.UnloadAsync();
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        return plateId;
    }
}
