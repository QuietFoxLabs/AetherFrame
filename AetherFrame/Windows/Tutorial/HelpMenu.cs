using System;
using System.Numerics;
using AetherFrame.Services.Commands;
using AetherFrame.Services.Diagnostics;
using AetherFrame.UI.Theme;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows.Tutorial;

/// <summary>
/// The Help menu behind the Help button in My Plates and both editors: the tutorial (start,
/// resume, restart, a chapter to jump to), the keyboard shortcuts, the chat commands and the
/// running build. One instance, shared by every window that shows the button; each window draws
/// the button (and the popup right after it, in the same id scope) with <see cref="DrawButton"/>.
/// The quiet "take the tour" reminder for a player who said Maybe Later is
/// <see cref="DrawReminder"/>, drawn by My Plates alone.
/// </summary>
internal sealed class HelpMenu
{
    private const string PopupSuffix = "##AetherFrameHelpMenu";

    /// <summary>The button's text: a word, which reads more clearly than a glyph and sits centred in its button.</summary>
    internal const string Label = "Help";

    private static readonly string OpenCommandMeaning = "Open or close My Plates (also " + AetherFrameCommand.Name + ")";
    private static readonly string ViewCommand = AetherFrameCommand.Alias + " " + AetherFrameCommand.ViewArgument;
    private static readonly string VersionCommand = AetherFrameCommand.Alias + " " + AetherFrameCommand.VersionArgument;

    private readonly OnboardingCoordinator coordinator;
    private readonly ITutorialHost host;
    private readonly string buildDescription = AetherFrameBuildInfo.Current.Describe();
    private readonly string[] chapterRows;
    private string? resumeLabel;
    private int resumeChapter = -1;

    /// <summary>Opens the fonts' licences (<see cref="FontLicencesWindow"/>).</summary>
    internal Action? OpenFontLicences { get; set; }

    internal HelpMenu(OnboardingCoordinator coordinator, ITutorialHost host)
    {
        this.coordinator = coordinator;
        this.host = host;
        var chapters = coordinator.Session.Chapters;
        chapterRows = new string[chapters.Count];
        for (var i = 0; i < chapters.Count; i++)
        {
            chapterRows[i] = $"{i + 1}.  {chapters[i].Title}";
        }
    }

    /// <summary>The Help button's width, for a window that places it at its right edge.</summary>
    internal static float ButtonWidth => ImGui.CalcTextSize(Label).X + (ImGui.GetStyle().FramePadding.X * 2f);

    /// <summary>
    /// The Help button and, when clicked, its popup. <paramref name="id"/> keeps the popup unique
    /// per window; <paramref name="target"/> marks the button for the tutorial (My Plates' is the
    /// one the tour points at).
    /// </summary>
    internal void DrawButton(string id, TutorialTarget target = TutorialTarget.None)
    {
        var popupId = id + PopupSuffix;
        if (AetherControls.SecondaryButton(Label + "##" + id, tooltip: "The tutorial, keyboard shortcuts and chat commands"))
        {
            ImGui.OpenPopup(popupId);
        }

        TutorialAnchorMarks.Mark(target);

        using var popup = ImRaii.Popup(popupId);
        if (!popup.Success)
        {
            return;
        }

        DrawContents();
    }

    /// <summary>
    /// The reminder left by Maybe Later: one line with Take the tour and a dismiss, until the
    /// player takes the tour or dismisses it. Draws nothing otherwise.
    /// </summary>
    internal void DrawReminder()
    {
        if (!coordinator.ShowReminder || coordinator.IsTutorialActive)
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        using var id = ImRaii.PushId("TutorialReminder");
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var height = ImGui.GetFrameHeight() + (AetherMetrics.SpaceSm * scale);
        var width = ImGui.GetContentRegionAvail().X;
        drawList.AddRectFilled(min, min + new Vector2(width, height), ImGui.GetColorU32(AetherPalette.InfoTint), AetherMetrics.RadiusMd * scale);
        drawList.AddRectFilled(min, new Vector2(min.X + (AetherMetrics.AccentBarWidth * scale), min.Y + height), ImGui.GetColorU32(AetherPalette.Glow), AetherMetrics.RadiusMd * scale, ImDrawFlags.RoundCornersLeft);

        ImGui.SetCursorScreenPos(min + new Vector2(AetherMetrics.SpaceMd * scale, AetherMetrics.SpaceXs * scale));
        EditorWidgets.IconText(FontAwesomeIcon.GraduationCap, AetherPalette.Glow);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextSecondary))
        {
            ImGui.TextUnformatted("New here? A short guided tour points at the real controls.");
        }

        ImGui.SameLine();
        if (AetherControls.PrimaryButton("Take the tour"))
        {
            coordinator.StartTutorial(host.Snapshot());
        }

        ImGui.SameLine();
        if (AetherControls.GhostButton("Not now", tooltip: "Hide this reminder. Help keeps the tutorial."))
        {
            coordinator.DismissReminder();
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y + height + (AetherMetrics.SpaceXs * scale)));
    }

    private void DrawContents()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = 300f * scale;
        var snapshot = host.Snapshot();

        AetherBrand.Header("Help", buildDescription);
        ImGui.Dummy(new Vector2(width, 0f));

        // ---- the tutorial
        AetherControls.SectionHeader("Tutorial", topSpacing: 0f);
        var prefs = coordinator.Preferences;
        if (coordinator.IsTutorialActive)
        {
            AetherControls.MutedInline("The tour is running.");
            if (ImGui.MenuItem("Restart from the beginning"))
            {
                coordinator.StartTutorial(snapshot);
            }
        }
        else
        {
            if (coordinator.CanResume)
            {
                if (resumeChapter != prefs.LastChapter || resumeLabel is null)
                {
                    resumeChapter = prefs.LastChapter;
                    resumeLabel = "Resume: " + coordinator.Session.Chapters[resumeChapter].Title;
                }

                if (ImGui.MenuItem(resumeLabel))
                {
                    coordinator.ResumeTutorial(snapshot);
                }

                if (ImGui.MenuItem("Start over"))
                {
                    coordinator.StartTutorial(snapshot);
                }
            }
            else
            {
                var label = prefs.Status == TutorialStatus.Completed ? "Take the tour again" : "Start the tutorial";
                if (ImGui.MenuItem(label))
                {
                    coordinator.StartTutorial(snapshot);
                }
            }

            if (prefs.Status == TutorialStatus.Completed)
            {
                AetherControls.MutedInline(coordinator.IsUpdatedSinceCompletion ? "Completed; the tour has been updated since." : "Completed.");
            }
        }

        using (var chapters = ImRaii.Menu("Jump to a chapter"))
        {
            if (chapters.Success)
            {
                var list = coordinator.Session.Chapters;
                for (var i = 0; i < list.Count; i++)
                {
                    if (ImGui.MenuItem(chapterRows[i]))
                    {
                        coordinator.StartChapter(snapshot, i);
                    }

                    AetherControls.Tooltip(list[i].Summary);
                }
            }
        }

        // ---- shortcuts
        AetherControls.SectionHeader("Editor shortcuts");
        Shortcut("Ctrl+S", "Save");
        Shortcut("Ctrl+Z", "Undo");
        Shortcut("Ctrl+Y", "Redo (also Ctrl+Shift+Z)");
        Shortcut("Ctrl+D", "Duplicate the selected element");
        Shortcut("Delete", "Delete the selected element");
        Shortcut("Arrows", "Nudge by 1 px (Shift: 10 px)");
        Shortcut("F", "Fit the canvas to the window");
        Shortcut("Alt", "Hold to move without snapping");
        Shortcut("Esc", "Close the Plate Viewer (Preview)");
        Shortcut("Wheel", "Zoom the canvas; middle-drag pans");

        // ---- commands
        AetherControls.SectionHeader("Chat commands");
        Shortcut(AetherFrameCommand.Alias, OpenCommandMeaning);
        Shortcut(ViewCommand, "Show your character's Active Plate");
        Shortcut(VersionCommand, "Print the running version in chat");

        // ---- licences
        AetherControls.SectionHeader("About");
        if (OpenFontLicences is { } openLicences && ImGui.MenuItem("Font licences"))
        {
            openLicences();
        }

        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceXs * scale));
        AetherControls.Muted("Your Plates stay on your PC. Nothing goes to AetherFrame's server unless you turn on sharing for a character, and an Art Style's artwork downloads from GitHub the first time you use it. Bugs and ideas: the AetherFrame repository's issue tracker.");
    }

    private static void Shortcut(string keys, string meaning)
    {
        AetherControls.KeyHint(keys);
        ImGui.SameLine(112f * ImGuiHelpers.GlobalScale);
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextSecondary))
        {
            ImGui.TextUnformatted(meaning);
        }
    }
}
