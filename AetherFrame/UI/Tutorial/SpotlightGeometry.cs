using System;
using System.Numerics;

namespace AetherFrame.UI.Tutorial;

/// <summary>
/// The spotlight's shapes, as plain geometry: the hole cut around a target, and the up to four
/// dimming strips that tile the rest of the viewport. The strips are what the overlay draws as
/// input-blocking windows (an ImGui window can't have a hole, so the dim is made of rectangles
/// that leave one), so their union must cover exactly the viewport minus the hole, with no gaps
/// and no overlap of the hole — the tests hold that.
/// </summary>
internal static class SpotlightGeometry
{
    /// <summary>The most strips a hole ever needs.</summary>
    internal const int MaxStrips = 4;

    /// <summary>
    /// The spotlight hole for a target whose visible rectangle is <paramref name="visibleTarget"/>:
    /// the rectangle grown by <paramref name="margin"/>, kept inside the viewport. Empty when the
    /// target is empty or lies entirely outside the viewport.
    /// </summary>
    internal static ScreenRect Hole(ScreenRect visibleTarget, float margin, ScreenRect viewport)
    {
        if (visibleTarget.IsEmpty || !visibleTarget.IsFinite || viewport.IsEmpty)
        {
            return ScreenRect.Empty;
        }

        margin = float.IsFinite(margin) ? Math.Max(0f, margin) : 0f;
        return visibleTarget.Expand(margin).Intersect(viewport);
    }

    /// <summary>
    /// The dimming strips for <paramref name="hole"/> inside <paramref name="viewport"/>, written
    /// to <paramref name="strips"/> (at least <see cref="MaxStrips"/> long); returns how many were
    /// written. An empty hole yields one strip: the whole viewport. Strips never overlap each other
    /// or the hole, and together they cover everything else.
    /// </summary>
    internal static int Strips(ScreenRect viewport, ScreenRect hole, Span<ScreenRect> strips)
    {
        if (strips.Length < MaxStrips)
        {
            throw new ArgumentException($"At least {MaxStrips} slots are needed.", nameof(strips));
        }

        if (viewport.IsEmpty)
        {
            return 0;
        }

        var clipped = hole.Intersect(viewport);
        if (clipped.IsEmpty)
        {
            strips[0] = viewport;
            return 1;
        }

        var count = 0;

        // Top and bottom span the full viewport width; left and right fill the hole's own rows.
        count = Add(strips, count, new ScreenRect(viewport.Min, new Vector2(viewport.Max.X, clipped.Min.Y)));
        count = Add(strips, count, new ScreenRect(new Vector2(viewport.Min.X, clipped.Max.Y), viewport.Max));
        count = Add(strips, count, new ScreenRect(new Vector2(viewport.Min.X, clipped.Min.Y), new Vector2(clipped.Min.X, clipped.Max.Y)));
        count = Add(strips, count, new ScreenRect(new Vector2(clipped.Max.X, clipped.Min.Y), new Vector2(viewport.Max.X, clipped.Max.Y)));
        return count;
    }

    private static int Add(Span<ScreenRect> strips, int count, ScreenRect strip)
    {
        if (!strip.IsEmpty)
        {
            strips[count++] = strip;
        }

        return count;
    }

    /// <summary>Whether <paramref name="point"/> lies in any of the first <paramref name="count"/> strips.</summary>
    internal static bool IsDimmed(ReadOnlySpan<ScreenRect> strips, int count, Vector2 point)
    {
        for (var i = 0; i < count; i++)
        {
            if (strips[i].Contains(point))
            {
                return true;
            }
        }

        return false;
    }
}
