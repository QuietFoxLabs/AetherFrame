using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// Builds the repository entry for a release from validated packages. Dalamud has one entry per
/// plugin with a stable slot and an optional testing slot, so three shapes exist:
/// stable only; stable plus a newer testing version for opted-in testers; and testing-exclusive,
/// where only opted-in testers see the plugin at all. Everything descriptive comes from the packaged
/// manifest, the version from the package, the URLs from the configuration, and the changelog from
/// CHANGELOG.md; nothing is typed in twice.
/// </summary>
public static class RepositoryGenerator
{
    public static readonly DateTimeOffset EarliestLastUpdate = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset LatestLastUpdate = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ASCII digits only: \d also matches other scripts' digits, which the number parsers then reject with an exception.
    private static readonly Regex UnixSeconds = new(@"^[0-9]{1,11}$", RegexOptions.CultureInvariant);
    private static readonly Regex Iso8601 = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant);

    public static RepositoryEntry Build(RepositoryConfiguration config, PackageReport? stable, PackageReport? testing, bool testingExclusive, DateTimeOffset lastUpdate)
    {
        if (lastUpdate < EarliestLastUpdate || lastUpdate >= LatestLastUpdate)
        {
            throw new ReleaseCheckException($"last update {lastUpdate:O} is outside {EarliestLastUpdate:yyyy-MM-dd} to {LatestLastUpdate:yyyy-MM-dd}.");
        }

        PackageReport primary;
        if (testingExclusive)
        {
            if (stable is not null)
            {
                throw new ReleaseCheckException("a testing-exclusive entry has no stable package; give only the testing package.");
            }

            primary = testing ?? throw new ReleaseCheckException("a testing-exclusive entry needs the testing package.");
        }
        else
        {
            primary = stable ?? throw new ReleaseCheckException("a stable package is required unless the entry is testing-exclusive.");
            if (testing is not null && testing.Version <= stable.Version)
            {
                throw new ReleaseCheckException($"the testing version {testing.Version} must be newer than the stable version {stable.Version}; Dalamud ignores it otherwise.");
            }
        }

        if (testing is not null && testing.Manifest.InternalName != primary.Manifest.InternalName)
        {
            throw new ReleaseCheckException("the stable and testing packages are different plugins.");
        }

        var manifest = primary.Manifest;
        var entry = new RepositoryEntry
        {
            Author = manifest.Author,
            Name = manifest.Name,
            Punchline = manifest.Punchline,
            Description = manifest.Description,
            Changelog = primary.ChangelogSection,
            Tags = manifest.Tags,
            CategoryTags = manifest.CategoryTags,
            IsHide = false,
            InternalName = manifest.InternalName,
            AssemblyVersion = primary.Version.AssemblyVersion.ToString(),
            RepoUrl = manifest.RepoUrl,
            ApplicableVersion = manifest.ApplicableVersion ?? "any",
            MinimumDalamudVersion = manifest.MinimumDalamudVersion,
            DalamudApiLevel = config.DalamudApiLevel,
            LastUpdate = lastUpdate.ToUnixTimeSeconds(),
            DownloadLinkInstall = primary.DownloadUrl,
            DownloadLinkUpdate = primary.DownloadUrl,
            DownloadLinkTesting = primary.DownloadUrl,
            LoadRequiredState = manifest.LoadRequiredState ?? 0,
            LoadSync = manifest.LoadSync ?? false,
            LoadPriority = manifest.LoadPriority ?? 0,
            CanUnloadAsync = manifest.CanUnloadAsync ?? false,
            ImageUrls = manifest.ImageUrls,
            IconUrl = manifest.IconUrl,
            AcceptsFeedback = manifest.AcceptsFeedback ?? true,
            FeedbackMessage = manifest.FeedbackMessage,
            IsTestingExclusive = testingExclusive,
        };

        if (testing is not null)
        {
            entry.TestingAssemblyVersion = testing.Version.AssemblyVersion.ToString();
            entry.TestingDalamudApiLevel = config.DalamudApiLevel;
            entry.DownloadLinkTesting = testing.DownloadUrl;
            entry.TestingChangelog = testing.ChangelogSection;
        }

        return entry;
    }

    /// <summary>An explicit release instant: ISO 8601 with a zone (2026-09-26T09:23:30Z) or Unix seconds. Never "now".</summary>
    public static DateTimeOffset ParseLastUpdate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ReleaseCheckException("--last-update is required: the release instant as ISO 8601 with a zone, or Unix seconds.");
        }

        text = text.Trim();
        DateTimeOffset value;
        if (UnixSeconds.IsMatch(text))
        {
            value = DateTimeOffset.FromUnixTimeSeconds(long.Parse(text, CultureInfo.InvariantCulture));
        }
        else if (Iso8601.IsMatch(text)
                 && DateTimeOffset.TryParseExact(text, new[] { "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK" }, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            value = parsed;
        }
        else
        {
            throw new ReleaseCheckException($"last update '{text}' is neither ISO 8601 with a zone (2026-09-26T09:23:30Z) nor Unix seconds.");
        }

        if (value < EarliestLastUpdate || value >= LatestLastUpdate)
        {
            throw new ReleaseCheckException($"last update {value:O} is outside {EarliestLastUpdate:yyyy-MM-dd} to {LatestLastUpdate:yyyy-MM-dd}.");
        }

        return value;
    }
}
