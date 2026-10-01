using System;
using System.Collections.Generic;

namespace AetherFrame.Domain.Components;

/// <summary>
/// A piece of artwork bundled inside the plugin assembly, drawn by a graphical built-in
/// <see cref="ComponentDefinition"/> (<see cref="ComponentShape.Art"/>). Compile-time data like the
/// definitions themselves: nothing about it is persisted, so a Plate never stores a path, a file
/// name or image bytes — only the definition id that selects it.
/// </summary>
/// <param name="Id">Stable, frozen-forever logical id ("af.asset.&lt;family&gt;.&lt;kind&gt;.&lt;name&gt;"),
/// independent of where or how the image is bundled. Never reused.</param>
/// <param name="Name">Display label only.</param>
/// <param name="Kind">The one Component kind this artwork is drawn for.</param>
/// <param name="ResourceName">Manifest resource name of the runtime PNG inside the plugin assembly
/// (not a filesystem path). Always an 8-bit RGBA (or, for opaque art, RGB) PNG (see <c>BundledArtImage</c>).</param>
/// <param name="PixelWidth">The runtime PNG's width, in pixels.</param>
/// <param name="PixelHeight">The runtime PNG's height, in pixels. The artwork is always drawn at this
/// aspect ratio (fitted inside its placement box, never visibly stretched: see <c>ComponentPaintPlan</c>).</param>
/// <param name="Tintable">True when the artwork is white/greyscale and takes the Component's color;
/// false draws its own colors, with only the color's alpha applied.</param>
/// <param name="DefaultOpacity">Alpha of the definition's default color.</param>
/// <param name="CornerPlacement">For Corner Ornaments: how the one (top-left) drawing serves the
/// other three corners.</param>
/// <param name="SizeFactor">Size of the artwork's placement box relative to its kind's standard
/// procedural box (artwork needs more room than a line mark to read): a Corner Ornament's square,
/// anchored at the same corner; a Name Backing's or Divider's box, around the same center. Unused
/// (1) for the kinds whose art fills its whole anchor: Background, Plate Frame, Portrait Frame.</param>
public sealed record BuiltInArtAsset(
    string Id,
    string Name,
    PlateComponentKind Kind,
    string ResourceName,
    int PixelWidth,
    int PixelHeight,
    bool Tintable,
    float DefaultOpacity,
    CornerArtPlacement CornerPlacement,
    float SizeFactor)
{
    /// <summary>Width over height of the runtime artwork (1 for square art).</summary>
    public float AspectRatio => PixelHeight > 0 ? (float)PixelWidth / PixelHeight : 1f;

    /// <summary>How the artwork stretches to any width, or null when it is always drawn whole at its
    /// own aspect ratio. Only Name Backings and Dividers are sliced (see <c>ComponentPaintPlan</c>).</summary>
    public ArtSlices? Slices { get; init; }

    /// <summary>The horizontal span of <paramref name="piece"/> in texture coordinates (0 to 1);
    /// the whole width for <see cref="ArtPiece.Whole"/> and for any piece of unsliced artwork.</summary>
    public (float U0, float U1) Window(ArtPiece piece)
    {
        if (Slices is not { } slices || PixelWidth <= 0)
        {
            return (0f, 1f);
        }

        var (x0, x1) = piece switch
        {
            ArtPiece.LeftCap => (0, slices.CapLeft),
            ArtPiece.LeftFill => (slices.CapLeft, slices.CenterLeft),
            ArtPiece.Center => (slices.CenterLeft, slices.CenterRight),
            ArtPiece.RightFill => (slices.CenterRight, slices.CapRight),
            ArtPiece.RightCap => (slices.CapRight, PixelWidth),
            _ => (0, PixelWidth),
        };

        return ((float)x0 / PixelWidth, (float)x1 / PixelWidth);
    }

    /// <summary>The ident a shared Plate names <paramref name="piece"/> by: <see cref="Id"/> for the whole
    /// artwork, else the id and the piece's suffix ("[id].left-cap"). See <see cref="BuiltInArtCatalog.FindPiece"/>.</summary>
    public string PieceIdent(ArtPiece piece) => piece == ArtPiece.Whole ? Id : Id + "." + ArtPieces.Suffix(piece);

    /// <summary>The visual family the artwork was designed in, shown with it wherever Components are
    /// browsed ("Celestial Sakura"); null for a standalone piece. Display and grouping only.</summary>
    public string? Family { get; init; }

    /// <summary>Extra words the artwork answers to in a search (motifs, colors, its role), lower case.
    /// Display metadata only: nothing is ever resolved by them.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>True when <paramref name="query"/> (trimmed, case-insensitive) occurs in the name, the
    /// family or a keyword; an empty query matches everything.</summary>
    public bool MatchesSearch(string? query)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (Name.Contains(text, StringComparison.OrdinalIgnoreCase) || (Family?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return true;
        }

        foreach (var keyword in Keywords)
        {
            if (keyword.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Where a horizontal artwork (a nameplate, a divider) is cut so it fits any width: five pieces,
/// left to right — a left cap, a fill, a center piece, a fill, a right cap. The caps and the center
/// piece are always drawn at the artwork's own proportions; only the two fills stretch, equally, so
/// a short name gets a compact plaque and a long one a long plaque, and no ornament is ever
/// distorted. A fill must therefore look the same in every column (plain rails and a plain band).
/// Every value is an x position in the runtime PNG's pixels, in order:
/// 0 &lt;= <paramref name="ContentLeft"/> &lt;= <paramref name="CapLeft"/> &lt;= <paramref name="CenterLeft"/>
/// &lt;= <paramref name="CenterRight"/> &lt;= <paramref name="CapRight"/> &lt;= <paramref name="ContentRight"/>
/// &lt;= the artwork's width. Artwork without a center piece has <paramref name="CenterLeft"/> equal
/// to <paramref name="CenterRight"/>.
/// </summary>
/// <param name="ContentLeft">Where the text area starts: the anchor box (the name and title, padded)
/// spans <paramref name="ContentLeft"/> to <paramref name="ContentRight"/>. 0 for a divider, which
/// spans its whole width.</param>
/// <param name="CapLeft">Where the left cap ends and the left fill starts.</param>
/// <param name="CenterLeft">Where the left fill ends and the center piece starts.</param>
/// <param name="CenterRight">Where the center piece ends and the right fill starts.</param>
/// <param name="CapRight">Where the right fill ends and the right cap starts.</param>
/// <param name="ContentRight">Where the text area ends.</param>
public sealed record ArtSlices(int ContentLeft, int CapLeft, int CenterLeft, int CenterRight, int CapRight, int ContentRight)
{
    /// <summary>True when every value is in order inside an artwork <paramref name="pixelWidth"/> wide,
    /// and both fills are at least a pixel wide (each stretches).</summary>
    public bool IsValidFor(int pixelWidth) =>
        ContentLeft >= 0 && ContentLeft <= CapLeft && CapLeft < CenterLeft && CenterLeft <= CenterRight
        && CenterRight < CapRight && CapRight <= ContentRight && ContentRight <= pixelWidth;

    /// <summary>The pixels that never stretch: the caps and the center piece.</summary>
    public int FixedWidth(int pixelWidth) => CapLeft + (CenterRight - CenterLeft) + (pixelWidth - CapRight);

    /// <summary>The pixels outside the text area, on both sides together.</summary>
    public int OutsideContent(int pixelWidth) => ContentLeft + (pixelWidth - ContentRight);
}

/// <summary>A part of an artwork, as a primitive draws it. Not persisted.</summary>
public enum ArtPiece
{
    /// <summary>The whole artwork (every unsliced artwork).</summary>
    Whole,
    LeftCap,
    LeftFill,
    Center,
    RightFill,
    RightCap,
}

/// <summary>The suffixes a shared Plate's art idents give the pieces of sliced artwork.</summary>
public static class ArtPieces
{
    /// <summary>The pieces of sliced artwork, left to right.</summary>
    public static readonly IReadOnlyList<ArtPiece> Sliced = [ArtPiece.LeftCap, ArtPiece.LeftFill, ArtPiece.Center, ArtPiece.RightFill, ArtPiece.RightCap];

    /// <summary>The ident suffix of <paramref name="piece"/>: lower case letters and '-' only. Frozen once
    /// shipped, like the art ids themselves (a viewer looks pieces up by them).</summary>
    public static string Suffix(ArtPiece piece) => piece switch
    {
        ArtPiece.LeftCap => "left-cap",
        ArtPiece.LeftFill => "left-fill",
        ArtPiece.Center => "center",
        ArtPiece.RightFill => "right-fill",
        ArtPiece.RightCap => "right-cap",
        _ => string.Empty,
    };
}

/// <summary>How a corner drawing, designed for the top-left corner, is placed in the other corners.</summary>
public enum CornerArtPlacement
{
    /// <summary>Mirrored horizontally and/or vertically (what the procedural corner marks do).</summary>
    Mirror,

    /// <summary>Rotated by 90/180/270 degrees, so asymmetric details keep their handedness.</summary>
    Rotate,
}

/// <summary>The artwork AetherFrame bundles. Local and compile-time: no files outside the plugin
/// assembly, no network, no user packs.</summary>
public static class BuiltInArtCatalog
{
    public const string CelestialDreamAstrolabePivot = "af.asset.celestial-dream.corner-ornament.astrolabe-pivot";

    public const string CelestialSakuraBackground = "af.asset.celestial-sakura.background.twilight";
    public const string CelestialSakuraPlateFrame = "af.asset.celestial-sakura.plate-frame.blossom";
    public const string CelestialSakuraPortraitFrame = "af.asset.celestial-sakura.portrait-frame.blossom";
    public const string CelestialSakuraNameplate = "af.asset.celestial-sakura.name-backing.nameplate";
    public const string CelestialSakuraOrnateDivider = "af.asset.celestial-sakura.divider.ornate";
    public const string CelestialSakuraSlimDivider = "af.asset.celestial-sakura.divider.slim";
    public const string CelestialSakuraCornerOrnament = "af.asset.celestial-sakura.corner-ornament.blossom";

    /// <summary>The family name every Celestial Sakura piece carries (<see cref="BuiltInArtAsset.Family"/>).</summary>
    public const string CelestialSakuraFamily = "Celestial Sakura";

    /// <summary>Prefix of every bundled manifest resource name (see the plugin project's Assets folder).</summary>
    public const string ResourcePrefix = "AetherFrame.Assets.";

    private const string CelestialSakuraResources = ResourcePrefix + "Components.CelestialSakura.";

    public static readonly BuiltInArtAsset AstrolabePivot = new(
        CelestialDreamAstrolabePivot,
        "Astrolabe Pivot",
        PlateComponentKind.CornerOrnament,
        ResourcePrefix + "Components.CelestialDream.CornerOrnaments.AstrolabePivot.png",
        PixelWidth: 512,
        PixelHeight: 512,
        Tintable: true,
        DefaultOpacity: 0.9f,
        CornerPlacement: CornerArtPlacement.Rotate,
        SizeFactor: 2f);

    // Celestial Sakura: full-color artwork (champagne gold filigree, blush cherry blossoms, pearls,
    // a crescent moon) bundled exactly as approved — never tinted, at its generated resolution. The
    // Plate-sized pieces are 16:9 like the Adventure Plate canvas, the Portrait Frame 5:8 like its
    // portrait; each is drawn at its own aspect ratio (see Assets/README.md for the measurements).

    /// <summary>The words every Celestial Sakura piece answers to, before its own role words. Declared
    /// before the pieces: static fields initialize in order.</summary>
    private static readonly string[] CelestialSakuraKeywords = ArtKeywords.CelestialSakura;

    /// <summary>A twilight sky with cherry branches and a crescent moon, covering the whole Plate (opaque).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraBackgroundArt = CelestialSakura(
        CelestialSakuraBackground, "Celestial Sakura", PlateComponentKind.Background, "CelestialSakura_Background.png", 1672, 941, 1f,
        "background", "sky", "twilight", "landscape");

    /// <summary>A gold filigree border with blossom corners and a crescent crest, drawn edge to edge
    /// (the drawing keeps its own few-pixel margin).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraPlateFrameArt = CelestialSakura(
        CelestialSakuraPlateFrame, "Celestial Sakura", PlateComponentKind.PlateFrame, "CelestialSakura_PlateFrame.png", 1672, 941, 1f,
        "frame", "plate frame", "border");

    /// <summary>A slim gold portrait border with blossoms at opposing corners, fitted to the portrait.</summary>
    public static readonly BuiltInArtAsset CelestialSakuraPortraitFrameArt = CelestialSakura(
        CelestialSakuraPortraitFrame, "Celestial Sakura", PlateComponentKind.PortraitFrame, "CelestialSakura_PortraitFrame.png", 992, 1586, 1f,
        "frame", "portrait", "portrait frame", "border");

    /// <summary>An ivory enamel plaque with blossom ends, behind the name. 1.5x the name backing's height
    /// (the plaque's rails and ornaments surround a text area about a third of its height), and as
    /// wide as the name: sliced between the blossom ends and the crescent crest, where the plaque is
    /// only its plain rails and band (see Assets/README.md for the measurements).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraNameplateArt = CelestialSakura(
        CelestialSakuraNameplate, "Celestial Sakura", PlateComponentKind.NameBacking, "CelestialSakura_Nameplate.png", 2172, 724, 1.5f,
        "nameplate", "name", "plaque", "banner") with
    {
        Slices = new ArtSlices(ContentLeft: 340, CapLeft: 512, CenterLeft: 760, CenterRight: 1400, CapRight: 1672, ContentRight: 1840),
    };

    /// <summary>The primary divider: curling gold with blossom clusters and a crescent-set gem. 3:1, in a
    /// band 3x the procedural Divider's height: its drawing fills two thirds of that band, which keeps
    /// the crescent clear of the name above and the first section heading below on the Classic layout.</summary>
    public static readonly BuiltInArtAsset CelestialSakuraOrnateDividerArt = CelestialSakura(
        CelestialSakuraOrnateDivider, "Celestial Sakura Ornate", PlateComponentKind.Divider, "CelestialSakura_Divider_Ornate.png", 2172, 724, 3f,
        "divider", "ornate", "line");

    /// <summary>The secondary divider: a fine tapering gold line with one star and one blossom, in a band
    /// 4x the procedural Divider's height (only its small star rises above the line, so it can be wider).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraSlimDividerArt = CelestialSakura(
        CelestialSakuraSlimDivider, "Celestial Sakura Slim", PlateComponentKind.Divider, "CelestialSakura_Divider_Slim.png", 2172, 724, 4f,
        "divider", "slim", "line", "star");

    /// <summary>An L-shaped blossom cluster with a crescent, drawn for the top-left corner and mirrored
    /// into the others (so its hanging crystals hang down in both top corners), 3x the procedural box.</summary>
    public static readonly BuiltInArtAsset CelestialSakuraCornerOrnamentArt = CelestialSakura(
        CelestialSakuraCornerOrnament, "Celestial Sakura", PlateComponentKind.CornerOrnament, "CelestialSakura_CornerOrnament.png", 1254, 1254, 3f,
        "corner", "ornament", "corner ornament");

    public static readonly IReadOnlyList<BuiltInArtAsset> All =
    [
        AstrolabePivot,
        CelestialSakuraBackgroundArt,
        CelestialSakuraPlateFrameArt,
        CelestialSakuraPortraitFrameArt,
        CelestialSakuraNameplateArt,
        CelestialSakuraOrnateDividerArt,
        CelestialSakuraSlimDividerArt,
        CelestialSakuraCornerOrnamentArt,
        .. ArtSets.Assets,
    ];

    private static readonly Dictionary<string, BuiltInArtAsset> ById = BuildIndex();

    /// <summary>The artwork with this exact id, or null (ids are case-sensitive).</summary>
    public static BuiltInArtAsset? Find(string? id) => id is not null && ById.TryGetValue(id, out var art) ? art : null;

    /// <summary>
    /// The artwork and piece a shared Plate's art ident names (<see cref="BuiltInArtAsset.PieceIdent"/>):
    /// an exact id is the whole artwork; an id followed by a piece's suffix is that piece of a sliced
    /// artwork. Null for anything else, a piece of unsliced artwork included. Exact matches only,
    /// like <see cref="Find"/>.
    /// </summary>
    public static (BuiltInArtAsset Art, ArtPiece Piece)? FindPiece(string? ident)
    {
        if (Find(ident) is { } whole)
        {
            return (whole, ArtPiece.Whole);
        }

        if (ident is null)
        {
            return null;
        }

        foreach (var piece in ArtPieces.Sliced)
        {
            var suffix = "." + ArtPieces.Suffix(piece);
            if (ident.EndsWith(suffix, StringComparison.Ordinal) && Find(ident[..^suffix.Length]) is { Slices: not null } art)
            {
                return (art, piece);
            }
        }

        return null;
    }

    /// <summary>Every artwork of one family, in catalog order (display grouping; never used to resolve).</summary>
    public static IEnumerable<BuiltInArtAsset> OfFamily(string family)
    {
        foreach (var art in All)
        {
            if (string.Equals(art.Family, family, StringComparison.Ordinal))
            {
                yield return art;
            }
        }
    }

    private static BuiltInArtAsset CelestialSakura(string id, string name, PlateComponentKind kind, string file, int width, int height, float sizeFactor, params string[] roleKeywords) =>
        new(id, name, kind, CelestialSakuraResources + file, width, height, Tintable: false, DefaultOpacity: 1f, CornerArtPlacement.Mirror, sizeFactor)
        {
            Family = CelestialSakuraFamily,
            Keywords = [.. CelestialSakuraKeywords, .. roleKeywords],
        };

    private static Dictionary<string, BuiltInArtAsset> BuildIndex()
    {
        var index = new Dictionary<string, BuiltInArtAsset>(StringComparer.Ordinal);
        foreach (var art in All)
        {
            index.Add(art.Id, art); // throws on a duplicate id: caught by any test run
        }

        return index;
    }
}

/// <summary>Search words more than one catalog uses: a class of its own, so the catalogs share them
/// without depending on each other's static initialization.</summary>
internal static class ArtKeywords
{
    /// <summary>The words every Celestial Sakura piece answers to, before its own role words.</summary>
    internal static readonly string[] CelestialSakura =
        ["celestial sakura", "sakura", "cherry blossom", "blossom", "moon", "crescent", "rose", "pink", "gold", "pearl"];
}
