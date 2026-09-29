using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Plate Library's threading contract against a dispatcher that behaves like the game's
/// framework thread — it starts each operation and moves on, never waiting for it: loading reads
/// and parses nothing on that thread, and gives it back before the first file is even opened.
/// </summary>
public class PlateLibraryThreadingTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InitializeAsync_ReadsAndParsesOffTheDispatcherThread()
    {
        using var dispatcher = new QueuedDispatcher();
        using var fixture = new LibraryFixture();
        var (seeded, _) = await SeedAsync(fixture);
        var store = new ThreadRecordingStore(fixture.Store);
        var library = new PlateLibraryService(fixture.Paths, store, fixture.Log, () => fixture.Clock.Now, dispatcher.Dispatch);

        await library.InitializeAsync().WaitAsync(Generous);

        Assert.True(library.IsLoaded);
        var reads = store.Calls.Where(c => c.Operation is nameof(IPlateFileStore.ReadTextAsync) or nameof(IPlateFileStore.ListFiles)).ToList();
        Assert.True(reads.Count >= 5, "expected the Plates, bindings and index to be read");
        Assert.All(store.Calls, call => Assert.NotEqual(dispatcher.ThreadId, call.ThreadId));
        Assert.Equal(seeded.GetOrderedPlates().Select(p => p.PlateId), library.GetOrderedPlates().Select(p => p.PlateId));
    }

    [Fact]
    public async Task InitializeAsync_DispatchedDelegateYieldsBeforeTheFirstRead()
    {
        using var dispatcher = new QueuedDispatcher();
        using var fixture = new LibraryFixture();
        var (seeded, bound) = await SeedAsync(fixture);
        var store = new ReadGatedStore(fixture.Store);
        Task? started = null;
        var library = new PlateLibraryService(fixture.Paths, store, fixture.Log, () => fixture.Clock.Now, work => dispatcher.Dispatch(() => started = work()));

        store.Hold();
        var loading = library.InitializeAsync();
        await store.ReadStarted.WaitAsync(Generous);

        // The first read is held, yet the dispatcher already returned from the operation's delegate
        // (the next queued item runs to completion) and that delegate's Task is still pending.
        await dispatcher.Dispatch(() => Task.CompletedTask).WaitAsync(Generous);
        Assert.NotNull(started);
        Assert.False(started!.IsCompleted);
        Assert.False(loading.IsCompleted);
        Assert.NotEqual(dispatcher.ThreadId, store.ReadThreadId);
        Assert.False(library.IsLoaded);

        store.Release();
        await loading.WaitAsync(Generous);

        // And the Library loaded exactly as it does inline.
        Assert.True(library.IsLoaded);
        Assert.Equal(seeded.GetOrderedPlates().Select(p => p.PlateId), library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal(seeded.GetActivePlateId(bound.ContentId), library.GetActivePlateId(bound.ContentId));
        Assert.Equal(seeded.GetBinding(bound.ContentId)!.PlateIds, library.GetBinding(bound.ContentId)!.PlateIds);
        Assert.Equal(seeded.Generation, library.Generation);
    }

    [Fact]
    public async Task FirstRunMigration_ThroughAQueuedDispatcher_StillWritesBindingsBeforeTheIndex()
    {
        using var dispatcher = new QueuedDispatcher();
        using var fixture = new LibraryFixture();
        var profileId = Guid.NewGuid();
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, 4242));
        fixture.WriteBindingJson(4242, LegacyData.VersionOneBinding(4242, profileId, profileId));
        var store = new ThreadRecordingStore(fixture.Store);
        var library = new PlateLibraryService(fixture.Paths, store, fixture.Log, () => fixture.Clock.Now, dispatcher.Dispatch);

        await library.InitializeAsync().WaitAsync(Generous);

        var writes = store.Calls.Where(c => c.Operation == nameof(IPlateFileStore.WriteTextAsync)).Select(c => c.Path).ToList();
        Assert.Equal([fixture.Paths.GetBindingPath(4242), fixture.Paths.LibraryFile], writes);
        Assert.All(store.Calls, call => Assert.NotEqual(dispatcher.ThreadId, call.ThreadId));
        Assert.Equal(profileId, library.GetActivePlateId(4242));
        Assert.Equal([profileId], fixture.ReadLibraryOrder());
    }

    [Fact]
    public async Task CanceledLoad_ThroughAQueuedDispatcher_WritesNothing()
    {
        using var dispatcher = new QueuedDispatcher();
        using var fixture = new LibraryFixture();
        var profileId = Guid.NewGuid();
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, 4242));
        var store = new ReadGatedStore(fixture.Store);
        var library = new PlateLibraryService(fixture.Paths, store, fixture.Log, () => fixture.Clock.Now, dispatcher.Dispatch);
        using var cancellation = new CancellationTokenSource();

        store.Hold();
        var loading = library.InitializeAsync(cancellation.Token);
        await store.ReadStarted.WaitAsync(Generous);
        cancellation.Cancel();
        store.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(Generous));
        Assert.False(library.IsLoaded);
        Assert.False(System.IO.File.Exists(fixture.Paths.LibraryFile));
        Assert.False(System.IO.File.Exists(fixture.Paths.GetBindingPath(4242)));
    }

    /// <summary>A few Plates, one bound character and a saved order, loaded once inline for comparison.</summary>
    private static async Task<(PlateLibraryService Seeded, CharacterContext Bound)> SeedAsync(LibraryFixture fixture)
    {
        var library = await fixture.LoadAsync();
        for (var i = 0; i < 3; i++)
        {
            fixture.Clock.Tick();
            await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, $"Plate {i}");
        }

        var unbound = Guid.NewGuid();
        fixture.WritePlateJson(unbound, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, unbound, "Hand-copied", fixture.Clock.Now), JsonOptions.Default));
        return (await fixture.LoadAsync(), Characters.Alice);
    }

    /// <summary>Plain files whose next read can be held after it has begun, recording the thread it began on.</summary>
    private sealed class ReadGatedStore : IPlateFileStore
    {
        private readonly IPlateFileStore inner;
        private TaskCompletionSource? gate;
        private TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ReadGatedStore(IPlateFileStore inner)
        {
            this.inner = inner;
        }

        internal Task ReadStarted => readStarted.Task;

        /// <summary>The managed id of the thread that made the held read, or -1.</summary>
        internal int ReadThreadId { get; private set; } = -1;

        internal void Hold()
        {
            readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void Release() => gate?.TrySetResult();

        public bool FileExists(string path) => inner.FileExists(path);

        public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => inner.ListFiles(directory, searchPattern);

        public async Task ReadTextAsync(string path, Action<StoredText> reader)
        {
            if (gate is { } held)
            {
                ReadThreadId = Environment.CurrentManagedThreadId;
                readStarted.TrySetResult();
                await held.Task.ConfigureAwait(false);
                gate = null;
            }

            await inner.ReadTextAsync(path, reader).ConfigureAwait(false);
        }

        public Task WriteTextAsync(string path, string contents) => inner.WriteTextAsync(path, contents);

        public void MoveFile(string sourcePath, string destinationPath) => inner.MoveFile(sourcePath, destinationPath);

        public void CopyFile(string sourcePath, string destinationPath) => inner.CopyFile(sourcePath, destinationPath);

        public void DeleteFile(string path) => inner.DeleteFile(path);
    }
}
