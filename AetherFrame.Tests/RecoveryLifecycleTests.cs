using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Recovery checkpoints against Save, a failed Save, Discard, Revert, Save as New Plate and switching
/// Plates, with a checkpoint write still under way for each: a late write never brings back saved or
/// discarded work, a failed save keeps everything, and work left unsaved by switching stays kept.
/// </summary>
public class RecoveryLifecycleTests
{
    [Fact]
    public async Task Save_RetiresTheCheckpoints_AndTheNextLoadOffersNothing()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit();
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());

        Assert.True(await game.Session.SaveProfileAsync());
        await game.Recovery.RunAsync(1);

        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
        game.Recovery.Files.Crash();
        var next = await GameSession.StartAsync(fixture);
        Assert.Empty(await next.LoadKeptChangesAsync());
    }

    [Fact]
    public async Task Save_WhileACheckpointIsBeingWritten_TheLateWriteIsRetiredToo()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        await game.Recovery.FrameAsync();
        game.Edit();
        await game.Recovery.FrameAsync(0.2);

        // The checkpoint's write starts and is held on the disk.
        game.Recovery.Files.Hold();
        game.Recovery.Now += TimeSpan.FromSeconds(6);
        game.Session.SyncWithCurrentProfile();
        game.Recovery.Recovery.Tick();
        Assert.True(game.Recovery.Files.WriteStarted.Wait(TimeSpan.FromSeconds(10)));

        // Saved meanwhile; the frame after asks to retire the editing's checkpoints.
        Assert.True(await game.Session.SaveProfileAsync());
        game.Session.SyncWithCurrentProfile();
        game.Recovery.Recovery.Tick();

        game.Recovery.Files.Release();
        await game.Recovery.SettleAsync();

        Assert.Single(game.Recovery.Files.Written);
        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
        Assert.Equal(RecoveryIndicatorKind.None, game.Recovery.Recovery.Indicator.Kind);
    }

    [Fact]
    public async Task AFailedSave_KeepsEveryCheckpoint_AndTheChangesAreOfferedAfterACrash()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync();
        game.Open(plateId);
        game.Edit("not saved");
        await game.Recovery.RunAsync(6);

        store.FailWrite = path => path.EndsWith($"{plateId}.json", StringComparison.OrdinalIgnoreCase);
        Assert.False(await game.Session.SaveProfileAsync());
        await game.Recovery.RunAsync(10);

        Assert.Single(game.Recovery.OwnCheckpoints());
        Assert.True(game.Session.IsDirty);
        store.FailWrite = null;

        game.Recovery.Files.Crash();
        var next = await GameSession.StartAsync(fixture);
        var offered = Assert.Single(await next.LoadKeptChangesAsync());
        Assert.Equal(KeptChangesChoice.Restore, offered.Choice);
        Assert.Contains(offered.Draft.Document.Elements, e => e is TextProfileElement { Text: "not saved" });
    }

    [Fact]
    public async Task Discard_WhileACheckpointIsBeingWritten_LeavesNothingToOffer()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit("first");
        await game.Recovery.RunAsync(6);
        game.Edit("thrown away");
        await game.Recovery.FrameAsync(0.2);

        game.Recovery.Files.Hold();
        game.Recovery.Now += TimeSpan.FromSeconds(6);
        game.Session.SyncWithCurrentProfile();
        game.Recovery.Recovery.Tick();
        Assert.True(game.Recovery.Files.WriteStarted.Wait(TimeSpan.FromSeconds(10)));

        Assert.True(game.Session.DiscardChanges());
        game.Recovery.Recovery.Tick();
        game.Recovery.Files.Release();
        await game.Recovery.SettleAsync();

        Assert.Equal(2, game.Recovery.Files.Written.Count);
        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
        game.Recovery.Files.Crash();
        Assert.Empty(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
    }

    [Fact]
    public async Task Revert_RetiresTheCheckpoints_AndUndoingIt_IsCheckpointedAgain()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit("reverted, then back");
        await game.Recovery.RunAsync(6);

        Assert.True(game.Session.RevertToSaved(undoable: true));
        await game.Recovery.RunAsync(1);
        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));

        game.Session.Undo();
        await game.Recovery.RunAsync(6);
        var newest = RecoveryRig.Read(Assert.Single(game.Recovery.OwnCheckpoints()));
        Assert.Contains(newest.Document.Elements, e => e is TextProfileElement { Text: "reverted, then back" });
    }

    [Fact]
    public async Task OpeningAnotherPlate_WithUnsavedChanges_KeepsThemAsAFinalCheckpoint()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var first = await game.CreatePlateAsync(name: "First");
        var second = await game.CreatePlateAsync(name: "Second");
        game.Open(first);
        await game.Recovery.FrameAsync();
        game.Edit("left unsaved in First");

        // Opened before any checkpoint was due (as a deleted Plate's or a direct open's would be).
        await game.Recovery.FrameAsync(1);
        game.Open(second);
        await game.Recovery.FrameAsync();
        game.Edit("in Second");
        await game.Recovery.RunAsync(6);

        var files = KeptFiles.Checkpoints(fixture.Paths).Select(RecoveryRig.Read).ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal(2, files.Select(f => f.EditId).Distinct().Count());
        Assert.Contains(files, f => f.PlateId == first && f.Document.Elements.OfType<TextProfileElement>().Any(t => t.Text == "left unsaved in First"));

        game.Recovery.Files.Crash();
        var next = await GameSession.StartAsync(fixture);
        var offered = await next.LoadKeptChangesAsync();
        Assert.Equal(2, offered.Count);
        Assert.Equal(new[] { first, second }.ToHashSet(), offered.Select(o => o.PlateId).ToHashSet());
    }

    [Fact]
    public async Task DiscardThenOpenInTheSameFrame_RetiresTheCheckpoints()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var first = await game.CreatePlateAsync();
        var second = await game.CreatePlateAsync();
        game.Open(first);
        game.Edit();
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());

        // My Plates' Discard, then the other Plate opens, before recovery's next frame.
        Assert.True(game.Session.DiscardChanges());
        game.Open(second);
        await game.Recovery.RunAsync(1);

        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
    }

    [Fact]
    public async Task SaveAsNewPlate_ThatOpensTheCopy_RetiresTheOriginalsCheckpoints()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var original = await game.CreatePlateAsync(name: "Original");
        game.Open(original);
        game.Edit("now in the copy");
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());

        var actions = new PlateActions(game.Library, null!, null!, game.Profiles, game.Session, new PlateOperationRunner(fixture.Log), fixture.Log);
        actions.SaveAsNewPlate();
        for (var i = 0; i < 200 && game.Profiles.OpenPlateId == original; i++)
        {
            await Task.Delay(10);
            actions.Runner.Advance();
        }

        Assert.NotEqual(original, game.Profiles.OpenPlateId);
        await game.Recovery.RunAsync(1);
        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
    }

    [Fact]
    public async Task UnloadingWhileARetirementWaits_FinishesIt()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit();
        await game.Recovery.RunAsync(6);
        Assert.True(await game.Session.SaveProfileAsync());

        // Saved just before unloading: the frame asks to retire, then unloading stops recovery.
        game.Session.SyncWithCurrentProfile();
        game.Recovery.Recovery.Tick();
        game.Recovery.Recovery.Stop();
        Assert.True(await game.Recovery.Recovery.Writer.WaitIdleAsync(TimeSpan.FromSeconds(10)));
        game.Recovery.Store.CloseSession();

        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
        Assert.False(Directory.Exists(game.Recovery.Store.SessionDirectory));
    }

    [Fact]
    public async Task TheDraftKeptAtUnload_JoinsItsEditingsCheckpoints_InOneOffer()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Evening Look"));
        game.Edit("one");
        await game.Recovery.RunAsync(6);
        game.Edit("two");
        await game.Recovery.RunAsync(6);
        game.Edit("three");
        await game.Recovery.FrameAsync(1);

        await game.UnloadAsync();
        game.Recovery.Recovery.Stop();
        game.Recovery.Store.CloseSession();

        var next = await GameSession.StartAsync(fixture);
        var offered = Assert.Single(await next.LoadKeptChangesAsync());
        Assert.False(offered.IsCheckpoint);
        Assert.Equal(3, offered.Draft.Document.Elements.Count);
        Assert.Equal(2, offered.Older.Count);
        Assert.All(offered.Older, o => Assert.True(o.IsCheckpoint));
        Assert.Equal(3, next.Offer.CheckpointOptions!.Count);
        Assert.StartsWith("AetherFrame closed while", next.Offer.Body, StringComparison.Ordinal);
    }
}
