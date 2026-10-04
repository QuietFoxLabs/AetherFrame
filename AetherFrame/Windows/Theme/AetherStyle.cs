using System.Collections.Generic;
using System.Numerics;
using AetherFrame.UI.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows.Theme;

/// <summary>
/// AetherFrame's ImGui style: the palette and metrics (<see cref="AetherPalette"/>,
/// <see cref="AetherMetrics"/>) mapped onto ImGui's style colors and variables, pushed for
/// AetherFrame's own windows only and popped again after each one. Nothing here touches
/// Dalamud's or another plugin's style: a push made in a window's PreDraw is undone in its
/// PostDraw (Dalamud calls PostDraw whenever it called PreDraw, including the frame Draw threw),
/// and a push made inside Draw lives in a <c>using</c> scope, so an exception unwinds it.
///
/// <para>Style variables are pushed at Dalamud's global UI scale, as Dalamud scales its own.</para>
/// </summary>
internal static class AetherStyle
{
    private static readonly (ImGuiCol Slot, Vector4 Color)[] Colors =
    [
        (ImGuiCol.Text, AetherPalette.TextPrimary),
        (ImGuiCol.TextDisabled, AetherPalette.TextMuted),
        (ImGuiCol.WindowBg, AetherPalette.Void.WithOpacity(0.985f)),
        (ImGuiCol.ChildBg, Vector4.Zero),
        (ImGuiCol.PopupBg, AetherPalette.Popup),
        (ImGuiCol.Border, AetherPalette.Border),
        (ImGuiCol.BorderShadow, Vector4.Zero),
        (ImGuiCol.FrameBg, AetherPalette.SurfaceRaised),
        (ImGuiCol.FrameBgHovered, AetherPalette.SurfaceHover),
        (ImGuiCol.FrameBgActive, AetherPalette.SurfaceActive),
        (ImGuiCol.TitleBg, AetherPalette.TitleBar),
        (ImGuiCol.TitleBgActive, AetherPalette.TitleBarActive),
        (ImGuiCol.TitleBgCollapsed, AetherPalette.TitleBar),
        (ImGuiCol.MenuBarBg, AetherPalette.Surface),
        (ImGuiCol.ScrollbarBg, Vector4.Zero),
        (ImGuiCol.ScrollbarGrab, new Vector4(1f, 1f, 1f, 0.12f)),
        (ImGuiCol.ScrollbarGrabHovered, new Vector4(1f, 1f, 1f, 0.20f)),
        (ImGuiCol.ScrollbarGrabActive, new Vector4(1f, 1f, 1f, 0.28f)),
        (ImGuiCol.CheckMark, AetherPalette.AetherHover),
        (ImGuiCol.SliderGrab, AetherPalette.Aether),
        (ImGuiCol.SliderGrabActive, AetherPalette.AetherHover),
        (ImGuiCol.Button, AetherPalette.SurfaceRaised),
        (ImGuiCol.ButtonHovered, AetherPalette.SurfaceHover),
        (ImGuiCol.ButtonActive, AetherPalette.SurfaceActive),
        (ImGuiCol.Header, AetherPalette.AetherFaint),
        (ImGuiCol.HeaderHovered, AetherPalette.AetherSoft),
        (ImGuiCol.HeaderActive, AetherPalette.Aether.WithOpacity(0.35f)),
        (ImGuiCol.Separator, AetherPalette.Divider),
        (ImGuiCol.SeparatorHovered, AetherPalette.Aether),
        (ImGuiCol.SeparatorActive, AetherPalette.AetherHover),
        (ImGuiCol.ResizeGrip, new Vector4(1f, 1f, 1f, 0.06f)),
        (ImGuiCol.ResizeGripHovered, AetherPalette.AetherSoft),
        (ImGuiCol.ResizeGripActive, AetherPalette.Aether),
        (ImGuiCol.Tab, AetherPalette.Surface),
        (ImGuiCol.TabHovered, AetherPalette.SurfaceHover),
        (ImGuiCol.TabActive, AetherPalette.SurfaceRaised),
        (ImGuiCol.TabUnfocused, AetherPalette.Surface),
        (ImGuiCol.TabUnfocusedActive, AetherPalette.SurfaceRaised),
        (ImGuiCol.DockingPreview, AetherPalette.AetherSoft),
        (ImGuiCol.DockingEmptyBg, AetherPalette.Void),
        (ImGuiCol.PlotLines, AetherPalette.Aether),
        (ImGuiCol.PlotLinesHovered, AetherPalette.AetherHover),
        (ImGuiCol.PlotHistogram, AetherPalette.Aether),
        (ImGuiCol.PlotHistogramHovered, AetherPalette.AetherHover),
        (ImGuiCol.TableHeaderBg, AetherPalette.Surface),
        (ImGuiCol.TableBorderStrong, AetherPalette.BorderStrong),
        (ImGuiCol.TableBorderLight, AetherPalette.Border),
        (ImGuiCol.TableRowBg, Vector4.Zero),
        (ImGuiCol.TableRowBgAlt, new Vector4(1f, 1f, 1f, 0.02f)),
        (ImGuiCol.TextSelectedBg, AetherPalette.Aether.WithOpacity(0.35f)),
        (ImGuiCol.DragDropTarget, AetherPalette.Glow),
        (ImGuiCol.NavHighlight, AetherPalette.Glow),
        (ImGuiCol.NavWindowingHighlight, AetherPalette.GlowSoft),
        (ImGuiCol.NavWindowingDimBg, AetherPalette.Dim),
        (ImGuiCol.ModalWindowDimBg, AetherPalette.ModalDim),
    ];

    /// <summary>Style variables holding a single value, in unscaled pixels (scaled at push time) or plain numbers.</summary>
    private static readonly (ImGuiStyleVar Var, float Value, bool Scaled)[] FloatVars =
    [
        (ImGuiStyleVar.WindowRounding, AetherMetrics.RadiusLg, true),
        (ImGuiStyleVar.WindowBorderSize, AetherMetrics.BorderThin, false),
        (ImGuiStyleVar.ChildRounding, AetherMetrics.RadiusMd, true),
        (ImGuiStyleVar.ChildBorderSize, 0f, false),
        (ImGuiStyleVar.PopupRounding, AetherMetrics.RadiusMd, true),
        (ImGuiStyleVar.PopupBorderSize, AetherMetrics.BorderThin, false),
        (ImGuiStyleVar.FrameRounding, AetherMetrics.RadiusSm, true),
        (ImGuiStyleVar.FrameBorderSize, 0f, false),
        (ImGuiStyleVar.IndentSpacing, AetherMetrics.SpaceLg, true),
        (ImGuiStyleVar.ScrollbarSize, AetherMetrics.ScrollbarSize, true),
        (ImGuiStyleVar.ScrollbarRounding, AetherMetrics.RadiusMd, true),
        (ImGuiStyleVar.GrabMinSize, AetherMetrics.GrabMinSize, true),
        (ImGuiStyleVar.GrabRounding, AetherMetrics.RadiusSm, true),
        (ImGuiStyleVar.TabRounding, AetherMetrics.RadiusSm, true),
        (ImGuiStyleVar.DisabledAlpha, 0.5f, false),
    ];

    /// <summary>Style variables holding a pair, in unscaled pixels.</summary>
    private static readonly (ImGuiStyleVar Var, Vector2 Value)[] VectorVars =
    [
        (ImGuiStyleVar.WindowPadding, new Vector2(AetherMetrics.WindowPadding)),
        (ImGuiStyleVar.FramePadding, new Vector2(AetherMetrics.FramePaddingX, AetherMetrics.FramePaddingY)),
        (ImGuiStyleVar.ItemSpacing, new Vector2(AetherMetrics.ItemSpacingX, AetherMetrics.ItemSpacingY)),
        (ImGuiStyleVar.ItemInnerSpacing, new Vector2(AetherMetrics.ItemInnerSpacing)),
        (ImGuiStyleVar.CellPadding, new Vector2(AetherMetrics.SpaceSm, AetherMetrics.SpaceXs)),
    ];

    // Every push made around a window's frame, until its pop: if a window's own PreDraw throws,
    // Dalamud never reaches PostDraw and never restores ImGui's stacks, so the plugin's draw
    // (Plugin.DrawUi) pops whatever is still outstanding before the exception leaves it.
    private static readonly List<IOutstandingStyle> Outstanding = new();

    /// <summary>How many colors <see cref="Push"/> pushes (what <see cref="Pop"/> pops).</summary>
    internal static int ColorCount => Colors.Length;

    /// <summary>How many style variables <see cref="Push"/> pushes.</summary>
    internal static int VarCount => FloatVars.Length + VectorVars.Length;

    /// <summary>Pushes the whole style. Pair with <see cref="Pop"/>.</summary>
    internal static void Push()
    {
        var scale = ImGuiHelpers.GlobalScale;
        foreach (var (slot, color) in Colors)
        {
            ImGui.PushStyleColor(slot, color);
        }

        foreach (var (var, value, scaled) in FloatVars)
        {
            ImGui.PushStyleVar(var, scaled ? value * scale : value);
        }

        foreach (var (var, value) in VectorVars)
        {
            ImGui.PushStyleVar(var, value * scale);
        }
    }

    /// <summary>Pops what <see cref="Push"/> pushed.</summary>
    internal static void Pop()
    {
        ImGui.PopStyleVar(VarCount);
        ImGui.PopStyleColor(ColorCount);
    }

    /// <summary>Records that <paramref name="owner"/> has pushes on ImGui's stacks until it pops them.</summary>
    internal static void NotePushed(IOutstandingStyle owner)
    {
        if (!Outstanding.Contains(owner))
        {
            Outstanding.Add(owner);
        }
    }

    /// <summary>Records that <paramref name="owner"/> popped what it pushed.</summary>
    internal static void NotePopped(IOutstandingStyle owner) => Outstanding.Remove(owner);

    /// <summary>
    /// Pops every push still outstanding: called when an exception leaves the plugin's draw, so a
    /// PreDraw that threw between its push and Dalamud's PostDraw can't leave AetherFrame's style on
    /// every window drawn after it. Nothing to do on a frame that ended normally.
    /// </summary>
    internal static void RecoverOutstanding()
    {
        for (var i = Outstanding.Count - 1; i >= 0; i--)
        {
            Outstanding[i].PopOutstanding();
        }

        Outstanding.Clear();
    }
}

/// <summary>Something that has pushed onto ImGui's style stacks and can pop it on demand.</summary>
internal interface IOutstandingStyle
{
    /// <summary>Pops what is still pushed, if anything; safe to call when nothing is.</summary>
    void PopOutstanding();
}

/// <summary>
/// Extra pushes a window makes around its frame beyond the shared style (a card's own surface,
/// say): counted, so they are popped in PostDraw or recovered after an exception.
/// </summary>
internal sealed class FramePushes : IOutstandingStyle
{
    private int colors;
    private int vars;

    /// <summary>Records <paramref name="colorCount"/> colors and <paramref name="varCount"/> variables just pushed.</summary>
    internal void Pushed(int colorCount, int varCount)
    {
        colors += colorCount;
        vars += varCount;
        AetherStyle.NotePushed(this);
    }

    /// <summary>Pops everything recorded.</summary>
    internal void Pop()
    {
        PopOutstanding();
        AetherStyle.NotePopped(this);
    }

    public void PopOutstanding()
    {
        if (vars > 0)
        {
            ImGui.PopStyleVar(vars);
        }

        if (colors > 0)
        {
            ImGui.PopStyleColor(colors);
        }

        vars = 0;
        colors = 0;
    }
}

/// <summary>
/// What every AetherFrame window does around its frame: the style, and the window policy the
/// tutorial needs. One instance per window, called from its PreDraw and PostDraw:
///
/// <code>
/// public override void PreDraw()  { chrome.PushStyle(); /* the window's own PreDraw */ chrome.ApplyPolicy(this); }
/// public override void PostDraw() { /* the window's own PostDraw */ chrome.PopStyle(); }
/// </code>
///
/// The style goes on first so a window's own pushes (the Plate Viewer's transparent presentation,
/// say) win over it; ImGui pops by count, so the order of the pops in PostDraw doesn't matter.
/// The policy goes on last so it wins over whatever flags the window's own PreDraw set.
/// </summary>
internal sealed class AetherWindowChrome : IOutstandingStyle
{
    private bool pushed;

    /// <summary>Pushes the style for this window; safe to call when already pushed.</summary>
    internal void PushStyle()
    {
        if (pushed)
        {
            return;
        }

        AetherStyle.Push();
        pushed = true;
        AetherStyle.NotePushed(this);
    }

    /// <summary>Pops the style if it was pushed this frame.</summary>
    internal void PopStyle()
    {
        PopOutstanding();
        AetherStyle.NotePopped(this);
    }

    public void PopOutstanding()
    {
        if (!pushed)
        {
            return;
        }

        pushed = false;
        AetherStyle.Pop();
    }

    /// <summary>
    /// The window policy for this frame. While the tutorial's spotlight is up, an AetherFrame
    /// window stays behind the dimming (it no longer jumps in front when clicked), so a click
    /// inside the spotlight reaches the control without letting the rest of the window out from
    /// under the dim; keyboard focus still moves to it as usual.
    ///
    /// While the screen eyedropper picks (issue #120), an AetherFrame window takes no mouse input:
    /// one on another monitor (Dalamud's multi-monitor windows) is a window of its own, out from
    /// under the eyedropper's cover, and a click there would select or drag instead of picking.
    /// Mouse input only, so ImGui still gives the focus back to the window the pick started from
    /// once it ends.
    /// </summary>
    internal static void ApplyPolicy(Window window)
    {
        if (Tutorial.TutorialOverlayState.IsSpotlightActive)
        {
            window.Flags |= ImGuiWindowFlags.NoBringToFrontOnFocus;
        }
        else
        {
            window.Flags &= ~ImGuiWindowFlags.NoBringToFrontOnFocus;
        }

        if (ScreenEyedropper.ClaimsInput)
        {
            window.Flags |= ImGuiWindowFlags.NoMouseInputs;
        }
        else
        {
            window.Flags &= ~ImGuiWindowFlags.NoMouseInputs;
        }
    }
}
