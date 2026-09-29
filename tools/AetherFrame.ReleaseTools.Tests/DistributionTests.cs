using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

/// <summary>
/// The committed distribution files: the repository configuration agrees with the plugin project, the
/// dry-run fixture is what the tool generates for the current version, and the plugin's own build
/// output (when there is one) is a valid release package.
/// </summary>
public class DistributionTests
{
    private const string DryRunTemplate = "https://dry-run.invalid/QuietFoxLabs/AetherFrame/releases/download/v{version}/{package}";

    [Fact]
    public void Configuration_AgreesWithThePluginProject()
    {
        var configuration = RepositoryConfiguration.Load(RepositoryPaths.File(Path.Combine("distribution", "repository.json")));
        var project = XDocument.Load(RepositoryPaths.File(Path.Combine("AetherFrame", "AetherFrame.csproj")));

        Assert.Equal("AetherFrame", configuration.InternalName);
        Assert.Equal(configuration.SourceRepositoryUrl, project.Root!.Elements("PropertyGroup").Elements("RepoUrl").Single().Value.Trim());
        var sdk = (string)project.Root.Attribute("Sdk")!;
        Assert.Equal(configuration.DalamudApiLevel, int.Parse(Regex.Match(sdk, @"^Dalamud\.NET\.Sdk/(\d+)\.").Groups[1].Value));
        Assert.StartsWith("https://github.com/QuietFoxLabs/AetherFrame/releases/download/", configuration.DownloadUrlTemplate.Text);
        Assert.EndsWith("/pluginmaster.json", configuration.PluginMasterUrl);
    }

    [Fact]
    public void RepositoryUrl_IsTheApprovedPermanentAddress()
    {
        // Dalamud offers a plugin's updates only from the exact address it was installed from, so this address
        // can never change once players use it (docs/CustomRepository.md, Permanent repository URL). A change
        // here is a decision, not an edit: it would orphan every installation made from the old address.
        var configuration = RepositoryConfiguration.Load(RepositoryPaths.File(Path.Combine("distribution", "repository.json")));

        Assert.Equal("https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/refs/heads/plugin-repository/pluginmaster.json", configuration.PluginMasterUrl);
        Assert.Equal("plugin-repository", PublicationTarget.FromConfiguration(configuration).Branch);
    }

    [Fact]
    public void DryRunFixture_IsForTheCurrentVersion_AndValidates()
    {
        var configuration = RepositoryConfiguration.Load(RepositoryPaths.File(Path.Combine("distribution", "repository.json")))
            .WithDownloadUrlTemplate(DownloadUrlTemplate.Parse(DryRunTemplate));
        var fixture = File.ReadAllBytes(RepositoryPaths.File(Path.Combine("distribution", "dry-run", "pluginmaster.json")));

        var checks = new CheckList();
        var entries = RepositoryValidator.Validate(new RepositoryValidationRequest
        {
            Document = fixture,
            What = "distribution/dry-run/pluginmaster.json",
            Configuration = configuration,
            ChangelogPath = RepositoryPaths.File("CHANGELOG.md"),
        }, checks);
        TestPackages.AllPassed(checks);

        var current = ProductVersion.ReadVersionProps(RepositoryPaths.File("Version.props"));
        Assert.True(
            entries[0].AssemblyVersion == current.AssemblyVersion.ToString(),
            $"distribution/dry-run/pluginmaster.json describes {entries[0].AssemblyVersion} but Version.props is {current}. Regenerate it as docs/CustomRepository.md describes.");
        Assert.Contains("dry-run.invalid", entries[0].DownloadLinkInstall);

        // The fixture must be checked out byte for byte (distribution/dry-run/.gitattributes). A Windows
        // checkout with core.autocrlf, such as GitHub's Windows runners, would otherwise give it CRLF line
        // ends, and it would no longer be what the tool writes.
        Assert.True(Array.IndexOf(fixture, (byte)'\r') < 0, "distribution/dry-run/pluginmaster.json has CR line ends; git must check it out without converting them.");
    }

    [Fact]
    public void BuiltPackage_IsAValidRelease_AndRegeneratesTheFixtureByteForByte()
    {
        // CI builds the plugin first and can name its package in AETHERFRAME_RELEASE_PACKAGE; locally
        // the plugin's own build output of the same configuration is checked when there is one.
        var package = RepositoryPaths.BuiltPackage();
        if (package is null)
        {
            return;
        }

        var configuration = RepositoryConfiguration.Load(RepositoryPaths.File(Path.Combine("distribution", "repository.json")));
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = package,
            Configuration = configuration,
            ExpectedVersion = ProductVersion.ReadVersionProps(RepositoryPaths.File("Version.props")),
            ProjectPath = RepositoryPaths.File(Path.Combine("AetherFrame", "AetherFrame.csproj")),
            ChangelogPath = RepositoryPaths.File("CHANGELOG.md"),
        });
        TestPackages.AllPassed(checks);
        Assert.NotNull(report);

        var dryRun = configuration.WithDownloadUrlTemplate(DownloadUrlTemplate.Parse(DryRunTemplate));
        var (dryRunChecks, dryRunReport) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = package,
            Configuration = dryRun,
            ChangelogPath = RepositoryPaths.File("CHANGELOG.md"),
        });
        TestPackages.AllPassed(dryRunChecks);

        var fixturePath = RepositoryPaths.File(Path.Combine("distribution", "dry-run", "pluginmaster.json"));
        var fixture = File.ReadAllBytes(fixturePath);
        var committed = RepositoryDocument.Parse(fixture, "fixture").Single();
        var regenerated = RepositoryDocument.Serialize(new[]
        {
            RepositoryGenerator.Build(dryRun, dryRunReport!, null, false, DateTimeOffset.FromUnixTimeSeconds(committed.LastUpdate!.Value)),
        });

        Assert.True(
            fixture.SequenceEqual(regenerated),
            "distribution/dry-run/pluginmaster.json is not what the tool generates from this build. Regenerate it as docs/CustomRepository.md describes.");
    }
}
