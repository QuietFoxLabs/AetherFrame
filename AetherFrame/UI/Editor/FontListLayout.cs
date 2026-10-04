using System;

namespace AetherFrame.UI.Editor;

/// <summary>One row of the font list, in pixels (see <see cref="FontListLayout"/>).</summary>
/// <param name="SampleSize">The sample's size: its preview tier.</param>
/// <param name="Height">The row's height.</param>
/// <param name="Baseline">How far below the row's top its baseline is.</param>
internal readonly record struct FontListRow(float SampleSize, float Height, float Baseline)
{
    /// <summary>The room below the baseline.</summary>
    internal float BelowBaseline => Height - Baseline;
}

/// <summary>
/// The font list's rows (issue #116): each family's name in the interface font, so a script or
/// display face's name can always be read, then the same sample in the family's own face, both on
/// one baseline. Every row has the same height, whether its face is built yet or not, so nothing
/// moves as previews arrive. The row is tall enough for every bundled face's line box (ImGui draws a
/// face in a box from its ascent to its descent) and for what its sample's glyphs reach beyond it,
/// measured from the faces themselves (FontListLayoutTests); a face that doesn't fit, above, below or
/// across, is drawn just small enough to, and never cut off. Pure layout, free of ImGui.
/// </summary>
internal static class FontListLayout
{
    /// <summary>
    /// The room above the baseline, as a share of the sample's size. Every bundled face's ascent
    /// fits (at most 0.858 of its size once ImGui rounds it up, Lato's), and so does the ink of its
    /// sample, which reaches up to 0.04 above the box (Josefin Sans), with one exception: Press
    /// Start 2P's capitals fill its whole box, so it is drawn a little smaller.
    /// </summary>
    internal const float AboveBaseline = 0.88f;

    /// <summary>The room below the baseline, as a share of the sample's size: the deepest descent,
    /// Homemade Apple's (at most 0.429 once rounded), fits.</summary>
    internal const float BelowBaseline = 0.44f;

    /// <summary>
    /// The sample column's width, in multiples of the sample's size. Every bundled face's sample fits
    /// but those of Syncopate and Press Start 2P, the two widest, which are drawn smaller to fit.
    /// </summary>
    internal const float SampleColumnEms = 9f;

    /// <summary>
    /// A row for samples of <paramref name="sampleSize"/> pixels, beside names in an interface font
    /// whose ascent and descent (positive, below the baseline) are given, so the name always fits too.
    /// </summary>
    internal static FontListRow Row(float sampleSize, float interfaceAscent, float interfaceDescent)
    {
        var above = MathF.Ceiling(Math.Max(sampleSize * AboveBaseline, interfaceAscent));
        var below = MathF.Ceiling(Math.Max(sampleSize * BelowBaseline, interfaceDescent));
        return new FontListRow(sampleSize, above + below, above);
    }

    /// <summary>The sample column's least width, for samples of <paramref name="sampleSize"/> pixels.</summary>
    internal static float SampleColumn(float sampleSize) => MathF.Ceiling(sampleSize * SampleColumnEms);

    /// <summary>
    /// The scale a face's sample is drawn at: 1, the face's own size, crisp, unless its line box
    /// reaches past the row above or below the baseline, or the sample is wider than its column;
    /// then just small enough to fit, never cut off.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="ascent">The face's ascent above the baseline, in pixels at its size (ImGui's ImFont.Ascent).</param>
    /// <param name="descent">Its descent below the baseline, positive (minus ImFont.Descent).</param>
    /// <param name="width">The sample's width at the face's size.</param>
    /// <param name="column">The width the sample has.</param>
    internal static float SampleScale(in FontListRow row, float ascent, float descent, float width, float column)
    {
        var scale = 1f;
        if (ascent > row.Baseline)
        {
            scale = Math.Min(scale, row.Baseline / ascent);
        }

        if (descent > row.BelowBaseline)
        {
            scale = Math.Min(scale, row.BelowBaseline / descent);
        }

        if (width > column && column > 0f)
        {
            scale = Math.Min(scale, column / width);
        }

        return scale;
    }
}
