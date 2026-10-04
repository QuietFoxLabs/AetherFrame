using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Editor;

/// <summary>
/// One move or resize of several things at once, or of one Component, on the Advanced canvas: what each
/// of them was when the gesture began, and how a translation or a uniform scale around a fixed point maps
/// onto their own values. Elements move by their Position and grow by their Size (and a text's font
/// size); a Component moves by its Offset and grows by its Scale, around its drawn placement. A Component
/// that follows an element that moves in the same gesture (a frame on its picture, a backing on its name)
/// is already carried by that element: it keeps its Offset on a move, and only its Offset grows with a
/// scale, so nothing moves twice. Every value is derived from the starting state, never accumulated, so a
/// gesture can be updated every frame and always lands exactly where the mouse says.
/// </summary>
internal sealed class CanvasGesture
{
    private readonly List<ElementStart> elements = new();
    private readonly List<ComponentStart> components = new();

    private CanvasGesture()
    {
    }

    /// <summary>The handle corners, top-left, top-right, bottom-right, bottom-left: one Component's drawn
    /// (and turned) placement, or the box around everything that moves.</summary>
    internal Vector2[] Corners { get; private set; } = new Vector2[4];

    /// <summary>The axis-aligned box around everything that moves, as it was when the gesture began.</summary>
    internal (Vector2 Min, Vector2 Max) Bounds { get; private set; }

    /// <summary>Element ids that move (for snapping, which must not snap to them).</summary>
    internal IEnumerable<Guid> ElementIds
    {
        get
        {
            foreach (var element in elements)
            {
                yield return element.Id;
            }
        }
    }

    /// <summary>
    /// The gesture for <paramref name="items"/>, from <paramref name="plan"/> (the Plate's paint plan as
    /// drawn now). <paramref name="placementIndex"/> picks which of a single Component's placements the
    /// handles are on (a Corner Ornament has one per corner). Null when nothing among them is on the Plate.
    /// </summary>
    internal static CanvasGesture? Create(ProfileDocument profile, IReadOnlyList<CanvasItemRef> items, IReadOnlyList<PaintStep> plan, int placementIndex = 0)
    {
        var gesture = new CanvasGesture();
        var moving = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (!item.IsComponent && profile.Elements.Find(e => e.Id == item.Id) is { } element && moving.Add(element.Id))
            {
                gesture.elements.Add(new ElementStart(
                    element.Id, element.Position, element.Size, RotationGeometry.GetRotationDegrees(element), (element as TextProfileElement)?.FontSize));
            }
        }

        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var start in gesture.elements)
        {
            var (low, high) = RotationGeometry.GetVisualBounds(start.Position, start.Size, start.Rotation);
            min = Vector2.Min(min, low);
            max = Vector2.Max(max, high);
        }

        var followed = new List<Guid>();
        var singleComponent = gesture.elements.Count == 0 && items.Count == 1;
        foreach (var item in items)
        {
            if (!item.IsComponent || PlateComponentEditor.Find(profile, item.Id) is not { } component || gesture.components.Exists(c => c.Id == component.Id))
            {
                continue;
            }

            // Its placements as drawn: the box everything is measured from, and which way its Offset turns.
            ComponentPlacement? primary = null;
            var seen = 0;
            foreach (var step in plan)
            {
                if (!ReferenceEquals(step.Component, component))
                {
                    continue;
                }

                if (primary is null || seen == placementIndex)
                {
                    primary = step.Placement;
                }

                seen++;
                foreach (var corner in RotationGeometry.GetRotatedCorners(step.Placement.Rect.Position, step.Placement.Rect.Size, step.Placement.RotationDegrees))
                {
                    min = Vector2.Min(min, corner);
                    max = Vector2.Max(max, corner);
                }
            }

            LinkedGroups.FollowedElements(profile, component, followed);
            var dependent = followed.Exists(moving.Contains);
            var (flipX, flipY) = primary is { } placement ? (placement.OffsetFlipX, placement.OffsetFlipY) : LinkedGroups.OffsetFlips(profile, component);
            var center = primary is { } drawn ? drawn.Rect.Position + (drawn.Rect.Size / 2f) : Vector2.Zero;
            gesture.components.Add(new ComponentStart(component.Id, component.Offset, PlateComponentLimits.ClampScale(component.Scale), center, flipX, flipY, dependent, primary is not null));

            if (singleComponent && primary is { } handles)
            {
                gesture.Corners = RotationGeometry.GetRotatedCorners(handles.Rect.Position, handles.Rect.Size, handles.RotationDegrees);
            }
        }

        if (gesture.elements.Count == 0 && gesture.components.Count == 0)
        {
            return null;
        }

        if (!(min.X <= max.X) || !(min.Y <= max.Y))
        {
            min = max = Vector2.Zero;
        }

        gesture.Bounds = (min, max);
        if (!singleComponent)
        {
            gesture.Corners = [min, new Vector2(max.X, min.Y), max, new Vector2(min.X, max.Y)];
        }

        return gesture;
    }

    /// <summary>
    /// The largest part of <paramref name="delta"/> that keeps every moving element's visual bounds inside
    /// a <paramref name="canvas"/>-sized Plate, as dragging one element does. An element already past an
    /// edge is never pushed back, so a gesture never jumps when it begins. Components aren't held to the
    /// Plate (they may reach past it on purpose).
    /// </summary>
    internal Vector2 ClampTranslation(Vector2 delta, Vector2 canvas)
    {
        var low = new Vector2(float.MinValue);
        var high = new Vector2(float.MaxValue);
        foreach (var start in elements)
        {
            var (min, max) = RotationGeometry.GetVisualBounds(start.Position, start.Size, start.Rotation);
            low = Vector2.Max(low, Vector2.Min(-min, Vector2.Zero));
            high = Vector2.Min(high, Vector2.Max(canvas - max, Vector2.Zero));
        }

        return Vector2.Clamp(delta, Vector2.Min(low, high), high);
    }

    /// <summary>The scale factors every member can take and stay within its own limits (an element's
    /// minimum size, a text's font sizes, a Component's sizes), so the group keeps its proportions exactly
    /// instead of one member stopping early. Always includes 1.</summary>
    internal (float Min, float Max) ScaleLimits
    {
        get
        {
            var low = 0f;
            var high = float.MaxValue;
            foreach (var start in elements)
            {
                low = Math.Max(low, Math.Min(1f, Math.Max(EditorSession.MinElementWidth / start.Size.X, EditorSession.MinElementHeight / start.Size.Y)));
                if (start.FontSize is { } font && font > 0f)
                {
                    low = Math.Max(low, Math.Min(1f, TextProfileElement.MinFontSize / font));
                    high = Math.Min(high, Math.Max(1f, TextProfileElement.MaxFontSize / font));
                }
            }

            foreach (var start in components)
            {
                if (!start.Dependent && start.Scale > 0f)
                {
                    low = Math.Max(low, Math.Min(1f, PlateComponentLimits.MinScale / start.Scale));
                    high = Math.Min(high, Math.Max(1f, PlateComponentLimits.MaxScale / start.Scale));
                }
            }

            // Never down to nothing.
            return (Math.Max(low, 0.01f), Math.Max(high, 1f));
        }
    }

    /// <summary>Moves everything by <paramref name="delta"/> (logical canvas units) from where it began.</summary>
    internal void Translate(Vector2 delta, Action<Guid, Action<ProfileElement>> updateElement, Action<Guid, Action<PlateComponent>> updateComponent)
    {
        foreach (var start in elements)
        {
            var position = start.Position + delta;
            updateElement(start.Id, element => element.Position = position);
        }

        foreach (var start in components)
        {
            var offset = start.Dependent ? start.Offset : start.Offset + start.Flip(delta);
            updateComponent(start.Id, component => component.Offset = offset);
        }
    }

    /// <summary>
    /// Scales everything by <paramref name="factor"/> (bounded by <see cref="ScaleLimits"/>) around the
    /// fixed point <paramref name="anchor"/>, from where it began: each element's center moves away from
    /// the anchor by the factor and its size (and a text's font size) grows by it, and each Component's
    /// drawn placement does the same through its Offset and Scale. Rotations are kept.
    /// </summary>
    internal void Scale(Vector2 anchor, float factor, Action<Guid, Action<ProfileElement>> updateElement, Action<Guid, Action<PlateComponent>> updateComponent)
    {
        var (low, high) = ScaleLimits;
        var s = float.IsFinite(factor) ? Math.Clamp(factor, low, high) : 1f;

        foreach (var start in elements)
        {
            var center = start.Position + (start.Size / 2f);
            var size = start.Size * s;
            var position = anchor + ((center - anchor) * s) - (size / 2f);
            float? fontSize = start.FontSize is { } font ? Math.Clamp(font * s, TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize) : null;
            updateElement(start.Id, element =>
            {
                element.Position = position;
                element.Size = size;
                if (fontSize is { } value && element is TextProfileElement text)
                {
                    text.FontSize = value;
                }
            });
        }

        foreach (var start in components)
        {
            if (start.Dependent)
            {
                // Its anchor grows around the same point: only the distance from it grows.
                var offset = start.Offset * s;
                updateComponent(start.Id, component => component.Offset = offset);
                continue;
            }

            var movedCenter = start.IsDrawn ? anchor + ((start.Center - anchor) * s) : start.Center;
            var newOffset = start.Offset + start.Flip(movedCenter - start.Center);
            var newScale = start.Scale * s;
            updateComponent(start.Id, component =>
            {
                component.Offset = newOffset;
                component.Scale = newScale;
            });
        }
    }

    /// <summary>
    /// The uniform factor a handle drag asks for: how far the mouse is along the line from the fixed
    /// corner <paramref name="anchor"/> through the dragged corner <paramref name="handleCorner"/>, relative
    /// to that corner's own distance. 1 when the two corners coincide.
    /// </summary>
    internal static float FactorFor(Vector2 anchor, Vector2 handleCorner, Vector2 mouse)
    {
        var diagonal = handleCorner - anchor;
        var lengthSquared = diagonal.LengthSquared();
        return lengthSquared > 1e-6f ? Vector2.Dot(mouse - anchor, diagonal) / lengthSquared : 1f;
    }

    private sealed record ElementStart(Guid Id, Vector2 Position, Vector2 Size, float Rotation, float? FontSize);

    private sealed record ComponentStart(Guid Id, Vector2 Offset, float Scale, Vector2 Center, bool FlipX, bool FlipY, bool Dependent, bool IsDrawn)
    {
        internal Vector2 Flip(Vector2 delta) => new(FlipX ? -delta.X : delta.X, FlipY ? -delta.Y : delta.Y);
    }
}
