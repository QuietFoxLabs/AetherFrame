using System;
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

    /// <summary>What's happening (busy/error/status), or the selected Plate's own info, plus a
    /// quiet reminder that actions live on each card's right-click menu (see
    /// <see cref="PlateMenu.DrawCardItems"/>); the persistent action button row is gone.</summary>
    private void DrawStatusFooter()
    {
        var selected = selectedPlateId is { } id ? library.FindPlate(id) : null;

        if (IsBusy)
        {
            ImGui.TextDisabled("Working...");
        }
        else if (runner.Error is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }
        else if (runner.Status is { } status)
        {
            ImGui.TextColored(EditorWidgets.SuccessColor with { W = 0.85f }, status);
        }
        else if (selected is { } plate)
        {
            var openHere = profileService.OpenPlateId == plate.PlateId;
            ImGui.TextDisabled(plate.IsReady
                ? $"Last saved {plate.ModifiedUtc.ToLocalTime():g}{(openHere ? " · Open in the editor" : string.Empty)}"
                : plate.Problem ?? "This Plate can't be opened.");
        }
        else
        {
            ImGui.TextDisabled("Select a Plate. Double-click to edit; drag to reorder.");
        }

        ImGui.TextDisabled("Right click a Plate for actions");
    }

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
