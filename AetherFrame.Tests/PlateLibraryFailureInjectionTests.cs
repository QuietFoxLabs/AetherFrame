using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Lifecycle;
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
