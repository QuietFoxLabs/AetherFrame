using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The independent rechecks of continuous recovery at <c>03e969c</c>, <c>efd4da6</c> and <c>b9a610d</c>: each case
/// failed at one of them, by losing kept work or by keeping saved, deleted or discarded work, and
/// passes with the fixes.
/// </summary>
public class RecoveryRecheckFixesTests
{
    // ---------------------------------------------------------------- Undo and Redo

    [Fact]
    public async Task UndoToTheSavedState_ThenRedoWithinOneSample_KeepsTheCheckpoints()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Flip"));
        await game.Recovery.FrameAsync();
        game.Edit("the work on screen");
        fixture.Clock.Tick();
        await game.Recovery.RunAsync(6);
        var kept = Assert.Single(game.Recovery.OwnCheckpoints());

        game.Session.Undo();
        await game.Recovery.FrameAsync(0.25);
        Assert.Equal(new[] { kept }, game.Recovery.OwnCheckpoints());

        // The first frame after the Redo: nothing can be redone, and the work is back on screen.
        game.Session.Redo();
        Assert.True(game.Session.IsDirty);
        await game.Recovery.FrameAsync(0.05);
        Assert.Equal(new[] { kept }, game.Recovery.OwnCheckpoints());
        await game.Recovery.RunAsync(4);
        game.Recovery.Files.Crash();

        var offered = await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync();
        Assert.Contains(offered, o => o.Draft.Document.Elements.Any(e => e is TextProfileElement { Text: "the work on screen" }));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(250)]
    [InlineData(350)]
    [InlineData(500)]
    public async Task UndoThenRedo_AtSixtyFramesASecond_NeverRetiresTheCheckpoints(int redoAfterMs)
    {
        for (var phase = 0; phase < 200; phase += 17)
        {
            using var fixture = new LibraryFixture();
            var game = await GameSession.StartAsync(fixture);
            game.Open(await game.CreatePlateAsync(name: "Flip"));
            await game.Recovery.FrameAsync();
            game.Edit("work");
            fixture.Clock.Tick();
            await game.Recovery.RunAsync(6, 1.0 / 60);
            Assert.Single(game.Recovery.OwnCheckpoints());
            await game.Recovery.RunAsync(phase / 1000.0, 1.0 / 60);

            game.Session.Undo();
            await game.Recovery.RunAsync(redoAfterMs / 1000.0, 1.0 / 60);
            game.Session.Redo();
            await game.Recovery.RunAsync(0.5, 1.0 / 60);
            Assert.Single(game.Recovery.OwnCheckpoints());
        }
    }

    [Fact]
    public async Task ResumedWork_UndoneAndQuicklyRedone_ThenACrash_IsStillOffered()
    {
        using var fixture = new LibraryFixture();
        await CrashWithEditsAsync(fixture, "one", "two", "three");

        var next = await GameSession.StartAsync(fixture);
        await next.LoadKeptChangesAsync();
        next.Offer.Choose();
        Assert.Null(next.Offer.Error);
        await next.Recovery.FrameAsync(0.2);
        Assert.Single(next.Recovery.OwnCheckpoints());

        // Resume Editing put the work back as one undoable step: Undo to compare, then Redo.
        next.Session.Undo();
        await next.Recovery.FrameAsync(0.25);
        next.Session.Redo();
        await next.Recovery.FrameAsync(0.05);
        await next.Recovery.RunAsync(4);
        next.Recovery.Files.Crash();

        var offered = await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync();
        Assert.Contains(offered, o => o.Draft.Document.Elements.Any(e => e is TextProfileElement { Text: "three" }));
    }

    [Fact]
    public async Task RevertToSaved_ThenUndo_CheckpointsTheWorkAtOnce()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Revert"));
        await game.Recovery.FrameAsync();
        game.Edit("hours of work");
        fixture.Clock.Tick();
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());

        // The revert is discarded work: its checkpoints go at once.
        Assert.True(game.Session.RevertToSaved(undoable: true));
        await game.Recovery.FrameAsync(0.1);
        Assert.Empty(game.Recovery.OwnCheckpoints());

        // "You can still undo the revert": the Undo brings it all back, and it is kept in that frame.
        game.Session.Undo();
        Assert.True(game.Session.IsDirty);
        await game.Recovery.FrameAsync(0.05);
        var kept = RecoveryRig.Read(Assert.Single(game.Recovery.OwnCheckpoints()));
        Assert.Contains(kept.Document.Elements, e => e is TextProfileElement { Text: "hours of work" });
        game.Recovery.Files.Crash();

        var offered = await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync();
        Assert.Contains(offered, o => o.Draft.Document.Elements.Any(e => e is TextProfileElement { Text: "hours of work" }));
    }

    [Fact]
    public async Task RevertEditAndUndoBoth_CheckpointsTheWorkAtOnce_WhenTheRevertIsUndone()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Revert"));
        await game.Recovery.FrameAsync();
        game.Edit("hours of work");
        fixture.Clock.Tick();
        await game.Recovery.RunAsync(6);

        // After the revert, an edit of its own is checkpointed, so the editing has a checkpoint when the revert is undone.
        Assert.True(game.Session.RevertToSaved(undoable: true));
        await game.Recovery.FrameAsync(0.1);
        game.Edit("tried something");
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());

        game.Session.Undo();
        await game.Recovery.FrameAsync(0.05);
        game.Session.Undo();
        await game.Recovery.FrameAsync(0.05);
        var newest = RecoveryRig.Read(game.Recovery.OwnCheckpoints()[0]);
        Assert.Contains(newest.Document.Elements, e => e is TextProfileElement { Text: "hours of work" });
    }

    [Fact]
    public async Task UndoOfARevert_RightAfterAnotherCheckpointWasAskedFor_StillCheckpointsTheWork()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Revert"));
        await game.Recovery.FrameAsync();
        const int Edits = 20;
        for (var i = 0; i < Edits; i++)
        {
            game.Edit($"edit {i}");
        }

        fixture.Clock.Tick();
        await game.Recovery.RunAsync(6);
        Assert.True(game.Session.RevertToSaved(undoable: true));
        await game.Recovery.FrameAsync(0.1);
        game.Edit("after 1");
        await game.Recovery.FrameAsync(1.0 / 60);
        game.Edit("after 2");
        await game.Recovery.FrameAsync(1.0 / 60);

        // Undo pressed every fourth frame: the first is the editing's first checkpoint, the third undoes
        // the revert eight frames later, and the steps after it come before any pause.
        for (var frame = 0; frame < 4 * 7; frame++)
        {
            if (frame % 4 == 0)
            {
                game.Session.Undo();
            }

            await game.Recovery.FrameAsync(1.0 / 60);
        }

        await game.Recovery.RunAsync(6);
        game.Recovery.Files.Crash();

        var offered = await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync();
        Assert.Contains(AllPoints(offered), d => Texts(d) == Edits);
    }

    [Fact]
    public async Task UndoPressedBackToTheSavedState_ThenACrash_StillOffersTheFullWork()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Pressed"));
        await game.Recovery.FrameAsync();
        const int Edits = 40;
        for (var i = 0; i < Edits; i++)
        {
            game.Edit($"edit {i}");
        }

        fixture.Clock.Tick();
        await game.Recovery.RunAsync(6);
        Assert.Equal(Edits, Texts(RecoveryRig.Read(Assert.Single(game.Recovery.OwnCheckpoints()))));

        // Undo pressed every other frame at 60 frames a second, until nothing is left to undo.
        var frame = 0;
        while (game.Session.CanUndo)
        {
            if (frame++ % 2 == 0)
            {
                game.Session.Undo();
            }

            await game.Recovery.FrameAsync(1.0 / 60);
        }

        Assert.False(game.Session.IsDirty);
        Assert.True(game.Session.CanRedo);
        await game.Recovery.RunAsync(1);
        game.Recovery.Files.Crash();

        var offered = await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync();
        Assert.Contains(AllPoints(offered), d => Texts(d) == Edits);
    }

    [Fact]
    public async Task UndoClickedFiveStepsToCompare_ThenACrash_StillOffersTheFullWork()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Compare"));
        await game.Recovery.FrameAsync();
        const int Edits = 10;
        for (var i = 0; i < Edits; i++)
        {
            game.Edit($"edit {i}");
        }

        fixture.Clock.Tick();
        await game.Recovery.RunAsync(6);
        var before = game.Recovery.Files.Written.Count;

        for (var i = 0; i < 5; i++)
        {
            game.Session.Undo();
            await game.Recovery.RunAsync(0.3);
        }

        // Undo waits for the pause as edits do: nothing written yet, and the full work is the newest point.
        Assert.True(game.Session.IsDirty);
        Assert.Equal(before, game.Recovery.Files.Written.Count);
        game.Recovery.Files.Crash();

        var offered = Assert.Single(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
        Assert.Equal(Edits, Texts(offered.Draft));
    }

    [Fact]
    public async Task UndoToTheSavedState_AsACheckpointFallsDue_NeverWritesTheSavedContent()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Undo"));
        await game.Recovery.FrameAsync();
        game.Edit("X");
        await game.Recovery.FrameAsync(0.2);
        await game.Recovery.FrameAsync(5.0);
        await game.Recovery.SettleAsync();
        Assert.Single(game.Recovery.OwnCheckpoints());

        game.Edit("Y");
        await game.Recovery.FrameAsync(0.2);
        await game.Recovery.FrameAsync(4.9);
        game.Session.Undo();
        game.Session.Undo();
        Assert.False(game.Session.IsDirty);
        Assert.True(game.Session.CanRedo);
        await game.Recovery.FrameAsync(0.1);
        await game.Recovery.FrameAsync(0.2);
        await game.Recovery.RunAsync(2);
        Assert.All(game.Recovery.OwnCheckpoints(), f => Assert.NotEmpty(RecoveryRig.Read(f).Document.Elements.OfType<TextProfileElement>()));
        game.Recovery.Files.Crash();

        // The changes one Redo away are offered, not retired as "the saved Plate".
        var offered = Assert.Single(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
        Assert.Contains(offered.Draft.Document.Elements, e => e is TextProfileElement { Text: "X" });
    }

    // ---------------------------------------------------------------- what lands between the last frame and unloading

    [Fact]
    public async Task ASaveAfterTheLastFrame_ThenUnloading_LeavesNothingToOffer()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "A"));
        await game.Recovery.FrameAsync();
        game.Edit("one");
        fixture.Clock.Tick();
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());
        game.Edit("two");
        await game.Recovery.RunAsync(1);

        // The save finishes off the framework thread; unloading follows before another frame.
        Assert.True(await game.Session.SaveProfileAsync());
        await UnloadAsync(game);
        fixture.Clock.Tick(60);

        Assert.Empty(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
    }

    [Fact]
    public async Task DeletingTheOpenPlateAfterTheLastFrame_ThenUnloading_LeavesNothingToOffer()
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
        await UnloadAsync(game);

        Assert.Empty(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
    }

    [Fact]
    public async Task AnUndoToTheSavedStateAfterTheLastSample_ThenUnloading_RetiresOnTheLiveDocument()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "A"));
        await game.Recovery.FrameAsync();
        game.Edit("undone before unloading");
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());

        game.Session.Undo();
        await UnloadAsync(game);

        Assert.Empty(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
    }

    // ---------------------------------------------------------------- races and failures

    [Fact]
    public async Task TheLastSave_IsKnownForRecovery_WhileTheFrameAdoptsIt()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Race"));
        game.Edit("content");
        var document = game.Document;
        var state = ProfileService.DocumentState.Capture(document);

        // A probe of a race: the frame adopting the save between its two steps lost the record at 03e969c.
        for (var i = 0; i < 500; i++)
        {
            var save = Task.Run(() => game.Session.SaveProfileAsync());
            while (!save.IsCompleted)
            {
                game.Session.SyncWithCurrentProfile();
            }

            Assert.True(await save);
            Assert.True(game.Session.WasSavedAs(document, state));
        }
    }

    [Fact]
    public async Task DiscardThenResume_WhileCheckpointsFail_ThenACrash_NeverOffersTheDiscardedWork()
    {
        using var fixture = new LibraryFixture();
        var first = await GameSession.StartAsync(fixture);
        var a = await first.CreatePlateAsync(name: "A");
        first.Open(a);
        await first.Recovery.FrameAsync();
        first.Edit("kept at unload");
        await UnloadAsync(first);
        fixture.Clock.Tick(60);

        var second = await GameSession.StartAsync(fixture);
        await second.LoadKeptChangesAsync();
        second.Offer.DecideLater();
        second.Open(a);
        await second.Recovery.FrameAsync();
        second.Edit("discarded by the player");
        fixture.Clock.Tick();
        await second.Recovery.RunAsync(6);
        Assert.Single(second.Recovery.OwnCheckpoints());

        second.Recovery.Files.FailWrite = _ => true;
        second.Offer.Review();
        second.Offer.Choose();
        Assert.NotNull(second.Offer.Question);
        second.Offer.AnswerDiscard();
        Assert.Null(second.Offer.Error);
        fixture.Clock.Tick();
        await second.Recovery.RunAsync(20);
        second.Recovery.Files.Crash();

        var offered = await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync();
        Assert.DoesNotContain(offered, o => o.Draft.Document.Elements.Any(e => e is TextProfileElement { Text: "discarded by the player" }));
    }

    [Fact]
    public async Task ADamagedCheckpoint_LeavesTheRecoveryFolder_WithItsAnswer()
    {
        using var fixture = new LibraryFixture();
        var files = await CrashWithEditsAsync(fixture, "one", "two");
        var bytes = File.ReadAllBytes(files[0]);
        File.WriteAllBytes(files[0], bytes[..(bytes.Length - 3)]);

        var next = await GameSession.StartAsync(fixture);
        Assert.Single(await next.LoadKeptChangesAsync());
        next.Offer.Choose();
        Assert.Null(next.Offer.Error);
        next.Recovery.Store.CloseSession();

        // Later starts neither read it again nor log it again; the ended run's folder can be tidied.
        var errors = fixture.Log.Messages.Count(m => m.Contains("could not read recovery checkpoint", StringComparison.Ordinal));
        var later = await GameSession.StartAsync(fixture);
        Assert.Empty(await later.LoadKeptChangesAsync());
        Assert.Equal(errors, fixture.Log.Messages.Count(m => m.Contains("could not read recovery checkpoint", StringComparison.Ordinal)));
        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
    }

    [Fact]
    public async Task ARecoveryFolderThatCantBeListed_StillOffersTheDraftsKeptAtUnload()
    {
        using var fixture = new LibraryFixture();
        var first = await GameSession.StartAsync(fixture);
        first.Open(await first.CreatePlateAsync(name: "A"));
        await first.Recovery.FrameAsync();
        first.Edit("kept at unload");
        await UnloadAsync(first);
        fixture.Clock.Tick(60);

        var next = await GameSession.StartAsync(fixture);
        next.Recovery.Files.FailListDirectories = true;
        next.Recovery.Store.Sweep();
        Assert.Single(await next.LoadKeptChangesAsync());
        Assert.Contains(fixture.Log.Messages, m => m.Contains("couldn't list its recovery folders", StringComparison.Ordinal));
    }

    private static int Texts(PlateDraft draft) => draft.Document.Elements.OfType<TextProfileElement>().Count();

    private static IEnumerable<PlateDraft> AllPoints(IReadOnlyList<KeptDraft> offered) =>
        offered.SelectMany(o => new[] { o.Draft }.Concat(o.Older.Select(p => p.Draft)));

    private static async Task UnloadAsync(GameSession game)
    {
        // As the plugin unloads: the draft kept at unload, recovery settled, its writes finished, its folder closed.
        await game.UnloadAsync();
        game.Recovery.Recovery.Stop();
        Assert.True(await game.Recovery.Recovery.Writer.WaitIdleAsync(TimeSpan.FromSeconds(10)));
        game.Recovery.Store.CloseSession();
    }

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
