using System;
using System.Collections.Generic;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Editor;

/// <summary>
/// What the Basic editor's style browser shows: one system's catalog at a time, Art Styles or Simple
/// Themes (<see cref="Catalog"/>, issue #118), narrowed by a search and, for Simple Themes, an optional
/// family filter (from the catalog's own <see cref="ThemeFamily"/> metadata — the families that
/// actually have themes, in <see cref="ProfileThemePresets.FamilyOrder"/>). Pure: it only picks and
/// orders themes; applying one is unchanged (by its stable <see cref="ProfileThemePreset.Id"/>), and
/// nothing here reads or writes a Plate beyond telling which style it uses.
/// </summary>
internal static class ThemeBrowser
{
    /// <summary>
    /// The themes matching <paramref name="search"/> and <paramref name="family"/>, in catalog order.
    /// The search is case-insensitive and ignores surrounding spaces; every word of it must appear in
    /// a theme's name, description, or family. An empty search and no family (All) give the whole catalog.
    /// </summary>
    internal static List<ProfileThemePreset> Filter(IEnumerable<ProfileThemePreset> themes, string? search, ThemeFamily? family)
    {
        var words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = new List<ProfileThemePreset>();
        foreach (var theme in themes)
        {
            if (family is { } wanted && theme.Family != wanted)
            {
                continue;
            }

            if (Array.TrueForAll(words, word => Matches(theme, word)))
            {
                matches.Add(theme);
            }
        }

        return matches;
    }

    /// <summary>
    /// <see cref="Filter"/>'s results grouped by family: the families in the catalog's family order
    /// (<see cref="ProfileThemePresets.FamilyOrder"/>, then any family it doesn't list), each with its
    /// matching themes in catalog order. A family with no match is left out, so a search keeps its
    /// results under their families instead of mixing them together.
    /// </summary>
    internal static List<(ThemeFamily Family, List<ProfileThemePreset> Themes)> Group(IEnumerable<ProfileThemePreset> themes, string? search, ThemeFamily? family)
    {
        var matches = Filter(themes, search, family);
        var order = new List<ThemeFamily>(ProfileThemePresets.FamilyOrder);
        foreach (var theme in matches)
        {
            if (!order.Contains(theme.Family))
            {
                order.Add(theme.Family);
            }
        }

        var groups = new List<(ThemeFamily, List<ProfileThemePreset>)>();
        foreach (var candidate in order)
        {
            var members = matches.FindAll(theme => theme.Family == candidate);
            if (members.Count > 0)
            {
                groups.Add((candidate, members));
            }
        }

        return groups;
    }

    /// <summary>The families that have at least one theme, in the catalog's family order: the only filters worth offering.</summary>
    internal static List<(ThemeFamily Family, int Count)> Families(IReadOnlyCollection<ProfileThemePreset> themes)
    {
        var families = new List<(ThemeFamily, int)>();
        foreach (var family in ProfileThemePresets.FamilyOrder)
        {
            var count = 0;
            foreach (var theme in themes)
            {
                if (theme.Family == family)
                {
                    count++;
                }
            }

            if (count > 0)
            {
                families.Add((family, count));
            }
        }

        return families;
    }

    /// <summary>The style the Plate uses (its stored Basic theme id), or null — whatever the browser is showing.</summary>
    internal static ProfileThemePreset? Current(ProfileDocument profile) => PlateStyle.InUse(profile);

    /// <summary>One system's catalog, in catalog order: the Art Styles, or every Simple Theme.</summary>
    internal static IReadOnlyList<ProfileThemePreset> Catalog(StyleSystem system) =>
        system == StyleSystem.ArtStyle ? ArtSets.Styles : ProfileThemePresets.SimpleThemes;

    /// <summary>A system's name on its tab.</summary>
    internal static string SystemLabel(StyleSystem system) => system == StyleSystem.ArtStyle ? "Art Styles" : SimpleThemesLabel;

    /// <summary>
    /// While <paramref name="showing"/> isn't the system in use: what choosing from it does, naming
    /// the style in use, which is kept for later, and the system's own last choice (the marked card).
    /// Null while the system in use is shown, or with no style in use.
    /// </summary>
    internal static string? SwitchHint(ProfileDocument profile, StyleSystem showing)
    {
        if (PlateStyle.InUse(profile) is not { } inUse || PlateStyle.SystemInUse(profile) == showing)
        {
            return null;
        }

        var last = PlateStyle.Chosen(profile, showing);
        return showing == StyleSystem.ArtStyle
            ? $"Your Plate uses the Simple Theme {inUse.Name}. Choosing an Art Style switches to it; {inUse.Name} and your background are kept for later."
                + (last is null ? string.Empty : $" Your last Art Style, {last.Name}, is marked.")
            : $"Your Plate uses the Art Style {inUse.Name}. Choosing a Simple Theme takes its pieces away; {inUse.Name} is kept for later."
                + (last is null ? string.Empty : $" Your last Simple Theme, {last.Name}, is marked, and comes back with your background as you left it.");
    }

    /// <summary>How many theme cards fit side by side in <paramref name="width"/> (always at least one).</summary>
    internal static int Columns(float width, float cardWidth, float spacing) =>
        Math.Max(1, (int)((width + spacing) / Math.Max(1f, cardWidth + spacing)));

    /// <summary>A family's name as the browser shows it: "Art Styles" for the Art Styles, the family's
    /// own name for each family of Simple Themes.</summary>
    internal static string FamilyLabel(ThemeFamily family) => family == ThemeFamily.ArtStyle ? "Art Styles" : family.ToString();

    /// <summary>The name of every family but the Art Styles: their themes set colors only.</summary>
    internal const string SimpleThemesLabel = "Simple Themes";

    private static bool Matches(ProfileThemePreset theme, string word) =>
        theme.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
        || theme.Description.Contains(word, StringComparison.OrdinalIgnoreCase)
        || FamilyLabel(theme.Family).Contains(word, StringComparison.OrdinalIgnoreCase)
        || (!theme.IsArtStyle && SimpleThemesLabel.Contains(word, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The style browser's view state (system shown, search text and family filter): editor-only, never part of a Plate.</summary>
internal sealed class ThemeBrowserState
{
    internal const int MaxSearchLength = 64;

    /// <summary>The system browsed (Art Styles first); set from <see cref="PlateStyle.OpensOn"/> as each Plate is shown.</summary>
    internal StyleSystem Showing { get; set; }

    internal string Search { get; set; } = string.Empty;

    /// <summary>The Simple Themes' family filter; null is All (the default).</summary>
    internal ThemeFamily? Family { get; set; }

    /// <summary>The Plate the browser last showed, and whether its current theme still needs scrolling into view.</summary>
    internal Guid PlateId { get; set; }

    internal bool ScrollToCurrent { get; set; }

    internal bool IsFiltered => Family is not null || Search.Trim().Length > 0;

    internal void Clear()
    {
        Search = string.Empty;
        Family = null;
    }

    /// <summary>
    /// Called each frame with the Plate shown. Another Plate than last time opens on its own system
    /// (<see cref="PlateStyle.OpensOn"/>), unfiltered, search included, so a search left from the
    /// last Plate can't hide this one's choice, which is scrolled into view when it has one.
    /// </summary>
    internal void ShowPlate(ProfileDocument profile)
    {
        if (PlateId == profile.ProfileId)
        {
            return;
        }

        PlateId = profile.ProfileId;
        ShowSystem(profile, PlateStyle.OpensOn(profile));
    }

    /// <summary>Shows <paramref name="system"/>'s tab, unfiltered, on the Plate's choice there when it has one.</summary>
    internal void ShowSystem(ProfileDocument profile, StyleSystem system)
    {
        Showing = system;
        Clear();
        ScrollToCurrent = PlateStyle.Chosen(profile, system) is not null;
    }
}
