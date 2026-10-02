using System;
using System.Numerics;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The unsaved-changes question both editors ask when closing would lose work: Save, Discard, or
/// Cancel, answered through the window's <see cref="EditorCloseGuard"/>. Drawn from the window's
/// outermost scope every frame (the same deferred-open pattern as every other editor popup).
/// </summary>
internal static class EditorClosePrompt
{
    private const string PopupId = "Unsaved Changes##AetherFrameUnsavedChanges";

    /// <param name="guard">The window's guard.</param>
    /// <param name="close">Closes the window (after Discard).</param>
    internal static void Draw(EditorCloseGuard guard, Action close)
    {
        if (guard.ConsumePromptRequest())
        {
            ImGui.OpenPopup(PopupId);
        }

        using var popup = ImRaii.PopupModal(PopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);
        if (!popup.Success)
        {
            return;
        }

        if (!guard.IsAsking)
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.TextUnformatted("This Plate has unsaved changes. Save them before closing?");
        ImGui.Spacing();

        var buttonSize = new Vector2(110f * ImGuiHelpers.GlobalScale, 0f);
        using (ImRaii.Disabled(!guard.CanSave))
        {
            if (AetherControls.PrimaryButton(guard.IsSaving ? "Saving..." : "Save", buttonSize))
            {
                guard.Save();
                ImGui.CloseCurrentPopup();
            }
        }

        EditorWidgets.Tooltip("Save, then close.");

        // Discard is unavailable exactly while Save is: a save still being written (the question's
        // own, or one started from the action bar just before closing) can't have the document
        // reverted underneath it — the guard refuses that. A refusal (that, or no saved version to
        // go back to) keeps the question open and is explained below it, instead of closing the
        // window with the edits still in place.
        ImGui.SameLine();
        using (ImRaii.Disabled(!guard.CanSave))
        {
            if (AetherControls.DangerButton("Discard", buttonSize) && guard.Discard())
            {
                ImGui.CloseCurrentPopup();
                close();
            }
        }

        EditorWidgets.Tooltip("Go back to the last saved version, then close.");

        ImGui.SameLine();
        if (AetherControls.GhostButton("Cancel", buttonSize) || PopupEscapeGuard.CancelsPrompt())
        {
            guard.Cancel();
            ImGui.CloseCurrentPopup();
        }

        EditorWidgets.Tooltip("Keep editing. Nothing is lost.");

        if (guard.DiscardRefusal is { } refusal)
        {
            ImGui.Spacing();
            ImGui.TextColored(EditorWidgets.ErrorColor, refusal);
        }
    }
}
