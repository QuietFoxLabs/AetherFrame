using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AetherFrame.ReleaseTools;

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
    };

    private RepositoryConfiguration(string internalName, int dalamudApiLevel, string sourceRepositoryUrl, string pluginMasterUrl, DownloadUrlTemplate downloadUrlTemplate)
    {
        InternalName = internalName;
        DalamudApiLevel = dalamudApiLevel;
        SourceRepositoryUrl = sourceRepositoryUrl;
        PluginMasterUrl = pluginMasterUrl;
        DownloadUrlTemplate = downloadUrlTemplate;
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

        DownloadUrlTemplate template;
        try
        {
            template = DownloadUrlTemplate.Parse(raw.DownloadUrlTemplate);
        }
        catch (ReleaseCheckException e)
        {
            throw new ReleaseCheckException($"{what}: {e.Message}");
        }

        return new RepositoryConfiguration(raw.InternalName, raw.DalamudApiLevel.Value, source, master.OriginalString, template);
    }

    /// <summary>The same configuration with another download URL template, for dry runs.</summary>
    public RepositoryConfiguration WithDownloadUrlTemplate(DownloadUrlTemplate template) =>
        new(InternalName, DalamudApiLevel, SourceRepositoryUrl, PluginMasterUrl, template);

    /// <summary>The release package's file name, as New-ReleasePackage.ps1 stages it.</summary>
    public string PackageFileName(ProductVersion version) => $"{InternalName}-{version}.zip";

    public string DownloadUrl(ProductVersion version) => DownloadUrlTemplate.Resolve(InternalName, version, PackageFileName(version));

    private sealed class RawConfiguration
    {
        [JsonPropertyName("$comment")]
        public string? Comment { get; set; }

        [JsonPropertyName("internalName")]
        public string? InternalName { get; set; }

        [JsonPropertyName("dalamudApiLevel")]
        public int? DalamudApiLevel { get; set; }

        [JsonPropertyName("sourceRepositoryUrl")]
        public string? SourceRepositoryUrl { get; set; }

        [JsonPropertyName("pluginMasterUrl")]
        public string? PluginMasterUrl { get; set; }

        [JsonPropertyName("downloadUrlTemplate")]
        public string? DownloadUrlTemplate { get; set; }
    }
}
