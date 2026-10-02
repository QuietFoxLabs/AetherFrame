using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Kept changes are claimed by moving them to the trash before anything acts on them (D8: no lock
/// spans game clients), so only one game window ever acts on a draft; a move that fails leaves it
/// where it was and nothing happens; nothing ever deletes one; a Library that failed to load leaves
/// them untouched; and the dormant image cleanup's scan counts them.
/// </summary>
public class KeptChangesClaimTests
{
    [Fact]
    public async Task TwoGameWindows_SeeTheSameDraft_TheFirstClaimWins_TheSecondSaysItWasHandledThere()
    {
        using var fixture = new LibraryFixture();
        var plateId = await KeepAnEditAsync(fixture);
        var first = await GameSession.StartAsync(fixture);
        var second = await GameSession.StartAsync(fixture);
        Assert.Single(await first.LoadKeptChangesAsync());
        Assert.Single(await second.LoadKeptChangesAsync());

        first.Offer.Choose();
        second.Offer.Choose();

        Assert.Equal(plateId, first.Profiles.OpenPlateId);
        Assert.True(first.Session.IsDirty);
        Assert.Null(second.Profiles.CurrentProfile);
        Assert.Empty(second.Shown);
        Assert.Equal(KeptChangesOffer.AlreadyHandledMessage, second.Offer.Notice);
        Assert.False(second.Offer.HasCurrent);
        Assert.Single(KeptFiles.Trashed(fixture.Paths));
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
    }

    [Fact]
    public async Task ADiscardInAnotherWindow_LeavesNothingToRestore()
    {
        using var fixture = new LibraryFixture();
        await KeepAnEditAsync(fixture);
        var first = await GameSession.StartAsync(fixture);
        var second = await GameSession.StartAsync(fixture);
        await first.LoadKeptChangesAsync();
        await second.LoadKeptChangesAsync();

        first.Offer.Discard();
        second.Offer.Discard();

        Assert.Null(first.Offer.Notice);
        Assert.Equal(KeptChangesOffer.AlreadyHandledMessage, second.Offer.Notice);
        Assert.Single(KeptFiles.Trashed(fixture.Paths));
    }

    [Fact]
    public async Task AFailedMove_KeepsTheDraft_AndRestoresNothing()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = await KeepAnEditAsync(fixture);
        var draft = Assert.Single(KeptFiles.Drafts(fixture.Paths));
        store.FailMove = path => path == draft;
        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();

        next.Offer.Choose();

        Assert.Equal(KeptChangesOffer.ClaimFailedMessage, next.Offer.Error);
        Assert.Null(next.Profiles.CurrentProfile);
        Assert.Empty(next.Shown);
        Assert.True(next.Offer.HasCurrent);
        Assert.Equal(new[] { draft }, KeptFiles.Drafts(fixture.Paths));

        next.Offer.Discard();
        Assert.Equal(KeptChangesOffer.ClaimFailedMessage, next.Offer.Error);
        Assert.True(File.Exists(draft));

        // Nor is a new Plate made from it.
        await next.Library.DeletePlateAsync(plateId);
        next.Offer.Choose();
        next.Offer.Choose();
        await next.SettleAsync();
        Assert.Empty(next.Library.GetOrderedPlates());
        Assert.True(File.Exists(draft));
    }

    [Fact]
    public async Task ARestoreAsNewPlateThatFails_PutsTheDraftBack_StillOffered()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = await KeepAnEditAsync(fixture);
        var next = await GameSession.StartAsync(fixture);
        await next.Library.DeletePlateAsync(plateId);
        Assert.Equal(KeptChangesChoice.Deleted, Assert.Single(await next.LoadKeptChangesAsync()).Choice);
        store.FailWrite = path => path.StartsWith(fixture.Paths.PlatesDirectory, StringComparison.OrdinalIgnoreCase);

        next.Offer.Choose();
        await next.SettleAsync();

        Assert.EndsWith("The changes are still kept.", next.Offer.Error, StringComparison.Ordinal);
        Assert.True(next.Offer.HasCurrent);
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Empty(KeptFiles.Trashed(fixture.Paths));
        Assert.Empty(next.Library.GetOrderedPlates());

        // Once writing works again, the same offer restores it.
        store.FailWrite = null;
        next.Offer.Choose();
        await next.SettleAsync();
        Assert.Single(next.Library.GetOrderedPlates());
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADraftCopiedBackFromTheTrash_IsOfferedAgain_AndCanBeRestoredOrDiscarded(bool restore)
    {
        using var fixture = new LibraryFixture();
        var plateId = await KeepAnEditAsync(fixture);
        var draft = Assert.Single(KeptFiles.Drafts(fixture.Paths));
        var draftBytes = File.ReadAllBytes(draft);
        var first = await GameSession.StartAsync(fixture);
        await first.LoadKeptChangesAsync();
        first.Offer.Discard();
        var trashed = Assert.Single(KeptFiles.Trashed(fixture.Paths));

        // The player copies it back out of the trash to have it offered again: its name is taken there.
        File.Copy(trashed, draft);
        var next = await GameSession.StartAsync(fixture);
        Assert.Equal(KeptChangesChoice.Restore, Assert.Single(await next.LoadKeptChangesAsync()).Choice);

        if (restore)
        {
            next.Offer.Choose();
            Assert.Equal(plateId, next.Profiles.OpenPlateId);
            Assert.True(next.Session.IsDirty);
            Assert.Contains(next.Document.Elements, e => e is TextProfileElement { Text: "Kept text" });
        }
        else
        {
            next.Offer.Discard();
            Assert.Null(next.Profiles.CurrentProfile);
        }

        Assert.Null(next.Offer.Error);
        Assert.False(next.Offer.HasCurrent);
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
        var numbered = fixture.Paths.GetDraftTrashPath(draft, 2);
        Assert.Equal(Path.GetFileNameWithoutExtension(draft) + ".2.json", Path.GetFileName(numbered));
        Assert.Equal(new[] { trashed, numbered }.Order(StringComparer.Ordinal), KeptFiles.Trashed(fixture.Paths));
        Assert.All(KeptFiles.Trashed(fixture.Paths), path => Assert.Equal(draftBytes, File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task ADraftCopiedBackFromTheTrash_WhoseNewPlateFails_IsPutBackFromWhereItsClaimMovedIt()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = await KeepAnEditAsync(fixture);
        var draft = Assert.Single(KeptFiles.Drafts(fixture.Paths));
        var first = await GameSession.StartAsync(fixture);
        await first.LoadKeptChangesAsync();
        first.Offer.Discard();
        var trashed = Assert.Single(KeptFiles.Trashed(fixture.Paths));
        File.Copy(trashed, draft);

        var next = await GameSession.StartAsync(fixture);
        await next.Library.DeletePlateAsync(plateId);
        Assert.Equal(KeptChangesChoice.Deleted, Assert.Single(await next.LoadKeptChangesAsync()).Choice);
        store.FailWrite = path => path.StartsWith(fixture.Paths.PlatesDirectory, StringComparison.OrdinalIgnoreCase);

        next.Offer.Choose();
        await next.SettleAsync();

        Assert.EndsWith("The changes are still kept.", next.Offer.Error, StringComparison.Ordinal);
        Assert.True(next.Offer.HasCurrent);
        Assert.Equal(new[] { draft }, KeptFiles.Drafts(fixture.Paths));
        Assert.Equal(new[] { trashed }, KeptFiles.Trashed(fixture.Paths));

        store.FailWrite = null;
        next.Offer.Choose();
        await next.SettleAsync();
        Assert.Single(next.Library.GetOrderedPlates());
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
        Assert.Equal(2, KeptFiles.Trashed(fixture.Paths).Length);
    }

    [Fact]
    public async Task NothingEverDeletesADraft()
    {
        var store = new DeleteCountingStore();
        using var fixture = new LibraryFixture(store);
        await KeepAnEditAsync(fixture);
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: "Second");
        game.Open(plateId);
        game.Edit();
        await game.UnloadAsync();

        var next = await GameSession.StartAsync(fixture);
        Assert.Equal(2, (await next.LoadKeptChangesAsync()).Count);
        next.Offer.Discard();
        next.Offer.Choose();
        await next.SettleAsync();
        await next.UnloadAsync();
        await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync();

        Assert.DoesNotContain(store.Deletes, path =>
            path.StartsWith(fixture.Paths.DraftsDirectory, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(fixture.Paths.DraftTrashDirectory, StringComparison.OrdinalIgnoreCase));
        // The two answered moved to the trash; the restored one, unloaded unsaved, was kept again.
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Equal(2, KeptFiles.Trashed(fixture.Paths).Length);
    }

    [Fact]
    public async Task ALibraryThatFailedToLoad_LeavesDraftsUntouched_AndOffersNothing()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        await KeepAnEditAsync(fixture);
        var before = KeptFiles.Snapshot(fixture.Root);

        // The Plates folder can't be listed, so the Library doesn't load; neither may the drafts be listed.
        store.FailList = directory => directory == fixture.Paths.PlatesDirectory || directory == fixture.Paths.DraftsDirectory;
        var library = new PlateLibraryService(fixture.Paths, store, fixture.Log, () => fixture.Clock.Now);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => library.InitializeAsync());
        Assert.False(library.IsLoaded);

        var found = await KeptChangesReview.LoadAsync(new DraftStore(fixture.Paths, store, fixture.Log), library, fixture.Log);

        Assert.Empty(found);
        Assert.Equal(before, KeptFiles.Snapshot(fixture.Root));
    }

    // ---------------------------------------------------------------- the image cleanup's scan

    [Fact]
    public async Task AnImageOnlyKeptChangesUse_IsCounted_WaitingAndInTheTrash()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync();
        game.Open(plateId);
        var assetId = Guid.NewGuid();
        game.Session.AddElement(new ImageProfileElement { AssetId = assetId });
        await game.UnloadAsync();

        var waiting = await game.Library.ScanAssetReferencesAsync();
        Assert.True(waiting.IsComplete, string.Join("; ", waiting.Problems));
        Assert.Contains(assetId, waiting.ReferencedAssetIds);

        var templates = new TemplateLibraryService(fixture.Paths, fixture.Store, game.Library, fixture.Log, () => fixture.Clock.Now);
        await templates.InitializeAsync();
        Assert.Contains(assetId, (await LiveAssetReferences.ComputeAsync(game.Library, templates)).ReferencedAssetIds);

        Assert.Equal(DraftClaim.Claimed, game.Drafts.Claim(Assert.Single(KeptFiles.Drafts(fixture.Paths))));
        var trashed = await game.Library.ScanAssetReferencesAsync();
        Assert.True(trashed.IsComplete);
        Assert.Contains(assetId, trashed.ReferencedAssetIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableKeptChanges_MakeTheScanIncomplete(bool inTheTrash)
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var folder = inTheTrash ? fixture.Paths.DraftTrashDirectory : fixture.Paths.DraftsDirectory;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fixture.Paths.GetDraftPath(Guid.NewGuid(), fixture.Clock.Now, Guid.NewGuid()).Split(Path.DirectorySeparatorChar).Last()), "{ not json");

        var scan = await game.Library.ScanAssetReferencesAsync();

        Assert.False(scan.IsComplete);
        Assert.Contains(scan.Problems, p => p.StartsWith("Kept changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANewerVersionsKeptChanges_MakeTheScanIncomplete_ButTheirIdsStillCount()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var assetId = Guid.NewGuid();
        KeptFiles.WriteDraftJson(fixture.Paths, Guid.NewGuid(), fixture.Clock.Now, Guid.NewGuid(), $$"""{ "Version": 7, "Cover": "{{assetId}}" }""");

        var scan = await game.Library.ScanAssetReferencesAsync();

        Assert.False(scan.IsComplete);
        Assert.Contains(assetId, scan.ReferencedAssetIds);
    }

    /// <summary>One session: a Plate named "Edited", opened, given one unsaved edit, and unloaded with it.</summary>
    private static async Task<Guid> KeepAnEditAsync(LibraryFixture fixture)
    {
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: "Edited");
        game.Open(plateId);
        game.Edit();
        await game.UnloadAsync();
        return plateId;
    }
}
