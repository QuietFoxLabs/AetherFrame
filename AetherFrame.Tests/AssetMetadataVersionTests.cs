using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AetherFrame.Persistence;
using AetherFrame.Services.Assets;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The metadata store follows the plugin-wide rule for files from a newer version: read what can
/// be read, never write over them. A damaged sidecar, by contrast, is derived data and is simply
/// rebuilt — and never throws on the way.
/// </summary>
public class AssetMetadataVersionTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory directory = new();

        internal Fixture()
        {
            Paths = new PlateStoragePaths(directory.Path);
            Metadata = new AssetMetadataStore(Paths.AssetMetadataDirectory, Log);
        }

        internal PlateStoragePaths Paths { get; }

        internal AssetMetadataStore Metadata { get; }

        internal TestLog Log { get; } = new();

        internal (Guid AssetId, string Path, byte[] Bytes) AddLegacyAsset()
        {
            var id = Guid.NewGuid();
            var bytes = TestImages.Png(6, 4);
            return (id, TestImages.Write(Paths.AssetsDirectory, id.ToString("N") + ".png", bytes), bytes);
        }

        internal string WriteSidecar(Guid assetId, string json)
        {
            Directory.CreateDirectory(Paths.AssetMetadataDirectory);
            File.WriteAllText(Metadata.GetPath(assetId), json);
            return Metadata.GetPath(assetId);
        }

        public void Dispose() => directory.Dispose();
    }

    [Fact]
    public void GetOrCreate_NeverWritesOverANewerVersionSidecar_ButStillComputesMetadata()
    {
        using var fixture = new Fixture();
        var (assetId, assetPath, bytes) = fixture.AddLegacyAsset();
        var json = $"{{\"Version\": 99, \"AssetId\": \"{assetId}\", \"Future\": 1, \"OriginalFileName\": \"from-the-future.png\"}}";
        var sidecar = fixture.WriteSidecar(assetId, json);
        var before = File.ReadAllBytes(sidecar);

        Assert.Null(fixture.Metadata.TryLoad(assetId));
        var computed = fixture.Metadata.GetOrCreate(assetId, assetPath)!;

        Assert.Equal(assetId, computed.AssetId);
        Assert.Equal("image/png", computed.MediaType);
        Assert.Equal(6, computed.PixelWidth);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), computed.Sha256);
        Assert.Equal(Path.GetFileName(assetPath), computed.OriginalFileName);
        Assert.Equal(before, File.ReadAllBytes(sidecar));
        Assert.Null(fixture.Metadata.TryLoad(assetId));
        Assert.Equal(bytes, File.ReadAllBytes(assetPath));
    }

    [Fact]
    public void GetOrCreate_ReportsANewerVersionSidecarOnce()
    {
        using var fixture = new Fixture();
        var (assetId, assetPath, _) = fixture.AddLegacyAsset();
        fixture.WriteSidecar(assetId, $"{{\"Version\": 2, \"AssetId\": \"{assetId}\"}}");

        fixture.Metadata.GetOrCreate(assetId, assetPath);
        fixture.Metadata.GetOrCreate(assetId, assetPath);
        fixture.Metadata.GetOrCreate(assetId, assetPath);

        var reports = fixture.Log.Messages.Where(m => m.Contains("newer version", StringComparison.Ordinal)).ToList();
        Assert.Single(reports);
        Assert.StartsWith("W ", reports[0], StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Paths.Root, reports[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetOrCreate_NewerVersionSidecarForAnotherAsset_IsStillLeftAlone()
    {
        using var fixture = new Fixture();
        var (assetId, assetPath, _) = fixture.AddLegacyAsset();
        var sidecar = fixture.WriteSidecar(assetId, $"{{\"Version\": 5, \"AssetId\": \"{Guid.NewGuid()}\"}}");
        var before = File.ReadAllBytes(sidecar);

        Assert.NotNull(fixture.Metadata.GetOrCreate(assetId, assetPath));

        Assert.Equal(before, File.ReadAllBytes(sidecar));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"Version\": \"one\"}")]
    [InlineData("{\"Version\": 1, \"AssetId\": \"not a guid\"}")]
    [InlineData("{\"Version\": 1, \"PixelWidth\": \"wide\"}")]
    public void TryLoad_DamagedSidecar_ReturnsNullWithoutThrowing_AndGetOrCreateReplacesIt(string json)
    {
        using var fixture = new Fixture();
        var (assetId, assetPath, bytes) = fixture.AddLegacyAsset();
        var sidecar = fixture.WriteSidecar(assetId, json);

        Assert.Null(fixture.Metadata.TryLoad(assetId));

        var created = fixture.Metadata.GetOrCreate(assetId, assetPath)!;
        var reloaded = fixture.Metadata.TryLoad(assetId)!;

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), created.Sha256);
        Assert.Equal(created.Sha256, reloaded.Sha256);
        Assert.Equal(AetherFrame.Domain.Assets.AssetMetadata.CurrentVersion, reloaded.Version);
        Assert.NotEqual(json, File.ReadAllText(sidecar));
    }

    [Fact]
    public void TryLoad_SidecarWithDuplicateKeys_ReturnsNullWithoutThrowing()
    {
        using var fixture = new Fixture();
        var assetId = Guid.NewGuid();
        fixture.WriteSidecar(assetId, $"{{\"Version\": 1, \"AssetId\": \"{assetId}\", \"AssetId\": \"{assetId}\"}}");

        Assert.Null(fixture.Metadata.TryLoad(assetId));
        Assert.All(fixture.Log.Messages, m => Assert.DoesNotContain(fixture.Paths.Root, m, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryLoad_UnreadableSidecar_ReturnsNull_AndLogsNoPath()
    {
        using var fixture = new Fixture();
        var assetId = Guid.NewGuid();
        Directory.CreateDirectory(fixture.Paths.Root);
        File.WriteAllText(fixture.Paths.AssetMetadataDirectory, "a file where the folder should be");

        Assert.Null(fixture.Metadata.TryLoad(assetId));
        Assert.All(fixture.Log.Messages, m => Assert.DoesNotContain(fixture.Paths.Root, m, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetOrCreate_CurrentSidecar_IsReturnedAsIs()
    {
        using var fixture = new Fixture();
        var (assetId, assetPath, _) = fixture.AddLegacyAsset();
        fixture.Metadata.Save(new AetherFrame.Domain.Assets.AssetMetadata { AssetId = assetId, OriginalFileName = "kept.png", Sha256 = "not recomputed" });
        var before = File.ReadAllBytes(fixture.Metadata.GetPath(assetId));

        var loaded = fixture.Metadata.GetOrCreate(assetId, assetPath)!;

        Assert.Equal("kept.png", loaded.OriginalFileName);
        Assert.Equal("not recomputed", loaded.Sha256);
        Assert.Equal(before, File.ReadAllBytes(fixture.Metadata.GetPath(assetId)));
    }
}
