using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Continuous crash recovery while editing (October 5, 2026): when a checkpoint is written (5 seconds
/// after editing pauses, at least every 30 seconds while it goes on, on elapsed time), what it holds
/// (everything persistent, an edit in progress included), and everything it must never do (save,
/// share, touch history or dirty state, end an edit).
/// </summary>
public class ContinuousRecoveryTests
{
    // ---------------------------------------------------------------- when

    [Fact]
    public async Task OneEdit_IsCheckpointedFiveSecondsAfterEditingPauses_NotBefore()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        await game.Recovery.FrameAsync();

        game.Edit("Kept by recovery");
        await game.Recovery.RunAsync(4.8);
        Assert.Empty(game.Recovery.OwnCheckpoints());

        await game.Recovery.RunAsync(0.5);
        var checkpoint = RecoveryRig.Read(Assert.Single(game.Recovery.OwnCheckpoints()));
        Assert.Contains(checkpoint.Document.Elements, e => e is TextProfileElement { Text: "Kept by recovery" });
        Assert.Equal(game.Recovery.Store.SessionId, checkpoint.SessionId);
        Assert.Equal(game.Recovery.Recovery.CurrentEditId, checkpoint.EditId);
        Assert.Equal(1, checkpoint.Sequence);
        Assert.Equal(DraftEditor.Advanced, checkpoint.Editor);
    }

    [Fact]
    public async Task ContinuousEditing_IsCheckpointedEveryThirtySeconds_WhileItNeverPauses()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        var textId = game.Edit("0");
        await game.Recovery.FrameAsync();
        var start = game.Recovery.Now;

        // A keystroke every second for 65 seconds: editing never pauses for 5.
        var keystrokes = 0;
        var writtenAt = new System.Collections.Generic.List<TimeSpan>();
        for (var second = 0; second < 65; second++)
        {
            game.Session.BeginOrContinueEdit(textId, e => ((TextProfileElement)e).Text = (++keystrokes).ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (var step = 0; step < 10; step++)
            {
                var before = game.Recovery.Files.Written.Count;
                await game.Recovery.FrameAsync(0.1);
                if (game.Recovery.Files.Written.Count > before)
                {
                    writtenAt.Add(game.Recovery.Now - start);
                }
            }
        }

        Assert.Equal(2, writtenAt.Count);
        Assert.InRange(writtenAt[0].TotalSeconds, 29.9, 30.5);
        // The second counts from the first change after the first checkpoint (second 31), as sampled.
        Assert.InRange(writtenAt[1].TotalSeconds, 60.9, 61.3);

        // The newest holds the text as it stood when it was taken, mid-edit.
        var newest = RecoveryRig.Read(game.Recovery.OwnCheckpoints()[0]);
        Assert.Matches("^[0-9]+$", newest.Document.Elements.OfType<TextProfileElement>().Single().Text);
    }

    [Fact]
    public async Task NothingChanged_NothingIsWritten_HoweverLongItStaysOpen()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        await game.Recovery.RunAsync(60, step: 1);
        Assert.Empty(game.Recovery.Files.Written);

        game.Edit();
        await game.Recovery.RunAsync(120, step: 0.5);

        Assert.Single(game.Recovery.Files.Written);
        Assert.Equal(RecoveryIndicatorKind.Protected, game.Recovery.Recovery.Indicator.Kind);
    }

    [Fact]
    public async Task AnEditUndoneBeforeItsCheckpoint_WritesNothing()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        await game.Recovery.FrameAsync();

        game.Edit();
        await game.Recovery.RunAsync(2);
        game.Session.Undo();
        await game.Recovery.RunAsync(40);

        Assert.Empty(game.Recovery.Files.Written);
    }

    [Fact]
    public async Task ASecondEdit_AfterACheckpoint_GetsANewerOne_AndTheNewestHoldsBoth()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit("first");
        await game.Recovery.RunAsync(6);
        game.Edit("second");
        await game.Recovery.RunAsync(6);

        var files = game.Recovery.OwnCheckpoints();
        Assert.Equal(2, files.Length);
        var newest = RecoveryRig.Read(files[0]);
        Assert.Equal(2, newest.Sequence);
        Assert.Equal(["first", "second"], newest.Document.Elements.OfType<TextProfileElement>().Select(t => t.Text).Order().ToArray());
    }

    // ---------------------------------------------------------------- what

    [Fact]
    public async Task TextBeingTyped_IsCheckpointed_WithoutCommittingIt()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        var textId = game.Edit("before");
        await game.Recovery.RunAsync(6);

        // Typing, the field still focused: the edit is pending, not in the history yet.
        var undoBefore = game.Session.CanUndo;
        game.Session.BeginOrContinueEdit(textId, e => ((TextProfileElement)e).Text = "typed but not committed");
        await game.Recovery.RunAsync(6);

        var newest = RecoveryRig.Read(game.Recovery.OwnCheckpoints()[0]);
        Assert.Equal("typed but not committed", newest.Document.Elements.OfType<TextProfileElement>().Single().Text);

        // Still pending: committing now records exactly one entry, whose undo goes back to "before".
        Assert.True(game.Session.IsDirty);
        Assert.Equal(undoBefore, game.Session.CanUndo);
        game.Session.CommitPendingEdit();
        game.Session.Undo();
        Assert.Equal("before", game.Document.Elements.OfType<TextProfileElement>().Single().Text);
    }

    [Fact]
    public async Task ADragInProgress_IsCheckpointed_AndStillGoesOn()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        var textId = game.Edit();
        await game.Recovery.RunAsync(6);
        var element = game.Document.Elements.Single(e => e.Id == textId);
        var start = element.Position + new Vector2(5, 5);

        game.Session.BeginDrag(element, start);
        game.Session.UpdateInteraction(start + new Vector2(40, 25), snap: false, snapThreshold: 0f);
        var moved = game.Document.Elements.Single(e => e.Id == textId).Position;
        await game.Recovery.RunAsync(6);

        Assert.Equal(ElementInteractionKind.Dragging, game.Session.ActiveInteraction);
        var newest = RecoveryRig.Read(game.Recovery.OwnCheckpoints()[0]);
        Assert.Equal(moved, newest.Document.Elements.Single(e => e.Id == textId).Position);

        game.Session.UpdateInteraction(start + new Vector2(60, 25), snap: false, snapThreshold: 0f);
        game.Session.EndInteraction();
        Assert.Equal(ElementInteractionKind.None, game.Session.ActiveInteraction);
    }

    [Fact]
    public async Task Components_CanvasSettings_AndBackground_AreCheckpointed()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        await game.Recovery.FrameAsync();

        var component = game.Session.AddComponent(BuiltInComponentCatalog.PlateFrameLine);
        game.Session.ApplyCanvasResize(1600, 900, scaleContentsProportionally: false);
        game.Session.ApplyBackgroundEdit(style => style.Opacity = 0.42f);
        await game.Recovery.RunAsync(6);

        var newest = RecoveryRig.Read(Assert.Single(game.Recovery.OwnCheckpoints()));
        Assert.Contains(newest.Document.Components!, c => c.Id == component);
        Assert.Equal(1600, newest.Document.CanvasWidth);
        Assert.Equal(900, newest.Document.CanvasHeight);
        Assert.Equal(0.42f, newest.Document.Background!.Opacity);
    }

    [Fact]
    public async Task AnImage_IsKeptAsItsManagedReference_NeverItsBytes_AndCountsAsInUse()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        await game.Recovery.FrameAsync();
        var source = TestImages.Write(Path.Combine(fixture.Root, "user-files"), "photo.png", TestImages.Png(320, 200));

        game.Session.AddImageElement(source);
        var image = game.Document.Elements.OfType<ImageProfileElement>().Single();
        await game.Recovery.RunAsync(6);

        var file = Assert.Single(game.Recovery.OwnCheckpoints());
        Assert.Equal(image.AssetId, RecoveryRig.Read(file).Document.Elements.OfType<ImageProfileElement>().Single().AssetId);
        Assert.True(new FileInfo(file).Length < 16 * 1024);
        Assert.Equal([file], Directory.GetFiles(game.Recovery.Store.SessionDirectory, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal)).ToArray());

        // The dormant image cleanup's scan counts it, as it does a draft's.
        var scan = await game.Library.ScanAssetReferencesAsync();
        Assert.True(scan.IsComplete);
        Assert.Contains(image.AssetId, scan.ReferencedAssetIds);
    }

    [Fact]
    public async Task TheBasicEditor_IsCheckpointedToo_AndSaysSo()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var rig = new RecoveryRig(harness.Fixture.Paths, harness.Profiles, harness.Session, () => EditorSurfaceKind.Basic, harness.Fixture.Log);
        harness.SimulateBasicFrame();
        await rig.FrameAsync();

        harness.Basic.SetText(ProfileElementRole.BasicMessage, "Typed in the Basic editor");
        harness.SimulateBasicFrame();
        await rig.RunAsync(6);

        var checkpoint = RecoveryRig.Read(Assert.Single(rig.OwnCheckpoints()));
        Assert.Equal(DraftEditor.Basic, checkpoint.Editor);
        Assert.Contains(checkpoint.Document.Elements, e => e is TextProfileElement { Role: ProfileElementRole.BasicMessage, Text: "Typed in the Basic editor" });
    }

    [Fact]
    public async Task ANewPlate_IsCheckpointedFromItsFirstEdit()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: "Brand new");
        game.Open(plateId);
        await game.Recovery.FrameAsync();

        game.Edit();
        await game.Recovery.RunAsync(6);

        var checkpoint = RecoveryRig.Read(Assert.Single(game.Recovery.OwnCheckpoints()));
        Assert.Equal(plateId, checkpoint.PlateId);
        Assert.Equal("Brand new", checkpoint.PlateName);
    }

    // ---------------------------------------------------------------- never

    [Fact]
    public async Task ACheckpoint_NeverSaves_Shares_ChangesHistory_DirtyState_OrTheActivePlate()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync();
        game.Open(plateId);
        var plateFile = fixture.Paths.GetPlatePath(plateId);
        var savedBytes = File.ReadAllBytes(plateFile);
        var revision = game.Library.FindPlate(plateId)!.Revision;
        var saves = 0;
        game.Library.PlateSaved += _ => saves++;

        game.Edit("one");
        game.Edit("two");
        await game.Recovery.RunAsync(6);
        Assert.Single(game.Recovery.OwnCheckpoints());

        Assert.Equal(savedBytes, File.ReadAllBytes(plateFile));
        Assert.Equal(revision, game.Library.FindPlate(plateId)!.Revision);
        Assert.Equal(revision, game.Document.Revision);
        Assert.Equal(0, saves);
        Assert.True(game.Session.IsDirty);
        Assert.Null(game.Library.GetActivePlateId(1234));

        // Exactly two undo steps, then the saved Plate, then nothing more.
        game.Session.Undo();
        game.Session.Undo();
        Assert.False(game.Session.CanUndo);
        Assert.False(game.Session.IsDirty);
        Assert.True(game.Session.CanRedo);
    }
}
