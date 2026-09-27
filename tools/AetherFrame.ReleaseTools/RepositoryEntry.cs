using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// One plugin's entry in a Dalamud custom repository (a pluginmaster.json is an array of these):
/// the fields of Dalamud's PluginManifest and RemotePluginManifest records, which Dalamud
/// deserializes with Newtonsoft.Json, in their declaration order. Every value type is nullable here
/// so a missing field can be told from a default when a file is read back.
/// </summary>
public sealed class RepositoryEntry
{
    public static readonly IReadOnlyList<string> KnownKeys = new[]
    {
        "Author", "Name", "Punchline", "Description", "Changelog", "Tags", "CategoryTags", "IsHide", "InternalName",
        "AssemblyVersion", "RepoUrl", "ApplicableVersion", "MinimumDalamudVersion", "DalamudApiLevel", "LastUpdate",
        "DownloadLinkInstall", "DownloadLinkUpdate", "DownloadLinkTesting", "LoadRequiredState", "LoadSync",
        "LoadPriority", "CanUnloadAsync", "ImageUrls", "IconUrl", "AcceptsFeedback", "FeedbackMessage",
        "TestingAssemblyVersion", "TestingDalamudApiLevel", "IsTestingExclusive", "TestingChangelog",
    };

    [JsonPropertyName("Author"), JsonPropertyOrder(0)]
    public string? Author { get; set; }

    [JsonPropertyName("Name"), JsonPropertyOrder(1)]
    public string? Name { get; set; }

    [JsonPropertyName("Punchline"), JsonPropertyOrder(2)]
    public string? Punchline { get; set; }

    [JsonPropertyName("Description"), JsonPropertyOrder(3)]
    public string? Description { get; set; }

    /// <summary>Shown in the installer to players who have the plugin installed.</summary>
    [JsonPropertyName("Changelog"), JsonPropertyOrder(4)]
    public string? Changelog { get; set; }

    [JsonPropertyName("Tags"), JsonPropertyOrder(5)]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("CategoryTags"), JsonPropertyOrder(6)]
    public List<string>? CategoryTags { get; set; }

    /// <summary>Hides the plugin from the installer's list. Always false here; a bad release is rolled back, not hidden.</summary>
    [JsonPropertyName("IsHide"), JsonPropertyOrder(7)]
    public bool? IsHide { get; set; }

    [JsonPropertyName("InternalName"), JsonPropertyOrder(8)]
    public string? InternalName { get; set; }

    /// <summary>The stable version, MAJOR.MINOR.PATCH.0. Dalamud refuses a package whose own manifest says otherwise.</summary>
    [JsonPropertyName("AssemblyVersion"), JsonPropertyOrder(9)]
    public string? AssemblyVersion { get; set; }

    [JsonPropertyName("RepoUrl"), JsonPropertyOrder(10)]
    public string? RepoUrl { get; set; }

    [JsonPropertyName("ApplicableVersion"), JsonPropertyOrder(11)]
    public string? ApplicableVersion { get; set; }

    [JsonPropertyName("MinimumDalamudVersion"), JsonPropertyOrder(12)]
    public string? MinimumDalamudVersion { get; set; }

    /// <summary>Dalamud installs and updates only entries at its own API level, and lists the previous one as outdated.</summary>
    [JsonPropertyName("DalamudApiLevel"), JsonPropertyOrder(13)]
    public int? DalamudApiLevel { get; set; }

    /// <summary>Unix seconds. Shown as the last update and used to sort the installer's list.</summary>
    [JsonPropertyName("LastUpdate"), JsonPropertyOrder(14)]
    public long? LastUpdate { get; set; }

    [JsonPropertyName("DownloadLinkInstall"), JsonPropertyOrder(15)]
    public string? DownloadLinkInstall { get; set; }

    [JsonPropertyName("DownloadLinkUpdate"), JsonPropertyOrder(16)]
    public string? DownloadLinkUpdate { get; set; }

    [JsonPropertyName("DownloadLinkTesting"), JsonPropertyOrder(17)]
    public string? DownloadLinkTesting { get; set; }

    [JsonPropertyName("LoadRequiredState"), JsonPropertyOrder(18)]
    public int? LoadRequiredState { get; set; }

    [JsonPropertyName("LoadSync"), JsonPropertyOrder(19)]
    public bool? LoadSync { get; set; }

    [JsonPropertyName("LoadPriority"), JsonPropertyOrder(20)]
    public int? LoadPriority { get; set; }

    [JsonPropertyName("CanUnloadAsync"), JsonPropertyOrder(21)]
    public bool? CanUnloadAsync { get; set; }

    [JsonPropertyName("ImageUrls"), JsonPropertyOrder(22)]
    public List<string>? ImageUrls { get; set; }

    [JsonPropertyName("IconUrl"), JsonPropertyOrder(23)]
    public string? IconUrl { get; set; }

    [JsonPropertyName("AcceptsFeedback"), JsonPropertyOrder(24)]
    public bool? AcceptsFeedback { get; set; }

    [JsonPropertyName("FeedbackMessage"), JsonPropertyOrder(25)]
    public string? FeedbackMessage { get; set; }

    /// <summary>The testing version. Offered only to players who opted into testing builds of this plugin, and only when it is newer than the stable version.</summary>
    [JsonPropertyName("TestingAssemblyVersion"), JsonPropertyOrder(26)]
    public string? TestingAssemblyVersion { get; set; }

    /// <summary>Required with TestingAssemblyVersion; without it Dalamud never uses the testing version.</summary>
    [JsonPropertyName("TestingDalamudApiLevel"), JsonPropertyOrder(27)]
    public int? TestingDalamudApiLevel { get; set; }

    /// <summary>When true, only players who opted into testing builds see the plugin at all.</summary>
    [JsonPropertyName("IsTestingExclusive"), JsonPropertyOrder(28)]
    public bool? IsTestingExclusive { get; set; }

    [JsonPropertyName("TestingChangelog"), JsonPropertyOrder(29)]
    public string? TestingChangelog { get; set; }
}

/// <summary>A pluginmaster.json: the array of entries Dalamud fetches, as deterministic bytes.</summary>
public static class RepositoryDocument
{
    private static readonly HashSet<string> Known = new(RepositoryEntry.KnownKeys, StringComparer.Ordinal);

    public static byte[] Serialize(IReadOnlyList<RepositoryEntry> entries) => StrictJson.Serialize(entries);

    public static IReadOnlyList<RepositoryEntry> Parse(byte[] utf8, string what) => StrictJson.ReadArrayOfObjects<RepositoryEntry>(utf8, what, Known);
}
