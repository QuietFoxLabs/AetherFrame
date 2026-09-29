using System;
using System.Numerics;
using AetherFrame.UI.Theme;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows.Tutorial;

/// <summary>
/// The tutorial's explanation card: a fixed-width, auto-height window placed beside the
/// spotlight (see <see cref="TutorialCardPlacement"/>), with the chapter, the step's title and
/// text, an action when a step needs the player elsewhere, progress, and Back / Next / Skip,
/// plus a chapter picker. It never overlaps the hole when there is room, and always stays in the
/// viewport. Left and Right arrows page while the card has focus.
/// </summary>
internal sealed class TutorialCardWindow : Window
{
    private const ImGuiWindowFlags CardFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking
        | ImGuiWindowFlags.AlwaysAutoResize;

    private const string ChaptersPopupId = "##AetherFrameTutorialChapters";

    private readonly OnboardingCoordinator coordinator;
    private readonly ITutorialHost host;
    private readonly TutorialOverlayFrame frame;
    private int framesOpen;
    private bool pushed;
    private bool pendingChaptersPopup;

    internal TutorialCardWindow(OnboardingCoordinator coordinator, ITutorialHost host, TutorialOverlayFrame frame)
        : base("AetherFrame Tutorial Card##AetherFrameTutorialCard", CardFlags)
    {
        this.coordinator = coordinator;
        this.host = host;
        this.frame = frame;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
        AllowBackgroundBlur = true;
    }

    public override void OnOpen() => framesOpen = 0;

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowPos(frame.CardPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(AetherMetrics.TutorialCardWidth * scale, 0f), ImGuiCond.Always);

        // The card appears after the shades in the same frame, so for its first frames it asks
        // to come forward; from then on nothing of AetherFrame's can get in front of it.
        if (framesOpen < 3)
        {
            BringToFront();
        }

        framesOpen++;

        AetherStyle.Push();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, AetherPalette.SurfaceRaised.WithOpacity(0.985f));
        ImGui.PushStyleColor(ImGuiCol.Border, AetherPalette.BorderStrong);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, AetherMetrics.RadiusLg * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(AetherMetrics.SpaceLg, AetherMetrics.SpaceMd) * scale);
        pushed = true;
    }

    public override void PostDraw()
    {
        if (pushed)
        {
            pushed = false;
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
            AetherStyle.Pop();
        }
    }

    public override void Draw()
    {
        frame.CardSize = ImGui.GetWindowSize();
        if (frame.View is not { } view || frame.Frame != ImGui.GetFrameCount())
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var snapshot = host.Snapshot();
        var contentWidth = ImGui.GetContentRegionAvail().X;

        // ---- header: the mark, the chapter, and the close
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var markSize = AetherMetrics.BrandMarkSize * scale;
        AetherBrand.DrawMark(drawList, origin + new Vector2(markSize / 2f, ImGui.GetFrameHeight() / 2f), markSize, AetherPalette.Aether, AetherPalette.Glow);
        ImGui.SetCursorScreenPos(origin + new Vector2(markSize + (AetherMetrics.SpaceSm * scale), 0f));
        ImGui.AlignTextToFramePadding();
        using (AetherFonts.Label())
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.Aether))
        {
            ImGui.TextUnformatted($"CHAPTER {view.ChapterIndex + 1} OF {view.ChapterCount}  ·  {view.Chapter.Title.ToUpperInvariant()}");
        }

        ImGui.SameLine(contentWidth - ImGui.GetFrameHeight());
        if (EditorWidgets.IconButton("CloseTutorial", FontAwesomeIcon.Times, "Close the tutorial for now. Help in My Plates resumes it where you left off."))
        {
            coordinator.Suspend();
            return;
        }

        // ---- title and body
        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceXs * scale));
        using (AetherFonts.Display())
        {
            ImGui.TextWrapped(view.Title);
        }

        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceXs * scale));
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextSecondary))
        {
            ImGui.TextWrapped(view.Body);
        }

        // ---- what to do now
        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceSm * scale));
        switch (view.Presentation)
        {
            case TutorialStepPresentation.Prerequisite when view.Action != TutorialAction.None:
                if (AetherControls.PrimaryButton(ActionLabel(view.Action), new Vector2(-1f, 0f)))
                {
                    host.Perform(view.Action);
                }

                break;

            case TutorialStepPresentation.Spotlight when view.AllowInteraction:
                Note(FontAwesomeIcon.HandPointer, AetherPalette.Glow, view.Step.AdvanceWhen != TutorialCondition.None
                    ? "Use the highlighted control; the tour moves on by itself."
                    : "The highlighted control is live: try it, then use Next.");
                break;

            case TutorialStepPresentation.Spotlight:
                Note(FontAwesomeIcon.Eye, AetherPalette.TextMuted, "Have a look, then use Next.");
                break;

            case TutorialStepPresentation.MissingTarget:
                Note(FontAwesomeIcon.EyeSlash, AetherPalette.Warning, "The control isn't in view; Next continues anyway.");
                break;
        }

        // ---- progress
        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceSm * scale));
        var barMin = ImGui.GetCursorScreenPos();
        var barHeight = 3f * scale;
        var fraction = view.TotalSteps > 0 ? view.StepNumber / (float)view.TotalSteps : 0f;
        drawList.AddRectFilled(barMin, barMin + new Vector2(contentWidth, barHeight), ImGui.GetColorU32(AetherPalette.Border), barHeight / 2f);
        drawList.AddRectFilled(barMin, barMin + new Vector2(contentWidth * fraction, barHeight), ImGui.GetColorU32(AetherPalette.Aether), barHeight / 2f);
        ImGui.Dummy(new Vector2(contentWidth, barHeight + (AetherMetrics.SpaceXs * scale)));

        // ---- footer: chapters, skip; back, next
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextMuted))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"Step {view.StepNumber} of {view.TotalSteps}");
        }

        ImGui.SameLine();
        if (AetherControls.GhostButton("Chapters", tooltip: "Jump to a chapter."))
        {
            pendingChaptersPopup = true;
        }

        ImGui.SameLine();
        if (AetherControls.GhostButton("Skip tour", tooltip: "Leave the tutorial. You can start it again from Help."))
        {
            coordinator.SkipTutorial();
            return;
        }

        var nextLabel = view.IsLastStep ? "Finish" : "Next";
        var buttonWidth = Math.Max(ImGui.CalcTextSize(nextLabel).X, ImGui.CalcTextSize("Back").X) + (ImGui.GetStyle().FramePadding.X * 4f);
        var rowWidth = (buttonWidth * 2f) + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - rowWidth));
        using (ImRaii.Disabled(!view.CanGoBack))
        {
            if (AetherControls.SecondaryButton("Back", new Vector2(buttonWidth, 0f)))
            {
                coordinator.Back(snapshot);
            }
        }

        ImGui.SameLine();
        if (AetherControls.PrimaryButton(nextLabel, new Vector2(buttonWidth, 0f)))
        {
            Advance(view, snapshot);
        }

        // Keyboard paging while the card has focus.
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, false) || ImGui.IsKeyPressed(ImGuiKey.Enter, false))
            {
                Advance(view, snapshot);
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, false) && view.CanGoBack)
            {
                coordinator.Back(snapshot);
            }
        }

        DrawChaptersPopup(snapshot);
    }

    private void Advance(TutorialStepView view, TutorialContextSnapshot snapshot)
    {
        coordinator.Next(snapshot);
        if (view.IsLastStep && !coordinator.IsTutorialActive)
        {
            // Finish: back to My Plates, as the last card promises.
            host.Perform(TutorialAction.OpenMyPlates);
        }
    }

    private void DrawChaptersPopup(TutorialContextSnapshot snapshot)
    {
        if (pendingChaptersPopup)
        {
            pendingChaptersPopup = false;
            ImGui.OpenPopup(ChaptersPopupId);
        }

        using var popup = ImRaii.Popup(ChaptersPopupId);
        if (!popup.Success)
        {
            return;
        }

        var chapters = coordinator.Session.Chapters;
        var current = coordinator.Session.ChapterIndex;
        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            var label = $"{i + 1}.  {chapter.Title}";
            if (ImGui.Selectable(label, i == current))
            {
                coordinator.StartChapter(snapshot, i);
            }

            AetherControls.Tooltip(chapter.Summary);
        }
    }

    private static void Note(FontAwesomeIcon icon, Vector4 color, string text)
    {
        EditorWidgets.IconText(icon, color);
        ImGui.SameLine(0f, AetherMetrics.ItemInnerSpacing * ImGuiHelpers.GlobalScale);
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            ImGui.TextWrapped(text);
        }
    }

    private static string ActionLabel(TutorialAction action) => action switch
    {
        TutorialAction.OpenMyPlates => "Open My Plates",
        TutorialAction.OpenBasicEditor => "Switch to the Basic Editor",
        TutorialAction.OpenAdvancedEditor => "Switch to the Advanced Editor",
        _ => "Continue",
    };
}
