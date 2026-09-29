using System;
using System.Numerics;

namespace AetherFrame.UI.Tutorial;

/// <summary>An axis-aligned rectangle in screen pixels, as ImGui reports item and window bounds.</summary>
/// <param name="Min">Top-left.</param>
/// <param name="Max">Bottom-right (exclusive, as ImGui's).</param>
internal readonly record struct ScreenRect(Vector2 Min, Vector2 Max)
{
    internal static readonly ScreenRect Empty = new(Vector2.Zero, Vector2.Zero);

    internal static ScreenRect FromSize(Vector2 min, Vector2 size) => new(min, min + size);

    internal float Width => Max.X - Min.X;

    internal float Height => Max.Y - Min.Y;

    internal Vector2 Size => Max - Min;

    internal Vector2 Center => (Min + Max) / 2f;

    internal float Area => IsEmpty ? 0f : Width * Height;

    /// <summary>True when the rectangle covers no pixels (or is inside out).</summary>
    internal bool IsEmpty => !(Width > 0f) || !(Height > 0f);

    internal bool Contains(Vector2 point) => point.X >= Min.X && point.X < Max.X && point.Y >= Min.Y && point.Y < Max.Y;

    /// <summary>Grown by <paramref name="margin"/> on every side (shrunk when negative).</summary>
    internal ScreenRect Expand(float margin) => new(Min - new Vector2(margin), Max + new Vector2(margin));

    /// <summary>The overlap with <paramref name="other"/>; empty when they don't overlap.</summary>
    internal ScreenRect Intersect(ScreenRect other)
    {
        var min = Vector2.Max(Min, other.Min);
        var max = Vector2.Min(Max, other.Max);
        return max.X > min.X && max.Y > min.Y ? new ScreenRect(min, max) : new ScreenRect(min, min);
    }

    /// <summary>Whether the two rectangles share any pixel.</summary>
    internal bool Overlaps(ScreenRect other) => !Intersect(other).IsEmpty;

    /// <summary>The smallest rectangle holding both.</summary>
    internal ScreenRect Union(ScreenRect other) =>
        IsEmpty ? other : other.IsEmpty ? this : new ScreenRect(Vector2.Min(Min, other.Min), Vector2.Max(Max, other.Max));

    /// <summary>Whether every coordinate is a finite number.</summary>
    internal bool IsFinite => float.IsFinite(Min.X) && float.IsFinite(Min.Y) && float.IsFinite(Max.X) && float.IsFinite(Max.Y);

    /// <summary>
    /// Moved (never resized) so it lies inside <paramref name="bounds"/> when it fits; a rectangle
    /// larger than the bounds is pinned to their top-left.
    /// </summary>
    internal ScreenRect ClampInside(ScreenRect bounds)
    {
        var size = Size;
        var x = Math.Clamp(Min.X, bounds.Min.X, Math.Max(bounds.Min.X, bounds.Max.X - size.X));
        var y = Math.Clamp(Min.Y, bounds.Min.Y, Math.Max(bounds.Min.Y, bounds.Max.Y - size.Y));
        return FromSize(new Vector2(x, y), size);
    }
}
