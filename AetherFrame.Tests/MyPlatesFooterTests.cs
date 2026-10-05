using AetherFrame.Services.Diagnostics;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The My Plates footer's version item (the owner's request of October 4, 2026): the loaded
/// version at the bottom right with its build revision in a tooltip, beside the right-click hint
/// while both fit and under it otherwise, at any window width or UI scale.
/// </summary>
public class MyPlatesFooterTests
{
    [Fact]
    public void Version_ShowsTheLoadedVersion_WithItsRevisionInTheTooltip()
    {
        var build = AetherFrameBuildInfo.Parse("0.1.9+4b4507e1d2c3b4a5f6e7d8c9b0a1f2e3d4c5b6a7");
        Assert.Equal("v0.1.9", MyPlatesFooterText.Version(build));
        Assert.Equal("Loaded plugin: AetherFrame 0.1.9\nBuild 4b4507e", MyPlatesFooterText.VersionTooltip(build));

        var noRevision = AetherFrameBuildInfo.Parse("0.2.0-rc.1");
        Assert.Equal(new MyPlatesFooterItem("v0.2.0-rc.1", "Loaded plugin: AetherFrame 0.2.0-rc.1\nThis build has no build revision recorded."), MyPlatesFooterText.VersionItem(noRevision));
    }

    [Fact]
    public void Version_IsTheRunningBuilds()
    {
        Assert.Equal($"v{AetherFrameBuildInfo.Current.Version}", MyPlatesFooterText.Version(AetherFrameBuildInfo.Current));
        Assert.Contains(AetherFrameBuildInfo.Current.DisplayName, MyPlatesFooterText.VersionTooltip(AetherFrameBuildInfo.Current));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(3f)]
    public void Layout_KeepsTheItemsBesideTheHint_WhileBothFit_ThenPutsThemUnderIt(float scale)
    {
        // Widths as measured at 100%, scaled the way text and spacing scale.
        var hint = 190f * scale;
        var items = 42f * scale;
        var gap = 16f * scale;

        var wide = MyPlatesFooterLayout.For(800f, hint, items, gap);
        Assert.Equal(new MyPlatesFooterLayout(false, 800f - items), wide);
        Assert.Equal(1, wide.Rows);

        // Exactly enough room still fits side by side; a pixel less stacks them.
        var exact = hint + gap + items;
        Assert.False(MyPlatesFooterLayout.For(exact, hint, items, gap).ItemsOnOwnRow);
        var narrow = MyPlatesFooterLayout.For(exact - 1f, hint, items, gap);
        Assert.True(narrow.ItemsOnOwnRow);
        Assert.Equal(2, narrow.Rows);
        Assert.Equal(exact - 1f - items, narrow.ItemsX);

        // Side by side, the items never start before the hint ends.
        foreach (var width in new[] { 300f, 450f, 560f, 700f, 1200f })
        {
            var layout = MyPlatesFooterLayout.For(width, hint, items, gap);
            if (!layout.ItemsOnOwnRow)
            {
                Assert.True(layout.ItemsX >= hint + gap);
            }

            Assert.True(layout.ItemsX + items <= width || layout.ItemsX == 0f);
        }
    }

    [Fact]
    public void Layout_ItemsWiderThanTheFooter_StartAtItsLeftEdge()
    {
        var layout = MyPlatesFooterLayout.For(100f, 60f, 140f, 8f);
        Assert.Equal(new MyPlatesFooterLayout(true, 0f), layout);
    }

    [Fact]
    public void ItemsWidth_AddsASeparatorBetweenEachTwoItems()
    {
        Assert.Equal(0f, MyPlatesFooterLayout.ItemsWidth([], 10f));
        Assert.Equal(40f, MyPlatesFooterLayout.ItemsWidth([40f], 10f));
        Assert.Equal(40f + 10f + 70f, MyPlatesFooterLayout.ItemsWidth([40f, 70f], 10f));
    }
}
