using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

/// <summary>Every rule a release package must satisfy, each with the package that breaks it.</summary>
public class PackageValidatorTests
{
    [Fact]
    public void ValidPackage_PassesEveryCheck_AndReportsItsFacts()
    {
        using var directory = new TempDirectory();
        var package = TestPackages.Package(directory);
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = package,
            Configuration = TestPackages.Configuration(),
            ExpectedVersion = new ProductVersion(0, 1, 5),
            ExpectedTag = "v0.1.5",
            ExpectedCommit = TestPackages.Commit.ToUpperInvariant(),
            ProjectPath = TestPackages.Csproj(directory),
            ChangelogPath = TestPackages.Changelog(directory),
            ChecksumsPath = TestPackages.Checksums(directory, package),
        });

        TestPackages.AllPassed(checks);
        Assert.NotNull(report);
        Assert.Equal(new ProductVersion(0, 1, 5), report!.Version);
        Assert.Equal(TestPackages.Commit, report.Commit);
        Assert.Equal("### Fixed\n\n- A thing.", report.ChangelogSection);
        Assert.Equal("https://github.com/QuietFoxLabs/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip", report.DownloadUrl);
        Assert.Equal("AetherFrame-0.1.5.zip", report.Package.FileName);
        Assert.Equal(Checksums.Sha256Hex(package), report.Package.Sha256);
        Assert.Equal(new[] { "AetherFrame.deps.json", "AetherFrame.dll", "AetherFrame.json" }, report.Package.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Contains(checks.Checks, c => c.Name == "Dalamud reference" && c.Passed && c.Detail == "Dalamud 15.0.0.0");
        Assert.Contains(checks.Checks, c => c.Name == "plugin flavour" && c.Passed && c.Detail == "player build, no networking code");
    }

    [Fact]
    public void LatestZip_IsAcceptedAsDalamudPackagerOutput()
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, fileName: "latest.zip"),
            Configuration = TestPackages.Configuration(),
        });

        TestPackages.AllPassed(checks);
        Assert.Equal("latest.zip", report!.Package.FileName);
    }

    [Fact]
    public void MissingPackage_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = directory.File("AetherFrame-0.1.5.zip"),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains("not found", TestPackages.Failure(checks, "package file"));
    }

    [Fact]
    public void NotAZip_Fails()
    {
        using var directory = new TempDirectory();
        var path = directory.File("AetherFrame-0.1.5.zip");
        File.WriteAllText(path, "this is not a zip");

        var (checks, _) = TestPackages.Validate(new PackageValidationRequest { PackagePath = path, Configuration = TestPackages.Configuration() });

        Assert.Contains("not a ZIP archive", TestPackages.Failure(checks, "package format"));
    }

    [Theory]
    [InlineData("central directory")]
    [InlineData("entry data")]
    [InlineData("compression method")]
    public void DamagedZip_IsAFailedCheck_NotACrash(string damage)
    {
        using var directory = new TempDirectory();
        var path = TestPackages.Package(directory);
        TestPackages.RewriteEntry(path, damage == "central directory" ? "AetherFrame.json" : "AetherFrame.dll", (bytes, central, local) =>
        {
            switch (damage)
            {
                case "central directory":
                    bytes[central] = 0;
                    break;
                case "entry data":
                    var data = local + 30 + BitConverter.ToUInt16(bytes, local + 26) + BitConverter.ToUInt16(bytes, local + 28);
                    for (var i = data + 2; i < data + 64; i++)
                    {
                        bytes[i] ^= 0xFF;
                    }

                    break;
                default:
                    bytes[central + 10] = 14; // LZMA, which .NET cannot read
                    bytes[local + 8] = 14;
                    break;
            }
        });

        var (checks, report) = TestPackages.Validate(new PackageValidationRequest { PackagePath = path, Configuration = TestPackages.Configuration() });

        Assert.Null(report);
        Assert.Contains("damaged or uses a feature .NET cannot read", TestPackages.Failure(checks, "package format"));
    }

    [Fact]
    public void EntryShorterThanItsDeclaredSize_Fails()
    {
        using var directory = new TempDirectory();
        var path = TestPackages.Package(directory);
        var actual = TestPackages.Manifest().Length;
        TestPackages.RewriteEntry(path, "AetherFrame.json", (bytes, central, local) =>
        {
            BitConverter.GetBytes((uint)actual + 100).CopyTo(bytes, central + 24);
            BitConverter.GetBytes((uint)actual + 100).CopyTo(bytes, local + 22);
        });

        var (checks, report) = TestPackages.Validate(new PackageValidationRequest { PackagePath = path, Configuration = TestPackages.Configuration() });

        Assert.Null(report);
        Assert.Contains($"{actual} bytes when decompressed, although the directory says {actual + 100}", TestPackages.Failure(checks, "size of AetherFrame.json"));
    }

    [Theory]
    [InlineData("0.1.5", "0.1.6", "Version.props", "Version.props says 0.1.6, the package is 0.1.5")]
    public void VersionPropsMismatch_Fails(string packageVersion, string propsVersion, string check, string message)
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, packageVersion),
            Configuration = TestPackages.Configuration(),
            ExpectedVersion = ProductVersion.Parse(propsVersion, "test"),
        });

        Assert.Null(report);
        Assert.Contains(message, TestPackages.Failure(checks, check));
    }

    [Fact]
    public void TagMismatch_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory),
            Configuration = TestPackages.Configuration(),
            ExpectedTag = "v0.1.6",
        });

        Assert.Contains("the tag is 'v0.1.6', the package is 0.1.5", TestPackages.Failure(checks, "release tag"));
    }

    [Fact]
    public void CommitMismatch_Fails_AndMalformedExpectedCommitFails()
    {
        using var directory = new TempDirectory();
        var package = TestPackages.Package(directory);
        var configuration = TestPackages.Configuration();

        var (mismatch, _) = TestPackages.Validate(new PackageValidationRequest { PackagePath = package, Configuration = configuration, ExpectedCommit = new string('f', 40) });
        Assert.Contains($"built from {TestPackages.Commit}, not the expected {new string('f', 40)}", TestPackages.Failure(mismatch, "build commit"));

        var (malformed, _) = TestPackages.Validate(new PackageValidationRequest { PackagePath = package, Configuration = configuration, ExpectedCommit = "ab26da0" });
        Assert.Contains("not a 40-character commit id", TestPackages.Failure(malformed, "build commit"));
    }

    [Fact]
    public void FileNameVersion_MustMatchTheDll()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, "0.1.5", fileName: "AetherFrame-0.1.6.zip"),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("the file name says 0.1.6, the DLL is 0.1.5", TestPackages.Failure(checks, "package file name version"));
    }

    [Theory]
    [InlineData("AetherFrame.zip")]
    [InlineData("aetherframe-0.1.5.zip")]
    [InlineData("AetherFrame-0.1.5-beta.zip")]
    [InlineData("AetherFrame-0.1.zip")]
    public void UnexpectedFileName_Fails(string fileName)
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, fileName: fileName),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("neither latest.zip nor AetherFrame-MAJOR.MINOR.PATCH.zip", TestPackages.Failure(checks, "package file name"));
    }

    [Theory]
    [InlineData(true, false, false, "AetherFrame.dll")]
    [InlineData(false, true, false, "AetherFrame.json")]
    [InlineData(false, false, true, "AetherFrame.deps.json")]
    public void MissingRequiredFile_Fails(bool omitAssembly, bool omitManifest, bool omitDeps, string missing)
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, omitAssembly: omitAssembly, omitManifest: omitManifest, omitDeps: omitDeps),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains($"missing {missing}", TestPackages.Failure(checks, "required files"));
    }

    [Theory]
    [InlineData("AetherFrame.pdb", "debug symbols")]
    [InlineData("Plugin.cs", "a source file")]
    [InlineData("AetherFrame.csproj", "a source file")]
    [InlineData("AetherFrame.Tests.dll", "a test assembly")]
    [InlineData("xunit.core.dll", "a test assembly")]
    [InlineData("AetherFrame.csproj.user", "local configuration")]
    [InlineData("launchSettings.json", "local configuration")]
    [InlineData("dalamudConfig.json", "local configuration")]
    [InlineData("XIVLauncher-backup.json", "a user data path")]
    [InlineData(".gitignore", "a development-only file")]
    [InlineData("README.md", "a development-only file")]
    [InlineData("Newtonsoft.Json.dll", "an unexpected file")]
    public void ExtraFile_FailsWithItsKind(string name, string kind)
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, extraEntries: new[] { (name, new byte[] { 1, 2, 3 }) }),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains($"{name} ({kind})", TestPackages.Failure(checks, "no extra files"));
    }

    [Theory]
    [InlineData("../AetherFrame.dll", "climbs out of the package")]
    [InlineData("a/../b.dll", "climbs out of the package")]
    [InlineData("/AetherFrame.dll", "absolute path")]
    [InlineData("C:/AetherFrame.dll", "drive letter")]
    [InlineData("sub\\AetherFrame.dll", "backslash")]
    [InlineData("images/", "directory entry")]
    [InlineData("images/icon.png", "inside a folder")]
    [InlineData("./AetherFrame.dll", "'.' path segment")]
    [InlineData("a//b", "empty path segment")]
    [InlineData("bad\u0001name", "control characters")]
    public void HostileEntryName_Fails(string name, string reason)
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, extraEntries: new[] { (name, new byte[] { 1 }) }),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains(reason, TestPackages.Failure(checks, "entry names"));
    }

    [Fact]
    public void EntryNamePolicy_RejectsEmptyAndOverlongNames()
    {
        Assert.Contains("empty name", PackageEntryPolicy.Problem(string.Empty));
        Assert.Contains("longer than 255", PackageEntryPolicy.Problem(new string('a', 256) + ".dll"));
        Assert.Null(PackageEntryPolicy.Problem("AetherFrame.dll"));
    }

    [Fact]
    public void DuplicateEntry_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, extraEntries: new[] { ("AetherFrame.dll", new byte[] { 1 }) }),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains("more than one entry named AetherFrame.dll", TestPackages.Failure(checks, "unique entries"));
    }

    [Fact]
    public void EntriesDifferingOnlyByCase_Fail()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, extraEntries: new[] { ("aetherframe.DLL", new byte[] { 1 }) }),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("differ only by case", TestPackages.Failure(checks, "unique entries"));
    }

    [Fact]
    public void WrongCaseRequiredFile_IsReportedAsMissingWithAHint()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, omitManifest: true, extraEntries: new[] { ("aetherframe.json", TestPackages.Manifest()) }),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("AetherFrame.json (the package has 'aetherframe.json', which differs in case)", TestPackages.Failure(checks, "required files"));
    }

    [Fact]
    public void OversizedManifest_Fails()
    {
        using var directory = new TempDirectory();
        var huge = Encoding.UTF8.GetBytes("{\"Name\":\"" + new string('x', (int)PluginPackage.MaxTextBytes) + "\"}");
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: huge),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains("the limit is", TestPackages.Failure(checks, "size of AetherFrame.json"));
    }

    [Fact]
    public void TooManyEntries_Fails()
    {
        using var directory = new TempDirectory();
        var entries = Enumerable.Range(0, PluginPackage.MaxEntries + 1).Select(i => ($"file{i}.txt", new byte[] { 1 }));
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, extraEntries: entries),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains("a plugin package has 3", TestPackages.Failure(checks, "package entries"));
    }

    [Theory]
    [InlineData(Machine.I386, CorFlags.ILOnly, "machine I386, PE32")]
    [InlineData(Machine.I386, CorFlags.ILOnly | CorFlags.Requires32Bit, "requires 32-bit")]
    [InlineData(Machine.Arm64, CorFlags.ILOnly, "machine Arm64")]
    [InlineData(Machine.Amd64, (CorFlags)0, "mixed")]
    public void WrongArchitecture_Fails(Machine machine, CorFlags flags, string message)
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, assembly: TestPackages.Assembly(machine: machine, flags: flags)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains(message, TestPackages.Failure(checks, "assembly architecture"));
    }

    [Fact]
    public void X64DllMarkedAs32BitRequired_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, assembly: TestPackages.Assembly(machine: Machine.Amd64, flags: CorFlags.ILOnly | CorFlags.Requires32Bit)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains("machine Amd64, PE32+, IL only, requires 32-bit", TestPackages.Failure(checks, "assembly architecture"));
    }

    [Fact]
    public void NativeOrGarbageDll_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, assembly: Encoding.ASCII.GetBytes("MZ this is not a PE file at all, just bytes that start like one")),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        TestPackages.Failure(checks, "assembly");
    }

    [Fact]
    public void DllNamedDifferently_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, assembly: TestPackages.Assembly(name: "AetherFrameFork")),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("is 'AetherFrameFork', expected 'AetherFrame'", TestPackages.Failure(checks, "assembly name"));
    }

    [Fact]
    public void DllWithNonZeroRevision_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, assembly: TestPackages.Assembly(assemblyVersion: "0.1.5.7")),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains("0.1.5.7, not MAJOR.MINOR.PATCH.0", TestPackages.Failure(checks, "assembly version"));
    }

    [Fact]
    public void DllVersionDisagreeingWithManifest_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest("0.1.6")),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("is '0.1.6.0', the DLL is 0.1.5.0", TestPackages.Failure(checks, "manifest AssemblyVersion"));
    }

    [Theory]
    [InlineData(null, "'', expected '0.1.5.0'")]
    [InlineData("0.1.4.0", "'0.1.4.0', expected '0.1.5.0'")]
    public void FileVersionMissingOrDifferent_Fails(string? fileVersion, string message)
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, assembly: TestPackages.Assembly(fileVersion: fileVersion)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains(message, TestPackages.Failure(checks, "assembly file version"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.1.5")]
    [InlineData("0.1.5+")]
    [InlineData("0.1.5+ab26da0")]
    [InlineData("0.1.5+local")]
    [InlineData("0.1.6+ab26da043832712af955e92815f59fb517a7db46")]
    public void InformationalVersionWithoutTheFullCommit_Fails(string? informational)
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, assembly: TestPackages.Assembly(informationalVersion: informational)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        Assert.Contains("40-character commit id", TestPackages.Failure(checks, "assembly informational version"));
    }

    [Fact]
    public void DllWithoutADalamudReference_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, assembly: TestPackages.Assembly(dalamudMajor: null)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("does not reference Dalamud", TestPackages.Failure(checks, "Dalamud reference"));
    }

    [Theory]
    [InlineData("AetherFrame.Protocol")]
    [InlineData("AetherFrame.Personas.Storage")]
    public void DllHoldingNetworkingCode_Fails(string ns)
    {
        // A networking preview build (docs/networking/DecisionRegister.md, D9b and P2) is never a
        // release and never a test build, so the package check refuses its DLL.
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, assembly: TestPackages.Assembly(typeNamespace: ns)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains($"holds types in {ns}", TestPackages.Failure(checks, "plugin flavour"));
    }

    [Fact]
    public void FromSharingSince_AReleaseIsTheSharingBuild()
    {
        // The owner's direction of October 1, 2026 ("Testing channel gets sharing"): from
        // sharingSince on, a release carries the sharing code, and a player build is refused.
        var configuration = TestPackages.Configuration(TestPackages.ConfigJsonWithSharing("0.1.5"));
        Assert.Equal(new ProductVersion(0, 1, 5), configuration.SharingSince);

        using var directory = new TempDirectory();
        var (sharing, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, assembly: TestPackages.Assembly(typeNamespace: "AetherFrame.Protocol")),
            Configuration = configuration,
        });
        Assert.Contains(sharing.Checks, c => c.Name == "plugin flavour" && c.Passed && c.Detail == "sharing build, with the sharing code");

        using var other = new TempDirectory();
        var (player, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(other),
            Configuration = configuration,
        });
        Assert.Contains("holds no sharing code", TestPackages.Failure(player, "plugin flavour"));
        Assert.Contains("from 0.1.5", TestPackages.Failure(player, "plugin flavour"));
    }

    [Fact]
    public void BeforeSharingSince_AReleaseIsStillAPlayerBuild()
    {
        // 0.1.7 and earlier were released without the networking code. A publication verifies the
        // releases it describes again, a rollback included, so they must still pass as player builds,
        // and a networking build at such a version is still refused.
        var configuration = TestPackages.Configuration(TestPackages.ConfigJsonWithSharing("0.1.6"));
        Assert.Equal(ReleaseFlavour.Player, configuration.FlavourOf(new ProductVersion(0, 1, 5)));
        Assert.Equal(ReleaseFlavour.Sharing, configuration.FlavourOf(new ProductVersion(0, 1, 6)));
        Assert.Equal(ReleaseFlavour.Sharing, configuration.FlavourOf(new ProductVersion(0, 2, 0)));

        using var directory = new TempDirectory();
        var (player, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory),
            Configuration = configuration,
        });
        Assert.Contains(player.Checks, c => c.Name == "plugin flavour" && c.Passed && c.Detail == "player build, no networking code");

        using var other = new TempDirectory();
        var (networking, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(other, assembly: TestPackages.Assembly(typeNamespace: "AetherFrame.Personas.Storage")),
            Configuration = configuration,
        });
        Assert.Contains("releases before 0.1.6 carry none", TestPackages.Failure(networking, "plugin flavour"));
    }

    [Theory]
    [InlineData("\"sharing\"")]
    [InlineData("\"0.1\"")]
    [InlineData("true")]
    public void ASharingSinceThatIsNotAVersion_IsRefused(string value)
    {
        var json = TestPackages.ConfigJson().Replace("\"dalamudApiLevel\"", "\"sharingSince\": " + value + ",\n  \"dalamudApiLevel\"", StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => TestPackages.Configuration(json));
    }

    [Fact]
    public void WithoutSharingSince_EveryReleaseIsAPlayerBuild()
    {
        var configuration = TestPackages.Configuration();
        Assert.Null(configuration.SharingSince);
        Assert.Equal(ReleaseFlavour.Player, configuration.FlavourOf(new ProductVersion(9, 9, 9)));
        Assert.Null(configuration.WithDownloadUrlTemplate(configuration.DownloadUrlTemplate).SharingSince);
        var withSharing = TestPackages.Configuration(TestPackages.ConfigJsonWithSharing("0.1.8"));
        Assert.Equal(new ProductVersion(0, 1, 8), withSharing.WithDownloadUrlTemplate(withSharing.DownloadUrlTemplate).SharingSince);
    }

    [Fact]
    public void DllCompiledAgainstAnotherApiLevel_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, assembly: TestPackages.Assembly(dalamudMajor: 16)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("API level 16 is not the configured 15", TestPackages.Failure(checks, "Dalamud reference"));
    }

    [Fact]
    public void ManifestApiLevelMismatch_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m["DalamudApiLevel"] = 14)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("is 14, expected 15", TestPackages.Failure(checks, "manifest DalamudApiLevel"));
    }

    [Fact]
    public void ManifestApiLevelMissing_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m.Remove("DalamudApiLevel"))),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("is missing, expected 15", TestPackages.Failure(checks, "manifest DalamudApiLevel"));
    }

    [Fact]
    public void ConfiguredApiLevelOtherThanTheBuild_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory),
            Configuration = TestPackages.Configuration(TestPackages.ConfigJson(apiLevel: 16)),
        });

        Assert.Contains("is 15, expected 16", TestPackages.Failure(checks, "manifest DalamudApiLevel"));
        Assert.Contains("API level 15 is not the configured 16", TestPackages.Failure(checks, "Dalamud reference"));
    }

    [Theory]
    [InlineData("WorkingPluginId", "\"WorkingPluginId\" is assigned by Dalamud at install time")]
    [InlineData("InstalledFromUrl", "belongs to an installed plugin")]
    [InlineData("Disabled", "belongs to an installed plugin")]
    [InlineData("Testing", "belongs to an installed plugin")]
    public void ManifestOfAnInstalledPlugin_Fails(string key, string message)
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m[key] = key == "WorkingPluginId" ? Guid.NewGuid().ToString() : (object)"x")),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        var detail = TestPackages.Failure(checks, "manifest");
        Assert.Contains($"unknown key(s): {key}", detail);
        Assert.Contains(message.Replace("\"", "'", StringComparison.Ordinal), detail);
    }

    [Fact]
    public void ManifestWithAnUnknownKey_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m["Punchlne"] = "typo")),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("unknown key(s): Punchlne", TestPackages.Failure(checks, "manifest"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"Name\": \"A\", \"Name\": \"B\"}")]
    [InlineData("{\"Name\": \"A\",}")]
    [InlineData("{\"DalamudApiLevel\": \"15\", \"Name\": \"A\"}")]
    [InlineData("// comment\n{\"Name\": \"A\"}")]
    public void MalformedManifest_Fails(string json)
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: Encoding.UTF8.GetBytes(json)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Null(report);
        TestPackages.Failure(checks, "manifest");
    }

    [Fact]
    public void ManifestRepoUrlOtherThanConfigured_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m["RepoUrl"] = "https://github.com/someone-else/AetherFrame")),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("is 'https://github.com/someone-else/AetherFrame', expected 'https://github.com/QuietFoxLabs/AetherFrame'", TestPackages.Failure(checks, "manifest RepoUrl"));
    }

    [Fact]
    public void ManifestInternalNameOtherThanConfigured_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m["InternalName"] = "Aetherframe")),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("is 'Aetherframe', expected 'AetherFrame'", TestPackages.Failure(checks, "manifest InternalName"));
    }

    [Fact]
    public void EmptyInstallerFields_Fail()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m =>
            {
                m["Punchline"] = "  ";
                m.Remove("Author");
            })),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("empty: Author, Punchline", TestPackages.Failure(checks, "manifest installer fields"));
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/icon.png", "must use https")]
    [InlineData("https://user:pw@example.com/icon.png", "user information")]
    [InlineData("https://example.com/icon.png?x=1", "query string")]
    [InlineData("C:\\icons\\icon.png", "characters that do not belong")]
    [InlineData("icons/icon.png", "not an absolute URL")]
    public void BadIconUrl_Fails(string url, string message)
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m["IconUrl"] = url)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains(message, TestPackages.Failure(checks, "manifest IconUrl"));
    }

    [Fact]
    public void ApplicableVersionOtherThanAny_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m["ApplicableVersion"] = "2026.09.01.0000.0000")),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("must be 'any'", TestPackages.Failure(checks, "manifest ApplicableVersion"));
    }

    [Fact]
    public void RepeatedOrEmptyTags_Fail()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m["Tags"] = new[] { "plates", "Plates" })),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("repeated tag(s): plates", TestPackages.Failure(checks, "manifest Tags"));
    }

    [Theory]
    [InlineData("C:\\Users\\someone\\source\\AetherFrame")]
    [InlineData("/home/someone/AetherFrame")]
    [InlineData("\\\\server\\share\\AetherFrame")]
    public void LocalPathInTheManifest_Fails(string path)
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m["Description"] = "Built at " + path)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("looks like a local path", TestPackages.Failure(checks, "manifest text"));
    }

    [Fact]
    public void UrlsInTheManifest_AreNotMistakenForLocalPaths()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m => m["Description"] = "See https://example.com/home/page and https://x.invalid/Users/y.")),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains(checks.Checks, c => c.Name == "manifest text" && c.Passed);
    }

    [Theory]
    [InlineData("0.1.4", "AetherFrame", "does not list AetherFrame/0.1.5")]
    [InlineData("0.1.5", "OtherPlugin", "does not list AetherFrame/0.1.5")]
    public void DepsJsonOfAnotherBuild_Fails(string version, string name, string message)
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, deps: TestPackages.Deps(version, name)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains(message, TestPackages.Failure(checks, "deps.json"));
    }

    [Fact]
    public void DepsJsonNotDescribingTheProjectLibrary_Fails()
    {
        using var directory = new TempDirectory();
        var deps = Encoding.UTF8.GetString(TestPackages.Deps()).Replace("\"type\": \"project\"", "\"type\": \"package\"", StringComparison.Ordinal);
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, deps: Encoding.UTF8.GetBytes(deps)),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("does not describe AetherFrame/0.1.5 as the project library", TestPackages.Failure(checks, "deps.json"));
    }

    [Fact]
    public void MalformedDepsJson_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, deps: Encoding.UTF8.GetBytes("{\"runtimeTarget\": ")),
            Configuration = TestPackages.Configuration(),
        });

        Assert.Contains("not valid JSON", TestPackages.Failure(checks, "deps.json"));
    }

    [Fact]
    public void ProjectOnAnotherSdkMajor_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory),
            Configuration = TestPackages.Configuration(),
            ProjectPath = TestPackages.Csproj(directory, sdk: "Dalamud.NET.Sdk/16.0.0"),
        });

        Assert.Contains("Dalamud.NET.Sdk 16 does not match the configured API level 15", TestPackages.Failure(checks, "project SDK API level"));
    }

    [Fact]
    public void ProjectWhoseFieldsMovedSinceTheBuild_Fails()
    {
        using var directory = new TempDirectory();
        var fields = TestPackages.ManifestFields();
        fields["Description"] = "A newer description that was never built.";
        fields["Tags"] = new[] { "aetherframe" };
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory),
            Configuration = TestPackages.Configuration(),
            ProjectPath = TestPackages.Csproj(directory, fields: fields),
        });

        Assert.Contains("differs from the project in Description, Tags", TestPackages.Failure(checks, "project manifest fields"));
    }

    [Fact]
    public void ProjectSettingAFieldTwice_Fails_InsteadOfGuessingWhichApplies()
    {
        using var directory = new TempDirectory();
        var project = TestPackages.Csproj(directory);
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", "  <PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\">\n    <Description>Debug build.</Description>\n  </PropertyGroup>\n</Project>", StringComparison.Ordinal));

        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory),
            Configuration = TestPackages.Configuration(),
            ProjectPath = project,
        });

        Assert.Null(report);
        Assert.Contains("Description is set more than once in AetherFrame.csproj", TestPackages.Failure(checks, "project manifest fields"));
    }

    [Fact]
    public void MissingProjectFile_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory),
            Configuration = TestPackages.Configuration(),
            ProjectPath = directory.File("Missing.csproj"),
        });

        Assert.Contains("not found", TestPackages.Failure(checks, "project file"));
    }

    [Fact]
    public void ChangelogWithoutTheVersionsSection_Fails()
    {
        using var directory = new TempDirectory();
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory),
            Configuration = TestPackages.Configuration(),
            ChangelogPath = TestPackages.Changelog(directory, ("0.1.4", "- Older.")),
        });

        Assert.Contains("has no '## [0.1.5] - YYYY-MM-DD' section", TestPackages.Failure(checks, "changelog section"));
    }

    [Fact]
    public void ChecksumFileDisagreeingWithThePackage_Fails()
    {
        using var directory = new TempDirectory();
        var package = TestPackages.Package(directory);
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = package,
            Configuration = TestPackages.Configuration(),
            ChecksumsPath = TestPackages.Checksums(directory, package, new string('0', 64)),
        });

        Assert.Contains("lists " + new string('0', 64), TestPackages.Failure(checks, "package checksum"));
    }

    [Fact]
    public void ChecksumFileWithoutThePackage_Fails()
    {
        using var directory = new TempDirectory();
        var package = TestPackages.Package(directory);
        var checksums = directory.File("SHA256SUMS.txt");
        File.WriteAllText(checksums, new string('a', 64) + "  Other.zip\n");
        var (checks, _) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = package,
            Configuration = TestPackages.Configuration(),
            ChecksumsPath = checksums,
        });

        Assert.Contains("has no line for AetherFrame-0.1.5.zip", TestPackages.Failure(checks, "listed checksum"));
    }

    [Fact]
    public void AllProblems_AreReportedTogether_NotJustTheFirst()
    {
        using var directory = new TempDirectory();
        var (checks, report) = TestPackages.Validate(new PackageValidationRequest
        {
            PackagePath = TestPackages.Package(directory, manifest: TestPackages.Manifest(mutate: m =>
            {
                m["DalamudApiLevel"] = 14;
                m["RepoUrl"] = "https://github.com/x/y";
                m["Punchline"] = "";
            })),
            Configuration = TestPackages.Configuration(),
            ExpectedTag = "v9.9.9",
        });

        Assert.Null(report);
        Assert.Equal(4, checks.FailureCount);
        Assert.Equal(
            new[] { "manifest DalamudApiLevel", "manifest RepoUrl", "manifest installer fields", "release tag" },
            checks.Checks.Where(c => !c.Passed).Select(c => c.Name));
    }
}
