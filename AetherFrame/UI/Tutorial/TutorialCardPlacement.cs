using System;
using System.Numerics;

namespace AetherFrame.UI.Tutorial;

/// <summary>Which side of the spotlight the explanation card landed on.</summary>
internal enum TutorialCardSide
{
    /// <summary>No spotlight: the card sits in the middle of the viewport.</summary>
    Center,
    Below,
    Above,
    Right,
    Left,
}

/// <summary>
/// Where the explanation card goes: beside the spotlight, never over it when that is at all
/// possible, and always inside the viewport. Tries below, above, right, then left of the hole,
/// aligned to its edge; the first side with room wins. When no side has room (a huge hole, a
/// tiny screen) the card takes the side with the most room and is clamped into the viewport,
/// overlapping the hole as little as that allows. Pure geometry; tested.
/// </summary>
internal static class TutorialCardPlacement
{
    /// <param name="hole">The spotlight hole, or an empty rectangle for a step with no target.</param>
    /// <param name="cardSize">The card's size in screen pixels.</param>
    /// <param name="viewport">The area the card may use (the main viewport's work area).</param>
    /// <param name="gap">The gap between the hole and the card.</param>
    /// <param name="inset">How far the card stays inside the viewport's edges.</param>
    internal static (Vector2 Position, TutorialCardSide Side) Place(ScreenRect hole, Vector2 cardSize, ScreenRect viewport, float gap, float inset)
    {
        gap = Sane(gap);
        inset = Sane(inset);
        cardSize = new Vector2(Math.Max(1f, Sane(cardSize.X)), Math.Max(1f, Sane(cardSize.Y)));

        var usable = viewport.Expand(-inset);
        if (usable.IsEmpty)
        {
            usable = viewport;
        }

        if (hole.IsEmpty || !hole.IsFinite)
        {
            return (Centered(usable, cardSize), TutorialCardSide.Center);
        }

        var candidates = new (TutorialCardSide Side, Vector2 Position)[]
        {
            (TutorialCardSide.Below, new Vector2(hole.Min.X, hole.Max.Y + gap)),
            (TutorialCardSide.Above, new Vector2(hole.Min.X, hole.Min.Y - gap - cardSize.Y)),
            (TutorialCardSide.Right, new Vector2(hole.Max.X + gap, hole.Min.Y)),
            (TutorialCardSide.Left, new Vector2(hole.Min.X - gap - cardSize.X, hole.Min.Y)),
        };

        // First choice: a side with room for the whole card, slid along the hole's edge to stay
        // inside the viewport, and still clear of the hole after sliding.
        foreach (var (side, position) in candidates)
        {
            var card = ScreenRect.FromSize(position, cardSize).ClampInside(usable);
            if (FitsInside(card, usable) && !card.Overlaps(hole))
            {
                return (card.Min, side);
            }
        }

        // No side has room: the side with the most room, clamped, overlapping the hole as little
        // as the viewport allows.
        var best = TutorialCardSide.Below;
        var bestRoom = float.NegativeInfinity;
        Vector2 bestPosition = default;
        foreach (var (side, position) in candidates)
        {
            var room = side switch
            {
                TutorialCardSide.Below => usable.Max.Y - hole.Max.Y,
                TutorialCardSide.Above => hole.Min.Y - usable.Min.Y,
                TutorialCardSide.Right => usable.Max.X - hole.Max.X,
                _ => hole.Min.X - usable.Min.X,
            };

            if (room > bestRoom)
            {
                bestRoom = room;
                best = side;
                bestPosition = ScreenRect.FromSize(position, cardSize).ClampInside(usable).Min;
            }
        }

        return (bestPosition, best);
    }

    private static Vector2 Centered(ScreenRect area, Vector2 cardSize) =>
        ScreenRect.FromSize(area.Center - (cardSize / 2f), cardSize).ClampInside(area).Min;

    private static bool FitsInside(ScreenRect card, ScreenRect area) =>
        card.Min.X >= area.Min.X - 0.01f && card.Min.Y >= area.Min.Y - 0.01f && card.Max.X <= area.Max.X + 0.01f && card.Max.Y <= area.Max.Y + 0.01f;

    private static float Sane(float value) => float.IsFinite(value) ? Math.Max(0f, value) : 0f;
}
