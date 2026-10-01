using System;
using System.Numerics;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace AetherFrame.Windows.Network;

/// <summary>
/// Draws a <see cref="ServedPlate"/> as the specification's section 8.5 says a viewer draws one,
/// through AetherFrame's own renderer: the background with <c>ProfileBackgroundRenderer</c>, each
/// text with <c>ProfileTextRenderer</c>, each image through <c>ProfileRenderer</c>'s image path,
/// and each shape as <c>ComponentRenderer</c> draws a Component's. Everything is clipped to the
/// region the caller gives the Plate, so nothing a Plate holds can draw over the window. An image
/// that wasn't received, or didn't pass its checks, isn't drawn; a text in a font this build doesn't
/// bundle is drawn as a box. Compiled only in the networking preview flavour.
/// </summary>
internal static class ServedPlatePainter
{
    private static readonly Vector4 BackdropColor = new(0.09f, 0.09f, 0.09f, 1f);
    private static readonly Vector4 PlaceholderFill = new(0.2f, 0.2f, 0.2f, 0.6f);
    private static readonly Vector4 PlaceholderBorder = new(0.5f, 0.5f, 0.5f, 0.8f);

    /// <param name="imageOf">The texture of each served image by index, or null for none.</param>
    /// <param name="drawBackdrop">Whether to draw the workspace backdrop under the canvas, as an editor does; the Plate Viewer doesn't, so the game shows through.</param>
    internal static void Draw(ImDrawListPtr drawList, ServedPlate plate, Vector2 origin, float scale, Vector2 clipMin, Vector2 clipMax, ProfileRenderResources resources, Func<int, IDalamudTextureWrap?> imageOf, bool drawBackdrop = true)
    {
        resources.Fonts.EnsurePrewarmed(plate.FontWarmup);
        drawList.PushClipRect(clipMin, clipMax, true);
        try
        {
            var canvas = plate.Canvas * scale;
            if (drawBackdrop)
            {
                drawList.AddRectFilled(origin, origin + canvas, ImGui.GetColorU32(BackdropColor));
            }

            ProfileBackgroundRenderer.Draw(drawList, plate.Background, origin, canvas, scale, resources, plate.BackgroundIndex >= 0 ? imageOf(plate.BackgroundIndex) : null, placeholderWhenMissing: false);

            foreach (var step in plate.Steps)
            {
                switch (step)
                {
                    case ServedText text:
                        DrawText(drawList, text, origin, scale, resources);
                        break;

                    case ServedImageStep image when imageOf(image.Index) is { } wrap:
                        ProfileRenderer.DrawImageElement(drawList, image.Element, origin, scale, wrap);
                        break;

                    case ServedShape shape:
                        DrawShape(drawList, shape, origin, scale, resources, imageOf);
                        break;
                }
            }
        }
        finally
        {
            drawList.PopClipRect();
        }
    }

    /// <summary>Drops the text layouts drawing <paramref name="plate"/> made, once the viewer lets it go.</summary>
    internal static void Forget(ServedPlate plate)
    {
        foreach (var step in plate.Steps)
        {
            if (step is ServedText text)
            {
                ProfileTextRenderer.Forget(text.Element.Id);
            }
        }
    }

    private static void DrawText(ImDrawListPtr drawList, ServedText text, Vector2 origin, float scale, ProfileRenderResources resources)
    {
        var position = origin + (text.Element.Position * scale);
        var size = text.Element.Size * scale;
        if (!text.FontKnown)
        {
            drawList.AddRectFilled(position, position + size, ImGui.GetColorU32(PlaceholderFill));
            drawList.AddRect(position, position + size, ImGui.GetColorU32(PlaceholderBorder));
            return;
        }

        ProfileTextRenderer.Draw(drawList, text.Element, position, size, scale, resources.Fonts, null, text.Text);
    }

    private static void DrawShape(ImDrawListPtr drawList, ServedShape shape, Vector2 origin, float scale, ProfileRenderResources resources, Func<int, IDalamudTextureWrap?> imageOf)
    {
        var a = origin + (shape.A * scale);
        var b = origin + (shape.B * scale);
        var c = origin + (shape.C * scale);
        var d = origin + (shape.D * scale);
        var color = ImGui.GetColorU32(shape.Color);
        switch (shape.Kind)
        {
            case ServedShapeKind.Quad:
                drawList.AddQuadFilled(a, b, c, d, color);
                break;

            case ServedShapeKind.Triangle:
                drawList.AddTriangleFilled(a, b, c, color);
                break;

            case ServedShapeKind.Image:
                if (imageOf(shape.Index) is { } wrap)
                {
                    drawList.AddImageQuad(wrap.Handle, a, b, c, d, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), color);
                }

                break;

            case ServedShapeKind.Art:
                // As ComponentRenderer: the level closest to the on-screen size, tinted by the vertex colour.
                var screenPixels = MathF.Max(Vector2.Distance(a, b), Vector2.Distance(a, d));
                if (shape.Art is null)
                {
                    // An artwork this build doesn't bundle: a placeholder, named in a note (section 8.5).
                    drawList.AddQuadFilled(a, b, c, d, ImGui.GetColorU32(PlaceholderFill));
                    drawList.AddQuad(a, b, c, d, ImGui.GetColorU32(PlaceholderBorder));
                }
                else if (resources.Art.GetWrapOrNull(shape.Art, screenPixels) is { } artWrap)
                {
                    drawList.AddImageQuad(artWrap.Handle, a, b, c, d, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), color);
                }

                break;
        }
    }
}
