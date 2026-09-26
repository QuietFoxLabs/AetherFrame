using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Lifecycle;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Dalamud's reliable storage leaks the handle of "{path}.tmp" when a write fails inside it, after
/// which every later write of that file fails with a sharing violation until the game restarts.
/// The fallback store writes such a file itself — and only such a file.
/// </summary>
public class StuckTempFallbackFileStoreTests
{
    private const string Contents = "{\"Version\":1}";

    [Theory]
    [InlineData(StuckTempFallbackFileStore.SharingViolation, false)]
    [InlineData(StuckTempFallbackFileStore.LockViolation, false)]
    [InlineData(StuckTempFallbackFileStore.SharingViolation, true)]
    public async Task SharingOrLockViolation_WithTheTemporaryFilePresent_WritesTheFileDirectly(int nativeError, bool faultAsync)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "Profiles", "plate.json");
        var stuck = StuckTempFallbackFileStore.TemporaryPathFor(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(stuck, "half-written");

        var inner = new FaultInjectingStore { FailWriteAfter = 0, FaultAsync = faultAsync, FaultFactory = _ => new Win32Exception(nativeError) };
        var log = new TestLog();
        var store = new StuckTempFallbackFileStore(inner, log);

        await store.WriteTextAsync(path, Contents);

        Assert.Equal(Contents, File.ReadAllText(path, Encoding.UTF8));
        Assert.Equal(Encoding.UTF8.GetBytes(Contents), File.ReadAllBytes(path));

        // The stuck temporary file belongs to the storage that still holds it open: left alone.
        Assert.Equal("half-written", File.ReadAllText(stuck));

        // No temporary file of the fallback's own is left behind either.
        Assert.Equal(new[] { "plate.json", "plate.json.tmp" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).Order());

        var warning = Assert.Single(log.Messages);
        Assert.StartsWith("W ", warning);
        Assert.Contains("plate.json", warning);
        Assert.DoesNotContain(directory.Path, warning);
        Assert.DoesNotContain("Profiles", warning);
    }

    [Fact]
    public async Task IOExceptionCarryingASharingViolation_FallsBackTheSameWay()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "plate.json");
        File.WriteAllText(StuckTempFallbackFileStore.TemporaryPathFor(path), "half-written");

        var inner = new FaultInjectingStore
        {
            FailWriteAfter = 0,
            FaultFactory = p => new IOException($"The process cannot access the file '{p}'") { HResult = StuckTempFallbackFileStore.SharingViolationHResult },
        };
        var store = new StuckTempFallbackFileStore(inner, new TestLog());

        await store.WriteTextAsync(path, Contents);

        Assert.Equal(Contents, File.ReadAllText(path));
    }

    [Fact]
    public async Task TheStuckFile_IsReportedOncePerPath()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "plate.json");
        var other = Path.Combine(directory.Path, "other.json");
        File.WriteAllText(StuckTempFallbackFileStore.TemporaryPathFor(path), string.Empty);
        File.WriteAllText(StuckTempFallbackFileStore.TemporaryPathFor(other), string.Empty);

        var inner = new FaultInjectingStore { FailWriteAfter = 0, FaultFactory = _ => new Win32Exception(StuckTempFallbackFileStore.SharingViolation) };
        var log = new TestLog();
        var store = new StuckTempFallbackFileStore(inner, log);

        await store.WriteTextAsync(path, "1");
        await store.WriteTextAsync(path, "2");
        await store.WriteTextAsync(other, "3");

        Assert.Equal("2", File.ReadAllText(path));
        Assert.Equal("3", File.ReadAllText(other));
        Assert.Equal(2, log.Messages.Count);
        Assert.Single(log.Messages, m => m.Contains("\"plate.json\""));
        Assert.Single(log.Messages, m => m.Contains("\"other.json\""));
    }

    [Fact]
    public async Task SharingViolation_WithoutTheTemporaryFile_IsNotTheLeak_AndPropagates()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "plate.json");

        var inner = new FaultInjectingStore { FailWriteAfter = 0, FaultFactory = _ => new Win32Exception(StuckTempFallbackFileStore.SharingViolation) };
        var log = new TestLog();
        var store = new StuckTempFallbackFileStore(inner, log);

        var failure = await Assert.ThrowsAsync<Win32Exception>(() => store.WriteTextAsync(path, Contents));

        Assert.Equal(StuckTempFallbackFileStore.SharingViolation, failure.NativeErrorCode);
        Assert.False(File.Exists(path));
        Assert.Empty(log.Messages);
    }

    public static TheoryData<Exception> OtherFailures => new()
    {
        new Win32Exception(112),
        new IOException("disk full") { HResult = unchecked((int)0x80070070) },
        new IOException("plain"),
        new UnauthorizedAccessException("denied"),
        new OperationAbandonedException(),
        new ObjectDisposedException("ReliableFileStorage"),
    };

    [Theory]
    [MemberData(nameof(OtherFailures))]
    public async Task AnyOtherFailure_PropagatesUnchanged_EvenWithTheTemporaryFilePresent(Exception failure)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "plate.json");
        File.WriteAllText(StuckTempFallbackFileStore.TemporaryPathFor(path), "half-written");

        var inner = new FaultInjectingStore { FailWriteAfter = 0, FaultAsync = true, FaultFactory = _ => failure };
        var log = new TestLog();
        var store = new StuckTempFallbackFileStore(inner, log);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => store.WriteTextAsync(path, Contents));

        Assert.Same(failure, thrown);
        Assert.False(File.Exists(path));
        Assert.Empty(log.Messages);
    }

    [Fact]
    public void OnlySharingAndLockViolations_Qualify()
    {
        Assert.True(StuckTempFallbackFileStore.IsSharingOrLockViolation(new Win32Exception(32)));
        Assert.True(StuckTempFallbackFileStore.IsSharingOrLockViolation(new Win32Exception(33)));
        Assert.True(StuckTempFallbackFileStore.IsSharingOrLockViolation(new IOException("x") { HResult = unchecked((int)0x80070020) }));
        Assert.True(StuckTempFallbackFileStore.IsSharingOrLockViolation(new IOException("x") { HResult = unchecked((int)0x80070021) }));

        Assert.False(StuckTempFallbackFileStore.IsSharingOrLockViolation(new Win32Exception(112)));
        Assert.False(StuckTempFallbackFileStore.IsSharingOrLockViolation(new Win32Exception(5)));
        Assert.False(StuckTempFallbackFileStore.IsSharingOrLockViolation(new IOException("x") { HResult = 32 }));
        Assert.False(StuckTempFallbackFileStore.IsSharingOrLockViolation(new IOException("x")));
        Assert.False(StuckTempFallbackFileStore.IsSharingOrLockViolation(new OperationAbandonedException()));
        Assert.False(StuckTempFallbackFileStore.IsSharingOrLockViolation(new ObjectDisposedException("x")));
    }

    [Fact]
    public async Task AnAbandonedOperation_NeverReachesTheFallback()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "plate.json");
        File.WriteAllText(StuckTempFallbackFileStore.TemporaryPathFor(path), "half-written");

        // The plugin's composition: abandonment is checked outside the fallback.
        var inner = new FaultInjectingStore { FailWriteAfter = 0, FaultFactory = _ => new Win32Exception(StuckTempFallbackFileStore.SharingViolation) };
        var operations = new OwnedOperations();
        var store = new ShutdownGuardedFileStore(new StuckTempFallbackFileStore(inner, new TestLog()), operations);

        Assert.True(operations.TryBegin(out var lease));
        Assert.False(await operations.ShutdownAsync(TimeSpan.FromMilliseconds(50)));

        await Assert.ThrowsAsync<OperationAbandonedException>(() => store.WriteTextAsync(path, Contents));

        Assert.False(File.Exists(path));
        Assert.Equal(0, inner.FailedOperations);
        lease.Dispose();
    }

    [Fact]
    public async Task ReadsMovesCopiesAndDeletes_PassThrough()
    {
        using var directory = new TempDirectory();
        var inner = new FaultInjectingStore();
        var store = new StuckTempFallbackFileStore(inner, new TestLog());
        var path = Path.Combine(directory.Path, "a.json");
        var moved = Path.Combine(directory.Path, "b.json");
        var copied = Path.Combine(directory.Path, "c.json");

        await store.WriteTextAsync(path, Contents);
        Assert.True(store.FileExists(path));
        var read = string.Empty;
        await store.ReadTextAsync(path, text => read = text);
        Assert.Equal(Contents, read);

        store.CopyFile(path, copied);
        store.MoveFile(path, moved);
        Assert.Equal(new[] { "b.json", "c.json" }, store.ListFiles(directory.Path, "*.json").Select(Path.GetFileName).Order());
        store.DeleteFile(copied);
        Assert.False(store.FileExists(copied));
    }

    // ------------------------------------------------------------ through the Library

    [Fact]
    public async Task SaveOverAStuckTemporaryFile_Succeeds_ThroughTheLibrary()
    {
        var faulting = new FaultInjectingStore();
        var log = new TestLog();
        using var fixture = new LibraryFixture(new StuckTempFallbackFileStore(faulting, log));
        var library = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var session = OpenSession(fixture, library, created.PlateId);
        session.AddTextElement("Saved past the leak");

        // Dalamud's first, failed write left "{path}.tmp" open; every later write of it fails the same way.
        var platePath = fixture.Paths.GetPlatePath(created.PlateId);
        File.WriteAllText(StuckTempFallbackFileStore.TemporaryPathFor(platePath), "half-written");
        faulting.FailWriteAfter = 0;
        faulting.FaultAsync = true;
        faulting.FaultFactory = _ => new Win32Exception(StuckTempFallbackFileStore.SharingViolation);

        Assert.True(await session.SaveProfileAsync());

        Assert.Null(session.ErrorMessage);
        Assert.False(session.IsDirty);
        Assert.Contains("Saved past the leak", fixture.ReadPlateJson(created.PlateId));
        Assert.Contains(log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains(Path.GetFileName(platePath)));
        Assert.DoesNotContain(log.Messages, m => m.Contains(fixture.Root));
    }

    [Fact]
    public async Task SaveFailingWithAnotherWindowsError_ShowsThePlainMessage_AndTheNextSaveSucceeds()
    {
        var faulting = new FaultInjectingStore();
        using var fixture = new LibraryFixture(new StuckTempFallbackFileStore(faulting, new TestLog()));
        var library = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var before = fixture.ReadPlateJson(created.PlateId);
        var session = OpenSession(fixture, library, created.PlateId);
        session.AddTextElement("Unsaved");

        // Disk full, reported as Dalamud does: a Win32Exception from a faulted thread-pool task.
        faulting.FailWriteAfter = 0;
        faulting.FaultAsync = true;
        faulting.FaultFactory = _ => new Win32Exception(112);

        Assert.False(await session.SaveProfileAsync());

        Assert.Equal(EditorSession.SaveFailedMessage, session.ErrorMessage);
        Assert.True(session.IsDirty);
        Assert.Equal(before, fixture.ReadPlateJson(created.PlateId));
        Assert.Equal(created.PlateId, Assert.Single(library.GetOrderedPlates()).PlateId);

        // Space freed: the same Plate saves.
        faulting.FailWriteAfter = null;
        Assert.True(await session.SaveProfileAsync());
        Assert.False(session.IsDirty);
        Assert.Contains("Unsaved", fixture.ReadPlateJson(created.PlateId));
    }

    private static EditorSession OpenSession(LibraryFixture fixture, Services.Plates.PlateLibraryService library, Guid plateId)
    {
        var profiles = new ProfileService(library);
        profiles.OpenPlate(plateId);
        var assets = new AssetStorageService(fixture.Paths.AssetsDirectory, fixture.Paths.AssetStagingDirectory, new AssetMetadataStore(fixture.Paths.AssetMetadataDirectory));
        var session = new EditorSession(profiles, assets, new FakeImages(), fixture.Log, () => 1);
        session.SyncWithCurrentProfile();
        return session;
    }
}
