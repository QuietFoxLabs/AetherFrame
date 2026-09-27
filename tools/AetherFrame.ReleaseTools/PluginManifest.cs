using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// The manifest DalamudPackager writes beside the DLL and into the package (AetherFrame.json): the
/// fields of DalamudPackager's Manifest class, which Dalamud reads as its PluginManifest. Keys
/// Dalamud adds to a manifest after installing a plugin locally are refused, since a packaged
/// manifest must never carry them.
/// </summary>
public sealed class PluginManifest
{
    public static readonly IReadOnlyList<string> KnownKeys = new[]
    {
        "Author", "Name", "InternalName", "AssemblyVersion", "MinimumDalamudVersion", "Description",
        "ApplicableVersion", "RepoUrl", "Tags", "CategoryTags", "DalamudApiLevel", "LoadRequiredState",
        "LoadSync", "CanUnloadAsync", "LoadPriority", "ImageUrls", "IconUrl", "Punchline", "Changelog",
        "AcceptsFeedback", "FeedbackMessage",
    };

    /// <summary>Keys Dalamud writes into an installed plugin's manifest; their presence means the file is not a build output.</summary>
    public static readonly IReadOnlyDictionary<string, string> LocalOnlyKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WorkingPluginId"] = "'WorkingPluginId' is assigned by Dalamud at install time, and Dalamud refuses a package that carries one.",
        ["InstalledFromUrl"] = "'InstalledFromUrl' belongs to an installed plugin, not a package.",
        ["Disabled"] = "'Disabled' belongs to an installed plugin, not a package.",
        ["Testing"] = "'Testing' belongs to an installed plugin, not a package.",
        ["ScheduledForDeletion"] = "'ScheduledForDeletion' belongs to an installed plugin, not a package.",
    };

    [JsonPropertyName("Author")]
    public string? Author { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("InternalName")]
    public string? InternalName { get; set; }

    [JsonPropertyName("AssemblyVersion")]
    public string? AssemblyVersion { get; set; }

    [JsonPropertyName("MinimumDalamudVersion")]
    public string? MinimumDalamudVersion { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("ApplicableVersion")]
    public string? ApplicableVersion { get; set; }

    [JsonPropertyName("RepoUrl")]
    public string? RepoUrl { get; set; }

    [JsonPropertyName("Tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("CategoryTags")]
    public List<string>? CategoryTags { get; set; }

    [JsonPropertyName("DalamudApiLevel")]
    public int? DalamudApiLevel { get; set; }

    [JsonPropertyName("LoadRequiredState")]
    public int? LoadRequiredState { get; set; }

    [JsonPropertyName("LoadSync")]
    public bool? LoadSync { get; set; }

    [JsonPropertyName("CanUnloadAsync")]
    public bool? CanUnloadAsync { get; set; }

    [JsonPropertyName("LoadPriority")]
    public int? LoadPriority { get; set; }

    [JsonPropertyName("ImageUrls")]
    public List<string>? ImageUrls { get; set; }

    [JsonPropertyName("IconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("Punchline")]
    public string? Punchline { get; set; }

    [JsonPropertyName("Changelog")]
    public string? Changelog { get; set; }

    [JsonPropertyName("AcceptsFeedback")]
    public bool? AcceptsFeedback { get; set; }

    [JsonPropertyName("FeedbackMessage")]
    public string? FeedbackMessage { get; set; }

    public static PluginManifest Parse(byte[] utf8, string what)
    {
        var known = new HashSet<string>(KnownKeys, StringComparer.Ordinal);
        return StrictJson.ReadObject<PluginManifest>(utf8, what, known, LocalOnlyKeys);
    }
}
