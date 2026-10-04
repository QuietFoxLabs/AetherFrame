using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Tutorial;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The canvas panel: viewport (fit, wheel zoom around the cursor, middle-drag pan), the shared
/// profile rendering, editor-only chrome on top of it (canvas border, hover/selection outlines,
/// resize handles, snap guides), and mouse input (select, Ctrl+click to add, drag, resize, context
/// menu). Elements resize from their own corners as always; a Component, a selection of several things
/// and a linked group move and resize through one box (<see cref="CanvasGesture"/>).
/// </summary>
internal sealed partial class ProfileEditorWindow
{
    // Screen-space sizes below are in unscaled pixels, at Dalamud's global UI scale when used, so
    // handles stay grabbable and snapping feels the same on a high-DPI screen.
    private static float HandleScreenSize => EditorWidgets.Scaled(8f);

    // Snapping pulls within this many SCREEN pixels, at any zoom — close enough to feel helpful,
    // small enough not to fight deliberate placement.
    private static float SnapThresholdScreenPixels => EditorWidgets.Scaled(6f);

    private const float WheelZoomStep = 1.15f;

    // How much of the canvas must stay inside the panel when panning.
    private static float PanKeepVisiblePixels => EditorWidgets.Scaled(48f);

    private static readonly Vector4 SelectionColor = new(1f, 0.85f, 0.2f, 1f);
    private static readonly Vector4 HoverColor = new(0.45f, 0.72f, 1f, 0.75f);
    private static readonly Vector4 LinkedGroupColor = new(0.35f, 0.9f, 0.75f, 1f);
    private static readonly Vector4 SnapGuideColor = new(1f, 0.28f, 0.62f, 0.95f);
    private static readonly Vector4 CanvasBorderColor = new(0.4f, 0.4f, 0.4f, 1f);

    // Fit-to-window viewport state — all runtime only, never persisted with the profile.
    // lastCanvasPanelSize starts at a sentinel that can never match a real panel size, so Auto
    // Fit's size-change check always fires once on the first Draw after (re)opening.
    private Vector2 lastCanvasPanelSize = new(-1f, -1f);
    private bool isPanning;

    // Which of the selected Component's placements its handles are on (a Corner Ornament has one per
    // corner): the one last clicked. Runtime only.
    private int selectedPlacementIndex;

    /// <summary>Fit: re-enable Auto Fit and fit the canvas to the panel right away (button, F key).</summary>
    private void FitCanvas()
    {
        editorSession.AutoFit = true;
        if (lastCanvasPanelSize.X > 0f)
        {
            editorSession.ApplyFitZoom(lastCanvasPanelSize);
        }
    }

    private void DrawCanvasPanel(ProfileDocument profile, Vector2 size)
    {
        using var child = AetherChild.Begin("##AetherFrameCanvasPanel", size, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!child.Success)
        {
            return;
        }

        TutorialAnchorMarks.MarkWindow(TutorialTarget.AdvancedCanvas);

        // The interior content region (post-border/padding), not the outer `size` passed in —
        // this is what the canvas actually has to fit inside.
        var panelSize = ImGui.GetContentRegionAvail();
        var panelMin = ImGui.GetCursorScreenPos();
        var panelCenter = panelMin + (panelSize / 2f);

        if (editorSession.AutoFit && Vector2.DistanceSquared(panelSize, lastCanvasPanelSize) > 0.25f)
        {
            // Covers both the initial Fit-to-Window on open (lastCanvasPanelSize starts at an
            // impossible sentinel) and continuous re-fitting while the panel is being resized.
            editorSession.ApplyFitZoom(panelSize);
        }

        lastCanvasPanelSize = panelSize;
        editorSession.ClampPan(panelSize, PanKeepVisiblePixels);

        var zoom = editorSession.Zoom;
        var canvasScreenSize = new Vector2(profile.CanvasWidth, profile.CanvasHeight) * zoom;
        var canvasOrigin = panelCenter - (canvasScreenSize / 2f) + editorSession.PanOffset;

        // One interaction surface over the whole panel: clicks beside the canvas still deselect,
        // and wheel/pan work anywhere in the panel.
        ImGui.InvisibleButton("##AetherFrameCanvasSurface", Vector2.Max(panelSize, Vector2.One), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var panelHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(panelMin, panelMin + panelSize, true);

        var showGuides = editorSession.ShowGuides;
        var renderOptions = showGuides ? EditorPlaceholders.CanvasOptions : EditorPlaceholders.CanvasOptionsWithoutGuides;
        canvasArt.Begin(renderResources);
        ProfileRenderer.Draw(drawList, profile, canvasOrigin, zoom, renderResources, renderOptions);
        canvasArt.End(renderResources);

        if (showGuides)
        {
            // Editor-only chrome: outlines the logical canvas bounds.
            drawList.AddRect(canvasOrigin, canvasOrigin + canvasScreenSize, ImGui.GetColorU32(CanvasBorderColor));
        }

        // Hit testing walks the paint sequence the renderer just drew, in reverse, so whatever is
        // visually topmost wins: an element, or a Component drawn over it (issue #115).
        ProfileRenderer.BuildPaintPlan(profile, renderResources, renderOptions, canvasPlanBuffer);

        var selectedElement = GetSelectedElement(profile);
        var selectedComponent = editorSession.SelectedComponentId is { } selectedComponentId ? Domain.Components.PlateComponentEditor.Find(profile, selectedComponentId) : null;
        Vector2[]? selectedScreenCorners = null;

        // A Component, or several things: one gesture box with uniform-resize handles (see CanvasGesture).
        if (selectedComponent is null)
        {
            selectedPlacementIndex = 0;
        }

        var selectionGesture = editorSession.PreviewSelectionGesture(canvasPlanBuffer, selectedPlacementIndex);
        var selectionMovable = selectionGesture is not null && editorSession.SelectionTransformBlockedReason is null;
        Vector2[]? gestureScreenCorners = null;

        var logicalMouse = (ImGui.GetMousePos() - canvasOrigin) / zoom;
        var hover = panelHovered && editorSession.ActiveInteraction == ElementInteractionKind.None && !isPanning
            ? CanvasHitTest.Find(canvasPlanBuffer, logicalMouse, Domain.Components.ComponentPaintPlan.Unit(profile))
            : default;
        var hoverTarget = hover.Element;

        if (showGuides && hoverTarget is not null && hoverTarget.Id != selectedElement?.Id)
        {
            var hoverCorners = GetScreenCorners(hoverTarget, canvasOrigin, zoom);
            drawList.AddQuad(hoverCorners[0], hoverCorners[1], hoverCorners[2], hoverCorners[3], ImGui.GetColorU32(HoverColor), 1.5f);
        }

        // Components are outlined by each placement they draw (every corner of a Corner Ornament); a
        // selected one gets handles on the placement last clicked (see selectionGesture below).
        if (showGuides && hover.Component is { } hoverComponent && !ReferenceEquals(hoverComponent, selectedComponent))
        {
            DrawComponentOutlines(drawList, hoverComponent, canvasOrigin, zoom, HoverColor);
        }

        if (selectedComponent is not null)
        {
            DrawComponentOutlines(drawList, selectedComponent, canvasOrigin, zoom, SelectionColor);
        }

        if (editorSession.SelectedItems.Count > 1)
        {
            // Every member's own outline, dimmer, under the box they move and resize by.
            foreach (var item in editorSession.SelectedItems)
            {
                if (item.IsComponent)
                {
                    if (Domain.Components.PlateComponentEditor.Find(profile, item.Id) is { } member)
                    {
                        DrawComponentOutlines(drawList, member, canvasOrigin, zoom, SelectionColor with { W = 0.55f });
                    }
                }
                else if (profile.Elements.Find(e => e.Id == item.Id) is { } member)
                {
                    var memberCorners = GetScreenCorners(member, canvasOrigin, zoom);
                    drawList.AddQuad(memberCorners[0], memberCorners[1], memberCorners[2], memberCorners[3], ImGui.GetColorU32(SelectionColor with { W = 0.55f }), 1f);
                }
            }
        }

        if (selectionGesture is not null)
        {
            gestureScreenCorners = new Vector2[4];
            for (var i = 0; i < 4; i++)
            {
                gestureScreenCorners[i] = canvasOrigin + (selectionGesture.Corners[i] * zoom);
            }

            if (editorSession.SelectedItems.Count > 1)
            {
                var boxColor = editorSession.SelectedGroupId is not null ? LinkedGroupColor : SelectionColor;
                drawList.AddQuad(gestureScreenCorners[0], gestureScreenCorners[1], gestureScreenCorners[2], gestureScreenCorners[3], ImGui.GetColorU32(selectionMovable ? boxColor : boxColor with { W = 0.45f }), 1.5f);
            }

            if (showGuides && selectionMovable)
            {
                DrawResizeHandles(drawList, gestureScreenCorners);
            }
        }

        if (selectedElement is not null)
        {
            // Rotated corners (identity for rotation 0), not an axis-aligned rect, so the
            // outline and handles always match what's actually drawn/hit-tested.
            selectedScreenCorners = GetScreenCorners(selectedElement, canvasOrigin, zoom);

            if (showGuides)
            {
                var outlineColor = ImGui.GetColorU32(selectedElement.Locked ? SelectionColor with { W = 0.45f } : SelectionColor);
                drawList.AddQuad(selectedScreenCorners[0], selectedScreenCorners[1], selectedScreenCorners[2], selectedScreenCorners[3], outlineColor, 1.5f);

                if (!selectedElement.Locked && selectedElement.Visible)
                {
                    DrawResizeHandles(drawList, selectedScreenCorners);
                }
            }
        }

        DrawSnapGuides(drawList, canvasOrigin, zoom);
        drawList.PopClipRect();

        HandleNavigation(panelHovered, canvasOrigin, panelCenter);

        // Guides off also disables resize-handle interaction (nothing is drawn to grab) by
        // simply not handing HandleCanvasInput any corners to hit-test against; plain click-to-
        // select and drag-to-move on the canvas stay fully functional either way.
        HandleCanvasInput(
            selectedElement, showGuides && selectedElement is { Visible: true } ? selectedScreenCorners : null,
            showGuides && selectionMovable ? gestureScreenCorners : null,
            hoverTarget, hover.Component, logicalMouse, panelHovered, zoom, Domain.Components.ComponentPaintPlan.Unit(profile));

        canvasPlanBuffer.Clear();
    }

    private void DrawComponentOutlines(ImDrawListPtr drawList, Domain.Components.PlateComponent component, Vector2 canvasOrigin, float zoom, Vector4 color)
    {
        CanvasHitTest.Outlines(canvasPlanBuffer, component, componentOutlineBuffer);
        var packed = ImGui.GetColorU32(color);
        foreach (var corners in componentOutlineBuffer)
        {
            drawList.AddQuad(
                canvasOrigin + (corners[0] * zoom), canvasOrigin + (corners[1] * zoom), canvasOrigin + (corners[2] * zoom), canvasOrigin + (corners[3] * zoom),
                packed, 1.5f);
        }

        componentOutlineBuffer.Clear();
    }

    private static Vector2[] GetScreenCorners(ProfileElement element, Vector2 canvasOrigin, float zoom)
    {
        var corners = RotationGeometry.GetRotatedCorners(element.Position, element.Size, RotationGeometry.GetRotationDegrees(element));
        for (var i = 0; i < corners.Length; i++)
        {
            corners[i] = canvasOrigin + (corners[i] * zoom);
        }

        return corners;
    }

    /// <summary>Editor-only alignment guides for the current snapped drag/resize.</summary>
    private void DrawSnapGuides(ImDrawListPtr drawList, Vector2 canvasOrigin, float zoom)
    {
        var guides = editorSession.SnapGuides;
        if (guides.Count == 0)
        {
            return;
        }

        var color = ImGui.GetColorU32(SnapGuideColor);
        foreach (var guide in guides)
        {
            Vector2 from, to;
            if (guide.Vertical)
            {
                from = canvasOrigin + (new Vector2(guide.Position, guide.SpanStart) * zoom);
                to = canvasOrigin + (new Vector2(guide.Position, guide.SpanEnd) * zoom);
            }
            else
            {
                from = canvasOrigin + (new Vector2(guide.SpanStart, guide.Position) * zoom);
                to = canvasOrigin + (new Vector2(guide.SpanEnd, guide.Position) * zoom);
            }

            drawList.AddLine(from, to, color, 1f);
        }
    }

    /// <summary>Mouse-wheel zoom around the cursor and middle-mouse-drag panning.</summary>
    private void HandleNavigation(bool panelHovered, Vector2 canvasOrigin, Vector2 panelCenter)
    {
        var io = ImGui.GetIO();

        if (panelHovered && io.MouseWheel != 0f && editorSession.ActiveInteraction == ElementInteractionKind.None)
        {
            editorSession.ZoomAround(MathF.Pow(WheelZoomStep, io.MouseWheel), ImGui.GetMousePos(), canvasOrigin, panelCenter);
        }

        if (panelHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
        {
            isPanning = true;
        }

        if (isPanning)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Middle))
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
                if (io.MouseDelta != Vector2.Zero)
                {
                    editorSession.PanBy(io.MouseDelta);
                }
            }
            else
            {
                isPanning = false;
            }
        }
    }

    private void HandleCanvasInput(
        ProfileElement? selectedElement,
        Vector2[]? selectedScreenCorners,
        Vector2[]? gestureScreenCorners,
        ProfileElement? hoverTarget,
        Domain.Components.PlateComponent? hoverComponent,
        Vector2 logicalMouse,
        bool panelHovered,
        float zoom,
        float unit)
    {
        var io = ImGui.GetIO();
        var mouseScreen = ImGui.GetMousePos();
        var leftClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var leftReleased = ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        var rightClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Right);

        if (editorSession.ActiveInteraction != ElementInteractionKind.None)
        {
            if (leftReleased || !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                editorSession.EndInteraction();
            }
            else
            {
                // Every frame (not just on mouse movement), so pressing/releasing Alt mid-drag
                // takes effect immediately.
                var snap = editorSession.SnapEnabled && !io.KeyAlt;
                editorSession.UpdateInteraction(logicalMouse, snap, SnapThresholdScreenPixels / zoom);

                if (editorSession.ActiveInteraction == ElementInteractionKind.Dragging)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
                }
            }

            return;
        }

        if (!panelHovered || isPanning)
        {
            return;
        }

        var additive = io.KeyCtrl;
        var onHandle = false;
        if (!additive && gestureScreenCorners is not null && TryGetHoveredHandle(mouseScreen, gestureScreenCorners, out var gestureHandle))
        {
            // A Component's or a selection's handles: a uniform resize around the opposite corner.
            onHandle = true;
            ImGui.SetMouseCursor(GetResizeCursor(gestureScreenCorners, gestureHandle));

            if (leftClicked)
            {
                editorSession.BeginSelectionResize(canvasPlanBuffer, CornerIndex(gestureHandle), logicalMouse, selectedPlacementIndex);
                return;
            }
        }
        else if (!additive && selectedElement is not null && !selectedElement.Locked && selectedScreenCorners is not null
            && TryGetHoveredHandle(mouseScreen, selectedScreenCorners, out var hoveredHandle))
        {
            onHandle = true;
            ImGui.SetMouseCursor(GetResizeCursor(selectedScreenCorners, hoveredHandle));

            if (leftClicked)
            {
                editorSession.BeginResize(selectedElement, hoveredHandle, logicalMouse);
                return;
            }
        }
        else if (hoverTarget is { Locked: false } || hoverComponent is { Locked: false })
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }
        else if (hoverComponent is not null)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        Domain.Components.CanvasItemRef? hoverItem = hoverTarget is not null
            ? Domain.Components.CanvasItemRef.Element(hoverTarget.Id)
            : hoverComponent is not null ? Domain.Components.CanvasItemRef.Component(hoverComponent.Id) : null;

        // A Component is selected by a right-click too, which opens its controls in the Canvas tab.
        // The selected element's resize handles stay its own, even over a Component.
        if (rightClicked)
        {
            if (hoverTarget is not null)
            {
                RequestElementContextMenu(hoverTarget.Id);
            }
            else if (!onHandle && hoverItem is { } rightItem)
            {
                editorSession.SelectOnCanvas(rightItem, additive: false);
            }

            return;
        }

        if (!leftClicked || onHandle)
        {
            return;
        }

        if (hoverItem is not { } item)
        {
            if (!additive)
            {
                editorSession.Select(null);
            }

            return;
        }

        // Ctrl+click adds to the selection or takes out of it (a whole linked group at once); it never drags.
        if (additive)
        {
            editorSession.SelectOnCanvas(item, additive: true);
            return;
        }

        // A double-click on a member of a linked group edits that member on its own; on text, its content too.
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            if (editorSession.GroupOf(item) is not null)
            {
                editorSession.SelectFromList(item, additive: false);
                selectElementTabPending = !item.IsComponent;
            }

            if (hoverTarget is TextProfileElement && editorSession.SelectedElementId == hoverTarget.Id)
            {
                // Double-click text: jump straight to editing its content.
                selectElementTabPending = true;
                focusTextContentPending = true;
            }

            return;
        }

        editorSession.SelectOnCanvas(item, additive: false);
        if (hoverComponent is not null && editorSession.SelectedComponentId == hoverComponent.Id)
        {
            selectedPlacementIndex = CanvasHitTest.PlacementIndexAt(canvasPlanBuffer, hoverComponent, logicalMouse, unit);
        }

        if (editorSession.SelectionUsesGestures)
        {
            editorSession.BeginSelectionDrag(canvasPlanBuffer, logicalMouse, selectedPlacementIndex);
            return;
        }

        if (hoverTarget is not null && editorSession.SelectedElementId == hoverTarget.Id)
        {
            selectElementTabPending = true;
            editorSession.BeginDrag(hoverTarget, logicalMouse);
        }
    }

    /// <summary>A handle's index in <see cref="CanvasGesture.Corners"/> order (top-left, top-right,
    /// bottom-right, bottom-left).</summary>
    private static int CornerIndex(ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft => 0,
        ResizeHandle.TopRight => 1,
        ResizeHandle.BottomRight => 2,
        _ => 3,
    };

    private static void DrawResizeHandles(ImDrawListPtr drawList, Vector2[] screenCorners)
    {
        var fill = ImGui.GetColorU32(SelectionColor);
        var border = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f));
        var half = HandleScreenSize / 2f;

        foreach (var corner in screenCorners)
        {
            drawList.AddRectFilled(corner - new Vector2(half, half), corner + new Vector2(half, half), fill);
            drawList.AddRect(corner - new Vector2(half, half), corner + new Vector2(half, half), border);
        }
    }

    /// <summary>
    /// <paramref name="screenCorners"/> is in the same perimeter order as
    /// <see cref="RotationGeometry.GetRotatedCorners"/> (TopLeft, TopRight, BottomRight,
    /// BottomLeft) — <paramref name="handle"/> identifies which LOCAL corner of the element was
    /// hit, not which corner it currently appears at on screen (rotation can move that visually).
    /// </summary>
    private static bool TryGetHoveredHandle(Vector2 mouseScreen, Vector2[] screenCorners, out ResizeHandle handle)
    {
        var half = HandleScreenSize;

        ReadOnlySpan<ResizeHandle> handleForCornerIndex = [ResizeHandle.TopLeft, ResizeHandle.TopRight, ResizeHandle.BottomRight, ResizeHandle.BottomLeft];

        for (var i = 0; i < screenCorners.Length; i++)
        {
            var center = screenCorners[i];
            var min = center - new Vector2(half, half);
            var max = center + new Vector2(half, half);

            if (mouseScreen.X >= min.X && mouseScreen.X <= max.X && mouseScreen.Y >= min.Y && mouseScreen.Y <= max.Y)
            {
                handle = handleForCornerIndex[i];
                return true;
            }
        }

        handle = ResizeHandle.None;
        return false;
    }

    /// <summary>
    /// Picks the diagonal resize cursor (NW-SE vs NE-SW) that matches the CURRENT visual angle
    /// between the hovered corner and its opposite — not <paramref name="handle"/>'s local
    /// identity, which points at a different visual diagonal once the element is rotated. At 90
    /// degrees, for example, the local TopLeft/BottomRight pair (the NW-SE diagonal at rotation
    /// 0) visually sits on the NE-SW diagonal instead.
    /// </summary>
    private static ImGuiMouseCursor GetResizeCursor(Vector2[] screenCorners, ResizeHandle handle)
    {
        var index = handle switch
        {
            ResizeHandle.TopLeft => 0,
            ResizeHandle.TopRight => 1,
            ResizeHandle.BottomRight => 2,
            _ => 3, // BottomLeft
        };
        var oppositeIndex = (index + 2) % 4;

        // Same-signed X/Y offset to the opposite corner means it's visually down-right (or
        // up-left) of the hovered one, i.e. the NW-SE diagonal; opposite signs mean NE-SW.
        var diff = screenCorners[oppositeIndex] - screenCorners[index];
        return diff.X * diff.Y >= 0f ? ImGuiMouseCursor.ResizeNwse : ImGuiMouseCursor.ResizeNesw;
    }
}
