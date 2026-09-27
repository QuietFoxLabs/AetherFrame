using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// The command-line entry point. Exit codes: 0 when every check passed, 1 when a check failed, 2 for
/// wrong usage. Output goes to the given writers so the tests drive the whole tool in process.
/// </summary>
public static class Program
{
    public const string Usage = """
        AetherFrame release tooling (docs/CustomRepository.md)

        Usage:
          validate-package     --package <zip> --config <repository.json>
                               [--version-props <Version.props>] [--tag <vX.Y.Z>] [--commit <sha>]
                               [--csproj <AetherFrame.csproj>] [--changelog <CHANGELOG.md>]
                               [--checksums <SHA256SUMS.txt>] [--summary <summary.json>]
          generate-repository  --config <repository.json> --changelog <CHANGELOG.md>
                               --last-update <ISO-8601 with zone | Unix seconds>
                               (--stable-package <zip> [--testing-package <zip>]
                                | --testing-exclusive --testing-package <zip>)
                               [--download-url-template <template>] (--output <pluginmaster.json> | --dry-run)
          validate-repository  --repository <pluginmaster.json> --config <repository.json>
                               [--stable-package <zip>] [--testing-package <zip>] [--changelog <CHANGELOG.md>]
                               [--download-url-template <template>]
          checksums            --output <SHA256SUMS.txt> <file>...
          verify-checksums     --checksums <SHA256SUMS.txt> [--directory <dir>]

        Exit codes: 0 all checks passed, 1 a check failed, 2 usage error.
        """;

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            stdout.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var rest = args.Skip(1).ToList();
        try
        {
            return args[0] switch
            {
                "validate-package" => ValidatePackage(rest, stdout),
                "generate-repository" => GenerateRepository(rest, stdout),
                "validate-repository" => ValidateRepository(rest, stdout),
                "checksums" => WriteChecksums(rest, stdout),
                "verify-checksums" => VerifyChecksums(rest, stdout),
                _ => throw new UsageException($"unknown command '{args[0]}'."),
            };
        }
        catch (UsageException e)
        {
            stderr.WriteLine("Usage error: " + e.Message);
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return 2;
        }
    }

    private static int ValidatePackage(List<string> args, TextWriter stdout)
    {
        var options = Arguments.Parse(args, new[] { "package", "config", "version-props", "tag", "commit", "csproj", "changelog", "checksums", "summary" }, Array.Empty<string>());
        options.NoPositionals();
        var packagePath = options.Required("package");
        var configPath = options.Required("config");

        var checks = new CheckList();
        PackageReport? report = null;
        var config = checks.Attempt("repository configuration", () => RepositoryConfiguration.Load(configPath), c => $"{c.InternalName}, Dalamud API {c.DalamudApiLevel}");
        var expectedVersion = options.Optional("version-props") is { } props
            ? checks.Attempt("Version.props", () => (ProductVersion?)ProductVersion.ReadVersionProps(props), v => v!.Value.ToString())
            : null;

        if (config is not null && !checks.HasFailures)
        {
            report = Validate(new PackageValidationRequest
            {
                PackagePath = packagePath,
                Configuration = config,
                ExpectedVersion = expectedVersion,
                ExpectedTag = options.Optional("tag"),
                ExpectedCommit = options.Optional("commit"),
                ProjectPath = options.Optional("csproj"),
                ChangelogPath = options.Optional("changelog"),
                ChecksumsPath = options.Optional("checksums"),
            }, checks);
        }

        checks.Print(stdout);
        if (report is null)
        {
            return Failed(checks, stdout);
        }

        if (options.Optional("summary") is { } summaryPath)
        {
            WriteAtomically(summaryPath, StrictJson.Serialize(PackageSummary.From(report, config!)));
            stdout.WriteLine($"Summary written to {summaryPath}");
        }

        stdout.WriteLine($"Package OK: {report.Package.FileName} ({config!.InternalName} {report.Version.AssemblyVersion}, commit {report.Commit[..7]}, Dalamud API {config.DalamudApiLevel})");
        stdout.WriteLine($"SHA-256: {report.Package.Sha256}");
        return 0;
    }

    private static int GenerateRepository(List<string> args, TextWriter stdout)
    {
        var options = Arguments.Parse(
            args,
            new[] { "config", "changelog", "last-update", "output", "stable-package", "testing-package", "download-url-template" },
            new[] { "testing-exclusive", "dry-run" });
        options.NoPositionals();
        var configPath = options.Required("config");
        var changelogPath = options.Required("changelog");
        var dryRun = options.Has("dry-run");
        var outputPath = options.Optional("output");
        if (outputPath is null && !dryRun)
        {
            throw new UsageException("--output is required unless --dry-run is given.");
        }

        var testingExclusive = options.Has("testing-exclusive");
        var stablePath = options.Optional("stable-package");
        var testingPath = options.Optional("testing-package");
        if (testingExclusive ? stablePath is not null || testingPath is null : stablePath is null)
        {
            throw new UsageException(testingExclusive
                ? "--testing-exclusive takes --testing-package only."
                : "--stable-package is required (with an optional --testing-package), or use --testing-exclusive --testing-package.");
        }

        var checks = new CheckList();
        var config = LoadConfiguration(configPath, options.Optional("download-url-template"), checks);
        var lastUpdate = checks.Attempt("last update", () => (DateTimeOffset?)RepositoryGenerator.ParseLastUpdate(options.Required("last-update")), t => t!.Value.ToString("O"));
        byte[]? document = null;
        if (config is not null && lastUpdate is not null)
        {
            var stable = stablePath is null ? null : ValidatePackage(stablePath, config, changelogPath, "stable package", checks);
            var testing = testingPath is null ? null : ValidatePackage(testingPath, config, changelogPath, testingExclusive ? "testing-exclusive package" : "testing package", checks);
            if (!checks.HasFailures)
            {
                var entry = checks.Attempt("repository entry", () => RepositoryGenerator.Build(config, stable, testing, testingExclusive, lastUpdate.Value), e => $"{e.InternalName} {e.AssemblyVersion}{(e.TestingAssemblyVersion is null ? string.Empty : $", testing {e.TestingAssemblyVersion}")}{(e.IsTestingExclusive == true ? " (testing-exclusive)" : string.Empty)}");
                if (entry is not null)
                {
                    document = RepositoryDocument.Serialize(new[] { entry });
                    // The output must pass its own validation before it exists.
                    ValidateDocument(document, "generated repository", config, testingExclusive ? testing : stable, testingExclusive ? null : testing, changelogPath, checks);
                }
            }
        }

        checks.Print(stdout);
        if (document is null || checks.HasFailures)
        {
            return Failed(checks, stdout);
        }

        if (dryRun)
        {
            stdout.WriteLine("Dry run: nothing written. The repository metadata would be:");
            stdout.WriteLine();
            stdout.Write(System.Text.Encoding.UTF8.GetString(document));
        }
        else
        {
            WriteAtomically(outputPath!, document);
            stdout.WriteLine($"Repository metadata OK: {outputPath} ({document.Length} bytes, SHA-256 {Checksums.Sha256Hex(document)})");
        }

        stdout.WriteLine($"Configured plugin master URL (must never change once players use it): {config!.PluginMasterUrl}");
        return 0;
    }

    private static int ValidateRepository(List<string> args, TextWriter stdout)
    {
        var options = Arguments.Parse(args, new[] { "repository", "config", "stable-package", "testing-package", "changelog", "download-url-template" }, Array.Empty<string>());
        options.NoPositionals();
        var repositoryPath = options.Required("repository");
        var configPath = options.Required("config");

        var checks = new CheckList();
        var config = LoadConfiguration(configPath, options.Optional("download-url-template"), checks);
        var document = checks.Attempt("repository file", () =>
        {
            if (!File.Exists(repositoryPath))
            {
                throw new ReleaseCheckException($"not found at {repositoryPath}.");
            }

            return File.ReadAllBytes(repositoryPath);
        }, d => $"{Path.GetFileName(repositoryPath)}, {d.Length} bytes");

        if (config is not null && document is not null)
        {
            var changelogPath = options.Optional("changelog");
            var stable = options.Optional("stable-package") is { } stablePath ? ValidatePackage(stablePath, config, changelogPath, "stable package", checks) : null;
            var testing = options.Optional("testing-package") is { } testingPath ? ValidatePackage(testingPath, config, changelogPath, "testing package", checks) : null;
            if (!checks.HasFailures)
            {
                ValidateDocument(document, Path.GetFileName(repositoryPath), config, stable, testing, changelogPath, checks);
            }
        }

        checks.Print(stdout);
        if (checks.HasFailures)
        {
            return Failed(checks, stdout);
        }

        stdout.WriteLine($"Repository metadata OK: {repositoryPath}");
        return 0;
    }

    private static int WriteChecksums(List<string> args, TextWriter stdout)
    {
        var options = Arguments.Parse(args, new[] { "output" }, Array.Empty<string>());
        var outputPath = options.Required("output");
        if (options.Positionals.Count == 0)
        {
            throw new UsageException("give at least one file to checksum.");
        }

        var checks = new CheckList();
        var text = checks.Attempt("checksums", () => Checksums.ForFiles(options.Positionals), t => $"{options.Positionals.Count} file(s)");
        checks.Print(stdout);
        if (text is null)
        {
            return Failed(checks, stdout);
        }

        WriteAtomically(outputPath, new System.Text.UTF8Encoding(false).GetBytes(text));
        stdout.Write(text);
        stdout.WriteLine($"Checksums written to {outputPath}");
        return 0;
    }

    private static int VerifyChecksums(List<string> args, TextWriter stdout)
    {
        var options = Arguments.Parse(args, new[] { "checksums", "directory" }, Array.Empty<string>());
        options.NoPositionals();
        var checksumsPath = options.Required("checksums");
        var directory = options.Optional("directory") ?? Path.GetDirectoryName(Path.GetFullPath(checksumsPath))!;

        var checks = new CheckList();
        Checksums.Verify(checksumsPath, directory, checks);
        checks.Print(stdout);
        if (checks.HasFailures)
        {
            return Failed(checks, stdout);
        }

        stdout.WriteLine($"Checksums OK: {checksumsPath}");
        return 0;
    }

    private static RepositoryConfiguration? LoadConfiguration(string configPath, string? templateOverride, CheckList checks)
    {
        var config = checks.Attempt("repository configuration", () => RepositoryConfiguration.Load(configPath), c => $"{c.InternalName}, Dalamud API {c.DalamudApiLevel}");
        if (config is not null && templateOverride is not null)
        {
            config = checks.Attempt("download URL template override", () => config.WithDownloadUrlTemplate(DownloadUrlTemplate.Parse(templateOverride)), c => c.DownloadUrlTemplate.Text);
        }

        return config;
    }

    /// <summary>Runs the package checks under a role prefix and returns the report, or null when any failed.</summary>
    private static PackageReport? ValidatePackage(string packagePath, RepositoryConfiguration config, string? changelogPath, string role, CheckList checks)
    {
        var own = new CheckList();
        var report = Validate(new PackageValidationRequest { PackagePath = packagePath, Configuration = config, ChangelogPath = changelogPath }, own);
        foreach (var check in own.Checks)
        {
            if (check.Passed)
            {
                checks.Pass($"{role}: {check.Name}", check.Detail);
            }
            else
            {
                checks.Fail($"{role}: {check.Name}", check.Detail);
            }
        }

        return report;
    }

    private static PackageReport? Validate(PackageValidationRequest request, CheckList checks)
    {
        try
        {
            return PackageValidator.Validate(request, checks);
        }
        catch (ReleaseCheckException e)
        {
            if (!checks.HasFailures)
            {
                checks.Fail("package", e.Message);
            }

            return null;
        }
    }

    private static void ValidateDocument(byte[] document, string what, RepositoryConfiguration config, PackageReport? stable, PackageReport? testing, string? changelogPath, CheckList checks)
    {
        try
        {
            RepositoryValidator.Validate(new RepositoryValidationRequest
            {
                Document = document,
                What = what,
                Configuration = config,
                StablePackage = stable,
                TestingPackage = testing,
                ChangelogPath = changelogPath,
            }, checks);
        }
        catch (ReleaseCheckException e)
        {
            if (!checks.HasFailures)
            {
                checks.Fail("repository", e.Message);
            }
        }
    }

    private static int Failed(CheckList checks, TextWriter stdout)
    {
        stdout.WriteLine($"FAILED: {Math.Max(1, checks.FailureCount)} check(s) failed.");
        return 1;
    }

    /// <summary>Writes the whole file or nothing: to a sibling temporary file first, then into place.</summary>
    private static void WriteAtomically(string path, byte[] content)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Machine-readable facts about a validated package, for CI summaries and later publishing steps. File names only, never paths.</summary>
    public sealed class PackageSummary
    {
        [JsonPropertyName("internalName"), JsonPropertyOrder(0)]
        public string? InternalName { get; set; }

        [JsonPropertyName("version"), JsonPropertyOrder(1)]
        public string? Version { get; set; }

        [JsonPropertyName("assemblyVersion"), JsonPropertyOrder(2)]
        public string? AssemblyVersion { get; set; }

        [JsonPropertyName("commit"), JsonPropertyOrder(3)]
        public string? Commit { get; set; }

        [JsonPropertyName("dalamudApiLevel"), JsonPropertyOrder(4)]
        public int DalamudApiLevel { get; set; }

        [JsonPropertyName("downloadUrl"), JsonPropertyOrder(5)]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("package"), JsonPropertyOrder(6)]
        public FileSummary? Package { get; set; }

        [JsonPropertyName("entries"), JsonPropertyOrder(7)]
        public List<FileSummary>? Entries { get; set; }

        public static PackageSummary From(PackageReport report, RepositoryConfiguration config) => new()
        {
            InternalName = config.InternalName,
            Version = report.Version.ToString(),
            AssemblyVersion = report.Version.AssemblyVersion.ToString(),
            Commit = report.Commit,
            DalamudApiLevel = config.DalamudApiLevel,
            DownloadUrl = report.DownloadUrl,
            Package = new FileSummary { Name = report.Package.FileName, Size = report.Package.Size, Sha256 = report.Package.Sha256 },
            Entries = new List<FileSummary>
            {
                new() { Name = PluginPackage.DepsEntryName(config.InternalName), Size = report.Package.DepsJson.Length, Sha256 = Checksums.Sha256Hex(report.Package.DepsJson) },
                new() { Name = PluginPackage.AssemblyEntryName(config.InternalName), Size = report.Package.Assembly.Length, Sha256 = Checksums.Sha256Hex(report.Package.Assembly) },
                new() { Name = PluginPackage.ManifestEntryName(config.InternalName), Size = report.Package.ManifestJson.Length, Sha256 = Checksums.Sha256Hex(report.Package.ManifestJson) },
            },
        };
    }

    public sealed class FileSummary
    {
        [JsonPropertyName("name"), JsonPropertyOrder(0)]
        public string? Name { get; set; }

        [JsonPropertyName("size"), JsonPropertyOrder(1)]
        public long Size { get; set; }

        [JsonPropertyName("sha256"), JsonPropertyOrder(2)]
        public string? Sha256 { get; set; }
    }
}
