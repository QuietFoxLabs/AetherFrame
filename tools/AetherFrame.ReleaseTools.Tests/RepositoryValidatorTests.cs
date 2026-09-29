using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

public class RepositoryValidatorTests
{
    private static readonly DateTimeOffset LastUpdate = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void GeneratedDocument_Passes_AgainstConfigurationPackageAndChangelog()
    {
        using var directory = new TempDirectory();
        var changelog = TestPackages.Changelog(directory);
        var configuration = TestPackages.Configuration();
        var stable = TestPackages.ValidReport(directory, configuration: configuration, changelogPath: changelog);
        var document = Generate(configuration, stable);

        var (checks, entries) = Validate(document, configuration, stable, changelog);

        TestPackages.AllPassed(checks);
        Assert.Single(entries!);
    }

    [Theory]
    [InlineData("{}", "must be a JSON array")]
    [InlineData("[1]", "must be a JSON object")]
    [InlineData("[", "not valid JSON")]
    [InlineData("[{\"Author\": \"a\", \"Author\": \"b\"}]", "more than once")]
    [InlineData("[{\"Author\": \"a\", \"Sneaky\": true}]", "unknown key(s): Sneaky")]
    [InlineData("[{\"DalamudApiLevel\": \"15\"}]", "wrong type")]
    public void MalformedDocument_Fails(string json, string message)
    {
        var (checks, entries) = Validate(Encoding.UTF8.GetBytes(json), TestPackages.Configuration(), null, null);

        Assert.Null(entries);
        Assert.Contains(message, TestPackages.Failure(checks, "repository document"));
    }

    [Fact]
    public void TwoEntries_Fail()
    {
        using var directory = new TempDirectory();
        var configuration = TestPackages.Configuration();
        var stable = TestPackages.ValidReport(directory, configuration: configuration);
        var entry = RepositoryGenerator.Build(configuration, stable, null, false, LastUpdate);

        var (checks, entries) = Validate(RepositoryDocument.Serialize(new[] { entry, entry }), configuration, null, null);

        Assert.Null(entries);
        Assert.Contains("2 entries", TestPackages.Failure(checks, "one plugin"));
    }

    [Theory]
    [InlineData("InternalName", "\"Aetherframe\"", "InternalName", "expected 'AetherFrame'")]
    [InlineData("DalamudApiLevel", "14", "DalamudApiLevel", "is 14, expected 15")]
    [InlineData("RepoUrl", "\"https://github.com/x/AetherFrame\"", "RepoUrl", "expected 'https://github.com/QuietFoxLabs/AetherFrame'")]
    [InlineData("ApplicableVersion", "\"7.0\"", "ApplicableVersion", "expected 'any'")]
    [InlineData("AssemblyVersion", "\"0.1.5\"", "AssemblyVersion", "not MAJOR.MINOR.PATCH.0")]
    [InlineData("AssemblyVersion", "\"0.1.5.1\"", "AssemblyVersion", "not MAJOR.MINOR.PATCH.0")]
    [InlineData("AssemblyVersion", "\"00.1.5.0\"", "AssemblyVersion", "not MAJOR.MINOR.PATCH.0")]
    [InlineData("AssemblyVersion", "\"0.1.5.0 \"", "AssemblyVersion", "not MAJOR.MINOR.PATCH.0")]
    [InlineData("LastUpdate", "0", "LastUpdate", "between 2020 and 2100")]
    [InlineData("IsHide", "true", "IsHide", "rolled back rather than hidden")]
    [InlineData("Name", "\" \"", "installer fields", "empty: Name")]
    [InlineData("DownloadLinkInstall", "\"http://github.com/QuietFoxLabs/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip\"", "DownloadLinkInstall", "expected 'https://github.com/QuietFoxLabs/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip'")]
    [InlineData("DownloadLinkInstall", "\"https://evil.invalid/QuietFoxLabs/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip\"", "DownloadLinkInstall", "from the configured template")]
    [InlineData("DownloadLinkInstall", "\"https://github.com/QuietFoxLabs/AetherFrame/releases/download/v0.1.4/AetherFrame-0.1.5.zip\"", "DownloadLinkInstall", "from the configured template")]
    [InlineData("DownloadLinkInstall", "\"https://github.com/QuietFoxLabs/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.4.zip\"", "DownloadLinkInstall", "from the configured template")]
    [InlineData("DownloadLinkUpdate", "\"https://github.com/QuietFoxLabs/AetherFrame/releases/download/v0.1.4/AetherFrame-0.1.4.zip\"", "DownloadLinkUpdate", "expected the install link")]
    [InlineData("DownloadLinkTesting", "\"https://github.com/QuietFoxLabs/AetherFrame/releases/download/v0.1.6/AetherFrame-0.1.6.zip\"", "DownloadLinkTesting", "when there is no testing version")]
    [InlineData("IconUrl", "\"http://x.invalid/icon.png\"", "IconUrl", "must use https")]
    [InlineData("Tags", "[\"a\", \"a\"]", "Tags", "distinct")]
    [InlineData("Changelog", "\"\"", "Changelog", "empty")]
    [InlineData("TestingDalamudApiLevel", "15", "testing slot", "no TestingAssemblyVersion but sets TestingDalamudApiLevel")]
    [InlineData("IsTestingExclusive", "true", "testing slot", "no TestingAssemblyVersion but sets IsTestingExclusive")]
    public void TamperedField_Fails(string field, string value, string check, string message)
    {
        using var directory = new TempDirectory();
        var configuration = TestPackages.Configuration();
        var stable = TestPackages.ValidReport(directory, configuration: configuration);
        var document = Tamper(Generate(configuration, stable), field, value);

        var (checks, _) = Validate(document, configuration, null, null);

        Assert.Contains(message, TestPackages.Failure(checks, check));
    }

    [Theory]
    [InlineData("LastUpdate")]
    [InlineData("IsHide")]
    [InlineData("LoadRequiredState")]
    [InlineData("AcceptsFeedback")]
    [InlineData("IsTestingExclusive")]
    public void MissingField_Fails(string field)
    {
        using var directory = new TempDirectory();
        var configuration = TestPackages.Configuration();
        var stable = TestPackages.ValidReport(directory, configuration: configuration);
        var document = Remove(Generate(configuration, stable), field);

        var (checks, _) = Validate(document, configuration, null, null);

        Assert.True(checks.HasFailures, $"removing {field} was not noticed");
    }

    [Fact]
    public void TestingVersionWithoutTestingApiLevel_Fails()
    {
        using var directory = new TempDirectory();
        var changelog = TestPackages.Changelog(directory, ("0.1.6", "- T."), ("0.1.5", "- S."));
        var configuration = TestPackages.Configuration();
        var stable = TestPackages.ValidReport(directory, "0.1.5", configuration, changelog);
        var testing = TestPackages.ValidReport(directory, "0.1.6", configuration, changelog);
        var entry = RepositoryGenerator.Build(configuration, stable, testing, false, LastUpdate);
        entry.TestingDalamudApiLevel = null;

        var (checks, _) = Validate(RepositoryDocument.Serialize(new[] { entry }), configuration, null, null);

        Assert.Contains("never uses a testing version without it", TestPackages.Failure(checks, "TestingDalamudApiLevel"));
    }

    [Fact]
    public void TestingVersionNotNewerThanStable_Fails()
    {
        using var directory = new TempDirectory();
        var configuration = TestPackages.Configuration();
        var stable = TestPackages.ValidReport(directory, configuration: configuration);
        var entry = RepositoryGenerator.Build(configuration, stable, null, false, LastUpdate);
        entry.TestingAssemblyVersion = "0.1.5.0";
        entry.TestingDalamudApiLevel = 15;

        var (checks, _) = Validate(RepositoryDocument.Serialize(new[] { entry }), configuration, null, null);

        Assert.Contains("is not newer than the stable 0.1.5", TestPackages.Failure(checks, "testing version order"));
    }

    [Fact]
    public void TestingExclusiveWithTwoDifferentVersions_Fails()
    {
        using var directory = new TempDirectory();
        var configuration = TestPackages.Configuration();
        var stable = TestPackages.ValidReport(directory, configuration: configuration);
        var entry = RepositoryGenerator.Build(configuration, stable, null, false, LastUpdate);
        entry.IsTestingExclusive = true;
        entry.TestingAssemblyVersion = "0.1.6.0";
        entry.TestingDalamudApiLevel = 15;

        var (checks, _) = Validate(RepositoryDocument.Serialize(new[] { entry }), configuration, null, null);

        Assert.Contains("one version in both slots", TestPackages.Failure(checks, "testing-exclusive versions"));
    }

    [Fact]
    public void TestingExclusiveEntry_IsComparedWithTheTestingPackageGivenForIt()
    {
        using var directory = new TempDirectory();
        using var other = new TempDirectory();
        var changelog = TestPackages.Changelog(directory, ("0.1.6", "- Only for testers."), ("0.1.5", "- Older."));
        var configuration = TestPackages.Configuration();
        var exclusive = TestPackages.ValidReport(directory, "0.1.6", configuration, changelog);
        var document = RepositoryDocument.Serialize(new[] { RepositoryGenerator.Build(configuration, null, exclusive, true, LastUpdate) });

        var (matching, _) = Validate(document, configuration, null, changelog, testing: exclusive);
        TestPackages.AllPassed(matching);
        Assert.Contains(matching.Checks, c => c.Name == "testing package version" && c.Passed);

        var (otherChecks, otherBuild) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(other, "0.1.5", manifest: TestPackages.Manifest("0.1.5", m => m["Description"] = "Another build.")),
            Configuration = configuration,
            ChangelogPath = changelog,
        });
        TestPackages.AllPassed(otherChecks);

        var (checks, entries) = Validate(document, configuration, null, changelog, testing: otherBuild);

        Assert.Null(entries);
        Assert.Contains("the entry says 0.1.6, the package is 0.1.5", TestPackages.Failure(checks, "testing package version"));
        Assert.Contains("Description", TestPackages.Failure(checks, "testing package manifest fields"));
    }

    [Fact]
    public void TestingExclusiveEntry_WithAnotherTestingChangelog_Fails()
    {
        using var directory = new TempDirectory();
        var changelog = TestPackages.Changelog(directory, ("0.1.6", "- Only for testers."));
        var configuration = TestPackages.Configuration();
        var entry = RepositoryGenerator.Build(configuration, null, TestPackages.ValidReport(directory, "0.1.6", configuration, changelog), true, LastUpdate);
        entry.TestingChangelog = "- Something else.";

        var (checks, _) = Validate(RepositoryDocument.Serialize(new[] { entry }), configuration, null, null);

        Assert.Contains("the same text", TestPackages.Failure(checks, "testing-exclusive changelogs"));
    }

    [Fact]
    public void EntryDescribingAnotherPackage_Fails()
    {
        using var directory = new TempDirectory();
        var configuration = TestPackages.Configuration();
        var stable = TestPackages.ValidReport(directory, configuration: configuration);
        var document = Generate(configuration, stable);
        using var other = new TempDirectory();
        var (otherChecks, otherReport) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(other, "0.1.6", manifest: TestPackages.Manifest("0.1.6", m => m["Description"] = "Changed.")),
            Configuration = configuration,
        });
        TestPackages.AllPassed(otherChecks);

        var (checks, _) = Validate(document, configuration, otherReport, null);

        Assert.Contains("the entry says 0.1.5, the package is 0.1.6", TestPackages.Failure(checks, "stable package version"));
        Assert.Contains("Description", TestPackages.Failure(checks, "stable package manifest fields"));
    }

    [Fact]
    public void StaleChangelog_Fails()
    {
        using var directory = new TempDirectory();
        var changelog = TestPackages.Changelog(directory);
        var configuration = TestPackages.Configuration();
        var stable = TestPackages.ValidReport(directory, configuration: configuration, changelogPath: changelog);
        var document = Generate(configuration, stable);
        System.IO.File.WriteAllText(changelog, TestPackages.ChangelogText(("0.1.5", "### Fixed\n\n- A thing, reworded after publishing.")));

        var (checks, _) = Validate(document, configuration, null, changelog);

        Assert.Contains("differs from the current 0.1.5 section", TestPackages.Failure(checks, "Changelog is current"));
    }

    [Fact]
    public void HostileTextInTheManifest_CannotInjectFields()
    {
        using var directory = new TempDirectory();
        var configuration = TestPackages.Configuration();
        const string hostile = "Plates\",\n  \"IsHide\": true,\n  \"DownloadLinkInstall\": \"https://evil.invalid/x.zip\",\n  \"X\": \"\u0007 \u2028 <script>alert('x')</script> \\ \"";
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m["Description"] = hostile)),
            Configuration = configuration,
        });
        TestPackages.AllPassed(checks);

        var document = Generate(configuration, report!);
        var (validation, entries) = Validate(document, configuration, report, null);

        TestPackages.AllPassed(validation);
        Assert.Equal(hostile, entries![0].Description);
        Assert.False(entries[0].IsHide);
        Assert.Equal("https://github.com/QuietFoxLabs/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip", entries[0].DownloadLinkInstall);
        Assert.Equal(RepositoryEntry.KnownKeys.Count, RepositoryEntry.KnownKeys.Distinct().Count());
    }

    private static byte[] Generate(RepositoryConfiguration configuration, PackageReport stable) =>
        RepositoryDocument.Serialize(new[] { RepositoryGenerator.Build(configuration, stable, null, false, LastUpdate) });

    private static (CheckList Checks, IReadOnlyList<RepositoryEntry>? Entries) Validate(byte[] document, RepositoryConfiguration configuration, PackageReport? stable, string? changelog, PackageReport? testing = null)
    {
        var checks = new CheckList();
        try
        {
            return (checks, RepositoryValidator.Validate(new RepositoryValidationRequest
            {
                Document = document,
                What = "test document",
                Configuration = configuration,
                StablePackage = stable,
                TestingPackage = testing,
                ChangelogPath = changelog,
            }, checks));
        }
        catch (ReleaseCheckException)
        {
            Assert.True(checks.HasFailures, "a validation exception without a recorded failure");
            return (checks, null);
        }
    }

    /// <summary>Replaces one top-level field's JSON value in the generated document's text.</summary>
    private static byte[] Tamper(byte[] document, string field, string value)
    {
        var text = Encoding.UTF8.GetString(document);
        var lines = text.Split('\n').ToList();
        var index = lines.FindIndex(l => l.StartsWith($"    \"{field}\": ", StringComparison.Ordinal));
        if (index < 0)
        {
            // An absent optional field: add it as the first property.
            var open = lines.FindIndex(l => l == "  {");
            lines.Insert(open + 1, $"    \"{field}\": {value},");
            return Encoding.UTF8.GetBytes(string.Join('\n', lines));
        }

        var trailingComma = lines[index].EndsWith(',') || lines[index].EndsWith("[", StringComparison.Ordinal);
        if (lines[index].EndsWith("[", StringComparison.Ordinal))
        {
            // An array spans lines; drop through its closing bracket.
            var end = lines.FindIndex(index, l => l.StartsWith("    ]", StringComparison.Ordinal));
            trailingComma = lines[end].EndsWith(',');
            lines.RemoveRange(index + 1, end - index);
        }

        lines[index] = $"    \"{field}\": {value}{(trailingComma ? "," : string.Empty)}";
        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    private static byte[] Remove(byte[] document, string field)
    {
        var text = Encoding.UTF8.GetString(document);
        var lines = text.Split('\n').ToList();
        var index = lines.FindIndex(l => l.StartsWith($"    \"{field}\": ", StringComparison.Ordinal));
        Assert.True(index >= 0, $"no field {field} in the document");
        var wasLast = !lines[index].EndsWith(',');
        lines.RemoveAt(index);
        if (wasLast)
        {
            lines[index - 1] = lines[index - 1].TrimEnd(',');
        }

        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }
}
