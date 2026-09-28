using System;
using System.Text.RegularExpressions;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// Where a publication is written: the branch of the source repository that the configured
/// pluginMasterUrl serves, as one of GitHub's two raw file addresses:
/// <c>https://raw.githubusercontent.com/OWNER/REPO/BRANCH/pluginmaster.json</c> or
/// <c>https://raw.githubusercontent.com/OWNER/REPO/refs/heads/BRANCH/pluginmaster.json</c>.
/// Players add that URL to Dalamud, so the publication may write nowhere else: not another
/// repository, not a file below the branch root, and never a branch that holds the source code.
/// </summary>
public sealed class PublicationTarget
{
    public const string FileName = "pluginmaster.json";

    private const string RawHost = "raw.githubusercontent.com";

    // Lowercase words joined by single hyphens: a name git, a URL and a shell all read the same way.
    private static readonly Regex BranchPattern = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex SourcePattern = new(@"^https://github\.com/([A-Za-z0-9-]+)/([A-Za-z0-9._-]+)$", RegexOptions.CultureInvariant);

    // Branches that hold source code, and the path word GitHub reads as the start of refs/heads/.
    private static readonly string[] ForbiddenBranches = { "master", "main", "refs" };

    private PublicationTarget(string owner, string repository, string branch, string url)
    {
        Owner = owner;
        Repository = repository;
        Branch = branch;
        Url = url;
    }

    public string Owner { get; }

    public string Repository { get; }

    /// <summary>The branch that holds only the published files.</summary>
    public string Branch { get; }

    /// <summary>The configured pluginMasterUrl: the address players add to Dalamud.</summary>
    public string Url { get; }

    public static PublicationTarget FromConfiguration(RepositoryConfiguration config)
    {
        var source = SourcePattern.Match(config.SourceRepositoryUrl);
        if (!source.Success)
        {
            throw new ReleaseCheckException($"sourceRepositoryUrl '{config.SourceRepositoryUrl}' is not https://github.com/OWNER/REPO; the publication branch lives in that repository.");
        }

        var owner = source.Groups[1].Value;
        var repository = source.Groups[2].Value;
        var uri = Urls.ValidateHttps(config.PluginMasterUrl, "pluginMasterUrl");
        if (uri.Host != RawHost)
        {
            throw new ReleaseCheckException($"pluginMasterUrl '{config.PluginMasterUrl}' is not served from {RawHost}; the publication workflow can only write a branch of {config.SourceRepositoryUrl} that GitHub serves there.");
        }

        // "/OWNER/REPO/BRANCH/pluginmaster.json" or "/OWNER/REPO/refs/heads/BRANCH/pluginmaster.json".
        var segments = uri.AbsolutePath.Split('/');
        string? branch = null;
        if (segments.Length == 5 && segments[4] == FileName)
        {
            branch = segments[3];
        }
        else if (segments.Length == 7 && segments[3] == "refs" && segments[4] == "heads" && segments[6] == FileName)
        {
            branch = segments[5];
        }

        if (branch is null || segments[1] != owner || segments[2] != repository)
        {
            throw new ReleaseCheckException($"pluginMasterUrl '{config.PluginMasterUrl}' is not https://{RawHost}/{owner}/{repository}/[refs/heads/]BRANCH/{FileName}, the root of a branch in the source repository.");
        }

        if (!BranchPattern.IsMatch(branch) || branch.Length > 64 || Array.IndexOf(ForbiddenBranches, branch) >= 0)
        {
            throw new ReleaseCheckException($"pluginMasterUrl names the branch '{branch}'; the publication branch must be lowercase words joined by hyphens, and not master, main or refs, so that it never holds source code.");
        }

        return new PublicationTarget(owner, repository, branch, config.PluginMasterUrl);
    }

    /// <summary>The configured target, refusing any branch but the one players' URL reads.</summary>
    public static PublicationTarget Resolve(RepositoryConfiguration config, string? branch)
    {
        var target = FromConfiguration(config);
        if (branch != target.Branch)
        {
            throw new ReleaseCheckException($"the publication branch '{branch}' is not '{target.Branch}', the branch pluginMasterUrl ({target.Url}) serves. Change both together, and only before the first publication.");
        }

        return target;
    }
}
