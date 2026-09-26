using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Persistence;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Where package work runs, and how it ends at unload. Import's commit — copying, hashing and
/// fsyncing every image — must never run on the thread that started it (the Import Preview
/// starts it from a draw), and Inspect, which extracts into staging, is an owned operation like
/// Export and Import: refused once unloading has begun, drained when already running.
/// </summary>
public class PlatePackageServiceThreadingTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A decoder query that, once armed, records the thread it is asked on and holds that thread
    /// until released — unless it finds itself on the test's own thread, where holding would be
    /// waiting for itself; it then returns at once and the thread assertion fails instead.
    /// </summary>
    private sealed class DecoderGate
    {
        private readonly ManualResetEventSlim reached = new(false);
        private readonly ManualResetEventSlim release = new(false);
        private volatile bool armed;
        private int callerThread = -1;

        internal int ObservedThread { get; private set; } = -1;

        internal Func<string, bool> Query => _ =>
        {
            if (armed)
            {
                if (ObservedThread < 0)
                {
                    ObservedThread = Environment.CurrentManagedThreadId;
                    reached.Set();
                }

                if (Environment.CurrentManagedThreadId != callerThread)
                {
                    release.Wait(Generous);
                }
            }

            return true;
        };

        internal void Arm()
        {
            callerThread = Environment.CurrentManagedThreadId;
            armed = true;
        }

        /// <summary>Blocks the calling thread until the first armed query, keeping that thread busy meanwhile.</summary>
        internal void WaitUntilReached() => Assert.True(reached.Wait(Generous), "the decoder query was never asked");

        internal void Release() => release.Set();
    }

    [Fact]
    public async Task ImportAsync_CopiesAssetsOffTheCallingThread()
    {
        var gate = new DecoderGate();
        using var fixture = new PackageFixture(decoder: gate.Query);
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var path = fixture.Export(packages, plateId);
        using var staged = packages.Inspect(path);
        var assetFilesBefore = Directory.GetFiles(fixture.Paths.AssetsDirectory).Length;

        // Every image is queried while it is committed; the first query holds the commit.
        gate.Arm();
        var callingThread = Environment.CurrentManagedThreadId;
        var import = packages.ImportAsync(staged);
        gate.WaitUntilReached();

        Assert.NotEqual(callingThread, gate.ObservedThread);
        Assert.False(import.IsCompleted);
        Assert.Equal(assetFilesBefore, Directory.GetFiles(fixture.Paths.AssetsDirectory).Length);

        gate.Release();
        var result = await import.WaitAsync(Generous);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(assetFilesBefore + 3, Directory.GetFiles(fixture.Paths.AssetsDirectory).Length);
    }

    [Fact]
    public async Task ImportAsync_RefusalAtShutdown_IsImmediateOnTheCaller()
    {
        var operations = new OwnedOperations();
        using var fixture = new PackageFixture();
        var (library, packages) = await LoadAsync(fixture, fixture.Library.Store, operations);
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        using var staged = packages.Inspect(fixture.Export(packages, plateId));

        await operations.ShutdownAsync(Generous);
        var import = packages.ImportAsync(staged);

        Assert.True(import.IsCompleted);
        var result = await import;
        Assert.False(result.Succeeded);
        Assert.Equal(PackageErrorCode.CommitFailed, result.Error!.Code);
        Assert.Single(library.GetOrderedPlates());
    }

    [Fact]
    public async Task RunningImport_IsDrained_BeforeDisposal()
    {
        var store = new HeldWriteStore();
        var operations = new OwnedOperations();
        using var fixture = new PackageFixture(store: store);
        var (library, packages) = await LoadAsync(fixture, store, operations);
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        using var staged = packages.Inspect(fixture.Export(packages, plateId));

        store.Hold();
        var import = packages.ImportAsync(staged);
        await store.WriteStarted.WaitAsync(Generous);

        var shutdown = operations.ShutdownAsync(Generous);
        await Task.Delay(50);
        Assert.False(shutdown.IsCompleted);
        Assert.True(operations.RunningCount >= 1);

        store.Release();
        var result = await import.WaitAsync(Generous);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.True(await shutdown);
        Assert.Equal(0, operations.RunningCount);
        Assert.Equal(2, library.GetOrderedPlates().Count);
    }

    [Fact]
    public async Task OnceShutdownBegins_InspectIsRefused_AndNothingIsStaged()
    {
        var operations = new OwnedOperations();
        using var fixture = new PackageFixture();
        var (library, packages) = await LoadAsync(fixture, fixture.Library.Store, operations);
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var path = fixture.Export(packages, plateId);
        Assert.True(fixture.StagingIsEmpty);

        await operations.ShutdownAsync(Generous);

        using var refused = packages.Inspect(path);
        Assert.Equal(PackageCompatibility.Invalid, refused.Compatibility);
        Assert.False(refused.CanImport);
        var error = Assert.Single(refused.Diagnostics.Errors);
        Assert.Equal(PackageErrorCode.CommitFailed, error.Code);
        Assert.Contains("closing", error.Message);
        Assert.Equal(Path.GetFileName(path), refused.SourceFileName);
        Assert.False(Directory.Exists(refused.StagingDirectory));
        Assert.True(fixture.StagingIsEmpty);
        Assert.Equal(0, operations.RunningCount);
        Assert.False((await packages.ImportAsync(refused)).Succeeded);
    }

    [Fact]
    public async Task RunningInspect_IsDrained()
    {
        var gate = new DecoderGate();
        var operations = new OwnedOperations();
        using var fixture = new PackageFixture(decoder: gate.Query);
        var (library, packages) = await LoadAsync(fixture, fixture.Library.Store, operations);
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var path = fixture.Export(packages, plateId);

        // The first image's check holds the Inspect mid-extraction.
        gate.Arm();
        var inspecting = Task.Run(() => packages.Inspect(path));
        gate.WaitUntilReached();

        var shutdown = operations.ShutdownAsync(Generous);
        await Task.Delay(50);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(1, operations.RunningCount);

        gate.Release();
        using var staged = await inspecting.WaitAsync(Generous);
        Assert.True(staged.CanImport, staged.DescribeForLog());
        Assert.True(await shutdown);
        Assert.Equal(0, operations.RunningCount);
    }

    private static async Task<(PlateLibraryService Library, PlatePackageService Packages)> LoadAsync(PackageFixture fixture, IPlateFileStore store, OwnedOperations operations)
    {
        var library = new PlateLibraryService(fixture.Paths, store, fixture.Log, () => fixture.Clock.Now, operations: operations);
        await library.InitializeAsync();
        var packages = new PlatePackageService(library, fixture.Assets, fixture.Paths, "AetherFrame Tests", fixture.Decoder, fixture.Log, () => fixture.Clock.Now, operations);
        return (library, packages);
    }

    /// <summary>Plain files whose next write can be held after it has begun and before anything reaches disk.</summary>
    private sealed class HeldWriteStore : IPlateFileStore
    {
        private readonly SystemFileStore files = new();
        private TaskCompletionSource? gate;
        private TaskCompletionSource writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WriteStarted => writeStarted.Task;

        internal void Hold()
        {
            writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void Release() => gate?.TrySetResult();

        public bool FileExists(string path) => files.FileExists(path);

        public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => files.ListFiles(directory, searchPattern);

        public Task ReadTextAsync(string path, Action<string> reader) => files.ReadTextAsync(path, reader);

        public async Task WriteTextAsync(string path, string contents)
        {
            if (gate is { } held)
            {
                writeStarted.TrySetResult();
                await held.Task.ConfigureAwait(false);
                gate = null;
            }

            await files.WriteTextAsync(path, contents).ConfigureAwait(false);
        }

        public void MoveFile(string sourcePath, string destinationPath) => files.MoveFile(sourcePath, destinationPath);

        public void CopyFile(string sourcePath, string destinationPath) => files.CopyFile(sourcePath, destinationPath);

        public void DeleteFile(string path) => files.DeleteFile(path);
    }
}
