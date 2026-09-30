using System;
using System.Diagnostics.CodeAnalysis;
using AetherFrame.Domain.Components;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// When a finished Plate's parts draw anything at all: the renderer's own rules, in one place, so
/// that every surface that decides what a Plate shows (the renderer, and the snapshot a Plate is
/// shared as) decides it the same way. Pure logic, no Dalamud.
/// </summary>
public static class PaintVisibility
{
    /// <summary>A text's own opacity: its colour's alpha, clamped to 0 to 1 (outline and shadow included).</summary>
    public static float TextAlpha(TextProfileElement text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Math.Clamp(text.Color.W, 0f, 1f);
    }

    /// <summary>Whether a text draws anything: some content, at an opacity above 0.</summary>
    public static bool TextDraws([NotNullWhen(true)] string? content, float alpha) => !string.IsNullOrEmpty(content) && alpha > 0f;

    /// <summary>An image element's opacity, clamped to 0 to 1.</summary>
    public static float ImageOpacity(ImageProfileElement image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Math.Clamp(image.Opacity, 0f, 1f);
    }

    /// <summary>Whether an image element with its image draws anything: an opacity above 0.</summary>
    public static bool ImageDraws(ImageProfileElement image) => ImageOpacity(image) > 0f;

    /// <summary>
    /// The background's opacity when it draws anything, clamped to 0 to 1; 0 when there is none, or
    /// its mode is None (which draws nothing at all, pattern included).
    /// </summary>
    public static float BackgroundOpacity(ProfileBackground? background) =>
        background is null || background.Mode == ProfileBackgroundMode.None ? 0f : Math.Clamp(background.Opacity, 0f, 1f);

    /// <summary>The image a component's image primitive draws, or null when none is set (and it draws nothing).</summary>
    public static Guid? ComponentImage(PlateComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        return component.AssetId is { } asset && asset != Guid.Empty ? asset : null;
    }
}
