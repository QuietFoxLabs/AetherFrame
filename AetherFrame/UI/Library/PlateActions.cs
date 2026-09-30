using System;
using System.IO;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Templates;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.UI.Editor;

namespace AetherFrame.UI.Library;

/// <summary>
/// The actions on one Plate as a whole, shared by My Plates' card menu and the editors' Plate
/// menu, so both do exactly the same thing with the same words: Set Active, Rename, Duplicate or
/// Save as New Plate, Save as Template, Export and Delete. Each runs through its window's
/// <see cref="PlateOperationRunner"/>, which shows the result or the error in that window.
///
/// <para>Every action but Save as New Plate works on the saved Plate, as it always has from My
/// Plates: an editor's unsaved changes are not part of it, and the editors' Plate menu says so
/// (see <see cref="UsesLastSavedNote"/>). Rename is safe either way: the open document takes the
/// new name at once, and its next save keeps it. Save as New Plate is the editors' own: it saves
/// the document as it is, unsaved changes included, as a new Plate, and continues on that one.</para>
/// </summary>
internal sealed class PlateActions
{
    /// <summary>The editors' Plate menu line while the open Plate has unsaved changes.</summary>
    internal const string UsesLastSavedNote = "Unsaved changes: Set Active, Save as Template and Export use the last saved version. Save first to include them.";

    /// <summary>A card's menu line when its Plate is open in the editor with unsaved changes.</summary>
    internal const string CardUsesLastSavedNote = "Open in the editor with unsaved changes: Set Active, Duplicate, Save as Template and Export use the last saved version. Save first to include them.";

    /// <summary>The editors' View: the Plate Viewer over the game, not the editor's own Preview.</summary>
    internal const string ViewTooltip = "Shows this Plate over the game in the Plate Viewer, which you can move and resize.\nUnsaved changes show there too.";

    /// <summary>Save as New Plate's tooltip.</summary>
    internal const string SaveAsNewPlateTooltip = "Saves this Plate as it is now, unsaved changes included, as a new Plate, and opens it.\nThis one stays as it was last saved.";

    private readonly PlateLibraryService library;
    private readonly TemplateLibraryService templates;
    private readonly PlatePackageService packages;
    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly IAetherFrameLog log;

    internal PlateActions(
        PlateLibraryService library,
        TemplateLibraryService templates,
        PlatePackageService packages,
        ProfileService profileService,
        EditorSession editorSession,
        PlateOperationRunner runner,
        IAetherFrameLog log)
    {
        this.library = library;
        this.templates = templates;
        this.packages = packages;
        this.profileService = profileService;
        this.editorSession = editorSession;
        Runner = runner;
        this.log = log;
    }

    /// <summary>Runs these actions and holds their results for the window.</summary>
    internal PlateOperationRunner Runner { get; }

    /// <summary>
    /// Why Set Active is unavailable (no character logged in, or the Plate already is its Active
    /// Plate), or null when it's available.
    /// </summary>
    internal static string? SetActiveUnavailableReason(CharacterContext? character, bool isActive) =>
        character is null ? MyPlatesCharacterText.SetActiveNeedsCharacter
        : isActive ? MyPlatesCharacterText.AlreadyActive
        : null;

    /// <summary>Makes the saved Plate <paramref name="character"/>'s Active Plate.</summary>
    internal void SetActive(CharacterContext character, Guid plateId, string plateName) =>
        Runner.Run("set the Active Plate", () => library.SetActivePlateAsync(character, plateId),
            () => Runner.Status = MyPlatesCharacterText.NowActive(plateName));

    /// <summary>
    /// Renames the Plate, or returns why <paramref name="requestedName"/> can't be its name (for the
    /// prompt to show, with nothing started).
    /// </summary>
    internal string? Rename(Guid plateId, string requestedName)
    {
        if (!PlateNaming.TryNormalizeName(requestedName, out var name, out var error))
        {
            return error;
        }

        Runner.Run("rename the Plate", () => library.RenamePlateAsync(plateId, name));
        return null;
    }

    /// <summary>Copies the saved Plate after it; <paramref name="onCopied"/> gets the copy's id.</summary>
    internal void Duplicate(Guid plateId, Action<Guid> onCopied) =>
        Runner.Run("duplicate the Plate", () => library.DuplicatePlateAsync(plateId), onCopied);

    /// <summary>
    /// Saves the saved Plate as a Template, or returns why <paramref name="requestedName"/> can't be
    /// its name (for the prompt to show, with nothing started).
    /// </summary>
    internal string? SaveAsTemplate(Guid plateId, string requestedName)
    {
        if (!TemplateNaming.TryNormalizeName(requestedName, out var name, out var error))
        {
            return error;
        }

        Runner.Run("save the Template", () => templates.SaveAsTemplateAsync(plateId, name),
            _ => Runner.Status = $"Saved \"{name}\" as a Template.");
        return null;
    }

    /// <summary>The path Export writes: the chosen one, with the package extension added when it's missing.</summary>
    internal static string ExportPath(string chosenPath) =>
        chosenPath.EndsWith(PackagePolicy.FileExtension, StringComparison.OrdinalIgnoreCase) ? chosenPath : chosenPath + PackagePolicy.FileExtension;

    /// <summary>
    /// Exports the saved Plate to <paramref name="path"/>, off the render thread, with
    /// <paramref name="previewPath"/>'s thumbnail when there is one.
    /// </summary>
    internal void Export(Guid plateId, string plateName, string path, bool overwrite, string? previewPath) =>
        Runner.Run("export the Plate", () => Task.Run(() => packages.Export(plateId, path, overwrite, previewPath)), result =>
        {
            if (result.Succeeded)
            {
                Runner.Status = $"Exported \"{plateName}\" to {Path.GetFileName(result.FilePath)}.";
            }
            else
            {
                Runner.Error = result.FailureMessage;
            }
        });

    /// <summary>Deletes the Plate (to the trash); <paramref name="onDeleted"/> runs first, once it's gone.</summary>
    internal void Delete(Guid plateId, string plateName, Action onDeleted) =>
        Runner.Run("delete the Plate", () => library.DeletePlateAsync(plateId), result =>
        {
            onDeleted();
            Runner.Status = result.ClearedActiveForContentIds.Count > 0
                ? $"Deleted \"{plateName}\". No Plate is Active for {(result.ClearedActiveForContentIds.Count == 1 ? "that character" : "those characters")} now."
                : $"Deleted \"{plateName}\".";
        });

    /// <summary>
    /// Save as New Plate: the open Plate as it is now, unsaved changes included, saved as a new
    /// Plate after it, which then opens in its place. The original keeps its last saved version.
    /// If the open Plate changed while the copy was written (another Plate was opened, or an edit
    /// slipped in), the copy is still saved but not opened, so no change is ever dropped.
    /// </summary>
    internal void SaveAsNewPlate()
    {
        if (Runner.IsBusy)
        {
            Runner.Error = PlateOperationRunner.BusyMessage;
            return;
        }

        // What is on screen, exactly as Save takes it: an edit still being typed or dragged counts.
        editorSession.CommitPendingEdits();
        editorSession.EndInteraction();
        if (profileService.CurrentProfile is not { } open)
        {
            Runner.Error = "No Plate is open.";
            return;
        }

        var originalId = open.ProfileId;
        var originalName = open.Name;
        var hadChanges = editorSession.IsDirty;
        var copied = ProfileService.DocumentState.Capture(open);

        Runner.Run("save the Plate as a new Plate", SaveCopyAsync, copyId => OpenCopy(copyId, originalId, originalName, hadChanges, copied));
    }

    private async Task<Guid> SaveCopyAsync()
    {
        try
        {
            return await profileService.SaveCopyOfCurrentAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.GetType() == typeof(InvalidOperationException))
        {
            // AetherFrame's own wording (a value JSON can't hold, say): shown as it is, and logged.
            log.Error(ex, "AetherFrame failed to save the Plate as a new Plate.");
            throw new PlateLibraryException(UserFacingError.Describe(ex, "The new Plate couldn't be saved."));
        }
    }

    private void OpenCopy(Guid copyId, Guid originalId, string originalName, bool hadChanges, ProfileService.DocumentState copied)
    {
        var copyName = library.FindPlate(copyId)?.DisplayName ?? "The new Plate";
        if (profileService.OpenPlateId != originalId || !editorSession.LiveDocumentMatches(copied))
        {
            Runner.Status = $"Saved \"{copyName}\" as a new Plate in My Plates. This Plate changed meanwhile, so it stays open.";
            return;
        }

        try
        {
            profileService.OpenPlate(copyId);
        }
        catch (Exception ex) when (ex is PlateLibraryException or InvalidOperationException)
        {
            Runner.Error = $"Saved \"{copyName}\" as a new Plate in My Plates, but it couldn't be opened here. {UserFacingError.Describe(ex, string.Empty)}".TrimEnd();
            return;
        }

        Runner.Status = hadChanges
            ? $"You're editing \"{copyName}\" now. \"{originalName}\" stays as it was last saved."
            : $"You're editing \"{copyName}\" now, a copy of \"{originalName}\".";
    }
}
