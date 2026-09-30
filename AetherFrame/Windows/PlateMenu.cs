using System;
using System.IO;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Templates;
using AetherFrame.Services;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Thumbnails;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The actions on a Plate as a whole, as a menu, with the prompts they ask: My Plates' card menu
/// (a card's right-click) and the Plate menu in both editors' action bar. It is one component, so
/// both offer the same actions with the same words in the same order and ask the same questions;
/// the actions themselves are <see cref="PlateActions"/>, which reports through its window's
/// <see cref="PlateOperationRunner"/>. Delete, and the unsaved-changes question before opening
/// another Plate, belong to My Plates, where the whole Library is in view.
///
/// <para>A menu item only asks for its prompt: every prompt is opened and drawn by
/// <see cref="DrawPopups"/>, from the host window's outermost scope, because OpenPopup and
/// BeginPopup must share one id-stack scope and menu items are drawn inside a popup (and, in My
/// Plates, inside the card grid's child window). Nothing is keyed off a Plate's name beyond
/// pre-filling a text field, and every name is drawn unformatted.</para>
/// </summary>
internal sealed class PlateMenu
{
    private const string RenamePopupId = "Rename Plate##AetherFrameRenamePlate";
    private const string DeletePopupId = "Delete Plate##AetherFrameDeletePlate";
    private const string SaveAsTemplatePopupId = "Save as Template##AetherFrameSaveAsTemplate";
    private const string OverwritePopupId = "Replace File?##AetherFrameExportOverwrite";
    private const string UnsavedPopupId = "Unsaved Changes##AetherFramePlateSwitch";
    private const string PackageFilter = "AetherFrame Plate{" + PackagePolicy.FileExtension + "}";

    private readonly PlateActions actions;
    private readonly PlateLibraryService library;
    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly PlateThumbnailService thumbnails;
    private readonly FileDialogManager fileDialogs;

    private bool pendingRenamePopup;
    private bool pendingDeletePopup;
    private bool pendingSaveAsTemplatePopup;
    private bool pendingOverwritePopup;
    private bool pendingGuardPrompt;

    private Guid renameTargetId;
    private string renameBuffer = string.Empty;
    private string? renameError;

    private Guid deleteTargetId;

    private Guid saveAsTemplateSourcePlateId;
    private string saveAsTemplateBuffer = string.Empty;
    private string? saveAsTemplateError;

    private (Guid PlateId, string Name, string Path)? pendingOverwrite;

    private PlateOpenGuard? openGuard;
    private Action<PlateOpenRequest>? openNow;

    /// <param name="actions">The actions, and the runner of the window drawing this menu.</param>
    /// <param name="library">Finds Plates by id.</param>
    /// <param name="profileService">The open Plate, for Delete's warning and the unsaved-changes question.</param>
    /// <param name="editorSession">Whether the open Plate has unsaved changes, for Delete's warning.</param>
    /// <param name="thumbnails">Export's preview image, when a thumbnail of exactly the saved Plate is ready.</param>
    /// <param name="fileDialogs">The host window's file dialogs, which it draws every frame.</param>
    internal PlateMenu(
        PlateActions actions, PlateLibraryService library, ProfileService profileService, EditorSession editorSession, PlateThumbnailService thumbnails, FileDialogManager fileDialogs)
    {
        this.actions = actions;
        this.library = library;
        this.profileService = profileService;
        this.editorSession = editorSession;
        this.thumbnails = thumbnails;
        this.fileDialogs = fileDialogs;
    }

    internal PlateActions Actions => actions;

    /// <summary>Runs this menu's actions and holds their results.</summary>
    internal PlateOperationRunner Runner => actions.Runner;

    /// <summary>Checks what sharing a Plate would send, when this build can; the item shows only then.</summary>
    internal Action<Guid>? CheckSharing { get; set; }

    /// <summary>My Plates: a card's Duplicate made this copy (the selection follows it).</summary>
    internal Action<Guid>? Duplicated { get; set; }

    /// <summary>My Plates: this Plate was deleted.</summary>
    internal Action<Guid>? Deleted { get; set; }

    private bool IsBusy => actions.Runner.IsBusy;

    /// <summary>
    /// My Plates: opening another Plate asks first when the open one has unsaved changes.
    /// <paramref name="open"/> opens a Plate once nothing stands in the way.
    /// </summary>
    internal void AttachOpenGuard(PlateOpenGuard guard, Action<PlateOpenRequest> open)
    {
        openGuard = guard;
        openNow = open;
    }

    /// <summary>Shows the unsaved-changes question for the guard's pending open (see <see cref="PlateOpenGuard.Request"/>).</summary>
    internal void AskBeforeOpening() => pendingGuardPrompt = true;

    /// <summary>
    /// My Plates' card menu: View, the two editors, Set Active, Duplicate, Save as Template,
    /// Export, Rename and Delete. Must be called between a matching BeginPopup/EndPopup.
    /// </summary>
    /// <param name="plate">The card's Plate.</param>
    /// <param name="character">The logged-in character, if any (only Set Active needs one).</param>
    /// <param name="activePlateId">That character's Active Plate, if any.</param>
    /// <param name="view">Shows the Plate in the Plate Viewer.</param>
    /// <param name="open">Opens the Plate in the Basic (true) or Advanced (false) Editor.</param>
    internal void DrawCardItems(PlateSummary plate, CharacterContext? character, Guid? activePlateId, Action<Guid> view, Action<Guid, bool> open)
    {
        var ready = plate.IsReady;

        if (profileService.OpenPlateId == plate.PlateId && editorSession.IsDirty)
        {
            DrawNote(PlateActions.CardUsesLastSavedNote);
        }

        using (ImRaii.Disabled(!ready))
        {
            if (ImGui.MenuItem("View"))
            {
                view(plate.PlateId);
            }

            if (ImGui.MenuItem("Open in Basic Editor"))
            {
                open(plate.PlateId, true);
            }

            if (ImGui.MenuItem("Open in Advanced Editor"))
            {
                open(plate.PlateId, false);
            }
        }

        ImGui.Separator();
        DrawSetActiveItem(plate, character, activePlateId);
        ImGui.Separator();

        using (ImRaii.Disabled(!ready || IsBusy))
        {
            if (ImGui.MenuItem("Duplicate"))
            {
                actions.Duplicate(plate.PlateId, copyId => Duplicated?.Invoke(copyId));
            }

            DrawTemplateExportAndCheckItems(plate);
            ImGui.Separator();
            DrawRenameItem(plate);
        }

        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.MenuItem("Delete"))
            {
                deleteTargetId = plate.PlateId;
                pendingDeletePopup = true;
            }
        }
    }

    /// <summary>
    /// The editors' Plate menu, for the open Plate: View, Set Active, Save as New Plate, Save as
    /// Template, Export and Rename, in the card menu's order, with a note while there are unsaved
    /// changes.
    /// Must be called between a matching BeginPopup/EndPopup.
    /// </summary>
    /// <param name="plate">The open Plate, as My Plates lists it.</param>
    /// <param name="character">The logged-in character, if any.</param>
    /// <param name="activePlateId">That character's Active Plate, if any.</param>
    /// <param name="hasUnsavedChanges">The open Plate has unsaved changes.</param>
    /// <param name="saving">A save is being written.</param>
    /// <param name="view">Shows the open Plate, unsaved changes included, in the Plate Viewer.</param>
    internal void DrawEditorItems(PlateSummary plate, CharacterContext? character, Guid? activePlateId, bool hasUnsavedChanges, bool saving, Action<Guid> view)
    {
        if (hasUnsavedChanges)
        {
            DrawNote(PlateActions.UsesLastSavedNote);
        }

        if (ImGui.MenuItem("View"))
        {
            view(plate.PlateId);
        }

        EditorWidgets.Tooltip(PlateActions.ViewTooltip);
        ImGui.Separator();
        DrawSetActiveItem(plate, character, activePlateId);
        ImGui.Separator();

        using (ImRaii.Disabled(!plate.IsReady || IsBusy || saving))
        {
            if (ImGui.MenuItem("Save as New Plate"))
            {
                actions.SaveAsNewPlate();
            }
        }

        EditorWidgets.Tooltip(PlateActions.SaveAsNewPlateTooltip);

        using (ImRaii.Disabled(!plate.IsReady || IsBusy))
        {
            DrawTemplateExportAndCheckItems(plate);
            ImGui.Separator();
            DrawRenameItem(plate);
        }
    }

    /// <summary>Every prompt this menu asks. Call once per frame from the host window's outermost scope.</summary>
    /// <param name="character">The logged-in character, if any (Delete's warnings).</param>
    internal void DrawPopups(CharacterContext? character)
    {
        DrawRenamePopup();
        DrawSaveAsTemplatePopup();
        DrawOverwritePopup();
        DrawDeletePopup(character);
        DrawUnsavedChangesPopup();
    }

    /// <summary>
    /// My Plates: after the unsaved-changes question's Save, opens the other Plate once the save
    /// succeeds, or shows why it didn't. Call once per frame.
    /// </summary>
    internal void AdvanceOpenGuard()
    {
        if (openGuard?.Advance() is not { } outcome)
        {
            return;
        }

        if (outcome.Open is { } open)
        {
            openNow?.Invoke(open);
        }
        else
        {
            Runner.Error = outcome.Error;
        }
    }

    // ---------------------------------------------------------------- items

    /// <summary>A menu's first line, in the warning tone, wrapped to a menu's width.</summary>
    private static void DrawNote(string note)
    {
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + EditorWidgets.Scaled(300f)))
        using (ImRaii.PushColor(ImGuiCol.Text, EditorWidgets.WarningColor))
        {
            ImGui.TextWrapped(note);
        }

        ImGui.Separator();
    }

    private void DrawSetActiveItem(PlateSummary plate, CharacterContext? character, Guid? activePlateId)
    {
        var reason = PlateActions.SetActiveUnavailableReason(character, plate.PlateId == activePlateId);
        using (ImRaii.Disabled(!plate.IsReady || reason is not null || IsBusy))
        {
            if (ImGui.MenuItem("Set Active") && character is { } who)
            {
                actions.SetActive(who, plate.PlateId, plate.DisplayName);
            }
        }

        if (plate.IsReady && reason is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(reason);
        }
    }

    private void DrawTemplateExportAndCheckItems(PlateSummary plate)
    {
        if (ImGui.MenuItem("Save as Template"))
        {
            saveAsTemplateSourcePlateId = plate.PlateId;
            saveAsTemplateBuffer = plate.DisplayName;
            saveAsTemplateError = null;
            pendingSaveAsTemplatePopup = true;
        }

        if (ImGui.MenuItem("Export"))
        {
            OpenExportDialog(plate.PlateId, plate.DisplayName);
        }

        if (CheckSharing is { } checkSharing && ImGui.MenuItem("Check what would be shared (preview)"))
        {
            checkSharing(plate.PlateId);
        }
    }

    private void DrawRenameItem(PlateSummary plate)
    {
        if (ImGui.MenuItem("Rename"))
        {
            renameTargetId = plate.PlateId;
            renameBuffer = plate.DisplayName;
            renameError = null;
            pendingRenamePopup = true;
        }
    }

    // ---------------------------------------------------------------- rename

    private void DrawRenamePopup()
    {
        if (pendingRenamePopup)
        {
            ImGui.OpenPopup(RenamePopupId);
            pendingRenamePopup = false;
        }

        if (!ImGui.BeginPopupModal(RenamePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(EditorWidgets.Scaled(300f));
        var submitted = ImGui.InputText("##PlateName", ref renameBuffer, PlateNaming.MaxNameLength, ImGuiInputTextFlags.EnterReturnsTrue);

        if (renameError is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(IsBusy))
        {
            if (AetherControls.PrimaryButton("Rename", EditorWidgets.Scaled(new Vector2(110f, 0f))) || submitted)
            {
                renameError = actions.Rename(renameTargetId, renameBuffer);
                if (renameError is null)
                {
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine();
        if (AetherControls.GhostButton("Cancel", EditorWidgets.Scaled(new Vector2(110f, 0f))))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- Save as Template

    private void DrawSaveAsTemplatePopup()
    {
        if (pendingSaveAsTemplatePopup)
        {
            ImGui.OpenPopup(SaveAsTemplatePopupId);
            pendingSaveAsTemplatePopup = false;
        }

        if (!ImGui.BeginPopupModal(SaveAsTemplatePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        EditorWidgets.Hint("Saves this Plate's last saved state. Changes you haven't saved yet won't be included.");
        ImGui.Spacing();

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(EditorWidgets.Scaled(300f));
        var submitted = ImGui.InputText("##TemplateName", ref saveAsTemplateBuffer, TemplateNaming.MaxNameLength, ImGuiInputTextFlags.EnterReturnsTrue);

        if (saveAsTemplateError is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.Button("Save as Template", EditorWidgets.Scaled(new Vector2(160f, 0f))) || submitted)
            {
                saveAsTemplateError = actions.SaveAsTemplate(saveAsTemplateSourcePlateId, saveAsTemplateBuffer);
                if (saveAsTemplateError is null)
                {
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", EditorWidgets.Scaled(new Vector2(110f, 0f))))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- Export

    private void OpenExportDialog(Guid plateId, string name) =>
        fileDialogs.SaveFileDialog("Export Plate", PackageFilter, PackagePaths.SuggestFileName(name), PackagePolicy.FileExtension, (chosen, path) =>
        {
            if (!chosen || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            path = PlateActions.ExportPath(path);
            if (File.Exists(path))
            {
                pendingOverwrite = (plateId, name, path);
                pendingOverwritePopup = true;
                return;
            }

            StartExport(plateId, name, path, overwrite: false);
        });

    // The thumbnail is read here, on the draw thread; the export itself runs off it.
    private void StartExport(Guid plateId, string name, string path, bool overwrite) =>
        actions.Export(plateId, name, path, overwrite, ReadyThumbnailPath(plateId));

    /// <summary>A ready thumbnail of exactly the saved Plate, if one exists (none are generated yet).</summary>
    private string? ReadyThumbnailPath(Guid plateId)
    {
        if (library.FindPlate(plateId) is not { IsReady: true } plate)
        {
            return null;
        }

        var thumbnail = thumbnails.Get(plateId, PlateThumbnailService.VersionKeyFor(plate.Revision, plate.ModifiedUtc), () => library.GetSavedDocument(plateId));
        return thumbnail.State == PlateThumbnailState.Ready ? thumbnail.ImagePath : null;
    }

    private void DrawOverwritePopup()
    {
        if (pendingOverwritePopup)
        {
            ImGui.OpenPopup(OverwritePopupId);
            pendingOverwritePopup = false;
        }

        if (!ImGui.BeginPopupModal(OverwritePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        if (pendingOverwrite is not { } pending)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"\"{Path.GetFileName(pending.Path)}\" already exists.");
        ImGui.TextUnformatted("Replace it with this Plate?");
        ImGui.Spacing();

        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.Button("Replace", EditorWidgets.Scaled(new Vector2(110f, 0f))))
            {
                pendingOverwrite = null;
                StartExport(pending.PlateId, pending.Name, pending.Path, overwrite: true);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", EditorWidgets.Scaled(new Vector2(110f, 0f))))
        {
            pendingOverwrite = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- delete (My Plates)

    private void DrawDeletePopup(CharacterContext? character)
    {
        if (pendingDeletePopup)
        {
            ImGui.OpenPopup(DeletePopupId);
            pendingDeletePopup = false;
        }

        if (!ImGui.BeginPopupModal(DeletePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        var plate = library.FindPlate(deleteTargetId);
        if (plate is null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Delete \"{plate.DisplayName}\"?");
        EditorWidgets.Hint("It's moved to AetherFrame's Plate trash folder, not destroyed. Its images are kept.");

        var activeForCurrent = character is { } who && plate.ActiveForContentIds.Contains(who.ContentId);
        var activeForOthers = plate.ActiveForContentIds.Count - (activeForCurrent ? 1 : 0);
        if (activeForCurrent)
        {
            foreach (var line in MyPlatesCharacterText.DeletingCurrentActive)
            {
                ImGui.TextColored(EditorWidgets.WarningColor, line);
            }
        }

        if (activeForOthers > 0)
        {
            ImGui.TextColored(EditorWidgets.WarningColor, activeForOthers == 1
                ? "It's also another character's Active Plate; that character will be left without one."
                : $"It's also the Active Plate of {activeForOthers} other characters; they will be left without one.");
        }

        if (profileService.OpenPlateId == plate.PlateId)
        {
            ImGui.TextColored(EditorWidgets.WarningColor, editorSession.IsDirty
                ? "It's open in the editor with unsaved changes, which will be lost."
                : "It's open in the editor, which will close it.");
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(IsBusy))
        {
            if (AetherControls.DangerButton("Delete", EditorWidgets.Scaled(new Vector2(110f, 0f))))
            {
                var plateId = plate.PlateId;
                actions.Delete(plateId, plate.DisplayName, () => Deleted?.Invoke(plateId));
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (AetherControls.GhostButton("Cancel", EditorWidgets.Scaled(new Vector2(110f, 0f))))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- unsaved changes before opening (My Plates)

    private void DrawUnsavedChangesPopup()
    {
        if (openGuard is not { } guard)
        {
            return;
        }

        if (pendingGuardPrompt)
        {
            ImGui.OpenPopup(UnsavedPopupId);
            pendingGuardPrompt = false;
        }

        using var popup = ImRaii.PopupModal(UnsavedPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);
        if (!popup.Success)
        {
            return;
        }

        if (guard.Pending is not { } open)
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        var openName = profileService.CurrentProfile?.Name ?? "The open Plate";
        var targetName = library.FindPlate(open.PlateId)?.DisplayName ?? "the other Plate";
        ImGui.TextUnformatted($"\"{openName}\" has unsaved changes.");
        ImGui.TextUnformatted($"Save them before opening \"{targetName}\"?");
        ImGui.Spacing();

        var buttonSize = EditorWidgets.Scaled(new Vector2(110f, 0f));
        using (ImRaii.Disabled(!guard.CanAnswer))
        {
            if (AetherControls.PrimaryButton(guard.IsSaving ? "Saving..." : "Save", buttonSize))
            {
                guard.Save();
                ImGui.CloseCurrentPopup();
            }
        }

        // Discard is unavailable while a save is being written, exactly like Save: the revert would
        // be refused underneath it, and the other Plate would then open over unsaved edits.
        ImGui.SameLine();
        using (ImRaii.Disabled(!guard.CanAnswer))
        {
            if (AetherControls.DangerButton("Discard", buttonSize) && guard.Discard() is { } discarded)
            {
                // Only once the edits are really gone: a refused revert (a save landed meanwhile)
                // keeps the question open, with the editor's own message saying why.
                ImGui.CloseCurrentPopup();
                openNow?.Invoke(discarded);
            }
        }

        ImGui.SameLine();
        if (AetherControls.GhostButton("Cancel", buttonSize))
        {
            guard.Cancel();
            ImGui.CloseCurrentPopup();
        }
    }
}
