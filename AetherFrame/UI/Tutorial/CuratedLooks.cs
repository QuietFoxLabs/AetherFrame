using System.Collections.Generic;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Tutorial;

/// <summary>
/// The four looks guided creation offers first, each an existing Art Style chosen to cover a wide
/// range of moods (regal, bright crystal, cozy, neon), and for each one a Simple Theme in similar
/// colors: the local fallback when the style's artwork can't download, which needs no download at
/// all. The chooser shows the styles' bundled preview cards, so nothing downloads until a look is
/// applied, and then only that style's artwork (art on demand). The full collection stays one
/// click away. Ids only: applying one is the Basic editor's own Apply Theme.
/// </summary>
internal static class CuratedLooks
{
    /// <summary>The curated Art Styles and their fallback Simple Themes, in the order shown.</summary>
    internal static readonly IReadOnlyList<(string StyleId, string FallbackThemeId)> Pairs =
    [
        ("af.style.high-fantasy-royal", "Royal"),
        ("af.style.crystarium-crystal", "Cool"),
        ("af.style.botanical-cottage", "Forest"),
        ("af.style.cyberpunk-neon", "Midnight"),
    ];

    /// <summary>The fallback for an Art Style that isn't curated (one picked from the full collection).</summary>
    internal const string DefaultFallbackThemeId = "Midnight";

    private static IReadOnlyList<ProfileThemePreset>? styles;

    /// <summary>The curated Art Styles this build has, in order (a missing id is skipped, never an error). Built once.</summary>
    internal static IReadOnlyList<ProfileThemePreset> Styles() => styles ??= FindStyles();

    private static List<ProfileThemePreset> FindStyles()
    {
        var found = new List<ProfileThemePreset>(Pairs.Count);
        foreach (var (styleId, _) in Pairs)
        {
            if (ProfileThemePresets.Find(styleId) is { IsArtStyle: true } style)
            {
                found.Add(style);
            }
        }

        return found;
    }

    /// <summary>The Simple Theme to offer when <paramref name="style"/>'s artwork can't download.</summary>
    internal static ProfileThemePreset? FallbackFor(ProfileThemePreset? style)
    {
        var id = DefaultFallbackThemeId;
        foreach (var (styleId, fallbackId) in Pairs)
        {
            if (style?.Id == styleId)
            {
                id = fallbackId;
                break;
            }
        }

        return ProfileThemePresets.Find(id) is { IsArtStyle: false } theme ? theme : null;
    }
}
