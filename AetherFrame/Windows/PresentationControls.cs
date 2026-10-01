using System;
using System.Numerics;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.Windows;

/// <summary>
/// AetherFrame's Close control look, drawn over a Plate: the Plate Viewer's (and its message
/// window's). A compact translucent dark disc with a faint outer shadow, a light ring and a bold
/// light X — readable on bright and dark backgrounds alike — that turns red on hover (closing is
/// destructive) and deeper red while pressed. Colors: <see cref="PlateViewerPresentation"/>.
/// </summary>
internal static class PresentationControls
{
    /// <summary>A Close control drawn as an ImGui item at <paramref name="min"/>; true when clicked.</summary>
    internal static bool Close(string id, Vector2 min, float size, string tooltip)
    {
        if (!(size > 0f))
        {
            return false;
        }

        ImGui.SetCursorScreenPos(min);
        var clicked = ImGui.InvisibleButton(id, new Vector2(size));
        var hovered = ImGui.IsItemHovered();
        var pressed = ImGui.IsItemActive();
        if (hovered || pressed)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (hovered && !pressed && !string.IsNullOrEmpty(tooltip))
        {
            ImGui.SetTooltip(tooltip);
        }

        DrawClose(ImGui.GetWindowDrawList(), min + new Vector2(size / 2f), size / 2f, hovered, pressed);
        return clicked;
    }

    /// <summary>Paints the Close look (disc, shadow, ring, X) centered at <paramref name="center"/>; no input.</summary>
    internal static void DrawClose(ImDrawListPtr drawList, Vector2 center, float radius, bool hovered, bool pressed)
    {
        if (!(radius > 0f))
        {
            return;
        }

        var line = Math.Max(1f, radius / 8f);
        var backing = pressed ? PlateViewerPresentation.CloseBackingPressed
            : hovered ? PlateViewerPresentation.CloseBackingHovered
            : PlateViewerPresentation.CloseBacking;
        drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(backing));
        drawList.AddCircle(center, radius - (line / 2f), ImGui.GetColorU32(PlateViewerPresentation.CloseShadow), 0, line * 2f);
        drawList.AddCircle(center, radius - line, ImGui.GetColorU32(PlateViewerPresentation.CloseRing), 0, line);

        var arm = radius * 0.38f;
        var glyph = ImGui.GetColorU32(PlateViewerPresentation.CloseGlyph);
        drawList.AddLine(center - new Vector2(arm), center + new Vector2(arm), glyph, line * 1.6f);
        drawList.AddLine(center + new Vector2(arm, -arm), center + new Vector2(-arm, arm), glyph, line * 1.6f);
    }
}
