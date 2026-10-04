using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Theme;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Tutorial;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The editors' Plate menu (interface task 1): the control in the shared action bar that opens
/// it, its items and prompts (<see cref="PlateMenu"/>'s), and the line under the bar where its
/// results and errors show, under the save state. The control always shows the Plate icon and the
/// menu caret, and the Plate's name when there's room, so it stays reachable at every window width
/// and UI scale.
///
/// <para>One instance serves both editors, like <see cref="EditorDocumentCommands"/>, so an action
/// started in one finishes wherever the Plate is being edited. Its Export file dialog is its own,
/// drawn once a frame by whichever editor is open (<see cref="DrawFrame"/>); when no editor draws
/// one frame, <see cref="EndFrame"/> closes a dialog left open, and still applies finished actions.
/// So are its Open another Plate and New Plate... (interface task 2): the menu's switcher, and its
/// Create Plate chooser, which the open editor draws over itself.</para>
/// </summary>
internal sealed class EditorPlateMenu
{
    private const string PopupId = "##AetherFramePlateMenu";

    // Below this width (unscaled) the name isn't worth showing: the icon and caret carry the control.
    private const float MinimumNameWidth = 24f;

    // How long a result stays under the bar; an error stays until the next action.
    private const double ResultSeconds = 10.0;

    private const string ControlTooltip = "Plate menu: View, Set Active, Save as New Plate, Save as Template, Export, Rename,\nOpen another Plate, New Plate";

    private readonly PlateMenu menu;
    private readonly PlateLibraryService library;
    private readonly CharacterIdentityService characterIdentity;
    private readonly EditorDocumentCommands commands;
    private readonly FileDialogManager fileDialogs;
    private readonly Action<Guid> view;

    private int lastDrawnFrame = -1;

    private int shownMessageVersion = -1;
    private Guid? messagePlateId;
    private double messageShownAt;

    /// <param name="menu">The shared Plate menu, over this instance's own runner and <paramref name="fileDialogs"/>.</param>
    /// <param name="library">The open Plate as My Plates lists it, and its character's Active Plate.</param>
    /// <param name="characterIdentity">The logged-in character, if any (Set Active needs one).</param>
    /// <param name="commands">Whether the open Plate has unsaved changes, or is being saved.</param>
    /// <param name="fileDialogs">Export's file dialog, drawn by <see cref="DrawFrame"/>.</param>
    /// <param name="view">Shows a Plate in the Plate Viewer (the open one shows its live document).</param>
    internal EditorPlateMenu(
        PlateMenu menu, PlateLibraryService library, CharacterIdentityService characterIdentity, EditorDocumentCommands commands, FileDialogManager fileDialogs, Action<Guid> view)
    {
        this.menu = menu;
        this.library = library;
        this.characterIdentity = characterIdentity;
        this.commands = commands;
        this.fileDialogs = fileDialogs;
        this.view = view;
    }

    internal PlateMenu Menu => menu;

    /// <summary>Whether the Create Plate chooser, opened by New Plate..., was on screen this frame or the one before (the tutorial reads it).</summary>
    internal bool TemplateChooserShowing => menu.Chooser.Showing;

    /// <summary>Shows the Plate with <paramref name="plateId"/> in the Plate Viewer: the open one shows its live document.</summary>
    internal void View(Guid plateId) => view(plateId);

    /// <summary>
    /// Applies a finished action, opens a Plate waiting to open (Open another Plate, New Plate,
    /// or either once the unsaved-changes question is answered), and draws the Export file dialog,
    /// once a frame whichever editor calls it. Call at the start of an editor's Draw, before it
    /// reads the open Plate, so an action that opens another Plate takes effect before anything is
    /// drawn.
    /// </summary>
    internal void DrawFrame()
    {
        var frame = ImGui.GetFrameCount();
        if (frame == lastDrawnFrame)
        {
            return;
        }

        lastDrawnFrame = frame;
        menu.Runner.Advance();
        menu.AdvanceOpenGuard();

        // Hidden while the eyedropper picks (issue #120), and back as it was once the pick ends: it
        // is Dalamud's window, not AetherFrame's, so on another monitor a pick's click would reach
        // it. The frame still counts as drawn, so EndFrame doesn't close it.
        if (!ScreenEyedropper.ClaimsInput)
        {
            fileDialogs.Draw();
        }
    }

    /// <summary>
    /// After every window has drawn: when no editor drew this frame, applies a finished action
    /// anyway, opens a Plate waiting to open, and closes an Export dialog its editor left open.
    /// Call once a frame.
    /// </summary>
    internal void EndFrame()
    {
        var frame = ImGui.GetFrameCount();
        if (frame == lastDrawnFrame)
        {
            return;
        }

        menu.Runner.Advance();
        menu.AdvanceOpenGuard();
        if (lastDrawnFrame == frame - 1)
        {
            fileDialogs.Reset();
        }
    }

    /// <summary>The control's width without the name: padding, the Plate icon, the gap and the caret.</summary>
    internal float MinimumWidth()
    {
        var (icon, caret) = IconSizes();
        return (ImGui.GetStyle().FramePadding.X * 2f) + icon.X + Gap + caret.X;
    }

    /// <summary>
    /// Draws the control, with the Plate's name fitted into <paramref name="nameRoom"/> (left out
    /// below a usable width), and the menu it opens.
    /// </summary>
    internal void DrawControl(ProfileDocument profile, float nameRoom)
    {
        var style = ImGui.GetStyle();
        var gap = Gap;
        var (iconSize, caretSize) = IconSizes();
        var name = nameRoom - gap >= MinimumNameWidth * ImGuiHelpers.GlobalScale ? FitText(profile.Name, nameRoom - gap) : string.Empty;
        var nameWidth = name.Length == 0 ? 0f : ImGui.CalcTextSize(name).X + gap;
        var size = new Vector2((style.FramePadding.X * 2f) + iconSize.X + gap + nameWidth + caretSize.X, ImGui.GetFrameHeight());

        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, AetherPalette.SurfaceHover))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, AetherPalette.SurfaceActive))
        {
            clicked = ImGui.Button("##PlateMenuControl", size);
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();
        TutorialAnchorMarks.Mark(TutorialTarget.EditorPlateMenu);

        var drawList = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(AetherPalette.TextPrimary);
        var x = min.X + style.FramePadding.X;
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            drawList.AddText(new Vector2(x, Middle(min, max, iconSize)), ImGui.GetColorU32(AetherPalette.TextSecondary), EditorWidgets.GetIconString(FontAwesomeIcon.IdCard));
            drawList.AddText(new Vector2(max.X - style.FramePadding.X - caretSize.X, Middle(min, max, caretSize)), ImGui.GetColorU32(AetherPalette.TextSecondary), EditorWidgets.GetIconString(FontAwesomeIcon.CaretDown));
        }

        if (name.Length > 0)
        {
            // Player text: drawn as it is, never as a format string or a label.
            drawList.AddText(new Vector2(x + iconSize.X + gap, Middle(min, max, ImGui.CalcTextSize(name))), color, name);
        }

        if (hovered && !ImGui.IsPopupOpen(PopupId))
        {
            using (ImRaii.Tooltip())
            {
                if (name != profile.Name)
                {
                    ImGui.TextUnformatted(profile.Name);
                }

                ImGui.TextUnformatted(ControlTooltip);
            }
        }

        if (clicked)
        {
            ImGui.OpenPopup(PopupId);
        }

        ImGui.SetNextWindowPos(new Vector2(min.X, max.Y), ImGuiCond.Appearing);
        using var popup = ImRaii.Popup(PopupId);
        if (!popup.Success)
        {
            return;
        }

        if (library.FindPlate(profile.ProfileId) is not { } plate)
        {
            ImGui.TextDisabled("This Plate isn't in My Plates.");
            return;
        }

        var character = characterIdentity.CurrentCharacter;
        var activePlateId = character is { } who ? library.GetActivePlateId(who.ContentId) : null;
        menu.DrawEditorItems(plate, character, activePlateId, commands.IsDirty, commands.IsSaving, view);
    }

    /// <summary>The menu's prompts. Call once per frame from the editor's outermost scope.</summary>
    internal void DrawPopups() => menu.DrawPopups(characterIdentity.CurrentCharacter);

    /// <summary>
    /// The last action's result or error, on its own line under the bar and aligned under the save
    /// state, for the Plate it concerns: a result for ten seconds, an error until the next action.
    /// </summary>
    internal void DrawResult(Guid openPlateId)
    {
        var runner = menu.Runner;
        var error = runner.Error;
        var text = error ?? runner.Status;
        if (text is null)
        {
            return;
        }

        var now = ImGui.GetTime();
        if (runner.MessageVersion != shownMessageVersion)
        {
            shownMessageVersion = runner.MessageVersion;
            messagePlateId = openPlateId;
            messageShownAt = now;
        }

        if (messagePlateId != openPlateId)
        {
            return;
        }

        if (error is null && now - messageShownAt > ResultSeconds)
        {
            runner.Status = null;
            shownMessageVersion = runner.MessageVersion;
            return;
        }

        // Right-aligned when it fits, like the save state above it; from the left when it doesn't.
        float iconWidth;
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            iconWidth = ImGui.CalcTextSize(EditorWidgets.GetIconString(FontAwesomeIcon.CheckCircle)).X;
        }

        var width = iconWidth + (AetherMetrics.ItemInnerSpacing * ImGuiHelpers.GlobalScale) + ImGui.CalcTextSize(text).X;
        var rowEnd = ImGui.GetWindowContentRegionMax().X;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), rowEnd - width));
        AetherControls.StatusLine(error is null ? AetherTone.Success : AetherTone.Danger, text);
    }

    private static float Gap => AetherMetrics.ItemInnerSpacing * ImGuiHelpers.GlobalScale;

    private static (Vector2 Icon, Vector2 Caret) IconSizes()
    {
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            return (ImGui.CalcTextSize(EditorWidgets.GetIconString(FontAwesomeIcon.IdCard)), ImGui.CalcTextSize(EditorWidgets.GetIconString(FontAwesomeIcon.CaretDown)));
        }
    }

    private static float Middle(Vector2 min, Vector2 max, Vector2 size) => min.Y + ((max.Y - min.Y - size.Y) / 2f);

    /// <summary>The text, shortened with an ellipsis when it's wider than <paramref name="width"/>.</summary>
    private static string FitText(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
        {
            return text;
        }

        const string ellipsis = "...";
        var length = text.Length;
        while (length > 0 && ImGui.CalcTextSize(string.Concat(text.AsSpan(0, length), ellipsis)).X > width)
        {
            length--;
        }

        return length == 0 ? string.Empty : string.Concat(text.AsSpan(0, length), ellipsis);
    }
}
