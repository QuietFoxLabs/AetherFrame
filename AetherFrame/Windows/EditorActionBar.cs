using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Tutorial;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The persistent top action bar both editors share, so Basic and Advanced read as two modes of
/// one editor: the same actions, with the same words, in the same places.
///
/// <code>
/// [My Plates] [Basic|Advanced] [Plate name v]   [Undo][Redo]      Unsaved changes [Preview] [Revert] [Save]
///                                                                  Exported "Evening Look" to Evening Look.aetherframe.
/// </code>
///
/// It is drawn at the top of the window, outside every scrolling region, so it stays in view while
/// the editor's content scrolls. The Plate's name opens the Plate menu (<see cref="EditorPlateMenu"/>),
/// whose control keeps its icon and caret at every width; the name shows in whatever room is left,
/// and the menu's results and errors show on a line of their own under the save state. When the
/// window is too narrow for one row, the save state and its buttons move to a second row.
/// Availability comes from <see cref="EditorDocumentCommands"/>: Save
/// and Revert only with unsaved changes, Undo and Redo only when there's something to undo or redo.
/// Revert always asks first (and the revert itself can be undone). Everything an editor mode adds
/// of its own (Advanced's + Text, Guides...) goes on its own row below this one.
/// </summary>
internal sealed class EditorActionBar
{
    private const string RevertPopupId = "Revert to Saved##AetherFrameRevert";
    private const string PreviewLabel = "Preview";
    private const string RevertLabel = "Revert";
    private const string SaveLabel = "Save";
    private const string SavingText = "Saving...";
    private const string UnsavedText = "Unsaved changes";
    private const string SavedText = "Saved";
    private const string RetryLabel = "Retry now";

    private static readonly Vector4 SavingColor = new(0.85f, 0.85f, 0.4f, 1f);

    private readonly EditorDocumentCommands commands;
    private readonly EditorSurfaceKind mode;
    private readonly Action openMyPlates;
    private readonly Action switchMode;
    private readonly Func<HelpMenu?> help;
    private readonly EditorPlateMenu plateMenu;

    // Requested from the bar, opened at window level (one id-stack scope, see ProfileEditorWindow).
    private bool pendingRevertPrompt;

    /// <param name="commands">The shared document actions.</param>
    /// <param name="mode">Which editor this bar belongs to (its half of the Basic / Advanced switch is highlighted).</param>
    /// <param name="openMyPlates">Opens My Plates, or brings it forward when it's already open.</param>
    /// <param name="switchMode">Hands the open Plate to the other editor mode.</param>
    /// <param name="help">The Help menu, once the plugin has attached it to the window (null before that).</param>
    /// <param name="plateMenu">The Plate menu both editors share.</param>
    internal EditorActionBar(EditorDocumentCommands commands, EditorSurfaceKind mode, Action openMyPlates, Action switchMode, Func<HelpMenu?> help, EditorPlateMenu plateMenu)
    {
        this.commands = commands;
        this.mode = mode;
        this.openMyPlates = openMyPlates;
        this.switchMode = switchMode;
        this.help = help;
        this.plateMenu = plateMenu;
    }

    /// <summary>The Help menu to draw at the bar's right edge, or null when the plugin hasn't attached one.</summary>
    private HelpMenu? Help => help();

    internal EditorDocumentCommands Commands => commands;

    internal EditorPlateMenu PlateMenu => plateMenu;

    /// <summary>
    /// Whether Save, while it can save, is the screen's one primary (accent-filled) button: the Basic
    /// editor's Simple view sets it. Otherwise Save is tinted like a selected toggle, as before.
    /// </summary>
    internal bool EmphasizeSave { get; set; }

    /// <summary>Asks to revert to the last saved version (confirmed by <see cref="DrawPopups"/>); ignored when there's nothing to revert.</summary>
    internal void RequestRevert()
    {
        if (commands.CanRevert)
        {
            pendingRevertPrompt = true;
        }
    }

    /// <summary>
    /// Draws the bar row, then the Plate menu's last result (if any) and <paramref name="errorMessage"/>
    /// (if any), each on its own line.
    /// </summary>
    /// <param name="profile">The open Plate.</param>
    /// <param name="showPreview">What Preview does in this editor.</param>
    /// <param name="previewTooltip">What Preview shows, in this editor's words.</param>
    /// <param name="errorMessage">The editor's current error, if any.</param>
    internal void Draw(ProfileDocument profile, Action showPreview, string previewTooltip, string? errorMessage)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        var gap = 12f * scale;

        // ---- left: My Plates and the mode switch
        if (EditorWidgets.IconButton("MyPlates", FontAwesomeIcon.ThLarge, "My Plates"))
        {
            openMyPlates();
        }

        TutorialAnchorMarks.Mark(TutorialTarget.EditorMyPlates);

        ImGui.SameLine();
        var modeSwitchMin = ImGui.GetCursorScreenPos();
        DrawModeSwitch();
        TutorialAnchorMarks.MarkRect(TutorialTarget.EditorModeSwitch, modeSwitchMin, ImGui.GetItemRectMax());
        var leftEnd = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;

        // ---- measure the Plate menu's control and the other two groups
        var controlStart = leftEnd + gap;
        var controlMinimum = plateMenu.MinimumWidth();
        var frame = ImGui.GetFrameHeight();
        const float historyGap = 2f;
        var centerWidth = (frame * 2f) + historyGap;

        var (stateText, stateColor) = SaveState();
        var recovery = commands.RecoveryIndicator;

        // The recovery checkpoint's mark beside the save state: always measured, so the bar never shifts when it appears.
        var buttonsWidth = RecoveryMarkWidth + ButtonWidth(PreviewLabel) + ButtonWidth(RevertLabel) + ButtonWidth(SaveLabel)
            + (style.ItemSpacing.X * 3f)
            + (Help is null ? 0f : HelpMenu.ButtonWidth + style.ItemSpacing.X);
        var widestState = WidestSaveState();

        var rows = EditorActionBarLayout.ArrangeRows(
            ImGui.GetWindowContentRegionMin().X, ImGui.GetWindowContentRegionMax().X, controlStart, controlMinimum, centerWidth,
            ImGui.CalcTextSize(stateText).X + buttonsWidth, widestState + buttonsWidth, gap);

        // ---- the Plate menu, always drawn, with the Plate's name in whatever room is left
        ImGui.SameLine(controlStart);
        plateMenu.DrawControl(profile, rows.NameRoom);

        // ---- center: history
        ImGui.SameLine(rows.CenterX);
        var historyMin = ImGui.GetCursorScreenPos();
        using (ImRaii.Disabled(!commands.CanUndo))
        {
            if (EditorWidgets.IconButton("Undo", FontAwesomeIcon.Undo, "Undo (Ctrl+Z)"))
            {
                commands.Undo();
            }
        }

        ImGui.SameLine(0f, historyGap);
        using (ImRaii.Disabled(!commands.CanRedo))
        {
            if (EditorWidgets.IconButton("Redo", FontAwesomeIcon.Redo, "Redo (Ctrl+Y)"))
            {
                commands.Redo();
            }
        }

        TutorialAnchorMarks.MarkRect(TutorialTarget.EditorHistory, historyMin, ImGui.GetItemRectMax());

        // ---- right: save state, Preview, Revert, Save, Help; on a row of their own when one row can't
        // hold everything, so none of them is ever cut off
        if (rows.TwoRows)
        {
            ImGui.SetCursorPosX(rows.RightX);
        }
        else
        {
            ImGui.SameLine(rows.RightX);
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(stateColor, stateText);
        TutorialAnchorMarks.Mark(TutorialTarget.EditorSaveState);
        DrawRecoveryMark(recovery);

        ImGui.SameLine();
        if (ImGui.Button(PreviewLabel))
        {
            showPreview();
        }

        EditorWidgets.Tooltip(previewTooltip);
        TutorialAnchorMarks.Mark(TutorialTarget.EditorPreview);

        ImGui.SameLine();
        using (ImRaii.Disabled(!commands.CanRevert))
        {
            if (ImGui.Button(RevertLabel))
            {
                RequestRevert();
            }
        }

        TutorialAnchorMarks.Mark(TutorialTarget.EditorRevert);
        EditorWidgets.Tooltip("Discard unsaved changes and go back to the last saved version. Asks first.");

        ImGui.SameLine();
        var canSave = commands.CanSave;
        var emphasized = canSave && EmphasizeSave;
        using (ImRaii.Disabled(!canSave))
        using (ImRaii.PushColor(ImGuiCol.Button, EditorWidgets.ActiveToggleColor, canSave && !emphasized))
        {
            if (emphasized ? AetherControls.PrimaryButton(SaveLabel) : ImGui.Button(SaveLabel))
            {
                commands.Save();
            }
        }

        TutorialAnchorMarks.Mark(TutorialTarget.EditorSave);
        EditorWidgets.Tooltip("Save (Ctrl+S)");

        if (Help is { } help)
        {
            ImGui.SameLine();
            help.DrawButton("EditorHelp");
        }

        plateMenu.DrawResult(profile.ProfileId);
        DrawRecoveryWarning(recovery);

        // Wrapped at the window's edge, so a long reason (a save the system refused, say) stays readable in a narrow window.
        if (errorMessage is { } error)
        {
            using (ImRaii.TextWrapPos(0f))
            {
                ImGui.TextColored(EditorWidgets.ErrorColor, error);
            }
        }
    }

    /// <summary>The bar's popups: Revert's confirmation, and the Plate menu's prompts. Call once per frame from the window's outermost scope.</summary>
    internal void DrawPopups()
    {
        plateMenu.DrawPopups();

        if (pendingRevertPrompt)
        {
            pendingRevertPrompt = false;
            ImGui.OpenPopup(RevertPopupId);
        }

        using var popup = ImRaii.PopupModal(RevertPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);
        if (!popup.Success)
        {
            return;
        }

        ImGui.TextUnformatted("Revert this Plate to its last saved version?");
        EditorWidgets.Hint("All unsaved changes will be discarded. You can still undo the revert.");
        ImGui.Spacing();

        var buttonSize = new Vector2(120f * ImGuiHelpers.GlobalScale, 0f);
        if (AetherControls.DangerButton("Revert", buttonSize))
        {
            commands.Revert();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (AetherControls.GhostButton("Cancel", buttonSize) || PopupEscapeGuard.CancelsPrompt())
        {
            ImGui.CloseCurrentPopup();
        }
    }

    /// <summary>Basic | Advanced: the current mode highlighted; the other hands this same Plate over.</summary>
    private void DrawModeSwitch()
    {
        using var id = ImRaii.PushId("EditorMode");
        ModeButton(EditorSurfaceKind.Basic, "Basic", "Basic Editor: guided editing with familiar Adventure Plate sections.");
        ImGui.SameLine(0f, 0f);
        ModeButton(EditorSurfaceKind.Advanced, "Advanced", "Advanced Editor: freeform positioning, layering and every property.");

        void ModeButton(EditorSurfaceKind kind, string label, string description)
        {
            var current = kind == mode;
            if (EditorWidgets.TextToggle(label, current, tooltip: current
                    ? $"{description}\nYou're editing here now."
                    : $"{description}\nSwitch to it: your unsaved changes and undo history come along.")
                && !current)
            {
                switchMode();
            }
        }
    }

    /// <summary>
    /// The room the recovery mark takes after the save state, with the spacing before what follows it:
    /// the same whether the mark shows or not, and nothing where recovery doesn't run. Guided creation's
    /// header reserves it too.
    /// </summary>
    internal float RecoveryMarkWidth => commands.Recovery is null ? 0f : ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;

    /// <summary>
    /// A small shield after the save state while there are unsaved changes: muted once the last recovery
    /// checkpoint holds them, dimmer while newer changes wait for one, the warning colour while writing
    /// fails. Its tooltip names the last checkpoint that finished. Takes its room even when hidden.
    /// </summary>
    private void DrawRecoveryMark(RecoveryIndicator recovery)
    {
        if (commands.Recovery is null)
        {
            return;
        }

        ImGui.SameLine();
        DrawRecoveryMarkHere(recovery);
    }

    /// <summary>
    /// The recovery mark where the cursor stands (<see cref="DrawRecoveryMark"/>), in its frame-height
    /// room shown or hidden; the next <c>SameLine</c> follows that room. Nothing where recovery doesn't run.
    /// </summary>
    internal void DrawRecoveryMarkHere(RecoveryIndicator recovery)
    {
        if (commands.Recovery is null)
        {
            return;
        }

        var start = ImGui.GetCursorPosX();
        var room = ImGui.GetFrameHeight();
        if (recovery.Kind == RecoveryIndicatorKind.None)
        {
            ImGui.Dummy(new Vector2(room, ImGui.GetFontSize()));
            return;
        }

        var color = recovery.Kind switch
        {
            RecoveryIndicatorKind.Protected => EditorWidgets.DimTextColor,
            RecoveryIndicatorKind.Failing => EditorWidgets.WarningColor,
            _ => EditorWidgets.DimTextColor with { W = 0.45f },
        };
        EditorWidgets.IconText(FontAwesomeIcon.ShieldAlt, color);
        EditorWidgets.Tooltip(EditorDocumentCommands.RecoveryText(recovery));

        // The same room shown or hidden (the frame height the bar measured for it), so nothing after it moves.
        ImGui.SameLine(start + room, 0f);
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>
    /// While recovery checkpoints fail: one restrained line under the bar (or guided creation's header),
    /// with Retry now at its right. In a window too narrow for both on one line the text wraps beside
    /// the button, so neither runs past the window's edge.
    /// </summary>
    internal void DrawRecoveryWarning(RecoveryIndicator recovery)
    {
        if (EditorDocumentCommands.RecoveryWarning(recovery) is not { } warning)
        {
            return;
        }

        var style = ImGui.GetStyle();
        var retryWidth = ImGui.CalcTextSize(RetryLabel).X + (style.FramePadding.X * 2f);
        var textRoom = Math.Max(ImGui.GetFontSize() * 4f, ImGui.GetContentRegionAvail().X - retryWidth - style.ItemSpacing.X);
        ImGui.AlignTextToFramePadding();
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + textRoom))
        {
            ImGui.TextColored(EditorWidgets.WarningColor, warning);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(RetryLabel + "##RecoveryRetry"))
        {
            commands.Recovery?.RetryNow();
        }

        EditorWidgets.Tooltip("Write a recovery checkpoint of the unsaved changes again now. Save still keeps them in the Plate.");
    }

    private (string Text, Vector4 Color) SaveState() =>
        commands.IsSaving ? (SavingText, SavingColor)
        : commands.IsDirty ? (UnsavedText, EditorWidgets.WarningColor)
        : (SavedText, EditorWidgets.SuccessColor with { W = 0.75f });

    private static float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2f);

    /// <summary>
    /// The save state as the bar shows it (Saving..., Unsaved changes or Saved), for a view that draws
    /// the commands its own way: guided creation's header.
    /// </summary>
    internal (string Text, Vector4 Color) CurrentSaveState => SaveState();

    /// <summary>The widest the save state gets, so what follows it never shifts as it changes.</summary>
    internal static float WidestSaveState() =>
        Math.Max(ImGui.CalcTextSize(UnsavedText).X, Math.Max(ImGui.CalcTextSize(SavingText).X, ImGui.CalcTextSize(SavedText).X));
}
