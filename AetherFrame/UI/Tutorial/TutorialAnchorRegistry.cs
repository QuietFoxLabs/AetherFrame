using System.Collections.Generic;
using System.Numerics;

namespace AetherFrame.UI.Tutorial;

/// <summary>
/// Where one target was drawn this frame: the widget's rectangle and the clip rectangle it was
/// drawn under (a scrolled child, a collapsed section), so the spotlight knows how much of it is
/// really on screen.
/// </summary>
/// <param name="Target">The control.</param>
/// <param name="Bounds">The widget's rectangle.</param>
/// <param name="Clip">The clip rectangle in effect when it was drawn.</param>
/// <param name="Frame">The frame it was drawn on.</param>
internal readonly record struct TutorialAnchor(TutorialTarget Target, ScreenRect Bounds, ScreenRect Clip, int Frame)
{
    /// <summary>The part of the widget that is actually visible (its bounds within its clip).</summary>
    internal ScreenRect VisibleBounds => Bounds.Intersect(Clip);

    /// <summary>How much of the widget shows, 0 (clipped away) to 1 (all of it).</summary>
    internal float VisibleFraction => Bounds.Area > 0f ? VisibleBounds.Area / Bounds.Area : 0f;

    /// <summary>Enough of the widget shows to point at it (at least a quarter, and a sliver of size).</summary>
    internal bool IsUsable => VisibleFraction >= MinVisibleFraction && VisibleBounds.Width >= MinVisibleEdge && VisibleBounds.Height >= MinVisibleEdge;

    internal const float MinVisibleFraction = 0.25f;
    internal const float MinVisibleEdge = 4f;
}

/// <summary>
/// The anchors every AetherFrame window marks while it draws, read back by the tutorial overlay
/// in the same frame. Anchors expire on their own: one that wasn't marked for a couple of frames
/// (its window closed, its section collapsed, its panel not drawn) is gone, so the spotlight never
/// lingers on a control that isn't there. The overlay may also ask a window to <em>reveal</em> a
/// target (scroll to it, open its section) through <see cref="RequestReveal"/>; the marking window
/// consumes the request while drawing.
///
/// <para>Render thread only, like everything ImGui. Allocation free per frame once every target
/// has been seen once.</para>
/// </summary>
internal sealed class TutorialAnchorRegistry
{
    /// <summary>An anchor marked this many frames ago or more is stale.</summary>
    internal const int MaxAgeFrames = 2;

    private readonly Dictionary<TutorialTarget, TutorialAnchor> anchors = new();
    private readonly Dictionary<TutorialTarget, int> revealRequests = new();

    /// <summary>Records where <paramref name="target"/> was drawn on <paramref name="frame"/>.</summary>
    /// <remarks>
    /// A target drawn twice in one frame (a category in the wide rail and again in the narrow
    /// strip, say) keeps the larger visible one, so the spotlight lands on the control that shows.
    /// A rectangle with a non-finite coordinate is ignored.
    /// </remarks>
    internal void Record(TutorialTarget target, ScreenRect bounds, ScreenRect clip, int frame)
    {
        if (target == TutorialTarget.None || !bounds.IsFinite || !clip.IsFinite)
        {
            return;
        }

        var anchor = new TutorialAnchor(target, bounds, clip, frame);
        if (anchors.TryGetValue(target, out var existing) && existing.Frame == frame && existing.VisibleBounds.Area > anchor.VisibleBounds.Area)
        {
            return;
        }

        anchors[target] = anchor;
    }

    /// <summary>Records a target drawn with nothing clipping it.</summary>
    internal void Record(TutorialTarget target, Vector2 min, Vector2 max, int frame) =>
        Record(target, new ScreenRect(min, max), new ScreenRect(min, max), frame);

    /// <summary>
    /// The anchor for <paramref name="target"/> if it was marked within the last
    /// <see cref="MaxAgeFrames"/> frames; a stale one doesn't count.
    /// </summary>
    internal bool TryGet(TutorialTarget target, int currentFrame, out TutorialAnchor anchor)
    {
        if (target != TutorialTarget.None && anchors.TryGetValue(target, out anchor) && currentFrame - anchor.Frame < MaxAgeFrames && currentFrame >= anchor.Frame)
        {
            return true;
        }

        anchor = default;
        return false;
    }

    /// <summary>Whether <paramref name="target"/> is marked, fresh and visible enough to point at.</summary>
    internal bool IsAvailable(TutorialTarget target, int currentFrame) => TryGet(target, currentFrame, out var anchor) && anchor.IsUsable;

    /// <summary>Asks the window that draws <paramref name="target"/> to bring it into view on its next draw.</summary>
    internal void RequestReveal(TutorialTarget target, int frame)
    {
        if (target != TutorialTarget.None)
        {
            revealRequests[target] = frame;
        }
    }

    /// <summary>
    /// For the window drawing <paramref name="target"/>: whether a reveal was requested recently
    /// (within <see cref="MaxAgeFrames"/> frames). Consuming it clears the request.
    /// </summary>
    internal bool ConsumeReveal(TutorialTarget target, int currentFrame)
    {
        if (!revealRequests.TryGetValue(target, out var frame))
        {
            return false;
        }

        revealRequests.Remove(target);
        return currentFrame - frame <= MaxAgeFrames && currentFrame >= frame;
    }

    /// <summary>Forgets everything (the tutorial ended, or a window system reset).</summary>
    internal void Clear()
    {
        anchors.Clear();
        revealRequests.Clear();
    }

    /// <summary>How many targets have ever been marked (for diagnostics and tests).</summary>
    internal int Count => anchors.Count;
}
