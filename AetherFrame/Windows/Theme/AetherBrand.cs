using System;
using System.Numerics;
using AetherFrame.UI.Theme;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.Windows.Theme;

/// <summary>
/// The AetherFrame mark: four frame corners around a four-point aether spark, drawn in code
/// (no asset, nothing borrowed). It is the frame a Plate sits in, with the aether at its heart.
/// Used small beside window titles, larger on the welcome and completion cards, and as the
/// spotlight's corner motif; always the same proportions, so it is recognized at any size.
/// </summary>
internal static class AetherBrand
{
    /// <summary>
    /// Draws the mark centered at <paramref name="center"/> inside a square of <paramref name="size"/>.
    /// The corners take <paramref name="frame"/>, the spark <paramref name="spark"/>.
    /// </summary>
    internal static void DrawMark(ImDrawListPtr drawList, Vector2 center, float size, Vector4 frame, Vector4 spark)
    {
        if (!(size > 0f))
        {
            return;
        }

        var half = size / 2f;
        var arm = size * 0.30f;
        var thickness = Math.Max(1.25f, size * 0.085f);
        var frameColor = ImGui.GetColorU32(frame);
        var inset = thickness / 2f;

        // Four corners of the frame, each an L: the horizontal arm then the vertical arm.
        DrawCorner(drawList, new Vector2(center.X - half + inset, center.Y - half + inset), new Vector2(1f, 1f), arm, thickness, frameColor);
        DrawCorner(drawList, new Vector2(center.X + half - inset, center.Y - half + inset), new Vector2(-1f, 1f), arm, thickness, frameColor);
        DrawCorner(drawList, new Vector2(center.X - half + inset, center.Y + half - inset), new Vector2(1f, -1f), arm, thickness, frameColor);
        DrawCorner(drawList, new Vector2(center.X + half - inset, center.Y + half - inset), new Vector2(-1f, -1f), arm, thickness, frameColor);

        DrawSpark(drawList, center, size * 0.26f, spark);
    }

    /// <summary>The four-point spark alone (the tutorial's progress dots, a "new" marker).</summary>
    internal static void DrawSpark(ImDrawListPtr drawList, Vector2 center, float radius, Vector4 color)
    {
        if (!(radius > 0f))
        {
            return;
        }

        // Two thin diamonds crossing: each convex, so each fills cleanly; drawn opaque so the
        // overlap doesn't double up.
        var waist = radius * 0.30f;
        var col = ImGui.GetColorU32(color with { W = 1f });
        var alpha = Math.Clamp(color.W, 0f, 1f);
        if (alpha < 1f)
        {
            col = ImGui.GetColorU32(color);
        }

        drawList.AddQuadFilled(
            center + new Vector2(0f, -radius), center + new Vector2(waist, 0f), center + new Vector2(0f, radius), center + new Vector2(-waist, 0f), col);
        drawList.AddQuadFilled(
            center + new Vector2(-radius, 0f), center + new Vector2(0f, -waist), center + new Vector2(radius, 0f), center + new Vector2(0f, waist), col);
    }

    /// <summary>
    /// The four frame corners alone around a rectangle, for the spotlight and for framing a
    /// selected card: <paramref name="arm"/> long, <paramref name="thickness"/> thick, drawn just
    /// outside <paramref name="min"/>..<paramref name="max"/> by <paramref name="gap"/>.
    /// </summary>
    internal static void DrawCorners(ImDrawListPtr drawList, Vector2 min, Vector2 max, float gap, float arm, float thickness, Vector4 color)
    {
        if (!(arm > 0f) || !(thickness > 0f))
        {
            return;
        }

        var col = ImGui.GetColorU32(color);
        var inset = thickness / 2f;
        var outerMin = min - new Vector2(gap) + new Vector2(inset);
        var outerMax = max + new Vector2(gap) - new Vector2(inset);
        arm = Math.Min(arm, Math.Min(outerMax.X - outerMin.X, outerMax.Y - outerMin.Y) / 2f);
        if (!(arm > 0f))
        {
            return;
        }

        DrawCorner(drawList, outerMin, new Vector2(1f, 1f), arm, thickness, col);
        DrawCorner(drawList, new Vector2(outerMax.X, outerMin.Y), new Vector2(-1f, 1f), arm, thickness, col);
        DrawCorner(drawList, new Vector2(outerMin.X, outerMax.Y), new Vector2(1f, -1f), arm, thickness, col);
        DrawCorner(drawList, outerMax, new Vector2(-1f, -1f), arm, thickness, col);
    }

    private static void DrawCorner(ImDrawListPtr drawList, Vector2 corner, Vector2 direction, float arm, float thickness, uint color)
    {
        drawList.AddLine(corner, corner + new Vector2(direction.X * arm, 0f), color, thickness);
        drawList.AddLine(corner, corner + new Vector2(0f, direction.Y * arm), color, thickness);
    }

    /// <summary>
    /// The brand row at the top of a window: the mark, the window's own title in the display face,
    /// and an optional subtitle in the small label face.
    /// </summary>
    internal static void Header(string title, string? subtitle = null)
    {
        var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var markSize = AetherMetrics.BrandMarkSize * scale;
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();

        float titleHeight;
        using (AetherFonts.Display())
        {
            titleHeight = ImGui.GetTextLineHeight();
        }

        var rowHeight = Math.Max(titleHeight, markSize);
        DrawMark(drawList, origin + new Vector2(markSize / 2f, rowHeight / 2f), markSize, AetherPalette.Aether, AetherPalette.Glow);

        var textX = origin.X + markSize + (AetherMetrics.SpaceSm * scale);
        using (AetherFonts.Display())
        {
            drawList.AddText(new Vector2(textX, origin.Y + ((rowHeight - ImGui.GetTextLineHeight()) / 2f)), ImGui.GetColorU32(AetherPalette.TextPrimary), title);
            textX += ImGui.CalcTextSize(title).X + (AetherMetrics.SpaceSm * scale);
        }

        if (!string.IsNullOrEmpty(subtitle))
        {
            using (AetherFonts.Label())
            {
                drawList.AddText(new Vector2(textX, origin.Y + ((rowHeight - ImGui.GetTextLineHeight()) / 2f) + (1f * scale)), ImGui.GetColorU32(AetherPalette.TextMuted), subtitle);
            }
        }

        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, rowHeight));
    }
}
