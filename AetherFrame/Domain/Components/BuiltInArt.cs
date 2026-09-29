using System;
using System.Collections.Generic;
using System.Numerics;

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
/// <param name="Family">The visual family the artwork belongs to ("Celestial Dream", "Astral Gold"):
/// shown with the name so same-named artwork of different families can be told apart. Display only.</param>
/// <param name="Kind">The one Component kind this artwork is drawn for.</param>
/// <param name="ResourceName">Manifest resource name of the runtime PNG inside the plugin assembly
/// (not a filesystem path). Always an 8-bit RGBA PNG whose sides are multiples of 16 (see
/// <c>BundledArtImage</c>).</param>
/// <param name="PixelWidth">The runtime PNG's width, in pixels.</param>
/// <param name="PixelHeight">The runtime PNG's height, in pixels. The artwork is always drawn at this
/// aspect ratio (fitted inside its placement box, never stretched).</param>
/// <param name="ColorMode">Whether the artwork takes the Component's color (greyscale artwork) or
/// keeps its own authored colors.</param>
/// <param name="DefaultOpacity">Alpha of the definition's default color.</param>
/// <param name="CornerPlacement">For Corner Ornaments: how the one (top-left) drawing serves the
/// other three corners.</param>
/// <param name="SizeFactor">Size of the artwork's placement box relative to its kind's standard
/// procedural box (artwork needs more room than a line mark to read): a Corner Ornament's square,
/// anchored at the same corner; for every other kind the box's height, around the same center (a
/// Section Header's around its underline, see <see cref="Pivot"/>).</param>
public sealed record BuiltInArtAsset(
    string Id,
    string Name,
    string Family,
    PlateComponentKind Kind,
    string ResourceName,
    int PixelWidth,
    int PixelHeight,
    ArtColorMode ColorMode,
    float DefaultOpacity,
    CornerArtPlacement CornerPlacement,
    float SizeFactor)
{
    /// <summary>Width over height of the runtime artwork (1 for square art).</summary>
    public float AspectRatio => PixelHeight > 0 ? (float)PixelWidth / PixelHeight : 1f;

    /// <summary>True when the artwork takes the Component's color (<see cref="ArtColorMode.Tintable"/>).</summary>
    public bool Tintable => ColorMode == ArtColorMode.Tintable;

    /// <summary>The placement box's width relative to its kind's standard box, for kinds placed around
    /// a center (every kind but Corner Ornaments); 1 keeps the standard width.</summary>
    public float WidthFactor { get; init; } = 1f;

    /// <summary>The point of the artwork (0 to 1 across and down) that sits on its placement's anchor
    /// point and that Scale grows it around: the center, except for Section Headers, whose anchor is
    /// the heading's start on its bottom line (so a pinned end can sit just before the text).</summary>
    public Vector2 Pivot { get; init; } = new(0.5f, 0.5f);
}

/// <summary>How bundled artwork takes color. A property of the built-in artwork, never persisted.</summary>
public enum ArtColorMode
{
    /// <summary>White/greyscale artwork multiplied by the Component's color (theme accent by default).</summary>
    Tintable,

    /// <summary>Full-color artwork drawn with its own colors: the Component's color is ignored, only
    /// Opacity applies.</summary>
    AuthoredColor,
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
    public const string CelestialDreamEquatorLine = "af.asset.celestial-dream.divider.equator-line";

    public const string AstralGoldOrbitalRing = "af.asset.astral-gold.plate-frame.orbital-ring";
    public const string AstralGoldCrescentCradle = "af.asset.astral-gold.portrait-frame.crescent-cradle";
    public const string AstralGoldFallingStardust = "af.asset.astral-gold.portrait-overlay.falling-stardust";
    public const string AstralGoldAstrolabePivot = "af.asset.astral-gold.corner-ornament.astrolabe-pivot";
    public const string AstralGoldOrbitalConstellationUnderlay = "af.asset.astral-gold.name-backing.orbital-constellation-underlay";
    public const string AstralGoldEquatorLine = "af.asset.astral-gold.divider.equator-line";
    public const string AstralGoldStarPinnedUnderline = "af.asset.astral-gold.section-header.star-pinned-underline";

    /// <summary>Family names (display only).</summary>
    public const string CelestialDream = "Celestial Dream";
    public const string AstralGold = "Astral Gold";

    /// <summary>Prefix of every bundled manifest resource name (see the plugin project's Assets folder).</summary>
    public const string ResourcePrefix = "AetherFrame.Assets.";

    public static readonly BuiltInArtAsset AstrolabePivot = new(
        CelestialDreamAstrolabePivot,
        "Astrolabe Pivot",
        CelestialDream,
        PlateComponentKind.CornerOrnament,
        ResourcePrefix + "Components.CelestialDream.CornerOrnaments.AstrolabePivot.png",
        PixelWidth: 512,
        PixelHeight: 512,
        ArtColorMode.Tintable,
        DefaultOpacity: 0.9f,
        CornerPlacement: CornerArtPlacement.Rotate,
        SizeFactor: 2f);

    /// <summary>A 3:1 horizontal rule with a central compass star, drawn centered on the Divider's line.
    /// The band is 4x the procedural Divider's height (the star and glow need it); the drawing is fitted
    /// inside the band at its own aspect ratio, so it is at most as wide as the name above it.</summary>
    public static readonly BuiltInArtAsset EquatorLine = new(
        CelestialDreamEquatorLine,
        "Equator Line",
        CelestialDream,
        PlateComponentKind.Divider,
        ResourcePrefix + "Components.CelestialDream.Dividers.EquatorLine.png",
        PixelWidth: 1536,
        PixelHeight: 512,
        ArtColorMode.Tintable,
        DefaultOpacity: 0.9f,
        CornerPlacement: CornerArtPlacement.Mirror, // not a Corner Ornament: unused
        SizeFactor: 4f);

    // Astral Gold: full-color gold, navy and blue artwork (authored color, never tinted). Every
    // drawing keeps its source aspect ratio; the plugin's Assets/README.md records how each runtime
    // PNG was prepared from its preserved source.

    /// <summary>A 16:9 frame designed around the whole Plate: drawn over the full canvas (its own
    /// margin keeps it off the edge), not inside the procedural frames' inset.</summary>
    public static readonly BuiltInArtAsset AstralOrbitalRing = new(
        AstralGoldOrbitalRing,
        "Orbital Ring",
        AstralGold,
        PlateComponentKind.PlateFrame,
        ResourcePrefix + "Components.AstralGold.PlateFrames.OrbitalRing.png",
        PixelWidth: 1536,
        PixelHeight: 864,
        ArtColorMode.AuthoredColor,
        DefaultOpacity: 1f,
        CornerPlacement: CornerArtPlacement.Mirror, // not a Corner Ornament: unused
        SizeFactor: 1f);

    /// <summary>A 5:8 frame drawn for the portrait's own proportions: placed exactly over the portrait.</summary>
    public static readonly BuiltInArtAsset AstralCrescentCradle = new(
        AstralGoldCrescentCradle,
        "Crescent Cradle",
        AstralGold,
        PlateComponentKind.PortraitFrame,
        ResourcePrefix + "Components.AstralGold.PortraitFrames.CrescentCradle.png",
        PixelWidth: 800,
        PixelHeight: 1280,
        ArtColorMode.AuthoredColor,
        DefaultOpacity: 1f,
        CornerPlacement: CornerArtPlacement.Mirror, // not a Corner Ornament: unused
        SizeFactor: 1f);

    /// <summary>Asymmetric stardust arching over the portrait's top (the 3:4 drawing padded below to the
    /// portrait's 5:8). Scaled past the portrait it overflows like any Component, never clipped.</summary>
    public static readonly BuiltInArtAsset AstralFallingStardust = new(
        AstralGoldFallingStardust,
        "Falling Stardust",
        AstralGold,
        PlateComponentKind.PortraitOverlay,
        ResourcePrefix + "Components.AstralGold.PortraitOverlays.FallingStardust.png",
        PixelWidth: 640,
        PixelHeight: 1024,
        ArtColorMode.AuthoredColor,
        DefaultOpacity: 1f,
        CornerPlacement: CornerArtPlacement.Mirror, // not a Corner Ornament: unused
        SizeFactor: 1f);

    /// <summary>The source is drawn for the top-right corner; the runtime PNG is it turned a quarter
    /// counter-clockwise (the top-left drawing), so the top-right corner shows it exactly as authored.</summary>
    public static readonly BuiltInArtAsset AstralAstrolabePivot = new(
        AstralGoldAstrolabePivot,
        "Astrolabe Pivot",
        AstralGold,
        PlateComponentKind.CornerOrnament,
        ResourcePrefix + "Components.AstralGold.CornerOrnaments.AstrolabePivot.png",
        PixelWidth: 512,
        PixelHeight: 512,
        ArtColorMode.AuthoredColor,
        DefaultOpacity: 1f,
        CornerPlacement: CornerArtPlacement.Rotate,
        SizeFactor: 2.5f);

    /// <summary>An open 3:1 cartouche behind the name, centered on it: 1.15x the name backing's width, up
    /// to twice its height (so it never rises above a Plate's top edge on the Classic layout; very long
    /// names get a cartouche narrower than the name, which Scale can enlarge).</summary>
    public static readonly BuiltInArtAsset AstralOrbitalConstellationUnderlay = new(
        AstralGoldOrbitalConstellationUnderlay,
        "Orbital Constellation Underlay",
        AstralGold,
        PlateComponentKind.NameBacking,
        ResourcePrefix + "Components.AstralGold.NameBackings.OrbitalConstellationUnderlay.png",
        PixelWidth: 1536,
        PixelHeight: 512,
        ArtColorMode.AuthoredColor,
        DefaultOpacity: 1f,
        CornerPlacement: CornerArtPlacement.Mirror, // not a Corner Ornament: unused
        SizeFactor: 2f)
    {
        WidthFactor = 1.15f,
    };

    /// <summary>A thinner 3:1 rule than Celestial Dream's (its drawing fills ~40% of its height), so its
    /// band is 5x the procedural Divider's.</summary>
    public static readonly BuiltInArtAsset AstralEquatorLine = new(
        AstralGoldEquatorLine,
        "Equator Line",
        AstralGold,
        PlateComponentKind.Divider,
        ResourcePrefix + "Components.AstralGold.Dividers.EquatorLine.png",
        PixelWidth: 1536,
        PixelHeight: 512,
        ArtColorMode.AuthoredColor,
        DefaultOpacity: 1f,
        CornerPlacement: CornerArtPlacement.Mirror, // not a Corner Ornament: unused
        SizeFactor: 5f);

    /// <summary>A 3:1 underline pinned by a star medallion at its start: 2.5x the heading's height, its
    /// line on the heading's bottom edge, the medallion just before the heading text.</summary>
    public static readonly BuiltInArtAsset AstralStarPinnedUnderline = new(
        AstralGoldStarPinnedUnderline,
        "Star Pinned Underline",
        AstralGold,
        PlateComponentKind.SectionHeader,
        ResourcePrefix + "Components.AstralGold.SectionHeaders.StarPinnedUnderline.png",
        PixelWidth: 1152,
        PixelHeight: 384,
        ArtColorMode.AuthoredColor,
        DefaultOpacity: 1f,
        CornerPlacement: CornerArtPlacement.Mirror, // not a Corner Ornament: unused
        SizeFactor: 2.5f)
    {
        Pivot = new Vector2(0.27f, 0.52f),
    };

    public static readonly IReadOnlyList<BuiltInArtAsset> All =
    [
        AstrolabePivot, EquatorLine,
        AstralOrbitalRing, AstralCrescentCradle, AstralFallingStardust, AstralAstrolabePivot,
        AstralOrbitalConstellationUnderlay, AstralEquatorLine, AstralStarPinnedUnderline,
    ];

    private static readonly Dictionary<string, BuiltInArtAsset> ById = BuildIndex();

    /// <summary>The artwork with this exact id, or null (ids are case-sensitive).</summary>
    public static BuiltInArtAsset? Find(string? id) => id is not null && ById.TryGetValue(id, out var art) ? art : null;

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
