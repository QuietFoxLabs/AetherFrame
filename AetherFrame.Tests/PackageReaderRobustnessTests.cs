using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// PackageReader.Open never throws and never loses its staging folder: whatever the file, the
/// decoder query or AetherFrame's own disk does, the verdict is data, the folder belongs to the
/// returned package, and a problem with the installation is told apart from a bad package. Plus
/// the archive-level lies no exporter produces (central directory sizes that disagree with the
/// bytes), export failures in the file system, and invisible Plate names.
/// </summary>
public class PackageReaderRobustnessTests
{
    /// <summary>A valid, minimal package: a Blank Plate with one image element and one text element.</summary>
    private sealed class Setup : IDisposable
    {
        private Setup(PackageFixture fixture, PlateLibraryService library, PlatePackageService packages, string validPath, string assetEntry)
        {
            Fixture = fixture;
            Library = library;
            Packages = packages;
            ValidPath = validPath;
            AssetEntry = assetEntry;
        }

        internal PackageFixture Fixture { get; }

        internal PlateLibraryService Library { get; }

        internal PlatePackageService Packages { get; }

        internal string ValidPath { get; }

        internal string AssetEntry { get; }

        internal static async Task<Setup> CreateAsync(Func<string, bool>? decoder = null)
        {
            var fixture = new PackageFixture(decoder);
            var (library, packages) = await fixture.LoadAsync();
            var assetId = fixture.AddImage(TestImages.Png(64, 48));
            var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Small");
            var document = library.OpenDocumentForEditing(created.PlateId);
            document.Elements.Add(new ImageProfileElement { AssetId = assetId, Position = new Vector2(10, 10), Size = new Vector2(64, 48) });
            document.Elements.Add(new TextProfileElement { Text = "hello", Position = new Vector2(100, 10), Size = new Vector2(200, 40), ZIndex = 1 });
            await library.SavePlateDocumentAsync(document);
            var path = fixture.Export(packages, created.PlateId, "valid.aetherframe");
            var assetEntry = PackageFiles.Read(path).Single(e => e.Name.StartsWith(PackagePaths.AssetsFolder, StringComparison.Ordinal)).Name;
            return new Setup(fixture, library, packages, path, assetEntry);
        }

        internal string WriteBytes(byte[] bytes, string fileName)
        {
            var path = Path.Combine(Fixture.ExportDirectory, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>Refused with one of <paramref name="expected"/>, nothing imported, installation unchanged, staging cleaned up.</summary>
        internal async Task<StagedPackage> AssertRefusedAsync(string path, params PackageErrorCode[] expected)
        {
            var before = Fixture.SnapshotInstallation();
            var staged = Packages.Inspect(path);
            try
            {
                Assert.False(staged.CanImport, "a hostile package was importable: " + staged.DescribeForLog());
                Assert.Equal(PackageCompatibility.Invalid, staged.Compatibility);
                Assert.Null(staged.PreparedProfile);
                Assert.Empty(staged.Assets);
                Assert.True(staged.Diagnostics.Errors.Any(e => expected.Contains(e.Code)), $"expected one of [{string.Join(", ", expected)}], got: {staged.DescribeForLog()}");
                Assert.All(staged.Diagnostics.Errors, e => Assert.DoesNotContain(Fixture.Paths.Root, e.Message + e.Detail, StringComparison.OrdinalIgnoreCase));
                Assert.False((await Packages.ImportAsync(staged)).Succeeded);
            }
            finally
            {
                staged.Dispose();
            }

            Assert.Equal(before, Fixture.SnapshotInstallation());
            Assert.True(Fixture.StagingIsEmpty);
            return staged;
        }

        public void Dispose() => Fixture.Dispose();
    }

    // ---------------------------------------------------------------- lying central directory sizes

    /// <summary>
    /// Sets the uncompressed size an archive declares for <paramref name="entryName"/>, in both its
    /// central directory header and its local file header, walking the archive's own structure
    /// from the end record so no signature inside compressed data can be mistaken for a header.
    /// </summary>
    private static byte[] WithDeclaredUncompressedSize(byte[] zip, string entryName, uint size)
    {
        var bytes = (byte[])zip.Clone();
        var end = bytes.Length - 22;
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end)));
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 10));
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        var patched = false;

        for (var i = 0; i < entries; i++)
        {
            Assert.Equal(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 32));
            var name = Encoding.UTF8.GetString(bytes, offset + 46, nameLength);
            if (name == entryName)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24), size);
                var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 42));
                Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(local)));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 22), size);
                patched = true;
            }

            offset += 46 + nameLength + extraLength + commentLength;
        }

        Assert.True(patched, "entry not found");
        return bytes;
    }

    /// <summary>
    /// An entry whose headers (and manifest) claim 16 bytes while its data is a whole file. A
    /// stored entry's stream yields its real bytes whatever the header claims, so only the count
    /// of bytes actually read stops it — the limit a refactor trusting the declared length would
    /// lose. .NET's inflater stops a deflated entry at the claimed size, so that lie can only ever
    /// under-deliver, and the hash of what came out refuses it. Either way nothing past the
    /// declared size reaches staging.
    /// </summary>
    [Theory]
    [InlineData("profile", true)]
    [InlineData("asset", true)]
    [InlineData("profile", false)]
    [InlineData("asset", false)]
    public async Task CentralDirectoryUnderstatingAnEntry_IsStillBoundedByBytesRead(string which, bool stored)
    {
        using var setup = await Setup.CreateAsync();
        const uint declared = 16;
        var entryName = which == "profile" ? PackagePaths.ProfilePath : setup.AssetEntry;

        // The manifest and the archive agree on a tiny size; only the bytes themselves disagree.
        var consistent = PackageFiles.Rewrite(setup.ValidPath, entries =>
        {
            PackageFiles.Entry(entries, entryName).Level = stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            PackageFiles.EditManifest(entries, m =>
            {
                var declaration = which == "profile" ? m["profile"]!.AsObject() : m["assets"]![0]!.AsObject();
                declaration["byteLength"] = declared;
            });
        }, reseal: false);
        var path = setup.WriteBytes(WithDeclaredUncompressedSize(File.ReadAllBytes(consistent), entryName, declared), $"understated-{which}-{stored}.aetherframe");
        using (var archive = ZipFile.OpenRead(path))
        {
            Assert.Equal(declared, (ulong)archive.GetEntry(entryName)!.Length);
        }

        var before = setup.Fixture.SnapshotInstallation();
        var staged = setup.Packages.Inspect(path);
        try
        {
            Assert.False(staged.CanImport, staged.DescribeForLog());
            var error = Assert.Single(staged.Diagnostics.Errors);
            if (stored)
            {
                Assert.Equal(PackageErrorCode.PackageTooLarge, error.Code);
                Assert.Contains($"exceeds {declared} bytes", error.Detail);
            }
            else
            {
                Assert.Equal(PackageErrorCode.HashMismatch, error.Code);
            }

            // Nothing past the declared size was ever written to staging.
            foreach (var file in Directory.GetFiles(staged.StagingDirectory))
            {
                Assert.True(new FileInfo(file).Length <= declared, $"{Path.GetFileName(file)} holds {new FileInfo(file).Length} bytes");
            }
        }
        finally
        {
            staged.Dispose();
        }

        Assert.True(setup.Fixture.StagingIsEmpty);
        Assert.Equal(before, setup.Fixture.SnapshotInstallation());
    }

    [Fact]
    public async Task CentralDirectoryOverstatingAnEntry_IsRefusedByTheDeclaredLimit()
    {
        using var setup = await Setup.CreateAsync();
        var path = setup.WriteBytes(
            WithDeclaredUncompressedSize(File.ReadAllBytes(setup.ValidPath), setup.AssetEntry, (uint)PackagePolicy.MaxEntryBytes + 1), "overstated.aetherframe");

        var staged = await setup.AssertRefusedAsync(path, PackageErrorCode.PackageTooLarge);

        Assert.Contains(staged.Diagnostics.Errors, e => e.Detail?.Contains($"{PackagePolicy.MaxEntryBytes + 1} bytes", StringComparison.Ordinal) == true);
        Assert.Null(staged.Manifest);
    }

    // ---------------------------------------------------------------- commit and export failures

    [Fact]
    public async Task SecondImageCommitFailure_RollsBackTheFirst()
    {
        var armed = false;
        var commits = 0;
        using var fixture = new PackageFixture(decoder: _ => !armed || ++commits != 2);
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var path = fixture.Export(packages, plateId);
        var before = fixture.SnapshotInstallation();
        var plates = library.GetOrderedPlates().Count;

        var staged = packages.Inspect(path);
        Assert.Equal(3, staged.Assets.Count);
        armed = true;
        var result = await packages.ImportAsync(staged);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageErrorCode.CommitFailed, result.Error!.Code);
        Assert.Equal(2, commits);
        Assert.Equal(plates, library.GetOrderedPlates().Count);
        Assert.Equal(before, fixture.SnapshotInstallation());
        Assert.Empty(Directory.Exists(fixture.Paths.AssetStagingDirectory) ? Directory.GetFiles(fixture.Paths.AssetStagingDirectory) : []);
        staged.Dispose();
        Assert.True(fixture.StagingIsEmpty);
    }

    [Fact]
    public async Task Export_UnwritableDestination_FailsWithoutTempFiles()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var blocker = Path.Combine(fixture.ExportDirectory, "not-a-folder");
        File.WriteAllText(blocker, "a file, not a folder");
        var destination = Path.Combine(blocker, "plate.aetherframe");

        var result = packages.Export(plateId, destination, overwrite: false);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageErrorCode.ExportFailed, result.Errors[0].Code);
        Assert.StartsWith("The file couldn't be saved there", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.ExportDirectory, result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
        Assert.Equal("a file, not a folder", File.ReadAllText(blocker));
        Assert.Equal([blocker], Directory.GetFileSystemEntries(fixture.ExportDirectory));
        Assert.True(fixture.StagingIsEmpty);
    }

    [Fact]
    public async Task Export_ImageChangedMidExport_FailsWithoutWritingAndSaysSo()
    {
        var armed = false;
        var queries = 0;
        string? victim = null;
        using var fixture = new PackageFixture(decoder: _ =>
        {
            // Export queries each of the three images while planning (and hashing) them, then the
            // preview: by then every image's hash is fixed, so a change now is a change mid-export.
            if (armed && ++queries == 4)
            {
                // Same length, different content: the last byte of IEND's CRC.
                var bytes = File.ReadAllBytes(victim!);
                bytes[^1] ^= 0xFF;
                File.WriteAllBytes(victim!, bytes);
            }

            return true;
        });
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, portrait, _, _) = await fixture.CreateRichPlateAsync(library);
        victim = fixture.Assets.ResolveAssetPath(portrait)!;
        var thumbnail = TestImages.Write(fixture.SourceDirectory, "thumb.png", TestImages.Png(320, 180));
        var destination = Path.Combine(fixture.ExportDirectory, "racing.aetherframe");

        armed = true;
        var result = packages.Export(plateId, destination, overwrite: false, previewPngPath: thumbnail);

        Assert.False(result.Succeeded);
        Assert.Equal(4, queries);
        Assert.Equal(PackageErrorCode.ExportFailed, result.Errors[0].Code);
        Assert.Equal("The Plate's images changed while exporting. Try again.", result.FailureMessage);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ExportDirectory));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("changed", StringComparison.Ordinal));
        Assert.True(fixture.StagingIsEmpty);

        // Once the image is stable again, the export goes through.
        armed = false;
        Assert.True(packages.Export(plateId, destination, overwrite: false, previewPngPath: thumbnail).Succeeded);
    }}
