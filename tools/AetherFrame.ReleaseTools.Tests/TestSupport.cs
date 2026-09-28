using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

/// <summary>A fresh directory per test, deleted afterwards.</summary>
internal sealed class TempDirectory : IDisposable
{
    internal TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aetherframe-release-tools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The checkout the tests run in, for the committed distribution files and the plugin's own build output.</summary>
internal static class RepositoryPaths
{
    internal static DirectoryInfo Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "Version.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!;
    }

    internal static string File(string relativePath) => Path.Combine(Root().FullName, relativePath);

    /// <summary>
    /// The package DalamudPackager built: AETHERFRAME_RELEASE_PACKAGE when set (it must exist), else
    /// this checkout's Release or Debug build of the plugin, or null when that hasn't been built.
    /// </summary>
    internal static string? BuiltPackage()
    {
        var configured = Environment.GetEnvironmentVariable("AETHERFRAME_RELEASE_PACKAGE");
        if (!string.IsNullOrEmpty(configured))
        {
            Assert.True(System.IO.File.Exists(configured), $"AETHERFRAME_RELEASE_PACKAGE points at a missing file: {configured}");
            return configured;
        }

        // bin/<Configuration>/<tfm>/ here; the plugin builds to AetherFrame/bin/x64/<Configuration>/AetherFrame/latest.zip.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var local = File(Path.Combine("AetherFrame", "bin", "x64", configuration, "AetherFrame", "latest.zip"));
        return System.IO.File.Exists(local) ? local : null;
    }
}

/// <summary>
/// Builds the pieces of a release in a temporary directory: a real .NET assembly with chosen name,
/// versions, architecture and references (via PersistedAssemblyBuilder, so the checks read actual PE
/// metadata), DalamudPackager-shaped manifests, deps.json files, ZIPs with arbitrary entry names,
/// and the repository files around them.
/// </summary>
internal static class TestPackages
{
    internal const string InternalName = "AetherFrame";
    internal const string Commit = "ab26da043832712af955e92815f59fb517a7db46";
    internal const string RepoUrl = "https://github.com/richhiiee/AetherFrame";
    internal const string IconUrl = "https://raw.githubusercontent.com/richhiiee/AetherFrameAssets/master/images/icon.png";
    internal const string DownloadTemplate = "https://github.com/richhiiee/AetherFrame/releases/download/v{version}/{package}";
    internal const string DryRunTemplate = "https://dry-run.invalid/richhiiee/AetherFrame/releases/download/v{version}/{package}";

    internal static byte[] Assembly(
        string name = InternalName,
        string version = "0.1.5",
        string? assemblyVersion = null,
        string? fileVersion = "",
        string? informationalVersion = "",
        Machine machine = Machine.Amd64,
        CorFlags flags = CorFlags.ILOnly,
        int? dalamudMajor = 15)
    {
        assemblyVersion ??= version + ".0";
        if (fileVersion == string.Empty)
        {
            fileVersion = version + ".0";
        }

        if (informationalVersion == string.Empty)
        {
            informationalVersion = version + "+" + Commit;
        }

        var attributes = new List<CustomAttributeBuilder>();
        if (fileVersion is not null)
        {
            attributes.Add(new CustomAttributeBuilder(typeof(AssemblyFileVersionAttribute).GetConstructor(new[] { typeof(string) })!, new object[] { fileVersion }));
        }

        if (informationalVersion is not null)
        {
            attributes.Add(new CustomAttributeBuilder(typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) })!, new object[] { informationalVersion }));
        }

        var builder = new PersistedAssemblyBuilder(new AssemblyName(name) { Version = Version.Parse(assemblyVersion) }, typeof(object).Assembly, attributes);
        var module = builder.DefineDynamicModule(name);
        module.DefineType("Plugin", TypeAttributes.Public | TypeAttributes.Class).CreateType();

        var metadata = builder.GenerateMetadata(out var ilStream, out var fieldData);
        if (dalamudMajor is not null)
        {
            metadata.AddAssemblyReference(metadata.GetOrAddString("Dalamud"), new Version(dalamudMajor.Value, 0, 0, 0), default, default, 0, default);
        }

        var characteristics = Characteristics.ExecutableImage | Characteristics.Dll;
        if (machine == Machine.Amd64)
        {
            characteristics |= Characteristics.LargeAddressAware;
        }

        var pe = new ManagedPEBuilder(new PEHeaderBuilder(machine: machine, imageCharacteristics: characteristics), new MetadataRootBuilder(metadata), ilStream, fieldData, flags: flags);
        var blob = new BlobBuilder();
        pe.Serialize(blob);
        return blob.ToArray();
    }

    /// <summary>The fields DalamudPackager writes for AetherFrame, in its order.</summary>
    internal static Dictionary<string, object?> ManifestFields(string version = "0.1.5") => new(StringComparer.Ordinal)
    {
        ["Author"] = "richhiiee",
        ["Name"] = "AetherFrame",
        ["InternalName"] = InternalName,
        ["AssemblyVersion"] = version + ".0",
        ["Description"] = "Design character Plates: a test description.",
        ["ApplicableVersion"] = "any",
        ["RepoUrl"] = RepoUrl,
        ["Tags"] = new[] { "aetherframe", "plates" },
        ["DalamudApiLevel"] = 15,
        ["LoadRequiredState"] = 0,
        ["LoadSync"] = false,
        ["CanUnloadAsync"] = false,
        ["LoadPriority"] = 0,
        ["IconUrl"] = IconUrl,
        ["Punchline"] = "Design character Plates.",
        ["AcceptsFeedback"] = true,
    };

    internal static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });

    internal static byte[] Manifest(string version = "0.1.5", Action<Dictionary<string, object?>>? mutate = null)
    {
        var fields = ManifestFields(version);
        mutate?.Invoke(fields);
        return Json(fields);
    }

    internal static byte[] Deps(string version = "0.1.5", string name = InternalName) => Encoding.UTF8.GetBytes($$"""
        {
          "runtimeTarget": {
            "name": ".NETCoreApp,Version=v10.0",
            "signature": ""
          },
          "compilationOptions": {},
          "targets": {
            ".NETCoreApp,Version=v10.0": {
              "{{name}}/{{version}}": {
                "runtime": {
                  "{{name}}.dll": {}
                }
              }
            }
          },
          "libraries": {
            "{{name}}/{{version}}": {
              "type": "project",
              "serviceable": false,
              "sha512": ""
            }
          }
        }
        """);

    /// <summary>A ZIP with exactly these entries, names taken as given (so traversal, duplicates and folders can be built).</summary>
    internal static string Zip(string path, params (string Name, byte[] Content)[] entries)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(content);
        }

        return path;
    }

    /// <summary>
    /// Rewrites one entry of a ZIP in place, for damaged-archive tests: the callback gets the file's bytes
    /// and the offsets of the entry's central directory header and local header (ZIP specification
    /// layout: central +10 / local +8 compression method, central +24 / local +22 uncompressed size).
    /// </summary>
    internal static void RewriteEntry(string zipPath, string entryName, Action<byte[], int, int> rewrite)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var name = Encoding.ASCII.GetBytes(entryName);
        for (var i = 0; i + 46 + name.Length <= bytes.Length; i++)
        {
            if (BitConverter.ToUInt32(bytes, i) == 0x02014b50 && BitConverter.ToUInt16(bytes, i + 28) == name.Length && bytes.AsSpan(i + 46, name.Length).SequenceEqual(name))
            {
                rewrite(bytes, i, (int)BitConverter.ToUInt32(bytes, i + 42));
                File.WriteAllBytes(zipPath, bytes);
                return;
            }
        }

        throw new InvalidOperationException($"no central directory header for {entryName}");
    }

    /// <summary>A valid package for the version, with optional replacements and extra entries.</summary>
    internal static string Package(
        TempDirectory directory,
        string version = "0.1.5",
        string? fileName = null,
        byte[]? assembly = null,
        byte[]? manifest = null,
        byte[]? deps = null,
        IEnumerable<(string Name, byte[] Content)>? extraEntries = null,
        bool omitAssembly = false,
        bool omitManifest = false,
        bool omitDeps = false)
    {
        var entries = new List<(string, byte[])>();
        if (!omitDeps)
        {
            entries.Add((InternalName + ".deps.json", deps ?? Deps(version)));
        }

        if (!omitAssembly)
        {
            entries.Add((InternalName + ".dll", assembly ?? Assembly(version: version)));
        }

        if (!omitManifest)
        {
            entries.Add((InternalName + ".json", manifest ?? Manifest(version)));
        }

        if (extraEntries is not null)
        {
            entries.AddRange(extraEntries);
        }

        return Zip(directory.File(fileName ?? $"{InternalName}-{version}.zip"), entries.ToArray());
    }

    internal const string PluginMasterUrl = "https://raw.githubusercontent.com/richhiiee/AetherFrame/refs/heads/plugin-repository/pluginmaster.json";

    internal static string ConfigJson(string template = DownloadTemplate, int apiLevel = 15, string internalName = InternalName, string sourceRepositoryUrl = RepoUrl, string pluginMasterUrl = PluginMasterUrl) => $$"""
        {
          "$comment": "test configuration",
          "internalName": "{{internalName}}",
          "dalamudApiLevel": {{apiLevel}},
          "sourceRepositoryUrl": "{{sourceRepositoryUrl}}",
          "pluginMasterUrl": "{{pluginMasterUrl}}",
          "downloadUrlTemplate": "{{template}}"
        }
        """;

    internal static string Config(TempDirectory directory, string? json = null)
    {
        var path = directory.File("repository.json");
        File.WriteAllText(path, json ?? ConfigJson());
        return path;
    }

    internal static RepositoryConfiguration Configuration(string? json = null) => RepositoryConfiguration.Parse(Encoding.UTF8.GetBytes(json ?? ConfigJson()), "test configuration");

    internal static string ChangelogText(params (string Version, string Body)[] sections)
    {
        var builder = new StringBuilder("# Changelog\n\nAll notable changes.\n\n## [Unreleased]\n\nNothing yet.\n\n");
        foreach (var (version, body) in sections)
        {
            builder.Append("## [").Append(version).Append("] - 2026-09-26\n\n").Append(body).Append("\n\n");
        }

        builder.Append("[Unreleased]: https://github.com/richhiiee/AetherFrame/compare/v0.1.5...HEAD\n");
        foreach (var (version, _) in sections)
        {
            builder.Append('[').Append(version).Append("]: https://github.com/richhiiee/AetherFrame/releases/tag/v").Append(version).Append('\n');
        }

        return builder.ToString();
    }

    internal static string Changelog(TempDirectory directory, params (string Version, string Body)[] sections)
    {
        var path = directory.File("CHANGELOG.md");
        File.WriteAllText(path, ChangelogText(sections.Length == 0 ? new[] { ("0.1.5", "### Fixed\n\n- A thing.") } : sections));
        return path;
    }

    internal static string VersionProps(TempDirectory directory, string version = "0.1.5")
    {
        var path = directory.File("Version.props");
        File.WriteAllText(path, $"<Project>\n  <PropertyGroup>\n    <Version>{version}</Version>\n  </PropertyGroup>\n</Project>\n");
        return path;
    }

    internal static string Csproj(TempDirectory directory, string sdk = "Dalamud.NET.Sdk/15.0.0", Dictionary<string, object?>? fields = null)
    {
        var path = directory.File("AetherFrame.csproj");
        File.WriteAllText(path, CsprojText(sdk, fields));
        return path;
    }

    internal static string CsprojText(string sdk = "Dalamud.NET.Sdk/15.0.0", Dictionary<string, object?>? fields = null)
    {
        fields ??= ManifestFields();
        var tags = string.Join(";", (string[])fields["Tags"]!);
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="{sdk}">
              <Import Project="..\Version.props" />
              <PropertyGroup>
                <Author>{fields["Author"]}</Author>
                <Name>{fields["Name"]}</Name>
                <Punchline>{fields["Punchline"]}</Punchline>
                <Description>{fields["Description"]}</Description>
                <RepoUrl>{fields["RepoUrl"]}</RepoUrl>
                <Tags>{tags}</Tags>
                <IconUrl>{fields["IconUrl"]}</IconUrl>
              </PropertyGroup>
            </Project>
            """;
    }

    internal static string Checksums(TempDirectory directory, string packagePath, string? hash = null)
    {
        var path = directory.File("SHA256SUMS.txt");
        File.WriteAllText(path, $"{hash ?? ReleaseTools.Checksums.Sha256Hex(packagePath)}  {Path.GetFileName(packagePath)}\n");
        return path;
    }

    /// <summary>Runs the package checks the way the command does, returning the check list and the report (null when failed).</summary>
    internal static (CheckList Checks, PackageReport? Report) Validate(PackageValidationRequest request)
    {
        var checks = new CheckList();
        try
        {
            return (checks, PackageValidator.Validate(request, checks));
        }
        catch (ReleaseCheckException)
        {
            Assert.True(checks.HasFailures, "a validation exception without a recorded failure");
            return (checks, null);
        }
    }

    internal static PackageReport ValidReport(TempDirectory directory, string version = "0.1.5", RepositoryConfiguration? configuration = null, string? changelogPath = null)
    {
        var (checks, report) = Validate(new PackageValidationRequest
        {
            PackagePath = Package(directory, version),
            Configuration = configuration ?? Configuration(),
            ChangelogPath = changelogPath,
        });
        Assert.True(report is not null, string.Join("\n", checks.Checks.Where(c => !c.Passed).Select(c => c.Name + ": " + c.Detail)));
        return report!;
    }

    internal static string Failure(CheckList checks, string name)
    {
        var check = checks.Checks.FirstOrDefault(c => c.Name == name);
        Assert.True(check is not null, $"no check named '{name}'; checks: {string.Join(", ", checks.Checks.Select(c => c.Name))}");
        Assert.False(check!.Passed, $"check '{name}' passed: {check.Detail}");
        return check.Detail;
    }

    internal static void AllPassed(CheckList checks) =>
        Assert.True(!checks.HasFailures, string.Join("\n", checks.Checks.Where(c => !c.Passed).Select(c => c.Name + ": " + c.Detail)));
}

/// <summary>What a test release differs in from a consistent, published release of its version.</summary>
internal sealed class ReleaseOptions
{
    public bool Draft { get; init; }

    public bool Prerelease { get; init; } = true;

    public string PublishedAt { get; init; } = "2026-09-28T12:00:00Z";

    /// <summary>The commit the DLL says it was built from.</summary>
    public string BuildCommit { get; init; } = TestPackages.Commit;

    /// <summary>The commit the tag points at.</summary>
    public string TagCommit { get; init; } = TestPackages.Commit;

    public string TagObjectType { get; init; } = "tag";

    public bool OnDefaultBranch { get; init; } = true;

    /// <summary>The version Version.props sets at the tag; the release's own when null.</summary>
    public string? TaggedVersion { get; init; }

    /// <summary>SHA256SUMS.txt; a correct line for the package when null.</summary>
    public string? ChecksumsText { get; init; }

    /// <summary>Changes the release description after it was built from the files.</summary>
    public Action<JsonObject>? Release { get; init; }

    /// <summary>Runs last, with the release directory, to damage or remove a file.</summary>
    public Action<string>? After { get; init; }
}

/// <summary>
/// Release directories as .github/scripts/custom-repository.sh fetch-release lays them out: the GitHub
/// Release description (shaped like the REST API's, extra fields included), its two downloaded assets,
/// the tag facts and the tagged sources. A published release of the version, built from the tagged
/// commit on the default branch, unless <see cref="ReleaseOptions"/> changes one fact.
/// </summary>
internal static class TestReleases
{
    internal const long ReleaseId = 397792887;

    internal static string Create(string releasesDirectory, string version, ReleaseOptions? options = null)
    {
        options ??= new ReleaseOptions();
        var tag = "v" + version;
        var directory = Path.Combine(releasesDirectory, tag);
        var source = Path.Combine(directory, "source");
        Directory.CreateDirectory(source);

        var packageName = $"{TestPackages.InternalName}-{version}.zip";
        var package = TestPackages.Zip(
            Path.Combine(directory, packageName),
            (TestPackages.InternalName + ".deps.json", TestPackages.Deps(version)),
            (TestPackages.InternalName + ".dll", TestPackages.Assembly(version: version, informationalVersion: version + "+" + options.BuildCommit)),
            (TestPackages.InternalName + ".json", TestPackages.Manifest(version)));
        var checksums = Path.Combine(directory, "SHA256SUMS.txt");
        File.WriteAllText(checksums, options.ChecksumsText ?? $"{Checksums.Sha256Hex(package)}  {packageName}\n");

        // A draft has no tag yet as far as GitHub's addresses are concerned.
        var place = options.Draft ? "untagged-0123456789abcdef0123" : tag;
        var release = new JsonObject
        {
            ["url"] = $"https://api.github.com/repos/richhiiee/AetherFrame/releases/{ReleaseId}",
            ["html_url"] = $"https://github.com/richhiiee/AetherFrame/releases/tag/{place}",
            ["id"] = ReleaseId,
            ["author"] = new JsonObject { ["login"] = "richhiiee", ["id"] = 1 },
            ["tag_name"] = tag,
            ["target_commitish"] = "master",
            ["name"] = $"AetherFrame {version}",
            ["draft"] = options.Draft,
            ["immutable"] = false,
            ["prerelease"] = options.Prerelease,
            ["created_at"] = "2026-09-27T20:04:13Z",
            ["published_at"] = options.Draft ? null : options.PublishedAt,
            ["assets"] = new JsonArray(Asset(593676853, package, place), Asset(593676854, checksums, place)),
            ["body"] = "Release notes.",
        };
        options.Release?.Invoke(release);
        File.WriteAllText(Path.Combine(directory, "release.json"), release.ToJsonString());

        File.WriteAllText(Path.Combine(directory, "tag.json"), new JsonObject
        {
            ["tag"] = tag,
            ["objectType"] = options.TagObjectType,
            ["commit"] = options.TagCommit,
            ["defaultBranch"] = "master",
            ["onDefaultBranch"] = options.OnDefaultBranch,
        }.ToJsonString());

        File.WriteAllText(Path.Combine(source, "Version.props"), $"<Project>\n  <PropertyGroup>\n    <Version>{options.TaggedVersion ?? version}</Version>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(source, "AetherFrame.csproj"), TestPackages.CsprojText());
        File.WriteAllText(Path.Combine(source, "CHANGELOG.md"), TestPackages.ChangelogText((version, $"### Fixed\n\n- Changes in {version}.")));
        options.After?.Invoke(directory);
        return directory;
    }

    /// <summary>One asset of a release description, for tests that change it.</summary>
    internal static JsonObject AssetNamed(JsonObject release, string name) =>
        release["assets"]!.AsArray().Select(a => a!.AsObject()).Single(a => (string?)a["name"] == name);

    private static JsonObject Asset(long id, string path, string place)
    {
        var name = Path.GetFileName(path);
        return new JsonObject
        {
            ["url"] = $"https://api.github.com/repos/richhiiee/AetherFrame/releases/assets/{id}",
            ["id"] = id,
            ["name"] = name,
            ["label"] = string.Empty,
            ["content_type"] = name.EndsWith(".zip", StringComparison.Ordinal) ? "application/zip" : "text/plain; charset=utf-8",
            ["state"] = "uploaded",
            ["size"] = new FileInfo(path).Length,
            ["digest"] = "sha256:" + Checksums.Sha256Hex(path),
            ["download_count"] = 0,
            ["browser_download_url"] = $"https://github.com/richhiiee/AetherFrame/releases/download/{place}/{name}",
        };
    }
}
