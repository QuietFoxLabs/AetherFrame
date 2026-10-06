using System;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Theme;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Theme;
using AetherFrame.Windows.Tutorial;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Guided creation in the Basic editor (<see cref="GuidedCreation"/>): while the open Plate is the
/// guided one, the editor shows its three steps around the same live view instead of the category
/// navigator. Choose a Look (four curated looks, the full collection one click away), Make It Yours
/// (name, portrait and message, each optional) and Save. Each step is one decision: a short heading,
/// at most one short line of guidance (anything more is a tooltip), labelled controls, and one
/// primary button, Continue or Save, beside Back in a footer that never scrolls away. Every edit is
/// the Basic editor's own, through the same session, so Undo, Redo, recovery and image ownership work
/// as they always do; nothing here saves except the Save step's Save. Once saved, a short success
/// line, View Plate and Keep Editing.
/// </summary>
internal sealed partial class BasicProfileEditorWindow
{
    // The steps' panel beside the live view (unscaled pixels), and the width below which the live
    // view moves above the panel.
    private const float GuidedPanelMinWidth = 320f;
    private const float GuidedPanelMaxWidth = 460f;
    private const float GuidedSideBySideMinWidth = 700f;

    // Spelled as the steps' headings and the welcome spell them.
    private static readonly string[] StepTitles = ["Choose a look", "Make it yours", "Save"];
    private static readonly string[] StepNumbers = ["1", "2", "3"];

    private bool drawingGuided;

    // Choose a Look shows the artwork's download state itself, so the live view's toolbar leaves it out.
    private bool artStatusInSteps;

    /// <summary>Whether a saved Plate is the logged-in character's Active Plate: null with no character logged in. Set by the plugin.</summary>
    internal Func<Guid, bool?>? IsActivePlate { get; set; }

    /// <summary>Shows a saved Plate in the Plate Viewer. Set by the plugin.</summary>
    internal Action<Guid>? ViewPlate { get; set; }

    /// <summary>Whether this build can share Plates (only then does the saved state mention sharing).</summary>
    internal bool SharingAvailable { get; set; }

    private void DrawGuided(ProfileDocument profile, GuidedCreation guided)
    {
        drawingGuided = true;
        try
        {
            DrawGuidedBody(profile, guided);
        }
        finally
        {
            drawingGuided = false;
            artStatusInSteps = false;
        }
    }

    private void DrawGuidedBody(ProfileDocument profile, GuidedCreation guided)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var success = guided.ShowsSuccess(profile.ProfileId);
        artStatusInSteps = !success && guided.Stage == GuidedStage.ChooseLook;
        DrawGuidedHeader(guided, success);

        // While recovery checkpoints fail, the action bar's warning line with Retry now, on its own row.
        if (!success)
        {
            actionBar.DrawRecoveryWarning(actionBar.Commands.RecoveryIndicator);
        }

        // The editor's error line, except a failed Save the Save step already explains (or did, before Back).
        if (basicEditorSession.ErrorMessage is { } error && !success && !ReferenceEquals(error, guided.SaveFailure))
        {
            AetherControls.StatusLine(AetherTone.Danger, error, wrap: true);
        }

        ImGui.Separator();

        // Exactly what the footer draws (DrawGuidedFooter): the row's spacing, its separator, a gap and
        // the button row, so the steps never give the window a scrollbar.
        var style = ImGui.GetStyle();
        var footerHeight = success ? 0f : ImGui.GetFrameHeight() + (style.ItemSpacing.Y * 3f) + (AetherMetrics.SpaceXs * scale) + 1f;
        var body = ImGui.GetContentRegionAvail();
        body.Y = Math.Max(body.Y - footerHeight, 160f * scale);

        if (body.X >= GuidedSideBySideMinWidth * scale)
        {
            var panelWidth = Math.Clamp(body.X * 0.4f, GuidedPanelMinWidth * scale, GuidedPanelMaxWidth * scale);
            DrawGuidedPanel(profile, guided, success, new Vector2(panelWidth, body.Y));
            ImGui.SameLine();
            DrawPreview(profile, new Vector2(0f, body.Y));
        }
        else
        {
            // Narrow: the live view first, at the canvas' own shape within its share of the height
            // (smaller than its usual minimum when a short window has little room), then the step's controls.
            var panelMinimum = 100f * scale;
            var previewHeight = BasicEditorView.StackedPreviewHeight(
                body.X, profile.CanvasWidth, profile.CanvasHeight, ImGui.GetFrameHeightWithSpacing(), body.Y, BasicEditorView.GuidedPreviewShare, 120f * scale, panelMinimum + style.ItemSpacing.Y);
            DrawPreview(profile, new Vector2(-1f, previewHeight));
            DrawGuidedPanel(profile, guided, success, new Vector2(-1f, Math.Max(panelMinimum, body.Y - previewHeight - style.ItemSpacing.Y)));
        }

        if (!success)
        {
            DrawGuidedFooter(profile, guided);
        }
    }

    /// <summary>
    /// The title, then, at the right, the save state (nothing is saved until Save) with the recovery
    /// mark the action bar shows beside it, Undo, Redo, Exit Guide and Help, on a row of their own when
    /// the window is too narrow to hold them beside the title, so nothing is ever drawn over another.
    /// Where the player is shows once, in the steps below.
    /// </summary>
    private void DrawGuidedHeader(GuidedCreation guided, bool success)
    {
        var style = ImGui.GetStyle();
        using (AetherFonts.Heading())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(success ? "Your Plate" : "Create your Plate");
        }

        var commands = actionBar.Commands;
        float Width(string label) => ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f);
        var stateWidth = EditorActionBar.WidestSaveState();
        var markWidth = actionBar.RecoveryMarkWidth;
        var rightWidth = success ? 0f : stateWidth + markWidth + Width("Undo") + Width("Redo") + Width("Exit Guide") + (style.ItemSpacing.X * 3f);
        if (Help is not null)
        {
            rightWidth += HelpMenu.ButtonWidth + (success ? 0f : style.ItemSpacing.X);
        }

        AetherControls.AlignRightAfterItem(rightWidth);
        if (!success)
        {
            // The action bar's own save state, in the room its widest wording needs, so the buttons never shift.
            var stateStart = ImGui.GetCursorPosX();
            var (stateText, stateColor) = actionBar.CurrentSaveState;
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(stateColor, stateText);
            ImGui.SameLine(stateStart + stateWidth + style.ItemSpacing.X);

            // The recovery mark (its tooltip gives the last checkpoint's time), in room taken shown or
            // hidden, after the save state's widest wording, so Undo and what follows never shift.
            if (markWidth > 0f)
            {
                actionBar.DrawRecoveryMarkHere(commands.RecoveryIndicator);
                ImGui.SameLine();
            }

            // None of these while Save is being written: what's being saved is what was there when Save was pressed.
            using (ImRaii.Disabled(!commands.CanUndo || !guided.CanEdit))
            {
                if (ImGui.Button("Undo##GuidedUndo"))
                {
                    commands.Undo();
                }
            }

            EditorWidgets.Tooltip("Undo (Ctrl+Z)");
            ImGui.SameLine();
            using (ImRaii.Disabled(!commands.CanRedo || !guided.CanEdit))
            {
                if (ImGui.Button("Redo##GuidedRedo"))
                {
                    commands.Redo();
                }
            }

            EditorWidgets.Tooltip("Redo (Ctrl+Y)");
            ImGui.SameLine();
            using (ImRaii.Disabled(!guided.CanEdit))
            {
                if (AetherControls.GhostButton("Exit Guide", tooltip: "Leave the steps and edit this Plate with every Basic control.\nEverything you entered stays; the steps end for this Plate."))
                {
                    guided.Leave();
                }
            }
        }

        if (Help is { } help)
        {
            if (!success)
            {
                ImGui.SameLine();
            }

            help.DrawButton("GuidedHelp");
        }
    }

    /// <summary>The step's controls, scrolling on their own: the steps, the step's heading and at most one short line, then what it asks.</summary>
    private void DrawGuidedPanel(ProfileDocument profile, GuidedCreation guided, bool success, Vector2 size)
    {
        using var panel = AetherChild.Begin("##GuidedPanel", size, false);
        if (!panel.Success)
        {
            return;
        }

        if (success)
        {
            DrawGuidedSaved(profile, guided);
            return;
        }

        DrawStepper(guided.Stage);
        ImGui.Spacing();
        switch (guided.Stage)
        {
            case GuidedStage.ChooseLook:
                DrawChooseLook(profile);
                break;
            case GuidedStage.MakeItYours:
                DrawMakeItYours(profile);
                break;
            default:
                DrawSaveStep(profile, guided);
                break;
        }

        ImGui.Spacing();
    }

    /// <summary>The three steps in a row: done ones checked, the current one in the accent, later ones muted.</summary>
    private static void DrawStepper(GuidedStage stage)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var available = ImGui.GetContentRegionAvail().X;
        var radius = ImGui.GetTextLineHeight() * 0.62f;
        var height = (radius * 2f) + (AetherMetrics.SpaceXs * scale);
        var origin = ImGui.GetCursorScreenPos();
        var slot = available / StepTitles.Length;

        // Every step's name beside its number, or none when one doesn't fit (the heading below names
        // the current step), so a later step is never named while the current one isn't.
        var room = slot - (radius * 2f) - 1f - (AetherMetrics.SpaceXs * scale * 2f);
        var titled = true;
        foreach (var title in StepTitles)
        {
            titled &= ImGui.CalcTextSize(title).X <= room;
        }

        for (var i = 0; i < StepTitles.Length; i++)
        {
            var center = new Vector2(origin.X + (slot * i) + radius + 1f, origin.Y + radius);
            var done = i < (int)stage;
            var current = i == (int)stage;
            var color = current || done ? EditorWidgets.AccentColor : EditorWidgets.DimTextColor;
            if (current || done)
            {
                drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(color));
            }
            else
            {
                drawList.AddCircle(center, radius, ImGui.GetColorU32(color), 0, 1.5f * scale);
            }

            var numberColor = ImGui.GetColorU32(current || done ? AetherPalette.TextPrimary : EditorWidgets.DimTextColor);
            if (done)
            {
                // The game font has no check mark: the icon font's, as the theme cards draw theirs.
                var check = EditorWidgets.GetIconString(FontAwesomeIcon.Check);
                using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
                {
                    drawList.AddText(center - (ImGui.CalcTextSize(check) / 2f), numberColor, check);
                }
            }
            else
            {
                var number = StepNumbers[i];
                drawList.AddText(center - (ImGui.CalcTextSize(number) / 2f), numberColor, number);
            }

            if (titled)
            {
                var textPos = new Vector2(center.X + radius + (AetherMetrics.SpaceXs * scale), origin.Y + radius - (ImGui.GetTextLineHeight() / 2f));
                drawList.AddText(textPos, ImGui.GetColorU32(current ? AetherPalette.TextPrimary : EditorWidgets.DimTextColor), StepTitles[i]);
            }
        }

        ImGui.Dummy(new Vector2(available, height));
    }

    // ---------------------------------------------------------------- step 1

    private void DrawChooseLook(ProfileDocument profile)
    {
        AetherControls.SectionHeader("Choose a look", topSpacing: 0f);
        AetherControls.Secondary("Pick a style; you can change it later.");
        ImGui.Spacing();

        using (ImRaii.PushId("CuratedLooks"))
        {
            backgroundPanel.DrawLooks(profile, CuratedLooks.Styles(), basicEditorSession.ApplyTheme);
        }

        DrawLookArtwork(profile);

        ImGui.Spacing();
        var browse = ImGui.CollapsingHeader("Browse the full collection##GuidedAllStyles");
        EditorWidgets.Tooltip(renderResources.ArtStore.Downloader is null
            ? "Every Art Style and Simple Theme."
            : "Every Art Style and Simple Theme. Only the style you choose is downloaded.");
        if (browse)
        {
            using var id = ImRaii.PushId("AllLooks");
            backgroundPanel.DrawThemeBrowser(profile, basicEditorSession.ApplyTheme, explainOnHover: true);
        }
    }

    /// <summary>
    /// The chosen look's artwork: downloading (the live view fills in when it arrives), or couldn't
    /// download, with Try again and a Simple Theme in similar colors that needs no download.
    /// </summary>
    private void DrawLookArtwork(ProfileDocument profile)
    {
        var summary = previewArt.Summary(renderResources.ArtStore);
        switch (summary.Kind)
        {
            case ArtNeedKind.Downloading:
                ImGui.Spacing();
                AetherControls.StatusLine(AetherTone.Info, "Downloading artwork...");
                EditorWidgets.Tooltip("The live view fills in as it arrives. You can carry on meanwhile.");
                break;

            case ArtNeedKind.Failed:
            case ArtNeedKind.Unavailable:
            {
                ImGui.Spacing();
                AetherControls.Callout(AetherTone.Warning, summary.Kind == ArtNeedKind.Failed ? "The artwork couldn't download." : "This build can't download artwork.");
                EditorWidgets.Tooltip(summary.Kind == ArtNeedKind.Failed ? "The live view shows this look's colors only until it does." : "The live view shows this look's colors only.");
                if (summary.Kind == ArtNeedKind.Failed && AetherControls.SecondaryButton("Try again##GuidedArtTryAgain"))
                {
                    previewArt.TryAgain(renderResources.ArtStore);
                }

                if (CuratedLooks.FallbackFor(PlateStyle.InUse(profile)) is { } fallback)
                {
                    if (summary.Kind == ArtNeedKind.Failed)
                    {
                        ImGui.SameLine();
                    }

                    if (AetherControls.SecondaryButton($"Use {fallback.Name} instead##GuidedArtFallback"))
                    {
                        basicEditorSession.ApplyTheme(fallback);
                    }

                    EditorWidgets.Tooltip($"{fallback.Name}: {fallback.Description}.\nA Simple Theme: colors only, nothing to download. Undoable.");
                }

                break;
            }
        }
    }

    // ---------------------------------------------------------------- step 2

    private void DrawMakeItYours(ProfileDocument profile)
    {
        AetherControls.SectionHeader("Make it yours", topSpacing: 0f);
        AetherControls.Secondary("Everything here is optional.");

        // ---- name (each control's explanation is its tooltip)
        Subheading("Name");
        var identity = basicEditorSession.Identity;
        if (BasicIdentitySession.HasNoHeader(profile))
        {
            if (ImGui.Button("Add a Name", new Vector2(-1f, 0f)))
            {
                identity.CreateHeader();
            }
        }
        else
        {
            // Typed by hand only: the logged-in character's name is never drawn here, not even as a hint.
            var name = BasicIdentitySession.Find(profile, ProfileElementRole.BasicName);
            var buffer = name?.Text ?? string.Empty;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputTextWithHint("##GuidedName", "Your character's name", ref buffer, 64))
            {
                identity.SetNameText(buffer);
            }

            // Explanations as tooltips, out of the way while typing.
            if (!ImGui.IsItemActive())
            {
                ToolTip("Shown large at the top of your Plate.");
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                identity.Commit();
            }

            if (name is { Visible: false })
            {
                if (ImGui.SmallButton("Show the name on my Plate##GuidedShowName"))
                {
                    identity.SetNameVisible(true);
                }

                ToolTip("Your name is hidden on this Plate.");
            }
        }

        // ---- portrait
        Subheading("Portrait");
        var portrait = basicEditorSession.Portrait;
        if (portrait is null)
        {
            if (ImGui.Button("Choose an Image...##GuidedPortrait", new Vector2(-1f, 0f)))
            {
                OpenImageFileDialog("Choose a Portrait", basicEditorSession.SetPortrait);
            }

            ToolTip("A screenshot or any picture on your PC.");
        }
        else
        {
            var thumbnail = 64f * ImGuiHelpers.GlobalScale;
            if (imageTextureCache.GetWrapOrNull(portrait.AssetId) is { } wrap)
            {
                ImGui.Image(wrap.Handle, new Vector2(thumbnail, thumbnail));
            }
            else
            {
                ImGui.Dummy(new Vector2(thumbnail, thumbnail));
                ToolTip("The image couldn't be loaded.");
            }

            ImGui.SameLine();
            using (ImRaii.Group())
            {
                var width = new Vector2(Math.Max(100f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X), 0f);
                if (ImGui.Button("Replace...##GuidedReplacePortrait", width))
                {
                    OpenImageFileDialog("Replace Portrait", basicEditorSession.SetPortrait);
                }

                if (ImGui.Button("Remove##GuidedRemovePortrait", width))
                {
                    basicEditorSession.RemovePortrait();
                }

                ToolTip("Takes the portrait off this Plate (undoable). The image stays on your PC.");
            }
        }

        // ---- message
        Subheading("Message");
        var message = BasicSections.FindText(profile, ProfileElementRole.BasicMessage)?.Text ?? string.Empty;
        var height = Math.Max(90f * ImGuiHelpers.GlobalScale, ImGui.GetTextLineHeightWithSpacing() * 4f);
        using (AetherChild.WhilePicking())
        {
            if (ImGui.InputTextMultiline("##GuidedMessage", ref message, TextProfileElement.MaxTextLength, new Vector2(-1f, height)))
            {
                basicEditorSession.SetText(ProfileElementRole.BasicMessage, message);
            }
        }

        if (!ImGui.IsItemActive())
        {
            ToolTip("A greeting, when you play, what you're looking for: anything.");
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            basicEditorSession.CommitTextEdit();
        }
    }

    // ---------------------------------------------------------------- step 3

    private void DrawSaveStep(ProfileDocument profile, GuidedCreation guided)
    {
        AetherControls.SectionHeader("Save", topSpacing: 0f);
        AetherControls.Secondary("Check the live view, then save.");
        ImGui.Spacing();

        var name = BasicIdentitySession.Find(profile, ProfileElementRole.BasicName) is { Visible: true, Text: { Length: > 0 } text } ? text : "No name";
        var hasMessage = BasicSections.FindText(profile, ProfileElementRole.BasicMessage) is { Text.Length: > 0 };
        SummaryRow("Look", PlateStyle.InUse(profile)?.Name ?? "Your own colors");
        SummaryRow("Name", name);
        SummaryRow("Portrait", basicEditorSession.Portrait is null ? "None" : "Added");
        SummaryRow("Message", hasMessage ? "Added" : "None");

        if (guided.SaveError is { } error)
        {
            ImGui.Spacing();
            AetherControls.Callout(AetherTone.Danger, $"{error} Everything you entered is still here. Try Save again.", "Not saved");
        }
    }

    private static void SummaryRow(string label, string value)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.SameLine(110f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(value);
    }

    /// <summary>
    /// The footer: Back (secondary) at the left, the step's one primary action at the right (Continue,
    /// or Save on the Save step). Outside the panel's scrolling, so it's always in reach.
    /// </summary>
    private void DrawGuidedFooter(ProfileDocument profile, GuidedCreation guided)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceXs * scale));

        using (ImRaii.Disabled(!guided.CanGoBack))
        {
            if (AetherControls.SecondaryButton("Back##GuidedBack", new Vector2(110f * scale, 0f)))
            {
                guided.Back();
            }
        }

        var (label, tooltip) = guided.Stage switch
        {
            GuidedStage.ChooseLook => ("Continue", "On to your name, portrait and message. Your look is kept."),
            GuidedStage.MakeItYours => ("Continue", "On to Save. Anything left empty can be added later."),
            _ => (guided.IsSaving ? "Saving..." : "Save", "Saves your Plate to My Plates, on your PC (Ctrl+S)."),
        };
        var width = Math.Max(160f * scale, ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 4f));
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - width));
        var busy = guided.Stage == GuidedStage.Save && (guided.IsSaving || actionBar.Commands.IsSaving);
        using (ImRaii.Disabled(busy))
        {
            if (AetherControls.PrimaryButton($"{label}##GuidedPrimary", new Vector2(width, 0f), tooltip))
            {
                if (guided.Stage == GuidedStage.Save)
                {
                    guided.Save();
                }
                else
                {
                    guided.Next();
                }
            }
        }
    }

    // ---------------------------------------------------------------- saved

    /// <summary>
    /// The saved state: "Saved to My Plates", one line on how the Plate becomes Active (the rest, and
    /// that sharing is separate, in its tooltip), and View Plate (primary) or Keep Editing.
    /// </summary>
    private void DrawGuidedSaved(ProfileDocument profile, GuidedCreation guided)
    {
        var scale = ImGuiHelpers.GlobalScale;
        EditorWidgets.IconText(FontAwesomeIcon.CheckCircle, EditorWidgets.SuccessColor);
        ImGui.SameLine();
        using (AetherFonts.Heading())
        {
            ImGui.TextUnformatted("Saved to My Plates");
        }

        ImGui.Spacing();
        var plateId = profile.ProfileId;
        var active = IsActivePlate?.Invoke(plateId);
        AetherControls.Secondary(active switch
        {
            true => "It's your character's Active Plate.",
            false => "Set it Active in My Plates to use it.",
            null => "Log in, then set it Active in My Plates.",
        });
        var sharing = SharingAvailable ? "\nSharing is separate and optional: Sharing, in My Plates, turns it on." : string.Empty;
        ToolTip(active switch
        {
            true => "The Plate AetherFrame shows for this character, and what /af view opens." + sharing,
            false => "Right-click it in My Plates and choose Set Active: the Plate AetherFrame shows for this character." + sharing,
            null => "Log in to the character, then right-click the Plate in My Plates and choose Set Active." + sharing,
        });

        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceSm * scale));
        var half = new Vector2(Math.Max(120f * scale, (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f), 0f);
        if (AetherControls.PrimaryButton("View Plate##GuidedView", half, "Shows the saved Plate in the Plate Viewer."))
        {
            ViewPlate?.Invoke(plateId);
            guided.DismissSuccess();
        }

        if (half.X * 2f <= ImGui.GetContentRegionAvail().X)
        {
            ImGui.SameLine();
        }

        if (AetherControls.SecondaryButton("Keep Editing##GuidedKeepEditing", half, "Carry on in the Basic editor. Save again when you change something."))
        {
            guided.DismissSuccess();
        }
    }
}
