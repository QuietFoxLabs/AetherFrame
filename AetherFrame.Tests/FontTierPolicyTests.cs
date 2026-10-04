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
        Assert.Equal(140f, FontTierPolicy.MaxTierSize(ProfileFontFamilies.AetherFrameMono));
        Assert.Equal(TextProfileElement.MaxFontSize, FontTierPolicy.MaxTierSize(ProfileFontFamilies.DalamudDefault));
        Assert.Equal(14_000_000L, FontTierPolicy.SingleTierBudgetPixels);
        Assert.Equal(18L * 4096 * 4096, FontTierPolicy.AtlasBudgetPixels);
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

            // The faces of a family map the same set bar a handful (PT Serif's bold faces carry six
            // more than its Regular), so one count per family is an honest bound for all of them.
            Assert.All(counts, count => Assert.InRange(count, FontTierPolicy.GlyphCount(family) - 6, FontTierPolicy.GlyphCount(family)));
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
    public void EveryFamilysHeaviestTierAtItsCap_PacksIntoOneAtlasTexture()
    {
        // What the single-tier budget is for: no tier needs a second 4096x4096 texture of its
        // own. Packed the way ImGui packs a font (stb_rect_pack's skyline, bottom-left, rects
        // sorted by height), the heaviest face of every family at its cap fits one texture with
        // room to spare, every glyph it maps included.
        foreach (var (family, prefix) in FamilyFiles)
        {
            var cap = FontTierPolicy.MaxTierSize(family);
            var heaviest = FaceSuffixes.Select(suffix => TrueTypeFace.Load(FontPath(prefix, suffix)))
                .MaxBy(face => face.Surface(cap, FontTierPolicy.CoversCodepoint))!;
            var rows = PackedHeight(heaviest.Rects(cap, FontTierPolicy.CoversCodepoint), AtlasTextureSide);
            Assert.True(rows <= AtlasTextureSide * 85 / 100, $"{prefix} at its {cap} px cap needs {rows} of a texture's {AtlasTextureSide} rows");
        }
    }

    [Fact]
    public void TheCapsKeepEveryGlyph_AndStillBoundMono_AGlyphSetOfThe015Size()
    {
        // The whole cmap is kept (see EveryGlyphABundledFaceMaps_IsInTheRanges_As015BuiltThem),
        // and the cap is what bounds a tier: Mono's top ladder tier alone would be over 100 Mpx.
        var cousine = TrueTypeFace.Load(FontPath("Cousine", "BoldItalic"));
        Assert.True(cousine.CountMapped(_ => true) > 2200);
        var top = FontTierPolicy.SizeLadder[^1];
        Assert.True(cousine.Surface(top, FontTierPolicy.CoversCodepoint) > 100_000_000);
        Assert.True(FontTierPolicy.EstimatedSurfacePixels(ProfileFontFamilies.AetherFrameMono, FontTierPolicy.MaxTierSize(ProfileFontFamilies.AetherFrameMono))
            <= FontTierPolicy.SingleTierBudgetPixels);
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
        Assert.True(FontTierPolicy.CoversCodepoint(0x2550)); // box drawing, decorative text in Mono
        Assert.True(FontTierPolicy.CoversCodepoint(0x0301)); // combining mark
        Assert.True(FontTierPolicy.CoversCodepoint(0xFFFE));
        Assert.False(FontTierPolicy.CoversCodepoint(0x0000));
        Assert.False(FontTierPolicy.CoversCodepoint(0xFFFF));

        // Exactly what Dalamud builds for a font given no ranges, as 0.1.5 did.
        Assert.Equal(new ushort[] { 0x0001, 0xFFFE, 0 }, ranges);

        // Covered is not mapped: no bundled face has CJK, which falls back as it did in 0.1.5.
        Assert.All(FamilyFiles, f => Assert.False(TrueTypeFace.Load(FontPath(f.FilePrefix, "Regular")).Maps(0x4E2D)));
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
    [InlineData(0x2302, "house (Mono)")]
    [InlineData(0x2500, "box drawings light horizontal (Mono)")]
    [InlineData(0x2502, "box drawings light vertical (Mono)")]
    [InlineData(0x2550, "box drawings double horizontal (Mono)")]
    [InlineData(0x2554, "box drawings double down and right (Mono)")]
    [InlineData(0x256C, "box drawings double vertical and horizontal (Mono)")]
    [InlineData(0x2580, "upper half block (Mono)")]
    [InlineData(0x2588, "full block (Mono)")]
    [InlineData(0x2591, "light shade (Mono)")]
    [InlineData(0x2593, "dark shade (Mono)")]
    [InlineData(0x0301, "combining acute accent")]
    [InlineData(0x1F00, "Greek small alpha with psili, polytonic (Mono)")]
    [InlineData(0x0259, "IPA schwa (Mono)")]
    [InlineData(0x1D00, "phonetic small capital A (Mono)")]
    [InlineData(0xFB2A, "Hebrew shin with shin dot (Mono)")]
    [InlineData(0x2C67, "Latin Extended-C")]
    [InlineData(0xF6C3, "private-use alternate (Sans, Serif)")]
    public void GlyphRanges_KeepWhat015Rendered(int codepoint, string what)
    {
        // Every one of these is mapped by at least one bundled face and was rasterized by 0.1.5,
        // which built the faces with their whole cmap; the ranges must not lose them.
        Assert.True(FontTierPolicy.CoversCodepoint(codepoint), $"U+{codepoint:X4} ({what}) is outside the glyph ranges");
        Assert.Contains(FamilyFiles, f => TrueTypeFace.Load(FontPath(f.FilePrefix, "Regular")).Maps(codepoint));
    }

    [Fact]
    public void EveryGlyphABundledFaceMaps_IsInTheRanges_As015BuiltThem()
    {
        // 0.1.5 built the bundled faces with no ranges, which Dalamud turns into [1, 0xFFFE]:
        // every glyph the TTF maps. A glyph left out renders as the fallback in a Plate that
        // showed it before, so none is: combining marks, polytonic Greek, IPA, box drawing and
        // the rest all stay. Only U+0000 is outside, as it was.
        foreach (var (_, prefix) in FamilyFiles)
        {
            foreach (var suffix in FaceSuffixes)
            {
                var face = TrueTypeFace.Load(FontPath(prefix, suffix));
                var missing = face.MappedCodepoints.Where(c => c != 0 && !FontTierPolicy.CoversCodepoint(c)).Order().ToList();
                Assert.True(missing.Count == 0, $"{prefix}-{suffix} maps {missing.Count} glyph(s) outside the ranges, e.g. {string.Join(" ", missing.Take(8).Select(c => $"U+{c:X4}"))}");
            }
        }
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
    public void NoBundledFace_CarriesTheDingbatDecoration()
    {
        // IdentityHeaderRules.IsDrawableDecoration rejects "✦" on its own because the fonts lack it
        // (the symbol fallback draws it, issue #121): the ranges cover every codepoint, but a glyph
        // no face maps still falls back.
        foreach (var (_, prefix) in FamilyFiles)
        {
            foreach (var suffix in FaceSuffixes)
            {
                Assert.False(TrueTypeFace.Load(FontPath(prefix, suffix)).Maps(0x2726));
            }
        }
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

    private const int AtlasTextureSide = 4096;

    /// <summary>
    /// The rows stb_rect_pack needs to pack <paramref name="rects"/> into a texture
    /// <paramref name="width"/> wide, with the heuristic ImGui's builder uses (the default,
    /// skyline bottom-left with the rects sorted by height, then width, both descending): each
    /// rect goes where it sits lowest, the leftmost such place on a tie.
    /// </summary>
    internal static int PackedHeight(IReadOnlyList<(int Width, int Height)> rects, int width)
    {
        // The skyline: (x, y) steps, each running to the next one's x (the last to the width).
        var skyline = new List<(int X, int Y)> { (0, 0) };
        var rows = 0;
        foreach (var (w, h) in rects.OrderByDescending(r => r.Height).ThenByDescending(r => r.Width))
        {
            var bestX = -1;
            var bestY = int.MaxValue;
            for (var i = 0; i < skyline.Count && skyline[i].X + w <= width; i++)
            {
                var x = skyline[i].X;
                var y = 0;
                for (var j = i; j < skyline.Count && skyline[j].X < x + w; j++)
                {
                    y = Math.Max(y, skyline[j].Y);
                }

                if (y < bestY)
                {
                    bestX = x;
                    bestY = y;
                }
            }

            Assert.True(bestX >= 0, $"a {w} px wide glyph box is wider than the texture");
            var top = bestY + h;
            var right = bestX + w;
            rows = Math.Max(rows, top);

            var yAtRight = skyline.Last(step => step.X <= right).Y;
            var updated = skyline.Where(step => step.X < bestX).ToList();
            updated.Add((bestX, top));
            if (right < width)
            {
                updated.Add((right, yAtRight));
            }

            updated.AddRange(skyline.Where(step => step.X > right));
            skyline = updated.Where((step, index) => index == 0 || step.Y != updated[index - 1].Y).ToList();
        }

        return rows;
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
    internal sealed class TrueTypeFace
    {
        private readonly int ascent;
        private readonly int ascentMinusDescent;
        private readonly (short X0, short Y0, short X1, short Y1)?[] boxes;
        private readonly int[] advances;
        private readonly Dictionary<int, int> codepointToGlyph;

        private TrueTypeFace(int ascent, int ascentMinusDescent, (short, short, short, short)?[] boxes, int[] advances, Dictionary<int, int> map)
        {
            this.ascent = ascent;
            this.ascentMinusDescent = ascentMinusDescent;
            this.boxes = boxes;
            this.advances = advances;
            codepointToGlyph = map;
        }

        /// <summary>
        /// The box ImGui draws the face in at <paramref name="sizePx"/>: its ascent above the baseline
        /// and its descent below it (positive), in whole pixels as the builder in Dalamud's ImGui (1.88)
        /// rounds them, a pixel outward: floor(ascent + 1) and floor(descent - 1), the descent being
        /// negative there. Its ImFont.Ascent and ImFont.Descent hold them.
        /// </summary>
        internal (float Ascent, float Descent) LineBox(float sizePx)
        {
            var scale = sizePx / ascentMinusDescent;
            return ((float)Math.Floor((ascent * scale) + 1), (float)-Math.Floor(((ascent - ascentMinusDescent) * scale) - 1));
        }

        /// <summary>How far <paramref name="text"/>'s glyphs reach above and below the baseline at
        /// <paramref name="sizePx"/>, in whole pixels as ImGui rasterizes them.</summary>
        internal (float Above, float Below) Ink(string text, float sizePx)
        {
            var scale = sizePx / ascentMinusDescent;
            float above = 0f, below = 0f;
            foreach (var c in text)
            {
                if (codepointToGlyph.TryGetValue(c, out var glyph) && boxes[glyph] is { } b)
                {
                    above = Math.Max(above, (float)-Math.Floor(-b.Y1 * scale));
                    below = Math.Max(below, (float)Math.Ceiling(-b.Y0 * scale));
                }
            }

            return (above, below);
        }

        /// <summary><paramref name="text"/>'s width at <paramref name="sizePx"/>, as ImGui draws it with
        /// Dalamud's SafeFontConfig: each glyph's advance rounded to a whole pixel (PixelSnapH), kerning
        /// aside.</summary>
        internal float Width(string text, float sizePx)
        {
            var scale = sizePx / ascentMinusDescent;
            return text.Sum(c => codepointToGlyph.TryGetValue(c, out var glyph) ? (float)Math.Floor((advances[glyph] * scale) + 0.5) : 0f);
        }

        /// <summary>
        /// The middle of the capitals, from the top of the box ImGui draws the face in (ascent to
        /// descent, the font size tall), as a fraction of its height; null when the face has none of
        /// E, H, I, L and T. The capitals reach the median top of those flat-topped letters (the
        /// lower middle one of an even count), as tools/fonts/build_font_catalog.py measures them:
        /// one letter alone can carry a swash or an ascender.
        /// </summary>
        internal double? CapCentre
        {
            get
            {
                var tops = "EHILT"
                    .Where(c => codepointToGlyph.TryGetValue(c, out var glyph) && boxes[glyph] is not null)
                    .Select(c => (int)boxes[codepointToGlyph[c]]!.Value.Y1)
                    .Order()
                    .ToList();
                return tops.Count == 0 ? null : (ascent - (tops[(tops.Count - 1) / 2] / 2.0)) / ascentMinusDescent;
            }
        }

        internal bool Maps(int codepoint) => codepointToGlyph.ContainsKey(codepoint);

        internal IEnumerable<int> MappedCodepoints => codepointToGlyph.Keys;

        internal int CountMapped(Func<int, bool> include) => codepointToGlyph.Keys.Count(include);

        /// <summary>The glyph boxes <see cref="Surface"/> adds up, as ImGui packs them.</summary>
        internal List<(int Width, int Height)> Rects(float sizePx, Func<int, bool> include)
        {
            var scale = sizePx / ascentMinusDescent;
            var rects = new List<(int Width, int Height)>();
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

                rects.Add((x1 - x0 + 1, y1 - y0 + 1));
            }

            return rects;
        }

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
            var longMetrics = U16(b, hhea + 34);
            var advances = new int[numGlyphs];
            for (var g = 0; g < numGlyphs; g++)
            {
                advances[g] = U16(b, tables["hmtx"] + (4 * Math.Min(g, longMetrics - 1)));
            }

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

            var map = new Dictionary<int, int>();
            if (U16(b, sub) == 12)
            {
                // Some library faces carry a format 12 table (whole Unicode); within U+0001 to
                // U+FFFE, which is all the ranges reach, it maps what a format 4 table would.
                var groups = U32(b, sub + 12);
                for (var group = 0; group < groups; group++)
                {
                    var at = sub + 16 + (12 * group);
                    var first = U32(b, at);
                    var last = U32(b, at + 4);
                    var glyph0 = U32(b, at + 8);
                    for (var c = first; c <= last && c <= 0xFFFE; c++)
                    {
                        var glyph = (int)(glyph0 + (c - first));
                        if (glyph != 0 && glyph < numGlyphs)
                        {
                            map[(int)c] = glyph;
                        }
                    }
                }

                return new TrueTypeFace(ascent, ascent - descent, boxes, advances, map);
            }

            Assert.Equal(4, U16(b, sub));
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

            return new TrueTypeFace(ascent, ascent - descent, boxes, advances, map);
        }

        private static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];

        private static uint U32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
    }
}
