using System;
using System.Collections.Generic;
using System.Linq;

namespace AetherFrame.ReleaseTools;

/// <summary>What to check a pluginmaster.json against.</summary>
public sealed class RepositoryValidationRequest
{
    public required byte[] Document { get; init; }

    public required string What { get; init; }

    public required RepositoryConfiguration Configuration { get; init; }

    /// <summary>The stable package (or the testing-exclusive one), when the entry must describe exactly it.</summary>
    public PackageReport? StablePackage { get; init; }

    public PackageReport? TestingPackage { get; init; }

    /// <summary>CHANGELOG.md, when the entry's changelog fields must be its current sections.</summary>
    public string? ChangelogPath { get; init; }
}

/// <summary>
/// Every check a pluginmaster.json must pass before it is served: the shape Dalamud deserializes, the
/// one plugin the repository is for, URLs derived only from the configuration, and a testing slot
/// Dalamud will actually use. With the packages at hand, the entry must describe exactly them.
/// </summary>
public static class RepositoryValidator
{
    public static IReadOnlyList<RepositoryEntry> Validate(RepositoryValidationRequest request, CheckList checks)
    {
        var config = request.Configuration;
        var entries = checks.Attempt("repository document", () => RepositoryDocument.Parse(request.Document, request.What), e => $"{e.Count} entry(ies), all keys known");
        if (entries is null)
        {
            throw new ReleaseCheckException("the repository document could not be parsed.");
        }

        if (!checks.Require(entries.Count == 1, "one plugin", config.InternalName, $"{entries.Count} entries; this repository serves exactly one plugin."))
        {
            throw new ReleaseCheckException("the repository must hold exactly one entry.");
        }

        var entry = entries[0];
        checks.Require(entry.InternalName == config.InternalName, "InternalName", entry.InternalName ?? string.Empty, $"is '{entry.InternalName}', expected '{config.InternalName}'.");

        var empty = new[] { ("Name", entry.Name), ("Author", entry.Author), ("Punchline", entry.Punchline), ("Description", entry.Description) }
            .Where(f => string.IsNullOrWhiteSpace(f.Item2)).Select(f => f.Item1).ToList();
        checks.Require(empty.Count == 0, "installer fields", "Name, Author, Punchline, Description", $"empty: {string.Join(", ", empty)}.");

        var version = checks.Attempt("AssemblyVersion", () => (ProductVersion?)ProductVersion.FromAssemblyVersion(entry.AssemblyVersion, "AssemblyVersion"), v => v!.Value.AssemblyVersion.ToString());
        checks.Require(entry.RepoUrl == config.SourceRepositoryUrl, "RepoUrl", entry.RepoUrl ?? string.Empty, $"is '{entry.RepoUrl}', expected '{config.SourceRepositoryUrl}'.");
        checks.Require(entry.ApplicableVersion == "any", "ApplicableVersion", entry.ApplicableVersion ?? string.Empty, $"is '{entry.ApplicableVersion}', expected 'any'.");
        if (entry.MinimumDalamudVersion is not null)
        {
            checks.Require(Version.TryParse(entry.MinimumDalamudVersion, out _), "MinimumDalamudVersion", entry.MinimumDalamudVersion, $"'{entry.MinimumDalamudVersion}' is not a version.");
        }

        checks.Require(entry.DalamudApiLevel == config.DalamudApiLevel, "DalamudApiLevel", entry.DalamudApiLevel?.ToString() ?? string.Empty, $"is {entry.DalamudApiLevel?.ToString() ?? "missing"}, expected {config.DalamudApiLevel}.");

        var earliest = RepositoryGenerator.EarliestLastUpdate.ToUnixTimeSeconds();
        var latest = RepositoryGenerator.LatestLastUpdate.ToUnixTimeSeconds();
        checks.Require(
            entry.LastUpdate is not null && entry.LastUpdate >= earliest && entry.LastUpdate < latest,
            "LastUpdate",
            entry.LastUpdate is null ? string.Empty : DateTimeOffset.FromUnixTimeSeconds(entry.LastUpdate.Value).ToString("O"),
            $"is {entry.LastUpdate?.ToString() ?? "missing"}; expected Unix seconds between 2020 and 2100.");

        checks.Require(entry.IsHide == false, "IsHide", "false", $"is {Describe(entry.IsHide)}; a released plugin is visible, and a bad release is rolled back rather than hidden.");

        var present = new[]
        {
            ("LoadRequiredState", entry.LoadRequiredState is not null), ("LoadSync", entry.LoadSync is not null), ("LoadPriority", entry.LoadPriority is not null),
            ("CanUnloadAsync", entry.CanUnloadAsync is not null), ("AcceptsFeedback", entry.AcceptsFeedback is not null), ("IsTestingExclusive", entry.IsTestingExclusive is not null),
        }.Where(f => !f.Item2).Select(f => f.Item1).ToList();
        checks.Require(present.Count == 0, "load and feedback fields", "present", $"missing: {string.Join(", ", present)}.");

        if (entry.IconUrl is not null)
        {
            checks.Attempt("IconUrl", () => Urls.ValidateHttps(entry.IconUrl, "IconUrl").OriginalString, u => u);
        }

        if (entry.ImageUrls is not null)
        {
            checks.Attempt("ImageUrls", () =>
            {
                if (entry.ImageUrls.Count > 5)
                {
                    throw new ReleaseCheckException($"{entry.ImageUrls.Count} image URLs; the installer shows at most 5.");
                }

                foreach (var url in entry.ImageUrls)
                {
                    Urls.ValidateHttps(url, "ImageUrls entry");
                }

                return entry.ImageUrls.Count;
            }, n => $"{n} image URL(s)");
        }

        if (entry.Tags is not null)
        {
            checks.Require(entry.Tags.Count > 0 && !entry.Tags.Any(string.IsNullOrWhiteSpace) && entry.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() == entry.Tags.Count, "Tags", string.Join(", ", entry.Tags), "must be non-empty, distinct tags.");
        }

        if (entry.Changelog is not null)
        {
            checks.Require(entry.Changelog.Trim().Length > 0 && entry.Changelog.Length <= ChangelogSections.MaxLength, "Changelog", $"{entry.Changelog.Length} characters", $"is empty or longer than {ChangelogSections.MaxLength} characters.");
        }

        if (entry.TestingChangelog is not null)
        {
            checks.Require(entry.TestingChangelog.Trim().Length > 0 && entry.TestingChangelog.Length <= ChangelogSections.MaxLength, "TestingChangelog", $"{entry.TestingChangelog.Length} characters", $"is empty or longer than {ChangelogSections.MaxLength} characters.");
        }

        if (version is null)
        {
            throw new ReleaseCheckException("the entry's AssemblyVersion could not be read.");
        }

        var stableVersion = version.Value;
        var install = checks.Attempt("DownloadLinkInstall", () =>
        {
            var expected = config.DownloadUrl(stableVersion);
            if (entry.DownloadLinkInstall != expected)
            {
                throw new ReleaseCheckException($"is '{entry.DownloadLinkInstall}', expected '{expected}' from the configured template.");
            }

            return expected;
        }, u => u);
        checks.Require(entry.DownloadLinkUpdate == entry.DownloadLinkInstall, "DownloadLinkUpdate", entry.DownloadLinkUpdate ?? string.Empty, $"is '{entry.DownloadLinkUpdate}', expected the install link.");

        ProductVersion? testingVersion = null;
        if (entry.TestingAssemblyVersion is null)
        {
            var leftovers = new List<string>();
            if (entry.TestingDalamudApiLevel is not null)
            {
                leftovers.Add("TestingDalamudApiLevel");
            }

            if (entry.TestingChangelog is not null)
            {
                leftovers.Add("TestingChangelog");
            }

            if (entry.IsTestingExclusive == true)
            {
                leftovers.Add("IsTestingExclusive");
            }

            checks.Require(leftovers.Count == 0, "testing slot", "none", $"has no TestingAssemblyVersion but sets {string.Join(", ", leftovers)}.");
            checks.Require(entry.DownloadLinkTesting == entry.DownloadLinkInstall, "DownloadLinkTesting", entry.DownloadLinkTesting ?? string.Empty, $"is '{entry.DownloadLinkTesting}', expected the install link when there is no testing version.");
        }
        else
        {
            testingVersion = checks.Attempt("TestingAssemblyVersion", () => (ProductVersion?)ProductVersion.FromAssemblyVersion(entry.TestingAssemblyVersion, "TestingAssemblyVersion"), v => v!.Value.AssemblyVersion.ToString());
            checks.Require(
                entry.TestingDalamudApiLevel == config.DalamudApiLevel,
                "TestingDalamudApiLevel",
                entry.TestingDalamudApiLevel?.ToString() ?? string.Empty,
                $"is {entry.TestingDalamudApiLevel?.ToString() ?? "missing"}, expected {config.DalamudApiLevel}; Dalamud never uses a testing version without it.");

            if (testingVersion is not null)
            {
                if (entry.IsTestingExclusive == true)
                {
                    checks.Require(testingVersion.Value == stableVersion, "testing-exclusive versions", stableVersion.ToString(), $"a testing-exclusive entry carries one version in both slots; stable is {stableVersion}, testing {testingVersion}.");
                    checks.Require(entry.DownloadLinkTesting == install, "DownloadLinkTesting", entry.DownloadLinkTesting ?? string.Empty, "a testing-exclusive entry's three links are the same.");
                }
                else
                {
                    checks.Require(testingVersion.Value > stableVersion, "testing version order", $"{testingVersion} > {stableVersion}", $"the testing version {testingVersion} is not newer than the stable {stableVersion}; Dalamud ignores it.");
                    checks.Attempt("DownloadLinkTesting", () =>
                    {
                        var expected = config.DownloadUrl(testingVersion.Value);
                        if (entry.DownloadLinkTesting != expected)
                        {
                            throw new ReleaseCheckException($"is '{entry.DownloadLinkTesting}', expected '{expected}' from the configured template.");
                        }

                        return expected;
                    }, u => u);
                }
            }
        }

        if (request.StablePackage is not null)
        {
            var stableEntryVersion = entry.IsTestingExclusive == true && testingVersion is not null ? testingVersion.Value : stableVersion;
            ComparePackage(entry, request.StablePackage, stableEntryVersion, entry.Changelog, entry.IsTestingExclusive == true ? "testing-exclusive package" : "stable package", checks);
        }

        if (request.TestingPackage is not null && entry.IsTestingExclusive != true)
        {
            checks.Require(testingVersion is not null && testingVersion.Value == request.TestingPackage.Version, "testing package version", request.TestingPackage.Version.ToString(), $"the entry's testing version is {testingVersion?.ToString() ?? "absent"}, the package is {request.TestingPackage.Version}.");
            checks.Require(entry.TestingChangelog == request.TestingPackage.ChangelogSection, "testing package changelog", "matches", "the entry's TestingChangelog is not the testing package's changelog section.");
        }

        if (request.ChangelogPath is not null)
        {
            var expectedChangelog = checks.Attempt("changelog section", () => ChangelogSections.Read(request.ChangelogPath, stableVersion), s => $"{s.Length} characters for {stableVersion}");
            if (expectedChangelog is not null)
            {
                checks.Require(entry.Changelog == expectedChangelog, "Changelog is current", "matches CHANGELOG.md", $"differs from the current {stableVersion} section of CHANGELOG.md.");
            }

            if (testingVersion is not null && entry.IsTestingExclusive != true)
            {
                var expectedTesting = checks.Attempt("testing changelog section", () => ChangelogSections.Read(request.ChangelogPath, testingVersion.Value), s => $"{s.Length} characters for {testingVersion}");
                if (expectedTesting is not null)
                {
                    checks.Require(entry.TestingChangelog == expectedTesting, "TestingChangelog is current", "matches CHANGELOG.md", $"differs from the current {testingVersion} section of CHANGELOG.md.");
                }
            }
        }

        checks.ThrowIfFailed();
        return entries;
    }

    private static void ComparePackage(RepositoryEntry entry, PackageReport package, ProductVersion entryVersion, string? entryChangelog, string role, CheckList checks)
    {
        checks.Require(entryVersion == package.Version, $"{role} version", package.Version.ToString(), $"the entry says {entryVersion}, the package is {package.Version}.");

        var manifest = package.Manifest;
        var differences = new List<string>();
        void Compare<T>(string name, T entryValue, T manifestValue)
        {
            if (!EqualityComparer<T>.Default.Equals(entryValue, manifestValue))
            {
                differences.Add(name);
            }
        }

        Compare("Author", entry.Author, manifest.Author);
        Compare("Name", entry.Name, manifest.Name);
        Compare("Punchline", entry.Punchline, manifest.Punchline);
        Compare("Description", entry.Description, manifest.Description);
        Compare("RepoUrl", entry.RepoUrl, manifest.RepoUrl);
        Compare("IconUrl", entry.IconUrl, manifest.IconUrl);
        Compare("FeedbackMessage", entry.FeedbackMessage, manifest.FeedbackMessage);
        Compare("MinimumDalamudVersion", entry.MinimumDalamudVersion, manifest.MinimumDalamudVersion);
        Compare("LoadRequiredState", entry.LoadRequiredState, manifest.LoadRequiredState ?? 0);
        Compare("LoadSync", entry.LoadSync, manifest.LoadSync ?? false);
        Compare("LoadPriority", entry.LoadPriority, manifest.LoadPriority ?? 0);
        Compare("CanUnloadAsync", entry.CanUnloadAsync, manifest.CanUnloadAsync ?? false);
        Compare("AcceptsFeedback", entry.AcceptsFeedback, manifest.AcceptsFeedback ?? true);
        if (!(entry.Tags ?? new List<string>()).SequenceEqual(manifest.Tags ?? new List<string>(), StringComparer.Ordinal))
        {
            differences.Add("Tags");
        }

        if (!(entry.CategoryTags ?? new List<string>()).SequenceEqual(manifest.CategoryTags ?? new List<string>(), StringComparer.Ordinal))
        {
            differences.Add("CategoryTags");
        }

        if (!(entry.ImageUrls ?? new List<string>()).SequenceEqual(manifest.ImageUrls ?? new List<string>(), StringComparer.Ordinal))
        {
            differences.Add("ImageUrls");
        }

        checks.Require(differences.Count == 0, $"{role} manifest fields", "match the entry", $"the entry differs from the packaged manifest in {string.Join(", ", differences)}.");
        checks.Require(entryChangelog == package.ChangelogSection, $"{role} changelog", package.ChangelogSection is null ? "none" : "matches", "the entry's Changelog is not the package's changelog section.");
        checks.Require(entry.DownloadLinkInstall == package.DownloadUrl || (entry.IsTestingExclusive == true && entry.DownloadLinkTesting == package.DownloadUrl), $"{role} download URL", package.DownloadUrl, $"the entry does not link to '{package.DownloadUrl}'.");
    }

    private static string Describe(bool? value) => value is null ? "missing" : value.Value ? "true" : "false";
}
