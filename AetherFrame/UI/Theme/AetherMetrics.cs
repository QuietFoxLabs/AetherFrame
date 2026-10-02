namespace AetherFrame.UI.Theme;

/// <summary>
/// AetherFrame's measurements, in unscaled pixels: the spacing scale, radii, borders and control
/// sizes every window builds from. Multiply by Dalamud's global UI scale when drawing (see
/// <c>EditorWidgets.Scaled</c>); nothing here is a screen pixel. Free of Dalamud types on purpose.
/// </summary>
internal static class AetherMetrics
{
    // ---------------------------------------------------------------- spacing scale (4 px grid)

    internal const float SpaceXs = 4f;
    internal const float SpaceSm = 8f;
    internal const float SpaceMd = 12f;
    internal const float SpaceLg = 16f;
    internal const float SpaceXl = 24f;
    internal const float SpaceXxl = 32f;

    // ---------------------------------------------------------------- radii

    /// <summary>Inputs, buttons, small chips.</summary>
    internal const float RadiusSm = 4f;

    /// <summary>Panels, popups, section frames.</summary>
    internal const float RadiusMd = 6f;

    /// <summary>Cards, callouts, the tutorial card, the window itself.</summary>
    internal const float RadiusLg = 10f;

    // ---------------------------------------------------------------- borders

    internal const float BorderThin = 1f;
    internal const float BorderStrong = 2f;

    /// <summary>The accent bar at the edge of a selected navigator row or a callout.</summary>
    internal const float AccentBarWidth = 3f;

    // ---------------------------------------------------------------- windows

    /// <summary>Padding inside a window's content region.</summary>
    internal const float WindowPadding = 12f;

    /// <summary>Padding inside a panel or card.</summary>
    internal const float PanelPadding = 10f;

    /// <summary>Padding inside an input or button frame (horizontal, vertical).</summary>
    internal const float FramePaddingX = 8f;
    internal const float FramePaddingY = 4f;

    /// <summary>Spacing between items (horizontal, vertical).</summary>
    internal const float ItemSpacingX = 8f;
    internal const float ItemSpacingY = 6f;

    /// <summary>Spacing between a control and its inline label.</summary>
    internal const float ItemInnerSpacing = 6f;

    internal const float ScrollbarSize = 10f;
    internal const float GrabMinSize = 10f;

    // ---------------------------------------------------------------- controls

    /// <summary>A primary or secondary button's minimum width, so a row of them lines up.</summary>
    internal const float ButtonMinWidth = 96f;

    /// <summary>A dialog's button width (Save / Discard / Cancel rows).</summary>
    internal const float DialogButtonWidth = 120f;

    /// <summary>The mark (the AetherFrame corner-bracket sigil) beside a window title.</summary>
    internal const float BrandMarkSize = 18f;

    /// <summary>A callout's icon column.</summary>
    internal const float CalloutIconWidth = 22f;

    // ---------------------------------------------------------------- tutorial

    /// <summary>How far the spotlight extends past the highlighted control.</summary>
    internal const float SpotlightMargin = 6f;

    /// <summary>The spotlight ring's thickness.</summary>
    internal const float SpotlightRing = 2f;

    /// <summary>The explanation card's width and its gap from the spotlight.</summary>
    internal const float TutorialCardWidth = 360f;
    internal const float TutorialCardGap = 14f;

    /// <summary>The explanation card stays this far inside the viewport's edge.</summary>
    internal const float TutorialCardViewportInset = 12f;
}
