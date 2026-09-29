using System;
using System.Numerics;

namespace AetherFrame.UI.Theme;

/// <summary>
/// AetherFrame's colors: the one place every color in the plugin's own UI comes from, named by
/// role rather than by hue so a surface, a line, a text tone or an accent is picked for what it
/// means, and the same everywhere (My Plates, both editors, the Plate Viewer, prompts, the tutorial).
///
/// <para>The identity: deep midnight surfaces that sit quietly over the game, one aetherial
/// violet-blue accent for what's selected, active or primary, a restrained cyan glow only for
/// focus and the tutorial spotlight, and the warm gold FFXIV players already read as "chosen" for
/// the Active Plate. Text is near-white, never pure white; lines are translucent so they take a
/// little of whatever is behind them. Nothing neon, nothing decorative that costs contrast.</para>
///
/// <para>Values are sRGB in 0..1 as ImGui takes them. The contrast tests in
/// <c>AetherPaletteTests</c> hold every text tone to a readable ratio on every surface it is used
/// on, so a retune here can't quietly make a label unreadable. Free of Dalamud types on purpose.</para>
/// </summary>
internal static class AetherPalette
{
    // ---------------------------------------------------------------- surfaces (back to front)

    /// <summary>The window itself: the deepest surface, behind everything.</summary>
    internal static readonly Vector4 Void = Rgb(0x14, 0x16, 0x21);

    /// <summary>A panel inside a window (navigator, inspector, layers, the card grid).</summary>
    internal static readonly Vector4 Surface = Rgb(0x1B, 0x1D, 0x29);

    /// <summary>A raised piece on a panel: a card, an input frame, a popup, the tutorial card.</summary>
    internal static readonly Vector4 SurfaceRaised = Rgb(0x24, 0x27, 0x36);

    /// <summary>A raised piece under the pointer.</summary>
    internal static readonly Vector4 SurfaceHover = Rgb(0x2D, 0x31, 0x43);

    /// <summary>A raised piece being pressed.</summary>
    internal static readonly Vector4 SurfaceActive = Rgb(0x36, 0x3A, 0x4F);

    /// <summary>The title bar of an unfocused window (a little deeper than the window).</summary>
    internal static readonly Vector4 TitleBar = Rgb(0x10, 0x12, 0x1B);

    /// <summary>The title bar of the focused window.</summary>
    internal static readonly Vector4 TitleBarActive = Rgb(0x1C, 0x1E, 0x2E);

    /// <summary>A popup or tooltip: raised, and slightly more opaque than a panel so it reads over anything.</summary>
    internal static readonly Vector4 Popup = Rgb(0x1F, 0x22, 0x30, 0.98f);

    /// <summary>The workspace behind a Plate canvas in the editors (deeper than a panel, so the Plate is what glows).</summary>
    internal static readonly Vector4 Workspace = Rgb(0x0E, 0x10, 0x18);

    // ---------------------------------------------------------------- lines

    /// <summary>A quiet border or divider: translucent, so it takes a little of what's behind it.</summary>
    internal static readonly Vector4 Border = new(1f, 1f, 1f, 0.08f);

    /// <summary>A border that must be seen: a card's edge, an input's edge on hover.</summary>
    internal static readonly Vector4 BorderStrong = new(0.62f, 0.64f, 0.84f, 0.32f);

    /// <summary>A separator between sections.</summary>
    internal static readonly Vector4 Divider = new(1f, 1f, 1f, 0.10f);

    // ---------------------------------------------------------------- text

    /// <summary>Body text and labels: near-white, never pure white.</summary>
    internal static readonly Vector4 TextPrimary = Rgb(0xEC, 0xEC, 0xF5);

    /// <summary>Secondary text: summaries, values, a card's status line.</summary>
    internal static readonly Vector4 TextSecondary = Rgb(0xB9, 0xBC, 0xCF);

    /// <summary>Muted text: hints, property labels, empty-state explanations, disabled labels.</summary>
    internal static readonly Vector4 TextMuted = Rgb(0x8A, 0x8F, 0xA8);

    /// <summary>Text on a filled accent (a primary button) or on gold (the Active badge).</summary>
    internal static readonly Vector4 TextOnAccent = Rgb(0xF7, 0xF7, 0xFF);

    /// <summary>Text on the gold badge.</summary>
    internal static readonly Vector4 TextOnGold = Rgb(0x1A, 0x14, 0x05);

    // ---------------------------------------------------------------- accents

    /// <summary>The aether: AetherFrame's one accent. Selection, the active mode, the primary action, section rules.</summary>
    internal static readonly Vector4 Aether = Rgb(0x7F, 0x74, 0xF3);

    /// <summary>The accent under the pointer.</summary>
    internal static readonly Vector4 AetherHover = Rgb(0x93, 0x89, 0xFA);

    /// <summary>The accent while pressed.</summary>
    internal static readonly Vector4 AetherActive = Rgb(0x6A, 0x5F, 0xE0);

    /// <summary>A soft tint of the accent behind a selected row or an active toggle.</summary>
    internal static readonly Vector4 AetherSoft = new(0.50f, 0.46f, 0.95f, 0.18f);

    /// <summary>A softer tint still, for a hovered row.</summary>
    internal static readonly Vector4 AetherFaint = new(0.50f, 0.46f, 0.95f, 0.10f);

    /// <summary>The glow: restrained cyan, only for keyboard focus and the tutorial spotlight.</summary>
    internal static readonly Vector4 Glow = Rgb(0x7F, 0xDE, 0xF4);

    /// <summary>The glow as a translucent halo.</summary>
    internal static readonly Vector4 GlowSoft = new(0.50f, 0.87f, 0.96f, 0.35f);

    /// <summary>Gold: the Active Plate, the one FFXIV-flavored accent. Used for nothing else.</summary>
    internal static readonly Vector4 Gold = Rgb(0xF2, 0xC7, 0x4D);

    // ---------------------------------------------------------------- semantic

    internal static readonly Vector4 Success = Rgb(0x73, 0xD9, 0x8C);
    internal static readonly Vector4 Warning = Rgb(0xFF, 0xB8, 0x52);
    internal static readonly Vector4 Danger = Rgb(0xF9, 0x73, 0x73);
    internal static readonly Vector4 Info = Glow;

    /// <summary>A filled destructive button (Delete, Revert, Discard).</summary>
    internal static readonly Vector4 DangerButton = Rgb(0x9E, 0x38, 0x3E);
    internal static readonly Vector4 DangerButtonHover = Rgb(0xB8, 0x44, 0x4B);
    internal static readonly Vector4 DangerButtonActive = Rgb(0x86, 0x2E, 0x34);

    /// <summary>A tinted background behind a callout of each kind (the text on it is the semantic color).</summary>
    internal static readonly Vector4 InfoTint = new(0.50f, 0.87f, 0.96f, 0.10f);
    internal static readonly Vector4 SuccessTint = new(0.45f, 0.85f, 0.55f, 0.10f);
    internal static readonly Vector4 WarningTint = new(1.00f, 0.72f, 0.32f, 0.12f);
    internal static readonly Vector4 DangerTint = new(0.98f, 0.45f, 0.45f, 0.12f);

    // ---------------------------------------------------------------- overlays

    /// <summary>The tutorial's dimming of everything but the spotlight.</summary>
    internal static readonly Vector4 Dim = new(0.03f, 0.03f, 0.06f, 0.62f);

    /// <summary>A modal prompt's dimming of the window behind it.</summary>
    internal static readonly Vector4 ModalDim = new(0.03f, 0.03f, 0.06f, 0.45f);

    /// <summary>A color from 8-bit sRGB components, with an optional alpha.</summary>
    internal static Vector4 Rgb(byte r, byte g, byte b, float alpha = 1f) => new(r / 255f, g / 255f, b / 255f, alpha);

    /// <summary>The color with its alpha replaced.</summary>
    internal static Vector4 WithAlpha(this Vector4 color, float alpha) => color with { W = alpha };

    /// <summary>
    /// <paramref name="front"/> composited over <paramref name="back"/> (straight alpha), which is
    /// what a translucent surface looks like on the one behind it; used by the contrast tests.
    /// </summary>
    internal static Vector4 Over(Vector4 front, Vector4 back)
    {
        var a = Math.Clamp(front.W, 0f, 1f);
        var b = Math.Clamp(back.W, 0f, 1f);
        var outA = a + (b * (1f - a));
        if (outA <= 0f)
        {
            return Vector4.Zero;
        }

        var rgb = ((new Vector3(front.X, front.Y, front.Z) * a) + (new Vector3(back.X, back.Y, back.Z) * b * (1f - a))) / outA;
        return new Vector4(rgb, outA);
    }

    /// <summary>WCAG relative luminance of an opaque sRGB color.</summary>
    internal static double Luminance(Vector4 color)
    {
        static double Channel(float c)
        {
            var v = Math.Clamp(c, 0f, 1f);
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.X)) + (0.7152 * Channel(color.Y)) + (0.0722 * Channel(color.Z));
    }

    /// <summary>WCAG contrast ratio between two opaque colors (1 to 21).</summary>
    internal static double Contrast(Vector4 a, Vector4 b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (light, dark) = la >= lb ? (la, lb) : (lb, la);
        return (light + 0.05) / (dark + 0.05);
    }
}
