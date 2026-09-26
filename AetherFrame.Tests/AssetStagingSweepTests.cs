using System;
using System.IO;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// An import the process didn't survive leaves "{guid:N}.importing" in asset staging. The sweep
/// at plugin load removes exactly those files and nothing else: not other files, not folders, and
/// never anything in managed storage.
/// </summary>
public class AssetStagingSweepTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory directory = new();

        internal Fixture()
        {
            Paths = new PlateStoragePaths(directory.Path);
            Metadata = new AssetMetadataStore(Paths.AssetMetadataDirectory);
            Storage = new AssetStorageService(Paths.AssetsDirectory, Paths.AssetStagingDirectory, Metadata, log: Log);
        }

        internal PlateStoragePaths Paths { get; }

        internal AssetMetadataStore Metadata { get; }

        internal AssetStorageService Storage { get; }

        internal TestLog Log { get; } = new();

        internal string Root => directory.Path;

        internal string Plant(string fileName, byte[]? bytes = null) =>
            TestImages.Write(Paths.AssetStagingDirectory, fileName, bytes ?? "partial copy"u8.ToArray());

        public void Dispose() => directory.Dispose();
    }

    [Fact]
    public void SweepStaging_RemovesOnlyStaleImportingFiles_AndKeepsEverythingElse()
    {
        using var fixture = new Fixture();
        var imported = fixture.Storage.ImportImage(TestImages.Write(Path.Combine(fixture.Root, "src"), "keep.png", TestImages.Png(4, 4)));
        var assetPath = fixture.Storage.ResolveAssetPath(imported)!;
        var assetBytes = File.ReadAllBytes(assetPath);
        var sidecarBytes = File.ReadAllBytes(fixture.Metadata.GetPath(imported));

        var stale = fixture.Plant(Guid.NewGuid().ToString("N") + ".importing", new byte[1024]);
        var staleTwo = fixture.Plant(Guid.NewGuid().ToString("N") + ".importing");
        var uppercase = fixture.Plant(Guid.NewGuid().ToString("N").ToUpperInvariant() + ".importing");
        var braced = fixture.Plant(Guid.NewGuid().ToString("D") + ".importing");
        var notes = fixture.Plant("notes.txt");
        var image = fixture.Plant(Guid.NewGuid().ToString("N") + ".png", TestImages.Png(2, 2));
        var doubled = fixture.Plant(Guid.NewGuid().ToString("N") + ".importing.importing");
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Paths.AssetStagingDirectory, Guid.NewGuid().ToString("N") + ".importing")).FullName;
        var nested = TestImages.Write(folder, Guid.NewGuid().ToString("N") + ".importing", [1, 2, 3]);

        fixture.Storage.SweepStaging();

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleTwo));
        Assert.True(File.Exists(uppercase));
        Assert.True(File.Exists(braced));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(image));
        Assert.True(File.Exists(doubled));
        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(nested));

        Assert.Equal(assetPath, fixture.Storage.ResolveAssetPath(imported));
        Assert.Equal(assetBytes, File.ReadAllBytes(assetPath));
        Assert.Equal(sidecarBytes, File.ReadAllBytes(fixture.Metadata.GetPath(imported)));
        Assert.Single(fixture.Storage.ListAssets());
        Assert.Empty(fixture.Log.Messages);
    }

    [Fact]
    public void SweepStaging_MissingDirectory_IsANoOp()
    {
        using var fixture = new Fixture();

        fixture.Storage.SweepStaging();

        Assert.False(Directory.Exists(fixture.Paths.AssetStagingDirectory));
        Assert.Empty(fixture.Log.Messages);
    }

    [Fact]
    public void SweepStaging_NeverTouchesManagedStorage_EvenForImportingNamesThere()
    {
        using var fixture = new Fixture();
        var strayInAssets = TestImages.Write(fixture.Paths.AssetsDirectory, Guid.NewGuid().ToString("N") + ".importing", [1]);
        fixture.Plant(Guid.NewGuid().ToString("N") + ".importing");

        fixture.Storage.SweepStaging();

        Assert.True(File.Exists(strayInAssets));
        Assert.Empty(Directory.GetFiles(fixture.Paths.AssetStagingDirectory));
    }

    [Fact]
    public void SweepStaging_WhenTheDirectoryIsAFile_LogsWithoutAPath_AndDoesNotThrow()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(fixture.Paths.AssetStagingDirectory, "in the way");

        fixture.Storage.SweepStaging();

        Assert.True(File.Exists(fixture.Paths.AssetStagingDirectory));
        Assert.All(fixture.Log.Messages, m => Assert.DoesNotContain(fixture.Root, m, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.importing", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef.importing", false)]
    [InlineData("0123456789abcdef0123456789abcde.importing", false)]
    [InlineData("0123456789abcdef0123456789abcdef.png", false)]
    [InlineData("0123456789abcdef0123456789abcdef.importing.bak", false)]
    [InlineData(".importing", false)]
    [InlineData("", false)]
    public void IsStagingFileName_AcceptsOnlyThirtyTwoLowercaseHexDigitsAndTheSuffix(string fileName, bool expected)
    {
        Assert.Equal(expected, AssetStorageService.IsStagingFileName(fileName));
    }

    [Fact]
    public void ACompletedImport_LeavesNothingForTheSweep()
    {
        using var fixture = new Fixture();
        fixture.Storage.ImportImage(TestImages.Write(Path.Combine(fixture.Root, "src"), "a.png", TestImages.Png(4, 4)));

        Assert.Empty(Directory.GetFiles(fixture.Paths.AssetStagingDirectory));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.Contains("temporary", StringComparison.Ordinal));
    }
}
