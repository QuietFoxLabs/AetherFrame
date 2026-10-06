using System;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Services;
using AetherFrame.Services.Templates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Tutorial;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Create Plate's Template chooser, part of the shared <see cref="PlateMenu"/> (interface task 2
/// moved it out of My Plates): My Plates draws it for Create Plate, and the editors for the Plate
/// menu's New Plate..., so both show the same chooser. What its Use Template then does is the
/// window's own (<see cref="Use"/>): My Plates makes the Plate and then asks about the open Plate's
/// unsaved changes, while the editors ask first (<see cref="PlateSwitcher"/>).
///
/// <para>A two-pane modal: a full-width, scrollable Template browser on the left (Start: the two
/// built-ins; My Templates: everything saved), and the selected Template's own details on the
/// right, with a live preview through the same <see cref="ProfileRenderer"/> every other preview in
/// AetherFrame uses. Nothing is created until Use Template (or a double-click on a row). A saved
/// Template's row menu also renames, duplicates and deletes it, through <see cref="PlateActions"/>,
/// with prompts that open over the chooser; My Plates' Manage Templates asks for the same prompts
/// (<see cref="RequestRename"/>, <see cref="RequestDelete"/>). It is a popup of whichever window
/// draws it, so that window's <see cref="PopupEscapeGuard"/> counts it: Escape on the chooser is its
/// Cancel, and on a row menu closes only the menu.</para>
/// </summary>
internal sealed class TemplateChooser
{
    /// <summary>What the chooser and Manage Templates say when the saved Templates couldn't be loaded.</summary>
    internal const string UserTemplatesUnavailableText = "Your saved Templates couldn't be loaded. Built-in Templates still work. See the Dalamud log for details.";

    private const string TemplateChooserPopupId = "Create Plate##AetherFrameTemplateChooser";
    private const string TemplateRenamePopupId = "Rename Template##AetherFrameTemplateRename";
    private const string TemplateDeletePopupId = "Delete Template##AetherFrameTemplateDelete";

    private const float ChooserWidth = 720f;
    private const float ChooserHeight = 450f;
    private const float ChooserMinWidth = 620f;
    private const float ChooserMinHeight = 380f;
    private const float ChooserLeftPaneWidth = 260f;

    // The right pane's words for Blank Canvas; its dash is built from its code point, so the source
    // holds no raw special character.
    private static readonly string NoPreviewText = "No preview " + (char)0x2014 + " Blank Canvas starts empty.";

    private readonly TemplateLibraryService templates;
    private readonly PlateActions actions;
    private readonly CharacterIdentityService characterIdentity;
    private readonly ProfileRenderResources renderResources;

    private bool pendingTemplateChooserPopup;
    private Guid chosenTemplateId = BuiltInTemplateCatalog.AdventurePlateClassicId;

    // Use Template chosen in a row's right-click menu. That menu is a popup inside the chooser, so
    // closing from it would close only the menu: the chooser takes the request in its own scope
    // instead, and closes as it does for its Use Template button.
    private Guid? chooserUseRequestedId;

    // The right pane's preview document is regenerated only when the selection actually changes
    // (never every frame): a built-in's document is freshly generated each time it's resolved,
    // and re-running that, plus the renderer's per-instance font prewarming, every single frame
    // the popup is open would be wasteful and would leak a prewarm cache entry per frame.
    private Guid? chooserPreviewedTemplateId;
    private ProfileDocument? chooserPreviewDocument;

    // The last ImGui frame the chooser was on screen (see Showing).
    private int shownFrame = int.MinValue;

    private bool pendingTemplateRenamePopup;
    private Guid templateRenameTargetId;
    private string templateRenameBuffer = string.Empty;
    private string? templateRenameError;

    private bool pendingTemplateDeletePopup;
    private Guid templateDeleteTargetId;

    /// <param name="templates">The Templates the chooser lists and previews.</param>
    /// <param name="actions">Its window's Plate actions: the row menus' Rename, Duplicate and Delete run through them.</param>
    /// <param name="characterIdentity">The logged-in character, for the footer and a built-in Template's preview.</param>
    /// <param name="renderResources">The preview's renderer resources.</param>
    internal TemplateChooser(TemplateLibraryService templates, PlateActions actions, CharacterIdentityService characterIdentity, ProfileRenderResources renderResources)
    {
        this.templates = templates;
        this.actions = actions;
        this.characterIdentity = characterIdentity;
        this.renderResources = renderResources;
    }

    /// <summary>
    /// Use Template, as the window drawing the chooser does it, for the Template's id: My Plates
    /// makes the Plate, then opens it behind the unsaved-changes question; the editors ask first.
    /// </summary>
    internal Action<Guid>? Use { get; set; }

    /// <summary>The footer's Manage Templates...: My Plates' Templates view. The link shows only when set.</summary>
    internal Action? ManageTemplates { get; set; }

    /// <summary>The footer's Step by Step...: guided creation, for a player who'd rather be guided. The link shows only when set.</summary>
    internal Action? CreateStepByStep { get; set; }

    /// <summary>A row menu's Duplicate made this copy (My Plates: Manage Templates' selection follows it).</summary>
    internal Action<Guid>? TemplateDuplicated { get; set; }

    /// <summary>This Template was deleted (My Plates: Manage Templates drops it from its selection).</summary>
    internal Action<Guid>? TemplateDeleted { get; set; }

    /// <summary>
    /// Whether the chooser was on screen this frame or the one before: the tutorial waits on it,
    /// and reads it before the windows draw. False when the window that draws it stopped drawing.
    /// </summary>
    internal bool Showing => shownFrame >= ImGui.GetFrameCount() - 1;

    private bool IsBusy => actions.Runner.IsBusy;

    /// <summary>Whether a new Plate can be made from the Template now (see <see cref="PlateActions.TemplateProblem"/>).</summary>
    private bool CanUse(Guid templateId) => actions.TemplateProblem(templateId) is null;

    /// <summary>Opens the chooser on Adventure Plate Classic, the next time <see cref="Draw"/> runs.</summary>
    internal void Open() => pendingTemplateChooserPopup = true;

    /// <summary>Asks to rename a saved Template (Manage Templates' Rename; the row menus ask for themselves).</summary>
    internal void RequestRename(Guid templateId, string currentName)
    {
        templateRenameTargetId = templateId;
        templateRenameBuffer = currentName;
        templateRenameError = null;
        pendingTemplateRenamePopup = true;
    }

    /// <summary>Asks to delete a saved Template (Manage Templates' Delete; the row menus ask for themselves).</summary>
    internal void RequestDelete(Guid templateId)
    {
        templateDeleteTargetId = templateId;
        pendingTemplateDeletePopup = true;
    }

    /// <summary>
    /// The chooser and the Template prompts. Call once per frame from the host window's outermost
    /// scope, where OpenPopup and BeginPopup share one id-stack scope.
    /// </summary>
    internal void Draw()
    {
        DrawTemplateChooserPopup();

        // While the chooser is open, its own Rename and Delete prompts (from a row's context menu)
        // are drawn from inside the chooser's popup scope instead (see DrawTemplateChooserPopup):
        // ImGui.OpenPopup() picks its stack level from how many BeginPopup/EndPopup blocks it's
        // lexically nested inside at the moment it's called, and opening a second popup at the SAME
        // level the chooser occupies closes the chooser first. Calling these two from here too
        // whenever that happens would open Rename/Delete twice in one frame for the same popup id,
        // which is unsafe, so exactly one of the two call sites ever runs on a given frame.
        if (!ImGui.IsPopupOpen(TemplateChooserPopupId))
        {
            DrawTemplateRenamePopup();
            DrawTemplateDeletePopup();
        }
    }

    // ---------------------------------------------------------------- the chooser

    /// <summary>
    /// A two-pane dialog: a full-width, scrollable Template browser on the left (Start: the two
    /// built-ins; My Templates: everything saved), and the selected Template's own details,
    /// including a live preview through the exact same <see cref="ProfileRenderer"/> every other
    /// preview surface in AetherFrame uses, on the right. Nothing is created until "Use Template"
    /// (or a double-click on a row) is confirmed.
    /// </summary>
    private void DrawTemplateChooserPopup()
    {
        if (pendingTemplateChooserPopup)
        {
            ImGui.OpenPopup(TemplateChooserPopupId);
            pendingTemplateChooserPopup = false;
            chosenTemplateId = BuiltInTemplateCatalog.AdventurePlateClassicId;
            chooserPreviewedTemplateId = null;
            chooserUseRequestedId = null;
        }

        ImGui.SetNextWindowSize(new Vector2(ChooserWidth, ChooserHeight) * ImGuiHelpers.GlobalScale, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(ChooserMinWidth, ChooserMinHeight) * ImGuiHelpers.GlobalScale, new Vector2(float.MaxValue, float.MaxValue));

        if (!ImGui.BeginPopupModal(TemplateChooserPopupId, ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        shownFrame = ImGui.GetFrameCount();
        TutorialAnchorMarks.MarkWindow(TutorialTarget.LibraryTemplateChooser);

        // The selection can go stale without the chooser closing, e.g. Delete Template from a
        // row's own context menu. Fall back to the default rather than showing an empty selection.
        if (BuiltInTemplateCatalog.Find(chosenTemplateId) is null && templates.FindTemplate(chosenTemplateId) is null)
        {
            chosenTemplateId = BuiltInTemplateCatalog.AdventurePlateClassicId;
            chooserPreviewedTemplateId = null;
        }

        var footerHeight = (ImGui.GetFrameHeightWithSpacing() * 2f) + ImGui.GetStyle().ItemSpacing.Y + (4f * ImGuiHelpers.GlobalScale);
        var bodyHeight = -footerHeight;
        var leftWidth = ChooserLeftPaneWidth * ImGuiHelpers.GlobalScale;

        using (var left = AetherChild.Begin("##TemplateChooserLeft", new Vector2(leftWidth, bodyHeight), true))
        {
            if (left.Success)
            {
                DrawTemplateChooserLeftPane();
            }
        }

        ImGui.SameLine();

        using (var right = AetherChild.Begin("##TemplateChooserRight", new Vector2(-1f, bodyHeight), false))
        {
            if (right.Success)
            {
                DrawTemplateChooserRightPane();
            }
        }

        ImGui.Separator();
        DrawTemplateChooserFooter();

        // A row menu's Use Template, taken here in the chooser's own scope, where closing closes
        // the chooser, exactly as its Use Template button does.
        if (chooserUseRequestedId is { } requestedId)
        {
            chooserUseRequestedId = null;
            Use?.Invoke(requestedId);
            ImGui.CloseCurrentPopup();
        }

        // Drawn here (nested inside the chooser's own popup scope), not from Draw's outer call,
        // whenever a row's context menu requested one (see Draw for the full explanation). This is
        // what makes ImGui.OpenPopup() register Rename/Delete one level above the chooser instead of
        // at the same level, so opening either one stacks on top of the chooser instead of silently
        // closing it.
        DrawTemplateRenamePopup();
        DrawTemplateDeletePopup();

        ImGui.EndPopup();
    }

    private void DrawTemplateChooserLeftPane()
    {
        ImGui.TextDisabled("START");
        ImGui.Spacing();

        DrawTemplateChooserRow(BuiltInTemplateCatalog.AdventurePlateClassicId, "Adventure Plate Classic", "Basic Editor", isBuiltIn: true);
        DrawTemplateChooserRow(BuiltInTemplateCatalog.BlankCanvasId, "Blank Canvas", "Advanced Editor", isBuiltIn: true);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("MY TEMPLATES");
        ImGui.Spacing();

        var userTemplates = templates.GetOrderedTemplates().Where(t => t.Kind == TemplateKind.UserSaved).ToList();
        if (userTemplates.Count == 0)
        {
            using (ImRaii.TextWrapPos(0f))
            {
                ImGui.TextDisabled(templates.LoadFailed ? UserTemplatesUnavailableText
                    : !templates.IsLoaded ? "Loading your Templates..."
                    : "You haven't saved any Templates yet.");
            }
        }
        else
        {
            foreach (var template in userTemplates)
            {
                DrawTemplateChooserRow(template.TemplateId, template.DisplayName, null, isBuiltIn: false);
            }
        }
    }

    /// <summary>
    /// One full-width, fully-clickable row. Uses <see cref="ImGui.Selectable"/> (not a radio
    /// button) purely for its built-in, AetherFrame-accent-tinted highlight: the label itself is
    /// drawn over it, the same "invisible interactive widget plus manual text" technique the
    /// Template/Plate card grids already use. Double-clicking a row uses that Template immediately,
    /// mirroring the same established convention the My Plates and Manage Templates card grids
    /// already use for their own primary action. Right-clicking opens a context menu (see
    /// <see cref="DrawUserTemplateContextMenuItems"/>/<see cref="DrawBuiltInTemplateContextMenuItems"/>).
    /// </summary>
    private void DrawTemplateChooserRow(Guid templateId, string label, string? secondaryText, bool isBuiltIn)
    {
        var selected = chosenTemplateId == templateId;
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var rowHeight = (secondaryText is null ? lineHeight : lineHeight * 2f) + (4f * ImGuiHelpers.GlobalScale);
        var rowId = $"##ChooserRow{templateId:N}";

        using (ImRaii.PushColor(ImGuiCol.Header, EditorWidgets.AccentColor with { W = 0.35f }))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, EditorWidgets.AccentColor with { W = 0.18f }))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, EditorWidgets.AccentColor with { W = 0.45f }))
        {
            if (ImGui.Selectable(rowId, selected, ImGuiSelectableFlags.None, new Vector2(0f, rowHeight)))
            {
                chosenTemplateId = templateId;
            }
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !IsBusy && CanUse(templateId))
        {
            chosenTemplateId = templateId;
            Use?.Invoke(templateId);
            ImGui.CloseCurrentPopup();
        }

        var contextMenuId = $"##ChooserRowMenu{templateId:N}";
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            chosenTemplateId = templateId;
            ImGui.OpenPopup(contextMenuId);
        }

        if (ImGui.BeginPopup(contextMenuId))
        {
            if (isBuiltIn)
            {
                DrawBuiltInTemplateContextMenuItems(templateId);
            }
            else
            {
                DrawUserTemplateContextMenuItems(templateId, label);
            }

            ImGui.EndPopup();
        }

        var min = ImGui.GetItemRectMin();
        var padding = new Vector2(6f, 3f) * ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddText(min + padding, ImGui.GetColorU32(ImGuiCol.Text), label);
        if (secondaryText is not null)
        {
            drawList.AddText(min + padding + new Vector2(0f, lineHeight), ImGui.GetColorU32(EditorWidgets.DimTextColor), secondaryText);
        }
    }

    /// <summary>
    /// The user Template context menu: Use Template, Rename, Duplicate, then Delete Template, the
    /// one reusable set of actions any surface listing user Templates can share (currently the
    /// Create Plate chooser's rows; Manage Templates' action bar already exposes the same actions
    /// as buttons). No Preview entry: selecting the row already shows its preview in the chooser's
    /// right pane. Rename/Delete defer to the existing popups (same pending-flag pattern Manage
    /// Templates' own buttons use) so confirmation, name validation, and duplicate-name policy are
    /// never reimplemented, and Use Template defers to the chooser, which closes as for its own
    /// button. Must be called between a matching BeginPopup/EndPopup.
    /// </summary>
    private void DrawUserTemplateContextMenuItems(Guid templateId, string displayName)
    {
        DrawUseTemplateMenuItem(templateId);

        if (ImGui.MenuItem("Rename"))
        {
            RequestRename(templateId, displayName);
        }

        if (ImGui.MenuItem("Duplicate"))
        {
            actions.DuplicateTemplate(templateId, newId =>
            {
                chosenTemplateId = newId;
                TemplateDuplicated?.Invoke(newId);
            });
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Delete Template"))
        {
            RequestDelete(templateId);
        }
    }

    /// <summary>Built-ins are immutable: their context menu (right-click is optional for them;
    /// left-click plus Use Template already covers the common case) offers only Use Template,
    /// never Rename/Duplicate/Delete.</summary>
    private void DrawBuiltInTemplateContextMenuItems(Guid templateId) => DrawUseTemplateMenuItem(templateId);

    /// <summary>
    /// A row menu's Use Template: available when the chooser's button is, and asking the chooser to
    /// use the Template, so the chooser closes with the menu (the menu item closes only the menu).
    /// </summary>
    private void DrawUseTemplateMenuItem(Guid templateId)
    {
        var problem = actions.TemplateProblem(templateId);
        using (ImRaii.Disabled(IsBusy || problem is not null))
        {
            if (ImGui.MenuItem("Use Template"))
            {
                chosenTemplateId = templateId;
                chooserUseRequestedId = templateId;
            }
        }

        EditorWidgets.Tooltip(problem);
    }

    private void DrawTemplateChooserRightPane()
    {
        var selection = ResolveChooserSelection();
        if (selection is null)
        {
            ImGui.TextDisabled("Select a Template.");
            return;
        }

        EnsureChooserPreviewFresh(selection.Value);

        ImGui.TextUnformatted(selection.Value.Name);
        ImGui.TextColored(EditorWidgets.DimTextColor, selection.Value.Destination);
        ImGui.Spacing();
        ImGui.TextWrapped(selection.Value.Description);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (!selection.Value.SupportsPreview)
        {
            // Deliberately no rendered preview here: Blank Canvas has nothing to show; a fake
            // stand-in would just be noise. Its destination and description above are enough.
            // A Template that can't be used has none either, and its description says why.
            ImGui.TextDisabled(chosenTemplateId == BuiltInTemplateCatalog.BlankCanvasId ? NoPreviewText : "No preview.");
            return;
        }

        if (chooserPreviewDocument is not { } document)
        {
            ImGui.TextDisabled("Preview unavailable.");
            return;
        }

        var available = ImGui.GetContentRegionAvail();
        if (available.X < 1f || available.Y < 1f || document.CanvasWidth <= 0f || document.CanvasHeight <= 0f)
        {
            return;
        }

        // Fits the Plate's visual bounds: its canvas plus any intentional Component overflow.
        var fit = PlateViewFit.Fit(available, ProfileVisualBounds.Compute(document));
        if (fit.Scale <= 0f)
        {
            return;
        }

        var canvasOrigin = ImGui.GetCursorScreenPos() + fit.CanvasOffset;
        ImGui.Dummy(available);

        var drawList = ImGui.GetWindowDrawList();
        ProfileRenderer.Draw(drawList, document, canvasOrigin, fit.Scale, renderResources, ProfileRenderOptions.Finished);
    }

    private void DrawTemplateChooserFooter()
    {
        var character = characterIdentity.CurrentCharacter;
        ImGui.TextColored(EditorWidgets.DimTextColor, character is not null
            ? MyPlatesCharacterText.NewPlateBelongsToCurrent
            : "No character is logged in, so the new Plate won't belong to a character yet.");

        ImGui.Spacing();

        // Left side: the quiet door into Manage Templates, not the common path through this popup
        // (Manage Templates is a mode of My Plates, entered from here, never a permanent tab).
        if (ManageTemplates is { } manageTemplates)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, EditorWidgets.DimTextColor))
            {
                if (ImGui.Selectable("Manage Templates...", false, ImGuiSelectableFlags.None, new Vector2(ImGui.CalcTextSize("Manage Templates...").X, 0f)))
                {
                    manageTemplates();
                    ImGui.CloseCurrentPopup();
                }
            }

            EditorWidgets.Tooltip("Preview, rename, duplicate, or delete your saved Templates.");
        }

        if (CreateStepByStep is { } stepByStep)
        {
            if (ManageTemplates is not null)
            {
                ImGui.SameLine();
            }

            if (AetherControls.SecondaryButton("Create Step by Step..."))
            {
                stepByStep();
                ImGui.CloseCurrentPopup();
            }

            EditorWidgets.Tooltip("Always a new Plate, in three short steps: your look, name, portrait and message. The easiest start.");
        }

        // Right side: Cancel, then Use Template as the primary (accent-colored) action; on a row of
        // their own when the left side leaves no room for them.
        var buttonSize = new Vector2(130f, 0f) * ImGuiHelpers.GlobalScale;
        var rightWidth = (buttonSize.X * 2f) + ImGui.GetStyle().ItemSpacing.X;
        if (ManageTemplates is not null || CreateStepByStep is not null)
        {
            AetherControls.AlignRightAfterItem(rightWidth);
        }
        else
        {
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - rightWidth));
        }

        if (ImGui.Button("Cancel", buttonSize) || PopupEscapeGuard.CancelsPrompt())
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        var problem = actions.TemplateProblem(chosenTemplateId);
        using (ImRaii.PushColor(ImGuiCol.Button, EditorWidgets.AccentColor))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, EditorWidgets.AccentColor with { W = 0.85f }))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, EditorWidgets.AccentColor with { W = 0.7f }))
        using (ImRaii.Disabled(IsBusy || problem is not null))
        {
            if (ImGui.Button("Use Template", buttonSize))
            {
                Use?.Invoke(chosenTemplateId);
                ImGui.CloseCurrentPopup();
            }
        }

        EditorWidgets.Tooltip(problem);
    }

    private readonly record struct ChooserSelection(string Name, string Description, string Destination, bool SupportsPreview);

    private ChooserSelection? ResolveChooserSelection()
    {
        if (BuiltInTemplateCatalog.Find(chosenTemplateId) is { } builtIn)
        {
            var destination = builtIn.TemplateId == BuiltInTemplateCatalog.AdventurePlateClassicId ? "Basic Editor" : "Advanced Editor";
            return new ChooserSelection(builtIn.Name, builtIn.Description, destination, builtIn.SupportsPreview);
        }

        var summary = templates.FindTemplate(chosenTemplateId);
        if (summary is null)
        {
            return null;
        }

        if (!summary.IsReady)
        {
            return new ChooserSelection(summary.DisplayName, summary.Problem ?? PlateActions.CannotUseTemplateNote, string.Empty, false);
        }

        var document = templates.GetSavedDocument(chosenTemplateId);
        var destinationText = EditorSurfaceChooser.ForDocument(document) == EditorSurfaceKind.Basic ? "Basic Editor" : "Advanced Editor";
        return new ChooserSelection(summary.DisplayName, $"Saved {summary.ModifiedUtc.ToLocalTime():g}.", destinationText, summary.SupportsPreview);
    }

    /// <summary>Regenerates the right pane's cached preview document only when the selected
    /// Template id actually changed since the last draw (see the field doc comment).</summary>
    private void EnsureChooserPreviewFresh(ChooserSelection selection)
    {
        if (chooserPreviewedTemplateId == chosenTemplateId)
        {
            return;
        }

        chooserPreviewedTemplateId = chosenTemplateId;
        chooserPreviewDocument = selection.SupportsPreview
            ? templates.GetSavedDocument(chosenTemplateId, new PlateStarterContent(characterIdentity.CurrentInfo))
            : null;
    }

    // ---------------------------------------------------------------- Template rename

    private void DrawTemplateRenamePopup()
    {
        if (pendingTemplateRenamePopup)
        {
            ImGui.OpenPopup(TemplateRenamePopupId);
            pendingTemplateRenamePopup = false;
        }

        if (!ImGui.BeginPopupModal(TemplateRenamePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(EditorWidgets.Scaled(300f));
        var submitted = ImGui.InputText("##RenameTemplateName", ref templateRenameBuffer, TemplateNaming.MaxNameLength, ImGuiInputTextFlags.EnterReturnsTrue);

        if (templateRenameError is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.Button("Rename", EditorWidgets.Scaled(new Vector2(110f, 0f))) || submitted)
            {
                templateRenameError = actions.RenameTemplate(templateRenameTargetId, templateRenameBuffer);
                if (templateRenameError is null)
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

    // ---------------------------------------------------------------- Template delete

    private void DrawTemplateDeletePopup()
    {
        if (pendingTemplateDeletePopup)
        {
            ImGui.OpenPopup(TemplateDeletePopupId);
            pendingTemplateDeletePopup = false;
        }

        if (!ImGui.BeginPopupModal(TemplateDeletePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        var template = templates.FindTemplate(templateDeleteTargetId);
        if (template is null || template.IsBuiltIn)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Delete \"{template.DisplayName}\"?");
        EditorWidgets.Hint("It's moved to AetherFrame's Template trash folder, not destroyed. Its images are kept.");
        ImGui.Spacing();

        using (ImRaii.Disabled(IsBusy))
        {
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.6f, 0.18f, 0.18f, 1f)))
            {
                if (ImGui.Button("Delete", EditorWidgets.Scaled(new Vector2(110f, 0f))))
                {
                    var templateId = template.TemplateId;
                    actions.DeleteTemplate(templateId, template.DisplayName, () => TemplateDeleted?.Invoke(templateId));
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
}
