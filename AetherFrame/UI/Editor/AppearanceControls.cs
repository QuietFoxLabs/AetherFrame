using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Which appearance controls can change how a Plate looks (issue #119), read from what the renderer
/// draws and never from which style is chosen, so the Basic and Advanced editors offer the same ones.
/// The renderer paints the Plate's own background, then any Background artwork over it, then every
/// other Component and element (<c>ProfileRenderer</c>, <see cref="ComponentPaintPlan"/>). So:
/// <list type="bullet">
/// <item>The Plate's own background settings (Basic's Pattern and Customize Background, the Advanced
/// editor's Background) change something only while some of that background shows (<see cref="Background"/>).
/// Background artwork drawn opaque over the whole Plate hides all of it, whichever style or player
/// placed it, and so does the plain color standing in for it while it loads. Artwork that is hidden,
/// see-through, smaller, moved or turned off an edge, or not drawn at all (none placed, taken away,
/// left out at the Component limit, or one this build doesn't know) lets it show.</item>
/// <item>A Component's color changes something unless the Component is artwork drawn in its own
/// colors, as every Art Style's pieces are (<see cref="ColorApplies"/>).</item>
/// <item>Everything else always applies: choosing a Look, text colors and sizes, and each
/// Component's style, opacity, placement and size.</item>
/// </list>
/// Read-only and asked every frame: a control that doesn't apply keeps its value, which applies
/// again, as it was, from the frame a change (a style chosen, artwork taken away) lets it show.
/// </summary>
internal static class AppearanceControls
{
    /// <summary>What a Component's Color row shows in place of a color, for artwork drawn in its own colors.</summary>
    internal const string OwnColorsLabel = "Its own colors";

    /// <summary>Why a Component of artwork drawn in its own colors offers no color.</summary>
    internal const string OwnColorsReason = "This artwork is drawn in its own colors, so a color would change nothing.\nOpacity sets how see-through it is.";

    /// <summary>Under a custom color kept on such artwork (from a style before, say): what of it still applies.</summary>
    internal const string KeptColorReason = "This artwork is drawn in its own colors: only this color's transparency applies. Turn Custom off to leave that to Opacity.";

    /// <summary>What covers the whole of the Plate's own background, as the renderer draws it now.</summary>
    internal static BackgroundCover Background(ProfileDocument profile) =>
        PlateComponentEditor.CoveringBackground(profile, BuiltInComponentCatalog.Instance, out var component) is { } artwork && component is not null
            ? new BackgroundCover(component, artwork)
            : default;

    /// <summary>
    /// Whether a color changes how a Component of <paramref name="definition"/> is drawn: false only for
    /// artwork drawn in its own colors, which takes nothing from a color but its alpha, as from Opacity
    /// (<see cref="ComponentGeometry"/>). Procedural styles, tintable artwork and images take it; a
    /// definition this build doesn't know (null) keeps its control.
    /// </summary>
    internal static bool ColorApplies(ComponentDefinition? definition) => definition?.Art is not { Tintable: false };

    /// <summary>The Basic editor's line in place of Pattern and Customize Background while <paramref name="cover"/> covers the Plate.</summary>
    internal static string BasicCoveredHint(BackgroundCover cover) =>
        $"Pattern and Customize Background are hidden while {ArtworkName(cover)} covers the whole Plate. Your settings are kept.";

    /// <summary>The Advanced editor's line over its greyed-out Background while <paramref name="cover"/> covers the Plate.</summary>
    internal static string AdvancedCoveredHint(BackgroundCover cover) =>
        $"{Capitalized(ArtworkName(cover))} covers the whole Plate, so the settings below don't show. They are kept for when it is removed, hidden or made see-through under Components.";

    private static string ArtworkName(BackgroundCover cover) =>
        cover.Artwork is { } artwork ? $"the {artwork.Name} background artwork" : "background artwork";

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>
/// The Background artwork hiding the whole of the Plate's own background, drawn by
/// <see cref="Component"/>, or none (<c>default</c>): see <see cref="PlateComponentEditor.CoveringBackground(ProfileDocument, IComponentCatalog)"/>.
/// </summary>
internal readonly record struct BackgroundCover(PlateComponent? Component, ComponentDefinition? Artwork)
{
    /// <summary>True while some of the Plate's own background shows, so its settings apply.</summary>
    internal bool BackgroundShows => Artwork is null;
}
