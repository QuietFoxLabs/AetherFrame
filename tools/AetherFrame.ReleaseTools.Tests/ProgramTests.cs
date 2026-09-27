using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

/// <summary>The command line as CI and a developer run it, end to end and in process.</summary>
public class ProgramTests
{
    [Fact]
    public void NoArguments_PrintsUsage_AndExits2()
    {
        var (code, output, _) = Run();

        Assert.Equal(2, code);
        Assert.Contains("Usage:", output);
    }

    [Fact]
    public void Help_PrintsUsage_AndExits0()
    {
        Assert.Equal(0, Run("--help").Code);
    }

    [Theory]
    [InlineData("frobnicate", "unknown command 'frobnicate'")]
    [InlineData("validate-package --package x.zip", "--config is required")]
    [InlineData("validate-package --package x.zip --config c.json --verbose", "unknown option --verbose")]
    [InlineData("validate-package --package x.zip --config", "--config needs a value")]
    [InlineData("validate-package --package x.zip --config c.json extra", "unexpected argument(s): 'extra'")]
    [InlineData("validate-package --package x.zip --package y.zip --config c.json", "more than once")]
    [InlineData("generate-repository --config c.json --changelog C.md --last-update 1790414610 --stable-package a.zip", "--output is required unless --dry-run")]
    [InlineData("generate-repository --config c.json --changelog C.md --last-update 1790414610 --output o.json", "--stable-package is required")]
    [InlineData("generate-repository --config c.json --changelog C.md --last-update 1790414610 --output o.json --testing-exclusive --stable-package a.zip --testing-package b.zip", "--testing-exclusive takes --testing-package only")]
    [InlineData("checksums --output s.txt", "at least one file")]
    public void WrongUsage_Exits2_WithTheReason(string commandLine, string reason)
    {
        var (code, _, error) = Run(commandLine.Split(' '));

        Assert.Equal(2, code);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void ValidatePackage_PassesAValidRelease_AndWritesASummary()
    {
        using var directory = new TempDirectory();
        var package = TestPackages.Package(directory);
        var summary = directory.File("summary.json");

        var (code, output, _) = Run(
            "validate-package", "--package", package, "--config", TestPackages.Config(directory),
            "--version-props", TestPackages.VersionProps(directory), "--tag", "v0.1.5", "--commit", TestPackages.Commit,
            "--csproj", TestPackages.Csproj(directory), "--changelog", TestPackages.Changelog(directory),
            "--checksums", TestPackages.Checksums(directory, package), "--summary", summary);

        Assert.Equal(0, code);
        Assert.Contains("Package OK: AetherFrame-0.1.5.zip (AetherFrame 0.1.5.0, commit ab26da0, Dalamud API 15)", output);
        Assert.DoesNotContain("[FAIL]", output);

        using var document = JsonDocument.Parse(File.ReadAllBytes(summary));
        Assert.Equal("0.1.5", document.RootElement.GetProperty("version").GetString());
        Assert.Equal(TestPackages.Commit, document.RootElement.GetProperty("commit").GetString());
        Assert.Equal("AetherFrame-0.1.5.zip", document.RootElement.GetProperty("package").GetProperty("name").GetString());
        Assert.Equal(Checksums.Sha256Hex(package), document.RootElement.GetProperty("package").GetProperty("sha256").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("entries").GetArrayLength());
        Assert.DoesNotContain(directory.Path.Replace('\\', '/'), File.ReadAllText(summary).Replace('\\', '/'));

        var first = File.ReadAllBytes(summary);
        Run("validate-package", "--package", package, "--config", TestPackages.Config(directory), "--summary", summary);
        Assert.Equal(first, File.ReadAllBytes(summary));
    }

    [Fact]
    public void ValidatePackage_FailsABrokenRelease_WithEveryReason()
    {
        using var directory = new TempDirectory();
        var package = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m["DalamudApiLevel"] = 14));

        var (code, output, _) = Run("validate-package", "--package", package, "--config", TestPackages.Config(directory), "--version-props", TestPackages.VersionProps(directory, "0.1.6"));

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] manifest DalamudApiLevel: is 14, expected 15.", output);
        Assert.Contains("[FAIL] Version.props: Version.props says 0.1.6, the package is 0.1.5.", output);
        Assert.Contains("FAILED: 2 check(s) failed.", output);
        Assert.DoesNotContain("Package OK", output);
    }

    [Fact]
    public void ValidatePackage_ReportsADamagedZip_AsAFailedCheck()
    {
        using var directory = new TempDirectory();
        var package = TestPackages.Package(directory);
        TestPackages.RewriteEntry(package, "AetherFrame.json", (bytes, central, _) => bytes[central] = 0);

        var (code, output, _) = Run("validate-package", "--package", package, "--config", TestPackages.Config(directory));

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] package format: the ZIP archive is damaged", output);
        Assert.Contains("FAILED:", output);
    }

    [Fact]
    public void ValidatePackage_WithAMissingConfiguration_Fails()
    {
        using var directory = new TempDirectory();
        var (code, output, _) = Run("validate-package", "--package", TestPackages.Package(directory), "--config", directory.File("nope.json"));

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] repository configuration", output);
    }

    [Fact]
    public void GenerateThenValidateRepository_RoundTrips()
    {
        using var directory = new TempDirectory();
        var package = TestPackages.Package(directory);
        var config = TestPackages.Config(directory);
        var changelog = TestPackages.Changelog(directory);
        var output = directory.File(Path.Combine("out", "pluginmaster.json"));

        var (generateCode, generateOutput, _) = Run("generate-repository", "--config", config, "--changelog", changelog, "--last-update", "2026-09-26T09:23:30Z", "--stable-package", package, "--output", output);

        Assert.Equal(0, generateCode);
        Assert.Contains("Repository metadata OK:", generateOutput);
        Assert.Contains("must never change once players use it", generateOutput);
        Assert.True(File.Exists(output));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.tmp"));
        var text = File.ReadAllText(output);
        Assert.StartsWith("[\n  {\n    \"Author\": \"richhiiee\",\n", text);
        Assert.EndsWith("}\n]\n", text);
        Assert.Contains("\"LastUpdate\": 1790414610,", text);

        var (validateCode, validateOutput, _) = Run("validate-repository", "--repository", output, "--config", config, "--stable-package", package, "--changelog", changelog);
        Assert.Equal(0, validateCode);
        Assert.Contains("Repository metadata OK:", validateOutput);

        // Regenerating is byte-identical.
        var second = directory.File("second.json");
        Run("generate-repository", "--config", config, "--changelog", changelog, "--last-update", "1790414610", "--stable-package", package, "--output", second);
        Assert.Equal(File.ReadAllBytes(output), File.ReadAllBytes(second));
    }

    [Fact]
    public void GenerateRepository_DryRun_WritesNothing_AndPrintsTheDocument()
    {
        using var directory = new TempDirectory();
        var before = Directory.GetFiles(directory.Path).Length;

        var (code, output, _) = Run("generate-repository", "--config", TestPackages.Config(directory), "--changelog", TestPackages.Changelog(directory), "--last-update", "1790414610", "--stable-package", TestPackages.Package(directory), "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("Dry run: nothing written.", output);
        Assert.Contains("\"InternalName\": \"AetherFrame\"", output);
        Assert.Equal(before + 3, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public void GenerateRepository_WithTestingPackage_AndTemplateOverride()
    {
        using var directory = new TempDirectory();
        var changelog = TestPackages.Changelog(directory, ("0.1.6", "- T."), ("0.1.5", "- S."));
        var output = directory.File("pluginmaster.json");

        var (code, text, _) = Run(
            "generate-repository", "--config", TestPackages.Config(directory), "--changelog", changelog, "--last-update", "1790414610",
            "--stable-package", TestPackages.Package(directory, "0.1.5"), "--testing-package", TestPackages.Package(directory, "0.1.6"),
            "--download-url-template", TestPackages.DryRunTemplate, "--output", output);

        Assert.Equal(0, code);
        Assert.Contains("testing 0.1.6.0", text);
        var document = File.ReadAllText(output);
        Assert.Contains("\"DownloadLinkTesting\": \"https://dry-run.invalid/richhiiee/AetherFrame/releases/download/v0.1.6/AetherFrame-0.1.6.zip\"", document);
        Assert.DoesNotContain("github.com/richhiiee/AetherFrame/releases", document);

        var (validateCode, _, _) = Run("validate-repository", "--repository", output, "--config", TestPackages.Config(directory), "--download-url-template", TestPackages.DryRunTemplate);
        Assert.Equal(0, validateCode);
        var (productionCode, productionOutput, _) = Run("validate-repository", "--repository", output, "--config", TestPackages.Config(directory));
        Assert.Equal(1, productionCode);
        Assert.Contains("[FAIL] DownloadLinkInstall", productionOutput);
    }

    [Fact]
    public void GenerateRepository_TestingExclusive()
    {
        using var directory = new TempDirectory();
        var output = directory.File("pluginmaster.json");

        var (code, text, _) = Run(
            "generate-repository", "--config", TestPackages.Config(directory), "--changelog", TestPackages.Changelog(directory), "--last-update", "1790414610",
            "--testing-exclusive", "--testing-package", TestPackages.Package(directory), "--output", output);

        Assert.True(code == 0, text);
        Assert.Contains("(testing-exclusive)", text);
        Assert.Contains("\"IsTestingExclusive\": true", File.ReadAllText(output));
    }

    [Fact]
    public void ValidateRepository_ChecksATestingExclusiveEntryAgainstTheGivenTestingPackage()
    {
        using var directory = new TempDirectory();
        using var other = new TempDirectory();
        var config = TestPackages.Config(directory);
        var changelog = TestPackages.Changelog(directory, ("0.1.6", "- New."), ("0.1.5", "- Old."));
        var package = TestPackages.Package(directory, "0.1.5");
        var output = directory.File("pluginmaster.json");
        Assert.Equal(0, Run("generate-repository", "--config", config, "--changelog", changelog, "--last-update", "1790414610", "--testing-exclusive", "--testing-package", package, "--output", output).Code);

        Assert.Equal(0, Run("validate-repository", "--repository", output, "--config", config, "--testing-package", package, "--changelog", changelog).Code);

        var (code, text, _) = Run("validate-repository", "--repository", output, "--config", config, "--testing-package", TestPackages.Package(other, "0.1.6"), "--changelog", changelog);
        Assert.Equal(1, code);
        Assert.Contains("[FAIL] testing package version: the entry says 0.1.5, the package is 0.1.6.", text);
        Assert.DoesNotContain("Repository metadata OK", text);
    }

    [Fact]
    public void GenerateRepository_RefusesABrokenPackage_AndWritesNothing()
    {
        using var directory = new TempDirectory();
        var output = directory.File("pluginmaster.json");

        var (code, text, _) = Run(
            "generate-repository", "--config", TestPackages.Config(directory), "--changelog", TestPackages.Changelog(directory), "--last-update", "1790414610",
            "--stable-package", TestPackages.Package(directory, extraEntries: new[] { ("AetherFrame.pdb", new byte[] { 1 }) }), "--output", output);

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] stable package: no extra files: AetherFrame.pdb (debug symbols)", text);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ValidateRepository_FailsATamperedFile()
    {
        using var directory = new TempDirectory();
        var config = TestPackages.Config(directory);
        var output = directory.File("pluginmaster.json");
        Run("generate-repository", "--config", config, "--changelog", TestPackages.Changelog(directory), "--last-update", "1790414610", "--stable-package", TestPackages.Package(directory), "--output", output);
        File.WriteAllText(output, File.ReadAllText(output).Replace("\"IsHide\": false", "\"IsHide\": true", StringComparison.Ordinal));

        var (code, text, _) = Run("validate-repository", "--repository", output, "--config", config);

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] IsHide", text);
    }

    [Fact]
    public void ChecksumsThenVerify_RoundTrips_AndCatchesTampering()
    {
        using var directory = new TempDirectory();
        var zip = TestPackages.Package(directory);
        File.WriteAllText(directory.File("release-notes.md"), "notes");
        var sums = directory.File("SHA256SUMS.txt");

        var (code, output, _) = Run("checksums", "--output", sums, zip, directory.File("release-notes.md"));
        Assert.Equal(0, code);
        var text = File.ReadAllText(sums);
        Assert.Equal($"{Checksums.Sha256Hex(zip)}  AetherFrame-0.1.5.zip\n{Checksums.Sha256Hex(directory.File("release-notes.md"))}  release-notes.md\n", text);
        Assert.Contains(text, output);

        Assert.Equal(0, Run("verify-checksums", "--checksums", sums).Code);
        Assert.Equal(0, Run("verify-checksums", "--checksums", sums, "--directory", directory.Path).Code);

        File.WriteAllText(directory.File("release-notes.md"), "edited");
        var (badCode, badOutput, _) = Run("verify-checksums", "--checksums", sums);
        Assert.Equal(1, badCode);
        Assert.Contains("[FAIL] checksum of release-notes.md", badOutput);
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Program.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
