using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Templates;
using AetherFrame.Services;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.Services.Thumbnails;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The actions on a Plate as a whole, as a menu, with the prompts they ask: My Plates' card menu
/// (a card's right-click) and the Plate menu in both editors' action bar. It is one component, so
/// both offer the same actions with the same words in the same order and ask the same questions;
/// the actions themselves are <see cref="PlateActions"/>, which reports through its window's
/// <see cref="PlateOperationRunner"/>. Delete belongs to My Plates, where the whole Library is in
/// view. The editors' menu ends with Open another Plate and New Plate... (interface task 2,
/// through <see cref="PlateSwitcher"/>), and both windows draw Create Plate's chooser from here
/// (<see cref="Chooser"/>). The unsaved-changes question before another Plate opens is asked by
/// whichever window opens it, through the guard it attaches.
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

    // The editors' last two items (interface task 2). Open another Plate is a submenu, so its label
    // has no "...", as the editors' other submenus (Align to Canvas) have none.
    private const string OpenAnotherLabel = "Open another Plate";
    private const string NewPlateLabel = "New Plate...";
    private const string OnlyPlateNote = "This is your only Plate. New Plate... starts another.";
    private const string NewPlateTooltip = "Start a new Plate from a Template, without leaving the editor.\nWith unsaved changes, it asks first, before anything is made.";

    // Open another Plate's list width (unscaled pixels).
    private const float OpenAnotherWidth = 300f;

    private readonly PlateActions actions;
    private readonly PlateLibraryService library;
    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly PlateThumbnailService thumbnails;
    private readonly FileDialogManager fileDialogs;

    // Open another Plate's rows: an id per Plate, made once, and the search typed into its list.
    private readonly Dictionary<Guid, string> openAnotherRowIds = new();
    private string openAnotherSearch = string.Empty;
    private PlateSwitcher? switcher;

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
    /// <param name="templates">The Templates Create Plate's chooser lists.</param>
    /// <param name="profileService">The open Plate, for Delete's warning and the unsaved-changes question.</param>
    /// <param name="editorSession">Whether the open Plate has unsaved changes, for Delete's warning.</param>
    /// <param name="characterIdentity">The logged-in character, for the chooser.</param>
    /// <param name="thumbnails">Export's preview image, when a thumbnail of exactly the saved Plate is ready.</param>
    /// <param name="renderResources">The chooser's preview.</param>
    /// <param name="fileDialogs">The host window's file dialogs, which it draws every frame.</param>
    internal PlateMenu(
        PlateActions actions,
        PlateLibraryService library,
        TemplateLibraryService templates,
        ProfileService profileService,
        EditorSession editorSession,
        CharacterIdentityService characterIdentity,
        PlateThumbnailService thumbnails,
        ProfileRenderResources renderResources,
        FileDialogManager fileDialogs)
    {
        this.actions = actions;
        this.library = library;
        this.profileService = profileService;
        this.editorSession = editorSession;
        this.thumbnails = thumbnails;
        this.fileDialogs = fileDialogs;
        Chooser = new TemplateChooser(templates, actions, characterIdentity, renderResources);
    }

    internal PlateActions Actions => actions;

    /// <summary>Create Plate's Template chooser, drawn by <see cref="DrawPopups"/>; its window says what Use Template does.</summary>
    internal TemplateChooser Chooser { get; }

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

    /// <summary>
    /// The editors: Open another Plate and New Plate... replace the open Plate through
    /// <paramref name="plates"/>, which asks first when it has unsaved changes, before a new Plate
    /// is made. The chooser's Use Template goes through it too.
    /// </summary>
    internal void AttachSwitcher(PlateSwitcher plates)
    {
        switcher = plates;
        AttachOpenGuard(plates.Guard, plates.Proceed);
        Chooser.Use = templateId => plates.New(templateId);
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
    /// changes; then, once a switcher is attached (<see cref="AttachSwitcher"/>), Open another Plate
    /// and New Plate..., which leave this Plate for another.
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

        if (switcher is { } plates)
        {
            ImGui.Separator();
            DrawOpenAnotherMenu(plates, activePlateId, saving);

            using (ImRaii.Disabled(IsBusy || saving))
            {
                if (ImGui.MenuItem(NewPlateLabel))
                {
                    Chooser.Open();
                }
            }

            EditorWidgets.Tooltip(NewPlateTooltip);
        }
    }

    /// <summary>
    /// Every prompt this menu asks, Create Plate's chooser first: a Use Template that asks about
    /// unsaved changes closes the chooser before the question opens. Call once per frame from the
    /// host window's outermost scope.
    /// </summary>
    /// <param name="character">The logged-in character, if any (Delete's warnings).</param>
    internal void DrawPopups(CharacterContext? character)
    {
        Chooser.Draw();
        DrawRenamePopup();
        DrawSaveAsTemplatePopup();
        DrawOverwritePopup();
        DrawDeletePopup(character);
        DrawUnsavedChangesPopup();
    }

    /// <summary>
    /// After the unsaved-changes question's Save: opens the other Plate once the save succeeds, or
    /// shows why it didn't. In the editors, the switcher also opens a Plate waiting for this frame
    /// (<see cref="PlateSwitcher.Advance"/>). Call once per frame, before the open Plate is read.
    /// </summary>
    internal void AdvanceOpenGuard()
    {
        if (switcher is { } plates)
        {
            plates.Advance();
            return;
        }

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

    // ---------------------------------------------------------------- Open another Plate (the editors)

    /// <summary>
    /// Open another Plate: a submenu of every Plate in My Plates' own order, the one being edited
    /// marked Editing and greyed out, the character's Active Plate marked Active. With more Plates
    /// than fit (<see cref="PlateSwitcher.VisibleRows"/>), the list scrolls under a search field
    /// that filters it as My Plates' search does. Choosing a Plate closes the menu and switches to
    /// it, asking first when this one has unsaved changes. Greyed out, with the reason, when there is
    /// no other Plate or an action or a save is running.
    /// </summary>
    private void DrawOpenAnotherMenu(PlateSwitcher plates, Guid? activePlateId, bool saving)
    {
        var hasAnother = plates.HasAnotherPlate;
        var available = hasAnother && !IsBusy && !saving;
        using var disabled = ImRaii.Disabled(!available);
        using var menu = ImRaii.Menu(OpenAnotherLabel);
        if (!available)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(hasAnother ? PlateOperationRunner.BusyMessage : OnlyPlateNote);
            }

            return;
        }

        if (!menu.Success)
        {
            return;
        }

        var width = EditorWidgets.Scaled(OpenAnotherWidth);
        var searching = plates.NeedsSearch;
        if (ImGui.IsWindowAppearing())
        {
            openAnotherSearch = string.Empty;
            if (searching)
            {
                ImGui.SetKeyboardFocusHere();
            }
        }

        if (searching)
        {
            ImGui.SetNextItemWidth(width);
            ImGui.InputTextWithHint("##OpenAnotherSearch", "Search Plates...", ref openAnotherSearch, 64);
        }

        var shown = plates.Plates(searching ? openAnotherSearch : null);
        if (shown.Count == 0)
        {
            ImGui.TextDisabled("No Plates match your search.");
            return;
        }

        // A list of its own, so a long one scrolls under the search field. Its rows aren't items of
        // the menu itself, so a choice closes the menu explicitly.
        var rows = Math.Min(shown.Count, PlateSwitcher.VisibleRows);
        using var list = ImRaii.Child("##OpenAnotherList", new Vector2(width, rows * ImGui.GetTextLineHeightWithSpacing()), false);
        if (!list.Success)
        {
            return;
        }

        for (var i = 0; i < shown.Count; i++)
        {
            if (DrawOpenAnotherRow(plates, shown[i], activePlateId))
            {
                var chosen = shown[i].PlateId;
                ImGui.CloseCurrentPopup();
                plates.Open(chosen);
                return;
            }
        }
    }

    /// <summary>One Plate in Open another Plate's list: its name, drawn as text, and its marks. True when it was chosen.</summary>
    private bool DrawOpenAnotherRow(PlateSwitcher plates, PlateSummary plate, Guid? activePlateId)
    {
        if (!openAnotherRowIds.TryGetValue(plate.PlateId, out var id))
        {
            id = "##OpenAnother" + plate.PlateId.ToString("N");
            openAnotherRowIds[plate.PlateId] = id;
        }

        // Where a label would go: the row's highlight reaches half the item spacing beyond it.
        var textPos = ImGui.GetCursorScreenPos();
        var rowEnd = textPos.X + ImGui.GetContentRegionAvail().X;
        var reason = plates.WhyNotOpen(plate);
        var editing = plate.PlateId == profileService.OpenPlateId;
        var chosen = ImGui.Selectable(id, editing, reason is null ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled, Vector2.Zero);
        if (reason is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(reason);
        }

        var drawList = ImGui.GetWindowDrawList();
        var gap = EditorWidgets.Scaled(AetherMetrics.SpaceSm);

        // The marks, right-aligned: Editing for the open Plate, and Active, in the Active badge's
        // gold, for the logged-in character's Active Plate.
        var nameEnd = rowEnd;
        if (editing)
        {
            nameEnd -= ImGui.CalcTextSize("Editing").X;
            drawList.AddText(new Vector2(nameEnd, textPos.Y), ImGui.GetColorU32(EditorWidgets.DimTextColor), "Editing");
            nameEnd -= gap;
        }

        if (plate.PlateId == activePlateId)
        {
            nameEnd -= ImGui.CalcTextSize("Active").X;
            drawList.AddText(new Vector2(nameEnd, textPos.Y), ImGui.GetColorU32(AetherPalette.Gold), "Active");
            nameEnd -= gap;
        }

        // Player text: drawn as it is, never as a label, and cut off before the marks.
        var lineEnd = new Vector2(Math.Max(textPos.X, nameEnd), textPos.Y + ImGui.GetTextLineHeight());
        drawList.PushClipRect(textPos, lineEnd, true);
        drawList.AddText(textPos, ImGui.GetColorU32(reason is null ? ImGuiCol.Text : ImGuiCol.TextDisabled), plate.DisplayName);
        drawList.PopClipRect();
        return chosen && reason is null;
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
        if (AetherControls.GhostButton("Cancel", EditorWidgets.Scaled(new Vector2(110f, 0f))) || PopupEscapeGuard.CancelsPrompt())
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
        if (ImGui.Button("Cancel", EditorWidgets.Scaled(new Vector2(110f, 0f))) || PopupEscapeGuard.CancelsPrompt())
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
        if (ImGui.Button("Cancel", EditorWidgets.Scaled(new Vector2(110f, 0f))) || PopupEscapeGuard.CancelsPrompt())
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
        if (AetherControls.GhostButton("Cancel", EditorWidgets.Scaled(new Vector2(110f, 0f))) || PopupEscapeGuard.CancelsPrompt())
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- unsaved changes before opening

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
        ImGui.TextUnformatted($"\"{openName}\" has unsaved changes.");
        if (open.TemplateId is { } templateId)
        {
            // The editors' New Plate: asked before the new Plate is made, so Cancel leaves nothing behind.
            var templateName = actions.TemplateName(templateId) ?? "the Template";
            ImGui.TextUnformatted($"Save them before starting a new Plate from \"{templateName}\"?");
        }
        else
        {
            var targetName = library.FindPlate(open.PlateId)?.DisplayName ?? "the other Plate";
            ImGui.TextUnformatted($"Save them before opening \"{targetName}\"?");
        }

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
        if (AetherControls.GhostButton("Cancel", buttonSize) || PopupEscapeGuard.CancelsPrompt())
        {
            guard.Cancel();
            ImGui.CloseCurrentPopup();
        }
    }
}
