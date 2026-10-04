using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// My Plates actions: the status footer, opening Plates in an editor (behind the unsaved-changes
/// question), and the one-time Basic Editor suggestion. A card's right-click menu and every prompt
/// its actions ask are the shared <see cref="PlateMenu"/>, which the editors' Plate menu uses too;
/// Library operations run through this window's <see cref="PlateOperationRunner"/>, without
/// blocking Draw.
/// </summary>
internal sealed partial class PlateLibraryWindow
{
    private const string BasicGuidancePopupId = "New to AetherFrame?##AetherFrameBasicGuidance";

    private bool libraryLoadFailed;

    private bool IsBusy => runner.IsBusy;

    /// <summary>Shown instead of the grid when the Library failed to load at startup.</summary>
    internal void MarkLoadFailed() => libraryLoadFailed = true;

    // ---------------------------------------------------------------- status footer

    private const string ActionsHint = "Right click a Plate for actions";

    /// <summary>The middle dot between two of the footer's right-hand items.</summary>
    private const string FooterItemSeparator = "\u00b7";

    /// <summary>The loaded plugin's version, the footer's last right-hand item.</summary>
    private static readonly MyPlatesFooterItem VersionItem = MyPlatesFooterText.VersionItem(AetherFrameBuildInfo.Current);

    /// <summary>The footer's right-hand items, left to right: a new item goes before the version, which stays at the bottom right.</summary>
    private static IReadOnlyList<MyPlatesFooterItem> FooterItems() => [VersionItem];

    /// <summary>
    /// The footer as this frame draws it, measured before the grid so the grid leaves it exactly the
    /// room it needs: the status line, wrapped to the window, then the right-click hint with the
    /// right-hand items beside it, or under it when the window is too narrow for both
    /// (<see cref="MyPlatesFooterLayout"/>).
    /// </summary>
    private FooterFrame MeasureFooter()
    {
        var (status, color) = FooterStatus();
        var items = FooterItems();
        var width = ImGui.GetContentRegionAvail().X;
        var style = ImGui.GetStyle();
        var widths = new float[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            widths[i] = ImGui.CalcTextSize(items[i].Text).X;
        }

        var separator = ImGui.CalcTextSize(FooterItemSeparator).X + (style.ItemSpacing.X * 2f);
        var hintWidth = ImGui.CalcTextSize(ActionsHint).X;
        var layout = MyPlatesFooterLayout.For(width, hintWidth, MyPlatesFooterLayout.ItemsWidth(widths, separator), style.ItemSpacing.X * 2f);

        var line = ImGui.GetTextLineHeightWithSpacing();
        var statusHeight = ImGui.CalcTextSize(status, false, width).Y + style.ItemSpacing.Y;
        var rowsHeight = layout.ItemsOnOwnRow ? ImGui.CalcTextSize(ActionsHint, false, width).Y + style.ItemSpacing.Y + line : line;
        return new FooterFrame(status, color, items, layout, statusHeight + rowsHeight + EditorWidgets.Scaled(4f));
    }

    /// <summary>What's happening (busy/error/status), or the selected Plate's own info, plus a
    /// quiet reminder that actions live on each card's right-click menu (see
    /// <see cref="PlateMenu.DrawCardItems"/>), and the right-hand items (the loaded version at the
    /// bottom right); the persistent action button row is gone.</summary>
    private void DrawStatusFooter(FooterFrame footer)
    {
        FooterText(footer.Status, footer.Color);

        var rowStart = ImGui.GetCursorPosX();
        FooterText(ActionsHint, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        if (footer.Layout.ItemsOnOwnRow)
        {
            ImGui.SetCursorPosX(rowStart + footer.Layout.ItemsX);
        }
        else
        {
            ImGui.SameLine(rowStart + footer.Layout.ItemsX);
        }

        for (var i = 0; i < footer.Items.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(FooterItemSeparator);
                ImGui.SameLine();
            }

            ImGui.TextDisabled(footer.Items[i].Text);
            AetherControls.Tooltip(footer.Items[i].Tooltip);
        }
    }

    /// <summary>The footer's status line: its text and color.</summary>
    private (string Text, uint Color) FooterStatus()
    {
        var disabled = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        if (IsBusy)
        {
            return ("Working...", disabled);
        }

        if (runner.Error is { } error)
        {
            return (error, ImGui.GetColorU32(EditorWidgets.ErrorColor));
        }

        if (runner.Status is { } status)
        {
            return (status, ImGui.GetColorU32(EditorWidgets.SuccessColor with { W = 0.85f }));
        }

        if (selectedPlateId is { } id && library.FindPlate(id) is { } plate)
        {
            var openHere = profileService.OpenPlateId == plate.PlateId;
            return (plate.IsReady
                ? $"Last saved {plate.ModifiedUtc.ToLocalTime():g}{(openHere ? " · Open in the editor" : string.Empty)}"
                : plate.Problem ?? "This Plate can't be opened.", disabled);
        }

        return ("Select a Plate. Double-click to edit; drag to reorder.", disabled);
    }

    /// <summary>A footer line in <paramref name="color"/>, wrapped at the window's edge rather than cut off.</summary>
    private static void FooterText(string text, uint color)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        using (ImRaii.TextWrapPos(0f))
        {
            ImGui.TextUnformatted(text);
        }
    }

    /// <summary>The footer measured for one frame: what it says, and the height it takes.</summary>
    private readonly record struct FooterFrame(string Status, uint Color, IReadOnlyList<MyPlatesFooterItem> Items, MyPlatesFooterLayout Layout, float Height);

    // ---------------------------------------------------------------- opening Plates

    /// <summary>
    /// A double-click on a Plate: the editor already showing this Plate, if one is; otherwise the one
    /// its content suits (<see cref="EditorSurfaceChooser"/>) — Basic for an Adventure Plate layout,
    /// Advanced for Blank Canvas and freeform Plates. Judged from the open document when it's the
    /// open Plate (it may have unsaved changes), else from its saved version.
    /// </summary>
    private void RequestEdit(Guid plateId)
    {
        var isOpen = profileService.OpenPlateId == plateId;
        var kind = isOpen && activeEditor() is { } showing
            ? showing
            : EditorSurfaceChooser.ForDocument(isOpen ? profileService.CurrentProfile : library.GetSavedDocument(plateId));
        RequestOpen(plateId, kind == EditorSurfaceKind.Basic);
    }

    /// <summary>
    /// Opens a Plate in an editor. Switching away from a Plate with unsaved changes asks first
    /// (<see cref="PlateOpenGuard"/>); reopening the Plate that's already open just shows the editor.
    /// </summary>
    private void RequestOpen(Guid plateId, bool basic)
    {
        runner.Error = null;

        switch (openGuard.Request(plateId, basic))
        {
            case PlateOpenDecision.AlreadyOpen:
                ShowEditor(basic);
                break;

            case PlateOpenDecision.Ask:
                plateMenu.AskBeforeOpening();
                break;

            default:
                OpenNow(plateId, basic);
                break;
        }
    }

    /// <summary>
    /// The one-time suggestion before the first Advanced Editor, while <see cref="advancedEntry"/>
    /// holds an Advanced open back (see <see cref="ShowEditor"/>). For a Plate that suits Basic: Try
    /// Basic Editor (the suggested choice, opening this Plate in Basic) or Continue to Advanced. For
    /// a freeform Plate, which can't sensibly open in Basic: what Basic is and how to start with it,
    /// and Continue to Advanced only. Closing it always continues to Advanced, and any answer
    /// handles the guidance for good.
    /// </summary>
    private void DrawBasicGuidancePopup()
    {
        if (advancedEntry.ConsumePromptRequest())
        {
            ImGui.OpenPopup(BasicGuidancePopupId);
        }

        if (!advancedEntry.IsWaiting)
        {
            return;
        }

        var open = true;
        BasicGuidanceAnswer? answer = null;
        using (var popup = ImRaii.PopupModal(BasicGuidancePopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            if (popup.Success)
            {
                advancedEntry.MarkShown();
                var offerBasic = advancedEntry.OffersTryBasic;
                using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + (380f * ImGuiHelpers.GlobalScale)))
                {
                    ImGui.TextUnformatted("Basic Editor is the easiest place to start and uses familiar FFXIV style controls.");
                    ImGui.Spacing();
                    ImGui.TextDisabled("Advanced Editor gives you freeform positioning, layering and additional controls.");
                    if (!offerBasic)
                    {
                        ImGui.Spacing();
                        ImGui.TextUnformatted("This Plate is a freeform design, so it opens in the Advanced Editor.");
                        ImGui.TextDisabled("Basic is the recommended start for Adventure Plate layouts: choose Create Plate and pick Adventure Plate Classic.");
                    }
                }

                ImGui.Spacing();
                var buttonSize = new Vector2(170f * ImGuiHelpers.GlobalScale, 0f);
                if (offerBasic)
                {
                    using (ImRaii.PushColor(ImGuiCol.Button, EditorWidgets.ActiveToggleColor))
                    {
                        if (ImGui.Button("Try Basic Editor", buttonSize))
                        {
                            answer = BasicGuidanceAnswer.TryBasicEditor;
                            ImGui.CloseCurrentPopup();
                        }
                    }

                    ImGui.SameLine();
                }

                if (ImGui.Button("Continue to Advanced", buttonSize))
                {
                    answer = BasicGuidanceAnswer.ContinueToAdvanced;
                    ImGui.CloseCurrentPopup();
                }

                // Escape is its close button.
                if (answer is null && PopupEscapeGuard.CancelsPrompt())
                {
                    open = false;
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        // Its close button: continue to Advanced. Only once the prompt has really been on screen,
        // so a frame where it isn't open yet can never count as an answer.
        if (answer is null && !open && advancedEntry.WasShown)
        {
            answer = BasicGuidanceAnswer.Closed;
        }

        if (answer is { } given && advancedEntry.Answer(given) is { } editor)
        {
            if (editor == EditorSurfaceKind.Basic)
            {
                openBasicEditor();
            }
            else
            {
                openAdvancedEditor();
            }
        }
    }

    private void OpenNow(Guid plateId, bool basic)
    {
        try
        {
            profileService.OpenPlate(plateId);
            ShowEditor(basic);
        }
        catch (Exception ex) when (ex is PlateLibraryException or InvalidOperationException)
        {
            runner.Error = UserFacingError.Describe(ex, "That Plate couldn't be opened.");
        }
        catch (Exception ex)
        {
            runner.Error = "That Plate couldn't be opened. See the Dalamud log for details.";
            DalamudServices.Log.Error(LogPrivacy.ForLog(ex), "AetherFrame failed to open a Plate.");
        }
    }

    /// <summary>
    /// Shows the open Plate in an editor. Every way My Plates opens the Advanced Editor comes
    /// through here — Open in Advanced Editor, Edit and double-click on a Plate that opens in
    /// Advanced, Use Template (Blank Canvas and other freeform Templates), and opening after the
    /// unsaved-changes prompt — so this is where a player who has never used the Basic Editor is
    /// asked once, before their first Advanced Editor (see <see cref="BasicGuidance"/>). The only
    /// other way into Advanced, switching from the Basic Editor, needs no question: opening Basic
    /// already handled it.
    /// </summary>
    private void ShowEditor(bool basic)
    {
        if (basic)
        {
            openBasicEditor();
            return;
        }

        if (advancedEntry.TryEnterAdvanced(activeEditor() == EditorSurfaceKind.Advanced, profileService.CurrentProfile))
        {
            openAdvancedEditor();
        }
    }
}
