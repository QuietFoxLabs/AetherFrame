using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Domain.Templates;
using AetherFrame.Services.Packages;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// <see cref="FontTierPolicy"/>: the size ladder and tier snapping the font cache uses, its
/// per-family surface estimate calibrated against the bundled TTFs (measured the way ImGui's
/// stb_truetype builder does), the per-family size cap that keeps a single tier inside the
/// single-tier budget however a size is reached (package FontSize, editor zoom), the atlas
/// budget the cache evicts against, and the glyph ranges the bundled families are built with.
/// </summary>
public class FontTierPolicyTests
{
    private static readonly string[] BundledFamilies =
    [
        ProfileFontFamilies.AetherFrameSans, ProfileFontFamilies.AetherFrameSerif, ProfileFontFamilies.AetherFrameMono,
    ];

    private static readonly string[] AllFamilies =
    [
        ProfileFontFamilies.AetherFrameSans, ProfileFontFamilies.AetherFrameSerif, ProfileFontFamilies.AetherFrameMono,
        ProfileFontFamilies.DalamudDefault,
    ];

    private static readonly (string FamilyId, string FilePrefix)[] FamilyFiles =
    [
        (ProfileFontFamilies.AetherFrameSans, "PTSans"),
        (ProfileFontFamilies.AetherFrameSerif, "PTSerif"),
        (ProfileFontFamilies.AetherFrameMono, "Cousine"),
    ];

    private static readonly string[] FaceSuffixes = ["Regular", "Bold", "Italic", "BoldItalic"];

    // ---- Ladder and snapping ----------------------------------------------------------------

    [Fact]
    public void Ladder_IsAscending_AndHoldsTheCommonAndDocumentedSizes()
    {
        var ladder = FontTierPolicy.SizeLadder;
        Assert.Equal(26, ladder.Count);
        Assert.True(ladder.Zip(ladder.Skip(1)).All(pair => pair.First < pair.Second));
        Assert.All(FontTierPolicy.CommonEditorSizes, size => Assert.Contains(size, ladder));
        Assert.All(new[] { 24f, 48f, 72f, 120f, 180f }, size => Assert.Contains(size, ladder));
        Assert.Equal(460f, ladder[^1]);
    }

    [Theory]
    [InlineData(0.5f, 10f)]
    [InlineData(10f, 10f)]
    [InlineData(10.01f, 12f)]
    [InlineData(96f, 96f)]
    [InlineData(97f, 110f)]
    public void FindTierIndex_SnapsToTheSmallestTierAtOrAboveTheRequest(float requested, float expectedTier)
    {
        // Below every bundled family's cap; Dalamud Default caps at 96 and is covered below.
        foreach (var family in BundledFamilies)
        {
            var index = FontTierPolicy.FindTierIndex(family, requested);
            Assert.Equal(expectedTier, FontTierPolicy.SizeLadder[index]);
        }
    }

    [Fact]
    public void FindTierIndex_NeverPassesTheFamilyCap_AndUsesTheLargestAllowedTierAbove()
    {
        foreach (var family in AllFamilies)
        {
            var cap = FontTierPolicy.MaxTierIndex(family);
            Assert.Equal(cap, FontTierPolicy.FindTierIndex(family, FontTierPolicy.MaxTierSize(family)));
            Assert.Equal(cap, FontTierPolicy.FindTierIndex(family, FontTierPolicy.MaxTierSize(family) + 1f));
            Assert.Equal(cap, FontTierPolicy.FindTierIndex(family, 100_000f));
            Assert.Equal(cap, FontTierPolicy.FindTierIndex(family, float.PositiveInfinity));
        }
    }

    [Fact]
    public void FamilyCaps_ArePinned()
    {
        // Derived from the budget and the calibrated models below; a change here is a change in
        // what zoom level starts upscaling, and should be deliberate.
        Assert.Equal(280f, FontTierPolicy.MaxTierSize(ProfileFontFamilies.AetherFrameSans));
        Assert.Equal(240f, FontTierPolicy.MaxTierSize(ProfileFontFamilies.AetherFrameSerif));
        Assert.Equal(160f, FontTierPolicy.MaxTierSize(ProfileFontFamilies.AetherFrameMono));
        Assert.Equal(TextProfileElement.MaxFontSize, FontTierPolicy.MaxTierSize(ProfileFontFamilies.DalamudDefault));
        Assert.Equal(12_000_000L, FontTierPolicy.SingleTierBudgetPixels);
        Assert.Equal(16L * 4096 * 4096, FontTierPolicy.AtlasBudgetPixels);
    }

    [Fact]
    public void EveryCommonEditorSize_IsBelowEveryFamilyCap()
    {
        // The eager baseline is built as is for every family; the cap only ever affects zoom.
        foreach (var family in AllFamilies)
        {
            Assert.All(FontTierPolicy.CommonEditorSizes, size => Assert.True(size <= FontTierPolicy.MaxTierSize(family)));
        }
    }

    // ---- Family resolution ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dalamud-default")]
    [InlineData("aetherframe-fancy")]
    [InlineData("AETHERFRAME-SANS")]
    public void UnknownOrLegacyFamilyIds_ResolveToDalamudDefault_WithItsCapAndCharge(string? familyId)
    {
        Assert.Equal(ProfileFontFamilies.DalamudDefault, FontTierPolicy.ResolveFamilyId(familyId));
        Assert.Equal(FontTierPolicy.MaxTierIndex(ProfileFontFamilies.DalamudDefault), FontTierPolicy.MaxTierIndex(familyId));
        Assert.Null(FontTierPolicy.GlyphRanges(familyId));
        Assert.Equal(0, FontTierPolicy.GlyphCount(familyId));
        Assert.Equal(FontTierPolicy.EstimatedSurfacePixels(ProfileFontFamilies.DalamudDefault, 16f), FontTierPolicy.EstimatedSurfacePixels(familyId, 460f));
        Assert.True(FontTierPolicy.EstimatedSurfacePixels(familyId, 96f) > 0);
    }

    [Fact]
    public void BundledFamilies_ResolveToThemselves_AndShareTheBundledRanges()
    {
        foreach (var family in BundledFamilies)
        {
            Assert.Equal(family, FontTierPolicy.ResolveFamilyId(family));
            Assert.True(FontTierPolicy.GlyphCount(family) > 0);
            Assert.Same(FontTierPolicy.GlyphRanges(ProfileFontFamilies.AetherFrameSans), FontTierPolicy.GlyphRanges(family));
        }
    }

    // ---- Surface budget: single tier --------------------------------------------------------

    [Fact]
    public void EveryReachableTier_StaysUnderTheSingleTierBudget()
    {
        // Every FontSize a package may carry, at every editor zoom, for every family and style:
        // the chosen tier's estimated surface fits the single-tier budget.
        var zooms = new[] { EditorSession.MinZoom, 0.5f, 1f, 1.5f, 2f, 3f, EditorSession.MaxZoom };
        foreach (var family in AllFamilies)
        {
            for (var fontSize = 1f; fontSize <= PackagePolicy.MaxFontSize; fontSize += 1f)
            {
                foreach (var zoom in zooms)
                {
                    var tier = FontTierPolicy.SizeLadder[FontTierPolicy.FindTierIndex(family, fontSize * zoom)];
                    var surface = FontTierPolicy.EstimatedSurfacePixels(family, tier);
                    Assert.True(surface <= FontTierPolicy.SingleTierBudgetPixels,
                        $"{family} at FontSize {fontSize} x zoom {zoom} -> tier {tier} px = {surface / 1e6:F1} Mpx");
                }
            }
        }
    }

    [Fact]
    public void TheTierAboveEachCap_WouldExceedTheBudget()
    {
        // The cap is the largest tier that fits, not merely one that fits.
        foreach (var family in BundledFamilies)
        {
            var next = FontTierPolicy.MaxTierIndex(family) + 1;
            Assert.True(next < FontTierPolicy.SizeLadder.Count);
            Assert.True(FontTierPolicy.EstimatedSurfacePixels(family, FontTierPolicy.SizeLadder[next]) > FontTierPolicy.SingleTierBudgetPixels);
        }
    }

    [Fact]
    public void SurfaceEstimate_GrowsWithSize_AndIsNeverNegative()
    {
        foreach (var family in BundledFamilies)
        {
            Assert.True(FontTierPolicy.EstimatedSurfacePixels(family, 0f) > 0);
            Assert.True(FontTierPolicy.EstimatedSurfacePixels(family, -5f) == FontTierPolicy.EstimatedSurfacePixels(family, 0f));
            long previous = 0;
            foreach (var size in FontTierPolicy.SizeLadder)
            {
                var surface = FontTierPolicy.EstimatedSurfacePixels(family, size);
                Assert.True(surface > previous);
                previous = surface;
            }
        }
    }

    // ---- Surface budget: the whole atlas ----------------------------------------------------

    [Fact]
    public void CommonSizesForEveryCombo_PlusOneMaxTierEach_FitTheAtlasBudget()
    {
        // The largest working set one Plate can legitimately need at once: every family with
        // every bold/italic combination at the eager common sizes, plus its largest allowed tier.
        // Dalamud Default has no real bold/italic faces, so its four combos are one set of keys
        // — exactly how the service keys them. If this did not fit, the LRU would evict handles
        // that are drawn every frame and rebuild the atlas continuously.
        var keys = new HashSet<(string Family, float Size, bool Bold, bool Italic)>();
        foreach (var family in AllFamilies)
        {
            var styled = family != ProfileFontFamilies.DalamudDefault;
            foreach (var bold in new[] { false, true })
            {
                foreach (var italic in new[] { false, true })
                {
                    var effectiveBold = bold && styled;
                    var effectiveItalic = italic && styled;
                    foreach (var size in FontTierPolicy.CommonEditorSizes)
                    {
                        keys.Add((family, size, effectiveBold, effectiveItalic));
                    }

                    keys.Add((family, FontTierPolicy.MaxTierSize(family), effectiveBold, effectiveItalic));
                }
            }
        }

        var total = keys.Sum(k => FontTierPolicy.EstimatedSurfacePixels(k.Family, k.Size));
        Assert.True(total <= FontTierPolicy.AtlasBudgetPixels, $"{keys.Count} handles estimate {total / 1e6:F0} Mpx");
        Assert.True(total >= FontTierPolicy.AtlasBudgetPixels / 2, "the budget should not be far looser than the largest legitimate working set");
    }

    [Fact]
    public void AHostileMonoPackage_CostsAtMostFourSingleTiers_NotHundredsOfMegapixels()
    {
        // Mono at the package FontSize ceiling in all four styles, as the Import Preview warms
        // it: bounded by the cap instead of the top of the ladder (which alone was 118 Mpx).
        var tier = FontTierPolicy.SizeLadder[FontTierPolicy.FindTierIndex(ProfileFontFamilies.AetherFrameMono, PackagePolicy.MaxFontSize)];
        var perStyle = FontTierPolicy.EstimatedSurfacePixels(ProfileFontFamilies.AetherFrameMono, tier);
        Assert.Equal(FontTierPolicy.MaxTierSize(ProfileFontFamilies.AetherFrameMono), tier);
        Assert.True(perStyle <= FontTierPolicy.SingleTierBudgetPixels);
        Assert.True(4 * perStyle < FontTierPolicy.AtlasBudgetPixels / 4, $"four styles estimate {4 * perStyle / 1e6:F0} Mpx");
    }

    [Fact]
    public async Task Package_WithMonoTextAtMaxFontSize_IsAccepted_AndPreviewsAtACappedTier()
    {
        // Reachability: a package whose text uses the Mono family at PackagePolicy.MaxFontSize
        // passes validation and yields a PreviewDocument, which the Import Preview draws (and so
        // warms) before the user imports. The tier that warming asks the policy for is capped.
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Huge");
        var document = library.OpenDocumentForEditing(created.PlateId);
        document.Elements.Add(new TextProfileElement { Text = "hello", Position = new Vector2(100, 10), Size = new Vector2(200, 40), ZIndex = 1 });
        await library.SavePlateDocumentAsync(document);
        var valid = fixture.Export(packages, created.PlateId, "valid.aetherframe");

        var hostile = PackageFiles.Rewrite(valid, entries => PackageFiles.EditProfile(entries, p =>
        {
            var text = ((JsonArray)p["Elements"]!).First(e => e!["elementType"]!.GetValue<string>() == "text")!;
            text["FontFamily"] = ProfileFontFamilies.AetherFrameMono;
            text["FontSize"] = PackagePolicy.MaxFontSize;
        }));

        using var staged = packages.Inspect(hostile);
        Assert.True(staged.CanImport, string.Join("; ", staged.Diagnostics.Errors));
        var previewText = Assert.Single(staged.PreviewDocument!.Elements.OfType<TextProfileElement>());
        Assert.Equal(ProfileFontFamilies.AetherFrameMono, previewText.FontFamily);
        Assert.Equal(PackagePolicy.MaxFontSize, previewText.FontSize);

        var tier = FontTierPolicy.SizeLadder[FontTierPolicy.FindTierIndex(previewText.FontFamily, previewText.FontSize)];
        Assert.Equal(FontTierPolicy.MaxTierSize(ProfileFontFamilies.AetherFrameMono), tier);
        Assert.True(FontTierPolicy.EstimatedSurfacePixels(previewText.FontFamily, tier) <= FontTierPolicy.SingleTierBudgetPixels);
    }

    // ---- Calibration against the bundled TTFs -----------------------------------------------

    [Fact]
    public void GlyphCount_IsWhatTheFamilysHeaviestFaceMapsInsideTheRanges()
    {
        foreach (var (family, prefix) in FamilyFiles)
        {
            var counts = FaceSuffixes.Select(suffix => TrueTypeFace.Load(FontPath(prefix, suffix)).CountMapped(FontTierPolicy.CoversCodepoint)).ToList();
            Assert.Equal(counts.Max(), FontTierPolicy.GlyphCount(family));

            // The faces of a family map the same set bar a handful (PT Serif Bold carries four
            // more), so one count per family is an honest bound for all of them.
            Assert.All(counts, count => Assert.InRange(count, FontTierPolicy.GlyphCount(family) - 4, FontTierPolicy.GlyphCount(family)));
        }
    }

    [Fact]
    public void SurfaceEstimate_IsNeverBelow_AndAtMostAFifthAbove_TheRealGlyphSurface()
    {
        // The estimate is fitted to the heaviest face, so it bounds every face from above at
        // every ladder size, while staying close enough to be a useful budget (the lightest face
        // is at most ~20% below it).
        foreach (var (family, prefix) in FamilyFiles)
        {
            foreach (var suffix in FaceSuffixes)
            {
                var face = TrueTypeFace.Load(FontPath(prefix, suffix));
                foreach (var size in FontTierPolicy.SizeLadder)
                {
                    var actual = face.Surface(size, FontTierPolicy.CoversCodepoint);
                    var estimate = FontTierPolicy.EstimatedSurfacePixels(family, size);
                    Assert.True(estimate >= actual, $"{prefix}-{suffix} at {size} px: real {actual / 1e6:F2} Mpx > estimate {estimate / 1e6:F2} Mpx");
                    Assert.True(estimate <= actual * 1.25, $"{prefix}-{suffix} at {size} px: estimate {estimate / 1e6:F2} Mpx is far above real {actual / 1e6:F2} Mpx");
                }
            }
        }
    }

    [Fact]
    public void ExplicitRanges_LeaveOutAThirdOfMonosFullCmap()
    {
        // The reason Mono reaches 160 px rather than 140: without ranges Dalamud builds every
        // glyph Cousine maps (over 2200), the phonetic alphabets, combining marks and box drawing
        // among them, and the full set would not fit the single-tier budget at the cap.
        var face = TrueTypeFace.Load(FontPath("Cousine", "Regular"));
        var cap = FontTierPolicy.MaxTierSize(ProfileFontFamilies.AetherFrameMono);
        var full = face.Surface(cap, _ => true);
        var ranged = face.Surface(cap, FontTierPolicy.CoversCodepoint);
        Assert.True(face.CountMapped(_ => true) > 2000);
        Assert.True(ranged * 4 < full * 3, $"ranged {ranged / 1e6:F1} Mpx vs full {full / 1e6:F1} Mpx");
        Assert.True(full > FontTierPolicy.SingleTierBudgetPixels, $"the full cmap fits the budget at {cap} px ({full / 1e6:F1} Mpx)");
        Assert.True(ranged <= FontTierPolicy.SingleTierBudgetPixels);
    }

    // ---- Glyph ranges -----------------------------------------------------------------------

    [Fact]
    public void GlyphRanges_AreInImGuiFormat()
    {
        // Dalamud's SafeFontConfig.ThrowOnInvalidValues: no leading zero, and a zero at the
        // last even index; ImGui reads inclusive pairs up to that terminator.
        var ranges = FontTierPolicy.GlyphRanges(ProfileFontFamilies.AetherFrameSans)!;
        Assert.True(ranges.Length % 2 == 1, "pairs plus a terminator");
        Assert.NotEqual(0, ranges[0]);
        Assert.Equal(0, ranges[(ranges.Length - 1) & -2]);
        Assert.Equal(0, ranges[^1]);
        for (var i = 0; i + 1 < ranges.Length - 1; i += 2)
        {
            Assert.True(ranges[i] <= ranges[i + 1], $"pair {i} is not ascending");
            if (i + 2 < ranges.Length - 1)
            {
                Assert.True(ranges[i + 1] < ranges[i + 2], $"pairs {i} and {i + 2} overlap or are unordered");
            }
        }

        Assert.True(FontTierPolicy.CoversCodepoint(' '));
        Assert.True(FontTierPolicy.CoversCodepoint('~'));
        Assert.True(FontTierPolicy.CoversCodepoint('·')); // the Basic separator
        Assert.True(FontTierPolicy.CoversCodepoint('Ж')); // Cyrillic
        Assert.True(FontTierPolicy.CoversCodepoint('…')); // ellipsis
        Assert.False(FontTierPolicy.CoversCodepoint(0x2500)); // box drawing
        Assert.False(FontTierPolicy.CoversCodepoint(0x4E2D)); // CJK
    }

    [Theory]
    [InlineData(0x00B2, "superscript two")]
    [InlineData(0x02BC, "modifier letter apostrophe (Ukrainian)")]
    [InlineData(0x03A9, "Greek capital omega")]
    [InlineData(0x0524, "Cyrillic Supplement")]
    [InlineData(0x05D0, "Hebrew alef")]
    [InlineData(0x1EAF, "Vietnamese a with breve and acute")]
    [InlineData(0x1E9E, "capital sharp s")]
    [InlineData(0x2082, "subscript two")]
    [InlineData(0x20AC, "euro sign")]
    [InlineData(0x2116, "numero sign")]
    [InlineData(0x2122, "trade mark sign")]
    [InlineData(0x2153, "vulgar fraction one third")]
    [InlineData(0x2192, "rightwards arrow")]
    [InlineData(0x2260, "not equal to")]
    [InlineData(0x221E, "infinity")]
    [InlineData(0x25CF, "black circle")]
    [InlineData(0x2665, "black heart suit")]
    [InlineData(0x266A, "eighth note")]
    [InlineData(0xFB01, "fi ligature")]
    public void GlyphRanges_KeepWhat015Rendered(int codepoint, string what)
    {
        // Every one of these is mapped by at least one bundled face and was rasterized by 0.1.5,
        // which built the faces with their whole cmap; the ranges must not lose them.
        Assert.True(FontTierPolicy.CoversCodepoint(codepoint), $"U+{codepoint:X4} ({what}) is outside the glyph ranges");
        Assert.Contains(FamilyFiles, f => TrueTypeFace.Load(FontPath(f.FilePrefix, "Regular")).Maps(codepoint));
    }

    [Theory]
    [InlineData(0x0259, "IPA schwa")]
    [InlineData(0x0301, "combining acute accent")]
    [InlineData(0x1D00, "phonetic extensions")]
    [InlineData(0x1F00, "Greek Extended")]
    [InlineData(0x2500, "box drawing")]
    [InlineData(0x2588, "block elements")]
    [InlineData(0x2726, "Dingbats (no face maps it)")]
    [InlineData(0xF500, "private use alternates")]
    [InlineData(0xFB1D, "Hebrew presentation forms")]
    [InlineData(0xFEFF, "byte order mark")]
    public void GlyphRanges_LeaveOutWhatNoPlateNeeds(int codepoint, string what)
    {
        Assert.False(FontTierPolicy.CoversCodepoint(codepoint), $"U+{codepoint:X4} ({what}) is inside the glyph ranges");
    }

    [Fact]
    public void EveryGlyphAFaceMapsOutsideTheRanges_IsInADeliberatelyExcludedBlock()
    {
        // The ranges are whole blocks chosen by hand; this pins what they leave out, so a font
        // swap or a block change is a visible decision rather than a silent loss of glyphs.
        var excluded = new (int From, int To, string Block)[]
        {
            (0x0000, 0x001F, "C0 controls"),
            (0x0250, 0x02AF, "IPA Extensions"),
            (0x0300, 0x036F, "Combining Diacritical Marks"),
            (0x1D00, 0x1DFF, "Phonetic Extensions and their supplement, Combining Diacritical Marks Supplement"),
            (0x1F00, 0x1FFF, "Greek Extended"),
            (0x20D0, 0x20FF, "Combining Diacritical Marks for Symbols"),
            (0x2300, 0x23FF, "Miscellaneous Technical"),
            (0x2500, 0x259F, "Box Drawing, Block Elements"),
            (0x2C60, 0x2C7F, "Latin Extended-C"),
            (0x2E00, 0x2E7F, "Supplemental Punctuation"),
            (0xA640, 0xA69F, "Cyrillic Extended-B"),
            (0xA700, 0xA7FF, "Modifier Tone Letters, Latin Extended-D"),
            (0xAB30, 0xAB6F, "Latin Extended-E"),
            (0xE000, 0xF8FF, "Private Use Area (the fonts' stylistic alternates)"),
            (0xFB07, 0xFB4F, "Alphabetic Presentation Forms beyond the Latin ligatures"),
            (0xFE00, 0xFE2F, "Variation Selectors, Combining Half Marks"),
            (0xFEFF, 0xFEFF, "byte order mark"),
        };

        var outside = 0;
        foreach (var (_, prefix) in FamilyFiles)
        {
            foreach (var suffix in FaceSuffixes)
            {
                var face = TrueTypeFace.Load(FontPath(prefix, suffix));
                foreach (var codepoint in face.MappedCodepoints.Where(c => !FontTierPolicy.CoversCodepoint(c)))
                {
                    outside++;
                    Assert.True(excluded.Any(b => codepoint >= b.From && codepoint <= b.To),
                        $"{prefix}-{suffix} maps U+{codepoint:X4} outside the glyph ranges and outside every deliberately excluded block");
                }
            }
        }

        Assert.True(outside > 1000, "the exclusions are meant to cost Cousine a third of its cmap");
    }

    [Fact]
    public void GlyphRanges_CoverEveryCharacterAetherFrameWritesIntoAPlate_AndEveryFaceMapsThem()
    {
        var text = new StringBuilder();
        var starter = new PlateStarterContent(new BasicCharacterInfo("Visible Hero", "Phoenix", "Light", 19, "Paladin", 100, "ABC"));
        foreach (var definition in BuiltInTemplateCatalog.All)
        {
            Collect(BuiltInTemplateCatalog.CreateDocument(definition.TemplateId, DateTime.UtcNow, starter), text);
            Collect(BuiltInTemplateCatalog.CreateDocument(definition.TemplateId, DateTime.UtcNow, null), text);
        }

        foreach (var layout in Enum.GetValues<PlateStartingLayout>())
        {
            Collect(PlateFactory.Create(layout, Guid.NewGuid(), PlateFactory.DefaultNameFor(layout), DateTime.UtcNow, starter), text);
        }

        text.Append(BasicPlateText.Separator);
        text.Append(BasicPlateText.FreeCompanyTag("TAG"));
        text.Append(BasicPlateText.Level(90));
        text.Append(BasicPlateText.World("Phoenix", "Light"));
        text.AppendJoin(' ', BasicPlateText.SuggestedPlaystyles);
        text.AppendJoin(' ', IdentityHeaderRules.DecorationSymbols);
        foreach (var role in Enum.GetValues<ProfileElementRole>())
        {
            text.Append(EditorPlaceholders.GetPlaceholder(new TextProfileElement { Role = role }));
        }

        var codepoints = text.ToString().Where(c => c > ' ').Select(c => (int)c).Distinct().ToList();
        Assert.True(codepoints.Count > 40);
        Assert.Contains(0x00B7, codepoints);
        Assert.Contains(0x00AB, codepoints);

        var faces = FamilyFiles.SelectMany(f => FaceSuffixes.Select(s => (Name: $"{f.FilePrefix}-{s}", Face: TrueTypeFace.Load(FontPath(f.FilePrefix, s))))).ToList();
        foreach (var codepoint in codepoints)
        {
            Assert.True(FontTierPolicy.CoversCodepoint(codepoint), $"U+{codepoint:X4} is outside the glyph ranges");
            foreach (var (name, face) in faces)
            {
                Assert.True(face.Maps(codepoint), $"{name} has no glyph for U+{codepoint:X4}");
            }
        }
    }

    [Fact]
    public void NoBundledFace_CarriesTheDingbatDecoration_SoTheRangesLeaveItOut()
    {
        // IdentityHeaderRules.IsDrawableDecoration rejects "✦" because the fonts lack it; the
        // ranges are consistent with that rather than paying for an empty block.
        foreach (var (_, prefix) in FamilyFiles)
        {
            Assert.False(TrueTypeFace.Load(FontPath(prefix, "Regular")).Maps(0x2726));
        }

        Assert.False(FontTierPolicy.CoversCodepoint(0x2726));
    }

    private static void Collect(ProfileDocument document, StringBuilder into)
    {
        foreach (var element in document.Elements)
        {
            if (element is TextProfileElement text)
            {
                into.Append(text.GetDisplayText());
                into.Append(EditorPlaceholders.GetPlaceholder(text));
            }
        }
    }

    private static string FontPath(string prefix, string suffix) =>
        Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Fonts", $"{prefix}-{suffix}.ttf");

    /// <summary>
    /// Minimal TrueType reader reproducing what ImGui's stb_truetype builder does per glyph
    /// (ImFontAtlasBuildWithStbTruetype step 5): for every codepoint in the range that maps to a
    /// glyph, scale = SizePx / (hhea.ascent - hhea.descent) (stbtt_ScaleForPixelHeight), bitmap
    /// box = floor/ceil of the glyf bbox at that scale, rect = (w + padding + oversample - 1) x
    /// (h + padding + oversample - 1) with padding 1 and oversample 1 (Dalamud's SafeFontConfig
    /// defaults).
    /// </summary>
    private sealed class TrueTypeFace
    {
        private readonly int ascentMinusDescent;
        private readonly (short X0, short Y0, short X1, short Y1)?[] boxes;
        private readonly Dictionary<int, int> codepointToGlyph;

        private TrueTypeFace(int ascentMinusDescent, (short, short, short, short)?[] boxes, Dictionary<int, int> map)
        {
            this.ascentMinusDescent = ascentMinusDescent;
            this.boxes = boxes;
            codepointToGlyph = map;
        }

        internal bool Maps(int codepoint) => codepointToGlyph.ContainsKey(codepoint);

        internal IEnumerable<int> MappedCodepoints => codepointToGlyph.Keys;

        internal int CountMapped(Func<int, bool> include) => codepointToGlyph.Keys.Count(include);

        internal long Surface(float sizePx, Func<int, bool> include)
        {
            var scale = sizePx / ascentMinusDescent;
            long total = 0;
            foreach (var (codepoint, glyph) in codepointToGlyph)
            {
                if (!include(codepoint))
                {
                    continue;
                }

                int x0 = 0, y0 = 0, x1 = 0, y1 = 0;
                if (boxes[glyph] is { } b)
                {
                    x0 = (int)Math.Floor(b.X0 * scale);
                    y0 = (int)Math.Floor(-b.Y1 * scale);
                    x1 = (int)Math.Ceiling(b.X1 * scale);
                    y1 = (int)Math.Ceiling(-b.Y0 * scale);
                }

                total += (long)(x1 - x0 + 1) * (y1 - y0 + 1);
            }

            return total;
        }

        internal static TrueTypeFace Load(string path)
        {
            var b = File.ReadAllBytes(path);
            var tables = new Dictionary<string, int>();
            var numTables = U16(b, 4);
            for (var i = 0; i < numTables; i++)
            {
                var record = 12 + (16 * i);
                tables[Encoding.ASCII.GetString(b, record, 4)] = (int)U32(b, record + 8);
            }

            var head = tables["head"];
            var indexToLocFormat = (short)U16(b, head + 50);
            var hhea = tables["hhea"];
            var ascent = (short)U16(b, hhea + 4);
            var descent = (short)U16(b, hhea + 6);
            var numGlyphs = U16(b, tables["maxp"] + 4);
            var loca = tables["loca"];
            var glyf = tables["glyf"];
            var boxes = new (short, short, short, short)?[numGlyphs];
            for (var g = 0; g < numGlyphs; g++)
            {
                long start = indexToLocFormat == 0 ? 2L * U16(b, loca + (2 * g)) : U32(b, loca + (4 * g));
                long end = indexToLocFormat == 0 ? 2L * U16(b, loca + (2 * (g + 1))) : U32(b, loca + (4 * (g + 1)));
                if (end > start)
                {
                    var o = (int)(glyf + start);
                    boxes[g] = ((short)U16(b, o + 2), (short)U16(b, o + 4), (short)U16(b, o + 6), (short)U16(b, o + 8));
                }
            }

            var cmap = tables["cmap"];
            var sub = -1;
            var count = U16(b, cmap + 2);
            for (var i = 0; i < count; i++)
            {
                var platform = U16(b, cmap + 4 + (8 * i));
                var encoding = U16(b, cmap + 6 + (8 * i));
                if ((platform == 3 && encoding is 1 or 10) || platform == 0)
                {
                    sub = cmap + (int)U32(b, cmap + 8 + (8 * i));
                }
            }

            Assert.Equal(4, U16(b, sub));
            var map = new Dictionary<int, int>();
            var segX2 = U16(b, sub + 6);
            var seg = segX2 / 2;
            var endsAt = sub + 14;
            var startsAt = sub + 16 + segX2;
            var deltasAt = sub + 16 + (2 * segX2);
            var rangeOffsetsAt = sub + 16 + (3 * segX2);
            for (var s = 0; s < seg; s++)
            {
                int end = U16(b, endsAt + (2 * s));
                int start = U16(b, startsAt + (2 * s));
                var delta = (short)U16(b, deltasAt + (2 * s));
                int rangeOffset = U16(b, rangeOffsetsAt + (2 * s));
                for (var c = start; c <= end && c != 0xFFFF; c++)
                {
                    int glyph;
                    if (rangeOffset == 0)
                    {
                        glyph = (c + delta) & 0xFFFF;
                    }
                    else
                    {
                        glyph = U16(b, rangeOffsetsAt + (2 * s) + rangeOffset + (2 * (c - start)));
                        if (glyph != 0)
                        {
                            glyph = (glyph + delta) & 0xFFFF;
                        }
                    }

                    if (glyph != 0 && glyph < numGlyphs)
                    {
                        map[c] = glyph;
                    }
                }
            }

            return new TrueTypeFace(ascent - descent, boxes, map);
        }

        private static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];

        private static uint U32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
    }
}
