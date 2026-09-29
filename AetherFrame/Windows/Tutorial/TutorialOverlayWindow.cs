using System;
using System.Numerics;
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
/// <para>Being a Dalamud window means an exception here is caught and logged per window rather
/// than ending the game's UI; everything it pushes lives in <c>using</c> scopes.</para>
/// </summary>
internal sealed class TutorialOverlayWindow : Window
{
    private const ImGuiWindowFlags DriverFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs
        | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
        | ImGuiWindowFlags.NoDocking;

    private readonly OnboardingCoordinator coordinator;
    private readonly ITutorialHost host;
    private readonly TutorialOverlayFrame frame;
    private int missingSince = -1;

    internal TutorialOverlayWindow(OnboardingCoordinator coordinator, ITutorialHost host, TutorialOverlayFrame frame)
        : base("AetherFrame Tutorial##AetherFrameTutorialDriver", DriverFlags)
    {
        this.coordinator = coordinator;
        this.host = host;
        this.frame = frame;
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
        var frameCount = ImGui.GetFrameCount();
        var viewport = ImGui.GetMainViewport();
        var work = new ScreenRect(viewport.WorkPos, viewport.WorkPos + viewport.WorkSize);

        // Parked in the corner, a pixel large: never seen, never hit.
        ImGui.SetNextWindowPos(work.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(Vector2.One, ImGuiCond.Always);

        try
        {
            Compute(frameCount, work);
        }
        catch
        {
            // A fault computing the overlay must never leave the dim up over an interface the
            // player can't reach: stand down for this frame, then rethrow for Dalamud's log.
            frame.Clear(frameCount);
            TutorialOverlayState.IsSpotlightActive = false;
            TutorialOverlayState.WantedTarget = TutorialTarget.None;
            throw;
        }
    }

    private void Compute(int frameCount, ScreenRect work)
    {
        var registry = TutorialOverlayState.Registry;
        var snapshot = host.Snapshot();
        var view = coordinator.Tick(snapshot, target => registry.IsAvailable(target, frameCount));
        if (view is not { } step)
        {
            frame.Clear(frameCount);
            TutorialOverlayState.IsSpotlightActive = false;
            TutorialOverlayState.WantedTarget = TutorialTarget.None;
            missingSince = -1;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var visible = ScreenRect.Empty;
        if (step.Target != TutorialTarget.None && registry.TryGet(step.Target, frameCount, out var anchor))
        {
            visible = anchor.VisibleBounds;
        }

        if (frame.CardSize.X <= 0f)
        {
            frame.CardSize = new Vector2(AetherMetrics.TutorialCardWidth, 220f) * scale;
        }

        frame.Set(frameCount, step, work, visible, AetherMetrics.SpotlightMargin * scale, AetherMetrics.TutorialCardGap * scale, AetherMetrics.TutorialCardViewportInset * scale);
        TutorialOverlayState.IsSpotlightActive = true;

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

    public override void Draw()
    {
        if (frame.View is not { } view || frame.Frame != ImGui.GetFrameCount())
        {
            return;
        }

        if (frame.Hole.IsEmpty)
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
            string glyph;
            using (Services.DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
            {
                glyph = EditorWidgets.GetIconString(Dalamud.Interface.FontAwesomeIcon.HandPointer);
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
