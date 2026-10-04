using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// Basic mode's Adventure Plate settings: what the section elements themselves can't express.
/// Every section's content still lives in ordinary role-tagged elements (see
/// <see cref="ProfileElementRole"/>), fully editable in the Advanced editor; this only holds the
/// structured data behind some of them (playstyles, active hours, the chosen job) and Basic's own
/// layout bookkeeping.
///
/// Null on <see cref="ProfileDocument.BasicPlate"/> (every profile before this existed) means
/// "not configured": Basic mode binds to whatever section elements already exist, exactly where
/// they are, and writes nothing until the user makes an explicit Basic edit.
/// </summary>
public sealed class BasicPlateSettings
{
    public const int MaxPlaystyles = 6;
    public const int MaxPlaystyleLength = 24;

    // The lists and the theme id are never null, and the lists hold no null entry: an explicit
    // JSON null (which no build writes) reads as empty — or, for one entry, as that entry left
    // out — so cloning, comparing and drawing them never has to guard against it.
    private List<BasicPlacement> placements = new();
    private List<string> playstyles = new();
    private List<uint> favoriteJobIds = new();
    private string themeId = string.Empty;
    private string? artStyleId;
    private string? simpleThemeId;

    public AdventurePlateOrientation Orientation { get; set; } = AdventurePlateOrientation.Normal;

    /// <summary>Where the portrait comes from. Only <see cref="BasicPortraitSource.ImportedImage"/> works today.</summary>
    public BasicPortraitSource PortraitSource { get; set; } = BasicPortraitSource.ImportedImage;

    /// <summary>
    /// Where Basic mode last placed each section element (not the Identity Header, which keeps its
    /// own record in <see cref="BasicIdentityHeader.AppliedLayout"/>). An element still sitting
    /// exactly there follows the Basic layout; once it has been moved or resized elsewhere (the
    /// Advanced editor) it counts as customized, and Basic never moves it again on its own. A list
    /// keyed by the numeric role rather than a dictionary, so a role this build doesn't know can
    /// never make the document fail to load.
    /// </summary>
    public List<BasicPlacement> Placements
    {
        get => placements;
        set => placements = WithoutNullEntries(value);
    }

    /// <summary>Up to <see cref="MaxPlaystyles"/> entries, in display order.</summary>
    public List<string> Playstyles
    {
        get => playstyles;
        set => playstyles = WithoutNullEntries(value);
    }

    /// <summary>Structured Active Hours, or null when never set.</summary>
    public BasicActiveHours? ActiveHours { get; set; }

    /// <summary>
    /// The primary Favorite Job's row id (game data), or 0 for none. Before multiple Favorite Jobs
    /// this was the only one; it's still written (always the first of <see cref="FavoriteJobIds"/>)
    /// so an older build opening the Plate sees the same primary job.
    /// </summary>
    public uint FavoriteJobId { get; set; }

    /// <summary>
    /// The Favorite Jobs' row ids (game data), in the player's order — the first is the primary
    /// favorite. Empty on a Plate saved before multiple Favorite Jobs: its single
    /// <see cref="FavoriteJobId"/> is read as a one-job list (see <c>BasicFavoriteJobs.IdsOf</c>).
    /// </summary>
    public List<uint> FavoriteJobIds
    {
        get => favoriteJobIds;
        set => favoriteJobIds = value ?? new List<uint>();
    }

    /// <summary>
    /// The level an earlier version showed beside the Favorite Job, or 0 for none. No longer part
    /// of Basic (which doesn't show or edit a level); kept exactly as saved, never stripped.
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Stable <see cref="ProfileThemePreset.Id"/> of the style the Plate uses ("" for none): the Art
    /// Style in use, else the Simple Theme (issue #118; see <c>PlateStyle</c>). The colors of
    /// Components without their own follow it, and Reset Section's colors default to it; nothing
    /// references the preset otherwise, and every color stays editable. The JSON property name
    /// (<c>ThemeName</c>) predates the Id/Name split and is kept as-is so no existing Plate needs
    /// rewriting; every value already stored there is a theme's Id (Id was defined equal to Name for
    /// every theme that shipped before the split existed). Earlier builds read it unchanged.
    /// </summary>
    [JsonPropertyName("ThemeName")]
    public string ThemeId
    {
        get => themeId;
        set => themeId = value ?? string.Empty;
    }

    /// <summary>
    /// The Art Style the player last chose (null for none; an empty value reads as none), kept while
    /// a Simple Theme is in use so it can be chosen again (issue #118). Written only by a choice; a
    /// Plate saved before it existed reads its Art Style from <see cref="ThemeId"/> (see
    /// <c>PlateStyle</c>). Not written while null, so every Plate saved before it stays byte for byte
    /// as it was; earlier builds keep it unread (<see cref="ExtensionData"/>).
    /// </summary>
    [JsonPropertyName("ArtStyle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtStyleId
    {
        get => artStyleId;
        set => artStyleId = string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>The Simple Theme the player last chose (null for none), kept while an Art Style is in
    /// use, as <see cref="ArtStyleId"/> is the other way round.</summary>
    [JsonPropertyName("SimpleTheme")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SimpleThemeId
    {
        get => simpleThemeId;
        set => simpleThemeId = string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Properties this build doesn't know, kept through clone and save unchanged.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public ElementRect? GetPlacement(ProfileElementRole role)
    {
        foreach (var placement in Placements)
        {
            if (placement.Role == role)
            {
                return placement.Rect;
            }
        }

        return null;
    }

    public void SetPlacement(ProfileElementRole role, ElementRect rect)
    {
        foreach (var placement in Placements)
        {
            if (placement.Role == role)
            {
                placement.Rect = rect;
                return;
            }
        }

        Placements.Add(new BasicPlacement { Role = role, Rect = rect });
    }

    public void RemovePlacement(ProfileElementRole role) => Placements.RemoveAll(p => p.Role == role);

    public BasicPlateSettings Clone()
    {
        var clone = new BasicPlateSettings
        {
            Orientation = Orientation,
            PortraitSource = PortraitSource,
            Playstyles = new List<string>(Playstyles),
            ActiveHours = ActiveHours?.Clone(),
            FavoriteJobId = FavoriteJobId,
            FavoriteJobIds = new List<uint>(FavoriteJobIds),
            Level = Level,
            ThemeId = ThemeId,
            ArtStyleId = ArtStyleId,
            SimpleThemeId = SimpleThemeId,
            ExtensionData = ProfileElement.CopyExtensionData(ExtensionData),
        };

        foreach (var placement in Placements)
        {
            clone.Placements.Add(placement.Clone());
        }

        return clone;
    }

    public bool ContentEquals(BasicPlateSettings? other)
    {
        if (other is null
            || Orientation != other.Orientation
            || PortraitSource != other.PortraitSource
            || FavoriteJobId != other.FavoriteJobId
            || Level != other.Level
            || ThemeId != other.ThemeId
            || ArtStyleId != other.ArtStyleId
            || SimpleThemeId != other.SimpleThemeId
            || (ActiveHours is null ? other.ActiveHours is not null : !ActiveHours.ContentEquals(other.ActiveHours))
            || Placements.Count != other.Placements.Count
            || Playstyles.Count != other.Playstyles.Count
            || FavoriteJobIds.Count != other.FavoriteJobIds.Count)
        {
            return false;
        }

        for (var i = 0; i < FavoriteJobIds.Count; i++)
        {
            if (FavoriteJobIds[i] != other.FavoriteJobIds[i])
            {
                return false;
            }
        }

        for (var i = 0; i < Playstyles.Count; i++)
        {
            if (Playstyles[i] != other.Playstyles[i])
            {
                return false;
            }
        }

        for (var i = 0; i < Placements.Count; i++)
        {
            if (Placements[i].Role != other.Placements[i].Role || Placements[i].Rect != other.Placements[i].Rect)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Drops every placement whose rectangle holds a value that isn't a number (NaN or infinity,
    /// from a hand-edited file): Basic mode then treats that element as never placed by it — as
    /// customized — instead of comparing it against garbage. Finite values are never changed. See
    /// <see cref="ProfileElementLimits"/>.
    /// </summary>
    /// <returns>True if a repair was applied.</returns>
    internal bool NormalizeValues() => Placements.RemoveAll(placement => !ProfileElementLimits.IsFinite(placement.Rect)) > 0;

    /// <summary>The list itself, unless it is null (then empty) or holds a null entry (then a copy
    /// without it) — neither of which any build writes, so a list a build wrote is kept as is.</summary>
    private static List<T> WithoutNullEntries<T>(List<T>? entries)
        where T : class
    {
        if (entries is null)
        {
            return new List<T>();
        }

        return entries.Exists(entry => entry is null) ? entries.FindAll(entry => entry is not null) : entries;
    }
}

/// <summary>Where Basic mode last placed one section element.</summary>
public sealed class BasicPlacement
{
    public ProfileElementRole Role { get; set; }

    public ElementRect Rect { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public BasicPlacement Clone() => new() { Role = Role, Rect = Rect, ExtensionData = ProfileElement.CopyExtensionData(ExtensionData) };
}

/// <summary>
/// When the character is usually around, as a Plate shows it: which days, a start and end time of
/// day, and an optional time zone label. Purely descriptive local data — never connected to online
/// status, scheduling, or location.
/// </summary>
public sealed class BasicActiveHours
{
    public const int MinutesPerDay = 24 * 60;
    public const int MaxTimeZoneLength = 16;

    private string timeZone = string.Empty;

    public BasicWeekdays Days { get; set; } = BasicWeekdays.None;

    /// <summary>Minutes after midnight, [0, 1440).</summary>
    public int StartMinutes { get; set; } = 20 * 60;

    /// <summary>Minutes after midnight, [0, 1440). Earlier than the start means "past midnight";
    /// equal to the start means "all day".</summary>
    public int EndMinutes { get; set; } = 23 * 60;

    public bool Use24HourClock { get; set; }

    /// <summary>Free text such as "EST" or "Server Time", up to <see cref="MaxTimeZoneLength"/>.
    /// Never null: an explicit JSON null (which no build writes) reads as empty.</summary>
    public string TimeZone
    {
        get => timeZone;
        set => timeZone = value ?? string.Empty;
    }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public BasicActiveHours Clone() => new()
    {
        Days = Days,
        StartMinutes = StartMinutes,
        EndMinutes = EndMinutes,
        Use24HourClock = Use24HourClock,
        TimeZone = TimeZone,
        ExtensionData = ProfileElement.CopyExtensionData(ExtensionData),
    };

    public bool ContentEquals(BasicActiveHours? other) =>
        other is not null
        && Days == other.Days
        && StartMinutes == other.StartMinutes
        && EndMinutes == other.EndMinutes
        && Use24HourClock == other.Use24HourClock
        && TimeZone == other.TimeZone;

    internal static int NormalizeMinutes(int minutes) => ((minutes % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;
}

/// <summary>Days of the week. Persisted numerically.</summary>
[Flags]
public enum BasicWeekdays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekends = Saturday | Sunday,
    Everyday = Weekdays | Weekends,
}

/// <summary>Adventure Plate Classic orientation. Persisted numerically; append only.</summary>
public enum AdventurePlateOrientation
{
    /// <summary>Portrait on the left, details on the right.</summary>
    Normal = 0,

    /// <summary>Portrait on the right, details on the left.</summary>
    Mirrored = 1,
}

/// <summary>Where the Basic portrait comes from. Persisted numerically; append only.</summary>
public enum BasicPortraitSource
{
    /// <summary>An image file imported into AetherFrame's managed assets.</summary>
    ImportedImage = 0,

    /// <summary>Reserved: the character's in-game portrait. Not available yet.</summary>
    CurrentPortrait = 1,

    /// <summary>Reserved: a scene composed in AetherFrame. Not available yet.</summary>
    AetherFrameScene = 2,
}
