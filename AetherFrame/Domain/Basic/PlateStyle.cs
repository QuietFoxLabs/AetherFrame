using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Basic;

/// <summary>The two ways to style a Plate (issue #118).</summary>
public enum StyleSystem
{
    /// <summary>A whole look: artwork pieces with text colors to match.</summary>
    ArtStyle,

    /// <summary>Colors only: the Plate's own background and every Basic text color.</summary>
    SimpleTheme,
}

/// <summary>
/// A Plate's style as both editors read it (issue #118). Art Styles and Simple Themes are two
/// systems, each with its own choice: the one in use (<see cref="BasicPlateSettings.ThemeId"/>, as
/// every build has read it) and the other one's last choice, kept so going back to it finds it where
/// it was left (<see cref="BasicPlateSettings.ArtStyleId"/>, <see cref="BasicPlateSettings.SimpleThemeId"/>).
/// A Plate saved before those existed has only the one in use, so its other system has no choice yet.
/// What choosing each one changes is <c>BasicPlateEditor.ApplyTheme</c>'s.
/// </summary>
public static class PlateStyle
{
    /// <summary>The style the Plate uses (an Art Style or a Simple Theme), or null when none is chosen
    /// or this build doesn't know it.</summary>
    public static ProfileThemePreset? InUse(ProfileDocument profile) => ProfileThemePresets.Find(profile.BasicPlate?.ThemeId);

    /// <summary>The system in use: Art Style while an Art Style is, else Simple Theme (also with none chosen).</summary>
    public static StyleSystem SystemInUse(ProfileDocument profile) => InUse(profile) is { IsArtStyle: true } ? StyleSystem.ArtStyle : StyleSystem.SimpleTheme;

    /// <summary>The Art Style in use, or null.</summary>
    public static ProfileThemePreset? ArtStyleInUse(ProfileDocument profile) => InUse(profile) is { IsArtStyle: true } style ? style : null;

    /// <summary>The Simple Theme in use, or null.</summary>
    public static ProfileThemePreset? SimpleThemeInUse(ProfileDocument profile) => InUse(profile) is { IsArtStyle: false } theme ? theme : null;

    /// <summary>The Plate's Art Style: the one in use, else the last one chosen (kept while a Simple Theme is in use), or null.</summary>
    public static ProfileThemePreset? ChosenArtStyle(ProfileDocument profile) =>
        ArtStyleInUse(profile) ?? (ProfileThemePresets.Find(profile.BasicPlate?.ArtStyleId) is { IsArtStyle: true } style ? style : null);

    /// <summary>The Plate's Simple Theme: the one in use, else the last one chosen (kept while an Art Style is in use), or null.</summary>
    public static ProfileThemePreset? ChosenSimpleTheme(ProfileDocument profile) =>
        SimpleThemeInUse(profile) ?? (ProfileThemePresets.Find(profile.BasicPlate?.SimpleThemeId) is { IsArtStyle: false } theme ? theme : null);

    /// <summary>The Plate's choice in <paramref name="system"/> (see <see cref="ChosenArtStyle"/>, <see cref="ChosenSimpleTheme"/>).</summary>
    public static ProfileThemePreset? Chosen(ProfileDocument profile, StyleSystem system) =>
        system == StyleSystem.ArtStyle ? ChosenArtStyle(profile) : ChosenSimpleTheme(profile);

    /// <summary>
    /// The system the style browser opens on. Art Styles come first: the browser opens on them unless
    /// the Plate uses a Simple Theme the player chose, that is one chosen in this build, or one other than
    /// the starter theme every new Plate begins with.
    /// </summary>
    public static StyleSystem OpensOn(ProfileDocument profile) =>
        SimpleThemeInUse(profile) is { } theme
        && (profile.BasicPlate?.SimpleThemeId is not null || theme.Id != ProfileThemePresets.All[0].Id)
            ? StyleSystem.SimpleTheme
            : StyleSystem.ArtStyle;

    /// <summary>The style in use, in a few words: "Art Style: Celestial Sakura", "Simple Theme: Royal", "No style chosen".</summary>
    public static string Describe(ProfileDocument profile) => InUse(profile) switch
    {
        { IsArtStyle: true } style => $"Art Style: {style.Name}",
        { } theme => $"Simple Theme: {theme.Name}",
        null => "No style chosen",
    };
}
