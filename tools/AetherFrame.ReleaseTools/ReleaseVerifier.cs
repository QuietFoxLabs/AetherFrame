using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AetherFrame.ReleaseTools;

/// <summary>One asset of a GitHub Release, as the REST API describes it.</summary>
public sealed record GitHubAsset(long Id, string Name, string State, long Size, string? Digest, string BrowserDownloadUrl);

/// <summary>
/// A GitHub Release as <c>GET /repos/{owner}/{repo}/releases/tags/{tag}</c> returns it. The API has many
/// more fields and adds new ones over time, so unknown keys are ignored here; every field that is used
/// must be present with the right type.
/// </summary>
public sealed record GitHubRelease(long Id, string TagName, bool Draft, bool Prerelease, string? PublishedAt, string HtmlUrl, IReadOnlyList<GitHubAsset> Assets, bool? Immutable)
{
    public const long MaxBytes = 4 * 1024 * 1024;

    public static GitHubRelease Parse(byte[] utf8, string what)
    {
        if (utf8.Length > MaxBytes)
        {
            throw new ReleaseCheckException($"{what} is {utf8.Length} bytes; a release description is far smaller.");
        }

        using var document = StrictJson.ParseDocument(utf8, what);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ReleaseCheckException($"{what} is not a JSON object.");
        }

        var assets = new List<GitHubAsset>();
        var list = Property(root, "assets", JsonValueKind.Array, what);
        var index = 0;
        foreach (var asset in list.EnumerateArray())
        {
            var where = $"{what} asset {index++}";
            if (asset.ValueKind != JsonValueKind.Object)
            {
                throw new ReleaseCheckException($"{where} is not a JSON object.");
            }

            assets.Add(new GitHubAsset(
                Number(asset, "id", where),
                String(asset, "name", where),
                String(asset, "state", where),
                Number(asset, "size", where),
                NullableString(asset, "digest", where),
                String(asset, "browser_download_url", where)));
        }

        return new GitHubRelease(
            Number(root, "id", what),
            String(root, "tag_name", what),
            Boolean(root, "draft", what),
            Boolean(root, "prerelease", what),
            NullableString(root, "published_at", what),
            String(root, "html_url", what),
            assets,
            // Whether GitHub locks the release's assets and tag (repository setting "Enable release
            // immutability"). Recorded, not required; absent from older API responses.
            root.TryGetProperty("immutable", out var immutable) && immutable.ValueKind != JsonValueKind.Null ? Boolean(root, "immutable", what) : null);
    }

    private static JsonElement Property(JsonElement element, string name, JsonValueKind kind, string what)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != kind)
        {
            throw new ReleaseCheckException($"{what} has no {kind.ToString().ToLowerInvariant()} '{name}'.");
        }

        return value;
    }

    private static string String(JsonElement element, string name, string what) => Property(element, name, JsonValueKind.String, what).GetString()!;

    private static string? NullableString(JsonElement element, string name, string what) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null ? null : String(element, name, what);

    private static bool Boolean(JsonElement element, string name, string what)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ReleaseCheckException($"{what} has no boolean '{name}'.");
        }

        return value.GetBoolean();
    }

    private static long Number(JsonElement element, string name, string what)
    {
        if (!Property(element, name, JsonValueKind.Number, what).TryGetInt64(out var number) || number <= 0)
        {
            throw new ReleaseCheckException($"{what} has a '{name}' that is not a positive whole number.");
        }

        return number;
    }
}

/// <summary>What git says about a release tag (tag.json, written by .github/scripts/custom-repository.sh).</summary>
public sealed class TagFacts
{
    private static readonly string[] KnownKeys = { "tag", "objectType", "commit", "defaultBranch", "onDefaultBranch" };

    /// <summary>The tag's name, v&lt;version&gt;.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    /// <summary><c>git cat-file -t refs/tags/&lt;tag&gt;</c>: "tag" for an annotated tag, "commit" for a lightweight one.</summary>
    [JsonPropertyName("objectType")]
    public string? ObjectType { get; set; }

    /// <summary>The commit the tag points at, peeled.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    /// <summary>Whether the default branch contains the commit (<c>git merge-base --is-ancestor</c>).</summary>
    [JsonPropertyName("onDefaultBranch")]
    public bool? OnDefaultBranch { get; set; }

    public static TagFacts Parse(byte[] utf8, string what) => StrictJson.ReadObject<TagFacts>(utf8, what, KnownKeys);
}

/// <summary>A release that passed every check, with what the publication record says about it.</summary>
public sealed record VerifiedRelease(ProductVersion Version, GitHubRelease Release, string Commit, DateTimeOffset PublishedAt, PackageReport Package, string ChecksumsSha256);

/// <summary>
/// Checks one release before a publication describes it, from the directory
/// <c>custom-repository.sh fetch-release</c> fills: the GitHub Release (published, not a draft, of
/// this tag, in this repository, with exactly the ZIP and SHA256SUMS.txt), the downloaded assets
/// (the same bytes GitHub's digests and SHA256SUMS.txt name), the tag (annotated, on the default
/// branch), and the package against every rule of <see cref="PackageValidator"/>, with the version,
/// project file and changelog read from the tagged commit and the DLL built from that commit.
/// Nothing is trusted because it exists: every file is read and checked.
/// </summary>
public static class ReleaseVerifier
{
    public const string ReleaseFile = "release.json";
    public const string TagFile = "tag.json";
    public const string SourceDirectory = "source";

    private static readonly Regex Digest = new(@"^sha256:([0-9a-f]{64})$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha1 = new(@"^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

    /// <summary>The directory a release's files are fetched into, below the releases directory.</summary>
    public static string DirectoryFor(string releasesDirectory, ProductVersion version) => Path.Combine(releasesDirectory, version.Tag);

    /// <summary>Verifies the release; <paramref name="stableSlot"/> requires a full release, not a pre-release.</summary>
    public static VerifiedRelease Verify(string directory, ProductVersion version, RepositoryConfiguration config, bool stableSlot, CheckList checks)
    {
        var tag = version.Tag;
        var own = new CheckList();
        try
        {
            return Run(directory, version, tag, config, stableSlot, own);
        }
        finally
        {
            foreach (var check in own.Checks)
            {
                if (check.Passed)
                {
                    checks.Pass($"{tag}: {check.Name}", check.Detail);
                }
                else
                {
                    checks.Fail($"{tag}: {check.Name}", check.Detail);
                }
            }
        }
    }

    private static VerifiedRelease Run(string directory, ProductVersion version, string tag, RepositoryConfiguration config, bool stableSlot, CheckList checks)
    {
        if (!Directory.Exists(directory))
        {
            checks.Fail("release files", $"no {Path.GetFileName(directory)} directory; the release was not fetched.");
            throw new ReleaseCheckException($"{tag} was not fetched.");
        }

        // The GitHub Release.
        var release = checks.Attempt("GitHub Release", () => GitHubRelease.Parse(ReadFile(directory, ReleaseFile, GitHubRelease.MaxBytes), ReleaseFile), r => $"id {r.Id}");
        if (release is null)
        {
            throw new ReleaseCheckException($"{tag} has no readable release description.");
        }

        checks.Require(!release.Draft, "published", "not a draft", "the release is a draft. A draft is never published to the custom repository: publish the GitHub Release first, then run again.");
        checks.Require(release.TagName == tag, "release tag", release.TagName, $"the release is for '{release.TagName}', not {tag}.");
        var releasePage = $"{config.SourceRepositoryUrl}/releases/tag/{tag}";
        checks.Require(release.HtmlUrl == releasePage, "release page", release.HtmlUrl, $"is '{release.HtmlUrl}', expected '{releasePage}': the release must be the published release of {tag} in {config.SourceRepositoryUrl}.");
        var publishedAt = checks.Attempt("publish time", () => release.PublishedAt is null
            ? throw new ReleaseCheckException("the release has no publish time; it has not been published.")
            : (DateTimeOffset?)RepositoryGenerator.ParseLastUpdate(release.PublishedAt), t => t!.Value.ToString("O"));
        if (stableSlot)
        {
            checks.Require(!release.Prerelease, "full release", "not a pre-release", "the release is marked as a pre-release on GitHub, and the stable channel takes only full releases. To promote it, edit the GitHub Release, untick 'Set as a pre-release', and run again.");
        }
        else
        {
            checks.Pass("release kind", release.Prerelease ? "pre-release" : "full release");
        }

        // Its assets: exactly the package and its checksum file, uploaded, with GitHub's SHA-256 digest,
        // served from the addresses the repository metadata will link to.
        var packageName = config.PackageFileName(version);
        var expectedNames = new[] { packageName, Checksums.DefaultFileName };
        var names = release.Assets.Select(a => a.Name).ToList();
        var unexpected = names.Where(n => !expectedNames.Contains(n, StringComparer.Ordinal)).ToList();
        var missing = expectedNames.Where(n => !names.Contains(n, StringComparer.Ordinal)).ToList();
        var repeated = names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        checks.Require(
            unexpected.Count == 0 && missing.Count == 0 && repeated.Count == 0,
            "release assets",
            string.Join(", ", expectedNames),
            string.Join("; ", new[]
            {
                missing.Count > 0 ? $"missing {string.Join(", ", missing)}" : null,
                unexpected.Count > 0 ? $"unexpected {string.Join(", ", unexpected)}" : null,
                repeated.Count > 0 ? $"more than one {string.Join(", ", repeated)}" : null,
            }.Where(p => p is not null)) + $"; a release carries exactly {string.Join(" and ", expectedNames)}.");
        checks.ThrowIfFailed();

        var packageAsset = release.Assets.Single(a => a.Name == packageName);
        var checksumsAsset = release.Assets.Single(a => a.Name == Checksums.DefaultFileName);
        var packageDigest = CheckAsset(packageAsset, config.DownloadUrl(version), checks);
        var checksumsDigest = CheckAsset(checksumsAsset, config.DownloadUrlTemplate.Resolve(config.InternalName, version, Checksums.DefaultFileName), checks);

        // The downloaded files are the bytes GitHub serves for those assets.
        var packagePath = Path.Combine(directory, packageName);
        var checksumsPath = Path.Combine(directory, Checksums.DefaultFileName);
        CheckDownload(packagePath, packageAsset, packageDigest, checks);
        var checksumsSha256 = CheckDownload(checksumsPath, checksumsAsset, checksumsDigest, checks);

        // The tag.
        var facts = checks.Attempt("tag facts", () => TagFacts.Parse(ReadFile(directory, TagFile, 64 * 1024), TagFile), _ => TagFile);
        var commit = string.Empty;
        if (facts is not null)
        {
            checks.Require(facts.Tag == tag, "tag name", facts.Tag ?? string.Empty, $"the facts are for '{facts.Tag}', not {tag}.");
            checks.Require(facts.ObjectType == "tag", "annotated tag", "annotated", $"{tag} is a '{facts.ObjectType}' object, not an annotated tag; release tags are annotated (docs/Versioning.md).");
            if (checks.Require(facts.Commit is not null && Sha1.IsMatch(facts.Commit), "tagged commit", facts.Commit ?? string.Empty, $"'{facts.Commit}' is not a 40-character lowercase commit id."))
            {
                commit = facts.Commit!;
            }

            checks.Require(facts.OnDefaultBranch == true, "tag on the default branch", facts.DefaultBranch ?? string.Empty, $"{tag} points at a commit the default branch ('{facts.DefaultBranch}') does not contain; releases are cut from it.");
        }

        // The version the tagged commit sets.
        var source = Path.Combine(directory, SourceDirectory);
        var versionProps = checks.Attempt("tagged Version.props", () => (ProductVersion?)ProductVersion.ReadVersionProps(Path.Combine(source, "Version.props")), v => v!.Value.ToString());
        if (versionProps is not null)
        {
            checks.Require(versionProps.Value == version, "tagged version", version.ToString(), $"Version.props at {tag} says {versionProps}, not {version}.");
        }

        checks.ThrowIfFailed();

        // The package, against every release rule, with the sources of the tagged commit.
        var package = PackageValidator.Validate(new PackageValidationRequest
        {
            PackagePath = packagePath,
            Configuration = config,
            ExpectedVersion = version,
            ExpectedTag = tag,
            ExpectedCommit = commit,
            ProjectPath = Path.Combine(source, config.InternalName + ".csproj"),
            ChangelogPath = Path.Combine(source, "CHANGELOG.md"),
            ChecksumsPath = checksumsPath,
        }, checks);

        return new VerifiedRelease(version, release, commit, publishedAt!.Value, package, checksumsSha256!);
    }

    private static string? CheckAsset(GitHubAsset asset, string expectedUrl, CheckList checks)
    {
        checks.Require(asset.State == "uploaded", $"asset {asset.Name} state", asset.State, $"is '{asset.State}', not 'uploaded'.");
        checks.Require(asset.BrowserDownloadUrl == expectedUrl, $"asset {asset.Name} address", asset.BrowserDownloadUrl, $"GitHub serves it from '{asset.BrowserDownloadUrl}', but the repository metadata links to '{expectedUrl}'.");
        var match = asset.Digest is null ? Match.Empty : Digest.Match(asset.Digest);
        return checks.Require(match.Success, $"asset {asset.Name} digest", asset.Digest ?? string.Empty, $"is '{asset.Digest ?? "missing"}', not GitHub's 'sha256:<64 lowercase hex>'.")
            ? match.Groups[1].Value
            : null;
    }

    private static string? CheckDownload(string path, GitHubAsset asset, string? digest, CheckList checks)
    {
        if (!File.Exists(path))
        {
            checks.Fail($"download of {asset.Name}", "the file was not downloaded.");
            return null;
        }

        var size = new FileInfo(path).Length;
        var sha256 = Checksums.Sha256Hex(path);
        checks.Require(size == asset.Size, $"download of {asset.Name} size", $"{size} bytes", $"{size} bytes, but GitHub lists {asset.Size}.");
        checks.Require(digest is not null && sha256 == digest, $"download of {asset.Name} digest", $"sha256:{sha256}", $"the downloaded file is sha256:{sha256}, GitHub's digest is {asset.Digest ?? "missing"}.");
        return sha256;
    }

    private static byte[] ReadFile(string directory, string name, long limit)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            throw new ReleaseCheckException($"{name} is missing.");
        }

        if (new FileInfo(path).Length > limit)
        {
            throw new ReleaseCheckException($"{name} is larger than {limit} bytes.");
        }

        return File.ReadAllBytes(path);
    }
}
