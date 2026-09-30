using System;
using System.Numerics;
using AetherFrame.UI.Tutorial;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The spotlight's geometry: anchors record where a control really was, the hole hugs the visible
/// part of it, the dimming strips tile everything else without gaps or overlaps, and the card
/// lands beside the hole, inside the viewport, and off the hole whenever there is room.
/// </summary>
public class TutorialGeometryTests
{
    private static readonly ScreenRect Viewport = new(Vector2.Zero, new Vector2(1920f, 1080f));

    // ---------------------------------------------------------------- anchors

    [Fact]
    public void Registry_ReturnsAFreshAnchor_AndForgetsAStaleOne()
    {
        var registry = new TutorialAnchorRegistry();
        registry.Record(TutorialTarget.LibraryCreatePlate, new Vector2(10f, 10f), new Vector2(110f, 40f), frame: 100);

        Assert.True(registry.TryGet(TutorialTarget.LibraryCreatePlate, 100, out var anchor));
        Assert.Equal(new ScreenRect(new Vector2(10f, 10f), new Vector2(110f, 40f)), anchor.Bounds);
        Assert.True(registry.TryGet(TutorialTarget.LibraryCreatePlate, 101, out _));
        Assert.False(registry.TryGet(TutorialTarget.LibraryCreatePlate, 100 + TutorialAnchorRegistry.MaxAgeFrames, out _));
        Assert.False(registry.TryGet(TutorialTarget.LibraryImport, 100, out _));
        Assert.False(registry.TryGet(TutorialTarget.None, 100, out _));
    }

    [Fact]
    public void Registry_RemembersTheWindowAnAnchorWasDrawnIn()
    {
        var registry = new TutorialAnchorRegistry();
        var rect = new ScreenRect(new Vector2(10f, 10f), new Vector2(110f, 40f));
        registry.Record(TutorialTarget.LibraryTemplateChooser, rect, rect, frame: 7, ownerWindowId: 0xC0FFEEu);
        registry.Record(TutorialTarget.LibraryCreatePlate, new Vector2(10f, 10f), new Vector2(110f, 40f), frame: 7);

        Assert.True(registry.TryGet(TutorialTarget.LibraryTemplateChooser, 7, out var chooser));
        Assert.Equal(0xC0FFEEu, chooser.OwnerWindowId);
        Assert.True(registry.TryGet(TutorialTarget.LibraryCreatePlate, 7, out var create));
        Assert.Equal(0u, create.OwnerWindowId);
    }

    [Fact]
    public void Registry_IgnoresAnAnchorFromTheFuture_AndNonFiniteRectangles()
    {
        var registry = new TutorialAnchorRegistry();
        registry.Record(TutorialTarget.LibraryImport, new Vector2(0f, 0f), new Vector2(10f, 10f), frame: 50);
        registry.Record(TutorialTarget.LibrarySearch, new Vector2(float.NaN, 0f), new Vector2(10f, 10f), frame: 50);

        Assert.False(registry.TryGet(TutorialTarget.LibraryImport, 49, out _));
        Assert.False(registry.TryGet(TutorialTarget.LibrarySearch, 50, out _));
    }

    [Fact]
    public void Registry_KeepsTheMoreVisibleOfTwoMarksInOneFrame_ButAlwaysTheNewerFrame()
    {
        var registry = new TutorialAnchorRegistry();
        var clip = new ScreenRect(Vector2.Zero, new Vector2(1000f, 1000f));
        registry.Record(TutorialTarget.BasicNavigatorStyle, new ScreenRect(new Vector2(0f, 0f), new Vector2(100f, 20f)), clip, frame: 7);
        registry.Record(TutorialTarget.BasicNavigatorStyle, new ScreenRect(new Vector2(0f, 990f), new Vector2(100f, 1010f)), clip, frame: 7);
        Assert.True(registry.TryGet(TutorialTarget.BasicNavigatorStyle, 7, out var kept));
        Assert.Equal(0f, kept.Bounds.Min.Y);

        registry.Record(TutorialTarget.BasicNavigatorStyle, new ScreenRect(new Vector2(0f, 990f), new Vector2(100f, 1010f)), clip, frame: 8);
        Assert.True(registry.TryGet(TutorialTarget.BasicNavigatorStyle, 8, out var newer));
        Assert.Equal(990f, newer.Bounds.Min.Y);
    }

    [Theory]
    [InlineData(0f, 0f, 100f, 30f, true)] // fully visible
    [InlineData(0f, 80f, 100f, 110f, true)] // two thirds visible
    [InlineData(0f, 93f, 100f, 123f, false)] // less than a quarter visible
    [InlineData(0f, 95f, 100f, 125f, false)] // barely peeking
    [InlineData(0f, 200f, 100f, 230f, false)] // scrolled out entirely
    public void Anchor_IsUsableOnlyWhenEnoughOfItShows(float minX, float minY, float maxX, float maxY, bool usable)
    {
        var clip = new ScreenRect(Vector2.Zero, new Vector2(400f, 100f));
        var anchor = new TutorialAnchor(TutorialTarget.AdvancedTextSize, new ScreenRect(new Vector2(minX, minY), new Vector2(maxX, maxY)), clip, 1);

        Assert.Equal(usable, anchor.IsUsable);
        Assert.True(anchor.VisibleFraction is >= 0f and <= 1f);
    }

    [Fact]
    public void Registry_RevealRequests_AreConsumedOnce_AndExpire()
    {
        var registry = new TutorialAnchorRegistry();
        registry.RequestReveal(TutorialTarget.AdvancedTextColor, frame: 10);

        Assert.False(registry.ConsumeReveal(TutorialTarget.AdvancedTextSize, 10));
        Assert.True(registry.ConsumeReveal(TutorialTarget.AdvancedTextColor, 11));
        Assert.False(registry.ConsumeReveal(TutorialTarget.AdvancedTextColor, 11));

        registry.RequestReveal(TutorialTarget.AdvancedTextColor, frame: 10);
        Assert.False(registry.ConsumeReveal(TutorialTarget.AdvancedTextColor, 10 + TutorialAnchorRegistry.MaxAgeFrames + 1));
        registry.Clear();
        Assert.Equal(0, registry.Count);
    }

    // ---------------------------------------------------------------- hole and strips

    [Fact]
    public void Hole_GrowsByTheMargin_AndStaysInsideTheViewport()
    {
        var target = new ScreenRect(new Vector2(1900f, 10f), new Vector2(1930f, 40f));

        var hole = SpotlightGeometry.Hole(target, 6f, Viewport);

        Assert.Equal(new ScreenRect(new Vector2(1894f, 4f), new Vector2(1920f, 46f)), hole);
        Assert.True(SpotlightGeometry.Hole(ScreenRect.Empty, 6f, Viewport).IsEmpty);
        Assert.True(SpotlightGeometry.Hole(new ScreenRect(new Vector2(3000f, 0f), new Vector2(3100f, 10f)), 6f, Viewport).IsEmpty);
        Assert.Equal(target.Intersect(Viewport), SpotlightGeometry.Hole(target, float.NaN, Viewport));
    }

    [Theory]
    [InlineData(100f, 100f, 300f, 200f)] // inside
    [InlineData(0f, 0f, 300f, 200f)] // top-left corner: no top or left strip
    [InlineData(1700f, 900f, 1920f, 1080f)] // bottom-right corner
    [InlineData(0f, 500f, 1920f, 520f)] // a full-width band: only top and bottom
    [InlineData(-50f, -50f, 100f, 100f)] // partly outside: clipped
    public void Strips_TileTheViewportMinusTheHole_WithoutGapsOrOverlap(float minX, float minY, float maxX, float maxY)
    {
        var hole = new ScreenRect(new Vector2(minX, minY), new Vector2(maxX, maxY));
        Span<ScreenRect> strips = stackalloc ScreenRect[SpotlightGeometry.MaxStrips];

        var count = SpotlightGeometry.Strips(Viewport, hole, strips);

        Assert.InRange(count, 1, SpotlightGeometry.MaxStrips);
        var clippedHole = hole.Intersect(Viewport);
        var area = 0f;
        for (var i = 0; i < count; i++)
        {
            Assert.False(strips[i].IsEmpty);
            Assert.False(strips[i].Overlaps(clippedHole), $"strip {i} overlaps the hole");
            for (var j = i + 1; j < count; j++)
            {
                Assert.False(strips[i].Overlaps(strips[j]), $"strips {i} and {j} overlap");
            }

            area += strips[i].Area;
        }

        Assert.Equal(Viewport.Area - clippedHole.Area, area, 1);

        // Sample points: every point outside the hole is dimmed, every point inside is not.
        var random = new Random(12345);
        for (var n = 0; n < 2000; n++)
        {
            var point = new Vector2((float)(random.NextDouble() * Viewport.Width), (float)(random.NextDouble() * Viewport.Height));
            Assert.Equal(!clippedHole.Contains(point), SpotlightGeometry.IsDimmed(strips, count, point));
        }
    }

    [Fact]
    public void Strips_WithNoHole_DimTheWholeViewport()
    {
        Span<ScreenRect> strips = stackalloc ScreenRect[SpotlightGeometry.MaxStrips];

        Assert.Equal(1, SpotlightGeometry.Strips(Viewport, ScreenRect.Empty, strips));
        Assert.Equal(Viewport, strips[0]);
        Assert.Equal(0, SpotlightGeometry.Strips(ScreenRect.Empty, ScreenRect.Empty, strips));
        Assert.Throws<ArgumentException>(() => SpotlightGeometry.Strips(Viewport, ScreenRect.Empty, new ScreenRect[2]));
    }

    // ---------------------------------------------------------------- card placement

    private static readonly Vector2 Card = new(360f, 220f);

    [Fact]
    public void Card_GoesBelowTheHole_WhenThereIsRoom()
    {
        var hole = new ScreenRect(new Vector2(100f, 100f), new Vector2(300f, 140f));

        var (position, side) = TutorialCardPlacement.Place(hole, Card, Viewport, gap: 14f, inset: 12f);

        Assert.Equal(TutorialCardSide.Below, side);
        Assert.Equal(new Vector2(100f, 154f), position);
    }

    [Fact]
    public void Card_GoesAbove_WhenBelowHasNoRoom_AndSlidesToStayInside()
    {
        var hole = new ScreenRect(new Vector2(1800f, 950f), new Vector2(1900f, 1000f));

        var (position, side) = TutorialCardPlacement.Place(hole, Card, Viewport, gap: 14f, inset: 12f);

        Assert.Equal(TutorialCardSide.Above, side);
        var card = ScreenRect.FromSize(position, Card);
        Assert.False(card.Overlaps(hole));
        Assert.True(card.Max.X <= 1920f - 12f);
        Assert.Equal(950f - 14f - 220f, position.Y);
    }

    [Fact]
    public void Card_GoesBeside_WhenNeitherAboveNorBelowFits()
    {
        // A hole spanning almost the full height leaves room only to its right.
        var hole = new ScreenRect(new Vector2(100f, 20f), new Vector2(500f, 1060f));

        var (position, side) = TutorialCardPlacement.Place(hole, Card, Viewport, gap: 14f, inset: 12f);

        Assert.Equal(TutorialCardSide.Right, side);
        Assert.Equal(new Vector2(514f, 20f), position);
        Assert.False(ScreenRect.FromSize(position, Card).Overlaps(hole));
    }

    [Fact]
    public void Card_WithNoHole_IsCentered_AndClampedInsideTheViewport()
    {
        var (position, side) = TutorialCardPlacement.Place(ScreenRect.Empty, Card, Viewport, 14f, 12f);

        Assert.Equal(TutorialCardSide.Center, side);
        Assert.Equal(Viewport.Center, position + (Card / 2f));

        var (tiny, _) = TutorialCardPlacement.Place(ScreenRect.Empty, Card, new ScreenRect(Vector2.Zero, new Vector2(300f, 200f)), 14f, 12f);
        Assert.Equal(new Vector2(12f, 12f), tiny);
    }

    [Fact]
    public void Card_OnAHugeHole_TakesTheSideWithMostRoom_AndStaysInside()
    {
        var hole = new ScreenRect(new Vector2(50f, 50f), new Vector2(1870f, 1030f));

        var (position, _) = TutorialCardPlacement.Place(hole, Card, Viewport, 14f, 12f);

        var card = ScreenRect.FromSize(position, Card);
        Assert.True(card.Min.X >= 12f && card.Min.Y >= 12f && card.Max.X <= 1908f && card.Max.Y <= 1068f);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void Card_StaysInsideTheViewport_AtEveryScale_ForRandomHoles(float scale)
    {
        var random = new Random(777);
        var card = Card * scale;
        var viewport = new ScreenRect(new Vector2(0f, 30f), new Vector2(2560f, 1440f)); // a work area below a top bar
        for (var n = 0; n < 500; n++)
        {
            var min = new Vector2((float)(random.NextDouble() * 2500f), (float)(random.NextDouble() * 1400f));
            var size = new Vector2((float)(random.NextDouble() * 600f) + 10f, (float)(random.NextDouble() * 400f) + 10f);
            var hole = new ScreenRect(min, min + size).Intersect(viewport);

            var (position, _) = TutorialCardPlacement.Place(hole, card, viewport, 14f * scale, 12f * scale);

            var placed = ScreenRect.FromSize(position, card);
            Assert.True(placed.Min.X >= viewport.Min.X && placed.Min.Y >= viewport.Min.Y, $"{placed} left the viewport");
            Assert.True(placed.Max.X <= viewport.Max.X + 0.01f && placed.Max.Y <= viewport.Max.Y + 0.01f, $"{placed} left the viewport");
        }
    }

    [Fact]
    public void Card_ToleratesNonFiniteInputs()
    {
        var (position, side) = TutorialCardPlacement.Place(new ScreenRect(new Vector2(float.NaN, 0f), new Vector2(10f, 10f)), new Vector2(float.PositiveInfinity, 100f), Viewport, float.NaN, -5f);

        Assert.Equal(TutorialCardSide.Center, side);
        Assert.True(float.IsFinite(position.X) && float.IsFinite(position.Y));
    }

    // ---------------------------------------------------------------- rect

    [Fact]
    public void ScreenRect_Basics()
    {
        var rect = new ScreenRect(new Vector2(10f, 20f), new Vector2(110f, 70f));
        Assert.Equal(100f, rect.Width);
        Assert.Equal(50f, rect.Height);
        Assert.Equal(new Vector2(60f, 45f), rect.Center);
        Assert.True(rect.Contains(new Vector2(10f, 20f)));
        Assert.False(rect.Contains(new Vector2(110f, 70f)));
        Assert.True(new ScreenRect(new Vector2(5f, 5f), new Vector2(5f, 9f)).IsEmpty);
        Assert.True(new ScreenRect(new Vector2(9f, 9f), new Vector2(5f, 5f)).IsEmpty);
        Assert.Equal(rect, rect.Union(ScreenRect.Empty));
        Assert.Equal(new ScreenRect(new Vector2(0f, 0f), new Vector2(110f, 70f)), rect.Union(new ScreenRect(Vector2.Zero, new Vector2(1f, 1f))));
        Assert.Equal(new ScreenRect(new Vector2(0f, 0f), new Vector2(100f, 50f)), rect.ClampInside(new ScreenRect(Vector2.Zero, new Vector2(100f, 50f))));
    }
}
