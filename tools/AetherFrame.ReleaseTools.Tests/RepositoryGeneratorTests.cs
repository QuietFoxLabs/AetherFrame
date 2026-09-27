using System;
using System.Linq;
using System.Text;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

public class RepositoryGeneratorTests
{
    private static readonly DateTimeOffset LastUpdate = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void StableRelease_ProducesExactlyTheDocumentDalamudReads()
    {
        using var directory = new TempDirectory();
        var changelog = TestPackages.Changelog(directory);
        var stable = TestPackages.ValidReport(directory, changelogPath: changelog);

        var entry = RepositoryGenerator.Build(TestPackages.Configuration(), stable, null, false, LastUpdate);
        var document = Encoding.UTF8.GetString(RepositoryDocument.Serialize(new[] { entry }));

        const string expected = """
            [
              {
                "Author": "richhiiee",
                "Name": "AetherFrame",
                "Punchline": "Design character Plates.",
                "Description": "Design character Plates: a test description.",
                "Changelog": "### Fixed\n\n- A thing.",
                "Tags": [
                  "aetherframe",
                  "plates"
                ],
                "IsHide": false,
                "InternalName": "AetherFrame",
                "AssemblyVersion": "0.1.5.0",
                "RepoUrl": "https://github.com/richhiiee/AetherFrame",
                "ApplicableVersion": "any",
                "DalamudApiLevel": 15,
                "LastUpdate": 1700000000,
                "DownloadLinkInstall": "https://github.com/richhiiee/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip",
                "DownloadLinkUpdate": "https://github.com/richhiiee/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip",
                "DownloadLinkTesting": "https://github.com/richhiiee/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip",
                "LoadRequiredState": 0,
                "LoadSync": false,
                "LoadPriority": 0,
                "CanUnloadAsync": false,
                "IconUrl": "https://raw.githubusercontent.com/richhiiee/AetherFrameAssets/master/images/icon.png",
                "AcceptsFeedback": true,
                "IsTestingExclusive": false
              }
            ]

            """;
        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), document);
    }

    [Fact]
    public void Output_IsIndependentOfBuildCommitAndPackageBytes()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var configuration = TestPackages.Configuration();

        var a = TestPackages.ValidReport(first, changelogPath: TestPackages.Changelog(first));
        var otherBuild = TestPackages.Package(second, assembly: TestPackages.Assembly(informationalVersion: "0.1.5+" + new string('c', 40)));
        var (checks, b) = TestPackages.Validate(new PackageValidationRequest { PackagePath = otherBuild, Configuration = configuration, ChangelogPath = TestPackages.Changelog(second) });
        TestPackages.AllPassed(checks);

        Assert.NotEqual(a.Package.Sha256, b!.Package.Sha256);
        Assert.NotEqual(a.Commit, b.Commit);
        Assert.Equal(
            RepositoryDocument.Serialize(new[] { RepositoryGenerator.Build(configuration, a, null, false, LastUpdate) }),
            RepositoryDocument.Serialize(new[] { RepositoryGenerator.Build(configuration, b, null, false, LastUpdate) }));
    }

    [Fact]
    public void StableWithNewerTesting_FillsTheTestingSlot()
    {
        using var directory = new TempDirectory();
        var changelog = TestPackages.Changelog(directory, ("0.1.6", "- Testing notes."), ("0.1.5", "- Stable notes."));
        var configuration = TestPackages.Configuration();
        var stable = TestPackages.ValidReport(directory, "0.1.5", configuration, changelog);
        var testing = TestPackages.ValidReport(directory, "0.1.6", configuration, changelog);

        var entry = RepositoryGenerator.Build(configuration, stable, testing, false, LastUpdate);

        Assert.Equal("0.1.5.0", entry.AssemblyVersion);
        Assert.Equal("0.1.6.0", entry.TestingAssemblyVersion);
        Assert.Equal(15, entry.TestingDalamudApiLevel);
        Assert.False(entry.IsTestingExclusive);
        Assert.Equal("- Stable notes.", entry.Changelog);
        Assert.Equal("- Testing notes.", entry.TestingChangelog);
        Assert.EndsWith("/v0.1.5/AetherFrame-0.1.5.zip", entry.DownloadLinkInstall);
        Assert.Equal(entry.DownloadLinkInstall, entry.DownloadLinkUpdate);
        Assert.EndsWith("/v0.1.6/AetherFrame-0.1.6.zip", entry.DownloadLinkTesting);

        var checks = new CheckList();
        RepositoryValidator.Validate(new RepositoryValidationRequest
        {
            Document = RepositoryDocument.Serialize(new[] { entry }),
            What = "generated",
            Configuration = configuration,
            StablePackage = stable,
            TestingPackage = testing,
            ChangelogPath = changelog,
        }, checks);
        TestPackages.AllPassed(checks);
    }

    [Theory]
    [InlineData("0.1.5")]
    [InlineData("0.1.4")]
    public void TestingNotNewerThanStable_IsRefused(string testingVersion)
    {
        using var directory = new TempDirectory();
        var configuration = TestPackages.Configuration();
        var stable = TestPackages.ValidReport(directory, "0.1.5", configuration);
        using var other = new TempDirectory();
        var testing = TestPackages.ValidReport(other, testingVersion, configuration);

        var e = Assert.Throws<ReleaseCheckException>(() => RepositoryGenerator.Build(configuration, stable, testing, false, LastUpdate));
        Assert.Contains("must be newer than the stable version 0.1.5", e.Message);
    }

    [Fact]
    public void TestingExclusive_CarriesOneVersionInBothSlots()
    {
        using var directory = new TempDirectory();
        var changelog = TestPackages.Changelog(directory, ("0.1.6", "- Only for testers."));
        var configuration = TestPackages.Configuration();
        var testing = TestPackages.ValidReport(directory, "0.1.6", configuration, changelog);

        var entry = RepositoryGenerator.Build(configuration, null, testing, true, LastUpdate);

        Assert.True(entry.IsTestingExclusive);
        Assert.Equal("0.1.6.0", entry.AssemblyVersion);
        Assert.Equal("0.1.6.0", entry.TestingAssemblyVersion);
        Assert.Equal(15, entry.TestingDalamudApiLevel);
        Assert.Equal(entry.DownloadLinkInstall, entry.DownloadLinkTesting);
        Assert.Equal(entry.DownloadLinkInstall, entry.DownloadLinkUpdate);
        Assert.EndsWith("/v0.1.6/AetherFrame-0.1.6.zip", entry.DownloadLinkInstall);
        Assert.Equal("- Only for testers.", entry.Changelog);
        Assert.Equal("- Only for testers.", entry.TestingChangelog);

        var checks = new CheckList();
        RepositoryValidator.Validate(new RepositoryValidationRequest
        {
            Document = RepositoryDocument.Serialize(new[] { entry }),
            What = "generated",
            Configuration = configuration,
            StablePackage = testing,
            ChangelogPath = changelog,
        }, checks);
        TestPackages.AllPassed(checks);
    }

    [Fact]
    public void ImpossibleCombinations_AreRefused()
    {
        using var directory = new TempDirectory();
        var configuration = TestPackages.Configuration();
        var report = TestPackages.ValidReport(directory, configuration: configuration);

        Assert.Contains("no stable package", Assert.Throws<ReleaseCheckException>(() => RepositoryGenerator.Build(configuration, report, report, true, LastUpdate)).Message);
        Assert.Contains("needs the testing package", Assert.Throws<ReleaseCheckException>(() => RepositoryGenerator.Build(configuration, null, null, true, LastUpdate)).Message);
        Assert.Contains("stable package is required", Assert.Throws<ReleaseCheckException>(() => RepositoryGenerator.Build(configuration, null, report, false, LastUpdate)).Message);
    }

    [Fact]
    public void LastUpdateOutsideThePlausibleRange_IsRefused()
    {
        using var directory = new TempDirectory();
        var configuration = TestPackages.Configuration();
        var report = TestPackages.ValidReport(directory, configuration: configuration);

        Assert.Throws<ReleaseCheckException>(() => RepositoryGenerator.Build(configuration, report, null, false, new DateTimeOffset(2019, 12, 31, 23, 59, 59, TimeSpan.Zero)));
        Assert.Throws<ReleaseCheckException>(() => RepositoryGenerator.Build(configuration, report, null, false, new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("2026-09-26T09:23:30Z", 1790414610L)]
    [InlineData("2026-09-26T05:23:30-04:00", 1790414610L)]
    [InlineData("2026-09-26T09:23:30.5Z", 1790414610L)]
    [InlineData("1790414610", 1790414610L)]
    public void LastUpdate_IsParsedFromIso8601WithAZoneOrUnixSeconds(string text, long expected)
    {
        Assert.Equal(expected, RepositoryGenerator.ParseLastUpdate(text).ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData("")]
    [InlineData("now")]
    [InlineData("2026-09-26")]
    [InlineData("2026-09-26T09:23:30")]
    [InlineData("26/09/2026 09:23")]
    [InlineData("1000000000")]
    [InlineData("99999999999")]
    [InlineData("-5")]
    [InlineData("١٧٩٠٤١٤٦١٠")]
    [InlineData("１７９０４１４６１０")]
    [InlineData("٢٠٢٦-09-26T09:23:30Z")]
    public void LastUpdateInAnotherFormOrOutOfRange_IsRefused(string text)
    {
        Assert.Throws<ReleaseCheckException>(() => RepositoryGenerator.ParseLastUpdate(text));
    }

    [Fact]
    public void DownloadUrlTemplateOverride_ChangesOnlyTheLinks()
    {
        using var directory = new TempDirectory();
        var changelog = TestPackages.Changelog(directory);
        var production = TestPackages.Configuration();
        var dryRun = production.WithDownloadUrlTemplate(DownloadUrlTemplate.Parse(TestPackages.DryRunTemplate));

        var package = TestPackages.Package(directory);
        var (a, productionReport) = TestPackages.Validate(new PackageValidationRequest { PackagePath = package, Configuration = production, ChangelogPath = changelog });
        var (b, dryRunReport) = TestPackages.Validate(new PackageValidationRequest { PackagePath = package, Configuration = dryRun, ChangelogPath = changelog });
        TestPackages.AllPassed(a);
        TestPackages.AllPassed(b);

        var productionEntry = RepositoryGenerator.Build(production, productionReport!, null, false, LastUpdate);
        var dryRunEntry = RepositoryGenerator.Build(dryRun, dryRunReport!, null, false, LastUpdate);

        Assert.Equal("https://dry-run.invalid/richhiiee/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip", dryRunEntry.DownloadLinkInstall);
        var productionText = Encoding.UTF8.GetString(RepositoryDocument.Serialize(new[] { productionEntry }));
        var dryRunText = Encoding.UTF8.GetString(RepositoryDocument.Serialize(new[] { dryRunEntry }));
        Assert.Equal(productionText, dryRunText.Replace("https://dry-run.invalid/richhiiee/AetherFrame/releases/download/", "https://github.com/richhiiee/AetherFrame/releases/download/", StringComparison.Ordinal));
    }

    [Fact]
    public void KnownKeys_AreExactlyTheSerializedProperties_InOrder()
    {
        var properties = typeof(RepositoryEntry).GetProperties()
            .Select(p => (Name: ((System.Text.Json.Serialization.JsonPropertyNameAttribute)p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false).Single()).Name,
                          Order: ((System.Text.Json.Serialization.JsonPropertyOrderAttribute)p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyOrderAttribute), false).Single()).Order))
            .OrderBy(p => p.Order)
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(properties, RepositoryEntry.KnownKeys);
        Assert.Equal(properties.Count, properties.Distinct(StringComparer.Ordinal).Count());
    }
}
