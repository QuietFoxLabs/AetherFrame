using System.Numerics;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Window layout at Dalamud's global UI scale: Dalamud scales window sizes and ImGui's style, so
/// the Advanced Editor's panels must scale with them, and a first-open size must fit the screen.
/// </summary>
public class WindowScaleTests
{
    // ImGui's default style, which Dalamud scales by the global scale.
    private static readonly Vector2 ItemSpacing = new(8f, 4f);
    private static readonly Vector2 WindowPadding = new(8f, 8f);

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void AdvancedEditor_PanelsScaleAndFitTheMinimumWindow(float scale)
    {
        var spacing = ItemSpacing * scale;
        var content = (AdvancedEditorLayout.MinimumWindowSize * scale) - (WindowPadding * scale * 2f);

        var layout = AdvancedEditorLayout.Compute(content, statusBarHeight: 30f * scale, spacing, scale);

        Assert.Equal(AdvancedEditorLayout.LayersPanelWidth * scale, layout.LayersWidth, 3);
        Assert.Equal(AdvancedEditorLayout.InspectorPanelWidth * scale, layout.InspectorWidth, 3);
        Assert.True(layout.CanvasWidth >= AdvancedEditorLayout.MinCanvasWidth * scale);
        Assert.True(layout.LayersWidth + layout.CanvasWidth + layout.InspectorWidth + (spacing.X * 2f) <= content.X + 0.01f);
    }

    [Fact]
    public void AdvancedEditor_AtScaleOne_KeepsItsExistingLayout()
    {
        var layout = AdvancedEditorLayout.Compute(new Vector2(1264f, 700f), statusBarHeight: 30f, ItemSpacing, scale: 1f);

        Assert.Equal(new AdvancedEditorLayout(236f, 1264f - 236f - 344f - 16f, 344f, 700f - 30f - 4f), layout);
    }

    [Theory]
    [InlineData(1f, 1920f, 1080f, 1280f, 760f)] // fits: the preferred size as is
    [InlineData(1.5f, 1920f, 1080f, 1728f, 972f)] // too large: held to 90% of the screen
    [InlineData(2f, 1280f, 720f, 1960f, 1120f)] // even the minimum doesn't fit: never below it
    [InlineData(1.5f, 0f, 0f, 1920f, 1140f)] // screen size unknown: the preferred size
    public void FirstUseSize_FitsTheScreen_ButNeverBelowTheMinimum(float scale, float screenWidth, float screenHeight, float expectedWidth, float expectedHeight)
    {
        var size = FirstUseWindowSize.Compute(AdvancedEditorLayout.FirstUseSize, AdvancedEditorLayout.MinimumWindowSize, new Vector2(screenWidth, screenHeight), scale);

        Assert.Equal(expectedWidth, size.X, 3);
        Assert.Equal(expectedHeight, size.Y, 3);
    }

    // Interface task 7: the Basic editor opened at Window.Size, which Dalamud scales and doesn't keep
    // on the screen, so its first window was taller than a 1080p screen at 150%.
    [Theory]
    [InlineData(1f, 1920f, 1080f, 1180f, 760f)] // fits: the preferred size as is
    [InlineData(1.5f, 1920f, 1080f, 1728f, 972f)] // 1770 x 1140 wouldn't fit: held to 90% of the screen
    [InlineData(2f, 1920f, 1080f, 1728f, 972f)] // 2360 x 1520: held to 90% of the screen, which the minimum (1040 x 960) fits
    [InlineData(2f, 1280f, 720f, 1152f, 960f)] // even the minimum doesn't fit: never below it
    [InlineData(1.5f, 0f, 0f, 1770f, 1140f)] // screen size unknown: the preferred size
    public void BasicEditor_FirstUseSize_FitsTheScreen_ButNeverBelowTheMinimum(float scale, float screenWidth, float screenHeight, float expectedWidth, float expectedHeight)
    {
        var size = FirstUseWindowSize.Compute(BasicEditorView.FirstUseSize, BasicEditorView.MinimumWindowSize, new Vector2(screenWidth, screenHeight), scale);

        Assert.Equal(expectedWidth, size.X, 3);
        Assert.Equal(expectedHeight, size.Y, 3);
    }

    [Fact]
    public void BasicEditor_AtOneHundredFiftyPercent_OpensWithinA1080pScreen()
    {
        var screen = new Vector2(1920f, 1080f);

        // Window.Size, as Dalamud applied it: too tall for the screen.
        Assert.True(BasicEditorView.FirstUseSize.Y * 1.5f > screen.Y);

        var size = FirstUseWindowSize.Compute(BasicEditorView.FirstUseSize, BasicEditorView.MinimumWindowSize, screen, 1.5f);
        Assert.True(size.X <= screen.X && size.Y <= screen.Y);
        Assert.True(size.X >= BasicEditorView.MinimumWindowSize.X * 1.5f && size.Y >= BasicEditorView.MinimumWindowSize.Y * 1.5f);
    }

    // The guided steps draw Back and Continue at the window's bottom edge, so at 200% the whole
    // window has to fit a 1080p screen, below the first-use position ImGui gives it (60, 60).
    [Fact]
    public void BasicEditor_AtTwoHundredPercent_KeepsTheGuidedFooterOnA1080pScreen()
    {
        var screen = new Vector2(1920f, 1080f);

        Assert.True(BasicEditorView.MinimumWindowSize.Y * 2f <= screen.Y * FirstUseWindowSize.MaxWorkAreaFraction);

        var size = FirstUseWindowSize.Compute(BasicEditorView.FirstUseSize, BasicEditorView.MinimumWindowSize, screen, 2f);
        Assert.True(60f + size.Y <= screen.Y);
    }
}
