using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Draws one placement of a <see cref="PlateComponent"/> from its <see cref="ComponentGeometry"/>
/// primitives. Only ever called by <see cref="ProfileRenderer"/>, in the order
/// <see cref="ComponentPaintPlan"/> decides — so every surface that renders a Plate draws
/// Components identically.
/// </summary>
internal static class ComponentRenderer
{
    // Reused every frame (render thread only).
    private static readonly List<ComponentPrimitive> PrimitiveBuffer = new(64);

    internal static void Draw(ImDrawListPtr drawList, ProfileDocument profile, in PaintStep step, Vector2 canvasOrigin, float scale, ProfileRenderResources resources)
    {
        if (step.Component is not { } component || step.Definition is not { } definition)
        {
            return;
        }

        PrimitiveBuffer.Clear();
        ComponentGeometry.Build(profile, component, definition, step.Placement, PrimitiveBuffer);

        foreach (var primitive in PrimitiveBuffer)
        {
            var a = canvasOrigin + (primitive.A * scale);
            var b = canvasOrigin + (primitive.B * scale);
            var c = canvasOrigin + (primitive.C * scale);
            var d = canvasOrigin + (primitive.D * scale);
            var color = ImGui.GetColorU32(primitive.Color);

            switch (primitive.Kind)
            {
                case ComponentPrimitiveKind.Quad:
                    drawList.AddQuadFilled(a, b, c, d, color);
                    break;

                case ComponentPrimitiveKind.Triangle:
                    drawList.AddTriangleFilled(a, b, c, color);
                    break;

                case ComponentPrimitiveKind.Image:
                    // Missing, still loading, or undecodable: nothing — a Component is decoration,
                    // and the Plate stays fully readable without it.
                    if (PaintVisibility.ComponentImage(component) is { } assetId && resources.Images.GetWrapOrNull(assetId) is { } wrap)
                    {
                        drawList.AddImageQuad(wrap.Handle, a, b, c, d, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), color);
                    }

                    break;

                case ComponentPrimitiveKind.Art:
                    // The level closest to (not smaller than) the on-screen size; the vertex color
                    // tints the white/greyscale artwork and carries the opacity.
                    if (definition.Art is { } art && resources.Art.GetWrapOrNull(art, ArtScreenPixels(art, primitive.Piece, a, b, d)) is { } artWrap)
                    {
                        var (u0, v0, u1, v1) = art.Window2D(primitive.Piece);
                        drawList.AddImageQuad(artWrap.Handle, a, b, c, d, new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(u0, v1), color);
                    }

                    break;
            }
        }

        PrimitiveBuffer.Clear();
    }

    /// <summary>
    /// The on-screen size, in pixels, of the whole artwork a quad A-B-C-D (D below A) draws
    /// <paramref name="piece"/> of: what picks its level. A whole artwork's longer side; a piece of
    /// sliced artwork scales its full-height strip by the artwork's long side over its height, and a
    /// cell of a frame scales by a side that keeps the artwork's proportions (a cap or the center
    /// piece), so every piece of one placement draws from the same level (a stretched fill never
    /// needs a larger one).
    /// </summary>
    internal static float ArtScreenPixels(BuiltInArtAsset art, ArtPiece piece, Vector2 a, Vector2 b, Vector2 d)
    {
        var longSide = Math.Max(art.PixelWidth, art.PixelHeight);
        if (ArtPieces.IsFrame(piece) && art.Frame is { } frame && art.PixelWidth > 0 && art.PixelHeight > 0)
        {
            var (row, column) = ArtPieces.FrameCell(piece);
            var (y0, y1) = ArtFrameSlices.Band(frame.Rows, row);
            var (x0, x1) = ArtFrameSlices.Band(frame.Columns, column);
            if (ArtFrameSlices.IsFixed(row) && y1 > y0)
            {
                return Vector2.Distance(a, d) * longSide / (y1 - y0);
            }

            if (ArtFrameSlices.IsFixed(column) && x1 > x0)
            {
                return Vector2.Distance(a, b) * longSide / (x1 - x0);
            }
        }

        return piece == ArtPiece.Whole || ArtPieces.IsFrame(piece) || art.PixelHeight <= 0
            ? MathF.Max(Vector2.Distance(a, b), Vector2.Distance(a, d))
            : Vector2.Distance(a, d) * longSide / art.PixelHeight;
    }
}
