using System.Collections.Generic;
using System.Numerics;
using AetherFrame.UI.Theme;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The design tokens stay readable: every text tone keeps a WCAG-style contrast ratio on every
/// surface it is drawn on, accents stay distinguishable from their surfaces, and translucent lines
/// still show. A retune of <see cref="AetherPalette"/> that breaks one of these fails here before
/// anyone squints at it in game.
/// </summary>
public class AetherPaletteTests
{
    public static IEnumerable<object[]> Surfaces() =>
    [
        [nameof(AetherPalette.Void), AetherPalette.Void],
        [nameof(AetherPalette.Surface), AetherPalette.Surface],
        [nameof(AetherPalette.SurfaceRaised), AetherPalette.SurfaceRaised],
        [nameof(AetherPalette.SurfaceHover), AetherPalette.SurfaceHover],
        [nameof(AetherPalette.SurfaceActive), AetherPalette.SurfaceActive],
        [nameof(AetherPalette.TitleBar), AetherPalette.TitleBar],
        [nameof(AetherPalette.TitleBarActive), AetherPalette.TitleBarActive],
        [nameof(AetherPalette.Popup), AetherPalette.Popup],
        [nameof(AetherPalette.Workspace), AetherPalette.Workspace],
    ];

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void PrimaryText_IsHighContrastOnEverySurface(string surfaceName, Vector4 surface)
    {
        var ratio = AetherPalette.Contrast(AetherPalette.TextPrimary, AetherPalette.Over(surface, AetherPalette.Void));

        Assert.True(ratio >= 9.0, $"{nameof(AetherPalette.TextPrimary)} on {surfaceName}: {ratio:0.00}");
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void SecondaryText_IsReadableOnEverySurface(string surfaceName, Vector4 surface)
    {
        var ratio = AetherPalette.Contrast(AetherPalette.TextSecondary, AetherPalette.Over(surface, AetherPalette.Void));

        Assert.True(ratio >= 5.5, $"{nameof(AetherPalette.TextSecondary)} on {surfaceName}: {ratio:0.00}");
    }

    /// <summary>
    /// Muted text (hints, property labels) is body text on the resting surfaces, so it meets the
    /// body-text ratio there; on a hovered or pressed surface only the row's own label shows, and
    /// muted text need only stay legible.
    /// </summary>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public void MutedText_MeetsBodyTextContrastOnRestingSurfaces(string surfaceName, Vector4 surface)
    {
        var ratio = AetherPalette.Contrast(AetherPalette.TextMuted, AetherPalette.Over(surface, AetherPalette.Void));
        var resting = surfaceName is not (nameof(AetherPalette.SurfaceHover) or nameof(AetherPalette.SurfaceActive));

        Assert.True(ratio >= (resting ? 4.5 : 3.0), $"{nameof(AetherPalette.TextMuted)} on {surfaceName}: {ratio:0.00}");
    }

    public static IEnumerable<object[]> SemanticTones() =>
    [
        [nameof(AetherPalette.Aether), AetherPalette.Aether],
        [nameof(AetherPalette.Glow), AetherPalette.Glow],
        [nameof(AetherPalette.Gold), AetherPalette.Gold],
        [nameof(AetherPalette.Success), AetherPalette.Success],
        [nameof(AetherPalette.Warning), AetherPalette.Warning],
        [nameof(AetherPalette.Danger), AetherPalette.Danger],
    ];

    [Theory]
    [MemberData(nameof(SemanticTones))]
    public void AccentAndSemanticTones_ReadAsTextOnTheWindowAndOnPanels(string name, Vector4 tone)
    {
        Assert.True(AetherPalette.Contrast(tone, AetherPalette.Void) >= 4.5, $"{name} on Void");
        Assert.True(AetherPalette.Contrast(tone, AetherPalette.Surface) >= 4.5, $"{name} on Surface");
        Assert.True(AetherPalette.Contrast(tone, AetherPalette.SurfaceRaised) >= 4.0, $"{name} on SurfaceRaised");
    }

    [Fact]
    public void TextOnFilledAccents_IsReadable()
    {
        Assert.True(AetherPalette.Contrast(AetherPalette.TextOnAccent, AetherPalette.Aether) >= 3.0);
        Assert.True(AetherPalette.Contrast(AetherPalette.TextOnAccent, AetherPalette.AetherActive) >= 3.0);
        Assert.True(AetherPalette.Contrast(AetherPalette.TextOnAccent, AetherPalette.DangerButton) >= 4.5);
        Assert.True(AetherPalette.Contrast(AetherPalette.TextOnGold, AetherPalette.Gold) >= 7.0);
    }

    [Fact]
    public void Surfaces_StepForwardInBrightness_SoLayersReadAsLayers()
    {
        Assert.True(AetherPalette.Luminance(AetherPalette.Workspace) < AetherPalette.Luminance(AetherPalette.Void));
        Assert.True(AetherPalette.Luminance(AetherPalette.Void) < AetherPalette.Luminance(AetherPalette.Surface));
        Assert.True(AetherPalette.Luminance(AetherPalette.Surface) < AetherPalette.Luminance(AetherPalette.SurfaceRaised));
        Assert.True(AetherPalette.Luminance(AetherPalette.SurfaceRaised) < AetherPalette.Luminance(AetherPalette.SurfaceHover));
        Assert.True(AetherPalette.Luminance(AetherPalette.SurfaceHover) < AetherPalette.Luminance(AetherPalette.SurfaceActive));
    }

    [Fact]
    public void AccentStates_AreDistinct()
    {
        Assert.NotEqual(AetherPalette.Aether, AetherPalette.AetherHover);
        Assert.NotEqual(AetherPalette.Aether, AetherPalette.AetherActive);
        Assert.True(AetherPalette.Luminance(AetherPalette.AetherHover) > AetherPalette.Luminance(AetherPalette.Aether));
        Assert.True(AetherPalette.Luminance(AetherPalette.AetherActive) < AetherPalette.Luminance(AetherPalette.Aether));
    }

    [Fact]
    public void TranslucentLines_StillShowOnTheirSurfaces()
    {
        // A border on a panel and a divider on a window must be visible, though quiet.
        var borderOnSurface = AetherPalette.Over(AetherPalette.Border, AetherPalette.Surface);
        var dividerOnVoid = AetherPalette.Over(AetherPalette.Divider, AetherPalette.Void);

        Assert.True(AetherPalette.Contrast(borderOnSurface, AetherPalette.Surface) >= 1.25);
        Assert.True(AetherPalette.Contrast(dividerOnVoid, AetherPalette.Void) >= 1.3);
        Assert.True(AetherPalette.Contrast(AetherPalette.Over(AetherPalette.BorderStrong, AetherPalette.SurfaceRaised), AetherPalette.SurfaceRaised) >= 1.8);
    }

    [Fact]
    public void Dimming_LeavesTheSpotlightObvious()
    {
        // What the tutorial dims loses at least half its brightness, so the lit spotlight stands
        // out, and the modal dim is lighter than the tutorial's (a prompt only asks; a tutorial focuses).
        foreach (var surface in new[] { AetherPalette.Surface, AetherPalette.SurfaceRaised, AetherPalette.SurfaceActive })
        {
            var dimmed = AetherPalette.Over(AetherPalette.Dim, surface);
            Assert.True(AetherPalette.Luminance(dimmed) <= AetherPalette.Luminance(surface) * 0.5);
        }

        Assert.True(AetherPalette.ModalDim.W < AetherPalette.Dim.W);
    }

    [Fact]
    public void Over_CompositesStraightAlpha()
    {
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), AetherPalette.Over(new Vector4(1f, 0f, 0f, 1f), new Vector4(0f, 0f, 1f, 1f)));
        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), AetherPalette.Over(new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 1f)));
        var half = AetherPalette.Over(new Vector4(1f, 1f, 1f, 0.5f), new Vector4(0f, 0f, 0f, 1f));
        Assert.Equal(0.5f, half.X, 3);
        Assert.Equal(1f, half.W, 3);
    }

    [Fact]
    public void Contrast_MatchesTheWcagFormula()
    {
        Assert.Equal(21.0, AetherPalette.Contrast(Vector4.One, new Vector4(0f, 0f, 0f, 1f)), 3);
        Assert.Equal(1.0, AetherPalette.Contrast(AetherPalette.Aether, AetherPalette.Aether), 3);
        Assert.Equal(0.0, AetherPalette.Luminance(new Vector4(0f, 0f, 0f, 1f)), 6);
        Assert.Equal(1.0, AetherPalette.Luminance(Vector4.One), 6);
    }

    [Fact]
    public void Metrics_FollowTheFourPixelGrid()
    {
        foreach (var value in new[] { AetherMetrics.SpaceXs, AetherMetrics.SpaceSm, AetherMetrics.SpaceMd, AetherMetrics.SpaceLg, AetherMetrics.SpaceXl, AetherMetrics.SpaceXxl, AetherMetrics.WindowPadding, AetherMetrics.DialogButtonWidth, AetherMetrics.ButtonMinWidth })
        {
            Assert.Equal(0f, value % 4f);
        }

        Assert.True(AetherMetrics.RadiusSm < AetherMetrics.RadiusMd);
        Assert.True(AetherMetrics.RadiusMd < AetherMetrics.RadiusLg);
    }
}
