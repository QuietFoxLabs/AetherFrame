using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services.Fonts;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The font library (the owner's request of October 1, 2026): every family's id, files and licence,
/// and its surface model checked against its own fonts, as FontTierPolicyTests checks AetherFrame's
/// own families: the estimate is never below a face's real glyph surface at any size, a family's
/// largest tier packs into one atlas texture, and AetherFrame Sans's merged fallback glyphs count.
/// </summary>
public partial class FontLibraryTests
{
    private static readonly string LibraryDirectory = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Fonts", "Library");

    [Fact]
    public void EveryId_IsALayoutIdent_UniqueAndApartFromAetherFramesOwn()
    {
        // A family's id is what a Plate saves and a shared layout carries (the specification's
        // section 8.5: 1 to 96 of a-z, 0-9, "." and "-", starting with a letter).
        var ids = FontLibrary.Families.Select(family => family.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches(IdentPattern(), id));
        Assert.All(ids, id => Assert.StartsWith("gf-", id, StringComparison.Ordinal));
        Assert.DoesNotContain(ProfileFontFamilies.AetherFrameSans, ids);
        Assert.DoesNotContain(ProfileFontFamilies.DalamudDefault, ids);
        Assert.Equal(FontLibrary.Families.Length, FontLibrary.Families.Select(family => family.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(FontLibrary.Families.Length >= 100);
    }

    [Fact]
    public void EveryFamily_HasExactlyItsFaces_OnDisk_AndALicence()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in FontLibrary.Families)
        {
            foreach (var style in Styles(family))
            {
                expected.Add($"{family.FilePrefix}-{style}.ttf");
            }
        }

        var onDisk = Directory.GetFiles(LibraryDirectory, "*.ttf").Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.Order(), onDisk.Order()!);

        var notices = File.ReadAllText(Path.Combine(LibraryDirectory, "THIRD-PARTY-FONT-LICENSES.txt"));
        foreach (var family in FontLibrary.Families)
        {
            Assert.Contains(family.DisplayName + " (", notices, StringComparison.Ordinal);
        }

        Assert.Contains("SIL OPEN FONT LICENSE", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManifest_RecordsEveryFile_WithItsChecksum()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepositoryPaths.Root().FullName, "tools", "fonts", "library.json")));
        var families = manifest.RootElement.GetProperty("families").EnumerateArray().ToList();
        Assert.Equal(FontLibrary.Families.Length, families.Count);
        foreach (var family in families)
        {
            var prefix = family.GetProperty("prefix").GetString()!;
            Assert.Contains(family.GetProperty("license").GetString(), new[] { "ofl", "apache" });
            foreach (var face in family.GetProperty("faces").EnumerateArray())
            {
                var bytes = File.ReadAllBytes(Path.Combine(LibraryDirectory, $"{prefix}-{face.GetProperty("style").GetString()}.ttf"));
                Assert.Equal(face.GetProperty("sha256").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
                Assert.StartsWith("https://fonts.gstatic.com/", face.GetProperty("source").GetString(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheCatalog_ListsAetherFramesOwnFirst_ThenTheLibrary_ThenDalamudDefault()
    {
        var ids = ProfileFontCatalog.All.Select(family => family.Id).ToList();
        Assert.Equal([ProfileFontFamilies.AetherFrameSans, ProfileFontFamilies.AetherFrameSerif, ProfileFontFamilies.AetherFrameMono], ids.Take(3));
        Assert.Equal(ProfileFontFamilies.DalamudDefault, ids[^1]);
        Assert.Equal(FontLibrary.Families.Select(family => family.Id), ids.Skip(3).Take(FontLibrary.Families.Length));

        foreach (var family in FontLibrary.Families)
        {
            var descriptor = ProfileFontCatalog.Resolve(family.Id);
            Assert.Equal((family.Id, family.DisplayName, family.HasBold, family.HasItalic, family.Category), (descriptor.Id, descriptor.DisplayName, descriptor.SupportsBold, descriptor.SupportsItalic, descriptor.Category));
            Assert.Equal(family.Id, FontTierPolicy.ResolveFamilyId(family.Id));
            Assert.True(FontTierPolicy.UsesFallback(family.Id));
        }

        Assert.False(FontTierPolicy.UsesFallback(ProfileFontFamilies.AetherFrameSans));
        Assert.False(FontTierPolicy.UsesFallback("gf-not-a-font"));
        Assert.Same(ProfileFontCatalog.DalamudDefault, ProfileFontCatalog.Resolve("GF-CINZEL"));
    }

    [Fact]
    public void AStyleAFamilyLacks_IsDrawnWithItsNearestFace()
    {
        var cinzel = FontLibrary.Find("gf-cinzel")!;
        Assert.Equal(("Bold", "Regular", "Bold"), (FontLibrary.FaceStyle(cinzel, true, false), FontLibrary.FaceStyle(cinzel, false, true), FontLibrary.FaceStyle(cinzel, true, true)));
        var lora = FontLibrary.Find("gf-lora")!;
        Assert.Equal(("BoldItalic", "Italic", "Regular"), (FontLibrary.FaceStyle(lora, true, true), FontLibrary.FaceStyle(lora, false, true), FontLibrary.FaceStyle(lora, false, false)));
        Assert.Equal("AetherFrame.Fonts.Library.Lora-BoldItalic.ttf", FontLibrary.ResourceName(lora, "BoldItalic"));
    }

    [Fact]
    public void TheFallbackRanges_AreInImGuiFormat_AndHoldTheBasicLatinLetters()
    {
        var ranges = FontTierPolicy.FallbackGlyphRanges;
        Assert.True(ranges.Length % 2 == 1);
        Assert.Equal(0, ranges[^1]);
        for (var i = 0; i + 1 < ranges.Length - 1; i += 2)
        {
            Assert.True(ranges[i] <= ranges[i + 1]);
            if (i + 2 < ranges.Length - 1)
            {
                Assert.True(ranges[i + 1] < ranges[i + 2]);
            }
        }

        Assert.True(InFallback('A') && InFallback(0x00E9) && InFallback(0x00B7) && InFallback(0x2026));
    }

    [Fact]
    public void EveryFamilysModel_BoundsEveryFaceAtEverySize_FallbackIncluded()
    {
        // As FontTierPolicyTests for AetherFrame's own: never below a face's real surface (its own
        // glyphs and the Sans glyphs merged in for the ones it lacks), so the budgets hold.
        foreach (var family in FontLibrary.Families)
        {
            foreach (var style in Styles(family))
            {
                var (face, fallback, added) = Load(family, style);
                Assert.True(face.CountMapped(FontTierPolicy.CoversCodepoint) + added.Count <= family.Glyphs, $"{family.DisplayName} {style} rasterizes more glyphs than its model");
                foreach (var size in FontTierPolicy.SizeLadder)
                {
                    var actual = Surface(face, fallback, added, size);
                    var estimate = FontTierPolicy.EstimatedSurfacePixels(family.Id, size);
                    Assert.True(estimate >= actual, $"{family.DisplayName} {style} at {size} px: real {actual / 1e6:F2} Mpx > estimate {estimate / 1e6:F2} Mpx");
                }
            }
        }
    }

    [Fact]
    public void EveryFamilysLargestTier_FitsOneAtlasTexture_AndNoCapIsBelowTheEditorsLargestSize()
    {
        foreach (var family in FontLibrary.Families)
        {
            var cap = FontTierPolicy.MaxTierSize(family.Id);
            Assert.True(cap >= TextProfileElement.MaxFontSize, $"{family.DisplayName} caps at {cap} px");
            Assert.True(FontTierPolicy.EstimatedSurfacePixels(family.Id, cap) <= FontTierPolicy.SingleTierBudgetPixels);
            foreach (var style in Styles(family))
            {
                var (face, fallback, added) = Load(family, style);
                var rects = face.Rects(cap, FontTierPolicy.CoversCodepoint);
                rects.AddRange(fallback.Rects(cap, added.Contains));
                var rows = FontTierPolicyTests.PackedHeight(rects, 4096);
                Assert.True(rows <= 4096 * 85 / 100, $"{family.DisplayName} {style} at its {cap} px cap needs {rows} rows");
            }
        }
    }

    private static IEnumerable<string> Styles(LibraryFontFamily family)
    {
        yield return "Regular";
        if (family.HasBold)
        {
            yield return "Bold";
        }

        if (family.HasItalic)
        {
            yield return "Italic";
        }

        if (family.HasBoldItalic)
        {
            yield return "BoldItalic";
        }
    }

    private static (FontTierPolicyTests.TrueTypeFace Face, FontTierPolicyTests.TrueTypeFace Fallback, HashSet<int> Added) Load(LibraryFontFamily family, string style)
    {
        var face = FontTierPolicyTests.TrueTypeFace.Load(Path.Combine(LibraryDirectory, $"{family.FilePrefix}-{style}.ttf"));
        var fallback = FontTierPolicyTests.TrueTypeFace.Load(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Fonts", $"PTSans-{style}.ttf"));
        var added = fallback.MappedCodepoints.Where(c => InFallback(c) && !face.Maps(c)).ToHashSet();
        return (face, fallback, added);
    }

    private static long Surface(FontTierPolicyTests.TrueTypeFace face, FontTierPolicyTests.TrueTypeFace fallback, HashSet<int> added, float size) =>
        face.Surface(size, FontTierPolicy.CoversCodepoint) + fallback.Surface(size, added.Contains);

    private static bool InFallback(int codepoint)
    {
        var ranges = FontTierPolicy.FallbackGlyphRanges;
        for (var i = 0; ranges[i] != 0; i += 2)
        {
            if (codepoint >= ranges[i] && codepoint <= ranges[i + 1])
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("^[a-z][a-z0-9.-]{0,95}$")]
    private static partial Regex IdentPattern();
}
