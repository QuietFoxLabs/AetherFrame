using System.Numerics;
using AetherFrame.UI.Theme;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows.Tutorial;

/// <summary>
/// One piece of the tutorial's dimming: four strips tile AetherFrame's windows around the
/// spotlight hole, and a fifth, transparent piece covers the hole itself on steps where the
/// control is only looked at. Each is an ordinary ImGui window that takes mouse input, so a click
/// on the dim lands on it and nowhere else (and hands focus to the card), while a click inside the
/// hole (when no cover is up) reaches the control beneath. On a step that has nothing to point at
/// and needs the player to get somewhere, the pieces take no input at all: the dim is then only a
/// tint. A piece with nothing to cover this frame is parked off screen, never closed, so it keeps
/// its place in front and never re-steals focus by reappearing.
///
/// <para>On the frame a piece first appears it may come to the front (that is how ImGui places a
/// new window); from the next frame it never jumps in front of the card when clicked. It is
/// always on the main viewport, so a parked piece can't become a window of its own on another monitor.</para>
/// </summary>
internal sealed class TutorialShadeWindow : Window
{
    /// <summary>The hole cover's index; 0 to 3 are the strips.</summary>
    internal const int HoleCoverIndex = SpotlightGeometry.MaxStrips;

    private const ImGuiWindowFlags ShadeFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking;

    private readonly TutorialOverlayFrame frame;
    private readonly int index;
    private readonly FramePushes pushes = new();
    private int framesOpen;

    internal TutorialShadeWindow(TutorialOverlayFrame frame, int index)
        : base($"AetherFrame Tutorial Shade##AetherFrameTutorialShade{index}", ShadeFlags, forceMainWindow: true)
    {
        this.frame = frame;
        this.index = index;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        DisableFadeInFadeOut = true;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
        AllowBackgroundBlur = false;
    }

    private bool IsHoleCover => index == HoleCoverIndex;

    public override void OnOpen() => framesOpen = 0;

    public override void PreDraw()
    {
        var current = frame.IsActive && frame.Frame == ImGui.GetFrameCount();
        var rect = !current ? ScreenRect.Empty
            : IsHoleCover ? (frame.AllowInteraction ? ScreenRect.Empty : frame.Hole)
            : frame.Strip(index);

        if (rect.IsEmpty)
        {
            // Parked: a pixel, just past the viewport's far corner.
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.Pos + viewport.Size + new Vector2(64f), ImGuiCond.Always);
            ImGui.SetNextWindowSize(Vector2.One, ImGuiCond.Always);
        }
        else
        {
            ImGui.SetNextWindowPos(rect.Min, ImGuiCond.Always);
            ImGui.SetNextWindowSize(rect.Size, ImGuiCond.Always);
        }

        var flags = ShadeFlags;
        if (framesOpen > 0)
        {
            flags |= ImGuiWindowFlags.NoBringToFrontOnFocus;
        }

        if (current && frame.PassThrough)
        {
            flags |= ImGuiWindowFlags.NoInputs;
        }

        Flags = flags;
        framesOpen++;

        // Popped in PostDraw (Dalamud pairs the two), or recovered by the plugin's draw if a later
        // PreDraw throws. A strip thinner than ImGui's minimum window size would otherwise be
        // widened over the hole, so the minimum is lifted here.
        var dim = IsHoleCover ? Vector4.Zero : current && frame.PassThrough ? AetherPalette.ModalDim : AetherPalette.Dim;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, dim);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        pushes.Pushed(1, 4);
    }

    public override void PostDraw() => pushes.Pop();

    public override void Draw()
    {
        // The dim is the window's own background. A click on it focuses this piece; the card takes
        // that focus over, so Escape and the arrow keys keep meaning the tutorial. A hovered shade
        // shows the ordinary cursor: nothing suggests the dimmed interface can be used.
        if (ImGui.IsWindowFocused())
        {
            frame.CardFocusRequested = true;
        }

        if (ImGui.IsWindowHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Arrow);
        }
    }
}
