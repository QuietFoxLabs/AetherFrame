using System;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Packages;

namespace AetherFrame.UI.Editor;

/// <summary>
/// What a style slider stores. A drag stays inside the slider's range, the convenient part of it
/// (ImGui keeps a drag there by itself). A value typed with Ctrl+Click may go past that range, as
/// 0.1.5 allowed, as far as a Plate can hold, draw and still export (see <see cref="PackagePolicy"/>),
/// so these sliders don't use ImGuiSliderFlags.AlwaysClamp: each value goes through the rule
/// here instead. Every result is finite and inside the package limits, so no typed value can make
/// a Plate unsaveable or unexportable. A value that isn't a number changes nothing, and an
/// infinite one (a typed 1e39 overflows to infinity) takes the limit.
///
/// <para>Opacity, Pattern intensity and outline thickness keep their slider's range as the
/// limit, with AlwaysClamp: the renderer draws opacity within 0-100% and an outline at most
/// <see cref="TextProfileElement.MaxOutlineThickness"/> wide, so typing past those never changed
/// the drawing.</para>
/// </summary>
internal static class TypedSliderValues
{
    /// <summary>The smallest font size the text renderer draws: it floors every size at 1 px.</summary>
    internal const float MinFontSize = 1f;

    /// <summary>A font size: from <see cref="MinFontSize"/> up to <see cref="PackagePolicy.MaxFontSize"/>.</summary>
    internal static float FontSize(float value, float current) => Limit(value, current, MinFontSize, PackagePolicy.MaxFontSize);

    /// <summary>Auto Fit's minimum size: the same range as <see cref="FontSize"/> (the renderer uses
    /// at most the element's own size).</summary>
    internal static float AutoFitMinimum(float value, float current) => Limit(value, current, MinFontSize, PackagePolicy.MaxFontSize);

    /// <summary>Letter spacing (px) or line spacing (x): within ±<see cref="PackagePolicy.MaxStyleMagnitude"/>.</summary>
    internal static float Spacing(float value, float current) =>
        Limit(value, current, -PackagePolicy.MaxStyleMagnitude, PackagePolicy.MaxStyleMagnitude);

    /// <summary>
    /// A gradient or Pattern angle on a 0-360 slider. The renderer turns it into a direction, so
    /// a typed value outside 0-360 wraps into it (-90 is 270, 450 is 90) and draws exactly as the
    /// unwrapped value did in 0.1.5, while staying in the slider's range and in the package's. An
    /// infinite angle has no direction, so it changes nothing.
    /// </summary>
    internal static float Angle(float value, float current)
    {
        if (!float.IsFinite(value))
        {
            return current;
        }

        return value is >= 0f and <= 360f ? value : RotationGeometry.NormalizeDegrees(value);
    }

    private static float Limit(float value, float current, float min, float max) =>
        float.IsNaN(value) ? current : Math.Clamp(value, min, max);
}
