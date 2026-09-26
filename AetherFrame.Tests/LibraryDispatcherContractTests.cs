using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Libraries' threading contract against a dispatcher that behaves like the game's framework
/// thread (<see cref="QueuedDispatcher"/>: items are started on one thread and never waited for
/// there) instead of the inline dispatcher every other test uses. Each operation's synchronous
/// prefix runs on the dispatcher thread; once a file step is held (an incomplete store Task), that
/// thread is free for the next item, and the rest of the operation — further file steps and the
/// PlateSaved/PlateRenamed/PlateDeleted events — runs on whichever thread completes the step, so
/// subscribers must be thread-safe and nothing may wait on the dispatcher. These hold both before
/// and after the load's read phase moves off the dispatcher thread (they only assert that the
/// dispatcher thread is never blocked while a file step is held, and that operations complete).
/// </summary>
public class LibraryDispatcherContractTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Instantiate_CompletesThroughAQueuedDispatcher()
    {
        using var dispatcher = new QueuedDispatcher();
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture();
        var plates = new PlateLibraryService(fixture.Paths, fixture.Store, fixture.Log, () => fixture.Clock.Now, dispatcher.Dispatch, operations);
        var templates = new TemplateLibraryService(fixture.Paths, fixture.Store, plates, fixture.Log, () => fixture.Clock.Now, dispatcher.Dispatch, operations);
        await plates.InitializeAsync().WaitAsync(Generous);
        await templates.InitializeAsync().WaitAsync(Generous);
        var source = await plates.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source").WaitAsync(Generous);
        var templateId = await templates.SaveAsTemplateAsync(source.PlateId, "Reusable").WaitAsync(Generous);

        // Use Template dispatches the Plate Library's operation from inside its own dispatched one.
        var fromBuiltIn = await templates.InstantiateAsync(BuiltInTemplateCatalog.AdventurePlateClassicId, Characters.Alice, new PlateStarterContent(null)).WaitAsync(Generous);
        var fromUser = await templates.InstantiateAsync(templateId, Characters.Alice).WaitAsync(Generous);

        Assert.True(fromBuiltIn.BecameActive);
        Assert.False(fromUser.BecameActive);
        Assert.Equal(PlateStatus.Ready, plates.FindPlate(fromBuiltIn.PlateId)!.Status);
        Assert.Equal(PlateStatus.Ready, plates.FindPlate(fromUser.PlateId)!.Status);
        Assert.Equal(0, operations.RunningCount);

        // Both Libraries' locks are released afterwards: the next operation of each runs at once.
        await templates.RenameTemplateAsync(templateId, "Still usable").WaitAsync(Generous);
        await plates.RenamePlateAsync(fromUser.PlateId, "Still usable too").WaitAsync(Generous);
        Assert.Equal("Still usable", templates.FindTemplate(templateId)!.DisplayName);
        Assert.Equal("Still usable too", plates.FindPlate(fromUser.PlateId)!.DisplayName);
        Assert.DoesNotContain(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateSaveRenameAndDelete_CompleteThroughAQueuedDispatcher()
    {
        using var dispatcher = new QueuedDispatcher();
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture();
        var plates = new PlateLibraryService(fixture.Paths, fixture.Store, fixture.Log, () => fixture.Clock.Now, dispatcher.Dispatch, operations);
        await plates.InitializeAsync().WaitAsync(Generous);

        var created = await plates.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice).WaitAsync(Generous);
        var document = plates.OpenDocumentForEditing(created.PlateId);
        document.Revision = 7;
        await plates.SavePlateDocumentAsync(document).WaitAsync(Generous);
        await plates.RenamePlateAsync(created.PlateId, "Renamed").WaitAsync(Generous);
        var other = await plates.CreatePlateAsync(PlateStartingLayout.Blank, null).WaitAsync(Generous);
        await plates.SetActivePlateAsync(Characters.Alice, other.PlateId).WaitAsync(Generous);
        await plates.DeletePlateAsync(created.PlateId).WaitAsync(Generous);

        Assert.Null(plates.FindPlate(created.PlateId));
        Assert.Equal(other.PlateId, plates.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Single(Directory.GetFiles(fixture.Paths.PlateTrashDirectory));
        Assert.Contains("\"Renamed\"", File.ReadAllText(Directory.GetFiles(fixture.Paths.PlateTrashDirectory)[0]));
        Assert.Equal([other.PlateId], fixture.ReadLibraryOrder());
        Assert.Equal(0, operations.RunningCount);

        var reloaded = await fixture.LoadAsync();
        Assert.Single(reloaded.GetOrderedPlates());
    }

    [Fact]
    public async Task SaveContinuations_AndEvents_RunOffTheDispatcherThread()
    {
        using var dispatcher = new QueuedDispatcher();
        var holding = new HoldingStore();
        var recording = new ThreadRecordingStore(holding);
        using var fixture = new LibraryFixture(recording);
        var plates = new PlateLibraryService(fixture.Paths, fixture.Store, fixture.Log, () => fixture.Clock.Now, dispatcher.Dispatch);
        await plates.InitializeAsync().WaitAsync(Generous);
        var savedOn = new List<int>();
        var renamedOn = new List<int>();
        var deletedOn = new List<int>();
        plates.PlateSaved += _ => savedOn.Add(Environment.CurrentManagedThreadId);
        plates.PlateRenamed += (_, _) => renamedOn.Add(Environment.CurrentManagedThreadId);
        plates.PlateDeleted += _ => deletedOn.Add(Environment.CurrentManagedThreadId);

        // Create: the Plate write is the synchronous prefix; the binding and index writes follow it.
        var callsBeforeCreate = recording.Calls.Count;
        var hold = holding.HoldNextWrite();
        var create = plates.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await hold.Started.WaitAsync(Generous);
        await AssertDispatcherIsFreeAsync(dispatcher);
        Assert.False(create.IsCompleted);
        hold.Release();
        var created = await create.WaitAsync(Generous);
        var createWrites = WritesSince(recording, callsBeforeCreate);
        Assert.Equal(3, createWrites.Count);
        Assert.Equal(dispatcher.ThreadId, createWrites[0].ThreadId);
        Assert.All(createWrites.Skip(1), write => Assert.NotEqual(dispatcher.ThreadId, write.ThreadId));

        // Save: the document write is the prefix; PlateSaved is raised by its continuation.
        var callsBeforeSave = recording.Calls.Count;
        var document = plates.OpenDocumentForEditing(created.PlateId);
        document.Revision = 3;
        hold = holding.HoldNextWrite();
        var save = plates.SavePlateDocumentAsync(document);
        await hold.Started.WaitAsync(Generous);
        await AssertDispatcherIsFreeAsync(dispatcher);
        Assert.False(save.IsCompleted);
        Assert.Empty(savedOn);
        hold.Release();
        await save.WaitAsync(Generous);
        var saveWrite = Assert.Single(WritesSince(recording, callsBeforeSave));
        Assert.Equal(dispatcher.ThreadId, saveWrite.ThreadId);
        Assert.NotEqual(dispatcher.ThreadId, Assert.Single(savedOn));

        // Rename: same shape.
        hold = holding.HoldNextWrite();
        var rename = plates.RenamePlateAsync(created.PlateId, "Renamed");
        await hold.Started.WaitAsync(Generous);
        await AssertDispatcherIsFreeAsync(dispatcher);
        hold.Release();
        await rename.WaitAsync(Generous);
        Assert.NotEqual(dispatcher.ThreadId, Assert.Single(renamedOn));

        // Delete: the move to the trash is synchronous, so the binding write is still the prefix;
        // the index write and PlateDeleted follow on the thread that completed it.
        var callsBeforeDelete = recording.Calls.Count;
        hold = holding.HoldNextWrite();
        var delete = plates.DeletePlateAsync(created.PlateId);
        await hold.Started.WaitAsync(Generous);
        await AssertDispatcherIsFreeAsync(dispatcher);
        Assert.Empty(deletedOn);
        hold.Release();
        await delete.WaitAsync(Generous);
        var deleteCalls = recording.Calls.Skip(callsBeforeDelete).ToList();
        Assert.Equal(dispatcher.ThreadId, deleteCalls.First(c => c.Operation == nameof(IPlateFileStore.MoveFile)).ThreadId);
        var deleteWrites = deleteCalls.Where(c => c.Operation == nameof(IPlateFileStore.WriteTextAsync)).ToList();
        Assert.Equal(2, deleteWrites.Count);
        Assert.Equal(dispatcher.ThreadId, deleteWrites[0].ThreadId);
        Assert.NotEqual(dispatcher.ThreadId, deleteWrites[1].ThreadId);
        Assert.NotEqual(dispatcher.ThreadId, Assert.Single(deletedOn));
    }

    [Fact]
    public async Task RunExclusive_NeverBlocksTheDispatcherThread_WhileALoadReadIsHeld()
    {
        using var dispatcher = new QueuedDispatcher();
        var operations = new OwnedOperations();
        var holding = new HoldingStore();
        using var fixture = new LibraryFixture(holding);
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, System.Text.Json.JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Existing", fixture.Clock.Now), JsonOptions.Default));
        var plates = new PlateLibraryService(fixture.Paths, fixture.Store, fixture.Log, () => fixture.Clock.Now, dispatcher.Dispatch, operations);

        var hold = holding.HoldNextRead();
        var load = plates.InitializeAsync();
        await hold.Started.WaitAsync(Generous);

        // The read is held mid-flight, yet the dispatcher thread already moved on to the next item.
        await AssertDispatcherIsFreeAsync(dispatcher);
        Assert.False(load.IsCompleted);
        Assert.False(plates.IsLoaded);
        Assert.Equal(1, operations.RunningCount);

        hold.Release();
        await load.WaitAsync(Generous);
        Assert.True(plates.IsLoaded);
        Assert.Equal(PlateStatus.Ready, plates.FindPlate(plateId)!.Status);
        Assert.Equal(0, operations.RunningCount);
        Assert.DoesNotContain(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) || m.StartsWith("E ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunExclusive_NeverBlocksTheDispatcherThread_WhileASaveWriteIsHeld()
    {
        using var dispatcher = new QueuedDispatcher();
        var operations = new OwnedOperations();
        var holding = new HoldingStore();
        using var fixture = new LibraryFixture(holding);
        var plates = new PlateLibraryService(fixture.Paths, fixture.Store, fixture.Log, () => fixture.Clock.Now, dispatcher.Dispatch, operations);
        await plates.InitializeAsync().WaitAsync(Generous);
        var created = await plates.CreatePlateAsync(PlateStartingLayout.Blank, null).WaitAsync(Generous);
        var document = plates.OpenDocumentForEditing(created.PlateId);
        document.Revision = 5;

        var hold = holding.HoldNextWrite();
        var save = plates.SavePlateDocumentAsync(document);
        await hold.Started.WaitAsync(Generous);

        // A second Library operation queues behind the running one (never behind the dispatcher);
        // unrelated dispatched work still runs meanwhile.
        var rename = plates.RenamePlateAsync(created.PlateId, "Renamed while saving");
        await AssertDispatcherIsFreeAsync(dispatcher);
        Assert.False(save.IsCompleted);
        Assert.False(rename.IsCompleted);
        Assert.Equal(1, operations.RunningCount);

        hold.Release();
        await save.WaitAsync(Generous);
        await rename.WaitAsync(Generous);
        Assert.Equal(5, plates.GetSavedDocument(created.PlateId)!.Revision);
        Assert.Equal("Renamed while saving", plates.FindPlate(created.PlateId)!.DisplayName);
        Assert.Equal(0, operations.RunningCount);
    }

    /// <summary>Passes only if the dispatcher thread is not waiting on anything: a fresh item runs to completion.</summary>
    private static async Task AssertDispatcherIsFreeAsync(QueuedDispatcher dispatcher)
    {
        var ranOn = -1;
        await dispatcher.Dispatch(() =>
        {
            ranOn = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        }).WaitAsync(Generous);
        Assert.Equal(dispatcher.ThreadId, ranOn);
    }

    private static List<(string Operation, string Path, int ThreadId)> WritesSince(ThreadRecordingStore store, int callIndex) =>
        store.Calls.Skip(callIndex).Where(c => c.Operation == nameof(IPlateFileStore.WriteTextAsync)).ToList();

    /// <summary>A file step held open: <see cref="Started"/> completes once the store call has begun
    /// (and its Task is incomplete); <see cref="Release"/> lets it finish, on a thread-pool thread.</summary>
    private sealed class Hold
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => started.Task;

        internal Task Released => released.Task;

        internal void MarkStarted() => started.TrySetResult();

        internal void Release() => released.TrySetResult();
    }

    /// <summary>Plain files whose next read or next write can be held mid-flight (after the call
    /// began, before anything reaches disk or the reader) and then released.</summary>
    private sealed class HoldingStore : IPlateFileStore
    {
        private readonly SystemFileStore files = new();
        private Hold? nextRead;
        private Hold? nextWrite;

        internal Hold HoldNextRead() => nextRead = new Hold();

        internal Hold HoldNextWrite() => nextWrite = new Hold();

        public bool FileExists(string path) => files.FileExists(path);

        public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => files.ListFiles(directory, searchPattern);

        public async Task ReadTextAsync(string path, Action<string> reader)
        {
            if (Interlocked.Exchange(ref nextRead, null) is { } hold)
            {
                hold.MarkStarted();
                await hold.Released.ConfigureAwait(false);
            }

            await files.ReadTextAsync(path, reader).ConfigureAwait(false);
        }

        public async Task WriteTextAsync(string path, string contents)
        {
            if (Interlocked.Exchange(ref nextWrite, null) is { } hold)
            {
                hold.MarkStarted();
                await hold.Released.ConfigureAwait(false);
            }

            await files.WriteTextAsync(path, contents).ConfigureAwait(false);
        }

        public void MoveFile(string sourcePath, string destinationPath) => files.MoveFile(sourcePath, destinationPath);

        public void CopyFile(string sourcePath, string destinationPath) => files.CopyFile(sourcePath, destinationPath);

        public void DeleteFile(string path) => files.DeleteFile(path);
    }
}
