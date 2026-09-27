using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

/// <summary>plan-publication and prepare-publication end to end, the way the publication workflow runs them.</summary>
public class PublicationTests
{
    [Fact]
    public void FirstTestingPublication_IsTestingExclusive_AndProducesTheBranchFilesAndTheRecord()
    {
        using var directory = new TempDirectory();
        var release = TestReleases.Create(directory.File("releases"), "0.1.6");

        var (code, output, outputs) = Prepare(directory, "0.1.6", "testing", current: null);

        Assert.True(code == 0, output);
        Assert.Contains("Publication prepared: Publish 0.1.6 to testing.", output);
        Assert.Contains("Nothing has been published.", output);

        // Exactly the files the workflow uses; the branch gets the first two and nothing else.
        var publication = directory.File("publication");
        Assert.Equal(
            new[] { "branch/README.md", "branch/pluginmaster.json", "commit-message.txt", "report.md", "summary.json" },
            Directory.GetFiles(publication, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(publication, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal));
        Assert.Equal(File.ReadAllBytes(Readme(directory)), File.ReadAllBytes(Path.Combine(publication, "branch", "README.md")));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".*.tmp"));

        var document = File.ReadAllBytes(Path.Combine(publication, "branch", "pluginmaster.json"));
        var entry = RepositoryDocument.Parse(document, "published").Single();
        Assert.True(entry.IsTestingExclusive);
        Assert.Equal("0.1.6.0", entry.AssemblyVersion);
        Assert.Equal("0.1.6.0", entry.TestingAssemblyVersion);
        Assert.Equal(15, entry.TestingDalamudApiLevel);
        Assert.Equal("https://github.com/richhiiee/AetherFrame/releases/download/v0.1.6/AetherFrame-0.1.6.zip", entry.DownloadLinkTesting);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T12:00:00Z").ToUnixTimeSeconds(), entry.LastUpdate);
        var summaryBytes = File.ReadAllBytes(Path.Combine(publication, "summary.json"));
        Assert.Equal($"changed=true\nsha256={Checksums.Sha256Hex(document)}\nsummary_sha256={Checksums.Sha256Hex(summaryBytes)}\n", outputs);

        // What the tool wrote passes validate-repository against the very package it links to.
        var (validateCode, validateOutput) = Run(
            "validate-repository", "--repository", Path.Combine(publication, "branch", "pluginmaster.json"), "--config", Config(directory),
            "--testing-package", Path.Combine(release, "AetherFrame-0.1.6.zip"), "--changelog", Path.Combine(release, "source", "CHANGELOG.md"));
        Assert.True(validateCode == 0, validateOutput);

        using var summary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(publication, "summary.json")));
        var root = summary.RootElement;
        Assert.Equal("plugin-repository", root.GetProperty("branch").GetString());
        Assert.Equal("publish", root.GetProperty("change").GetString());
        Assert.True(root.GetProperty("changed").GetBoolean());
        Assert.Equal("nothing published", root.GetProperty("previous").GetProperty("state").GetString());
        Assert.False(root.GetProperty("previous").TryGetProperty("sha256", out _));
        Assert.Equal("testing-exclusive 0.1.6", root.GetProperty("next").GetProperty("state").GetString());
        Assert.Equal(Checksums.Sha256Hex(document), root.GetProperty("next").GetProperty("sha256").GetString());
        var recorded = root.GetProperty("releases").EnumerateArray().Single();
        Assert.Equal("testing-exclusive", recorded.GetProperty("slot").GetString());
        Assert.Equal(TestPackages.Commit, recorded.GetProperty("commit").GetString());
        Assert.Equal(TestReleases.ReleaseId, recorded.GetProperty("releaseId").GetInt64());
        Assert.Equal(Checksums.Sha256Hex(Path.Combine(release, "AetherFrame-0.1.6.zip")), recorded.GetProperty("package").GetProperty("sha256").GetString());

        var message = File.ReadAllText(Path.Combine(publication, "commit-message.txt"));
        Assert.StartsWith("Publish 0.1.6 to testing\n\nAetherFrame custom repository: testing-exclusive 0.1.6 (was: nothing published).\n", message);
        Assert.Contains("Release v0.1.6 (testing-exclusive): https://github.com/richhiiee/AetherFrame/releases/tag/v0.1.6\n", message);
        Assert.Contains($"  release id {TestReleases.ReleaseId}, published 2026-09-28T12:00:00Z, pre-release\n", message);
        Assert.Contains($"  tagged commit {TestPackages.Commit}\n", message);
        Assert.Contains($"pluginmaster.json: sha256 {Checksums.Sha256Hex(document)}", message);
        Assert.Contains("Workflow run: https://github.com/richhiiee/AetherFrame/actions/runs/123456789\n", message);

        // The record names files, releases and hashes, never a local path.
        foreach (var name in new[] { "commit-message.txt", "report.md", "summary.json" })
        {
            Assert.DoesNotContain(directory.Path.Replace('\\', '/'), File.ReadAllText(Path.Combine(publication, name)).Replace('\\', '/'));
        }
    }

    [Fact]
    public void FirstStablePublication_IsStableOnly()
    {
        using var directory = new TempDirectory();
        TestReleases.Create(directory.File("releases"), "0.1.6", new ReleaseOptions { Prerelease = false });

        var (code, output, _) = Prepare(directory, "0.1.6", "stable", current: null);

        Assert.True(code == 0, output);
        var entry = RepositoryDocument.Parse(File.ReadAllBytes(directory.File("publication/branch/pluginmaster.json")), "published").Single();
        Assert.False(entry.IsTestingExclusive);
        Assert.Null(entry.TestingAssemblyVersion);
        Assert.Equal("0.1.6.0", entry.AssemblyVersion);
    }

    [Fact]
    public void TestingOverStable_KeepsTheStableVersion_AndVerifiesItAgain()
    {
        using var directory = new TempDirectory();
        var releases = directory.File("releases");
        TestReleases.Create(releases, "0.1.5", new ReleaseOptions { Prerelease = false, PublishedAt = "2026-09-26T09:00:00Z" });
        TestReleases.Create(releases, "0.1.6");
        var stable = Publish(directory, "0.1.5", "stable", current: null);

        var (code, output, _) = Prepare(directory, "0.1.6", "testing", current: stable, publication: "second");

        Assert.True(code == 0, output);
        Assert.Contains("[ OK ] v0.1.5: full release", output);
        Assert.Contains("[ OK ] v0.1.6: release kind: pre-release", output);
        var entry = RepositoryDocument.Parse(File.ReadAllBytes(directory.File("second/branch/pluginmaster.json")), "published").Single();
        Assert.Equal("0.1.5.0", entry.AssemblyVersion);
        Assert.Equal("0.1.6.0", entry.TestingAssemblyVersion);
        Assert.Equal("### Fixed\n\n- Changes in 0.1.5.", entry.Changelog);
        Assert.Equal("### Fixed\n\n- Changes in 0.1.6.", entry.TestingChangelog);
        Assert.Equal("https://github.com/richhiiee/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip", entry.DownloadLinkInstall);
        Assert.Equal("https://github.com/richhiiee/AetherFrame/releases/download/v0.1.6/AetherFrame-0.1.6.zip", entry.DownloadLinkTesting);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T12:00:00Z").ToUnixTimeSeconds(), entry.LastUpdate);
    }

    [Fact]
    public void AStableReleaseThatBecameAPreRelease_StopsATestingPublication()
    {
        using var directory = new TempDirectory();
        var releases = directory.File("releases");
        TestReleases.Create(releases, "0.1.5", new ReleaseOptions { Prerelease = false });
        var stable = Publish(directory, "0.1.5", "stable", current: null);
        Directory.Delete(Path.Combine(releases, "v0.1.5"), recursive: true);
        TestReleases.Create(releases, "0.1.5", new ReleaseOptions { Prerelease = true });
        TestReleases.Create(releases, "0.1.6");

        var (code, output, _) = Prepare(directory, "0.1.6", "testing", current: stable, publication: "second");

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] v0.1.5: full release", output);
        Assert.False(Directory.Exists(directory.File("second")));
    }

    [Fact]
    public void RollingBackTesting_RestoresTheStableOnlyRepository()
    {
        using var directory = new TempDirectory();
        var releases = directory.File("releases");
        TestReleases.Create(releases, "0.1.5", new ReleaseOptions { Prerelease = false, PublishedAt = "2026-09-26T09:00:00Z" });
        TestReleases.Create(releases, "0.1.6");
        var stableOnly = Publish(directory, "0.1.5", "stable", current: null);
        var withTesting = Publish(directory, "0.1.6", "testing", current: stableOnly, publication: "second");

        var (code, output, outputs) = Prepare(directory, "0.1.5", "testing", current: withTesting, publication: "rollback", rollback: true);

        Assert.True(code == 0, output);
        Assert.Contains("Roll back testing from 0.1.6 to 0.1.5", output);
        // Regenerated from the verified release, the rollback is byte for byte the earlier stable-only file.
        Assert.Equal(File.ReadAllBytes(stableOnly), File.ReadAllBytes(directory.File("rollback/branch/pluginmaster.json")));
        Assert.StartsWith("changed=true\n", outputs);
        using var summary = JsonDocument.Parse(File.ReadAllBytes(directory.File("rollback/summary.json")));
        Assert.Equal("rollback", summary.RootElement.GetProperty("change").GetString());
        Assert.True(summary.RootElement.GetProperty("request").GetProperty("rollback").GetBoolean());
        Assert.Equal(
            new[] { "LastUpdate", "DownloadLinkTesting", "TestingAssemblyVersion", "TestingDalamudApiLevel", "TestingChangelog" }.OrderBy(f => f),
            summary.RootElement.GetProperty("changedFields").EnumerateArray().Select(f => f.GetString()!).OrderBy(f => f));
        Assert.StartsWith("Roll back testing from 0.1.6 to 0.1.5\n", File.ReadAllText(directory.File("rollback/commit-message.txt")));
    }

    [Fact]
    public void RollingBackStable_PointsItAtTheKnownGoodRelease()
    {
        using var directory = new TempDirectory();
        var releases = directory.File("releases");
        TestReleases.Create(releases, "0.1.5", new ReleaseOptions { Prerelease = false, PublishedAt = "2026-09-26T09:00:00Z" });
        TestReleases.Create(releases, "0.1.6", new ReleaseOptions { Prerelease = false });
        var good = Publish(directory, "0.1.5", "stable", current: null);
        var bad = Publish(directory, "0.1.6", "stable", current: good, publication: "second");

        var (code, output, _) = Prepare(directory, "0.1.5", "stable", current: bad, publication: "rollback", rollback: true);

        Assert.True(code == 0, output);
        Assert.Equal(File.ReadAllBytes(good), File.ReadAllBytes(directory.File("rollback/branch/pluginmaster.json")));
    }

    [Fact]
    public void Output_IsDeterministic()
    {
        using var directory = new TempDirectory();
        TestReleases.Create(directory.File("releases"), "0.1.6");

        Assert.Equal(0, Prepare(directory, "0.1.6", "testing", current: null, publication: "first").Code);
        Assert.Equal(0, Prepare(directory, "0.1.6", "testing", current: null, publication: "second").Code);

        foreach (var file in new[] { "branch/pluginmaster.json", "branch/README.md", "summary.json", "report.md", "commit-message.txt" })
        {
            Assert.Equal(File.ReadAllBytes(directory.File("first/" + file)), File.ReadAllBytes(directory.File("second/" + file)));
        }
    }

    [Fact]
    public void TheRecord_BindsEachPackage_WhereTheFileCannot()
    {
        // pluginmaster.json names each release's download address, not its hash: a different build of the
        // same release gives the same file. The record (and so the approval) still tells them apart.
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var rebuilt = new string('d', 40);
        TestReleases.Create(first.File("releases"), "0.1.6");
        TestReleases.Create(second.File("releases"), "0.1.6", new ReleaseOptions { BuildCommit = rebuilt, TagCommit = rebuilt });

        var (a, _, aOutputs) = Prepare(first, "0.1.6", "testing", current: null);
        var (b, _, bOutputs) = Prepare(second, "0.1.6", "testing", current: null);

        Assert.Equal(0, a);
        Assert.Equal(0, b);
        Assert.Equal(File.ReadAllBytes(first.File("publication/branch/pluginmaster.json")), File.ReadAllBytes(second.File("publication/branch/pluginmaster.json")));
        Assert.Equal(aOutputs.Split('\n')[1], bOutputs.Split('\n')[1]);
        Assert.NotEqual(aOutputs.Split('\n')[2], bOutputs.Split('\n')[2]);
    }

    [Fact]
    public void TheSameRequestAgain_PublishesNothing()
    {
        using var directory = new TempDirectory();
        TestReleases.Create(directory.File("releases"), "0.1.6");
        var published = Publish(directory, "0.1.6", "testing", current: null);

        var (code, output, outputs) = Prepare(directory, "0.1.6", "testing", current: published, publication: "again");

        Assert.True(code == 0, output);
        Assert.Contains("Nothing to publish: branch plugin-repository already serves exactly this pluginmaster.json", output);
        Assert.StartsWith($"changed=false\nsha256={Checksums.Sha256Hex(File.ReadAllBytes(published))}\nsummary_sha256=", outputs);
        Assert.Equal(File.ReadAllBytes(published), File.ReadAllBytes(directory.File("again/branch/pluginmaster.json")));
        Assert.Contains("**Nothing to publish:**", File.ReadAllText(directory.File("again/report.md")));
    }

    [Fact]
    public void AnAccidentalDowngrade_IsRefused_AndWritesNothing()
    {
        using var directory = new TempDirectory();
        var releases = directory.File("releases");
        TestReleases.Create(releases, "0.1.5", new ReleaseOptions { Prerelease = false });
        TestReleases.Create(releases, "0.1.6", new ReleaseOptions { Prerelease = false });
        var published = Publish(directory, "0.1.6", "stable", current: null);

        var (code, output, outputs) = Prepare(directory, "0.1.5", "stable", current: published, publication: "downgrade");

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] plan: 0.1.5 is older than the 0.1.6 the stable channel serves now", output);
        Assert.False(Directory.Exists(directory.File("downgrade")));
        Assert.Equal(string.Empty, outputs);
    }

    [Fact]
    public void TheV016DraftToday_IsRefused_AndWritesNothing()
    {
        using var directory = new TempDirectory();
        TestReleases.Create(directory.File("releases"), "0.1.6", new ReleaseOptions { Draft = true });

        var (code, output, _) = Prepare(directory, "0.1.6", "testing", current: null);

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] v0.1.6: published: the release is a draft.", output);
        Assert.DoesNotContain("Publication prepared", output);
        Assert.False(Directory.Exists(directory.File("publication")));
    }

    [Theory]
    [InlineData("not json", "is not valid JSON")]
    [InlineData("[]", "0 entries; this repository serves exactly one plugin")]
    [InlineData("{\"InternalName\": \"AetherFrame\"}", "must be a JSON array")]
    public void AMalformedPublishedFile_IsRefused(string content, string reason)
    {
        using var directory = new TempDirectory();
        TestReleases.Create(directory.File("releases"), "0.1.6");
        var current = directory.File("current.json");
        File.WriteAllText(current, content);

        var (code, output, _) = Prepare(directory, "0.1.6", "testing", current: current);

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] published file: ", output);
        Assert.Contains(reason, output);
        Assert.False(Directory.Exists(directory.File("publication")));
    }

    [Theory]
    [InlineData("\"IsHide\": false", "\"IsHide\": true", "[FAIL] published file: IsHide")]
    [InlineData("https://github.com/richhiiee/AetherFrame/releases/download/v0.1.6/", "https://example.com/v0.1.6/", "[FAIL] published file: DownloadLinkInstall")]
    [InlineData("\"InternalName\": \"AetherFrame\"", "\"InternalName\": \"SomethingElse\"", "[FAIL] published file: InternalName")]
    public void AHandEditedPublishedFile_IsNotBuiltUpon(string original, string replacement, string failure)
    {
        using var directory = new TempDirectory();
        TestReleases.Create(directory.File("releases"), "0.1.6");
        var published = Publish(directory, "0.1.6", "testing", current: null);
        File.WriteAllText(published, File.ReadAllText(published).Replace(original, replacement, StringComparison.Ordinal));

        var (code, output, _) = Prepare(directory, "0.1.6", "testing", current: published, publication: "again");

        Assert.Equal(1, code);
        Assert.Contains(failure, output);
    }

    [Fact]
    public void AnotherBranch_IsRefused()
    {
        using var directory = new TempDirectory();
        TestReleases.Create(directory.File("releases"), "0.1.6");

        var (code, output, _) = Prepare(directory, "0.1.6", "testing", current: null, branch: "gh-pages");

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] publication target: the publication branch 'gh-pages' is not 'plugin-repository'", output);
    }

    [Fact]
    public void AnExistingOutputDirectory_IsRefused()
    {
        using var directory = new TempDirectory();
        TestReleases.Create(directory.File("releases"), "0.1.6");
        Directory.CreateDirectory(directory.File("publication"));

        var (code, output, _) = Prepare(directory, "0.1.6", "testing", current: null);

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] output directory:", output);
        Assert.Empty(Directory.GetFileSystemEntries(directory.File("publication")));
    }

    [Theory]
    [InlineData("0.1", "[FAIL] requested version")]
    [InlineData("v0.1.6", "[FAIL] requested version")]
    [InlineData("0.1.6; rm -rf /", "[FAIL] requested version")]
    public void AMalformedVersion_IsRefused(string version, string failure)
    {
        using var directory = new TempDirectory();

        var (code, output, _) = Prepare(directory, version, "testing", current: null);

        Assert.Equal(1, code);
        Assert.Contains(failure, output);
    }

    [Theory]
    [InlineData("--current", "x.json", "--no-current", "not both")]
    [InlineData("--rollback", null, null, "--current <published pluginmaster.json> is required, or --no-current")]
    public void CurrentAndNoCurrent_AreExclusive_AndOneIsRequired(string first, string? value, string? second, string reason)
    {
        var args = new[] { "plan-publication", "--config", "c.json", "--branch", "plugin-repository", "--version", "0.1.6", "--channel", "testing", first }
            .Concat(value is null ? Array.Empty<string>() : new[] { value })
            .Concat(second is null ? Array.Empty<string>() : new[] { second })
            .ToArray();
        var error = new StringWriter();

        Assert.Equal(2, Program.Run(args, new StringWriter(), error));
        Assert.Contains(reason, error.ToString());
    }

    [Fact]
    public void PlanPublication_NamesTheReleasesToFetch()
    {
        using var directory = new TempDirectory();
        TestReleases.Create(directory.File("releases"), "0.1.5", new ReleaseOptions { Prerelease = false });
        var published = Publish(directory, "0.1.5", "stable", current: null);
        var outputs = directory.File("plan-outputs");

        var (code, output) = Run(
            "plan-publication", "--config", Config(directory), "--branch", "plugin-repository", "--current", published,
            "--version", "0.1.6", "--channel", "testing", "--github-output", outputs);

        Assert.True(code == 0, output);
        Assert.Contains("[ OK ] published state: stable 0.1.5", output);
        Assert.Contains("Plan OK: Publish 0.1.6 to testing. Releases to fetch and verify: 0.1.5 0.1.6", output);
        Assert.Equal("releases=0.1.5 0.1.6\ninternal-name=AetherFrame\n", File.ReadAllText(outputs));
    }

    [Fact]
    public void PlanPublication_RefusesAnAccidentalDowngrade()
    {
        using var directory = new TempDirectory();
        TestReleases.Create(directory.File("releases"), "0.1.6");
        var published = Publish(directory, "0.1.6", "testing", current: null);

        var (code, output) = Run(
            "plan-publication", "--config", Config(directory), "--branch", "plugin-repository", "--current", published,
            "--version", "0.1.5", "--channel", "testing");

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] plan: 0.1.5 is older than the 0.1.6 the testing channel serves now", output);
    }

    /// <summary>Runs prepare-publication; returns its exit code, its output and what it appended to the GitHub output file.</summary>
    private static (int Code, string Output, string Outputs) Prepare(
        TempDirectory directory, string version, string channel, string? current, string publication = "publication", bool rollback = false, string branch = "plugin-repository")
    {
        var outputs = directory.File(publication + ".outputs");
        var args = new[]
            {
                "prepare-publication", "--config", Config(directory), "--branch", branch, "--version", version, "--channel", channel,
                "--releases", directory.File("releases"), "--branch-readme", Readme(directory), "--output", directory.File(publication),
                "--run-url", "https://github.com/richhiiee/AetherFrame/actions/runs/123456789", "--github-output", outputs,
            }
            .Concat(current is null ? new[] { "--no-current" } : new[] { "--current", current, "--current-commit", new string('e', 40) })
            .Concat(rollback ? new[] { "--rollback" } : Array.Empty<string>())
            .ToArray();
        var (code, output) = Run(args);
        return (code, output, File.Exists(outputs) ? File.ReadAllText(outputs) : string.Empty);
    }

    /// <summary>Prepares a publication that must succeed, and returns its pluginmaster.json, as if it had been published.</summary>
    private static string Publish(TempDirectory directory, string version, string channel, string? current, string publication = "publication")
    {
        var (code, output, _) = Prepare(directory, version, channel, current, publication);
        Assert.True(code == 0, output);
        return directory.File(publication + "/branch/pluginmaster.json");
    }

    private static string Config(TempDirectory directory) =>
        File.Exists(directory.File("repository.json")) ? directory.File("repository.json") : TestPackages.Config(directory);

    private static string Readme(TempDirectory directory)
    {
        var path = directory.File("branch-README.md");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "# Generated branch\n\nDon't edit it.\n", new UTF8Encoding(false));
        }

        return path;
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Program.Run(args, output, error);
        return (code, output.ToString() + error.ToString());
    }
}
