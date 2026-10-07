using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The editors' Open another Plate and New Plate (interface task 2) over a real Plate Library, with
/// the editors' frame order: the runner's results, then the switcher (<see cref="Frame"/>).
/// </summary>
internal sealed class SwitcherHarness : IDisposable
{
    private SwitcherHarness(PlateActionsHarness plates, CharacterContext? character)
    {
        Plates = plates;
        Switcher = new PlateSwitcher(
            plates.Library,
            plates.Editor.Profiles,
            plates.Editor.Session,
            plates.Actions,
            () => character,
            () => new PlateStarterContent(null),
            Shown.Add,
            () => Asked++,
            plates.Editor.Fixture.Log);
    }

    internal PlateActionsHarness Plates { get; }

    internal PlateSwitcher Switcher { get; }

    internal PlateOpenGuard Guard => Switcher.Guard;

    internal PlateLibraryService Library => Plates.Library;

    /// <summary>The editor the switcher showed for each Plate it opened, in order.</summary>
    internal List<EditorSurfaceKind> Shown { get; } = new();

    /// <summary>How many times the unsaved-changes question was asked for.</summary>
    internal int Asked { get; private set; }

    /// <summary>The Plate open in the editors when the harness was made (Alice's first, a blank Plate).</summary>
    internal Guid OriginalId => Plates.OpenId;

    internal Guid? OpenId => Plates.Editor.Profiles.OpenPlateId;

    internal bool IsDirty => Plates.Editor.Session.IsDirty;

    /// <param name="store">The Library's store (a <see cref="FaultInjectingStore"/> or <see cref="HeldWriteStore"/>).</param>
    /// <param name="character">The logged-in character when a new Plate is made (Alice, who has a Plate, by default).</param>
    internal static async Task<SwitcherHarness> CreateAsync(IPlateFileStore? store = null, CharacterContext? character = null) =>
        new(await PlateActionsHarness.CreateAsync(store), character ?? Characters.Alice);

    /// <summary>One of the editors' frames: finished actions, then what waits to open, then the editor reads the open Plate.</summary>
    internal void Frame()
    {
        Plates.Runner.Advance();
        Switcher.Advance();
        Plates.Editor.Session.SyncWithCurrentProfile();
    }

    /// <summary>Frames, as the editors draw them, until <paramref name="done"/> holds.</summary>
    internal async Task FramesUntilAsync(Func<bool> done)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            await Plates.Runner.Finished;
            Frame();
            if (done())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("It never got there.");
    }

    /// <summary>Frames until no action and no save are running.</summary>
    internal Task SettleAsync() => FramesUntilAsync(() => !Plates.Runner.IsBusy && !Guard.IsSaving && !Plates.Editor.Profiles.IsBusy);

    /// <summary>How many Plate files the Library folder holds.</summary>
    internal int PlateFiles() => Directory.GetFiles(Path.GetDirectoryName(Plates.Editor.Fixture.Paths.GetPlatePath(OriginalId))!).Length;

    public void Dispose() => Plates.Dispose();
}

/// <summary>
/// Create Plate's chooser is one component, which My Plates and the editors both draw (interface
/// task 2). It is ImGui code, not built here, so this reads the sources.
/// </summary>
public class TemplateChooserSourceTests
{
    [Fact]
    public void TheChooser_IsDeclaredOnce_AndDrawnByTheSharedPlateMenu_ForBothWindows()
    {
        var windows = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Windows");
        var sources = Directory.GetFiles(windows, "*.cs", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(windows, file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText);

        // One chooser: its popup and its prompts are declared by TemplateChooser alone.
        foreach (var id in new[] { "##AetherFrameTemplateChooser", "##AetherFrameTemplateRename", "##AetherFrameTemplateDelete" })
        {
            Assert.Equal(["TemplateChooser.cs"], sources.Where(source => source.Value.Contains(id, StringComparison.Ordinal)).Select(source => source.Key));
        }

        // The shared Plate menu draws it with its prompts, and both windows draw the menu's prompts.
        Assert.Contains("Chooser.Draw();", sources["PlateMenu.cs"], StringComparison.Ordinal);
        Assert.Contains("plateMenu.DrawPopups(", sources["PlateLibraryWindow.cs"], StringComparison.Ordinal);
        Assert.Contains("menu.DrawPopups(", sources["EditorPlateMenu.cs"], StringComparison.Ordinal);

        // Interface task 7's rules moved with it: a row menu's Use Template asks the chooser to act,
        // so the chooser closes as for its button, and Escape is the chooser's Cancel.
        var chooser = sources["TemplateChooser.cs"];
        Assert.Contains("chooserUseRequestedId = templateId;", chooser, StringComparison.Ordinal);
        Assert.Contains("PopupEscapeGuard.CancelsPrompt()", chooser, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEditorsMenu_GoesThroughTheSwitcher_EveryFrame_AndUseTemplateIsGreyedOutForATemplateThatCantBeUsed()
    {
        var root = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame");
        var menu = File.ReadAllText(Path.Combine(root, "Windows", "PlateMenu.cs"));
        var editorMenu = File.ReadAllText(Path.Combine(root, "Windows", "EditorPlateMenu.cs"));
        var chooser = File.ReadAllText(Path.Combine(root, "Windows", "TemplateChooser.cs"));

        // The editors' menu has a switcher: its chooser's Use Template is the switcher's New Plate,
        // and its question's Discard is the switcher's, which checks a new Plate's Template again.
        var plugin = File.ReadAllText(Path.Combine(root, "Plugin.cs"));
        Assert.Contains("var plateSwitcher = new PlateSwitcher(", plugin, StringComparison.Ordinal);
        Assert.Contains("editorPlates.AttachSwitcher(plateSwitcher);", plugin, StringComparison.Ordinal);
        Assert.Contains("Chooser.Use = templateId => plates.New(templateId);", menu, StringComparison.Ordinal);
        Assert.Contains("(switcher is { } editorPlates ? editorPlates.Discard() : guard.Discard())", menu, StringComparison.Ordinal);

        // Every frame advances it, whether an editor drew (DrawFrame) or not (EndFrame).
        var drawFrame = editorMenu.IndexOf("internal void DrawFrame()", StringComparison.Ordinal);
        var endFrame = editorMenu.IndexOf("internal void EndFrame()", StringComparison.Ordinal);
        var afterEndFrame = editorMenu.IndexOf("/// <summary>", endFrame, StringComparison.Ordinal);
        Assert.True(drawFrame >= 0 && endFrame > drawFrame && afterEndFrame > endFrame);
        Assert.Contains("menu.AdvanceOpenGuard();", editorMenu[drawFrame..endFrame], StringComparison.Ordinal);
        Assert.Contains("menu.AdvanceOpenGuard();", editorMenu[endFrame..afterEndFrame], StringComparison.Ordinal);

        // Use Template can't be chosen for a Template that can't be used, or while an action runs:
        // its button and a row's menu item are greyed out, with the reason as their tooltip, and a
        // double-click does nothing.
        Assert.Contains("var problem = actions.TemplateProblem(chosenTemplateId);", chooser, StringComparison.Ordinal);
        Assert.Contains("var problem = actions.TemplateProblem(templateId);", chooser, StringComparison.Ordinal);
        Assert.Equal(2, chooser.Split("ImRaii.Disabled(IsBusy || problem is not null)").Length - 1);
        Assert.Equal(2, chooser.Split("EditorWidgets.Tooltip(problem);").Length - 1);
        Assert.Contains("IsMouseDoubleClicked(ImGuiMouseButton.Left) && !IsBusy && CanUse(templateId)", chooser, StringComparison.Ordinal);
        Assert.Contains("private bool CanUse(Guid templateId) => actions.TemplateProblem(templateId) is null;", chooser, StringComparison.Ordinal);
    }
}

public class PlateSwitcherTests
{
    private static readonly Guid Classic = BuiltInTemplateCatalog.AdventurePlateClassicId;
    private static readonly Guid BlankCanvas = BuiltInTemplateCatalog.BlankCanvasId;

    // ---------------------------------------------------------------- Open another Plate: the list

    [Fact]
    public async Task TheList_IsMyPlatesOwnOrder_EvenAfterAReorder_AndMyPlatesSearch()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var evening = await harness.Plates.AnotherPlateAsync();
        var morning = (await harness.Library.CreatePlateAsync(PlateStartingLayout.Blank, character: null, name: "Morning Look")).PlateId;
        var market = (await harness.Library.CreatePlateAsync(PlateStartingLayout.Blank, character: null, name: "Market Day")).PlateId;

        // Not by name and not by when it was made: My Plates' order, which the player sets by dragging.
        await harness.Library.MovePlateAsync(harness.OriginalId, market, placeAfter: false);
        await harness.Library.MovePlateAsync(evening, market, placeAfter: false);
        var myPlates = harness.Library.GetOrderedPlates().Select(p => p.PlateId).ToList();
        Assert.Equal([harness.OriginalId, evening, market, morning], myPlates);
        Assert.Equal(myPlates, harness.Switcher.Plates(null).Select(p => p.PlateId));
        Assert.Equal(myPlates, harness.Switcher.Plates("   ").Select(p => p.PlateId));

        // A search keeps that order, and finds what My Plates' search finds.
        var looks = harness.Switcher.Plates("look").Select(p => p.PlateId).ToList();
        Assert.Equal(myPlates.Where(id => id == evening || id == morning), looks);
        Assert.Equal(harness.Library.Search("look").Select(p => p.PlateId), looks);
        Assert.Empty(harness.Switcher.Plates("nothing like this"));
    }

    [Fact]
    public async Task ThePlateBeingEdited_IsListed_ButNotOffered_NorIsAPlateThatCantOpen()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var other = await harness.Plates.AnotherPlateAsync();

        Assert.Equal(PlateSwitcher.EditingNote, harness.Switcher.WhyNotOpen(harness.Library.FindPlate(harness.OriginalId)!));
        Assert.Null(harness.Switcher.WhyNotOpen(harness.Library.FindPlate(other)!));

        var unreadable = harness.Library.FindPlate(other)! with { Status = PlateStatus.Unreadable, Problem = "This Plate couldn't be read." };
        Assert.Equal("This Plate couldn't be read.", harness.Switcher.WhyNotOpen(unreadable));
        Assert.Equal(PlateSwitcher.CannotOpenNote, harness.Switcher.WhyNotOpen(unreadable with { Problem = null }));
    }

    [Fact]
    public async Task WithOnlyTheOpenPlate_ThereIsNoOtherToOpen()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        Assert.False(harness.Switcher.HasAnotherPlate);

        await harness.Plates.AnotherPlateAsync();
        Assert.True(harness.Switcher.HasAnotherPlate);
    }

    [Fact]
    public async Task ALongList_GetsASearchField_PastTheRowsItShows()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        while (harness.Library.GetOrderedPlates().Count < PlateSwitcher.VisibleRows)
        {
            await harness.Plates.AnotherPlateAsync();
        }

        Assert.False(harness.Switcher.NeedsSearch);

        await harness.Plates.AnotherPlateAsync();
        Assert.True(harness.Switcher.NeedsSearch);
    }

    // ---------------------------------------------------------------- Open another Plate: switching

    [Fact]
    public async Task WithNothingUnsaved_AnotherPlateOpens_AtTheStartOfTheNextFrame_InTheEditorItsContentSuits()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var classic = (await harness.Library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, character: null, name: "Classic", starter: new PlateStarterContent(null))).PlateId;
        var blank = await harness.Plates.AnotherPlateAsync();

        // Never while the editor is still drawing the Plate it replaces.
        Assert.Equal(PlateOpenDecision.Open, harness.Switcher.Open(classic));
        Assert.Equal(harness.OriginalId, harness.OpenId);
        Assert.Empty(harness.Shown);

        harness.Frame();
        Assert.Equal(classic, harness.OpenId);
        Assert.Equal([EditorSurfaceKind.Basic], harness.Shown);
        Assert.Equal(0, harness.Asked);

        // A freeform Plate opens in the Advanced Editor, as a double-click on its card does.
        harness.Switcher.Open(blank);
        harness.Frame();
        Assert.Equal(blank, harness.OpenId);
        Assert.Equal([EditorSurfaceKind.Basic, EditorSurfaceKind.Advanced], harness.Shown);
    }

    [Fact]
    public async Task OpeningThePlateBeingEdited_DoesNothing()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        harness.Plates.Edit();

        Assert.Equal(PlateOpenDecision.AlreadyOpen, harness.Switcher.Open(harness.OriginalId));
        harness.Frame();

        Assert.Empty(harness.Shown);
        Assert.Equal(0, harness.Asked);
        Assert.True(harness.IsDirty);
    }

    [Fact]
    public async Task WithUnsavedChanges_ItAsksFirst_AndCancelKeepsThemAndThePlate()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var other = await harness.Plates.AnotherPlateAsync();
        harness.Plates.Edit();

        Assert.Equal(PlateOpenDecision.Ask, harness.Switcher.Open(other));
        Assert.Equal(1, harness.Asked);
        Assert.Equal(new PlateOpenRequest(other, false), harness.Guard.Pending);

        harness.Guard.Cancel();
        harness.Frame();

        Assert.Equal(harness.OriginalId, harness.OpenId);
        Assert.True(harness.IsDirty);
        Assert.Empty(harness.Shown);
    }

    [Fact]
    public async Task Discard_DropsTheChanges_ThenTheOtherPlateOpens()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var other = await harness.Plates.AnotherPlateAsync();
        var savedBefore = File.ReadAllText(harness.Plates.Editor.Fixture.Paths.GetPlatePath(harness.OriginalId));
        harness.Plates.Edit();
        harness.Switcher.Open(other);

        // As the question's Discard does.
        harness.Switcher.Proceed(harness.Switcher.Discard()!.Value);
        harness.Frame();

        Assert.Equal(other, harness.OpenId);
        Assert.Equal([EditorSurfaceKind.Advanced], harness.Shown);
        Assert.Equal(savedBefore, File.ReadAllText(harness.Plates.Editor.Fixture.Paths.GetPlatePath(harness.OriginalId)));
    }

    [Fact]
    public async Task Save_SavesTheChanges_ThenTheOtherPlateOpens()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var other = await harness.Plates.AnotherPlateAsync();
        harness.Plates.Edit();
        harness.Switcher.Open(other);

        harness.Guard.Save();
        await harness.FramesUntilAsync(() => harness.Shown.Count > 0);

        Assert.Equal(other, harness.OpenId);
        Assert.Contains(harness.Library.GetSavedDocument(harness.OriginalId)!.Elements, e => e is TextProfileElement { Text: "An unsaved line" });
    }

    [Fact]
    public async Task ASaveThatFails_OpensNothing_AndSaysWhy()
    {
        var store = new FaultInjectingStore();
        using var harness = await SwitcherHarness.CreateAsync(store);
        var other = await harness.Plates.AnotherPlateAsync();
        harness.Plates.Edit();
        harness.Switcher.Open(other);
        store.FailWrite = path => path.Contains(harness.OriginalId.ToString(), StringComparison.OrdinalIgnoreCase);

        harness.Guard.Save();
        await harness.SettleAsync();

        Assert.Equal(harness.OriginalId, harness.OpenId);
        Assert.True(harness.IsDirty);
        Assert.Empty(harness.Shown);
        Assert.NotNull(harness.Plates.Runner.Error);
    }

    [Fact]
    public async Task APlateThatCantBeOpened_LeavesTheOpenOne_AndSaysWhy()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var other = await harness.Plates.AnotherPlateAsync();
        harness.Switcher.Open(other);

        // Deleted (from My Plates, say) before the next frame opens it.
        await harness.Library.DeletePlateAsync(other);
        harness.Frame();

        Assert.Equal(harness.OriginalId, harness.OpenId);
        Assert.Empty(harness.Shown);
        Assert.NotNull(harness.Plates.Runner.Error);
    }

    // ---------------------------------------------------------------- New Plate

    [Fact]
    public async Task NewPlate_WithNothingUnsaved_IsMadeFromTheTemplate_AndOpens()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var before = harness.Library.GetOrderedPlates().Count;

        Assert.Equal(PlateOpenDecision.Open, harness.Switcher.New(Classic));
        await harness.FramesUntilAsync(() => harness.Shown.Count > 0);

        var made = harness.OpenId!.Value;
        Assert.NotEqual(harness.OriginalId, made);
        Assert.Equal(before + 1, harness.Library.GetOrderedPlates().Count);
        Assert.Equal(made, harness.Library.GetOrderedPlates()[0].PlateId);
        Assert.Equal("Adventure Plate Classic", harness.Library.FindPlate(made)!.DisplayName);
        Assert.Equal([EditorSurfaceKind.Basic], harness.Shown);
        Assert.Equal(0, harness.Asked);
        Assert.False(harness.IsDirty);
    }

    [Fact]
    public async Task NewPlate_OpensInTheEditorItsContentSuits()
    {
        using var harness = await SwitcherHarness.CreateAsync();

        harness.Switcher.New(BlankCanvas);
        await harness.FramesUntilAsync(() => harness.Shown.Count > 0);

        Assert.Equal([EditorSurfaceKind.Advanced], harness.Shown);
        Assert.Equal("Blank Canvas", harness.Library.FindPlate(harness.OpenId!.Value)!.DisplayName);
    }

    [Fact]
    public async Task NewPlate_WithUnsavedChanges_AsksBeforeAnythingIsWritten()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        harness.Plates.Edit();
        var plates = harness.Library.GetOrderedPlates().Count;
        var files = harness.PlateFiles();

        Assert.Equal(PlateOpenDecision.Ask, harness.Switcher.New(Classic));

        Assert.Equal(1, harness.Asked);
        Assert.Equal(Classic, harness.Guard.Pending!.Value.TemplateId);
        Assert.False(harness.Plates.Runner.IsBusy);
        harness.Frame();
        Assert.Equal(plates, harness.Library.GetOrderedPlates().Count);
        Assert.Equal(files, harness.PlateFiles());
    }

    [Fact]
    public async Task NewPlate_Cancel_LeavesNoPlateBehind_AndKeepsTheChanges()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        harness.Plates.Edit();
        var plates = harness.Library.GetOrderedPlates().Select(p => p.PlateId).ToList();
        var files = harness.PlateFiles();
        harness.Switcher.New(Classic);

        harness.Guard.Cancel();
        harness.Frame();
        harness.Frame();

        Assert.Null(harness.Guard.Pending);
        Assert.Equal(plates, harness.Library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal(files, harness.PlateFiles());
        Assert.Equal(harness.OriginalId, harness.OpenId);
        Assert.True(harness.IsDirty);
        Assert.Empty(harness.Shown);
    }

    [Fact]
    public async Task NewPlate_Discard_DropsTheChanges_ThenMakesThePlate_AndOpensIt()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var savedBefore = File.ReadAllText(harness.Plates.Editor.Fixture.Paths.GetPlatePath(harness.OriginalId));
        harness.Plates.Edit();
        harness.Switcher.New(Classic);

        harness.Switcher.Proceed(harness.Switcher.Discard()!.Value);
        await harness.FramesUntilAsync(() => harness.Shown.Count > 0);

        Assert.NotEqual(harness.OriginalId, harness.OpenId);
        Assert.Equal("Adventure Plate Classic", harness.Library.FindPlate(harness.OpenId!.Value)!.DisplayName);
        Assert.Equal(savedBefore, File.ReadAllText(harness.Plates.Editor.Fixture.Paths.GetPlatePath(harness.OriginalId)));
        Assert.Equal(1, harness.Asked);
    }

    [Fact]
    public async Task NewPlate_Save_SavesFirst_ThenMakesThePlate_AndOpensIt()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var plates = harness.Library.GetOrderedPlates().Count;
        harness.Plates.Edit();
        harness.Switcher.New(Classic);

        harness.Guard.Save();
        await harness.FramesUntilAsync(() => harness.Shown.Count > 0);

        Assert.Contains(harness.Library.GetSavedDocument(harness.OriginalId)!.Elements, e => e is TextProfileElement { Text: "An unsaved line" });
        Assert.Equal(plates + 1, harness.Library.GetOrderedPlates().Count);
        Assert.NotEqual(harness.OriginalId, harness.OpenId);
        Assert.False(harness.IsDirty);
    }

    [Fact]
    public async Task NewPlate_WhenTheSaveFails_NothingIsMade()
    {
        var store = new FaultInjectingStore();
        using var harness = await SwitcherHarness.CreateAsync(store);
        harness.Plates.Edit();
        var plates = harness.Library.GetOrderedPlates().Count;
        harness.Switcher.New(Classic);
        store.FailWrite = path => path.Contains(harness.OriginalId.ToString(), StringComparison.OrdinalIgnoreCase);

        harness.Guard.Save();
        await harness.SettleAsync();

        Assert.Equal(plates, harness.Library.GetOrderedPlates().Count);
        Assert.Equal(harness.OriginalId, harness.OpenId);
        Assert.True(harness.IsDirty);
        Assert.NotNull(harness.Plates.Runner.Error);
        Assert.Empty(harness.Shown);
    }

    [Fact]
    public async Task NewPlate_FromATemplateThatCantBeUsed_IsRefused_BeforeTheQuestion_SoNoEditIsLost()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var future = await harness.Plates.TemplateFromTheFutureAsync();
        harness.Plates.Edit();
        var plates = harness.Library.GetOrderedPlates().Select(p => p.PlateId).ToList();
        var files = harness.PlateFiles();

        Assert.Equal(PlateOpenDecision.Refused, harness.Switcher.New(future));
        await harness.SettleAsync();

        Assert.Equal(0, harness.Asked);
        Assert.Null(harness.Guard.Pending);
        Assert.True(harness.IsDirty);
        Assert.Equal(harness.OriginalId, harness.OpenId);
        Assert.Equal(harness.Plates.Templates.FindTemplate(future)!.Problem, harness.Plates.Runner.Error);
        Assert.Equal(plates, harness.Library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal(files, harness.PlateFiles());
        Assert.Empty(harness.Shown);
    }

    [Fact]
    public async Task NewPlate_FromATemplateNoLongerThere_IsRefused_AndSaysSo()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        harness.Plates.Edit();
        var plates = harness.Library.GetOrderedPlates().Count;

        Assert.Equal(PlateOpenDecision.Refused, harness.Switcher.New(Guid.NewGuid()));
        await harness.SettleAsync();

        Assert.Equal(0, harness.Asked);
        Assert.True(harness.IsDirty);
        Assert.Equal(PlateActions.TemplateGoneNote, harness.Plates.Runner.Error);
        Assert.Equal(plates, harness.Library.GetOrderedPlates().Count);
        Assert.Empty(harness.Shown);
    }

    [Fact]
    public async Task NewPlate_AfterARefusal_WithNothingUnsaved_ClearsWhyAndWorks()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var future = await harness.Plates.TemplateFromTheFutureAsync();
        var plates = harness.Library.GetOrderedPlates().Count;

        Assert.Equal(PlateOpenDecision.Refused, harness.Switcher.New(future));
        Assert.False(harness.Plates.Runner.IsBusy);
        Assert.NotNull(harness.Plates.Runner.Error);

        Assert.Equal(PlateOpenDecision.Open, harness.Switcher.New(Classic));
        await harness.FramesUntilAsync(() => harness.Shown.Count > 0);

        Assert.Null(harness.Plates.Runner.Error);
        Assert.Equal(plates + 1, harness.Library.GetOrderedPlates().Count);
        Assert.NotEqual(harness.OriginalId, harness.OpenId);
    }

    [Fact]
    public async Task NewPlate_WhileAnotherActionRuns_IsRefused_AndAsksNothing()
    {
        var store = new HeldWriteStore();
        using var harness = await SwitcherHarness.CreateAsync(store);
        harness.Plates.Edit();
        store.Hold();
        harness.Plates.Actions.UseTemplate(BlankCanvas, null, new PlateStarterContent(null), _ => { });
        await store.WriteStarted;

        Assert.Equal(PlateOpenDecision.Refused, harness.Switcher.New(Classic));

        Assert.Equal(0, harness.Asked);
        Assert.Null(harness.Guard.Pending);
        Assert.True(harness.IsDirty);
        Assert.Equal(PlateOperationRunner.BusyMessage, harness.Plates.Runner.Error);
        store.Release();
        await harness.SettleAsync();
        Assert.Equal(harness.OriginalId, harness.OpenId);
        Assert.True(harness.IsDirty);
        Assert.Empty(harness.Shown);
    }

    [Fact]
    public async Task NewPlate_Discard_WhenTheTemplateWasDeletedMeanwhile_KeepsTheChanges_AndSaysWhy()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var saved = await harness.Plates.Templates.SaveAsTemplateAsync(harness.OriginalId, "Night Out");
        harness.Plates.Edit();
        var plates = harness.Library.GetOrderedPlates().Count;
        Assert.Equal(PlateOpenDecision.Ask, harness.Switcher.New(saved));

        await harness.Plates.Templates.DeleteTemplateAsync(saved);
        Assert.Null(harness.Switcher.Discard());
        await harness.SettleAsync();

        Assert.Null(harness.Guard.Pending);
        Assert.True(harness.IsDirty);
        Assert.Equal(PlateActions.TemplateGoneNote, harness.Plates.Runner.Error);
        Assert.Equal(plates, harness.Library.GetOrderedPlates().Count);
        Assert.Equal(harness.OriginalId, harness.OpenId);
        Assert.Empty(harness.Shown);
    }

    [Fact]
    public async Task NewPlate_Discard_WhileAnotherActionRuns_KeepsTheChanges_AndSaysWhy()
    {
        var store = new HeldWriteStore();
        using var harness = await SwitcherHarness.CreateAsync(store);
        harness.Plates.Edit();
        Assert.Equal(PlateOpenDecision.Ask, harness.Switcher.New(Classic));
        store.Hold();
        harness.Plates.Actions.UseTemplate(BlankCanvas, null, new PlateStarterContent(null), _ => { });
        await store.WriteStarted;

        Assert.Null(harness.Switcher.Discard());

        Assert.Null(harness.Guard.Pending);
        Assert.True(harness.IsDirty);
        Assert.Equal(PlateOperationRunner.BusyMessage, harness.Plates.Runner.Error);
        store.Release();
        await harness.SettleAsync();
        Assert.Equal(harness.OriginalId, harness.OpenId);
        Assert.True(harness.IsDirty);
        Assert.Empty(harness.Shown);
    }

    [Fact]
    public async Task NewPlate_AnEditMadeWhileThePlateIsMade_IsAskedAbout_NeverDropped()
    {
        var store = new HeldWriteStore();
        using var harness = await SwitcherHarness.CreateAsync(store);
        store.Hold();

        Assert.Equal(PlateOpenDecision.Open, harness.Switcher.New(Classic));
        await store.WriteStarted;
        harness.Plates.Edit();
        store.Release();
        await harness.SettleAsync();

        // The new Plate is in My Plates; the question asks about the edit before it opens.
        var made = harness.Library.GetOrderedPlates()[0].PlateId;
        Assert.NotEqual(harness.OriginalId, made);
        Assert.Equal(1, harness.Asked);
        Assert.Equal(new PlateOpenRequest(made, true), harness.Guard.Pending);
        Assert.Equal(harness.OriginalId, harness.OpenId);
        Assert.True(harness.IsDirty);
        Assert.Empty(harness.Shown);

        // Answering it opens the new Plate.
        harness.Switcher.Proceed(harness.Switcher.Discard()!.Value);
        harness.Frame();
        Assert.Equal(made, harness.OpenId);
    }

    [Fact]
    public async Task NewPlate_ForACharactersFirstPlate_MakesItActive_AndSaysSo()
    {
        using var harness = await SwitcherHarness.CreateAsync(character: Characters.Bob);

        harness.Switcher.New(Classic);
        await harness.FramesUntilAsync(() => harness.Shown.Count > 0);

        Assert.Equal(harness.OpenId, harness.Library.GetActivePlateId(Characters.Bob.ContentId));
        Assert.Equal(MyPlatesCharacterText.FirstPlateCreated, harness.Plates.Runner.Status);
    }

    [Fact]
    public async Task NewPlate_ForACharacterWithPlates_LeavesItsActivePlate()
    {
        using var harness = await SwitcherHarness.CreateAsync();

        harness.Switcher.New(Classic);
        await harness.FramesUntilAsync(() => harness.Shown.Count > 0);

        Assert.Equal(harness.OriginalId, harness.Library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Contains(harness.OpenId!.Value, harness.Library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Null(harness.Plates.Runner.Status);
    }

    // ---------------------------------------------------------------- My Plates' order, until interface task 10

    [Fact]
    public async Task InMyPlates_UseTemplateStillMakesThePlateFirst_ThenAsks()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        harness.Edit();
        var before = harness.Library.GetOrderedPlates().Count;
        Guid? made = null;

        // My Plates' Use Template: the Plate is made, then opening it asks (Cancel leaves it in My
        // Plates, which interface task 10 changes); the editors' New Plate asks first (above).
        harness.Actions.UseTemplate(Classic, Characters.Alice, new PlateStarterContent(null), result => made = result.PlateId);
        await harness.FinishAsync();
        Assert.Equal(before + 1, harness.Library.GetOrderedPlates().Count);

        Assert.Equal(PlateOpenDecision.Ask, harness.Guard.Request(made!.Value, basic: true));
        harness.Guard.Cancel();
        Assert.NotNull(harness.Library.FindPlate(made.Value));
        Assert.True(harness.Editor.Session.IsDirty);
    }
}
