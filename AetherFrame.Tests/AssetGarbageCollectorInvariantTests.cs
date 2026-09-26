using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AetherFrame.Domain.Assets;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The asset trash must never sacrifice a live image or its metadata, whatever fails half way:
/// a record only ever describes a file that really moved, a stale record removes nothing but
/// itself, a failed step leaves the record for the next pass, and a restored asset gets a fresh
/// protection window instead of being trashed again by the next pass.
/// </summary>
public class AssetGarbageCollectorInvariantTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory directory = new();

        internal Fixture()
        {
            Paths = new PlateStoragePaths(directory.Path);
            Metadata = new AssetMetadataStore(Paths.AssetMetadataDirectory, Log);
            Storage = new AssetStorageService(Paths.AssetsDirectory, Paths.AssetStagingDirectory, Metadata, log: Log);
            Collector = new AssetGarbageCollector(Storage, Paths.AssetTrashDirectory, Log, () => Clock.Now);
        }

        internal FakeClock Clock { get; } = new() { Now = DateTime.UtcNow };

        internal PlateStoragePaths Paths { get; }

        internal AssetMetadataStore Metadata { get; }

        internal AssetStorageService Storage { get; }

        internal AssetGarbageCollector Collector { get; }

        internal TestLog Log { get; } = new();

        internal string Root => directory.Path;

        /// <summary>An asset old enough to be trashed, with a metadata sidecar.</summary>
        internal Guid AddOldAssetWithMetadata(string extension = ".png")
        {
            var id = Guid.NewGuid();
            var path = TestImages.Write(Paths.AssetsDirectory, id.ToString("N") + extension, TestImages.Png(2, 2));
            Assert.NotNull(Metadata.GetOrCreate(id, path));
            Clock.Now = Clock.Now.Add(AssetGarbageCollector.MinimumUnreferencedAge).AddDays(1);
            return id;
        }

        internal string RecordPath(Guid id) => Path.Combine(Paths.AssetTrashDirectory, id.ToString("N") + ".trash.json");

        internal string WriteRecord(Guid id, string fileName, DateTime trashedAtUtc)
        {
            Directory.CreateDirectory(Paths.AssetTrashDirectory);
            File.WriteAllText(RecordPath(id), JsonSerializer.Serialize(new AssetTrashRecord(1, id, fileName, trashedAtUtc), JsonOptions.Default));
            return RecordPath(id);
        }

        internal string[] TrashedImages() =>
            Directory.Exists(Paths.AssetTrashDirectory) ? Directory.GetFiles(Paths.AssetTrashDirectory).Where(f => !f.EndsWith(".trash.json", StringComparison.Ordinal)).ToArray() : [];

        internal string[] Records() =>
            Directory.Exists(Paths.AssetTrashDirectory) ? Directory.GetFiles(Paths.AssetTrashDirectory, "*.trash.json") : [];

        internal void AssertNoPathLogged() => Assert.All(Log.Messages, m => Assert.DoesNotContain(Root, m, StringComparison.OrdinalIgnoreCase));

        internal void PassGracePeriod() => Clock.Now = Clock.Now.Add(AssetGarbageCollector.TrashGracePeriod).AddDays(1);

        public void Dispose() => directory.Dispose();
    }

    private static AssetReferenceScan Complete(params Guid[] referenced) => new(true, referenced.ToHashSet(), []);

    [Fact]
    public void MoveToTrash_WhenTheMoveFails_LeavesNoRecord_AndTheAssetInStorage()
    {
        using var fixture = new Fixture();
        var id = fixture.AddOldAssetWithMetadata();
        var assetPath = fixture.Storage.ResolveAssetPath(id)!;
        var sidecar = File.ReadAllBytes(fixture.Metadata.GetPath(id));

        // Something already sits where the file would go: the move can't overwrite it.
        var inTheWay = TestImages.Write(fixture.Paths.AssetTrashDirectory, Path.GetFileName(assetPath), "in the way"u8.ToArray());

        var moved = fixture.Collector.MoveToTrash(fixture.Collector.Plan(Complete()));

        Assert.Equal(0, moved);
        Assert.Equal(assetPath, fixture.Storage.ResolveAssetPath(id));
        Assert.Empty(fixture.Records());
        Assert.Equal("in the way"u8.ToArray(), File.ReadAllBytes(inTheWay));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("could not move", StringComparison.Ordinal));
        fixture.AssertNoPathLogged();

        // With no record, no purge can ever touch the asset or its metadata.
        fixture.PassGracePeriod();
        Assert.Empty(fixture.Collector.PurgeExpired(purgeEnabled: true));
        Assert.Equal(sidecar, File.ReadAllBytes(fixture.Metadata.GetPath(id)));
        Assert.False(fixture.Collector.Restore(id));
    }

    [Fact]
    public void PurgeExpired_StaleRecordForALiveAsset_RemovesOnlyTheRecord()
    {
        using var fixture = new Fixture();
        var id = fixture.AddOldAssetWithMetadata();
        var assetPath = fixture.Storage.ResolveAssetPath(id)!;
        var assetBytes = File.ReadAllBytes(assetPath);
        var sidecar = File.ReadAllBytes(fixture.Metadata.GetPath(id));
        var record = fixture.WriteRecord(id, Path.GetFileName(assetPath), fixture.Clock.Now);
        fixture.PassGracePeriod();

        Assert.Equal([id], fixture.Collector.PurgeExpired(purgeEnabled: true));

        Assert.Equal(assetPath, fixture.Storage.ResolveAssetPath(id));
        Assert.Equal(assetBytes, File.ReadAllBytes(assetPath));
        Assert.Equal(sidecar, File.ReadAllBytes(fixture.Metadata.GetPath(id)));
        Assert.False(File.Exists(record));
        Assert.Empty(fixture.Log.Messages);
    }

    [Fact]
    public void PurgeExpired_WhenTheSidecarDeleteFails_KeepsTheRecordForRetry()
    {
        using var fixture = new Fixture();
        var id = fixture.AddOldAssetWithMetadata();
        Assert.Equal(1, fixture.Collector.MoveToTrash(fixture.Collector.Plan(Complete())));
        var trashedImage = Assert.Single(fixture.TrashedImages());

        // The sidecar can't be deleted: a folder with content sits under its name.
        File.Delete(fixture.Metadata.GetPath(id));
        TestImages.Write(fixture.Metadata.GetPath(id), "blocker", [1]);
        fixture.PassGracePeriod();

        Assert.Equal([id], fixture.Collector.PurgeExpired(purgeEnabled: true));

        Assert.False(File.Exists(trashedImage));
        Assert.True(File.Exists(fixture.RecordPath(id)), "the record must stay so the purge is retried");
        Assert.True(Directory.Exists(fixture.Metadata.GetPath(id)));
        Assert.Contains(fixture.Log.Messages, m => m.Contains("could not purge", StringComparison.Ordinal));
        fixture.AssertNoPathLogged();

        // Once the sidecar can be dealt with, the retry finishes the job.
        Directory.Delete(fixture.Metadata.GetPath(id), recursive: true);
        Assert.Equal([id], fixture.Collector.PurgeExpired(purgeEnabled: true));
        Assert.Empty(fixture.Records());
        Assert.Empty(fixture.TrashedImages());
    }

    [Fact]
    public void PurgeExpired_RemovesTheSidecar_OfAnAssetThatWasReallyTrashed()
    {
        using var fixture = new Fixture();
        var id = fixture.AddOldAssetWithMetadata();
        Assert.Equal(1, fixture.Collector.MoveToTrash(fixture.Collector.Plan(Complete())));
        Assert.True(File.Exists(fixture.Metadata.GetPath(id)));
        fixture.PassGracePeriod();

        Assert.Equal([id], fixture.Collector.PurgeExpired(purgeEnabled: true));

        Assert.False(File.Exists(fixture.Metadata.GetPath(id)));
        Assert.Empty(fixture.Records());
        Assert.Empty(fixture.TrashedImages());
        Assert.Null(fixture.Storage.ResolveAssetPath(id));
    }

    [Fact]
    public void Restore_ProtectsTheAssetFromTheNextCleanupPass()
    {
        using var fixture = new Fixture();
        var id = fixture.AddOldAssetWithMetadata();
        Assert.Equal(1, fixture.Collector.MoveToTrash(fixture.Collector.Plan(Complete())));
        var restoredAt = fixture.Clock.Now;

        Assert.True(fixture.Collector.Restore(id));

        var restoredPath = fixture.Storage.ResolveAssetPath(id)!;
        Assert.Empty(fixture.Records());
        Assert.InRange(File.GetLastWriteTimeUtc(restoredPath), restoredAt.AddSeconds(-2), restoredAt.AddSeconds(2));

        var plan = fixture.Collector.Plan(Complete());
        Assert.Equal(AssetLifecycleState.Protected, Assert.Single(plan.Assets).State);
        Assert.Equal(0, fixture.Collector.MoveToTrash(plan));
        Assert.Equal(restoredPath, fixture.Storage.ResolveAssetPath(id));

        // The window is a fresh one, not permanent protection.
        fixture.Clock.Now = fixture.Clock.Now.Add(AssetGarbageCollector.MinimumUnreferencedAge).AddDays(1);
        Assert.Equal(AssetLifecycleState.Unreferenced, Assert.Single(fixture.Collector.Plan(Complete()).Assets).State);
        Assert.Empty(fixture.Log.Messages);
    }

    [Fact]
    public void MoveToTrash_TwoFilesForOneId_NeverOrphansATrashedFile()
    {
        using var fixture = new Fixture();
        var id = fixture.AddOldAssetWithMetadata(".png");
        TestImages.Write(fixture.Paths.AssetsDirectory, id.ToString("N") + ".jpg", TestImages.Jpeg(2, 2));
        fixture.Clock.Now = fixture.Clock.Now.Add(AssetGarbageCollector.MinimumUnreferencedAge).AddDays(1);

        var plan = fixture.Collector.Plan(Complete());
        Assert.Equal(2, plan.Unreferenced.Count());

        Assert.Equal(1, fixture.Collector.MoveToTrash(plan));

        Assert.Single(fixture.TrashedImages());
        Assert.Single(fixture.Records());
        Assert.NotNull(fixture.Storage.ResolveAssetPath(id));
        Assert.Contains(fixture.Log.Messages, m => m.Contains("left unused asset", StringComparison.Ordinal));
        fixture.AssertNoPathLogged();

        // Every file is accounted for: the one in the trash restores beside the one left in place.
        Assert.True(fixture.Collector.Restore(id));
        Assert.Empty(fixture.TrashedImages());
        Assert.Empty(fixture.Records());
        Assert.Equal(2, fixture.Storage.ListAssets().Count);
    }

    [Fact]
    public void MoveToTrash_StaleRecordNamingNothingInTheTrash_IsReplaced()
    {
        using var fixture = new Fixture();
        var id = fixture.AddOldAssetWithMetadata();
        var assetPath = fixture.Storage.ResolveAssetPath(id)!;
        var staleTime = fixture.Clock.Now.AddDays(-100);
        var record = fixture.WriteRecord(id, Path.GetFileName(assetPath), staleTime);

        Assert.Equal(1, fixture.Collector.MoveToTrash(fixture.Collector.Plan(Complete())));

        Assert.Null(fixture.Storage.ResolveAssetPath(id));
        Assert.Single(fixture.TrashedImages());
        var rewritten = JsonSerializer.Deserialize<AssetTrashRecord>(File.ReadAllText(record), JsonOptions.Default)!;
        Assert.Equal(fixture.Clock.Now, rewritten.TrashedAtUtc);
        Assert.Empty(fixture.Log.Messages);

        // The fresh record means the grace period starts now, not at the stale time.
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        Assert.Empty(fixture.Collector.PurgeExpired(purgeEnabled: false));
        Assert.True(fixture.Collector.Restore(id));
    }

    [Fact]
    public void PurgeExpired_StaleRecordNamingAFileOutsideTheTrash_IsIgnored()
    {
        using var fixture = new Fixture();
        var id = fixture.AddOldAssetWithMetadata();
        var record = fixture.WriteRecord(id, Path.Combine("..", "assets", id.ToString("N") + ".png"), fixture.Clock.Now);
        fixture.PassGracePeriod();

        Assert.Empty(fixture.Collector.PurgeExpired(purgeEnabled: true));

        Assert.NotNull(fixture.Storage.ResolveAssetPath(id));
        Assert.True(File.Exists(fixture.Metadata.GetPath(id)));
        Assert.True(File.Exists(record));
    }
}
