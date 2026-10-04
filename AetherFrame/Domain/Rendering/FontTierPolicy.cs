using System;
using System.Collections.Generic;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Rendering;

/// <summary>
/// The rules behind the profile font cache (<c>ProfileFontService</c>), kept free of Dalamud so
/// they can be tested against the bundled TTFs: which pixel sizes a text element's font is
/// snapped to, how much atlas surface a (family, size) tier is expected to rasterize, the largest
/// tier each family may build, and the glyph set the bundled families are built with.
///
/// Every font handle the service builds lands in one shared ImGui atlas that is re-rasterized
/// whole on every rebuild, so the cost of a tier is the surface of its glyph bitmaps, not the
/// handle itself. Left unbounded, a Mono tier at the top of the ladder alone rasterizes over
/// 100 Mpx (seven 4096² textures), reachable from a package with a large FontSize or by zooming
/// a 96 px text to the editor's maximum. This policy bounds that by size, never by glyph:
///
/// <list type="bullet">
/// <item>The bundled families keep every glyph their TTFs map (<see cref="GlyphRanges"/>), as
/// 0.1.5 built them, so a Plate's text renders exactly as it did there: combining marks,
/// polytonic Greek, IPA, box drawing and all.</item>
/// <item>Each family has a largest tier (<see cref="MaxTierIndex"/>) chosen so that a single
/// tier's <see cref="EstimatedSurfacePixels"/> stays under <see cref="SingleTierBudgetPixels"/>,
/// which one atlas texture holds. A request above it uses the largest allowed tier, exactly as
/// a request above the ladder's top always has: the raster is upscaled a little at extreme
/// zoom, and nothing below the cap changes.</item>
/// <item>The service also evicts least-recently-used handles once their summed estimate
/// exceeds <see cref="AtlasBudgetPixels"/>, in addition to its handle count.</item>
/// </list>
///
/// The surface model is calibrated against the bundled TTFs with the same measure ImGui's
/// stb_truetype builder uses (glyph bounding box at scale size / (hhea.ascent - hhea.descent),
/// one pixel of padding per axis, no oversampling); see the calibration tests. Dalamud Default
/// (and any unknown family id, which resolves to it) is whatever the user's Dalamud language and
/// default-font settings make it — game bitmap glyphs, optional CJK merge fonts, or a custom TTF
/// rasterized at full size — so it cannot be calibrated here: it is charged a flat stand-in per
/// tier and capped at the editor's largest slider size, so every legacy element still gets its
/// own nominal tier and only zooming past 100% upscales.
/// </summary>
internal static class FontTierPolicy
{
    /// <summary>
    /// Fixed set of pixel sizes every cached font handle is snapped to. Deliberately fine-grained
    /// (~20-25% steps) so any actual request — prewarmed or lazily built on first demand — is
    /// close to its ideal size; includes AetherFrame's documented test sizes (24/48/72/120/180)
    /// exactly. Being in this list does NOT mean a tier is built ahead of time (see
    /// <see cref="CommonEditorSizes"/>), nor that every family may build it (see
    /// <see cref="MaxTierIndex"/>).
    /// </summary>
    internal static readonly IReadOnlyList<float> SizeLadder =
    [
        10f, 12f, 14f, 16f, 20f, 24f, 28f, 32f, 40f, 48f, 56f, 64f, 72f, 84f, 96f,
        110f, 120f, 140f, 160f, 180f, 210f, 240f, 280f, 330f, 390f, 460f,
    ];

    /// <summary>
    /// The small subset of <see cref="SizeLadder"/> eagerly warmed for a family/style combo
    /// ("a sensible set of common editor sizes") — everything else in the ladder is only ever
    /// built lazily, on first actual demand.
    /// </summary>
    internal static readonly IReadOnlyList<float> CommonEditorSizes = [16f, 24f, 32f, 48f, 64f, 96f];

    /// <summary>
    /// The most glyph surface one tier may be expected to rasterize, so that no tier ever needs a
    /// second texture of its own: a 4096×4096 atlas texture is 16.8 Mpx, and ImGui's packer
    /// (stb_rect_pack's skyline, which fills a tier's near-uniform glyph boxes almost solid) puts
    /// the heaviest tier this allows, any family's heaviest face at its cap, into at most about
    /// 3,400 of the texture's 4,096 rows. The packing test measures exactly that.
    /// </summary>
    internal const long SingleTierBudgetPixels = 14_000_000;

    /// <summary>
    /// The most estimated glyph surface the cache keeps alive at once: eighteen 4096×4096 atlas
    /// textures. Large on purpose — it must exceed what one Plate can legitimately need at the
    /// same time (every family/style combo at its common sizes plus one maximal tier each is
    /// about 270 Mpx), because evicting a handle that is still drawn every frame would rebuild
    /// the atlas continuously, which is far worse than a large atlas. It exists to stop a
    /// pathological session (many Plates, many families, every zoom level) from growing without
    /// bound, not to make the steady state small.
    /// </summary>
    // [updated 2026-10-01, the font library] The budget is sized for AetherFrame's own three
    // families and Dalamud Default at their common sizes and caps. A Plate naming many library
    // families is held to it by warming only each library text's own tier, and past it by the LRU
    // eviction: a Plate that needs more at once than the budget holds rebuilds as it draws, at the
    // cost of frames, never of memory.
    internal const long AtlasBudgetPixels = 18L * 4096 * 4096;

    /// <summary>
    /// The glyph ranges every bundled family is built with, in ImGui's format (inclusive pairs,
    /// zero-terminated): the whole Basic Multilingual Plane, which is exactly what Dalamud builds
    /// for a font given no ranges, and so exactly what 0.1.5 built. Whatever a face maps is
    /// rasterized — a Plate's combining marks, polytonic Greek, IPA, box drawing, block elements,
    /// ligatures and even the PT faces' private-use alternates (U+F401–F6D4, Adobe's legacy
    /// codepoints, clear of the game's icon codepoints) render as they did — and what no face
    /// maps (CJK, the Dingbats such as U+2726) falls back, as it did. The atlas is bounded by the
    /// tier caps, never by dropping glyphs. A symbol a Plate uses that no face maps (♥ in most of
    /// them) is merged in from the symbol faces (issue #121, <see cref="SymbolFallback"/>): at most
    /// <see cref="SymbolFallback.MaxSymbols"/> in a session, which every family's largest tier
    /// still holds in one atlas texture.
    /// </summary>
    private static readonly ushort[] BundledGlyphRanges = [0x0001, 0xFFFE, 0];

    /// <summary>
    /// Per-family surface model: <c>Glyphs × (Scale × size + Padding)²</c> pixels for a tier of
    /// <c>size</c> px, where Glyphs is the number of codepoints in <see cref="BundledGlyphRanges"/>
    /// the family's TTFs map (the most any of its faces does: PT Serif's bold faces carry six
    /// more than its Regular) and Scale the average glyph box side per pixel of size, fitted to the
    /// heaviest face (Bold Italic) so the estimate is never below the real surface of any face
    /// at any ladder size.
    /// </summary>
    private readonly record struct SurfaceModel(int Glyphs, double Scale);

    private const double PaddingPixels = 2d;

    private static readonly SurfaceModel SansModel = new(717, 0.472);
    private static readonly SurfaceModel SerifModel = new(723, 0.497);
    private static readonly SurfaceModel MonoModel = new(2281, 0.527);

    /// <summary>
    /// The flat charge for one Dalamud Default tier, whose real glyph set is unknowable here
    /// (see the type doc): a stand-in of about half a single-tier budget, so a legacy Plate's
    /// common sizes cost a few atlas textures in the estimate rather than nothing.
    /// </summary>
    private const long DalamudDefaultTierSurfacePixels = 8_000_000;

    private static readonly int SansMaxTierIndex = LargestTierWithin(SansModel);
    private static readonly int SerifMaxTierIndex = LargestTierWithin(SerifModel);
    private static readonly int MonoMaxTierIndex = LargestTierWithin(MonoModel);
    private static readonly int DalamudDefaultMaxTierIndex = LargestTierAtOrBelow(TextProfileElement.MaxFontSize);

    /// <summary>The family id the cache actually keys on: a known bundled id as is, anything
    /// else (null, a legacy element, an id from a newer build) as
    /// <see cref="ProfileFontFamilies.DalamudDefault"/> — the same rule the font catalog's
    /// descriptor lookup applies.</summary>
    internal static string ResolveFamilyId(string? familyId) => familyId switch
    {
        ProfileFontFamilies.AetherFrameSans => ProfileFontFamilies.AetherFrameSans,
        ProfileFontFamilies.AetherFrameSerif => ProfileFontFamilies.AetherFrameSerif,
        ProfileFontFamilies.AetherFrameMono => ProfileFontFamilies.AetherFrameMono,
        _ when FontLibrary.Find(familyId) is { } library => library.Id,
        _ => ProfileFontFamilies.DalamudDefault,
    };

    /// <summary>
    /// What a library family merges in from AetherFrame Sans (the same style), as inclusive
    /// pairs in ImGui's format: where the family has no glyph of its own in these ranges (an
    /// accented letter in a display face, say), Sans draws it instead of the fallback "?".
    /// ImGui skips every codepoint the family already maps. tools/fonts/build_font_catalog.py fits
    /// each library family's model with these same ranges.
    /// </summary>
    internal static readonly ushort[] FallbackGlyphRanges =
    [
        0x0020, 0x024F, 0x0370, 0x03FF, 0x0400, 0x04FF, 0x2000, 0x206F, 0x20A0, 0x20CF, 0x2100, 0x214F, 0x2190, 0x21FF, 0,
    ];

    /// <summary>Whether <paramref name="familyId"/> is a library family, built with AetherFrame Sans merged in (<see cref="FallbackGlyphRanges"/>).</summary>
    internal static bool UsesFallback(string? familyId) => FontLibrary.Find(familyId) is not null;

    /// <summary>The glyph ranges to build a family's faces with (ImGui format), or null for
    /// Dalamud Default, which keeps Dalamud's own default ranges. The array is shared and must
    /// not be modified.</summary>
    internal static ushort[]? GlyphRanges(string? familyId) =>
        ResolveFamilyId(familyId) == ProfileFontFamilies.DalamudDefault ? null : BundledGlyphRanges;

    /// <summary>True when <paramref name="codepoint"/> lies in the bundled families' glyph
    /// ranges (whether a given face actually maps it is the TTF's business).</summary>
    internal static bool CoversCodepoint(int codepoint)
    {
        for (var i = 0; BundledGlyphRanges[i] != 0; i += 2)
        {
            if (codepoint >= BundledGlyphRanges[i] && codepoint <= BundledGlyphRanges[i + 1])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The number of codepoints in <see cref="GlyphRanges"/> a bundled family's faces
    /// map (its heaviest face; the others map at most a few fewer), i.e. how many glyphs a tier
    /// of it rasterizes; 0 for Dalamud Default.</summary>
    internal static int GlyphCount(string? familyId) => ResolveFamilyId(familyId) switch
    {
        ProfileFontFamilies.AetherFrameSans => SansModel.Glyphs,
        ProfileFontFamilies.AetherFrameSerif => SerifModel.Glyphs,
        ProfileFontFamilies.AetherFrameMono => MonoModel.Glyphs,
        var id when FontLibrary.Find(id) is { } library => library.Glyphs,
        _ => 0,
    };

    /// <summary>
    /// The atlas surface, in pixels, a tier of <paramref name="familyId"/> at
    /// <paramref name="sizePx"/> is expected to rasterize (its heaviest face; the real surface
    /// of any face is at most this and never more than about a fifth less). Dalamud Default
    /// is charged a flat <see cref="DalamudDefaultTierSurfacePixels"/> whatever the size.
    /// </summary>
    internal static long EstimatedSurfacePixels(string? familyId, float sizePx)
    {
        var model = ResolveFamilyId(familyId) switch
        {
            ProfileFontFamilies.AetherFrameSans => SansModel,
            ProfileFontFamilies.AetherFrameSerif => SerifModel,
            ProfileFontFamilies.AetherFrameMono => MonoModel,
            var id when FontLibrary.Find(id) is { } library => new SurfaceModel(library.Glyphs, library.Scale),
            _ => default,
        };

        if (model.Glyphs == 0)
        {
            return DalamudDefaultTierSurfacePixels;
        }

        var side = (model.Scale * Math.Max(0f, sizePx)) + PaddingPixels;
        return (long)Math.Ceiling(model.Glyphs * side * side);
    }

    /// <summary>The index in <see cref="SizeLadder"/> of the largest tier
    /// <paramref name="familyId"/> may build (see the type doc).</summary>
    internal static int MaxTierIndex(string? familyId) => ResolveFamilyId(familyId) switch
    {
        ProfileFontFamilies.AetherFrameSans => SansMaxTierIndex,
        ProfileFontFamilies.AetherFrameSerif => SerifMaxTierIndex,
        ProfileFontFamilies.AetherFrameMono => MonoMaxTierIndex,
        var id when LibraryMaxTierIndexes.TryGetValue(id, out var index) => index,
        _ => DalamudDefaultMaxTierIndex,
    };

    // Each library family's largest tier, from its fitted model (computed once).
    private static readonly Dictionary<string, int> LibraryMaxTierIndexes = BuildLibraryMaxTierIndexes();

    private static Dictionary<string, int> BuildLibraryMaxTierIndexes()
    {
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var family in FontLibrary.Families)
        {
            indexes.Add(family.Id, LargestTierWithin(new SurfaceModel(family.Glyphs, family.Scale), LibraryTierBudgetPixels));
        }

        return indexes;
    }

    /// <summary>The largest tier size <paramref name="familyId"/> may build, in pixels.</summary>
    internal static float MaxTierSize(string? familyId) => SizeLadder[MaxTierIndex(familyId)];

    /// <summary>
    /// The index in <see cref="SizeLadder"/> of the tier to use for a request of
    /// <paramref name="requestedPixelSize"/>: the smallest tier at or above the request, so a
    /// glyph raster is only ever drawn at or below its own size (never stretched up), capped at
    /// the family's <see cref="MaxTierIndex"/>. Above the cap the largest allowed tier is used
    /// and upscaled — unavoidable at extreme zoom, and still far less severe than the default
    /// font stretched several times over that the font cache replaced.
    /// </summary>
    internal static int FindTierIndex(string? familyId, float requestedPixelSize)
    {
        var maxIndex = MaxTierIndex(familyId);
        for (var i = 0; i < maxIndex; i++)
        {
            if (SizeLadder[i] >= requestedPixelSize)
            {
                return i;
            }
        }

        return maxIndex;
    }

    /// <summary>
    /// A library family's largest tier keeps its estimate under this, below
    /// <see cref="SingleTierBudgetPixels"/>: decorative faces' glyph boxes vary far more than
    /// AetherFrame's own text faces', which packs them less tightly into a texture. FontLibraryTests
    /// packs every library face at its cap.
    /// </summary>
    internal const long LibraryTierBudgetPixels = 12_500_000;

    private static int LargestTierWithin(SurfaceModel model, long budget = SingleTierBudgetPixels)
    {
        var index = 0;
        for (var i = 0; i < SizeLadder.Count; i++)
        {
            var side = (model.Scale * SizeLadder[i]) + PaddingPixels;
            if (model.Glyphs * side * side <= budget)
            {
                index = i;
            }
        }

        return index;
    }

    private static int LargestTierAtOrBelow(float sizePx)
    {
        var index = 0;
        for (var i = 0; i < SizeLadder.Count; i++)
        {
            if (SizeLadder[i] <= sizePx)
            {
                index = i;
            }
        }

        return index;
    }
}
