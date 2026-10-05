using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The independent reviews of continuous recovery at <c>1353a4e</c>: each case failed there, by
/// bringing back saved or discarded work, or by losing kept work, and passes with the fixes.
/// </summary>
public class RecoveryReviewFixesTests
{
    // ---------------------------------------------------------------- saved, discarded, deleted: never offered

    [Fact]
    public async Task SaveAtTheOpenQuestion_ThenTheOtherPlateOpening_BeforeAFrame_RetiresTheSavedCheckpoints()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var a = await game.CreatePlateAsync(name: "A");
        var b = await game.CreatePlateAsync(name: "B");
        game.Open(a);
        await game.Recovery.FrameAsync();
        game.Edit("saved in A");
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());

        // My Plates' question: Save, and B opens in the frame the save is seen to finish, before recovery's tick.
        var guard = new PlateOpenGuard(game.Profiles, game.Session);
        Assert.Equal(PlateOpenDecision.Ask, guard.Request(b, basic: false));
        guard.Save();
        (PlateOpenRequest? Open, string? Error)? outcome = null;
        for (var i = 0; i < 500 && outcome is null; i++)
        {
            await Task.Delay(5);
            outcome = guard.Advance();
        }

        Assert.NotNull(outcome?.Open);
        game.Open(b);
        await game.Recovery.RunAsync(1);

        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.Contains("another Plate was opened", StringComparison.Ordinal));

        // A saved again later, then a clean unload: nothing is offered.
        game.Open(a);
        await game.Recovery.FrameAsync();
        game.Edit("second save of A");
        Assert.True(await game.Session.SaveProfileAsync());
        await game.Recovery.RunAsync(1);
        await game.UnloadAsync();
        game.Recovery.Recovery.Stop();
        Assert.True(await game.Recovery.Recovery.Writer.WaitIdleAsync(TimeSpan.FromSeconds(10)));
        game.Recovery.Store.CloseSession();
        Assert.Empty(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
    }

    [Fact]
    public async Task ResumeOverTheSamePlate_AfterTheQuestionsDiscard_LeavesNoDiscardedPoint()
    {
        using var fixture = new LibraryFixture();
        var first = await GameSession.StartAsync(fixture);
        var a = await first.CreatePlateAsync(name: "A");
        first.Open(a);
        await first.Recovery.FrameAsync();
        first.Edit("kept at unload");
        await first.UnloadAsync();
        first.Recovery.Recovery.Stop();
        first.Recovery.Store.CloseSession();
        fixture.Clock.Tick(60);

        var second = await GameSession.StartAsync(fixture);
        await second.LoadKeptChangesAsync();
        second.Offer.DecideLater();
        second.Open(a);
        await second.Recovery.FrameAsync();
        second.Edit("DISCARDED by the player");
        fixture.Clock.Tick();
        await second.Recovery.RunAsync(6);
        Assert.Single(second.Recovery.OwnCheckpoints());

        // Resume Editing over A; the question's Discard throws the open changes away and restores, in one frame.
        second.Offer.Review();
        second.Offer.Choose();
        Assert.NotNull(second.Offer.Question);
        second.Offer.AnswerDiscard();
        Assert.Null(second.Offer.Error);

        fixture.Clock.Tick();
        await second.Recovery.RunAsync(6);
        var kept = second.Recovery.OwnCheckpoints().Select(RecoveryRig.Read).ToList();
        Assert.All(kept, k => Assert.DoesNotContain(k.Document.Elements, e => e is TextProfileElement { Text: "DISCARDED by the player" }));
        second.Recovery.Files.Crash();

        var third = await GameSession.StartAsync(fixture);
        var offered = Assert.Single(await third.LoadKeptChangesAsync());
        Assert.Contains(offered.Draft.Document.Elements, e => e is TextProfileElement { Text: "kept at unload" });
        Assert.Empty(offered.Older);
    }

    [Fact]
    public async Task ARetirementThatFails_IsRetried_WhileThePlateIsEditedAgain()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "A"));
        await game.Recovery.FrameAsync();
        game.Edit("saved content");
        fixture.Clock.Tick();
        await game.Recovery.RunAsync(6);
        var saved = Assert.Single(game.Recovery.OwnCheckpoints());

        game.Recovery.Files.FailDelete = _ => true;
        Assert.True(await game.Session.SaveProfileAsync());
        await game.Recovery.RunAsync(1);
        Assert.True(File.Exists(saved));
        game.Recovery.Files.FailDelete = null;

        game.Edit("after the save");
        fixture.Clock.Tick();
        await game.Recovery.RunAsync(40);

        Assert.False(File.Exists(saved));
        game.Recovery.Files.Crash();
        var offered = Assert.Single(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
        Assert.Empty(offered.Older);
    }

    [Fact]
    public async Task AnOlderPointTheSavedPlateEquals_IsNeverOffered()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: "A");
        game.Open(plateId);
        await game.Recovery.FrameAsync();
        game.Edit("one");
        fixture.Clock.Tick();
        await game.Recovery.RunAsync(6);
        game.Edit("two");
        fixture.Clock.Tick();
        await game.Recovery.RunAsync(6);
        var files = game.Recovery.OwnCheckpoints();
        game.Recovery.Files.Crash();

        // Another client saves the older point's content exactly.
        var other = await GameSession.StartAsync(fixture);
        other.Open(plateId);
        Assert.True(other.Session.ApplyRecoveredState(ProfileService.DocumentState.Capture(RecoveryRig.Read(files[1]).Document)));
        Assert.True(await other.Session.SaveProfileAsync());

        var next = await GameSession.StartAsync(fixture);
        var offered = Assert.Single(await next.LoadKeptChangesAsync());
        Assert.Empty(offered.Older);
        Assert.Null(next.Offer.CheckpointOptions);
        next.Offer.Discard();
        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
    }

    [Fact]
    public async Task DeletingTheOpenPlate_WhoseChangesItsPromptSaysAreLost_RetiresThem()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var a = await game.CreatePlateAsync(name: "A");
        game.Open(a);
        await game.Recovery.FrameAsync();
        game.Edit("will be lost");
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());

        await game.Library.DeletePlateAsync(a);
        await game.Recovery.RunAsync(1);

        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
        game.Recovery.Files.Crash();
        Assert.Empty(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
    }

    // ---------------------------------------------------------------- kept work never lost

    [Fact]
    public async Task UndoBackToTheSavedState_KeepsTheCheckpoints_WhileTheChangesCanBeRedone()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Undo"));
        await game.Recovery.FrameAsync();
        game.Edit("thirty minutes of work");
        await game.Recovery.RunAsync(6);
        var kept = Assert.Single(game.Recovery.OwnCheckpoints());

        game.Session.Undo();
        await game.Recovery.RunAsync(6);
        Assert.Equal(new[] { kept }, game.Recovery.OwnCheckpoints());
        Assert.Equal(RecoveryIndicatorKind.None, game.Recovery.Recovery.Indicator.Kind);

        // Revert to Saved, which clears what can be redone, retires them.
        game.Session.Redo();
        Assert.True(game.Session.RevertToSaved(undoable: false));
        await game.Recovery.RunAsync(1);
        Assert.Empty(game.Recovery.OwnCheckpoints());
    }

    [Fact]
    public async Task UndoBackToTheSavedState_ThenACrash_OffersTheRedoableChanges()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Undo"));
        await game.Recovery.FrameAsync();
        game.Edit("one Redo away");
        await game.Recovery.RunAsync(6);
        game.Session.Undo();
        await game.Recovery.RunAsync(1);
        game.Recovery.Files.Crash();

        var offered = Assert.Single(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
        Assert.Contains(offered.Draft.Document.Elements, e => e is TextProfileElement { Text: "one Redo away" });
    }

    [Fact]
    public async Task UndoBackToTheSavedState_ThenACleanUnload_OffersNothing()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Undo"));
        await game.Recovery.FrameAsync();
        game.Edit("undone, then the game closed");
        await game.Recovery.RunAsync(6);
        game.Session.Undo();
        await game.Recovery.RunAsync(1);

        await game.UnloadAsync();
        game.Recovery.Recovery.Stop();
        Assert.True(await game.Recovery.Recovery.Writer.WaitIdleAsync(TimeSpan.FromSeconds(10)));
        game.Recovery.Store.CloseSession();

        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
        Assert.Empty(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
    }

    [Fact]
    public async Task AResumedOlderPoint_GoesToTheTrash_AndIsCheckpointedAgainAtOnce()
    {
        using var fixture = new LibraryFixture();
        var files = await CrashWithEditsAsync(fixture, "one", "two", "three");
        var chosen = RecoveryRig.Read(files[^1]);

        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();
        next.Offer.SelectCheckpoint(2);
        next.Offer.Choose();
        Assert.Null(next.Offer.Error);

        // The chosen point is in the trash beside the token, not deleted.
        var trashed = KeptFiles.Trashed(fixture.Paths).Select(p => File.ReadAllText(p)).ToList();
        Assert.Equal(2, trashed.Count);
        Assert.Contains(trashed, t => t.Contains(chosen.DraftId.ToString(), StringComparison.OrdinalIgnoreCase));

        // The resumed editing starts unsaved, so its first checkpoint doesn't wait for a pause.
        await next.Recovery.FrameAsync(0.2);
        var resumed = RecoveryRig.Read(Assert.Single(next.Recovery.OwnCheckpoints()));
        Assert.Equal(new[] { "one" }, resumed.Document.Elements.OfType<TextProfileElement>().Select(t => t.Text).ToArray());
    }

    [Fact]
    public async Task ADamagedNewestCheckpoint_IsLeftInPlace_WhenItsEditingIsAnswered()
    {
        using var fixture = new LibraryFixture();
        var files = await CrashWithEditsAsync(fixture, "one", "two");
        var bytes = File.ReadAllBytes(files[0]);
        File.WriteAllBytes(files[0], bytes[..(bytes.Length - 3)]);

        var next = await GameSession.StartAsync(fixture);
        var offered = Assert.Single(await next.LoadKeptChangesAsync());
        Assert.DoesNotContain(files[0], offered.OtherFiles);
        next.Offer.Choose();
        Assert.Null(next.Offer.Error);

        Assert.True(File.Exists(files[0]));
        Assert.Equal(bytes[..(bytes.Length - 3)], File.ReadAllBytes(files[0]));
    }

    [Fact]
    public async Task ACheckpointThatCantBeOpened_HoldsItsEditingBack_Untouched()
    {
        using var fixture = new LibraryFixture();
        var files = await CrashWithEditsAsync(fixture, "one", "two");

        using (new FileStream(files[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var next = await GameSession.StartAsync(fixture);
            Assert.Empty(await next.LoadKeptChangesAsync());
            Assert.All(files, f => Assert.True(File.Exists(f)));
        }

        var later = await GameSession.StartAsync(fixture);
        Assert.Single(Assert.Single(await later.LoadKeptChangesAsync()).Older);
    }

    [Fact]
    public async Task ADraftAnsweredToday_IsDatedToday_SoTheTrashCapKeepsIt()
    {
        using var fixture = new LibraryFixture();
        var files = await CrashWithEditsAsync(fixture, "weeks old work");
        File.SetLastWriteTimeUtc(files[0], DateTime.UtcNow.AddDays(-40));
        Directory.CreateDirectory(fixture.Paths.DraftTrashDirectory);
        for (var i = 0; i < RecoveryCheckpointStore.KeptInTrash; i++)
        {
            var path = Path.Combine(fixture.Paths.DraftTrashDirectory, $"{Guid.NewGuid()}.unsaved-{i:D4}.json");
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        }

        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();
        next.Offer.Choose();
        Assert.Null(next.Offer.Error);
        var token = Assert.Single(KeptFiles.Trashed(fixture.Paths), p => p.Contains(".checkpoint-", StringComparison.Ordinal));

        new RecoveryCheckpointStore(fixture.Paths, new TestRecoveryFiles(), Guid.NewGuid(), fixture.Log).Sweep();
        Assert.True(File.Exists(token));
        Assert.Equal(RecoveryCheckpointStore.KeptInTrash, KeptFiles.Trashed(fixture.Paths).Length);
    }

    [Fact]
    public async Task AFinalCheckpointAskedForAsAnotherPlateOpens_IsStillWritten_WhenUnloadingFollows()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var a = await game.CreatePlateAsync(name: "A");
        var b = await game.CreatePlateAsync(name: "B");
        game.Open(a);
        await game.Recovery.FrameAsync();
        game.Edit("first");
        await game.Recovery.FrameAsync(0.2);

        // A checkpoint of A is held on the disk, so A's final one waits behind it.
        game.Recovery.Files.Hold();
        game.Recovery.Now += TimeSpan.FromSeconds(6);
        game.Session.SyncWithCurrentProfile();
        game.Recovery.Recovery.Tick();
        Assert.True(game.Recovery.Files.WriteStarted.Wait(TimeSpan.FromSeconds(10)));
        game.Edit("left unsaved in A");
        game.Open(b);
        game.Recovery.Recovery.Tick();

        // Unloading begins before either is written.
        game.Recovery.Recovery.Stop();
        game.Recovery.Files.Release();
        Assert.True(await game.Recovery.Recovery.Writer.WaitIdleAsync(TimeSpan.FromSeconds(10)));

        var newest = RecoveryRig.Read(KeptFiles.Checkpoints(fixture.Paths).OrderByDescending(p => RecoveryRig.Read(p).Sequence).First());
        Assert.Contains(newest.Document.Elements, e => e is TextProfileElement { Text: "left unsaved in A" });
    }

    [Fact]
    public async Task ALockThatCantBeTaken_WritesNoCheckpoint_AndSaysSo_UntilItCan()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        await game.Recovery.FrameAsync();
        game.Recovery.Files.FailLock = true;
        game.Edit();
        await game.Recovery.RunAsync(6);

        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
        Assert.Equal(RecoveryIndicatorKind.Failing, game.Recovery.Recovery.Indicator.Kind);

        game.Recovery.Files.FailLock = false;
        game.Recovery.Recovery.RetryNow();
        await game.Recovery.SettleAsync();
        await game.Recovery.SettleAsync();
        Assert.Single(game.Recovery.OwnCheckpoints());
        Assert.True(File.Exists(Persistence.PlateStoragePaths.GetRecoverySessionLockPath(game.Recovery.Store.SessionDirectory)));
    }

    /// <summary>One checkpoint per edit of a new Plate in the Advanced editor, then a crash; its files, newest first.</summary>
    private static async Task<string[]> CrashWithEditsAsync(LibraryFixture fixture, params string[] edits)
    {
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Probe"));
        await game.Recovery.FrameAsync();
        foreach (var text in edits)
        {
            game.Edit(text);
            fixture.Clock.Tick();
            await game.Recovery.RunAsync(6);
        }

        var files = game.Recovery.OwnCheckpoints();
        Assert.Equal(edits.Length, files.Length);
        game.Recovery.Files.Crash();
        return files;
    }
}
