using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Path predicates for the Plate Library's files, for fault injection.</summary>
internal static class LibraryFiles
{
    internal static bool IsPlate(string path) => path.Contains(Path.DirectorySeparatorChar + "Profiles" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    internal static bool IsBinding(string path) => path.Contains(Path.DirectorySeparatorChar + "Characters" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    internal static bool IsLibrary(string path) => path.EndsWith("library.json", StringComparison.Ordinal);

    internal static bool IsRecovery(string path) => path.Contains(Path.DirectorySeparatorChar + "Recovery" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    internal static bool IsTrashedPlate(string path) =>
        path.Contains(Path.DirectorySeparatorChar + "Trash" + Path.DirectorySeparatorChar + "Plates" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>True when the Recovery folder holds nothing (or doesn't exist).</summary>
    internal static bool RecoveryIsEmpty(PlateStoragePaths paths) =>
        !Directory.Exists(paths.RecoveryDirectory) || Directory.GetFiles(paths.RecoveryDirectory).Length == 0;
}

/// <summary>
/// A file the Library couldn't OPEN (locked, gone, access denied) is not a damaged file: its content
/// may be intact, so nothing this session ever writes over it. Only content the store — and its
/// backup copy — actually handed back as unusable counts as damage.
/// </summary>
public class UnavailableFileTests
{
    public static TheoryData<string> ReadFailures => new() { "io", "missing", "denied" };

    private static Func<string, Exception> Fault(string kind) => kind switch
    {
        "missing" => _ => new FileNotFoundException("Could not find the file."),
        "denied" => path => new UnauthorizedAccessException($"Access to the path '{path}' is denied."),
        _ => path => new IOException($"The process cannot access the file '{path}' because it is being used by another process."),
    };

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public async Task BindingReadFailure_IsUnavailable_NeverReplaced_AndLoadsIntactOnceReadable(string kind)
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var seeded = await fixture.LoadAsync();
        var first = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var second = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var intact = fixture.ReadBindingJson(Characters.Alice.ContentId);
        fixture.Log.Messages.Clear();

        store.FailRead = LibraryFiles.IsBinding;
        store.FaultFactory = Fault(kind);
        var library = await fixture.LoadAsync();

        Assert.True(library.IsLoaded);
        Assert.Equal(2, library.GetOrderedPlates().Count);
        Assert.Null(library.GetBinding(Characters.Alice.ContentId));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("1001.json", StringComparison.Ordinal) && !m.Contains(fixture.Root, StringComparison.Ordinal));

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.SetActivePlateAsync(Characters.Alice, second.PlateId));
        Assert.Contains("Restart the game", refused.Message, StringComparison.Ordinal);
        Assert.Contains("couldn't be read", refused.Message, StringComparison.Ordinal);

        var created = await Assert.ThrowsAsync<PlateLibraryException>(() => library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice));
        Assert.Contains("The Plate was created", created.Message, StringComparison.Ordinal);
        Assert.Contains("Restart the game", created.Message, StringComparison.Ordinal);
        Assert.Equal(3, library.GetOrderedPlates().Count);
        Assert.Equal(3, fixture.ReadLibraryOrder().Count);

        // The binding is byte-identical, nothing went to Recovery, and the character is still unbound in memory.
        Assert.Equal(intact, fixture.ReadBindingJson(Characters.Alice.ContentId));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.Null(library.GetActivePlateId(Characters.Alice.ContentId));

        // Once the file can be read again, every association is there.
        store.FailRead = null;
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(first.PlateId, reloaded.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal([first.PlateId, second.PlateId], reloaded.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Equal(intact, fixture.ReadBindingJson(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task UnavailableBinding_IsNotAssociatedByTheLegacyMigration_AndIsNotBackedUpTwice()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var profileId = Guid.NewGuid();
        var legacyBinding = LegacyData.VersionOneBinding(4242, null);
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, 4242));
        fixture.WriteBindingJson(4242, legacyBinding);
        store.FailRead = LibraryFiles.IsBinding;

        var library = await fixture.LoadAsync();

        Assert.True(library.IsLoaded);
        Assert.Null(library.GetBinding(4242));
        Assert.Equal(legacyBinding, fixture.ReadBindingJson(4242));
        Assert.Equal([profileId], fixture.ReadLibraryOrder());
    }

    [Fact]
    public async Task DamagedBinding_IsStillReplaced_WithARecoveryCopyFirst()
    {
        // The other half of the contract: content the store hands back as unusable IS damage.
        using var fixture = new LibraryFixture();
        fixture.WriteBindingJson(Characters.Alice.ContentId, "{ \"Version\": 2, \"ContentId\": 1001, \"ProfileIds\": [ broken");
        var library = await fixture.LoadAsync();

        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await library.SetActivePlateAsync(Characters.Alice, plate.PlateId);

        Assert.Equal(plate.PlateId, fixture.ReadBinding(Characters.Alice.ContentId).GetProperty("ActiveProfileId").GetGuid());
        var preserved = Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory));
        Assert.Contains("1001.damaged-", Path.GetFileName(preserved), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public async Task PlateReadFailure_IsListedUnreadable_AndLeftUntouched(string kind)
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var good = Guid.NewGuid();
        var locked = Guid.NewGuid();
        fixture.WritePlateJson(good, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, good, "Good", fixture.Clock.Now), JsonOptions.Default));
        var lockedJson = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, locked, "Locked", fixture.Clock.Now), JsonOptions.Default);
        fixture.WritePlateJson(locked, lockedJson);
        store.FailRead = p => p == fixture.Paths.GetPlatePath(locked);
        store.FaultFactory = Fault(kind);

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(good)!.Status);
        Assert.Equal(PlateStatus.Unreadable, library.FindPlate(locked)!.Status);
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.RenamePlateAsync(locked, "x"));
        Assert.Equal(lockedJson, fixture.ReadPlateJson(locked));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));

        store.FailRead = null;
        Assert.Equal(PlateStatus.Ready, (await fixture.LoadAsync()).FindPlate(locked)!.Status);
    }

    [Fact]
    public async Task PlatesFolderUnreadable_LoadFails_AndWritesNothing()
    {
        var store = new FaultInjectingStore { FailList = d => d.EndsWith(Path.DirectorySeparatorChar + "Profiles", StringComparison.Ordinal) };
        using var fixture = new LibraryFixture(store);
        fixture.WriteBindingJson(Characters.Alice.ContentId, LegacyData.VersionOneBinding(Characters.Alice.ContentId, null));
        var library = fixture.CreateService();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.InitializeAsync());

        Assert.False(library.IsLoaded);
        Assert.False(File.Exists(fixture.Paths.LibraryFile));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.False(Directory.Exists(fixture.Paths.MigrationBackupDirectory));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.CreatePlateAsync(PlateStartingLayout.Blank, null));
    }

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public async Task IndexReadFailure_LoadsReadOnly_AndNeverOverwritesTheIndex(string kind)
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var seeded = await fixture.LoadAsync();
        var a = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "A");
        fixture.Clock.Tick();
        var b = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "B");
        await seeded.MovePlateAsync(a.PlateId, b.PlateId, placeAfter: false);
        var savedOrder = File.ReadAllText(fixture.Paths.LibraryFile);
        Assert.Equal([a.PlateId, b.PlateId], fixture.ReadLibraryOrder());

        store.FailRead = LibraryFiles.IsLibrary;
        store.FaultFactory = Fault(kind);
        var library = await fixture.LoadAsync();

        // Loaded, newest-first for this session only; the saved order is untouched and not in Recovery.
        Assert.True(library.IsLoaded);
        Assert.Equal([b.PlateId, a.PlateId], library.GetOrderedPlates().Select(p => p.PlateId));
        await library.MovePlateAsync(a.PlateId, b.PlateId, placeAfter: false);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "C");
        Assert.Equal(savedOrder, File.ReadAllText(fixture.Paths.LibraryFile));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));

        store.FailRead = null;
        Assert.Equal([a.PlateId, b.PlateId], (await fixture.LoadAsync()).GetOrderedPlates().Select(p => p.PlateId).Take(2));
    }
}

/// <summary>
/// A file the store could only read from its backup copy loads normally, but the player is told
/// once and the damaged on-disk bytes go to Recovery before any later write replaces them.
/// </summary>
public class BackupRecoveryObservabilityTests
{
    [Fact]
    public async Task DamagedPlate_RecoveredFromBackup_IsLogged_AndTheDamagedFileIsKeptInRecovery()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = Guid.NewGuid();
        store.Backups[fixture.Paths.GetPlatePath(plateId)] = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "From backup", fixture.Clock.Now), JsonOptions.Default);
        fixture.WritePlateJson(plateId, "{ truncated");

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        var warning = Assert.Single(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("backup", StringComparison.Ordinal));
        Assert.Contains($"{plateId}.json", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, warning, StringComparison.Ordinal);
        var preserved = Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory));
        Assert.Equal("{ truncated", File.ReadAllText(preserved));
        Assert.StartsWith($"{plateId}.damaged-", Path.GetFileName(preserved), StringComparison.Ordinal);

        // The load itself never rewrites the file; the next save does, and the Recovery copy survives it.
        Assert.Equal("{ truncated", fixture.ReadPlateJson(plateId));
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));
        Assert.Contains("From backup", fixture.ReadPlateJson(plateId), StringComparison.Ordinal);
        Assert.Equal("{ truncated", File.ReadAllText(preserved));
    }

    [Fact]
    public async Task DamagedBinding_RecoveredFromBackup_LoadsIntact_AndIsKeptInRecovery()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var seeded = await fixture.LoadAsync();
        var first = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var second = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        fixture.WriteBindingJson(Characters.Alice.ContentId, "{ \"Version\": 2, \"ContentId\": 1001, \"Pro");
        fixture.Log.Messages.Clear();

        var library = await fixture.LoadAsync();

        Assert.Equal(first.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal([first.PlateId, second.PlateId], library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("1001.json", StringComparison.Ordinal) && m.Contains("backup", StringComparison.Ordinal));
        var preserved = Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory));
        Assert.Equal("{ \"Version\": 2, \"ContentId\": 1001, \"Pro", File.ReadAllText(preserved));

        // A write for that character replaces the damaged file with the full, intact binding.
        await library.SetActivePlateAsync(Characters.Alice, second.PlateId);
        Assert.Equal([first.PlateId, second.PlateId], fixture.ReadBinding(Characters.Alice.ContentId).GetProperty("ProfileIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory));
    }

    [Fact]
    public async Task DamagedIndex_RecoveredFromBackup_KeepsTheOrder_AndIsKeptInRecovery()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var seeded = await fixture.LoadAsync();
        var a = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "A");
        fixture.Clock.Tick();
        var b = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "B");
        await seeded.MovePlateAsync(a.PlateId, b.PlateId, placeAfter: false);
        fixture.WriteLibraryJson("{ \"Version\": 1, \"Ord");

        var library = await fixture.LoadAsync();

        Assert.Equal([a.PlateId, b.PlateId], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("library.json", StringComparison.Ordinal) && m.Contains("backup", StringComparison.Ordinal));
        Assert.Equal("{ \"Version\": 1, \"Ord", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory))));
    }

    [Fact]
    public async Task HealthyPlate_NoLog_NoRecoveryCopy()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = Guid.NewGuid();
        store.Backups[fixture.Paths.GetPlatePath(plateId)] = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Stale backup", fixture.Clock.Now), JsonOptions.Default);
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "On disk", fixture.Clock.Now), JsonOptions.Default));

        var library = await fixture.LoadAsync();

        Assert.Equal("On disk", library.FindPlate(plateId)!.DisplayName);
        Assert.DoesNotContain(fixture.Log.Messages, m => m.Contains("backup", StringComparison.Ordinal));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
    }

    [Fact]
    public async Task NewerVersionPlate_NoFallback_NoRecoveryCopy()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = Guid.NewGuid();
        store.Backups[fixture.Paths.GetPlatePath(plateId)] = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Old backup", fixture.Clock.Now), JsonOptions.Default);
        var newer = $$"""{ "Version": 50, "ProfileId": "{{plateId}}", "Name": "Newer" }""";
        fixture.WritePlateJson(plateId, newer);

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.NewerVersion, library.FindPlate(plateId)!.Status);
        Assert.DoesNotContain(fixture.Log.Messages, m => m.Contains("backup", StringComparison.Ordinal));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.Equal(newer, fixture.ReadPlateJson(plateId));
    }

    [Fact]
    public async Task RecoveryCopyFailure_IsLogged_AndThePlateStillLoads()
    {
        var store = new RecoveryCopyFailingBackupStore();
        using var fixture = new LibraryFixture(store);
        var plateId = Guid.NewGuid();
        store.Backups[fixture.Paths.GetPlatePath(plateId)] = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "From backup", fixture.Clock.Now), JsonOptions.Default);
        fixture.WritePlateJson(plateId, "{ truncated");

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        Assert.Equal("From backup", library.FindPlate(plateId)!.DisplayName);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("Recovery", StringComparison.Ordinal) && !m.Contains(fixture.Root, StringComparison.Ordinal));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
    }

    /// <summary>A backup-simulating store whose copies into Recovery fail.</summary>
    private sealed class RecoveryCopyFailingBackupStore : IPlateFileStore
    {
        private readonly BackupSimulatingStore inner = new();

        internal System.Collections.Generic.Dictionary<string, string> Backups => inner.Backups;

        public bool FileExists(string path) => inner.FileExists(path);

        public System.Collections.Generic.IReadOnlyList<string> ListFiles(string directory, string searchPattern) => inner.ListFiles(directory, searchPattern);

        public Task ReadTextAsync(string path, Action<string> reader) => inner.ReadTextAsync(path, reader);

        public Task WriteTextAsync(string path, string contents) => inner.WriteTextAsync(path, contents);

        public void MoveFile(string sourcePath, string destinationPath) => inner.MoveFile(sourcePath, destinationPath);

        public void CopyFile(string sourcePath, string destinationPath) =>
            throw new IOException(LibraryFiles.IsRecovery(destinationPath) ? $"There is not enough space on the disk: '{destinationPath}'" : "Unexpected copy.");

        public void DeleteFile(string path) => inner.DeleteFile(path);
    }
}

/// <summary>
/// An import's document write is its commit point. A store can report failure after the file
/// landed; the importer then rolls its images back, so the document must not stay behind.
/// </summary>
public class ImportLateFailureTests
{
    [Fact]
    public async Task ImportWriteFailingAfterTheFileLanded_MovesItToTheTrash_AndListsNothing()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var existing = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var plateId = Guid.NewGuid();
        var raw = PlateDocuments.ToJson(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Imported", fixture.Clock.Now));
        store.ThrowAfterWrite = LibraryFiles.IsPlate;

        await Assert.ThrowsAsync<IOException>(() => library.ImportPlateAsync(plateId, raw));

        Assert.False(File.Exists(fixture.Paths.GetPlatePath(plateId)));
        var trashed = Assert.Single(Directory.GetFiles(fixture.Paths.PlateTrashDirectory));
        Assert.StartsWith($"{plateId}.deleted-", Path.GetFileName(trashed), StringComparison.Ordinal);
        Assert.Equal([existing.PlateId], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal([existing.PlateId], fixture.ReadLibraryOrder());
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("trash", StringComparison.Ordinal));

        store.ThrowAfterWrite = null;
        Assert.Equal([existing.PlateId], (await fixture.LoadAsync()).GetOrderedPlates().Select(p => p.PlateId));
    }

    [Fact]
    public async Task ImportWriteFailingBeforeTheFileLanded_TouchesNothing()
    {
        var store = new FaultInjectingStore { FailWrite = LibraryFiles.IsPlate };
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var plateId = Guid.NewGuid();

        await Assert.ThrowsAsync<IOException>(() => library.ImportPlateAsync(plateId, PlateDocuments.ToJson(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Imported", fixture.Clock.Now))));

        Assert.Empty(library.GetOrderedPlates());
        Assert.False(Directory.Exists(fixture.Paths.PlateTrashDirectory));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.Contains("trash", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommitFailureAfterTheWriteLanded_LeavesNoHalfImportedPlate()
    {
        var store = new FaultInjectingStore();
        using var fixture = new PackageFixture(store: store);
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var path = fixture.Export(packages, plateId);
        var before = fixture.SnapshotInstallation();
        var listedBefore = library.GetOrderedPlates().Select(p => p.PlateId).ToList();

        store.ThrowAfterWrite = LibraryFiles.IsPlate;
        using var staged = packages.Inspect(path);
        var result = await packages.ImportAsync(staged);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageErrorCode.CommitFailed, result.Error!.Code);
        Assert.Equal(listedBefore, library.GetOrderedPlates().Select(p => p.PlateId));

        // Images rolled back, the Plates folder as it was; the only new file is the trashed document.
        var after = fixture.SnapshotInstallation();
        var added = after.Keys.Except(before.Keys).ToList();
        var trashed = Assert.Single(added);
        Assert.StartsWith(Path.Combine("Trash", "Plates") + Path.DirectorySeparatorChar, trashed, StringComparison.Ordinal);
        Assert.All(before, entry => Assert.Equal(entry.Value, after[entry.Key]));

        store.ThrowAfterWrite = null;
        var reloaded = await fixture.Library.LoadAsync();
        Assert.Equal(listedBefore, reloaded.GetOrderedPlates().Select(p => p.PlateId));
    }
}

/// <summary>The image reference scan's problems can be quoted to the player, so they never carry a path.</summary>
public class AssetScanProblemTests
{
    [Fact]
    public async Task LibraryScan_Problems_NeverContainLocalPaths()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var trashed = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Trashed");
        await library.DeletePlateAsync(trashed.PlateId);
        store.FailRead = LibraryFiles.IsTrashedPlate;
        store.FaultFactory = path => new IOException($"The process cannot access the file 'C:\\Users\\Someone\\AppData\\{Path.GetFileName(path)}' ({path}).");

        var scan = await library.ScanAssetReferencesAsync();

        Assert.False(scan.IsComplete);
        var problem = Assert.Single(scan.Problems);
        Assert.Contains($"{trashed.PlateId}.deleted-", problem, StringComparison.Ordinal);
        Assert.Contains("IOException", problem, StringComparison.Ordinal);
        Assert.All(scan.Problems, p => Assert.False(AetherFrame.Services.Diagnostics.UserFacingError.ContainsPath(p), p));
        Assert.All(scan.Problems, p => Assert.DoesNotContain(fixture.Root, p, StringComparison.Ordinal));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("scanning", StringComparison.Ordinal) && !m.Contains(fixture.Root, StringComparison.Ordinal));
    }
}

/// <summary>A load that unloading stops between (or inside) two files has found nothing wrong.</summary>
public class AbandonedLoadTests
{
    [Fact]
    public async Task LoadAbandonedMidPlate_DoesNotLogThePlateAsDamaged_AndLeavesTheLibraryUnloaded()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        fixture.WritePlateJson(a, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, a, "A", fixture.Clock.Now), JsonOptions.Default));
        fixture.WritePlateJson(b, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, b, "B", fixture.Clock.Now), JsonOptions.Default));
        store.FailRead = p => p == fixture.Paths.GetPlatePath(b);
        store.FaultFactory = _ => new OperationAbandonedException();
        var library = fixture.CreateService();

        await Assert.ThrowsAsync<OperationAbandonedException>(() => library.InitializeAsync());

        Assert.False(library.IsLoaded);
        Assert.Empty(library.GetOrderedPlates());
        Assert.DoesNotContain(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal));
        Assert.False(File.Exists(fixture.Paths.LibraryFile));
    }

    [Fact]
    public async Task LoadAbandonedMidBinding_DoesNotLogTheBindingAsDamaged()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        fixture.WriteBindingJson(Characters.Alice.ContentId, LegacyData.VersionOneBinding(Characters.Alice.ContentId, null));
        store.FailRead = LibraryFiles.IsBinding;
        store.FaultFactory = _ => new OperationAbandonedException();
        var library = fixture.CreateService();

        await Assert.ThrowsAsync<OperationAbandonedException>(() => library.InitializeAsync());

        Assert.False(library.IsLoaded);
        Assert.DoesNotContain(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal));
        Assert.False(File.Exists(fixture.Paths.LibraryFile));
    }

    [Fact]
    public async Task LoadAbandonedMidIndex_IsNotTreatedAsADamagedIndex()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var seeded = await fixture.LoadAsync();
        await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var savedOrder = File.ReadAllText(fixture.Paths.LibraryFile);
        store.FailRead = LibraryFiles.IsLibrary;
        store.FaultFactory = _ => new OperationAbandonedException();
        var library = fixture.CreateService();

        await Assert.ThrowsAsync<OperationAbandonedException>(() => library.InitializeAsync());

        Assert.False(library.IsLoaded);
        Assert.Equal(savedOrder, File.ReadAllText(fixture.Paths.LibraryFile));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
    }
}

/// <summary>
/// Once a new Plate's document is written the Plate exists: a binding or index write failing
/// afterwards is reported for what it is, never as "nothing happened" (which would invite a retry
/// that creates "Name 2").
/// </summary>
public class CreateHalfFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateBindingFailure_KeepsThePlate_ListsIt_AndReportsALinkFailure(bool faultAsync)
    {
        var store = new FaultInjectingStore { FaultAsync = faultAsync };
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var existing = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var bindingBefore = fixture.ReadBindingJson(Characters.Alice.ContentId);
        store.FailWrite = LibraryFiles.IsBinding;

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Half"));

        Assert.Contains("The Plate was created", refused.Message, StringComparison.Ordinal);
        var created = Assert.Single(library.GetOrderedPlates(), p => p.DisplayName == "Half");
        Assert.Equal(created.PlateId, library.GetOrderedPlates()[0].PlateId);
        Assert.True(File.Exists(fixture.Paths.GetPlatePath(created.PlateId)));
        Assert.Equal([created.PlateId, existing.PlateId], fixture.ReadLibraryOrder());

        // Nothing about the character changed, on disk or in memory.
        Assert.Equal(bindingBefore, fixture.ReadBindingJson(Characters.Alice.ContentId));
        Assert.DoesNotContain(created.PlateId, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Equal(existing.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Empty(created.CharacterNames);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("could not associate", StringComparison.Ordinal));

        // Set Active afterwards links it like any other Plate; no second copy was ever made.
        store.FailWrite = null;
        await library.SetActivePlateAsync(Characters.Alice, created.PlateId);
        Assert.Equal(2, library.GetOrderedPlates().Count);
        Assert.Equal(2, Directory.GetFiles(fixture.Paths.PlatesDirectory).Length);
    }

    [Fact]
    public async Task CreateBindingFailure_ForACharactersFirstPlate_ReportsNotActive()
    {
        var store = new FaultInjectingStore { FailWrite = LibraryFiles.IsBinding };
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Bob));

        Assert.Single(library.GetOrderedPlates());
        Assert.Null(library.GetActivePlateId(Characters.Bob.ContentId));
        Assert.Null(library.GetBinding(Characters.Bob.ContentId));
        Assert.False(File.Exists(fixture.Paths.GetBindingPath(Characters.Bob.ContentId)));
    }

    [Fact]
    public async Task CreateFromTemplateBindingFailure_KeepsTheNewPlate()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var template = PlateDocuments.ToJson(SampleDocuments.Rich(Guid.NewGuid(), "Template", fixture.Clock.Now));
        var templateJson = template.ToJsonString();
        store.FailWrite = LibraryFiles.IsBinding;

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.CreatePlateFromTemplateAsync(template, "From Template", Characters.Alice));

        var created = Assert.Single(library.GetOrderedPlates());
        Assert.Equal("From Template", created.DisplayName);
        Assert.Equal(2, library.OpenDocumentForEditing(created.PlateId).Elements.Count);
        Assert.Equal(templateJson, template.ToJsonString());
        Assert.False(File.Exists(fixture.Paths.GetBindingPath(Characters.Alice.ContentId)));
    }

    [Fact]
    public async Task CreateOrderWriteFailure_KeepsThePlate_AndItIsListedAfterReload()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var existing = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        store.FailWrite = LibraryFiles.IsLibrary;

        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Unindexed");

        Assert.False(created.BecameActive);
        Assert.Equal([created.PlateId, existing.PlateId], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Contains(created.PlateId, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Equal([existing.PlateId], fixture.ReadLibraryOrder());
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("Library order", StringComparison.Ordinal));

        store.FailWrite = null;
        var reloaded = await fixture.LoadAsync();
        Assert.Contains(reloaded.GetOrderedPlates(), p => p.PlateId == created.PlateId && p.DisplayName == "Unindexed");
        Assert.Contains(created.PlateId, fixture.ReadLibraryOrder());
    }

    [Fact]
    public async Task CorruptBinding_WhoseRecoveryCopyFails_IsNeverOverwritten()
    {
        var store = new FaultInjectingStore { FailCopy = LibraryFiles.IsBinding };
        using var fixture = new LibraryFixture(store);
        fixture.WriteBindingJson(Characters.Alice.ContentId, "not json at all");
        var library = await fixture.LoadAsync();

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice));
        await Assert.ThrowsAsync<IOException>(() => library.SetActivePlateAsync(Characters.Alice, library.GetOrderedPlates()[0].PlateId));

        Assert.Equal("not json at all", fixture.ReadBindingJson(Characters.Alice.ContentId));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        var created = Assert.Single(library.GetOrderedPlates());
        Assert.True(File.Exists(fixture.Paths.GetPlatePath(created.PlateId)));

        var reloaded = await fixture.LoadAsync();
        Assert.Equal(created.PlateId, Assert.Single(reloaded.GetOrderedPlates()).PlateId);
        Assert.Equal("not json at all", fixture.ReadBindingJson(Characters.Alice.ContentId));
    }
}

/// <summary>
/// Startup writes are optional: everything was read before they begin, so a write that fails
/// leaves the Library loaded and is retried at the next startup — without an index ever claiming a
/// migration finished that didn't.
/// </summary>
public class StartupWriteFailureTests
{
    private const ulong Owner = 4242;

    [Fact]
    public async Task IndexWriteFailure_AtStartup_StillLoads_AndTheNextStartupWritesIt()
    {
        var store = new FaultInjectingStore { FailWrite = LibraryFiles.IsLibrary };
        using var fixture = new LibraryFixture(store);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        fixture.WritePlateJson(a, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, a, "A", fixture.Clock.Now), JsonOptions.Default));
        fixture.WritePlateJson(b, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, b, "B", fixture.Clock.Now.AddDays(1)), JsonOptions.Default));

        var library = await fixture.LoadAsync();

        Assert.True(library.IsLoaded);
        Assert.Equal([b, a], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.False(File.Exists(fixture.Paths.LibraryFile));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("Plate order", StringComparison.Ordinal));

        // Operations work; only the order still can't be saved while the failure persists.
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "C");
        Assert.Equal([created.PlateId, b, a], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.False(File.Exists(fixture.Paths.LibraryFile));

        store.FailWrite = null;
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(3, reloaded.GetOrderedPlates().Count);
        Assert.Equal(3, fixture.ReadLibraryOrder().Count);
    }

    [Fact]
    public async Task FirstRunMigration_BindingWriteFailure_LoadsButNeverWritesTheIndex()
    {
        var store = new FaultInjectingStore { FailWrite = LibraryFiles.IsBinding };
        using var fixture = new LibraryFixture(store);
        var profileId = Guid.NewGuid();
        var legacyBinding = LegacyData.VersionOneBinding(Owner, profileId, profileId);
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, Owner));
        fixture.WriteBindingJson(Owner, legacyBinding);

        var library = await fixture.LoadAsync();

        Assert.True(library.IsLoaded);
        Assert.Equal(profileId, Assert.Single(library.GetOrderedPlates()).PlateId);
        Assert.Equal(profileId, library.GetActivePlateId(Owner));
        Assert.Equal(legacyBinding, fixture.ReadBindingJson(Owner));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains($"{Owner}.json", StringComparison.Ordinal) && !m.Contains(fixture.Root, StringComparison.Ordinal));

        // No index this session — not even after an operation that normally writes it.
        Assert.False(File.Exists(fixture.Paths.LibraryFile));
        await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        Assert.False(File.Exists(fixture.Paths.LibraryFile));

        // The next startup migrates again and finishes the job.
        store.FailWrite = null;
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(profileId, reloaded.GetActivePlateId(Owner));
        Assert.Equal(2, reloaded.GetOrderedPlates().Count);
        Assert.Equal(2, fixture.ReadBinding(Owner).GetProperty("Version").GetInt32());
        Assert.Equal(2, fixture.ReadLibraryOrder().Count);
    }

    [Fact]
    public async Task LegacyBindingBackupCopyFailure_IsLoggedAndMigrationContinues()
    {
        var store = new FaultInjectingStore { FailCopy = LibraryFiles.IsBinding };
        using var fixture = new LibraryFixture(store);
        var profileId = Guid.NewGuid();
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, Owner));
        fixture.WriteBindingJson(Owner, LegacyData.VersionOneBinding(Owner, profileId, profileId));

        var library = await fixture.LoadAsync();

        Assert.True(library.IsLoaded);
        Assert.Equal(profileId, library.GetActivePlateId(Owner));
        Assert.Equal(2, fixture.ReadBinding(Owner).GetProperty("Version").GetInt32());
        Assert.Equal([profileId], fixture.ReadLibraryOrder());
        Assert.False(File.Exists(Path.Combine(fixture.Paths.MigrationBackupDirectory, "Characters", $"{Owner}.json")));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("back up", StringComparison.Ordinal) && !m.Contains(fixture.Root, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DamagedIndex_WhoseRecoveryCopyFails_LoadsReadOnly()
    {
        var store = new FaultInjectingStore { FailCopy = LibraryFiles.IsLibrary };
        using var fixture = new LibraryFixture(store);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        fixture.WritePlateJson(a, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, a, "A", fixture.Clock.Now), JsonOptions.Default));
        fixture.WritePlateJson(b, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, b, "B", fixture.Clock.Now.AddDays(1)), JsonOptions.Default));
        fixture.WriteLibraryJson("{{{{ not an index");

        var library = await fixture.LoadAsync();

        Assert.True(library.IsLoaded);
        Assert.Equal([b, a], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("Recovery", StringComparison.Ordinal));

        // Reordering works in memory and never writes over the damaged file.
        await library.MovePlateAsync(a, b, placeAfter: false);
        Assert.Equal([a, b], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal("{{{{ not an index", File.ReadAllText(fixture.Paths.LibraryFile));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));

        // Once the copy can be made, the next startup preserves and rebuilds as usual.
        store.FailCopy = null;
        await fixture.LoadAsync();
        Assert.Equal([b, a], fixture.ReadLibraryOrder());
        Assert.Equal("{{{{ not an index", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory))));
    }
}
