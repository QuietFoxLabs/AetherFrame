using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.UI.Editor;
using Xunit;
using Xunit.Abstractions;

namespace AetherFrame.Tests;

/// <summary>
/// The font list's rows and preview faces (issue #116), measured against the bundled faces the way
/// ImGui's builder draws them: the sample's size follows the interface in whole tiers; every face's
/// sample sits on the row's baseline at its own size and is never cut off, the two that don't fit
/// drawn just small enough to; and a preview face costs a fraction of a Plate font's tier.
/// </summary>
public class FontListLayoutTests(ITestOutputHelper output)
{
    /// <summary>Every size <see cref="FontPreview.Size"/> can give.</summary>
    private static readonly float[] PreviewSizes =
        FontTierPolicy.SizeLadder.Where(size => size >= FontPreview.SmallestSize && size <= FontPreview.LargestSize).ToArray();

    /// <summary>Every bundled family's Regular face (Dalamud Default has none to measure).</summary>
    private static readonly IReadOnlyList<(string FamilyId, string Name, FontTierPolicyTests.TrueTypeFace Face)> BundledFaces = LoadFaces();

    [Theory]
    [InlineData(16f, 28f)] // Dalamud's interface font at 100%
    [InlineData(17f, 28f)]
    [InlineData(12f, 20f)]
    [InlineData(20f, 32f)] // 125%
    [InlineData(24f, 40f)] // 150%
    [InlineData(32f, 56f)] // 200%
    [InlineData(64f, 56f)]
    [InlineData(4f, 20f)]
    [InlineData(float.NaN, 20f)]
    public void TheSampleSize_FollowsTheInterface_InWholeTiers(float interfaceSize, float expected)
    {
        Assert.Equal(expected, FontPreview.Size(interfaceSize));
        Assert.Equal(expected, FontTierPolicy.SizeLadder[FontPreview.TierIndex(interfaceSize)]);
    }

    [Fact]
    public void TheSample_IsInEveryPreviewFacesGlyphs()
    {
        var ranges = FontPreview.GlyphRanges;
        Assert.Equal(0, ranges[^1]);
        Assert.All(FontPreview.Sample, c => Assert.True(c >= ranges[0] && c <= ranges[1], $"'{c}'"));
        Assert.All(BundledFaces, f => Assert.All(FontPreview.Sample.Where(c => c != ' '), c => Assert.True(f.Face.Maps(c), $"{f.Name} lacks '{c}'")));
    }

    /// <summary>
    /// The tall and deep faces (UnifrakturMaguntia's blackletter, Homemade Apple's and the other
    /// scripts' long descenders, Josefin Sans's ascenders above its box) and every other: drawn at
    /// their own size, so their size in the list is their size on a Plate, with their box and every
    /// pixel of their sample inside the row, on its baseline.
    /// </summary>
    [Fact]
    public void EveryBundledFace_SitsOnTheRowsBaseline_AtItsOwnSize_AndNothingIsCutOff()
    {
        foreach (var size in PreviewSizes)
        {
            var row = FontListLayout.Row(size, 0f, 0f);
            foreach (var (_, name, face) in BundledFaces)
            {
                var (ascent, descent) = face.LineBox(size);
                var (above, below) = face.Ink(FontPreview.Sample, size);
                var scale = FontListLayout.SampleScale(row, ascent, descent, 0f, float.PositiveInfinity);
                var at = $"{name} at {size} px";

                // Press Start 2P's capitals fill its whole box, so it alone is drawn a little smaller.
                if (name == "PressStart2P")
                {
                    Assert.True(scale is > 0.8f and < 1f, $"{at}: {scale}");
                }
                else
                {
                    Assert.True(scale == 1f, $"{at}: drawn at {scale} of its size (box {ascent} + {descent} px, row {row.Baseline} + {row.BelowBaseline} px)");
                }

                Assert.True(row.Baseline - (ascent * scale) >= 0f, $"{at}: box above the row");
                Assert.True(row.Baseline + (descent * scale) <= row.Height, $"{at}: box below the row");
                Assert.True(row.Baseline - (above * scale) >= 0f, $"{at}: ink {above} px above the baseline, room {row.Baseline} px");
                Assert.True(row.Baseline + (below * scale) <= row.Height, $"{at}: ink {below} px below the baseline, room {row.BelowBaseline} px");
            }
        }
    }

    [Fact]
    public void EverySample_FitsItsColumn_ButTheTwoWidest()
    {
        var tooWide = BundledFaces.Where(f => f.Face.AdvanceEms(FontPreview.Sample) > FontListLayout.SampleColumnEms).Select(f => f.Name).Order().ToList();

        Assert.Equal(["PressStart2P", "Syncopate"], tooWide);
    }

    [Fact]
    public void ASampleTooWideOrTooTall_IsDrawnJustSmallEnough_TheTightestLimitFirst()
    {
        var row = FontListLayout.Row(28f, 0f, 0f);
        Assert.Equal(25f, row.Baseline);
        Assert.Equal(38f, row.Height);

        Assert.Equal(1f, FontListLayout.SampleScale(row, 20f, 8f, 150f, 252f));
        Assert.Equal(0.5f, FontListLayout.SampleScale(row, 20f, 8f, 504f, 252f)); // twice its column's width
        Assert.Equal(25f / 28f, FontListLayout.SampleScale(row, 28f, 0f, 150f, 252f)); // a box all above the baseline
        Assert.Equal(13f / 20f, FontListLayout.SampleScale(row, 10f, 20f, 150f, 252f)); // a deep one
        Assert.Equal(0.5f, FontListLayout.SampleScale(row, 28f, 0f, 504f, 252f));
        Assert.Equal(252f, FontListLayout.SampleColumn(28f));
    }

    [Fact]
    public void ARow_HasRoomForItsName_HoweverLargeTheInterfaceFont()
    {
        var row = FontListLayout.Row(20f, 30f, 12f);

        Assert.Equal(30f, row.Baseline);
        Assert.Equal(12f, row.BelowBaseline);
    }

    /// <summary>
    /// A preview face holds Basic Latin only: a fraction of the surface of the same family's Plate
    /// tier at the same size, which holds every glyph the face maps. The whole library's previews
    /// together, measured from the faces as ImGui packs them, are printed beside their Plate tiers.
    /// </summary>
    [Fact]
    public void APreviewFace_CostsAFractionOfAPlateTier()
    {
        var size = FontPreview.Size(16f);
        long previews = 0;
        long tiers = 0;
        var heaviest = 0L;
        foreach (var (familyId, name, face) in BundledFaces)
        {
            var preview = face.Surface(size, c => c >= FontPreview.GlyphRanges[0] && c <= FontPreview.GlyphRanges[1]);
            var tier = FontTierPolicy.EstimatedSurfacePixels(familyId, size);
            Assert.True(preview * 4 <= tier, $"{name}: preview {preview} px, Plate tier {tier} px");
            previews += preview;
            tiers += tier;
            heaviest = Math.Max(heaviest, preview);
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"At {size} px, {BundledFaces.Count} faces: previews {previews:N0} glyph px in all (the heaviest {heaviest:N0}); the same families' Plate tiers {tiers:N0} glyph px."));

        // One preview fits a 512 x 256 texture with room to spare.
        Assert.True(heaviest < 512 * 256 / 2, $"{heaviest}");
    }

    private static List<(string FamilyId, string Name, FontTierPolicyTests.TrueTypeFace Face)> LoadFaces()
    {
        var faces = new List<(string FamilyId, string Name, FontTierPolicyTests.TrueTypeFace Face)>();
        var fonts = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Fonts");
        foreach (var (familyId, prefix) in new[]
        {
            (ProfileFontFamilies.AetherFrameSans, "PTSans"),
            (ProfileFontFamilies.AetherFrameSerif, "PTSerif"),
            (ProfileFontFamilies.AetherFrameMono, "Cousine"),
        })
        {
            faces.Add((familyId, prefix, FontTierPolicyTests.TrueTypeFace.Load(Path.Combine(fonts, $"{prefix}-Regular.ttf"))));
        }

        foreach (var family in FontLibrary.Families)
        {
            faces.Add((family.Id, family.FilePrefix, FontTierPolicyTests.TrueTypeFace.Load(Path.Combine(fonts, "Library", $"{family.FilePrefix}-Regular.ttf"))));
        }

        return faces;
    }
}
