using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

/// <summary>A release is described in the repository only after every fact about it was checked.</summary>
public class ReleaseVerifierTests
{
    private static readonly ProductVersion Version = new(0, 1, 6);

    [Fact]
    public void APublishedRelease_Passes_WithWhatThePublicationRecords()
    {
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6");

        var (checks, verified) = Verify(release);

        TestPackages.AllPassed(checks);
        Assert.NotNull(verified);
        Assert.Equal(TestReleases.ReleaseId, verified!.Release.Id);
        Assert.Equal("https://github.com/richhiiee/AetherFrame/releases/tag/v0.1.6", verified.Release.HtmlUrl);
        Assert.Equal(TestPackages.Commit, verified.Commit);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T12:00:00Z"), verified.PublishedAt);
        Assert.Equal(Checksums.Sha256Hex(Path.Combine(release, "AetherFrame-0.1.6.zip")), verified.Package.Package.Sha256);
        Assert.All(checks.Checks, c => Assert.StartsWith("v0.1.6: ", c.Name));
    }

    [Fact]
    public void ADraft_IsRefused()
    {
        // What v0.1.6 is today. The workflow's fetch never even finds a draft (GitHub's by-tag endpoint
        // serves published releases only); this is the second line of defence.
        using var directory = new TempDirectory();

        var (checks, verified) = Verify(TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { Draft = true }));

        Assert.Null(verified);
        Assert.Contains("the release is a draft. A draft is never published to the custom repository", TestPackages.Failure(checks, "v0.1.6: published"));
        Assert.Contains("has not been published", TestPackages.Failure(checks, "v0.1.6: publish time"));
        TestPackages.Failure(checks, "v0.1.6: release page");
    }

    [Fact]
    public void AMissingRelease_IsRefused()
    {
        using var directory = new TempDirectory();
        var (notFetched, _) = Verify(Path.Combine(directory.Path, "v0.1.6"));
        Assert.Contains("the release was not fetched", TestPackages.Failure(notFetched, "v0.1.6: release files"));

        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { After = d => File.Delete(Path.Combine(d, "release.json")) });
        var (noDescription, verified) = Verify(release);
        Assert.Null(verified);
        Assert.Contains("release.json is missing", TestPackages.Failure(noDescription, "v0.1.6: GitHub Release"));
    }

    [Theory]
    [InlineData("AetherFrame-0.1.6.zip")]
    [InlineData("SHA256SUMS.txt")]
    public void AMissingAsset_IsRefused(string name)
    {
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions
        {
            Release = r => r["assets"]!.AsArray().Remove(TestReleases.AssetNamed(r, name)),
        });

        var (checks, verified) = Verify(release);

        Assert.Null(verified);
        Assert.Contains($"missing {name}", TestPackages.Failure(checks, "v0.1.6: release assets"));
    }

    [Theory]
    [InlineData("AetherFrame-0.1.6.zip")]
    [InlineData("SHA256SUMS.txt")]
    public void AnAssetThatWasNotDownloaded_IsRefused(string name)
    {
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { After = d => File.Delete(Path.Combine(d, name)) });

        var (checks, _) = Verify(release);

        Assert.Contains("was not downloaded", TestPackages.Failure(checks, $"v0.1.6: download of {name}"));
    }

    [Fact]
    public void AnExtraAsset_IsRefused()
    {
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions
        {
            Release = r => r["assets"]!.AsArray().Add(JsonNode.Parse(TestReleases.AssetNamed(r, "SHA256SUMS.txt").ToJsonString().Replace("SHA256SUMS.txt", "AetherFrame.pdb"))),
        });

        var (checks, _) = Verify(release);

        Assert.Contains("unexpected AetherFrame.pdb", TestPackages.Failure(checks, "v0.1.6: release assets"));
    }

    [Fact]
    public void ABadChecksum_IsRefused()
    {
        // SHA256SUMS.txt names another hash for the package. GitHub's digest of SHA256SUMS.txt is of
        // that file as uploaded, so only the checksum line itself disagrees with the package.
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { ChecksumsText = new string('a', 64) + "  AetherFrame-0.1.6.zip\n" });

        var (checks, verified) = Verify(release);

        Assert.Null(verified);
        Assert.Contains("SHA256SUMS.txt lists aaaa", TestPackages.Failure(checks, "v0.1.6: package checksum"));
    }

    [Fact]
    public void ADownloadThatIsNotWhatGitHubDigested_IsRefused()
    {
        // The asset was replaced between reading the release description and downloading it.
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions
        {
            After = d => TestPackages.Zip(Path.Combine(d, "AetherFrame-0.1.6.zip"), ("AetherFrame.dll", new byte[] { 1, 2, 3 })),
        });

        var (checks, verified) = Verify(release);

        Assert.Null(verified);
        Assert.Contains("GitHub's digest is sha256:", TestPackages.Failure(checks, "v0.1.6: download of AetherFrame-0.1.6.zip digest"));
    }

    [Theory]
    [InlineData(null, "not GitHub's 'sha256:<64 lowercase hex>'")]
    [InlineData("md5:0123", "not GitHub's 'sha256:<64 lowercase hex>'")]
    public void AnAssetWithoutAUsableDigest_IsRefused(string? digest, string reason)
    {
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { Release = r => TestReleases.AssetNamed(r, "AetherFrame-0.1.6.zip")["digest"] = digest });

        var (checks, _) = Verify(release);

        Assert.Contains(reason, TestPackages.Failure(checks, "v0.1.6: asset AetherFrame-0.1.6.zip digest"));
    }

    [Fact]
    public void AVersionMismatch_IsRefused()
    {
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { TaggedVersion = "0.1.5" });

        var (checks, verified) = Verify(release);

        Assert.Null(verified);
        Assert.Contains("Version.props at v0.1.6 says 0.1.5", TestPackages.Failure(checks, "v0.1.6: tagged version"));
    }

    [Fact]
    public void AReleaseOfAnotherTag_IsRefused()
    {
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { Release = r => r["tag_name"] = "v0.1.5" });

        var (checks, _) = Verify(release);

        Assert.Contains("the release is for 'v0.1.5', not v0.1.6", TestPackages.Failure(checks, "v0.1.6: release tag"));
    }

    [Fact]
    public void ALightweightTag_IsRefused()
    {
        using var directory = new TempDirectory();

        var (checks, _) = Verify(TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { TagObjectType = "commit" }));

        Assert.Contains("not an annotated tag", TestPackages.Failure(checks, "v0.1.6: annotated tag"));
    }

    [Fact]
    public void ATagOffTheDefaultBranch_IsRefused()
    {
        using var directory = new TempDirectory();

        var (checks, _) = Verify(TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { OnDefaultBranch = false }));

        Assert.Contains("the default branch ('master') does not contain", TestPackages.Failure(checks, "v0.1.6: tag on the default branch"));
    }

    [Fact]
    public void ADllBuiltFromAnotherCommit_IsRefused()
    {
        using var directory = new TempDirectory();
        var other = new string('c', 40);

        var (checks, verified) = Verify(TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { TagCommit = other }));

        Assert.Null(verified);
        Assert.Contains($"the package was built from {TestPackages.Commit}, not the expected {other}", TestPackages.Failure(checks, "v0.1.6: build commit"));
    }

    [Fact]
    public void APreRelease_IsRefusedForTheStableSlot_ButNotForTesting()
    {
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { Prerelease = true });

        var (stable, _) = Verify(release, stableSlot: true);
        var (testing, verified) = Verify(release, stableSlot: false);

        Assert.Contains("untick 'Set as a pre-release'", TestPackages.Failure(stable, "v0.1.6: full release"));
        TestPackages.AllPassed(testing);
        Assert.True(verified!.Release.Prerelease);
    }

    [Theory]
    [InlineData("html_url", "https://github.com/someone-else/AetherFrame/releases/tag/v0.1.6", "v0.1.6: release page")]
    [InlineData("browser_download_url", "https://github.com/someone-else/AetherFrame/releases/download/v0.1.6/AetherFrame-0.1.6.zip", "v0.1.6: asset AetherFrame-0.1.6.zip address")]
    public void AReleaseServedFromElsewhere_IsRefused(string field, string value, string check)
    {
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions
        {
            Release = r => (field == "html_url" ? r : TestReleases.AssetNamed(r, "AetherFrame-0.1.6.zip"))[field] = value,
        });

        var (checks, _) = Verify(release);

        Assert.Contains(value, TestPackages.Failure(checks, check));
    }

    [Theory]
    [InlineData("[]", "is not a JSON object")]
    [InlineData("{\"id\": 1}", "has no array 'assets'")]
    [InlineData("{\"id\": \"1\", \"assets\": []}", "has no number 'id'")]
    [InlineData("{\"id\": 1, \"assets\": [], \"tag_name\": \"v0.1.6\", \"draft\": \"false\"}", "has no boolean 'draft'")]
    public void AMalformedReleaseDescription_IsRefused(string json, string reason)
    {
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.Path, "0.1.6", new ReleaseOptions { After = d => File.WriteAllText(Path.Combine(d, "release.json"), json) });

        var (checks, _) = Verify(release);

        Assert.Contains(reason, TestPackages.Failure(checks, "v0.1.6: GitHub Release"));
    }

    private static (CheckList Checks, VerifiedRelease? Release) Verify(string directory, bool stableSlot = false)
    {
        var checks = new CheckList();
        try
        {
            return (checks, ReleaseVerifier.Verify(directory, Version, TestPackages.Configuration(), stableSlot, checks));
        }
        catch (ReleaseCheckException)
        {
            Assert.True(checks.HasFailures, "a verification exception without a recorded failure");
            Assert.All(checks.Checks, c => Assert.StartsWith("v0.1.6: ", c.Name));
            return (checks, null);
        }
    }
}
