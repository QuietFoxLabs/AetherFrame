using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// The address the source repository had before it moved, and the last version released there
/// (docs/CustomRepository.md, "Repository move"). History only: a package of that version or older
/// names the old address in its manifest, and the file published before the move links to it.
/// </summary>
public sealed record PreviousAddress(string SourceRepositoryUrl, DownloadUrlTemplate DownloadUrlTemplate, ProductVersion LastVersion);

/// <summary>Which flavour of the plugin a release carries (distribution/repository.json's releaseFlavour).</summary>
public enum ReleaseFlavour
{
    /// <summary>No sharing code at all (decisions D9b and P2 before October 1, 2026).</summary>
    Player,

    /// <summary>
    /// The sharing build: the protocol, the personas and the sharing client compiled in, sharing off
    /// until the player turns it on (V1). The owner's direction of October 1, 2026: "Testing channel
    /// gets sharing".
    /// </summary>
    Sharing,
}

/// <summary>
/// The explicit, reviewed facts the repository metadata is derived from (distribution/repository.json).
/// Nothing about where a package is downloaded from or which plugin it is comes from anywhere else:
/// not from the build machine, not from the environment, not from the package file itself.
/// </summary>
public sealed class RepositoryConfiguration
{
    private static readonly Regex InternalNamePattern = new(@"^[A-Za-z][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    private static readonly string[] KnownKeys =
    {
        "$comment", "internalName", "dalamudApiLevel", "sourceRepositoryUrl", "pluginMasterUrl", "downloadUrlTemplate",
        "previousSourceRepositoryUrl", "previousDownloadUrlTemplate", "previousAddressLastVersion", "releaseFlavour",
    };

    private RepositoryConfiguration(string internalName, int dalamudApiLevel, string sourceRepositoryUrl, string pluginMasterUrl, DownloadUrlTemplate downloadUrlTemplate, PreviousAddress? previous, ReleaseFlavour flavour)
    {
        Flavour = flavour;
        InternalName = internalName;
        DalamudApiLevel = dalamudApiLevel;
        SourceRepositoryUrl = sourceRepositoryUrl;
        PluginMasterUrl = pluginMasterUrl;
        DownloadUrlTemplate = downloadUrlTemplate;
        Previous = previous;
    }

    /// <summary>The plugin's internal name: its assembly name, its DLL and manifest file names, and the key Dalamud tracks it by.</summary>
    public string InternalName { get; }

    /// <summary>The Dalamud API level every package and every repository entry must declare.</summary>
    public int DalamudApiLevel { get; }

    /// <summary>The public source repository, which the packaged manifest's RepoUrl must equal.</summary>
    public string SourceRepositoryUrl { get; }

    /// <summary>
    /// Where the generated pluginmaster.json will be served from. Players add this URL to Dalamud, and
    /// Dalamud offers updates only from the exact URL a plugin was installed from, so it must never change.
    /// </summary>
    public string PluginMasterUrl { get; }

    public DownloadUrlTemplate DownloadUrlTemplate { get; }

    /// <summary>
    /// The source repository's address before it moved, or null if it never moved. Everything generated
    /// uses the current address; the previous one is accepted only where it is history.
    /// </summary>
    public PreviousAddress? Previous { get; }

    /// <summary>Which flavour every release package must be: <see cref="ReleaseFlavour.Player"/> when releaseFlavour is absent.</summary>
    public ReleaseFlavour Flavour { get; }

    public static RepositoryConfiguration Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ReleaseCheckException($"repository configuration not found at {path}.");
        }

        return Parse(File.ReadAllBytes(path), path);
    }

    public static RepositoryConfiguration Parse(byte[] utf8, string what)
    {
        var raw = StrictJson.ReadObject<RawConfiguration>(utf8, what, KnownKeys);

        if (raw.InternalName is null || !InternalNamePattern.IsMatch(raw.InternalName))
        {
            throw new ReleaseCheckException($"{what}: internalName '{raw.InternalName}' must be a letter followed by up to 63 letters, digits, '.', '_' or '-'.");
        }

        if (raw.DalamudApiLevel is null or < 1 or > 999)
        {
            throw new ReleaseCheckException($"{what}: dalamudApiLevel must be a positive whole number.");
        }

        var source = Urls.ValidateHttps(raw.SourceRepositoryUrl, $"{what}: sourceRepositoryUrl").OriginalString;
        var master = Urls.ValidateHttps(raw.PluginMasterUrl, $"{what}: pluginMasterUrl");
        if (!master.AbsolutePath.EndsWith(".json", StringComparison.Ordinal))
        {
            throw new ReleaseCheckException($"{what}: pluginMasterUrl '{raw.PluginMasterUrl}' must point at a .json file.");
        }

        var template = ParseTemplate(raw.DownloadUrlTemplate, what);
        var previous = ParsePrevious(raw, source, template, what);
        var flavour = raw.ReleaseFlavour switch
        {
            null or "player" => ReleaseFlavour.Player,
            "sharing" => ReleaseFlavour.Sharing,
            _ => throw new ReleaseCheckException($"{what}: releaseFlavour '{raw.ReleaseFlavour}' must be \"player\" or \"sharing\"."),
        };

        return new RepositoryConfiguration(raw.InternalName, raw.DalamudApiLevel.Value, source, master.OriginalString, template, previous, flavour);
    }

    /// <summary>The same configuration with another download URL template, for dry runs.</summary>
    public RepositoryConfiguration WithDownloadUrlTemplate(DownloadUrlTemplate template) =>
        new(InternalName, DalamudApiLevel, SourceRepositoryUrl, PluginMasterUrl, template, Previous, Flavour);

    /// <summary>The release package's file name, as New-ReleasePackage.ps1 stages it.</summary>
    public string PackageFileName(ProductVersion version) => $"{InternalName}-{version}.zip";

    public string DownloadUrl(ProductVersion version) => DownloadUrlTemplate.Resolve(InternalName, version, PackageFileName(version));

    /// <summary>True when <paramref name="repoUrl"/> is the previous source address and <paramref name="version"/> was released there.</summary>
    public bool IsPreviousRepoUrl(string? repoUrl, ProductVersion version) =>
        Previous is not null && repoUrl == Previous.SourceRepositoryUrl && version <= Previous.LastVersion;

    /// <summary>Where the file published before the move linked <paramref name="version"/>'s package; null for a version released after the move.</summary>
    public string? PreviousDownloadUrl(ProductVersion version) =>
        Previous is not null && version <= Previous.LastVersion
            ? Previous.DownloadUrlTemplate.Resolve(InternalName, version, PackageFileName(version))
            : null;

    private static DownloadUrlTemplate ParseTemplate(string? text, string what)
    {
        try
        {
            return DownloadUrlTemplate.Parse(text);
        }
        catch (ReleaseCheckException e)
        {
            throw new ReleaseCheckException($"{what}: {e.Message}");
        }
    }

    private static PreviousAddress? ParsePrevious(RawConfiguration raw, string source, DownloadUrlTemplate template, string what)
    {
        var given = new[] { raw.PreviousSourceRepositoryUrl, raw.PreviousDownloadUrlTemplate, raw.PreviousAddressLastVersion }.Count(v => v is not null);
        if (given == 0)
        {
            return null;
        }

        if (given != 3)
        {
            throw new ReleaseCheckException($"{what}: previousSourceRepositoryUrl, previousDownloadUrlTemplate and previousAddressLastVersion are set together or not at all.");
        }

        var previousSource = Urls.ValidateHttps(raw.PreviousSourceRepositoryUrl, $"{what}: previousSourceRepositoryUrl").OriginalString;
        if (previousSource == source)
        {
            throw new ReleaseCheckException($"{what}: previousSourceRepositoryUrl is the current sourceRepositoryUrl; it names the address the repository moved from.");
        }

        var previousTemplate = ParseTemplate(raw.PreviousDownloadUrlTemplate, what);
        if (!previousTemplate.Text.StartsWith(previousSource + "/releases/download/", StringComparison.Ordinal))
        {
            throw new ReleaseCheckException($"{what}: previousDownloadUrlTemplate '{previousTemplate.Text}' is not a release download of the previous source repository {previousSource}.");
        }

        if (previousTemplate.Text == template.Text)
        {
            throw new ReleaseCheckException($"{what}: previousDownloadUrlTemplate is the current downloadUrlTemplate.");
        }

        var lastVersion = ProductVersion.Parse(raw.PreviousAddressLastVersion!, $"{what}: previousAddressLastVersion");
        return new PreviousAddress(previousSource, previousTemplate, lastVersion);
    }

    private sealed class RawConfiguration
    {
        [JsonPropertyName("$comment")]
        public string? Comment { get; set; }

        [JsonPropertyName("internalName")]
        public string? InternalName { get; set; }

        [JsonPropertyName("releaseFlavour")]
        public string? ReleaseFlavour { get; set; }

        [JsonPropertyName("dalamudApiLevel")]
        public int? DalamudApiLevel { get; set; }

        [JsonPropertyName("sourceRepositoryUrl")]
        public string? SourceRepositoryUrl { get; set; }

        [JsonPropertyName("pluginMasterUrl")]
        public string? PluginMasterUrl { get; set; }

        [JsonPropertyName("downloadUrlTemplate")]
        public string? DownloadUrlTemplate { get; set; }

        [JsonPropertyName("previousSourceRepositoryUrl")]
        public string? PreviousSourceRepositoryUrl { get; set; }

        [JsonPropertyName("previousDownloadUrlTemplate")]
        public string? PreviousDownloadUrlTemplate { get; set; }

        [JsonPropertyName("previousAddressLastVersion")]
        public string? PreviousAddressLastVersion { get; set; }
    }
}
