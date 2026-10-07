using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Recovery while no Plate is open. Build 9bfa246 threw a NullReferenceException in
/// <c>ContinuousRecovery.Reconcile</c> at every frame from the game's start until a Plate was opened,
/// and again once the open Plate closed (its Plate deleted), so nothing ended was finished meanwhile:
/// a final checkpoint or a retirement that failed was never tried again, and unloading settled
/// nothing. On 89c392d every case here fails except the two that close the editor window: closing
/// an editor never closes the Plate, so those two guard that path rather than the null state.
/// </summary>
public class RecoveryNoOpenPlateTests
{
    [Fact]
    public async Task Startup_WithNoPlateOpen_FollowsNothing_AndProtectsThePlateOpenedLater()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        await game.Recovery.RunAsync(40, step: 0.5);

        Assert.Null(game.Recovery.Recovery.CurrentEditId);
        Assert.Equal(RecoveryIndicatorKind.None, game.Recovery.Recovery.Indicator.Kind);
        Assert.Empty(game.Recovery.Files.Written);

        game.Open(await game.CreatePlateAsync());
        await game.Recovery.FrameAsync();
        game.Edit("after a quiet start");
        await game.Recovery.RunAsync(6);

        Assert.Single(game.Recovery.OwnCheckpoints());
        Assert.Equal(RecoveryIndicatorKind.Protected, game.Recovery.Recovery.Indicator.Kind);
    }

    [Fact]
    public async Task Unloading_WithNoPlateEverOpen_SettlesWithoutAFailure()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        await game.Recovery.FrameAsync();

        game.Recovery.Recovery.Stop();
        Assert.True(await game.Recovery.Recovery.Writer.WaitIdleAsync(TimeSpan.FromSeconds(10)));

        RecoveryRig.AssertNoFailure(fixture.Log);
        Assert.Empty(game.Recovery.Files.Written);
    }

    [Fact]
    public async Task ClosingTheEditor_WithDiscard_RetiresTheCheckpoints_AndTheFramesAfterWriteNothing()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        await game.Recovery.FrameAsync();
        game.Edit("discarded as the editor closes");
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());

        // Closing the window never closes the Plate: the guard refuses the close while the changes
        // are unsaved, then Discard answers its question and the window goes.
        var guard = new EditorCloseGuard(game.Session, new EditorDocumentCommands(game.Profiles, game.Session));
        Assert.True(guard.PreOpenCheck(true));
        Assert.True(guard.PreOpenCheck(false));
        Assert.True(guard.IsAsking);
        Assert.True(guard.Discard());
        Assert.False(guard.ShouldReopenOnClose());
        game.ActiveSurface = null;
        await game.Recovery.RunAsync(40, step: 0.5);

        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
        Assert.Single(game.Recovery.Files.Written);
        Assert.Equal(RecoveryIndicatorKind.None, game.Recovery.Recovery.Indicator.Kind);
        game.Recovery.Files.Crash();
        Assert.Empty(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
    }

    [Fact]
    public async Task ClosingTheEditor_RefusedWithUnsavedChanges_KeepsThemProtected()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        await game.Recovery.FrameAsync();
        game.Edit("kept while the question is open");
        await game.Recovery.RunAsync(6);
        var kept = Assert.Single(game.Recovery.OwnCheckpoints());

        // Escape or a toggle gets the close through: the window reopens and asks instead.
        var guard = new EditorCloseGuard(game.Session, new EditorDocumentCommands(game.Profiles, game.Session));
        Assert.True(guard.PreOpenCheck(true));
        Assert.True(guard.ShouldReopenOnClose());
        guard.Cancel();
        await game.Recovery.RunAsync(10, step: 0.5);

        Assert.Equal(new[] { kept }, game.Recovery.OwnCheckpoints());
        Assert.Equal(RecoveryIndicatorKind.Protected, game.Recovery.Recovery.Indicator.Kind);
    }

    [Fact]
    public async Task ThePlateClosing_WithUnsavedChanges_KeepsAFinalCheckpoint_RetriedWhileNoPlateIsOpen()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: "Closed");
        game.Open(plateId);
        await game.Recovery.FrameAsync();
        game.Edit("first");
        await game.Recovery.RunAsync(6);
        game.Edit("pending as it closed");
        fixture.Clock.Tick();
        await game.Recovery.RunAsync(1);
        Assert.Single(game.Recovery.OwnCheckpoints());

        // The final checkpoint's first write fails as the Plate closes. CloseDocument stands for
        // any way the open Plate goes with nothing in its place; in the game that is its deletion
        // (see AnotherPlateOpened_ThenDeleted_RetriesTheFirstPlatesFinalCheckpointWhileNoPlateIsOpen).
        game.Recovery.Files.FailWrite = _ => true;
        game.Profiles.CloseDocument();
        game.ActiveSurface = null;
        await game.Recovery.FrameAsync(0.1);
        Assert.Single(game.Recovery.OwnCheckpoints());

        // With no Plate open, it is tried again after the retry pause.
        game.Recovery.Files.FailWrite = null;
        await game.Recovery.RunAsync(10, step: 0.5);
        Assert.Equal(2, game.Recovery.OwnCheckpoints().Length);

        game.Recovery.Files.Crash();
        var next = await GameSession.StartAsync(fixture);
        var offered = Assert.Single(await next.LoadKeptChangesAsync());
        Assert.Equal(plateId, offered.PlateId);
        next.Offer.Choose();
        Assert.Null(next.Offer.Error);
        Assert.Equal(new[] { "first", "pending as it closed" }, next.Document.Elements.OfType<TextProfileElement>().Select(t => t.Text).ToArray());
    }

    [Fact]
    public async Task Unloading_AfterThePlateClosed_StillWritesTheFinalCheckpointOwed()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        await game.Recovery.FrameAsync();
        game.Edit("first");
        await game.Recovery.RunAsync(6);
        game.Edit("owed at unload");
        await game.Recovery.RunAsync(1);

        game.Recovery.Files.FailWrite = _ => true;
        game.Profiles.CloseDocument();
        game.ActiveSurface = null;
        await game.Recovery.FrameAsync(0.1);
        game.Recovery.Files.FailWrite = null;

        // Unloading before the retry pause is over: the final checkpoint is asked for then.
        game.Recovery.Recovery.Stop();
        Assert.True(await game.Recovery.Recovery.Writer.WaitIdleAsync(TimeSpan.FromSeconds(10)));

        RecoveryRig.AssertNoFailure(fixture.Log);
        var newest = RecoveryRig.Read(game.Recovery.OwnCheckpoints()[0]);
        Assert.Contains(newest.Document.Elements, e => e is TextProfileElement { Text: "owed at unload" });
    }

    [Fact]
    public async Task DeletingTheOpenPlate_RetiresItsCheckpoints_RetriedWhileNoPlateIsOpen()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: "Deleted");
        game.Open(plateId);
        await game.Recovery.FrameAsync();
        game.Edit("lost with the Plate");
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());

        // The first retirement fails; the Plate stays closed.
        game.Recovery.Files.FailDelete = _ => true;
        await game.Library.DeletePlateAsync(plateId);
        await game.Recovery.FrameAsync(0.1);
        Assert.Null(game.Profiles.CurrentProfile);
        Assert.Single(KeptFiles.Checkpoints(fixture.Paths));

        game.Recovery.Files.FailDelete = null;
        await game.Recovery.RunAsync(10, step: 0.5);
        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
    }

    [Fact]
    public async Task AnotherPlateOpened_ThenDeleted_RetriesTheFirstPlatesFinalCheckpointWhileNoPlateIsOpen()
    {
        // The game's own way to no open Plate with a final checkpoint still owed: Plate A edited,
        // Plate B opened in its place while A's final checkpoint fails, then B deleted.
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var a = await game.CreatePlateAsync(name: "A");
        var b = await game.CreatePlateAsync(name: "B");
        game.Open(a);
        await game.Recovery.FrameAsync();
        game.Edit("first");
        await game.Recovery.RunAsync(6);
        game.Edit("pending when B opened");
        fixture.Clock.Tick();
        await game.Recovery.RunAsync(1);
        Assert.Single(game.Recovery.OwnCheckpoints());

        game.Recovery.Files.FailWrite = _ => true;
        game.Open(b);
        await game.Recovery.FrameAsync(0.1);
        Assert.Single(game.Recovery.OwnCheckpoints());

        game.Recovery.Files.FailWrite = null;
        await game.Library.DeletePlateAsync(b);
        await game.Recovery.RunAsync(10, step: 0.5);
        Assert.Null(game.Profiles.CurrentProfile);
        Assert.Equal(2, game.Recovery.OwnCheckpoints().Length);

        game.Recovery.Files.Crash();
        var next = await GameSession.StartAsync(fixture);
        var offered = Assert.Single(await next.LoadKeptChangesAsync());
        Assert.Equal(a, offered.PlateId);
        next.Offer.Choose();
        Assert.Null(next.Offer.Error);
        Assert.Equal(new[] { "first", "pending when B opened" }, next.Document.Elements.OfType<TextProfileElement>().Select(t => t.Text).ToArray());
    }

    [Fact]
    public async Task Restart_WithAnExistingCheckpoint_KeepsItWhileNoPlateIsOpen_AndResumeEditingRestoresTheTextAndItsPosition()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: "Crash Test");
        game.Open(plateId);
        await game.Recovery.FrameAsync();
        var textId = game.Edit("before the crash");
        Assert.Equal(textId, game.Session.SelectedElementId);
        game.Session.NudgeSelected(new Vector2(37, 21));
        var position = game.Document.Elements.Single(e => e.Id == textId).Position;
        Assert.NotEqual(new Vector2(ProfileElement.DefaultPositionX, ProfileElement.DefaultPositionY), position);
        await game.Recovery.RunAsync(6);
        var kept = Path.GetFileName(Assert.Single(game.Recovery.OwnCheckpoints()));
        game.Recovery.Files.Crash();

        // The next game: no Plate open while the player logs in, then while the offer waits. The
        // plugin sweeps the checkpoint folder before it loads kept changes.
        var next = await GameSession.StartAsync(fixture);
        await next.Recovery.RunAsync(30, step: 0.5);
        Assert.Equal(kept, Path.GetFileName(Assert.Single(KeptFiles.Checkpoints(fixture.Paths))));
        next.Recovery.Store.Sweep();
        Assert.Equal(kept, Path.GetFileName(Assert.Single(KeptFiles.Checkpoints(fixture.Paths))));
        var offered = Assert.Single(await next.LoadKeptChangesAsync());
        Assert.True(offered.IsCheckpoint);
        await next.Recovery.RunAsync(10, step: 0.5);
        Assert.Equal(kept, Path.GetFileName(Assert.Single(KeptFiles.Checkpoints(fixture.Paths))));
        Assert.Empty(next.Recovery.Files.Written);

        next.Offer.Choose();
        Assert.Null(next.Offer.Error);
        Assert.Equal(plateId, next.Profiles.OpenPlateId);
        Assert.True(next.Session.IsDirty);
        var text = Assert.Single(next.Document.Elements.OfType<TextProfileElement>());
        Assert.Equal("before the crash", text.Text);
        Assert.Equal(position, text.Position);

        // The resumed changes are protected again, text and position alike.
        await next.Recovery.RunAsync(1);
        var resumed = RecoveryRig.Read(Assert.Single(next.Recovery.OwnCheckpoints()));
        var written = Assert.Single(resumed.Document.Elements.OfType<TextProfileElement>());
        Assert.Equal("before the crash", written.Text);
        Assert.Equal(position, written.Position);
        Assert.Equal(RecoveryIndicatorKind.Protected, next.Recovery.Recovery.Indicator.Kind);
    }
}
