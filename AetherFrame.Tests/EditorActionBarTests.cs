using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The shared editor action bar's rules, outside ImGui: when Undo, Redo, Save and Revert are
/// available (the same for the Basic and Advanced editors, which share one session), what each does,
/// and where the bar's groups sit on its row.
/// </summary>
public class EditorActionBarTests
{
    private static EditorDocumentCommands Commands(BasicHarness harness) => new(harness.Profiles, harness.Session);

    // ---------------------------------------------------------------- availability

    [Fact]
    public async Task ACleanPlate_OffersNothingToSaveRevertUndoOrRedo()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var commands = Commands(harness);

        Assert.True(commands.HasPlate);
        Assert.False(commands.IsDirty);
        Assert.False(commands.CanSave);
        Assert.False(commands.CanRevert);
        Assert.False(commands.CanUndo);
        Assert.False(commands.CanRedo);
    }

    [Fact]
    public async Task AnEdit_EnablesSaveRevertAndUndo_ButNotRedo()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var commands = Commands(harness);

        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        Assert.True(commands.IsDirty);
        Assert.True(commands.CanSave);
        Assert.True(commands.CanRevert);
        Assert.True(commands.CanUndo);
        Assert.False(commands.CanRedo);
    }

    [Fact]
    public async Task UndoingBackToTheSavedState_DisablesSaveAndRevert_AndEnablesRedo()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var commands = Commands(harness);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        commands.Undo();

        Assert.Equal(AdventurePlateOrientation.Normal, BasicEditorSession.GetOrientation(harness.Document));
        Assert.False(commands.CanSave);
        Assert.False(commands.CanRevert);
        Assert.False(commands.CanUndo);
        Assert.True(commands.CanRedo);

        commands.Redo();

        Assert.Equal(AdventurePlateOrientation.Mirrored, BasicEditorSession.GetOrientation(harness.Document));
        Assert.True(commands.CanSave);
        Assert.False(commands.CanRedo);
    }

    [Fact]
    public async Task UndoAndRedo_DoNothingWhenUnavailable()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var commands = Commands(harness);
        var before = harness.Json();

        commands.Undo();
        commands.Redo();

        Assert.Equal(before, harness.Json());
        Assert.False(commands.IsDirty);
    }

    [Fact]
    public async Task NoPlate_OffersNoActions()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Profiles.CloseDocument();
        harness.Session.SyncWithCurrentProfile();
        var commands = Commands(harness);

        Assert.False(commands.HasPlate);
        Assert.False(commands.IsDirty);
        Assert.False(commands.CanSave);
        Assert.False(commands.CanRevert);
        Assert.False(commands.CanUndo);
        Assert.False(commands.CanRedo);
    }

    // ---------------------------------------------------------------- save

    [Fact]
    public async Task Save_IsRefusedForACleanPlate()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var commands = Commands(harness);

        Assert.False(commands.Save());
        Assert.False(await commands.SaveAsync());
        Assert.False(commands.IsDirty);
    }

    [Fact]
    public async Task Save_WritesTheChanges_AndClearsDirtyState()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var commands = Commands(harness);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        Assert.True(await commands.SaveAsync());
        harness.Session.SyncWithCurrentProfile();

        Assert.False(commands.IsDirty);
        Assert.False(commands.CanSave);
        Assert.False(commands.CanRevert);
        Assert.Equal(AdventurePlateOrientation.Mirrored, harness.Library.OpenDocumentForEditing(harness.PlateId).BasicPlate!.Orientation);
    }

    [Fact]
    public async Task TheSaveShortcut_InBasic_SavesOnlyUnsavedChanges()
    {
        // Ctrl+S in the Basic editor runs the action bar's own Save command.
        using var harness = await BasicHarness.NewClassicAsync();
        var commands = Commands(harness);
        harness.SimulateBasicFrame();

        Assert.False(commands.Save()); // clean: nothing happens
        Assert.Equal(0, harness.Library.FindPlate(harness.PlateId)!.Revision);

        harness.Basic.AddPlaystyle("Roleplay");
        Assert.True(await commands.SaveAsync());
        harness.Session.SyncWithCurrentProfile();

        Assert.False(commands.IsDirty);
        Assert.Equal(["Roleplay"], harness.Library.OpenDocumentForEditing(harness.PlateId).BasicPlate!.Playstyles);
    }

    // ---------------------------------------------------------------- revert

    [Fact]
    public async Task Revert_RestoresTheLastSavedVersion_AsOneUndoableStep()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var commands = Commands(harness);
        var saved = harness.Json();
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        harness.Basic.AddPlaystyle("Roleplay");

        Assert.True(commands.Revert());

        Assert.Equal(saved, harness.Json());
        Assert.False(commands.IsDirty);
        Assert.False(commands.CanRevert);

        // The revert itself can be taken back.
        commands.Undo();
        Assert.Equal(AdventurePlateOrientation.Mirrored, BasicEditorSession.GetOrientation(harness.Document));
        Assert.True(commands.IsDirty);
    }

    [Fact]
    public async Task Revert_AfterASave_GoesBackToThatSave()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var commands = Commands(harness);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        Assert.True(await commands.SaveAsync());
        harness.Session.SyncWithCurrentProfile();

        harness.Basic.AddPlaystyle("Roleplay");
        Assert.True(commands.Revert());

        Assert.Equal(AdventurePlateOrientation.Mirrored, BasicEditorSession.GetOrientation(harness.Document));
        Assert.Empty(harness.Document.BasicPlate!.Playstyles);
        Assert.False(commands.IsDirty);
    }

    [Fact]
    public async Task Revert_IsRefusedForACleanPlate()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var commands = Commands(harness);

        Assert.False(commands.Revert());
        Assert.False(commands.CanUndo);
    }

    [Fact]
    public async Task BothEditors_SeeTheSameAvailability()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var basicBar = Commands(harness);
        var advancedBar = Commands(harness);

        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);

        Assert.Equal(basicBar.CanSave, advancedBar.CanSave);
        Assert.Equal(basicBar.CanRevert, advancedBar.CanRevert);
        Assert.Equal(basicBar.CanUndo, advancedBar.CanUndo);
        Assert.True(advancedBar.CanSave);

        advancedBar.Undo();
        Assert.False(basicBar.CanSave);
        Assert.True(basicBar.CanRedo);
    }

    // ---------------------------------------------------------------- layout

    [Fact]
    public void Layout_CentersHistory_AndPutsTheDocumentGroupAtTheEnd()
    {
        var (centerX, rightX, nameWidth) = EditorActionBarLayout.Arrange(0f, 1000f, leftEnd: 150f, centerWidth: 60f, rightWidth: 300f, spacing: 10f);

        Assert.Equal(470f, centerX);
        Assert.Equal(700f, rightX);
        Assert.Equal(470f - 10f - 160f, nameWidth);
    }

    [Fact]
    public void Layout_OnANarrowRow_KeepsTheGroupsInOrderWithoutOverlap()
    {
        var (centerX, rightX, nameWidth) = EditorActionBarLayout.Arrange(0f, 400f, leftEnd: 150f, centerWidth: 60f, rightWidth: 300f, spacing: 10f);

        Assert.Equal(160f, centerX);
        Assert.Equal(230f, rightX);
        Assert.True(centerX >= 150f + 10f);
        Assert.True(rightX >= centerX + 60f + 10f);
        Assert.Equal(0f, nameWidth);
    }

    [Fact]
    public void ThePlateMenu_GetsTheRoomUpToTheHistoryGroup_ForTheName()
    {
        // The left group ends at 150, the control starts one gap later at its minimum width 40, and
        // the history group is centered: the name may widen the control to one gap before it.
        const float controlStart = 160f;
        const float controlMinimum = 40f;
        var (centerX, _, _) = EditorActionBarLayout.Arrange(0f, 1000f, controlStart + controlMinimum, centerWidth: 60f, rightWidth: 300f, spacing: 10f);

        var room = EditorActionBarLayout.NameRoom(controlStart, controlMinimum, centerX, spacing: 10f);

        Assert.Equal(470f, centerX);
        Assert.Equal(470f - 10f - 200f, room);
        Assert.True(controlStart + controlMinimum + room <= centerX - 10f);
    }

    [Fact]
    public void OnTheNarrowestRow_ThePlateMenuKeepsItsPlace_WithNoRoomForTheName()
    {
        // Narrower than everything together: the groups keep their order, the history group is
        // pushed right after the control, and the control keeps its minimum width.
        const float controlStart = 160f;
        const float controlMinimum = 40f;
        var (centerX, rightX, _) = EditorActionBarLayout.Arrange(0f, 300f, controlStart + controlMinimum, centerWidth: 60f, rightWidth: 300f, spacing: 10f);

        Assert.Equal(0f, EditorActionBarLayout.NameRoom(controlStart, controlMinimum, centerX, spacing: 10f));
        Assert.Equal(controlStart + controlMinimum + 10f, centerX);
        Assert.True(rightX >= centerX + 60f + 10f);
    }

    [Fact]
    public void TheNameRoom_IsNeverNegative()
    {
        Assert.Equal(0f, EditorActionBarLayout.NameRoom(controlStart: 100f, controlMinimum: 50f, centerX: 120f, spacing: 10f));
    }
}
