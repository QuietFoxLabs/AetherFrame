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
/// viewport. Escape closes it (the tour is remembered where it stopped); Left, Right and Enter
/// page while the pointer is over the focused card.
/// </summary>
internal sealed class TutorialCardWindow : Window
{
    private const ImGuiWindowFlags CardFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking
        | ImGuiWindowFlags.AlwaysAutoResize;

    private const string ChaptersPopupId = "##AetherFrameTutorialChapters";
    // How long the spotlight flashes after Next is pressed on a step that waits for the player.
    private const double FlashSeconds = 1.2;

    // Next pressed while a step needs something first: the highlighted control is the way there.
    private const string GetThereFirst = "Use the highlighted control to continue: this step needs it first.";

    private const string CloseTooltip ="Close the tutorial for now. Help in My Plates resumes it where you left off.";

    private readonly OnboardingCoordinator coordinator;
    private readonly ITutorialHost host;
    private readonly TutorialOverlayFrame frame;
    private readonly AetherWindowChrome chrome = new();
    private readonly FramePushes pushes = new();

    // Escape on the chapter list closes only the list; the tour carries on.
    private readonly PopupEscapeGuard escape = new();

    // Built once: the chapter line and the picker's rows, so the card allocates nothing per frame.
    private readonly string[] chapterLines;
    private readonly string[] chapterRows;
    private int framesOpen;
    private bool pendingChaptersPopup;

    // The step whose Next was pressed before the player did what it waits for; its hint shows until the step moves on.
    private TutorialStep? heldStep;

    internal TutorialCardWindow(OnboardingCoordinator coordinator, ITutorialHost host, TutorialOverlayFrame frame)
        : base("AetherFrame Tutorial Card##AetherFrameTutorialCard", CardFlags, forceMainWindow: true)
    {
        this.coordinator = coordinator;
        this.host = host;
        this.frame = frame;
        RespectCloseHotkey = true;
        DisableWindowSounds = true;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
        AllowBackgroundBlur = true;

        var chapters = coordinator.Session.Chapters;
        chapterLines = new string[chapters.Count];
        chapterRows = new string[chapters.Count];
        for (var i = 0; i < chapters.Count; i++)
        {
            chapterLines[i] = $"CHAPTER {i + 1} OF {chapters.Count}  ·  {chapters[i].Title.ToUpperInvariant()}";
            chapterRows[i] = $"{i + 1}.  {chapters[i].Title}";
        }
    }

    public override void OnOpen() => framesOpen = 0;

    /// <summary>Closed by Escape, or by Dalamud after a fault: the tour stops here and is remembered.</summary>
    public override void OnClose() => coordinator.Suspend();

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowPos(frame.CardPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(AetherMetrics.TutorialCardWidth * scale, 0f), ImGuiCond.Always);

        // The card appears after the shades in the same frame, so for its first frames it asks
        // to come forward; from then on nothing of AetherFrame's can get in front of it. A click
        // on the dim hands focus over here too.
        if (framesOpen < 3 || frame.CardFocusRequested)
        {
            frame.CardFocusRequested = false;
            BringToFront();
        }

        framesOpen++;

        chrome.PushStyle();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, AetherPalette.SurfaceRaised.WithOpacity(0.985f));
        ImGui.PushStyleColor(ImGuiCol.Border, AetherPalette.BorderStrong);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, AetherMetrics.RadiusLg * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(AetherMetrics.SpaceLg, AetherMetrics.SpaceMd) * scale);
        pushes.Pushed(2, 3);
    }

    public override void PostDraw()
    {
        pushes.Pop();
        chrome.PopStyle();
    }

    public override void Draw()
    {
        escape.Update(this);
        frame.CardSize = ImGui.GetWindowSize();
        if (frame.View is not { } view || frame.Frame != ImGui.GetFrameCount())
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var snapshot = host.Snapshot();
        var contentWidth = ImGui.GetContentRegionAvail().X;

        // ---- header: the mark, the chapter, and the close. The close is pinned to the top right
        // first, and the chapter line wraps short of it, so a long chapter name never runs under it.
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorPos();
        var origin = ImGui.GetCursorScreenPos();
        var markSize = AetherMetrics.BrandMarkSize * scale;
        var closeSize = ImGui.GetFrameHeight();
        var gap = AetherMetrics.SpaceSm * scale;
        AetherBrand.DrawMark(drawList, origin + new Vector2(markSize / 2f, closeSize / 2f), markSize, AetherPalette.Aether, AetherPalette.Glow);

        ImGui.SetCursorPos(start + new Vector2(contentWidth - closeSize, 0f));
        if (EditorWidgets.IconButton("CloseTutorial", FontAwesomeIcon.Times, CloseTooltip))
        {
            coordinator.Suspend();
            return;
        }

        var belowClose = ImGui.GetCursorPosY();
        ImGui.SetCursorPos(start + new Vector2(markSize + gap, 0f));
        ImGui.AlignTextToFramePadding();
        ImGui.PushTextWrapPos(start.X + contentWidth - closeSize - gap);
        using (AetherFonts.Label())
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.Aether))
        {
            ImGui.TextUnformatted(chapterLines[view.ChapterIndex]);
        }

        ImGui.PopTextWrapPos();
        ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), belowClose));

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

            case TutorialStepPresentation.Prerequisite:
                Note(FontAwesomeIcon.LocationArrow, AetherPalette.Glow, "The interface stays usable: go ahead, the tour waits.");
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
                Note(FontAwesomeIcon.EyeSlash, AetherPalette.Warning, view.NextHeld
                    ? "The control isn't in view; the interface stays usable."
                    : "The control isn't in view; the interface stays usable, and Next continues anyway.");
                break;
        }

        // ---- Next was pressed on a step that waits for the player: say what it waits for
        if (!view.NextHeld || !ReferenceEquals(heldStep, view.Step))
        {
            heldStep = null;
        }
        else if ((view.Presentation == TutorialStepPresentation.Prerequisite ? GetThereFirst : view.Step.WaitHint) is { } hint)
        {
            ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceXs * scale));
            Note(FontAwesomeIcon.ExclamationCircle, AetherPalette.Warning, hint);
        }

        // ---- progress
        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceSm * scale));
        var barMin = ImGui.GetCursorScreenPos();
        var barHeight = 3f * scale;
        var fraction = view.TotalSteps > 0 ? view.StepNumber / (float)view.TotalSteps : 0f;
        drawList.AddRectFilled(barMin, barMin + new Vector2(contentWidth, barHeight), ImGui.GetColorU32(AetherPalette.Border), barHeight / 2f);
        drawList.AddRectFilled(barMin, barMin + new Vector2(contentWidth * fraction, barHeight), ImGui.GetColorU32(AetherPalette.Aether), barHeight / 2f);
        ImGui.Dummy(new Vector2(contentWidth, barHeight + (AetherMetrics.SpaceXs * scale)));

        // ---- footer, two rows so nothing overlaps at any step: the step count with Chapters at
        // the right; then Skip tour, with Back (not on the first step) and Next at the right.
        var style = ImGui.GetStyle();
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, AetherPalette.TextMuted);
        ImGui.Text($"Step {view.StepNumber} of {view.TotalSteps}");
        ImGui.PopStyleColor();

        var chaptersWidth = ImGui.CalcTextSize("Chapters").X + (style.FramePadding.X * 2f);
        ImGui.SameLine();
        AlignRight(chaptersWidth);
        if (AetherControls.GhostButton("Chapters", new Vector2(chaptersWidth, 0f), "Jump to a chapter."))
        {
            pendingChaptersPopup = true;
        }

        if (AetherControls.GhostButton("Skip tour", tooltip: "Leave the tutorial. You can start it again from Help."))
        {
            coordinator.SkipTutorial();
            return;
        }

        var nextLabel = view.IsLastStep ? "Finish" : "Next";
        var buttonWidth = Math.Max(ImGui.CalcTextSize(nextLabel).X, ImGui.CalcTextSize("Back").X) + (style.FramePadding.X * 4f);
        var rowWidth = view.CanGoBack ? (buttonWidth * 2f) + style.ItemSpacing.X : buttonWidth;
        ImGui.SameLine();
        AlignRight(rowWidth);
        if (view.CanGoBack)
        {
            if (AetherControls.SecondaryButton("Back", new Vector2(buttonWidth, 0f)))
            {
                coordinator.Back(snapshot);
            }

            ImGui.SameLine();
        }

        if (AetherControls.PrimaryButton(nextLabel, new Vector2(buttonWidth, 0f)))
        {
            Advance(view, snapshot);
        }

        // Keyboard paging: only while the pointer is over the focused card itself (not its
        // popup), so Enter in the game's chat or arrows in the editor never page the tour.
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows)
            && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootWindow | ImGuiFocusedFlags.NoPopupHierarchy)
            && !ImGui.IsPopupOpen(ChaptersPopupId))
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

    /// <summary>After a SameLine: moves the cursor so an item <paramref name="width"/> wide ends at the right edge, unless that would overlap what is already on the line.</summary>
    private static void AlignRight(float width)
    {
        var x = ImGui.GetWindowContentRegionMax().X - width;
        if (x > ImGui.GetCursorPosX())
        {
            ImGui.SetCursorPosX(x);
        }
    }

    private void Advance(TutorialStepView view, TutorialContextSnapshot snapshot)
    {
        if (!coordinator.Next(snapshot))
        {
            // The step waits for the player: say what to do, and flash the control to click,
            // instead of moving on.
            heldStep = view.Step;
            frame.FlashUntil = ImGui.GetTime() + FlashSeconds;
            return;
        }

        heldStep = null;
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
            if (ImGui.Selectable(chapterRows[i], i == current))
            {
                coordinator.StartChapter(snapshot, i);
            }

            AetherControls.Tooltip(chapters[i].Summary);
        }
    }

    private static void Note(FontAwesomeIcon icon, Vector4 color, string text)
    {
        EditorWidgets.IconText(icon, color);
        ImGui.SameLine(0f, AetherMetrics.ItemInnerSpacing * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        try
        {
            ImGui.TextWrapped(text);
        }
        finally
        {
            ImGui.PopStyleColor();
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
