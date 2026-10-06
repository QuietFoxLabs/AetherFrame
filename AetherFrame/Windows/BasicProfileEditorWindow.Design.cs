using System;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The Style category: choices about the Plate as a whole, in order — Look (the style browser: Art
/// Styles first, then Simple Themes, each with its own choice; issue #118) and Pattern (both
/// first-class visual browsers), Customize Background (detailed color/mode tuning, collapsed by
/// default), Text (the shared section heading size), the Plate Frame and decoration Components,
/// then Layout: the orientation together with the layout actions that apply to every Basic section.
/// Pattern and Customize Background show only while the Plate's own background does
/// (<see cref="AppearanceControls"/>, the Advanced editor's rule too): background artwork that covers
/// it (an Art Style's) leaves them out, with one line saying why and a way to bring them back.
/// </summary>
internal sealed partial class BasicProfileEditorWindow
{
    private static readonly string[] OrientationLabels = ["Normal", "Mirrored"];

    private void DrawDesignCategory(ProfileDocument profile)
    {
        // The look first: the two first-class visual pickers, the style browser (an Art Style, or a
        // Simple Theme), then Pattern (the background's procedural texture) — both discoverable
        // without first opening Customize Background.
        Subheading("Look");
        using (ImRaii.PushId("Theme"))
        {
            backgroundPanel.DrawThemeBrowser(profile, basicEditorSession.ApplyTheme);
        }

        Hint(backgroundPanel.ShowingArtStyles
            ? "An Art Style is a whole look: background, frames, corners, name plaque, divider and section headers, with text colors to match. Each piece stays yours to change under Frame & Decorations."
            : "A Simple Theme sets the background and every Basic text color at once. Each value stays editable.");

        ImGui.Spacing();

        // The Simple view folds everything after the Look into labelled sections.
        if (MoreControls("Background and Pattern"))
        {
            DrawBackgroundControls(profile);
        }

        if (MoreControls("Heading Size"))
        {
            DrawSectionHeadingSize(profile);
        }

        if (MoreControls("Frame and Decorations"))
        {
            DrawFrameAndDecorations(profile);
        }

        if (MoreControls("Orientation and Layout"))
        {
            DrawPlateLayoutActions(profile);
        }
    }

    /// <summary>The Plate's own background: Pattern and Customize Background, or why artwork covers them.</summary>
    private void DrawBackgroundControls(ProfileDocument profile)
    {
        // Asked after the Look, so a style chosen this frame is already reflected (issue #119).
        var cover = AppearanceControls.Background(profile);
        if (cover.Component is { } covering)
        {
            // Background artwork covers the Plate's own background, so its settings would change
            // nothing here. They are kept, and come back with the background: taking the artwork away
            // is one undo step, here or under Frame & Decorations.
            Hint(AppearanceControls.BasicCoveredHint(cover));
            if (ImGui.SmallButton("Remove the Artwork"))
            {
                editorSession.RemoveComponent(covering.Id);
            }

            ToolTip("Takes the background artwork away (undoable), so your own background shows, with its Pattern and Customize Background.\nYou can choose artwork again under Frame & Decorations.");
        }
        else
        {
            Subheading("Pattern");
            using (ImRaii.PushId("Pattern"))
            {
                backgroundPanel.DrawPatternPresets(profile);
            }

            // Fine tuning — mode, exact colors, gradient, image — out of the way until wanted.
            ImGui.Spacing();
            if (ImGui.CollapsingHeader("Customize Background##CustomizeBackground"))
            {
                using var id = ImRaii.PushId("Background");
                backgroundPanel.Draw(profile, applyTheme: null);
            }
        }
    }

    /// <summary>
    /// Section heading size: one control for every standard section heading (Home World, Favorite
    /// Job, Free Company, Playstyle, Active Hours, Message) together — a whole-Plate presentation
    /// choice, so it lives here rather than in any one section. A slider drag is one undo step.
    /// (The Advanced Editor still sizes each heading on its own.)
    /// </summary>
    private void DrawSectionHeadingSize(ProfileDocument profile)
    {
        ImGui.Spacing();
        Subheading("Text");

        var headings = BasicPlateEditor.Headings(profile);

        // Up to the largest size the layout shows at full size, so every value on the slider is visible.
        var max = AdventurePlateClassicLayout.MaxHeadingFontSize(profile);
        var size = Math.Min(BasicPlateEditor.HeadingSize(profile) ?? AdventurePlateClassicLayout.DefaultHeadingFontSize * AdventurePlateClassicLayout.FontScale(profile), max);

        ImGui.TextUnformatted("Section heading size");
        using (ImRaii.Disabled(headings.Count == 0))
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##SectionHeadingSize", ref size, TextProfileElement.MinFontSize, max, "%.0f px", ImGuiSliderFlags.AlwaysClamp))
            {
                basicEditorSession.SetHeadingSize(size, continuous: true);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                basicEditorSession.CommitTextEdit();
            }
        }

        ToolTip("The small captions above your details (Home World, Free Company, Message...), all at once.");

        if (headings.Count == 0)
        {
            Hint("Your section headings appear here once your Plate has sections.");
            return;
        }

        if (BasicPlateEditor.HeadingSizesDiffer(profile))
        {
            Hint("Your headings have different sizes (set in the Advanced Editor). Changing this gives them all one size.");
        }

        // A heading placed or sized in the Advanced Editor keeps its own box, so a size larger than
        // that box is drawn fitted to it (auto fit), as everywhere else.
        var shown = headings.Select(h => UI.Rendering.ProfileTextRenderer.GetCachedEffectiveFontSize(h)).OfType<float>().DefaultIfEmpty(size).Min();
        if (shown < size - 0.5f)
        {
            Hint($"Some headings are shown at {shown:0} px: they were placed in the Advanced Editor and keep their own box. Apply Layout (below) fits them again.");
        }
    }

    /// <summary>
    /// The Plate's layout in one place: its orientation, then the actions for every Basic section at
    /// once, and what needs attention.
    /// </summary>
    private void DrawPlateLayoutActions(ProfileDocument profile)
    {
        ImGui.Spacing();
        Subheading("Layout");
        var orientation = BasicEditorSession.GetOrientation(profile);
        var clicked = EditorWidgets.Segmented("Orientation", OrientationLabels, (int)orientation);
        if (clicked >= 0)
        {
            basicEditorSession.SetOrientation((AdventurePlateOrientation)clicked);
        }

        Hint("Adventure Plate Classic: a portrait beside your details. Mirrored puts the portrait on the right, and mirrors an Art Style's background with it.");
        ImGui.Spacing();

        var customized = BasicEditorSession.CustomizedSections(profile);
        if (customized.Count == 0)
        {
            ImGui.TextDisabled("Every section follows the Adventure Plate layout.");
        }
        else
        {
            ImGui.TextColored(CustomizedColor, customized.Count == 1
                ? "1 section is customized in the Advanced Editor."
                : $"{customized.Count} sections are customized in the Advanced Editor.");
            ToolTip(string.Join("\n", customized.Select(GroupTitle))
                + "\n\nBasic keeps customized sections exactly where you placed them, even when you change\nthe orientation. Apply Layout (here or in a section's category) moves them back.");
        }

        DrawOverlapWarnings(profile, static _ => true);

        var half = new Vector2((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f, 0f);
        using (ImRaii.Disabled(!BasicEditorSession.CanResetLayout(profile)))
        {
            if (ImGui.Button("Apply Layout", half))
            {
                basicEditorSession.ApplyLayout();
            }

            ToolTip("Moves every Basic section into the Adventure Plate Classic layout for this orientation,\nincluding sections customized in the Advanced Editor. Content and styles are kept. Undoable.");

            ImGui.SameLine();
            if (ImGui.Button("Reset Basic Layout...", half))
            {
                pendingResetLayoutConfirm = true;
            }

            ToolTip("Back to the Normal orientation with every Basic section in its default place.\nAsks first.");
        }
    }
}
