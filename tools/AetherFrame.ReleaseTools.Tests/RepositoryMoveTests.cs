using System;
using System.Linq;
using System.Text;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

/// <summary>
/// The source repository moved (docs/CustomRepository.md, Repository move). Its previous address is
/// history: packages up to the last version released there name it, and the file published before the
/// move links to it. Both are accepted as that and nothing else; everything generated uses the current
/// address, and a later version, or a third address, is refused as before.
/// </summary>
public class RepositoryMoveTests
{
    private const string Previous = TestPackages.PreviousRepoUrl;

    [Fact]
    public void Configuration_ReadsThePreviousAddress()
    {
        var config = TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious());

        Assert.NotNull(config.Previous);
        Assert.Equal(Previous, config.Previous!.SourceRepositoryUrl);
        Assert.Equal(new ProductVersion(0, 1, 6), config.Previous.LastVersion);
        Assert.Equal("https://github.com/richhiiee/AetherFrame/releases/download/v0.1.6/AetherFrame-0.1.6.zip", config.PreviousDownloadUrl(new ProductVersion(0, 1, 6)));
        Assert.Null(config.PreviousDownloadUrl(new ProductVersion(0, 1, 7)));
        Assert.Null(TestPackages.Configuration().Previous);
    }

    [Fact]
    public void Configuration_TheShippedFileRecordsTheMoveFromRichhiiee()
    {
        var config = RepositoryConfiguration.Load(RepositoryPaths.File(System.IO.Path.Combine("distribution", "repository.json")));

        Assert.Equal("https://github.com/QuietFoxLabs/AetherFrame", config.SourceRepositoryUrl);
        Assert.Equal(Previous, config.Previous!.SourceRepositoryUrl);
        Assert.Equal(TestPackages.PreviousDownloadTemplate, config.Previous.DownloadUrlTemplate.Text);

        // v0.1.6 is the last release under the old address: 0.1.7 onwards must name the new one.
        Assert.Equal(new ProductVersion(0, 1, 6), config.Previous.LastVersion);
    }

    [Theory]
    [InlineData("previousSourceRepositoryUrl", "set together or not at all")]
    [InlineData("previousDownloadUrlTemplate", "set together or not at all")]
    [InlineData("previousAddressLastVersion", "set together or not at all")]
    public void Configuration_ThePreviousFieldsComeTogether(string omitted, string expected)
    {
        var json = FixTrailingComma(string.Join("\n", TestPackages.ConfigJsonWithPrevious().Split('\n').Where(line => !line.Contains($"\"{omitted}\"", StringComparison.Ordinal))));

        var exception = Assert.Throws<ReleaseCheckException>(() => TestPackages.Configuration(json));
        Assert.Contains(expected, exception.Message);
    }

    [Theory]
    [InlineData("https://github.com/QuietFoxLabs/AetherFrame", TestPackages.DownloadTemplate, "0.1.6", "is the current sourceRepositoryUrl")]
    [InlineData("http://github.com/richhiiee/AetherFrame", "http://github.com/richhiiee/AetherFrame/releases/download/v{version}/{package}", "0.1.6", "previousSourceRepositoryUrl")]
    [InlineData(TestPackages.PreviousRepoUrl, "https://example.com/richhiiee/AetherFrame/releases/download/v{version}/{package}", "0.1.6", "not a release download of the previous source repository")]
    [InlineData(TestPackages.PreviousRepoUrl, TestPackages.PreviousDownloadTemplate, "0.1", "previousAddressLastVersion")]
    [InlineData(TestPackages.PreviousRepoUrl, TestPackages.PreviousDownloadTemplate, "latest", "previousAddressLastVersion")]
    public void Configuration_RefusesAPreviousAddressThatIsNotOne(string url, string template, string lastVersion, string expected)
    {
        var exception = Assert.Throws<ReleaseCheckException>(() => TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious(lastVersion, url, template)));
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Configuration_ADryRunTemplateKeepsThePreviousAddress()
    {
        var config = TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious()).WithDownloadUrlTemplate(DownloadUrlTemplate.Parse(TestPackages.DryRunTemplate));

        Assert.Equal(Previous, config.Previous!.SourceRepositoryUrl);
    }

    [Theory]
    [InlineData("0.1.5", true)]
    [InlineData("0.1.6", true)]
    [InlineData("0.1.7", false)]
    [InlineData("1.0.0", false)]
    public void PreviousRepoUrl_CoversOnlyTheVersionsReleasedThere(string version, bool accepted)
    {
        var config = TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious());
        var parsed = ProductVersion.Parse(version, "version");

        Assert.Equal(accepted, config.IsPreviousRepoUrl(Previous, parsed));
        Assert.False(config.IsPreviousRepoUrl("https://github.com/someone-else/AetherFrame", parsed));
        Assert.False(TestPackages.Configuration().IsPreviousRepoUrl(Previous, parsed));
    }

    [Fact]
    public void Package_ReleasedBeforeTheMove_MayNameThePreviousAddress()
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = PackageNaming(directory, "0.1.6", Previous),
            Configuration = TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious()),
        });

        Assert.NotNull(report);
        Assert.Contains(checks.Checks, c => c.Name == "manifest RepoUrl" && c.Passed && c.Detail.Contains("the address before the move", StringComparison.Ordinal));
    }

    [Fact]
    public void Package_ReleasedAfterTheMove_MustNameTheCurrentAddress()
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = PackageNaming(directory, "0.1.7", Previous),
            Configuration = TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious()),
        });

        Assert.Null(report);
        Assert.Contains("only packages up to 0.1.6 carry; 0.1.7 must name 'https://github.com/QuietFoxLabs/AetherFrame'", TestPackages.Failure(checks, "manifest RepoUrl"));
    }

    [Fact]
    public void Package_ThePreviousAddressIsRefusedWithoutARecordedMove()
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = PackageNaming(directory, "0.1.6", Previous),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains("expected 'https://github.com/QuietFoxLabs/AetherFrame'", TestPackages.Failure(checks, "manifest RepoUrl"));
    }

    [Fact]
    public void Package_AThirdAddressIsRefusedEvenForAnOldVersion()
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = PackageNaming(directory, "0.1.6", "https://github.com/someone-else/AetherFrame"),
            Configuration = TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious()),
        });

        Assert.Null(report);
        Assert.Contains("is 'https://github.com/someone-else/AetherFrame', expected 'https://github.com/QuietFoxLabs/AetherFrame'", TestPackages.Failure(checks, "manifest RepoUrl"));
    }

    [Fact]
    public void Generator_AnEntryForAPackageReleasedBeforeTheMove_UsesOnlyTheCurrentAddress()
    {
        using var directory = new TempDirectory();
        var config = TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious());
        var package = Report(directory, "0.1.6", Previous, config);

        var entry = RepositoryGenerator.Build(config, package, null, false, DateTimeOffset.Parse("2026-09-27T23:58:31Z"));
        var document = RepositoryDocument.Serialize(new[] { entry });

        Assert.Equal(TestPackages.RepoUrl, entry.RepoUrl);
        Assert.Equal("https://github.com/QuietFoxLabs/AetherFrame/releases/download/v0.1.6/AetherFrame-0.1.6.zip", entry.DownloadLinkInstall);
        Assert.DoesNotContain("richhiiee/AetherFrame/", Encoding.UTF8.GetString(document));

        // The new document, checked against the very package it describes, needs no allowance.
        var checks = new CheckList();
        RepositoryValidator.Validate(new RepositoryValidationRequest { Document = document, What = "new", Configuration = config, StablePackage = package }, checks);
        TestPackages.AllPassed(checks);
    }

    [Fact]
    public void PublishedFile_FromBeforeTheMove_IsAcceptedOnlyAsHistory()
    {
        var config = TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious());
        var published = PublishedBeforeTheMove(config, "0.1.6");

        var history = new CheckList();
        RepositoryValidator.Validate(new RepositoryValidationRequest { Document = published, What = "published", Configuration = config, AcceptPreviousAddress = true }, history);
        TestPackages.AllPassed(history);

        // The same bytes as a document about to be published: every old address is refused.
        var fresh = new CheckList();
        Assert.Throws<ReleaseCheckException>(() => RepositoryValidator.Validate(new RepositoryValidationRequest { Document = published, What = "new", Configuration = config }, fresh));
        Assert.Contains("expected 'https://github.com/QuietFoxLabs/AetherFrame'", TestPackages.Failure(fresh, "RepoUrl"));
        Assert.Contains("expected 'https://github.com/QuietFoxLabs/AetherFrame/releases/download/v0.1.6/AetherFrame-0.1.6.zip'", TestPackages.Failure(fresh, "DownloadLinkInstall"));
    }

    [Fact]
    public void PublishedFile_WithThePreviousAddressForALaterVersion_IsRefused()
    {
        var config = TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious());
        var published = PublishedBeforeTheMove(config, "0.1.7");

        var checks = new CheckList();
        Assert.Throws<ReleaseCheckException>(() => RepositoryValidator.Validate(new RepositoryValidationRequest { Document = published, What = "published", Configuration = config, AcceptPreviousAddress = true }, checks));
        TestPackages.Failure(checks, "RepoUrl");
        TestPackages.Failure(checks, "DownloadLinkInstall");
    }

    [Fact]
    public void PublishedFile_WithoutARecordedMove_IsRefusedEvenAsHistory()
    {
        var moved = TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious());
        var published = PublishedBeforeTheMove(moved, "0.1.6");

        var checks = new CheckList();
        Assert.Throws<ReleaseCheckException>(() => RepositoryValidator.Validate(new RepositoryValidationRequest { Document = published, What = "published", Configuration = TestPackages.Configuration(), AcceptPreviousAddress = true }, checks));
        TestPackages.Failure(checks, "RepoUrl");
        TestPackages.Failure(checks, "DownloadLinkInstall");
    }

    [Fact]
    public void PublishedFile_LinkingToAThirdAddress_IsRefusedEvenAsHistory()
    {
        var config = TestPackages.Configuration(TestPackages.ConfigJsonWithPrevious());
        var published = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(PublishedBeforeTheMove(config, "0.1.6"))
            .Replace("https://github.com/richhiiee/AetherFrame/releases/download/", "https://github.com/someone-else/AetherFrame/releases/download/", StringComparison.Ordinal));

        var checks = new CheckList();
        Assert.Throws<ReleaseCheckException>(() => RepositoryValidator.Validate(new RepositoryValidationRequest { Document = published, What = "published", Configuration = config, AcceptPreviousAddress = true }, checks));
        TestPackages.Failure(checks, "DownloadLinkInstall");
    }

    /// <summary>A testing-exclusive pluginmaster.json for <paramref name="version"/> as the tooling wrote it before the move: every address the old one.</summary>
    private static byte[] PublishedBeforeTheMove(RepositoryConfiguration config, string version)
    {
        using var directory = new TempDirectory();
        var package = Report(directory, version, TestPackages.RepoUrl, config);
        var entry = RepositoryGenerator.Build(config, null, package, true, DateTimeOffset.Parse("2026-09-27T23:58:31Z"));
        var text = Encoding.UTF8.GetString(RepositoryDocument.Serialize(new[] { entry }))
            .Replace(TestPackages.RepoUrl, Previous, StringComparison.Ordinal);
        // Every source and download address is the old one; the icon's address is not a repository address.
        Assert.DoesNotContain(TestPackages.RepoUrl, text);
        return Encoding.UTF8.GetBytes(text);
    }

    private static PackageReport Report(TempDirectory directory, string version, string repoUrl, RepositoryConfiguration config)
    {
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest { PackagePath = PackageNaming(directory, version, repoUrl), Configuration = config });
        Assert.True(report is not null, string.Join("\n", checks.Checks.Where(c => !c.Passed).Select(c => c.Name + ": " + c.Detail)));
        return report!;
    }

    private static string PackageNaming(TempDirectory directory, string version, string repoUrl) =>
        TestPackages.Package(directory, version, manifest: TestPackages.Manifest(version, f => f["RepoUrl"] = repoUrl));

    private static string FixTrailingComma(string json)
    {
        var lines = json.Split('\n').ToList();
        var closing = lines.FindLastIndex(l => l.Trim() == "}");
        var last = lines.FindLastIndex(closing - 1, l => l.Trim().Length > 0);
        lines[last] = lines[last].TrimEnd().TrimEnd(',');
        return string.Join("\n", lines);
    }
}
