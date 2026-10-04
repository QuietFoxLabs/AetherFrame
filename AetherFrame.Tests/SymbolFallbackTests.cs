using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services.Packages;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Issue #121: symbols such as ♥ drawn as "?". The text always holds the character (it survives
/// editing, saving, export and sharing); the fonts are what lack it. Most of AetherFrame's fonts have
/// no ♥, and <see cref="SymbolFallback"/> draws it from the bundled symbol faces instead: only the
/// symbols used, bounded so every family's largest tier still fits one atlas texture, sized to the
/// text's em, and never in place of a glyph the font has.
/// </summary>
public class SymbolFallbackTests
{
    private const int Heart = 0x2665;

    /// <summary>A Plate's text with symbols the fallback draws, and characters it doesn't: ✨, an emoji, CJK.</summary>
    private const string SymbolText = "I ♥ Eorzea ★ ✿ ♪ ☾ ✨ \U0001F600 漢字 café";

    /// <summary>A representative set of decorative symbols players type: hearts, suits, stars, flowers, notes, moons, weather, checks, shapes, arrows and more.</summary>
    private const string DecorativeSymbols = "♥♡♦♢♣♧♠♤★☆✦✧✿❀❤❣❥♪♫♩♬☺☻☾☽☀☁☂☃❄☘✓✔✗✘◆◇○●◎□■▲▼♀♂⚔⚜⚘⚝☯☮✝☠⚡✉✈♛♔➤→↔⇨";

    private static readonly string Root = RepositoryPaths.Root().FullName;
    private static readonly string SymbolsDirectory = Path.Combine(Root, "AetherFrame", "Fonts", "Symbols");

    private static readonly Lazy<(FontTierPolicyTests.TrueTypeFace Face, FontVerticalMetrics Metrics)[]> Faces = new(() =>
        SymbolFallback.FaceResources.Select(resource =>
        {
            var bytes = File.ReadAllBytes(PathOf(resource));
            Assert.True(TrueTypeTables.TryReadVerticalMetrics(bytes, out var metrics));
            return (FontTierPolicyTests.TrueTypeFace.Load(PathOf(resource)), metrics);
        }).ToArray());

    private static readonly (string FamilyId, string FilePrefix)[] OwnFamilies =
    [
        (ProfileFontFamilies.AetherFrameSans, "PTSans"),
        (ProfileFontFamilies.AetherFrameSerif, "PTSerif"),
        (ProfileFontFamilies.AetherFrameMono, "Cousine"),
    ];

    private static readonly string[] Styles = ["Regular", "Bold", "Italic", "BoldItalic"];

    // ---- the cause ------------------------------------------------------------------------------

    [Fact]
    public void TheHeart_IsMissingFromMostFonts_NotFromTheText()
    {
        // Reproduced in the fonts themselves: AetherFrame Sans and Serif have no ♥, Mono (Cousine)
        // has one, and so do only a few library families.
        foreach (var style in Styles)
        {
            Assert.False(Own("PTSans", style).Maps(Heart));
            Assert.False(Own("PTSerif", style).Maps(Heart));
            Assert.True(Own("Cousine", style).Maps(Heart));
        }

        var withHeart = FontLibrary.Families.Count(family => FontLibraryTests.Load(family, "Regular").Face.Maps(Heart));
        Assert.InRange(withHeart, 1, FontLibrary.Families.Length / 4);

        // The text itself holds the character: one UTF-16 unit, U+2665.
        var element = new TextProfileElement { Text = "I ♥ you" };
        Assert.Equal(Heart, element.GetDisplayText()[2]);
    }

    [Fact]
    public void TheHeartAndADecorativeSet_AreDrawnByTheSymbolFaces()
    {
        var fallback = FromDisk();
        foreach (var symbol in DecorativeSymbols)
        {
            Assert.True(SymbolFallback.InBlocks(symbol), $"U+{(int)symbol:X4} is outside the fallback's blocks");
            Assert.True(fallback.CanDraw(symbol), $"no symbol face draws U+{(int)symbol:X4}");
        }
    }

    [Fact]
    public void GenuinelyUnsupportedCharacters_AreNotTakenAndStayAsTheyAre()
    {
        // Documented in CHANGELOG.md: ✨ has only an emoji form (no symbol face draws it); characters
        // beyond U+FFFF (most emoji) are outside what ImGui's glyph ranges can hold; CJK and other
        // scripts are the font's business (Dalamud Default draws Japanese). Letters, digits and
        // punctuation are never the fallback's, though the faces carry some.
        var fallback = FromDisk();
        Assert.False(fallback.CanDraw(0x2728));
        Assert.False(SymbolFallback.InBlocks(0x1F600));
        Assert.False(fallback.Take("\U0001F600"));
        Assert.False(fallback.Take("漢字 abc 123 ,.;"));
        Assert.Equal(0, fallback.Count);
    }

    // ---- what is taken --------------------------------------------------------------------------

    [Fact]
    public void APlateWithoutSymbols_TakesNothing_AndNeverReadsTheSymbolFaces()
    {
        var loads = 0;
        var fallback = new SymbolFallback(resource =>
        {
            loads++;
            return File.ReadAllBytes(PathOf(resource));
        });

        Assert.False(fallback.Take("Visible Hero « Phoenix » — Lv. 100 · café “quoted”"));
        Assert.Equal(0, fallback.Count);
        Assert.Empty(fallback.Merges);
        Assert.Equal(0, loads);

        Assert.True(fallback.Take("♥"));
        Assert.Equal(SymbolFallback.FaceResources.Length, loads); // each face read once
        Assert.True(fallback.Take("★"));
        Assert.Equal(SymbolFallback.FaceResources.Length, loads);
    }

    [Fact]
    public void Taking_IsGrowOnly_OnceEach_AndStopsAtTheLimit()
    {
        var fallback = FromDisk();
        Assert.True(fallback.Take(Heart));
        Assert.False(fallback.Take(Heart));
        Assert.False(fallback.Take("♥♥"));
        Assert.True(fallback.Take("♥★♥"));
        Assert.Equal(2, fallback.Count);
        Assert.True(fallback.Holds(Heart) && fallback.Holds(0x2605));
        Assert.False(fallback.Take('A'));

        var drawable = Enumerable.Range(0x2190, 0x2C00 - 0x2190).Where(fallback.CanDraw).Where(c => !fallback.Holds(c)).ToList();
        Assert.True(drawable.Count > SymbolFallback.MaxSymbols * 10);
        Assert.True(fallback.Take(new string(drawable.Take(SymbolFallback.MaxSymbols + 10).Select(c => (char)c).ToArray())));
        Assert.Equal(SymbolFallback.MaxSymbols, fallback.Count);

        // Full: one more stays "?", and /af fonts says why.
        var past = drawable.Last();
        Assert.False(fallback.Take(past));
        Assert.False(fallback.Holds(past));
        Assert.Contains("full", fallback.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Merges_GiveEachFaceOnlyWhatItDrawsFirst_AsImGuiGlyphRanges()
    {
        var fallback = FromDisk();
        Assert.True(fallback.Take("♥♣♠♢♡♪")); // ♠♡♢♣ (U+2660 to U+2663) and ♥ in Symbols 2, ♪ only in Symbols

        var merges = fallback.Merges;
        Assert.Equal(2, merges.Count);
        Assert.Equal(SymbolFallback.FaceResources[0], merges[0].Resource);
        Assert.Equal(new ushort[] { 0x2660, 0x2663, 0x2665, 0x2665, 0 }, merges[0].GlyphRanges);
        Assert.Equal(SymbolFallback.FaceResources[1], merges[1].Resource);
        Assert.Equal(new ushort[] { 0x266A, 0x266A, 0 }, merges[1].GlyphRanges);

        // A snapshot: taking more replaces it whole, never changes what a build already read.
        var before = fallback.Merges;
        Assert.True(fallback.Take('★'));
        Assert.Equal(new ushort[] { 0x2660, 0x2663, 0x2665, 0x2665, 0 }, before[0].GlyphRanges);
        Assert.Equal(new ushort[] { 0x2605, 0x2605, 0x2660, 0x2663, 0x2665, 0x2665, 0 }, fallback.Merges[0].GlyphRanges);
    }

    [Fact]
    public void GlyphRanges_JoinRuns_AndAreZeroTerminated()
    {
        Assert.Equal(new ushort[] { 0 }, SymbolFallback.ToGlyphRanges([]));
        Assert.Equal(new ushort[] { 0x2190, 0x2192, 0x2195, 0x2195, 0x2BFF, 0x2BFF, 0 }, SymbolFallback.ToGlyphRanges([0x2190, 0x2191, 0x2192, 0x2195, 0x2BFF]));
    }

    // ---- how they are drawn ---------------------------------------------------------------------

    [Fact]
    public void ASymbol_IsBuiltAtTheTextsEm_SoTheHeartStandsAsTallAsTheCapitals()
    {
        var sans = OwnMetrics("PTSans", "Regular");
        Assert.Equal(new FontVerticalMetrics(1000, 1018, -276), sans);
        var symbols = Faces.Value[0].Metrics;
        Assert.Equal(new FontVerticalMetrics(1000, 1069, -630), symbols);

        foreach (var size in FontTierPolicy.SizeLadder)
        {
            var em = sans.EmPixels(size);
            var symbolSize = symbols.SizeForEm(em);
            Assert.Equal(em, symbols.EmPixels(symbolSize), 3);
            Assert.True(symbolSize > size); // the symbol faces' line box is taller for their em
        }

        // ♥ is 719 units tall from the baseline, PT Sans's H 700: at a shared em they stand alike.
        var heart = Faces.Value[0].Face.Rects(symbols.SizeForEm(sans.EmPixels(240f)), c => c == Heart).Single();
        var capital = Own("PTSans", "Regular").Rects(240f, c => c == 'H').Single();
        Assert.InRange(heart.Height / (double)capital.Height, 0.95, 1.1);
    }

    [Fact]
    public void EveryFamilysLargestTier_WithTheMostSymbolsTheFallbackTakes_StillFitsOneAtlasTexture()
    {
        // The bound behind MaxSymbols: the heaviest MaxSymbols symbols, at the em of every face of
        // every family at its largest tier, added to that face's own glyphs (and, for a library
        // family, the AetherFrame Sans glyphs merged in), pack into one 4096x4096 texture as ImGui
        // packs them. Dalamud Default's real face is unknowable here (see FontTierPolicy).
        const int Texture = 4096;
        foreach (var (familyId, prefix) in OwnFamilies)
        {
            var cap = FontTierPolicy.MaxTierSize(familyId);
            foreach (var style in Styles)
            {
                var rects = Own(prefix, style).Rects(cap, FontTierPolicy.CoversCodepoint);
                rects.AddRange(HeaviestSymbols(OwnMetrics(prefix, style), cap));
                var rows = FontTierPolicyTests.PackedHeight(rects, Texture);
                Assert.True(rows <= Texture, $"{prefix}-{style} at its {cap} px cap with {SymbolFallback.MaxSymbols} symbols needs {rows} rows");
            }
        }

        foreach (var family in FontLibrary.Families)
        {
            var cap = FontTierPolicy.MaxTierSize(family.Id);
            foreach (var style in FontLibraryTests.Styles(family))
            {
                var (face, fallback, added) = FontLibraryTests.Load(family, style);
                var rects = face.Rects(cap, FontTierPolicy.CoversCodepoint);
                rects.AddRange(fallback.Rects(cap, added.Contains));
                Assert.True(TrueTypeTables.TryReadVerticalMetrics(File.ReadAllBytes(Path.Combine(Root, "AetherFrame", "Fonts", "Library", $"{family.FilePrefix}-{style}.ttf")), out var metrics));
                rects.AddRange(HeaviestSymbols(metrics, cap));
                var rows = FontTierPolicyTests.PackedHeight(rects, Texture);
                Assert.True(rows <= Texture, $"{family.DisplayName} {style} at its {cap} px cap with {SymbolFallback.MaxSymbols} symbols needs {rows} rows");
            }
        }
    }

    // ---- the faces ------------------------------------------------------------------------------

    [Fact]
    public void TheSymbolFaces_AreTheFilesTheManifestRecords_CutToTheBlocks()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "tools", "fonts", "symbols.json")))!;
        var blocks = manifest["blocks"]!.AsArray().Select(b => (Convert.ToInt32((string)b![0]!, 16), Convert.ToInt32((string)b[1]!, 16))).ToArray();
        Assert.Equal(SymbolFallback.Blocks, blocks);

        var faces = manifest["faces"]!.AsArray();
        Assert.Equal(SymbolFallback.FaceResources, faces.Select(f => "AetherFrame.Fonts.Symbols." + (string)f!["file"]!).ToArray());
        Assert.Equal(faces.Select(f => (string)f!["file"]!).Order(), Directory.GetFiles(SymbolsDirectory, "*.ttf").Select(Path.GetFileName).Order());
        foreach (var face in faces)
        {
            var bytes = File.ReadAllBytes(Path.Combine(SymbolsDirectory, (string)face!["file"]!));
            Assert.Equal((string)face["sha256"]!, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.Equal((long)face["bytes"]!, bytes.LongLength);
            Assert.Equal("ofl", (string)face["license"]!);
            Assert.Contains("The Noto Project Authors", (string)face["copyright"]!, StringComparison.Ordinal);
            Assert.All(TrueTypeTables.MappedCodepoints(bytes, 1, 0xFFFE), c => Assert.True(SymbolFallback.InBlocks(c), $"U+{c:X4} is outside the blocks"));
        }

        // Licensed beside the faces they ship with, and never taken for a text family.
        var notice = File.ReadAllText(Path.Combine(Root, "AetherFrame", "Fonts", "THIRD-PARTY-FONT-LICENSES.txt"));
        Assert.Contains("Noto Sans Symbols", notice, StringComparison.Ordinal);
        Assert.Contains("Copyright 2022 The Noto Project Authors", notice, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(Root, "AetherFrame", "Fonts"), "Noto*"));
    }

    [Fact]
    public void ThePlugin_CarriesTheSymbolFaces_UnderTheNamesItReads()
    {
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(path));
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var names = metadata.ManifestResources.Select(handle => metadata.GetString(metadata.GetManifestResource(handle).Name)).ToHashSet(StringComparer.Ordinal);
        Assert.All(SymbolFallback.FaceResources, resource => Assert.Contains(resource, names));
    }

    // ---- reading a font -------------------------------------------------------------------------

    [Fact]
    public void TrueTypeTables_MapWhatTheReferenceReaderMaps_InEveryBundledFace()
    {
        var paths = OwnFamilies.SelectMany(f => Styles.Select(s => Path.Combine(Root, "AetherFrame", "Fonts", $"{f.FilePrefix}-{s}.ttf")))
            .Concat(Directory.GetFiles(SymbolsDirectory, "*.ttf"))
            .Concat(Directory.GetFiles(Path.Combine(Root, "AetherFrame", "Fonts", "Library"), "*.ttf"));
        foreach (var path in paths)
        {
            var bytes = File.ReadAllBytes(path);
            var expected = FontTierPolicyTests.TrueTypeFace.Load(path).MappedCodepoints.Where(c => c is >= 1 and <= 0xFFFE).Order().ToArray();
            Assert.Equal(expected, TrueTypeTables.MappedCodepoints(bytes, 1, 0xFFFE));
            Assert.True(TrueTypeTables.TryReadVerticalMetrics(bytes, out var metrics), path);
            Assert.True(metrics.UnitsPerEm > 0 && metrics.LineBox > 0, path);
        }

        Assert.Equal(new FontVerticalMetrics(2048, 1705, -615), OwnMetrics("Cousine", "Regular"));
    }

    [Fact]
    public void TrueTypeTables_ReadNothingFromWhatIsntAFont()
    {
        var heart = File.ReadAllBytes(PathOf(SymbolFallback.FaceResources[0]));
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[12], Encoding.ASCII.GetBytes("not a font at all"), heart[..64], heart[..(heart.Length / 3)] })
        {
            Assert.False(TrueTypeTables.TryReadVerticalMetrics(bytes, out _) && TrueTypeTables.MappedCodepoints(bytes, 1, 0xFFFE).Length > 0);
        }

        Assert.False(TrueTypeTables.TryReadVerticalMetrics(new byte[64], out _));
        Assert.Empty(TrueTypeTables.MappedCodepoints(new byte[64], 1, 0xFFFE));
    }

    // ---- the text keeps its characters ----------------------------------------------------------

    [Fact]
    public async Task SymbolText_SurvivesSaving_Reopening_ExportAndImport_ExactlyAsWritten()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Symbols");
        var document = library.OpenDocumentForEditing(created.PlateId);
        document.Elements.Add(new TextProfileElement
        {
            Text = SymbolText,
            Prefix = "♡",
            Suffix = "✦",
            FontFamily = ProfileFontFamilies.AetherFrameSans,
            Position = new Vector2(20f, 20f),
            Size = new Vector2(600f, 80f),
        });
        await library.SavePlateDocumentAsync(document);

        // Saved as plain ASCII JSON, every character escaped rather than replaced.
        var saved = File.ReadAllBytes(fixture.Paths.GetPlatePath(created.PlateId));
        Assert.All(saved, b => Assert.True(b < 0x80));
        Assert.Contains("\\u2665", Encoding.ASCII.GetString(saved), StringComparison.OrdinalIgnoreCase);

        var (reopened, _) = await fixture.LoadAsync();
        AssertSymbolText(reopened.OpenDocumentForEditing(created.PlateId));

        using var staged = packages.Inspect(fixture.Export(packages, created.PlateId));
        var imported = await packages.ImportAsync(staged);
        Assert.True(imported.Succeeded, imported.Error?.ToString());
        AssertSymbolText(library.OpenDocumentForEditing(imported.PlateId));
    }

    private static void AssertSymbolText(ProfileDocument document)
    {
        var text = Assert.Single(document.Elements.OfType<TextProfileElement>(), t => t.Text == SymbolText);
        Assert.Equal(("♡", "✦"), (text.Prefix, text.Suffix));
        Assert.Equal("♡ " + SymbolText + " ✦", text.GetDisplayText());
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static SymbolFallback FromDisk() => new(resource => File.ReadAllBytes(PathOf(resource)));

    private static string PathOf(string resource) =>
        Path.Combine(SymbolsDirectory, resource["AetherFrame.Fonts.Symbols.".Length..]);

    private static FontTierPolicyTests.TrueTypeFace Own(string prefix, string style) =>
        FontTierPolicyTests.TrueTypeFace.Load(Path.Combine(Root, "AetherFrame", "Fonts", $"{prefix}-{style}.ttf"));

    private static FontVerticalMetrics OwnMetrics(string prefix, string style)
    {
        Assert.True(TrueTypeTables.TryReadVerticalMetrics(File.ReadAllBytes(Path.Combine(Root, "AetherFrame", "Fonts", $"{prefix}-{style}.ttf")), out var metrics));
        return metrics;
    }

    /// <summary>The <see cref="SymbolFallback.MaxSymbols"/> largest glyph boxes the fallback can add to a face of <paramref name="main"/>'s metrics at <paramref name="sizePx"/>, each from the face that draws it.</summary>
    private static IEnumerable<(int Width, int Height)> HeaviestSymbols(FontVerticalMetrics main, float sizePx)
    {
        var em = main.EmPixels(sizePx);
        var rects = new List<(int Width, int Height)>();
        var drawn = new HashSet<int>();
        foreach (var (face, metrics) in Faces.Value)
        {
            var mine = face.MappedCodepoints.Where(c => SymbolFallback.InBlocks(c) && !drawn.Contains(c)).ToHashSet();
            rects.AddRange(face.Rects(metrics.SizeForEm(em), mine.Contains));
            drawn.UnionWith(mine);
        }

        return rects.OrderByDescending(r => (long)r.Width * r.Height).Take(SymbolFallback.MaxSymbols);
    }
}
