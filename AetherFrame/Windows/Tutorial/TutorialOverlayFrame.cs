using System;
using System.Numerics;
using AetherFrame.UI.Tutorial;

namespace AetherFrame.Windows.Tutorial;

/// <summary>What the plugin gives the tutorial: a read-only view of the interface, and the few safe actions a card may offer.</summary>
internal interface ITutorialHost
{
    /// <summary>The interface's state this frame. Only read; never a live object.</summary>
    TutorialContextSnapshot Snapshot();

    /// <summary>Performs a card's action (opening or bringing forward a window). Never changes a Plate.</summary>
    void Perform(TutorialAction action);
}

/// <summary>
/// The overlay's state for one frame, computed once (by <see cref="TutorialOverlayWindow"/>,
/// after every AetherFrame window has drawn and marked its anchors) and read by the shade and
/// card windows drawn after it in the same frame. One instance, reused; nothing allocates per frame.
/// </summary>
internal sealed class TutorialOverlayFrame
{
    private readonly ScreenRect[] strips = new ScreenRect[SpotlightGeometry.MaxStrips];

    /// <summary>The frame this state was computed for.</summary>
    internal int Frame { get; private set; } = -1;

    /// <summary>The step being shown, or null when the tutorial isn't running.</summary>
    internal TutorialStepView? View { get; private set; }

    /// <summary>The main viewport's work area.</summary>
    internal ScreenRect Viewport { get; private set; }

    /// <summary>The spotlight hole; empty for a step with no control on screen.</summary>
    internal ScreenRect Hole { get; private set; }

    /// <summary>Whether the control inside the hole may be used (Interact steps).</summary>
    internal bool AllowInteraction { get; private set; }

    /// <summary>How many of <see cref="Strips"/> are in use.</summary>
    internal int StripCount { get; private set; }

    internal ReadOnlySpan<ScreenRect> Strips => strips.AsSpan(0, StripCount);

    /// <summary>The card's top-left, and the side of the hole it sits on.</summary>
    internal Vector2 CardPosition { get; private set; }

    internal TutorialCardSide CardSide { get; private set; }

    /// <summary>The card's size as last measured (its height depends on the step's text).</summary>
    internal Vector2 CardSize { get; set; }

    internal bool IsActive => View is not null;

    /// <summary>Nothing to show this frame.</summary>
    internal void Clear(int frame)
    {
        Frame = frame;
        View = null;
        Hole = ScreenRect.Empty;
        StripCount = 0;
        AllowInteraction = false;
    }

    /// <summary>Computes the hole, the strips and the card's place for <paramref name="view"/>.</summary>
    internal void Set(int frame, TutorialStepView view, ScreenRect viewport, ScreenRect visibleTarget, float margin, float gap, float inset)
    {
        Frame = frame;
        View = view;
        Viewport = viewport;
        Hole = SpotlightGeometry.Hole(visibleTarget, margin, viewport);
        AllowInteraction = view.AllowInteraction && !Hole.IsEmpty;
        StripCount = SpotlightGeometry.Strips(viewport, Hole, strips);
        var (position, side) = TutorialCardPlacement.Place(Hole, CardSize, viewport, gap, inset);
        CardPosition = position;
        CardSide = side;
    }

    /// <summary>The strip at <paramref name="index"/>, or an empty rectangle when there is none this frame.</summary>
    internal ScreenRect Strip(int index) => index < StripCount ? strips[index] : ScreenRect.Empty;
}
