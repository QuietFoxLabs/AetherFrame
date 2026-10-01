using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Components;

/// <summary>One bundled art set, as tools/art/make_runtime_art.py measured it (see <c>ArtSetData.g.cs</c>).</summary>
/// <param name="Slug">The set's part of every id ("allagan-tech"). Frozen forever, like the ids it makes.</param>
/// <param name="Name">Display name.</param>
/// <param name="Folder">The set's folder under Assets/Components, and its files' prefix.</param>
/// <param name="Theme">What the set looks like, for descriptions.</param>
/// <param name="Keywords">Search words, lower case.</param>
internal sealed record ArtSetSpec(
    string Slug,
    string Name,
    string Folder,
    string Theme,
    string[] Keywords,
    SlicedArtSpec NameBacking,
    SlicedArtSpec Divider,
    SlicedArtSpec SectionHeader,
    ArtStyleColors Colors);

/// <summary>A sliced piece's size on the Plate (<see cref="BuiltInArtAsset.SizeFactor"/>) and cuts.</summary>
internal readonly record struct SlicedArtSpec(float SizeFactor, ArtSlices Slices);

/// <summary>A style's colors, as 0xRRGGBB: the plain background under the art, the Details text,
/// the title and headings (on the backings), quieter text, and the name and its outline.</summary>
internal readonly record struct ArtStyleColors(uint Primary, uint Secondary, uint Text, uint Accent, uint Soft, uint Name, uint NameOutline, float NameOutlineStrength);

/// <summary>
/// The art sets AetherFrame bundles, as artwork, Components and Art Styles. Each set has seven pieces:
/// a Background, a Plate Frame, a Portrait Frame and a Corner Ornament, and a Name Backing, a Divider
/// and a Section Header that stretch to fit their text (<see cref="ArtSlices"/>). Every id is made
/// from the set's slug and frozen forever: "af.asset.&lt;slug&gt;.&lt;kind&gt;.standard" for the artwork,
/// "af.&lt;kind&gt;.&lt;slug&gt;" for the Component and "af.style.&lt;slug&gt;" for the style.
/// Celestial Sakura, which shipped first, keeps its own ids and gains a Section Header.
/// </summary>
public static class ArtSets
{
    /// <summary>Every new piece of artwork: seven per set, and Celestial Sakura's Section Header.</summary>
    public static IReadOnlyList<BuiltInArtAsset> Assets { get; }

    /// <summary>A Component for each piece in <see cref="Assets"/>.</summary>
    public static IReadOnlyList<ComponentDefinition> Definitions { get; }

    /// <summary>One Art Style per set, Celestial Sakura first: a theme that also places the set's seven pieces.</summary>
    public static IReadOnlyList<ProfileThemePreset> Styles { get; }

    public const string CelestialSakuraSectionHeader = "af.asset.celestial-sakura.section-header.ribbon";
    public const string SectionHeaderCelestialSakura = "af.section-header.celestial-sakura";

    private const string ResourceComponents = BuiltInArtCatalog.ResourcePrefix + "Components.";

    static ArtSets()
    {
        var assets = new List<BuiltInArtAsset>();
        var definitions = new List<ComponentDefinition>();
        var styles = new List<ProfileThemePreset>();

        var sakuraHeader = Asset(
            CelestialSakuraSectionHeader, BuiltInArtCatalog.CelestialSakuraFamily, "CelestialSakura", "SectionHeader", PlateComponentKind.SectionHeader,
            ArtSetData.CelestialSakuraSectionHeader, ArtKeywords.CelestialSakura);
        assets.Add(sakuraHeader);
        definitions.Add(ComponentDefinition.ForArt(SectionHeaderCelestialSakura, Describe(BuiltInArtCatalog.CelestialSakuraFamily, PlateComponentKind.SectionHeader, null), sakuraHeader, ComponentColorSource.White));
        styles.Add(Style(
            "celestial-sakura", BuiltInArtCatalog.CelestialSakuraFamily, "Cherry blossoms, moonlight and champagne gold", "CelestialSakura", ArtSetData.CelestialSakuraColors,
            [
                BuiltInComponentCatalog.BackgroundCelestialSakura, BuiltInComponentCatalog.PlateFrameCelestialSakura, BuiltInComponentCatalog.PortraitFrameCelestialSakura,
                BuiltInComponentCatalog.NameBackingCelestialSakura, BuiltInComponentCatalog.DividerCelestialSakuraOrnate, SectionHeaderCelestialSakura,
                BuiltInComponentCatalog.CornerOrnamentCelestialSakura,
            ]));

        foreach (var set in ArtSetData.Sets)
        {
            var ids = new List<string>();
            foreach (var (kind, piece, spec) in Pieces(set))
            {
                var art = Asset($"af.asset.{set.Slug}.{KindSlug(kind)}.standard", set.Name, set.Folder, piece, kind, spec, set.Keywords);
                assets.Add(art);
                var id = $"af.{KindSlug(kind)}.{set.Slug}";
                definitions.Add(ComponentDefinition.ForArt(id, Describe(set.Name, kind, set.Theme), art, ComponentColorSource.White));
                ids.Add(id);
            }

            styles.Add(Style(set.Slug, set.Name, Capitalized(set.Theme), set.Folder, set.Colors, ids));
        }

        Assets = assets;
        Definitions = definitions;
        Styles = styles;
    }

    /// <summary>The size of an Art Style's preview card (Assets/StylePreviews), in pixels.</summary>
    public const int PreviewWidth = 384;
    public const int PreviewHeight = 216;

    private static readonly Dictionary<string, BuiltInArtAsset> PreviewsByStyle = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="style"/>'s preview card as artwork the texture cache can load (it decodes, sizes
    /// and caches it like any bundled art), or null for a theme without one. Never a Component: it is
    /// in no catalog, and nothing stores its id.
    /// </summary>
    public static BuiltInArtAsset? PreviewArt(ProfileThemePreset style)
    {
        if (style.PreviewResource is not { } resource)
        {
            return null;
        }

        lock (PreviewsByStyle)
        {
            if (!PreviewsByStyle.TryGetValue(style.Id, out var art))
            {
                art = new BuiltInArtAsset($"af.preview.{style.Id}", style.Name, PlateComponentKind.Background, resource, PreviewWidth, PreviewHeight, Tintable: false, DefaultOpacity: 1f, CornerArtPlacement.Mirror, 1f);
                PreviewsByStyle.Add(style.Id, art);
            }

            return art;
        }
    }

    /// <summary>The style placing <paramref name="definitionId"/>, or null (exact ids).</summary>
    public static ProfileThemePreset? StyleOf(string? definitionId)
    {
        foreach (var style in Styles)
        {
            foreach (var id in style.Components)
            {
                if (string.Equals(id, definitionId, StringComparison.Ordinal))
                {
                    return style;
                }
            }
        }

        return null;
    }

    private static IEnumerable<(PlateComponentKind Kind, string Piece, SlicedArtSpec? Spec)> Pieces(ArtSetSpec set)
    {
        yield return (PlateComponentKind.Background, "Background", null);
        yield return (PlateComponentKind.PlateFrame, "PlateFrame", null);
        yield return (PlateComponentKind.PortraitFrame, "PortraitFrame", null);
        yield return (PlateComponentKind.NameBacking, "NameBacking", set.NameBacking);
        yield return (PlateComponentKind.Divider, "Divider", set.Divider);
        yield return (PlateComponentKind.SectionHeader, "SectionHeader", set.SectionHeader);
        yield return (PlateComponentKind.CornerOrnament, "CornerOrnament", null);
    }

    /// <summary>A piece's artwork: the full-size pieces at the sources' sizes, the others at half size
    /// (see Assets/ArtSets.md), drawn in their own colors.</summary>
    private static BuiltInArtAsset Asset(string id, string family, string folder, string piece, PlateComponentKind kind, SlicedArtSpec? spec, IReadOnlyList<string> setKeywords)
    {
        var (width, height, factor) = kind switch
        {
            PlateComponentKind.Background or PlateComponentKind.PlateFrame => (1672, 941, 1f),
            PlateComponentKind.PortraitFrame => (992, 1586, 1f),
            PlateComponentKind.CornerOrnament => (627, 627, 3f),
            _ => (1086, 362, spec?.SizeFactor ?? 1f),
        };

        return new BuiltInArtAsset(id, family, kind, $"{ResourceComponents}{folder}.{folder}_{piece}.png", width, height, Tintable: false, DefaultOpacity: 1f, CornerArtPlacement.Mirror, factor)
        {
            Family = family,
            Keywords = [.. setKeywords, .. RoleKeywords(kind)],
            Slices = spec?.Slices,
        };
    }

    private static ProfileThemePreset Style(string slug, string name, string description, string folder, ArtStyleColors colors, IReadOnlyList<string> components) =>
        new ProfileThemePreset(
            $"af.style.{slug}", name, ThemeFamily.ArtStyle, description,
            Rgb(colors.Primary), Rgb(colors.Secondary), 135f, ProfileBackgroundTexture.None, 0f,
            Rgb(colors.Text), Rgb(colors.Accent), Rgb(colors.Soft))
            .WithName(Rgb(colors.Name), Rgb(colors.NameOutline), colors.NameOutlineStrength)
            with
            {
                Components = components,
                PreviewResource = $"{BuiltInArtCatalog.ResourcePrefix}StylePreviews.{folder}.png",
            };

    private static string KindSlug(PlateComponentKind kind) => kind switch
    {
        PlateComponentKind.Background => "background",
        PlateComponentKind.PlateFrame => "plate-frame",
        PlateComponentKind.PortraitFrame => "portrait-frame",
        PlateComponentKind.NameBacking => "name-backing",
        PlateComponentKind.Divider => "divider",
        PlateComponentKind.SectionHeader => "section-header",
        PlateComponentKind.CornerOrnament => "corner-ornament",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string[] RoleKeywords(PlateComponentKind kind) => kind switch
    {
        PlateComponentKind.Background => ["background", "backdrop"],
        PlateComponentKind.PlateFrame => ["frame", "plate frame", "border"],
        PlateComponentKind.PortraitFrame => ["frame", "portrait", "portrait frame", "border"],
        PlateComponentKind.NameBacking => ["nameplate", "name", "plaque", "banner"],
        PlateComponentKind.Divider => ["divider", "line"],
        PlateComponentKind.SectionHeader => ["section header", "heading", "label", "ribbon"],
        PlateComponentKind.CornerOrnament => ["corner", "ornament", "corner ornament"],
        _ => [],
    };

    private static string Describe(string name, PlateComponentKind kind, string? theme) => kind switch
    {
        PlateComponentKind.Background => $"{name}: {theme}, over the whole Plate.",
        PlateComponentKind.PlateFrame => $"{name}: a frame around the Plate.",
        PlateComponentKind.PortraitFrame => $"{name}: a frame around the portrait.",
        PlateComponentKind.NameBacking => $"{name}: a plaque behind the name, as long as the name.",
        PlateComponentKind.Divider => $"{name}: an ornamental line under the name.",
        PlateComponentKind.SectionHeader => $"{name}: a label behind each section heading, as long as the heading.",
        _ => $"{name}: an ornament in each corner.",
    };

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static Vector4 Rgb(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
