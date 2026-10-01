using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AetherFrame.ReleaseTools;

/// <summary>What to check a package against. Only the package and the configuration are required.</summary>
public sealed class PackageValidationRequest
{
    public required string PackagePath { get; init; }

    public required RepositoryConfiguration Configuration { get; init; }

    /// <summary>The version Version.props sets, when the package must be this checkout's release.</summary>
    public ProductVersion? ExpectedVersion { get; init; }

    /// <summary>The release tag being built, which must be v&lt;version&gt;.</summary>
    public string? ExpectedTag { get; init; }

    /// <summary>The commit being released, which the DLL's informational version must name.</summary>
    public string? ExpectedCommit { get; init; }

    /// <summary>AetherFrame.csproj, whose manifest fields and SDK must match the package.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>CHANGELOG.md, which must have a section for the package's version.</summary>
    public string? ChangelogPath { get; init; }

    /// <summary>A SHA256SUMS.txt that must list the package with its actual hash.</summary>
    public string? ChecksumsPath { get; init; }
}

/// <summary>A package that passed every check, and the facts the repository metadata is built from.</summary>
public sealed record PackageReport(
    PluginPackage Package,
    PluginManifest Manifest,
    AssemblyFacts Assembly,
    ProductVersion Version,
    string Commit,
    string? ChangelogSection,
    string DownloadUrl);

/// <summary>
/// Every check a release package must pass before it is published or described in the repository
/// metadata. The rules New-ReleasePackage.ps1 applies since 0.1.5 are all here, plus the ones a
/// distribution channel needs: architecture, build commit, API level agreement between the DLL, the
/// manifest, the configuration and the project, and the shape of every entry.
/// </summary>
public static class PackageValidator
{
    // A drive letter not preceded by a letter (so "https://" is not one), a UNC path, or a Unix home.
    private static readonly Regex AbsolutePath = new(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/]|\\\\[A-Za-z0-9]|(?<![A-Za-z0-9.])/(home|Users|root|mnt|tmp|var|opt)/", RegexOptions.CultureInvariant);
    private static readonly Regex Sha1 = new(@"^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);

    // The namespaces of the networking code, which only the plugin's networking preview flavour
    // compiles in (docs/networking/DecisionRegister.md, D9b and P2). No package may hold them.
    private static readonly string[] NetworkingNamespaces = { "AetherFrame.Protocol", "AetherFrame.Personas" };

    public static PackageReport Validate(PackageValidationRequest request, CheckList checks)
    {
        var config = request.Configuration;
        var internalName = config.InternalName;

        var package = PluginPackage.Open(request.PackagePath, internalName, checks);
        if (package is null)
        {
            throw new ReleaseCheckException("the package could not be opened.");
        }

        // The file name: DalamudPackager's latest.zip, or the staged <InternalName>-<version>.zip.
        ProductVersion? nameVersion = null;
        var nameMatch = Regex.Match(package.FileName, "^" + Regex.Escape(internalName) + @"-(\d+\.\d+\.\d+)\.zip$", RegexOptions.CultureInvariant);
        if (package.FileName == "latest.zip")
        {
            checks.Pass("package file name", "latest.zip (DalamudPackager output)");
        }
        else if (nameMatch.Success && ProductVersion.TryParse(nameMatch.Groups[1].Value, out var parsed))
        {
            nameVersion = parsed;
            checks.Pass("package file name", package.FileName);
        }
        else
        {
            checks.Fail("package file name", $"'{package.FileName}' is neither latest.zip nor {internalName}-MAJOR.MINOR.PATCH.zip.");
        }

        // The DLL.
        var assembly = checks.Attempt("assembly", () => PluginAssemblyInspector.Inspect(package.Assembly, PluginPackage.AssemblyEntryName(internalName)), a => $"{a.Name} {a.Version}");
        if (assembly is null)
        {
            throw new ReleaseCheckException("the assembly could not be inspected.");
        }

        checks.Require(assembly.Name == internalName, "assembly name", assembly.Name, $"is '{assembly.Name}', expected '{internalName}'.");
        checks.Require(
            assembly.Machine == Machine.Amd64 && assembly.IsPe32Plus && assembly.IsIlOnly && !assembly.Requires32Bit,
            "assembly architecture",
            "x64, IL only",
            $"machine {assembly.Machine}, {(assembly.IsPe32Plus ? "PE32+" : "PE32")}, {(assembly.IsIlOnly ? "IL only" : "mixed")}{(assembly.Requires32Bit ? ", requires 32-bit" : string.Empty)}; a Dalamud plugin is x64 and IL only.");

        var versionFromAssembly = checks.Attempt("assembly version", () => (ProductVersion?)ProductVersion.FromAssemblyVersion(assembly.Version, "assembly version"), v => v!.Value.AssemblyVersion.ToString());
        if (versionFromAssembly is null)
        {
            throw new ReleaseCheckException("the assembly version could not be read.");
        }

        var version = versionFromAssembly.Value;

        checks.Require(
            assembly.FileVersion == version.AssemblyVersion.ToString(),
            "assembly file version",
            assembly.FileVersion ?? string.Empty,
            $"is '{assembly.FileVersion}', expected '{version.AssemblyVersion}'.");

        var commit = string.Empty;
        var informational = assembly.InformationalVersion ?? string.Empty;
        var informationalMatch = Regex.Match(informational, "^" + Regex.Escape(version.ToString()) + @"\+([0-9a-fA-F]{40})$", RegexOptions.CultureInvariant);
        if (checks.Require(
                informationalMatch.Success,
                "assembly informational version",
                informational,
                $"is '{informational}'; a release build carries '{version}+<40-character commit id>', which the SDK writes when building from a Git checkout."))
        {
            commit = informationalMatch.Groups[1].Value.ToLowerInvariant();
        }

        var dalamud = assembly.References.FirstOrDefault(r => r.Name == "Dalamud");
        if (dalamud is null)
        {
            checks.Fail("Dalamud reference", "the assembly does not reference Dalamud; it is not a Dalamud plugin build.");
        }
        else
        {
            checks.Require(
                dalamud.Version.Major == config.DalamudApiLevel,
                "Dalamud reference",
                $"Dalamud {dalamud.Version}",
                $"compiled against Dalamud {dalamud.Version}, whose API level {dalamud.Version.Major} is not the configured {config.DalamudApiLevel}.");
        }

        // The flavour distribution/repository.json names: a player package holds none of the
        // networking code; a sharing package (the owner's direction of October 1, 2026, "Testing
        // channel gets sharing") holds it, so a release is never the other flavour by mistake.
        var networking = assembly.TypeNamespaces
            .Where(ns => NetworkingNamespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal)))
            .ToList();
        if (config.Flavour == ReleaseFlavour.Sharing)
        {
            checks.Require(
                networking.Count > 0,
                "plugin flavour",
                "sharing build, with the sharing code",
                "the DLL holds no sharing code; distribution/repository.json says releases carry sharing (releaseFlavour).");
        }
        else
        {
            checks.Require(
                networking.Count == 0,
                "plugin flavour",
                "player build, no networking code",
                $"the DLL holds types in {string.Join(", ", networking)}; it is a networking preview build, which is never packaged for players.");
        }

        // The manifest.
        var manifest = checks.Attempt("manifest", () => PluginManifest.Parse(package.ManifestJson, PluginPackage.ManifestEntryName(internalName)), m => "parsed, all keys known");
        if (manifest is null)
        {
            throw new ReleaseCheckException("the manifest could not be parsed.");
        }
        CheckManifest(manifest, version, config, checks);
        CheckText("manifest text", package.ManifestJson, checks);

        // The deps.json.
        CheckDeps(package.DepsJson, internalName, version, checks);
        CheckText("deps.json text", package.DepsJson, checks);

        // One version everywhere.
        if (nameVersion is not null)
        {
            checks.Require(nameVersion.Value == version, "package file name version", version.ToString(), $"the file name says {nameVersion}, the DLL is {version}.");
        }

        if (request.ExpectedVersion is not null)
        {
            checks.Require(request.ExpectedVersion.Value == version, "Version.props", version.ToString(), $"Version.props says {request.ExpectedVersion}, the package is {version}.");
        }

        if (request.ExpectedTag is not null)
        {
            checks.Require(request.ExpectedTag == version.Tag, "release tag", request.ExpectedTag, $"the tag is '{request.ExpectedTag}', the package is {version} (tag {version.Tag}).");
        }

        if (request.ExpectedCommit is not null)
        {
            var expected = request.ExpectedCommit.Trim().ToLowerInvariant();
            if (!Sha1.IsMatch(expected))
            {
                checks.Fail("build commit", $"the expected commit '{request.ExpectedCommit}' is not a 40-character commit id.");
            }
            else
            {
                checks.Require(commit == expected, "build commit", commit, $"the package was built from {commit}, not the expected {expected}.");
            }
        }

        if (request.ProjectPath is not null)
        {
            CheckProject(request.ProjectPath, manifest, config, checks);
        }

        string? changelogSection = null;
        if (request.ChangelogPath is not null)
        {
            changelogSection = checks.Attempt("changelog section", () => ChangelogSections.Read(request.ChangelogPath, version), s => $"{s.Length} characters for {version}");
        }

        if (request.ChecksumsPath is not null)
        {
            var listed = checks.Attempt("listed checksum", () => Checksums.Listed(request.ChecksumsPath, package.FileName), h => h);
            if (listed is not null)
            {
                checks.Require(listed == package.Sha256, "package checksum", package.Sha256, $"{Path.GetFileName(request.ChecksumsPath)} lists {listed}, the package is {package.Sha256}.");
            }
        }

        var downloadUrl = checks.Attempt("download URL", () => config.DownloadUrl(version), u => u);

        checks.ThrowIfFailed();
        return new PackageReport(package, manifest, assembly, version, commit, changelogSection, downloadUrl!);
    }

    private static void CheckManifest(PluginManifest manifest, ProductVersion version, RepositoryConfiguration config, CheckList checks)
    {
        checks.Require(manifest.InternalName == config.InternalName, "manifest InternalName", manifest.InternalName ?? string.Empty, $"is '{manifest.InternalName}', expected '{config.InternalName}'.");
        checks.Require(
            manifest.AssemblyVersion == version.AssemblyVersion.ToString(),
            "manifest AssemblyVersion",
            manifest.AssemblyVersion ?? string.Empty,
            $"is '{manifest.AssemblyVersion}', the DLL is {version.AssemblyVersion}.");
        checks.Require(
            manifest.DalamudApiLevel == config.DalamudApiLevel,
            "manifest DalamudApiLevel",
            manifest.DalamudApiLevel?.ToString() ?? string.Empty,
            $"is {manifest.DalamudApiLevel?.ToString() ?? "missing"}, expected {config.DalamudApiLevel}.");
        // A package released before the source repository moved names the address it had then; any later
        // version must name the current one (RepositoryConfiguration.Previous).
        var previousAddress = config.Previous is not null && manifest.RepoUrl == config.Previous.SourceRepositoryUrl;
        checks.Require(
            manifest.RepoUrl == config.SourceRepositoryUrl || config.IsPreviousRepoUrl(manifest.RepoUrl, version),
            "manifest RepoUrl",
            previousAddress ? $"{manifest.RepoUrl} (the address before the move, which {version} was released under)" : manifest.RepoUrl ?? string.Empty,
            previousAddress
                ? $"is '{manifest.RepoUrl}', the address before the move, which only packages up to {config.Previous!.LastVersion} carry; {version} must name '{config.SourceRepositoryUrl}'."
                : $"is '{manifest.RepoUrl}', expected '{config.SourceRepositoryUrl}'.");

        var empty = new List<string>();
        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            empty.Add("Name");
        }

        if (string.IsNullOrWhiteSpace(manifest.Author))
        {
            empty.Add("Author");
        }

        if (string.IsNullOrWhiteSpace(manifest.Punchline))
        {
            empty.Add("Punchline");
        }

        if (string.IsNullOrWhiteSpace(manifest.Description))
        {
            empty.Add("Description");
        }

        checks.Require(empty.Count == 0, "manifest installer fields", "Name, Author, Punchline, Description", $"empty: {string.Join(", ", empty)}.");

        checks.Require(
            manifest.ApplicableVersion is null or "any",
            "manifest ApplicableVersion",
            manifest.ApplicableVersion ?? "(default: any)",
            $"is '{manifest.ApplicableVersion}'; the plugin is not tied to a game version, so it must be 'any'.");

        if (manifest.MinimumDalamudVersion is not null)
        {
            checks.Require(Version.TryParse(manifest.MinimumDalamudVersion, out _), "manifest MinimumDalamudVersion", manifest.MinimumDalamudVersion, $"'{manifest.MinimumDalamudVersion}' is not a version.");
        }

        if (manifest.IconUrl is not null)
        {
            checks.Attempt("manifest IconUrl", () => Urls.ValidateHttps(manifest.IconUrl, "IconUrl").OriginalString, u => u);
        }

        if (manifest.ImageUrls is not null)
        {
            checks.Attempt("manifest ImageUrls", () =>
            {
                if (manifest.ImageUrls.Count > 5)
                {
                    throw new ReleaseCheckException($"{manifest.ImageUrls.Count} image URLs; the installer shows at most 5.");
                }

                foreach (var url in manifest.ImageUrls)
                {
                    Urls.ValidateHttps(url, "ImageUrls entry");
                }

                return manifest.ImageUrls.Count;
            }, n => $"{n} image URL(s)");
        }

        if (manifest.Tags is not null)
        {
            checks.Attempt("manifest Tags", () =>
            {
                if (manifest.Tags.Any(string.IsNullOrWhiteSpace))
                {
                    throw new ReleaseCheckException("an empty tag.");
                }

                var duplicates = manifest.Tags.GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (duplicates.Count > 0)
                {
                    throw new ReleaseCheckException($"repeated tag(s): {string.Join(", ", duplicates)}.");
                }

                return string.Join(", ", manifest.Tags);
            }, t => t);
        }
    }

    private static void CheckDeps(byte[] deps, string internalName, ProductVersion version, CheckList checks)
    {
        checks.Attempt("deps.json", () =>
        {
            using var document = StrictJson.ParseDocument(deps, PluginPackage.DepsEntryName(internalName));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ReleaseCheckException("deps.json is not a JSON object.");
            }

            if (!root.TryGetProperty("runtimeTarget", out var runtimeTarget) || !runtimeTarget.TryGetProperty("name", out var targetName) || targetName.ValueKind != JsonValueKind.String)
            {
                throw new ReleaseCheckException("deps.json has no runtimeTarget.name.");
            }

            var key = $"{internalName}/{version}";
            if (!root.TryGetProperty("targets", out var targets) || !targets.TryGetProperty(targetName.GetString()!, out var target) || !target.TryGetProperty(key, out _))
            {
                throw new ReleaseCheckException($"deps.json does not list {key} under target '{targetName.GetString()}'; the deps.json belongs to another build.");
            }

            if (!root.TryGetProperty("libraries", out var libraries) || !libraries.TryGetProperty(key, out var library)
                || !library.TryGetProperty("type", out var type) || type.GetString() != "project")
            {
                throw new ReleaseCheckException($"deps.json does not describe {key} as the project library.");
            }

            return $"{key} on {targetName.GetString()}";
        }, d => d);
    }

    private static void CheckText(string name, byte[] utf8, CheckList checks)
    {
        var text = Encoding.UTF8.GetString(utf8);
        var match = AbsolutePath.Match(text);
        checks.Require(!match.Success, name, "no local paths", $"contains what looks like a local path: '{Excerpt(text, match.Index)}'.");
    }

    private static void CheckProject(string projectPath, PluginManifest manifest, RepositoryConfiguration config, CheckList checks)
    {
        XDocument? project = checks.Attempt("project file", () =>
        {
            if (!File.Exists(projectPath))
            {
                throw new ReleaseCheckException($"not found at {projectPath}.");
            }

            try
            {
                return XDocument.Load(projectPath);
            }
            catch (System.Xml.XmlException e)
            {
                throw new ReleaseCheckException($"{projectPath} is not well-formed XML: {e.Message}");
            }
        }, _ => Path.GetFileName(projectPath));
        if (project is null)
        {
            return;
        }

        var sdk = (string?)project.Root?.Attribute("Sdk") ?? string.Empty;
        var sdkMatch = Regex.Match(sdk, @"^Dalamud\.NET\.Sdk/(\d+)\.", RegexOptions.CultureInvariant);
        if (checks.Require(sdkMatch.Success, "project SDK", sdk, $"the project uses '{sdk}', not Dalamud.NET.Sdk/<API level>.x.y."))
        {
            var major = int.Parse(sdkMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            checks.Require(major == config.DalamudApiLevel, "project SDK API level", major.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Dalamud.NET.Sdk {major} does not match the configured API level {config.DalamudApiLevel}.");
        }

        // The project file is read as XML, not evaluated by MSBuild, so a field set twice (say, under a
        // Condition) has no single value to compare and fails below.
        var repeated = new List<string>();
        string? Property(string name)
        {
            var values = project.Root?.Elements("PropertyGroup").Elements(name).ToList() ?? new List<XElement>();
            if (values.Count > 1)
            {
                repeated.Add(name);
            }

            return values.Count == 1 ? values[0].Value.Trim() : null;
        }

        var differences = new List<string>();
        void Compare(string name, string? projectValue, string? manifestValue)
        {
            if (projectValue != manifestValue)
            {
                differences.Add(name);
            }
        }

        Compare("Name", Property("Name"), manifest.Name);
        Compare("Author", Property("Author"), manifest.Author);
        Compare("Punchline", Property("Punchline"), manifest.Punchline);
        Compare("Description", Property("Description"), manifest.Description);
        Compare("RepoUrl", Property("RepoUrl"), manifest.RepoUrl);
        Compare("IconUrl", Property("IconUrl"), manifest.IconUrl);
        var projectTags = Property("Tags")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!(projectTags ?? Array.Empty<string>()).SequenceEqual(manifest.Tags ?? new List<string>(), StringComparer.Ordinal))
        {
            differences.Add("Tags");
        }

        if (repeated.Count > 0)
        {
            checks.Fail("project manifest fields", $"{string.Join(", ", repeated)} {(repeated.Count == 1 ? "is" : "are")} set more than once in {Path.GetFileName(projectPath)}; each manifest field must be set exactly once, since this check reads the project file without evaluating MSBuild conditions.");
            return;
        }

        checks.Require(differences.Count == 0, "project manifest fields", "match the packaged manifest", $"the packaged manifest differs from the project in {string.Join(", ", differences)}; the package was built from other sources.");
    }

    private static string Excerpt(string text, int index)
    {
        var start = Math.Max(0, index - 10);
        var length = Math.Min(50, text.Length - start);
        return text.Substring(start, length).Replace("\n", " ", StringComparison.Ordinal);
    }
}
