using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.UI.Theme;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows.Tutorial;

/// <summary>
/// The tutorial overlay's driver: an always-open, invisible, input-free window whose only job is
/// to run after every AetherFrame window has drawn (it is added to the window system right after
/// them) and compute this frame's overlay state from the anchors they marked, then draw the
/// spotlight ring over everything. The shade and card windows, added after it, read that state in
/// the same frame, so the spotlight follows a moved or resized window with no lag.
///
/// <para>Its work happens in PreDraw, which Dalamud does not guard, so it guards itself: a fault
/// stands the tutorial down (the dim never stays up over an interface the player can't reach) and
/// is logged, never thrown into the frame.</para>
/// </summary>
internal sealed class TutorialOverlayWindow : Window
{
    private const ImGuiWindowFlags DriverFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs
        | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
        | ImGuiWindowFlags.NoDocking;

    private readonly OnboardingCoordinator coordinator;
    private readonly ITutorialHost host;
    private readonly TutorialOverlayFrame frame;
    private readonly IReadOnlyList<Window> dimmedWindows;
    private readonly IReadOnlyList<Window> tutorialWindows;
    private readonly Func<TutorialTarget, bool> isAvailable;
    private int currentFrame;
    private int missingSince = -1;

    // What was last brought to the front: the step, how it showed, and the window it pointed into.
    private TutorialStep? raisedStep;
    private TutorialStepPresentation raisedPresentation;
    private uint raisedOwner;

    /// <param name="coordinator">The tutorial's state.</param>
    /// <param name="host">What the tutorial may see and do.</param>
    /// <param name="frame">The state shared with the shades and the card.</param>
    /// <param name="dimmedWindows">AetherFrame's own windows: what the dim covers.</param>
    /// <param name="tutorialWindows">The shades, then the card: kept in front of the window a step points into, in this order.</param>
    internal TutorialOverlayWindow(OnboardingCoordinator coordinator, ITutorialHost host, TutorialOverlayFrame frame, IReadOnlyList<Window> dimmedWindows, IReadOnlyList<Window> tutorialWindows)
        : base("AetherFrame Tutorial##AetherFrameTutorialDriver", DriverFlags, forceMainWindow: true)
    {
        this.coordinator = coordinator;
        this.host = host;
        this.frame = frame;
        this.dimmedWindows = dimmedWindows;
        this.tutorialWindows = tutorialWindows;
        isAvailable = target => TutorialOverlayState.Registry.IsAvailable(target, currentFrame);
        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        DisableFadeInFadeOut = true;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
        AllowBackgroundBlur = false;
    }

    /// <summary>Never closes: a toggle from elsewhere is undone.</summary>
    public override void PreOpenCheck() => IsOpen = true;

    public override void PreDraw()
    {
        currentFrame = ImGui.GetFrameCount();
        var viewport = ImGui.GetMainViewport();
        var work = new ScreenRect(viewport.WorkPos, viewport.WorkPos + viewport.WorkSize);

        try
        {
            Compute(currentFrame, work);
        }
        catch (Exception ex)
        {
            StandDown(currentFrame);
            coordinator.Suspend();
            DalamudServices.Log.Error(LogPrivacy.ForLog(ex), "AetherFrame's tutorial could not compute its overlay and has closed.");
        }

        // Parked in the corner, a pixel large: never seen, never hit. Set only once the frame's
        // state is settled, so nothing above can leave ImGui's next-window data armed for another window.
        ImGui.SetNextWindowPos(work.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(Vector2.One, ImGuiCond.Always);
    }

    private void StandDown(int frameCount)
    {
        frame.Clear(frameCount);
        TutorialOverlayState.IsSpotlightActive = false;
        TutorialOverlayState.WantedTarget = TutorialTarget.None;
        missingSince = -1;
        raisedStep = null;
        raisedOwner = 0;
    }

    private void Compute(int frameCount, ScreenRect work)
    {
        var registry = TutorialOverlayState.Registry;
        var snapshot = host.Snapshot();
        var view = coordinator.Tick(snapshot, isAvailable);
        if (view is not { } step)
        {
            StandDown(frameCount);
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var visible = ScreenRect.Empty;
        var owner = 0u;
        if (step.Target != TutorialTarget.None && registry.TryGet(step.Target, frameCount, out var anchor))
        {
            visible = anchor.VisibleBounds;
            owner = anchor.OwnerWindowId;
        }

        if (frame.CardSize.X <= 0f)
        {
            frame.CardSize = new Vector2(AetherMetrics.TutorialCardWidth, 220f) * scale;
        }

        frame.Set(
            frameCount, step, work, DimmedArea(), visible,
            AetherMetrics.SpotlightMargin * scale, AetherMetrics.TutorialCardGap * scale, AetherMetrics.TutorialCardViewportInset * scale);
        TutorialOverlayState.IsSpotlightActive = true;
        KeepInFront(step, owner);

        // What the windows may bring into view: the step's own control (also while it's missing,
        // so a scrolled region can reveal it), or the way to meet a prerequisite.
        var wanted = step.Presentation == TutorialStepPresentation.MissingTarget ? step.Step.Target : step.Target;
        TutorialOverlayState.WantedTarget = wanted;
        if (step.Presentation == TutorialStepPresentation.MissingTarget)
        {
            if (missingSince < 0)
            {
                missingSince = frameCount;
            }

            // One reveal request shortly after the control went missing (not every frame).
            if (frameCount - missingSince == 2)
            {
                registry.RequestReveal(step.Step.Target, frameCount);
            }
        }
        else
        {
            missingSince = -1;
        }
    }

    /// <summary>
    /// Keeps what the step explains visible: when the step changes, the window its control is in
    /// comes in front of AetherFrame's other windows (My Plates never hides the editor a step
    /// points into), and the shades and then the card come in front of that. Only the display
    /// order changes, never focus, so an open popup such as the Create Plate chooser stays open.
    /// It happens again whenever that window rises above the card (a popup opened later comes to
    /// the front by itself, and a modal popup would dim the card behind it); it doesn't happen
    /// every frame, so a menu the player opens afterwards still shows in front.
    ///
    /// <para>A popup is the exception. It is already in front of AetherFrame's windows, and a
    /// modal one dims everything behind it by itself, so the shades stay behind it and never cover
    /// any of it, its title bar included. Only the card comes in front of it, so the card isn't
    /// dimmed, and only when the card doesn't overlap the spotlight: a card over a modal popup
    /// can't be clicked, and would hide the part of the popup beneath it.</para>
    /// </summary>
    private void KeepInFront(TutorialStepView step, uint owner)
    {
        var changed = !ReferenceEquals(step.Step, raisedStep) || step.Presentation != raisedPresentation || owner != raisedOwner;
        if (!changed && !IsAboveCard(owner))
        {
            return;
        }

        raisedStep = step.Step;
        raisedPresentation = step.Presentation;
        raisedOwner = owner;

        var ownerWindow = owner != 0 ? ImGuiP.FindWindowByID(owner) : default;
        if (!ownerWindow.IsNull && (ownerWindow.Flags & ImGuiWindowFlags.Popup) != 0)
        {
            var card = new ScreenRect(frame.CardPosition, frame.CardPosition + frame.CardSize);
            if (tutorialWindows.Count > 0 && card.Intersect(frame.Hole).IsEmpty)
            {
                BringToDisplayFront(tutorialWindows[^1]);
            }

            return;
        }

        if (!ownerWindow.IsNull)
        {
            ImGuiP.BringWindowToDisplayFront(ownerWindow);
        }

        foreach (var tutorialWindow in tutorialWindows)
        {
            BringToDisplayFront(tutorialWindow);
        }
    }

    private static void BringToDisplayFront(Window window)
    {
        var imgui = ImGuiP.FindWindowByName(window.WindowName);
        if (!imgui.IsNull)
        {
            ImGuiP.BringWindowToDisplayFront(imgui);
        }
    }

    /// <summary>Whether the window <paramref name="owner"/> is drawn in front of the card.</summary>
    private bool IsAboveCard(uint owner)
    {
        if (owner == 0 || tutorialWindows.Count == 0)
        {
            return false;
        }

        var window = ImGuiP.FindWindowByID(owner);
        var card = ImGuiP.FindWindowByName(tutorialWindows[^1].WindowName);
        return !window.IsNull && !card.IsNull && ImGuiP.FindWindowDisplayIndex(window) > ImGuiP.FindWindowDisplayIndex(card);
    }

    /// <summary>
    /// The rectangle AetherFrame's open windows occupy this frame (their ImGui windows, looked up
    /// by name, as ImGui placed them). Only that is dimmed and blocked; the game and every other
    /// plugin stay reachable.
    /// </summary>
    private ScreenRect DimmedArea()
    {
        var area = ScreenRect.Empty;
        foreach (var window in dimmedWindows)
        {
            if (!window.IsOpen)
            {
                continue;
            }

            var imgui = ImGuiP.FindWindowByName(window.WindowName);
            if (imgui.IsNull || !imgui.WasActive || imgui.Hidden)
            {
                continue;
            }

            Vector2 pos = imgui.Pos;
            Vector2 size = imgui.Size;
            area = area.Union(new ScreenRect(pos, pos + size));
        }

        return area;
    }

    public override void Draw()
    {
        if (frame.View is not { } view || frame.Frame != ImGui.GetFrameCount() || frame.Hole.IsEmpty)
        {
            return;
        }

        // The ring, over everything: the glow accent, with the frame corners; a soft breath while
        // the player is meant to use the control, so the eye is drawn to it.
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetForegroundDrawList();
        var min = frame.Hole.Min;
        var max = frame.Hole.Max;
        var rounding = AetherMetrics.RadiusMd * scale;
        var pulse = frame.AllowInteraction ? 0.75f + (0.25f * MathF.Sin((float)ImGui.GetTime() * 3f)) : 1f;
        var ring = AetherPalette.Glow with { W = 0.95f * pulse };

        AetherControls.Glow(drawList, min, max, rounding, AetherPalette.GlowSoft with { W = AetherPalette.GlowSoft.W * pulse }, 10f * scale);
        drawList.AddRect(min, max, ImGui.GetColorU32(ring), rounding, ImDrawFlags.None, AetherMetrics.SpotlightRing * scale);
        AetherBrand.DrawCorners(drawList, min, max, 5f * scale, Math.Min(14f * scale, Math.Min(frame.Hole.Width, frame.Hole.Height) / 3f), Math.Max(1.5f, 2f * scale), ring);

        // A small hand marks a control that's meant to be used.
        if (frame.AllowInteraction && view.Presentation == TutorialStepPresentation.Spotlight)
        {
            using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
            {
                var glyph = EditorWidgets.GetIconString(Dalamud.Interface.FontAwesomeIcon.HandPointer);
                var size = ImGui.CalcTextSize(glyph);
                var pos = new Vector2(max.X + (6f * scale), max.Y - size.Y);
                if (pos.X + size.X > frame.Viewport.Max.X)
                {
                    pos.X = min.X - size.X - (6f * scale);
                }

                drawList.AddText(pos + new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f)), glyph);
                drawList.AddText(pos, ImGui.GetColorU32(ring), glyph);
            }
        }
    }
}
