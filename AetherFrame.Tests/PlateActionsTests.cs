using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A real Plate Library (in a temporary folder) with one Plate open in the editors, and the Plate
/// actions My Plates' card menu and the editors' Plate menu share, over one window's runner.
/// </summary>
internal sealed class PlateActionsHarness : IDisposable
{
    private PlateActionsHarness(BasicHarness editor, TemplateLibraryService templates, PlatePackageService packages)
    {
        Editor = editor;
        Templates = templates;
        Packages = packages;
        Runner = new PlateOperationRunner(editor.Fixture.Log);
        Actions = new PlateActions(editor.Library, templates, packages, editor.Profiles, editor.Session, Runner, editor.Fixture.Log);
        Guard = new PlateOpenGuard(editor.Profiles, editor.Session);
    }

    internal BasicHarness Editor { get; }

    internal PlateLibraryService Library => Editor.Library;

    internal TemplateLibraryService Templates { get; }

    internal PlatePackageService Packages { get; }

    internal PlateOperationRunner Runner { get; }

    internal PlateActions Actions { get; }

    internal PlateOpenGuard Guard { get; }

    /// <summary>The Plate open in the editors (a blank Plate, Alice's first and so her Active Plate).</summary>
    internal Guid OpenId => Editor.PlateId;

    /// <param name="store">The Library's store (a <see cref="FaultInjectingStore"/> to make writes fail).</param>
    internal static async Task<PlateActionsHarness> CreateAsync(IPlateFileStore? store = null)
    {
        var editor = await BasicHarness.CreatePlateAsync(PlateStartingLayout.Blank, starter: null, character: Characters.Alice, store);
        var templates = new TemplateLibraryService(editor.Fixture.Paths, editor.Fixture.Store, editor.Library, editor.Fixture.Log, () => editor.Fixture.Clock.Now);
        await templates.InitializeAsync();
        var packages = new PlatePackageService(editor.Library, editor.Assets, editor.Fixture.Paths, "AetherFrame Tests", operations: new OwnedOperations());
        return new PlateActionsHarness(editor, templates, packages);
    }

    /// <summary>Another saved Plate, not open.</summary>
    internal async Task<Guid> AnotherPlateAsync() => (await Library.CreatePlateAsync(PlateStartingLayout.Blank, character: null, name: "Second Look")).PlateId;

    /// <summary>Waits for the running action, then applies its outcome as the window's next frame would.</summary>
    internal async Task FinishAsync()
    {
        await Runner.Finished;
        Runner.Advance();
    }

    /// <summary>An unsaved change to the open Plate.</summary>
    internal void Edit() => Editor.Profiles.AddTextElement("An unsaved line");

    public void Dispose() => Editor.Dispose();
}

/// <summary>
/// The unsaved-changes question before another Plate opens, moved out of My Plates into the
/// shared component (interface task 1) with the rules it had there.
/// </summary>
public class PlateOpenGuardTests
{
    [Fact]
    public async Task OpeningTheOpenPlate_OnlyShowsIt()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        harness.Edit();

        Assert.Equal(PlateOpenDecision.AlreadyOpen, harness.Guard.Request(harness.OpenId, basic: true));
        Assert.Null(harness.Guard.Pending);
    }

    [Fact]
    public async Task WithNothingUnsaved_TheOtherPlateOpensAtOnce()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var other = await harness.AnotherPlateAsync();

        Assert.Equal(PlateOpenDecision.Open, harness.Guard.Request(other, basic: false));
        Assert.Null(harness.Guard.Pending);
    }

    [Fact]
    public async Task WithUnsavedChanges_ItAsksFirst()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var other = await harness.AnotherPlateAsync();
        harness.Edit();

        Assert.Equal(PlateOpenDecision.Ask, harness.Guard.Request(other, basic: false));
        Assert.Equal(new PlateOpenRequest(other, false), harness.Guard.Pending);
        Assert.True(harness.Guard.CanAnswer);
    }

    [Fact]
    public async Task Save_OpensTheOtherPlate_OnceTheSaveSucceeds()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var other = await harness.AnotherPlateAsync();
        harness.Edit();
        harness.Guard.Request(other, basic: true);

        harness.Guard.Save();
        Assert.True(harness.Guard.IsSaving);
        Assert.False(harness.Guard.CanAnswer);

        var outcome = await AdvanceUntilAnsweredAsync(harness.Guard);
        Assert.Equal(new PlateOpenRequest(other, true), outcome.Open);
        Assert.Null(outcome.Error);
        Assert.Null(harness.Guard.Pending);
        Assert.Contains(harness.Library.GetSavedDocument(harness.OpenId)!.Elements, e => e is TextProfileElement { Text: "An unsaved line" });
    }

    [Fact]
    public async Task ASaveThatFails_KeepsThePlateOpen_WithTheReason()
    {
        var store = new FaultInjectingStore();
        using var harness = await PlateActionsHarness.CreateAsync(store);
        var other = await harness.AnotherPlateAsync();
        harness.Edit();
        harness.Guard.Request(other, basic: true);

        store.FailWrite = path => path.Contains(harness.OpenId.ToString(), StringComparison.OrdinalIgnoreCase);
        harness.Guard.Save();

        var outcome = await AdvanceUntilAnsweredAsync(harness.Guard);
        Assert.Null(outcome.Open);
        Assert.NotNull(outcome.Error);
        Assert.Equal(harness.Editor.Session.ErrorMessage ?? PlateOpenGuard.SaveFailedMessage, outcome.Error);
        Assert.Equal(harness.OpenId, harness.Editor.Profiles.OpenPlateId);
        Assert.True(harness.Editor.Session.IsDirty);
    }

    [Fact]
    public async Task Discard_DropsTheChanges_AndGivesTheOpen()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var other = await harness.AnotherPlateAsync();
        harness.Edit();
        harness.Guard.Request(other, basic: false);

        Assert.Equal(new PlateOpenRequest(other, false), harness.Guard.Discard());
        Assert.Null(harness.Guard.Pending);
        Assert.False(harness.Editor.Session.IsDirty);
    }

    [Fact]
    public async Task Cancel_KeepsTheChanges_AndOpensNothing()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var other = await harness.AnotherPlateAsync();
        harness.Edit();
        harness.Guard.Request(other, basic: false);

        harness.Guard.Cancel();

        Assert.Null(harness.Guard.Pending);
        Assert.True(harness.Editor.Session.IsDirty);
        Assert.Equal(harness.OpenId, harness.Editor.Profiles.OpenPlateId);
    }

    [Fact]
    public async Task WithNothingAsked_SaveAndDiscardDoNothing()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        harness.Edit();

        harness.Guard.Save();
        Assert.False(harness.Guard.IsSaving);
        Assert.Null(harness.Guard.Discard());
        Assert.True(harness.Editor.Session.IsDirty);
        Assert.Null(harness.Guard.Advance());
    }

    private static async Task<(PlateOpenRequest? Open, string? Error)> AdvanceUntilAnsweredAsync(PlateOpenGuard guard)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            if (guard.Advance() is { } outcome)
            {
                return outcome;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("The save never finished.");
    }
}

/// <summary>
/// The Plate actions My Plates' card menu and the editors' Plate menu share (interface task 1):
/// the same results and messages My Plates showed before, and the editors' Save as New Plate.
/// </summary>
public class PlateActionsTests
{
    [Fact]
    public void SetActive_SaysWhyItIsUnavailable()
    {
        Assert.Equal("Log in to a character to choose its Active Plate.", PlateActions.SetActiveUnavailableReason(null, isActive: false));
        Assert.Equal(MyPlatesCharacterText.SetActiveNeedsCharacter, PlateActions.SetActiveUnavailableReason(null, isActive: true));
        Assert.Equal("This is already your current character's Active Plate.", PlateActions.SetActiveUnavailableReason(Characters.Alice, isActive: true));
        Assert.Null(PlateActions.SetActiveUnavailableReason(Characters.Alice, isActive: false));
    }

    [Fact]
    public async Task SetActive_MakesThePlateActive_AndSaysSo()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var other = await harness.AnotherPlateAsync();

        harness.Actions.SetActive(Characters.Alice, other, "Second Look");
        await harness.FinishAsync();

        Assert.Equal(other, harness.Library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal("\"Second Look\" is now your current character's Active Plate.", harness.Runner.Status);
        Assert.Null(harness.Runner.Error);
    }

    [Fact]
    public async Task Rename_RefusesAnInvalidName_WithoutStartingAnything()
    {
        using var harness = await PlateActionsHarness.CreateAsync();

        var error = harness.Actions.Rename(harness.OpenId, "   ");

        Assert.NotNull(error);
        Assert.False(harness.Runner.IsBusy);
    }

    [Fact]
    public async Task Rename_RenamesTheOpenPlate_WhichTakesTheNameAndStaysSaved()
    {
        using var harness = await PlateActionsHarness.CreateAsync();

        Assert.Null(harness.Actions.Rename(harness.OpenId, "  Evening Look  "));
        await harness.FinishAsync();

        Assert.Equal("Evening Look", harness.Library.FindPlate(harness.OpenId)!.DisplayName);
        Assert.Equal("Evening Look", harness.Editor.Profiles.CurrentProfile!.Name);
        Assert.False(harness.Editor.Session.IsDirty);
        Assert.Null(harness.Runner.Error);
    }

    [Fact]
    public async Task Rename_WithUnsavedChanges_KeepsThemAndTheNewName()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        harness.Edit();

        harness.Actions.Rename(harness.OpenId, "Evening Look");
        await harness.FinishAsync();
        Assert.True(harness.Editor.Session.IsDirty);

        Assert.True(await harness.Editor.Session.SaveProfileAsync());
        var saved = harness.Library.GetSavedDocument(harness.OpenId)!;
        Assert.Equal("Evening Look", saved.Name);
        Assert.Contains(saved.Elements, e => e is TextProfileElement { Text: "An unsaved line" });
    }

    [Fact]
    public async Task Duplicate_CopiesTheSavedPlate_AndHandsBackTheCopy()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        harness.Edit();
        Guid? copy = null;

        harness.Actions.Duplicate(harness.OpenId, id => copy = id);
        await harness.FinishAsync();

        Assert.NotNull(copy);
        var copied = harness.Library.GetSavedDocument(copy!.Value)!;
        Assert.DoesNotContain(copied.Elements, e => e is TextProfileElement { Text: "An unsaved line" });
        Assert.EndsWith(" Copy", harness.Library.FindPlate(copy.Value)!.DisplayName);
    }

    [Fact]
    public async Task SaveAsTemplate_RefusesAnInvalidName_WithoutStartingAnything()
    {
        using var harness = await PlateActionsHarness.CreateAsync();

        Assert.NotNull(harness.Actions.SaveAsTemplate(harness.OpenId, string.Empty));
        Assert.False(harness.Runner.IsBusy);
    }

    [Fact]
    public async Task SaveAsTemplate_SavesTheTemplate_AndSaysSo()
    {
        using var harness = await PlateActionsHarness.CreateAsync();

        Assert.Null(harness.Actions.SaveAsTemplate(harness.OpenId, " Night Out "));
        await harness.FinishAsync();

        Assert.Contains(harness.Templates.GetOrderedTemplates(), t => t.DisplayName == "Night Out");
        Assert.Equal("Saved \"Night Out\" as a Template.", harness.Runner.Status);
    }

    [Fact]
    public void ExportPath_AddsTheExtension_OnlyWhenItIsMissing()
    {
        Assert.Equal("Look" + PackagePolicy.FileExtension, PlateActions.ExportPath("Look"));
        Assert.Equal("Look" + PackagePolicy.FileExtension, PlateActions.ExportPath("Look" + PackagePolicy.FileExtension));
        Assert.Equal("Look" + PackagePolicy.FileExtension.ToUpperInvariant(), PlateActions.ExportPath("Look" + PackagePolicy.FileExtension.ToUpperInvariant()));
    }

    [Fact]
    public async Task Export_WritesTheFile_AndNamesIt_OrReportsWhyNot()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var path = Path.Combine(harness.Editor.Fixture.Root, "Out" + PackagePolicy.FileExtension);

        harness.Actions.Export(harness.OpenId, "My Look", path, overwrite: false, previewPath: null);
        await harness.FinishAsync();

        Assert.True(File.Exists(path));
        Assert.Equal($"Exported \"My Look\" to Out{PackagePolicy.FileExtension}.", harness.Runner.Status);

        // The file exists now, and replacing it takes the player's explicit Replace.
        harness.Actions.Export(harness.OpenId, "My Look", path, overwrite: false, previewPath: null);
        await harness.FinishAsync();

        Assert.NotNull(harness.Runner.Error);
        Assert.Null(harness.Runner.Status);
    }

    [Fact]
    public async Task Delete_SaysWhatHappened_AndCallsBackFirst()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var other = await harness.AnotherPlateAsync();
        var deleted = false;

        harness.Actions.Delete(other, "Second Look", () => deleted = true);
        await harness.FinishAsync();
        Assert.True(deleted);
        Assert.Equal("Deleted \"Second Look\".", harness.Runner.Status);

        // Alice's Active Plate: she is left without one, and the message says so without naming her.
        harness.Actions.Delete(harness.OpenId, "Blank", () => { });
        await harness.FinishAsync();
        Assert.Equal("Deleted \"Blank\". No Plate is Active for that character now.", harness.Runner.Status);
    }

    [Fact]
    public async Task SaveAsNewPlate_WithUnsavedChanges_SavesThemInTheCopy_AndOpensIt()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var original = harness.OpenId;
        var originalName = harness.Library.FindPlate(original)!.DisplayName;
        var savedBefore = File.ReadAllText(harness.Editor.Fixture.Paths.GetPlatePath(original));
        harness.Edit();

        harness.Actions.SaveAsNewPlate();
        await harness.FinishAsync();

        var copy = harness.Editor.Profiles.OpenPlateId!.Value;
        Assert.NotEqual(original, copy);
        Assert.Contains(harness.Library.GetSavedDocument(copy)!.Elements, e => e is TextProfileElement { Text: "An unsaved line" });
        Assert.False(harness.Editor.Session.IsDirty);

        // The original is exactly as it was last saved, and the copy follows it in My Plates.
        Assert.Equal(savedBefore, File.ReadAllText(harness.Editor.Fixture.Paths.GetPlatePath(original)));
        var order = harness.Library.GetOrderedPlates().Select(p => p.PlateId).ToList();
        Assert.Equal(order.IndexOf(original) + 1, order.IndexOf(copy));

        var copyName = harness.Library.FindPlate(copy)!.DisplayName;
        Assert.Equal($"{originalName} Copy", copyName);
        Assert.Equal($"You're editing \"{copyName}\" now. \"{originalName}\" stays as it was last saved.", harness.Runner.Status);
    }

    [Fact]
    public async Task SaveAsNewPlate_WithNothingUnsaved_OpensACopy()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var original = harness.OpenId;
        var originalName = harness.Library.FindPlate(original)!.DisplayName;

        harness.Actions.SaveAsNewPlate();
        await harness.FinishAsync();

        var copy = harness.Editor.Profiles.OpenPlateId!.Value;
        Assert.NotEqual(original, copy);
        Assert.Equal($"You're editing \"{originalName} Copy\" now, a copy of \"{originalName}\".", harness.Runner.Status);
    }

    [Fact]
    public async Task SaveAsNewPlate_CopiesTheCharacterLinks_ButNeverActive()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var original = harness.OpenId;

        harness.Actions.SaveAsNewPlate();
        await harness.FinishAsync();

        var copy = harness.Editor.Profiles.OpenPlateId!.Value;
        Assert.Equal(original, harness.Library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Contains(copy, harness.Library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
    }

    [Fact]
    public async Task WhileTheCopyIsWritten_ThePlateCannotBeEdited()
    {
        var store = new HeldWriteStore();
        using var harness = await PlateActionsHarness.CreateAsync(store);
        var original = harness.OpenId;
        store.Hold();

        harness.Actions.SaveAsNewPlate();
        await store.WriteStarted;
        Assert.True(harness.Editor.Profiles.IsBusy);
        Assert.Throws<InvalidOperationException>(harness.Edit);

        store.Release();
        await harness.FinishAsync();
        Assert.False(harness.Editor.Profiles.IsBusy);
        Assert.NotEqual(original, harness.Editor.Profiles.OpenPlateId);
    }

    [Fact]
    public async Task WhenAnotherPlateWasOpenedMeanwhile_TheCopyIsSavedButNotOpened()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var other = await harness.AnotherPlateAsync();

        harness.Actions.SaveAsNewPlate();
        await harness.Runner.Finished;
        harness.Editor.Profiles.OpenPlate(other);
        harness.Runner.Advance();

        Assert.Equal(other, harness.Editor.Profiles.OpenPlateId);
        Assert.Equal(3, harness.Library.GetOrderedPlates().Count);
        Assert.EndsWith("This Plate changed meanwhile, so it stays open.", harness.Runner.Status);
    }

    [Fact]
    public async Task WhenAnEditSlippedIn_ThePlateStaysOpen_WithTheEdit()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var original = harness.OpenId;

        harness.Actions.SaveAsNewPlate();
        await harness.Runner.Finished;
        harness.Edit();
        harness.Runner.Advance();

        Assert.Equal(original, harness.Editor.Profiles.OpenPlateId);
        Assert.True(harness.Editor.Session.IsDirty);
        Assert.StartsWith("Saved \"", harness.Runner.Status);
    }

    [Fact]
    public async Task WhenTheCopysCharacterLinkFails_TheCopyStays_AndThePlateKeepsItsChanges()
    {
        var store = new FaultInjectingStore();
        using var harness = await PlateActionsHarness.CreateAsync(store);
        var original = harness.OpenId;
        harness.Edit();
        var characters = harness.Editor.Fixture.Paths.CharactersDirectory;
        store.FailWrite = path => path.StartsWith(characters, StringComparison.OrdinalIgnoreCase);

        harness.Actions.SaveAsNewPlate();
        await harness.FinishAsync();

        // The copy exists, with the changes; the editor keeps the original, still unsaved, and the
        // player is told the changes are already saved in the copy.
        Assert.Equal(PlateLibraryService.SaveCopyLinkFailure, harness.Runner.Error);
        Assert.Null(harness.Runner.Status);
        Assert.Equal(original, harness.Editor.Profiles.OpenPlateId);
        Assert.True(harness.Editor.Session.IsDirty);
        var copy = Assert.Single(harness.Library.GetOrderedPlates(), p => p.PlateId != original);
        Assert.Contains(harness.Library.GetSavedDocument(copy.PlateId)!.Elements, e => e is TextProfileElement { Text: "An unsaved line" });
    }

    [Fact]
    public async Task SaveAsNewPlate_WhileAnotherActionRuns_IsRefused()
    {
        using var harness = await PlateActionsHarness.CreateAsync();
        var release = new TaskCompletionSource();
        harness.Runner.Run("wait", () => release.Task);

        harness.Actions.SaveAsNewPlate();

        Assert.Equal(PlateOperationRunner.BusyMessage, harness.Runner.Error);
        release.SetResult();
        await harness.FinishAsync();
        Assert.Single(harness.Library.GetOrderedPlates());
    }

    [Fact]
    public void TheEditorMenusNote_NamesTheActionsThatUseTheLastSavedVersion()
    {
        Assert.Contains("Set Active", PlateActions.UsesLastSavedNote);
        Assert.Contains("Save as Template", PlateActions.UsesLastSavedNote);
        Assert.Contains("Export", PlateActions.UsesLastSavedNote);
        Assert.DoesNotContain("Rename", PlateActions.UsesLastSavedNote);
        Assert.Contains("Duplicate", PlateActions.CardUsesLastSavedNote);
        Assert.DoesNotContain("Rename", PlateActions.CardUsesLastSavedNote);
        Assert.Contains("unsaved changes included", PlateActions.SaveAsNewPlateTooltip);

        // View is the Plate Viewer over the game; Preview stays the editors' own.
        Assert.Contains("Plate Viewer", PlateActions.ViewTooltip);
        Assert.DoesNotContain("Preview", PlateActions.ViewTooltip);
    }
}
