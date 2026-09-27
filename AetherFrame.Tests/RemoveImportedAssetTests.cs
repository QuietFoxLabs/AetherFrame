using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AetherFrame.Domain.Assets;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A failed package import rolls back only the images it created. "Created by this import" is an
/// enforced invariant, not a caller's promise: the service refuses to remove any id it didn't
/// mint itself in this session, whatever path it is handed.
/// </summary>
public class RemoveImportedAssetTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory directory = new();

        internal Fixture()
        {
            Paths = new PlateStoragePaths(directory.Path);
            Metadata = new AssetMetadataStore(Paths.AssetMetadataDirectory);
            Storage = NewStorage();
        }

        internal PlateStoragePaths Paths { get; }

        internal AssetMetadataStore Metadata { get; }

        internal AssetStorageService Storage { get; }

        internal string SourceDirectory => Path.Combine(directory.Path, "src");

        /// <summary>Another service over the same folders, as a later plugin load would create.</summary>
        internal AssetStorageService NewStorage() => new(Paths.AssetsDirectory, Paths.AssetStagingDirectory, Metadata);

        /// <summary>An image staged the way a validated package stages one, plus its hash.</summary>
        internal (string Path, string Sha256) Staged(byte[] bytes, string name = "staged.png") =>
            (TestImages.Write(Path.Combine(directory.Path, "package-staging"), name, bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)));

        public void Dispose() => directory.Dispose();
    }

    [Fact]
    public void RemoveImportedAsset_RefusesAnAssetThisServiceDidNotImport()
    {
        using var fixture = new Fixture();
        var existing = Guid.NewGuid();
        var path = TestImages.Write(fixture.Paths.AssetsDirectory, existing.ToString("N") + ".png", TestImages.Png(4, 4));
        fixture.Metadata.Save(new AssetMetadata { AssetId = existing, OriginalFileName = "old.png" });
        var sidecar = File.ReadAllBytes(fixture.Metadata.GetPath(existing));

        var error = Assert.Throws<InvalidOperationException>(() => fixture.Storage.RemoveImportedAsset(existing, path));

        Assert.Contains("isn't this import's managed image", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
        Assert.Equal(sidecar, File.ReadAllBytes(fixture.Metadata.GetPath(existing)));
        Assert.Equal(path, fixture.Storage.ResolveAssetPath(existing));
    }

    [Fact]
    public void RemoveImportedAsset_RefusesAnAssetImportedInAnEarlierSession()
    {
        using var fixture = new Fixture();
        var earlier = fixture.NewStorage();
        var imported = earlier.ImportImage(TestImages.Write(fixture.SourceDirectory, "then.png", TestImages.Png(4, 4)));
        var path = fixture.Storage.ResolveAssetPath(imported)!;

        Assert.Throws<InvalidOperationException>(() => fixture.Storage.RemoveImportedAsset(imported, path));

        Assert.True(File.Exists(path));
        Assert.NotNull(fixture.Metadata.TryLoad(imported));
        Assert.DoesNotContain(imported, fixture.Storage.ImportedThisSession);
    }

    [Fact]
    public void RemoveImportedAsset_RemovesFileAndSidecar_ForAnImportedPackageImage()
    {
        using var fixture = new Fixture();
        var (staged, sha256) = fixture.Staged(TestImages.Png(8, 8));
        var assetId = Guid.NewGuid();
        var created = fixture.Storage.AddValidatedPackageImage(staged, assetId, sha256, "picture.png");
        Assert.NotNull(fixture.Metadata.TryLoad(assetId));

        fixture.Storage.RemoveImportedAsset(assetId, created);

        Assert.False(File.Exists(created));
        Assert.False(File.Exists(fixture.Metadata.GetPath(assetId)));
        Assert.Null(fixture.Storage.ResolveAssetPath(assetId));
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public void RemoveImportedAsset_RemovesAnImageImportedDirectly()
    {
        using var fixture = new Fixture();
        var imported = fixture.Storage.ImportImage(TestImages.Write(fixture.SourceDirectory, "now.png", TestImages.Png(4, 4)));
        var path = fixture.Storage.ResolveAssetPath(imported)!;

        fixture.Storage.RemoveImportedAsset(imported, path);

        Assert.Null(fixture.Storage.ResolveAssetPath(imported));
        Assert.Null(fixture.Metadata.TryLoad(imported));
    }

    [Fact]
    public void RemoveImportedAsset_StillChecksThePath_ForAnIdItMinted()
    {
        using var fixture = new Fixture();
        var (staged, sha256) = fixture.Staged(TestImages.Png(8, 8));
        var assetId = Guid.NewGuid();
        var created = fixture.Storage.AddValidatedPackageImage(staged, assetId, sha256, "picture.png");
        var other = Guid.NewGuid();
        var otherPath = TestImages.Write(fixture.Paths.AssetsDirectory, other.ToString("N") + ".png", TestImages.Png(4, 4));
        var elsewhere = TestImages.Write(Path.Combine(fixture.Paths.Root, "elsewhere"), assetId.ToString("N") + ".png", TestImages.Png(4, 4));

        Assert.Throws<InvalidOperationException>(() => fixture.Storage.RemoveImportedAsset(assetId, otherPath));
        Assert.Throws<InvalidOperationException>(() => fixture.Storage.RemoveImportedAsset(assetId, elsewhere));
        Assert.Throws<InvalidOperationException>(() => fixture.Storage.RemoveImportedAsset(assetId, staged));

        Assert.True(File.Exists(created));
        Assert.True(File.Exists(otherPath));
        Assert.True(File.Exists(elsewhere));
        Assert.True(File.Exists(staged));
    }

    /// <summary>
    /// The contract every other invariant here rests on: there is no import-time de-duplication.
    /// Identical bytes imported twice are two assets with two files; a change to that must be
    /// deliberate (see the invariants on <see cref="AssetStorageService.AddValidatedPackageImage"/>).
    /// </summary>
    [Fact]
    public void ImportingIdenticalBytesTwice_YieldsTwoAssets()
    {
        using var fixture = new Fixture();
        var bytes = TestImages.Png(16, 16);
        var first = fixture.Storage.ImportImage(TestImages.Write(fixture.SourceDirectory, "one.png", bytes));
        var second = fixture.Storage.ImportImage(TestImages.Write(fixture.SourceDirectory, "two.png", bytes));
        var (staged, sha256) = fixture.Staged(bytes);
        var third = Guid.NewGuid();
        fixture.Storage.AddValidatedPackageImage(staged, third, sha256, "three.png");

        Assert.NotEqual(first, second);
        Assert.Equal(3, fixture.Storage.ListAssets().Count);
        Assert.Equal(3, fixture.Storage.ListAssets().Select(a => a.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.All(new[] { first, second, third }, id => Assert.Equal(fixture.Metadata.TryLoad(id)!.Sha256, sha256));
    }
}
