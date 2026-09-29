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
/// which every later write of that file fails until the game restarts. The fallback store writes
/// such a file itself — and only such a file.
///
/// <para>The failures are shaped exactly as Dalamud 15.0.3.5 produces them (checked by calling its
/// FilesystemUtil.WriteAllBytesSafe with the temporary file held open): the write that leaks the
/// handle fails with its own error (a full disk: ERROR_DISK_FULL, 112), and every later one with
/// ERROR_INVALID_HANDLE (6), because the sharing violation of its CreateFile is lost when it
/// writes through the invalid handle anyway. What marks the leak is the temporary file held open
/// with no sharing, which these tests reproduce with a <see cref="FileStream"/> opened with
/// <see cref="FileShare.None"/>.</para>
/// </summary>
public class StuckTempFallbackFileStoreTests
{
    private const string Contents = "{\"Version\":1}";

    /// <summary>ERROR_INVALID_HANDLE: what every write after the leak throws.</summary>
    private const int InvalidHandle = 6;

    /// <summary>ERROR_DISK_FULL: the write that leaked the handle.</summary>
    private const int DiskFull = 112;

    /// <summary>The temporary file as the failed write leaves it: created, part-written, and still
    /// open with no sharing at all.</summary>
    private static FileStream HoldLikeTheLeakedHandle(string temporaryPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
        var leaked = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        leaked.Write(Encoding.UTF8.GetBytes("half-written"));
        leaked.Flush();
        return leaked;
    }

    [Theory]
    [InlineData(InvalidHandle, false)]
    [InlineData(InvalidHandle, true)]
    [InlineData(DiskFull, true)]
    public async Task DalamudsRealStuckFailure_WhileTheTemporaryFileIsHeldOpen_WritesTheFileDirectly(int nativeError, bool faultAsync)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "Profiles", "plate.json");
        var stuck = StuckTempFallbackFileStore.TemporaryPathFor(path);
        using var leaked = HoldLikeTheLeakedHandle(stuck);

        var inner = new FaultInjectingStore { FailWriteAfter = 0, FaultAsync = faultAsync, FaultFactory = _ => new Win32Exception(nativeError) };
        var log = new TestLog();
        var store = new StuckTempFallbackFileStore(inner, log);

        await store.WriteTextAsync(path, Contents);

        Assert.Equal(Contents, File.ReadAllText(path, Encoding.UTF8));
        Assert.Equal(Encoding.UTF8.GetBytes(Contents), File.ReadAllBytes(path));

        // No temporary file of the fallback's own is left behind.
        Assert.Equal(new[] { "plate.json", "plate.json.tmp" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).Order());

        // The stuck temporary file belongs to the storage that still holds it open: left alone.
        leaked.Dispose();
        Assert.Equal("half-written", File.ReadAllText(stuck));

        var warning = Assert.Single(log.Messages);
        Assert.StartsWith("W ", warning);
        Assert.Contains("plate.json", warning);
        Assert.DoesNotContain(directory.Path, warning);
        Assert.DoesNotContain("Profiles", warning);
    }

    [Fact]
    public async Task AnIOExceptionWhileTheTemporaryFileIsHeldOpen_FallsBackTheSameWay()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "plate.json");
        using var leaked = HoldLikeTheLeakedHandle(StuckTempFallbackFileStore.TemporaryPathFor(path));

        var inner = new FaultInjectingStore
        {
            FailWriteAfter = 0,
            FaultFactory = p => new IOException($"The process cannot access the file '{p}'") { HResult = unchecked((int)0x80070020) },
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
        using var leaked = HoldLikeTheLeakedHandle(StuckTempFallbackFileStore.TemporaryPathFor(path));
        using var otherLeaked = HoldLikeTheLeakedHandle(StuckTempFallbackFileStore.TemporaryPathFor(other));

        var inner = new FaultInjectingStore { FailWriteAfter = 0, FaultFactory = _ => new Win32Exception(InvalidHandle) };
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheSameFailure_WithTheTemporaryFileNotHeldOpen_IsNotTheLeak_AndPropagates(bool leftoverOnDisk)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "plate.json");
        if (leftoverOnDisk)
        {
            // A crash's leftover: on disk, but nobody holds it open, so the next write can replace it.
            File.WriteAllText(StuckTempFallbackFileStore.TemporaryPathFor(path), "half-written");
        }

        var inner = new FaultInjectingStore { FailWriteAfter = 0, FaultFactory = _ => new Win32Exception(InvalidHandle) };
        var log = new TestLog();
        var store = new StuckTempFallbackFileStore(inner, log);

        var failure = await Assert.ThrowsAsync<Win32Exception>(() => store.WriteTextAsync(path, Contents));

        Assert.Equal(InvalidHandle, failure.NativeErrorCode);
        Assert.False(File.Exists(path));
        Assert.Empty(log.Messages);
    }

    public static TheoryData<Exception> OtherKindsOfFailure => new()
    {
        new UnauthorizedAccessException("denied"),
        new OperationAbandonedException(),
        new ObjectDisposedException("ReliableFileStorage"),
        new OperationCanceledException(),
        new Exception("Could not write all bytes to temp file (3 of 12)"),
    };

    [Theory]
    [MemberData(nameof(OtherKindsOfFailure))]
    public async Task AnyOtherKindOfFailure_PropagatesUnchanged_EvenWhileTheTemporaryFileIsHeldOpen(Exception failure)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "plate.json");
        using var leaked = HoldLikeTheLeakedHandle(StuckTempFallbackFileStore.TemporaryPathFor(path));

        var inner = new FaultInjectingStore { FailWriteAfter = 0, FaultAsync = true, FaultFactory = _ => failure };
        var log = new TestLog();
        var store = new StuckTempFallbackFileStore(inner, log);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => store.WriteTextAsync(path, Contents));

        Assert.Same(failure, thrown);
        Assert.False(File.Exists(path));
        Assert.Empty(log.Messages);
    }

    [Fact]
    public void IsHeldOpenWithoutSharing_OnlyWhileAHandleDeniesAllSharing()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "plate.json.tmp");

        Assert.False(StuckTempFallbackFileStore.IsHeldOpenWithoutSharing(path));
        Assert.False(StuckTempFallbackFileStore.IsHeldOpenWithoutSharing(Path.Combine(directory.Path, "missing", "plate.json.tmp")));

        using (HoldLikeTheLeakedHandle(path))
        {
            Assert.True(StuckTempFallbackFileStore.IsHeldOpenWithoutSharing(path));
        }

        // Released (or never held: a leftover from a crash) — the next reliable write can replace it.
        Assert.False(StuckTempFallbackFileStore.IsHeldOpenWithoutSharing(path));

        // A handle that shares, such as a scanner reading the file, is not the leak.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.False(StuckTempFallbackFileStore.IsHeldOpenWithoutSharing(path));
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AnAbandonedOperation_NeverReachesTheFallback()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "plate.json");
        using var leaked = HoldLikeTheLeakedHandle(StuckTempFallbackFileStore.TemporaryPathFor(path));

        // The plugin's composition: abandonment is checked outside the fallback.
        var inner = new FaultInjectingStore { FailWriteAfter = 0, FaultFactory = _ => new Win32Exception(InvalidHandle) };
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
        await store.ReadTextAsync(path, text => read = text.Text);
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

        // Dalamud's first, failed write left "{path}.tmp" open; every later write of it fails with
        // an invalid handle, from a faulted thread-pool task.
        var platePath = fixture.Paths.GetPlatePath(created.PlateId);
        using var leaked = HoldLikeTheLeakedHandle(StuckTempFallbackFileStore.TemporaryPathFor(platePath));
        faulting.FailWriteAfter = 0;
        faulting.FaultAsync = true;
        faulting.FaultFactory = _ => new Win32Exception(InvalidHandle);

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

        // Disk full with no handle leaked, reported as Dalamud does: a Win32Exception from a
        // faulted thread-pool task.
        faulting.FailWriteAfter = 0;
        faulting.FaultAsync = true;
        faulting.FaultFactory = _ => new Win32Exception(DiskFull);

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
