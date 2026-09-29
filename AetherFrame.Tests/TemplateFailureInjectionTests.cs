using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Every Template operation is a single file step followed by the in-memory change, so a
/// failed step must leave the Library — memory and disk — exactly as it was.</summary>
public class TemplateFailureInjectionTests
{
    private static Func<string, bool> InFolder(string folder) =>
        path => string.Equals(Path.GetDirectoryName(path), folder, StringComparison.Ordinal);

    private static IReadOnlyList<string> FilesIn(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToList() : [];

    private static async Task<(TemplateLibraryFixture Fixture, TemplateLibraryService Templates, Guid TemplateId)> SeedAsync(FaultInjectingStore store)
    {
        var fixture = new TemplateLibraryFixture(store);
        var templates = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await templates.SaveAsTemplateAsync(plate.PlateId, "Original");
        return (fixture, templates, templateId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsTemplate_WriteFailure_AddsNothing_AndWritesNothing(bool faultAsync)
    {
        var store = new FaultInjectingStore { FaultAsync = faultAsync };
        using var fixture = new TemplateLibraryFixture(store);
        var templates = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var listedBefore = templates.GetOrderedTemplates().Select(t => t.TemplateId).ToList();
        var generationBefore = templates.Generation;
        store.FailWrite = InFolder(fixture.Paths.TemplatesDirectory);

        await Assert.ThrowsAsync<IOException>(() => templates.SaveAsTemplateAsync(plate.PlateId, "Doomed"));

        Assert.Equal(1, store.FailedOperations);
        Assert.Equal(listedBefore, templates.GetOrderedTemplates().Select(t => t.TemplateId));
        Assert.Equal(generationBefore, templates.Generation);
        Assert.Empty(FilesIn(fixture.Paths.TemplatesDirectory));

        store.FailWrite = null;
        var saved = await templates.SaveAsTemplateAsync(plate.PlateId, "Doomed");
        Assert.True(templates.FindTemplate(saved)!.IsReady);
        Assert.Single(FilesIn(fixture.Paths.TemplatesDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameTemplate_WriteFailure_KeepsTheOldName_AndTheFile(bool faultAsync)
    {
        var store = new FaultInjectingStore { FaultAsync = faultAsync };
        var (fixture, templates, templateId) = await SeedAsync(store);
        using var _ = fixture;
        var fileBefore = fixture.ReadTemplateJson(templateId);
        var summaryBefore = templates.FindTemplate(templateId)!;
        var generationBefore = templates.Generation;
        fixture.Clock.Tick();
        store.FailWrite = InFolder(fixture.Paths.TemplatesDirectory);

        await Assert.ThrowsAsync<IOException>(() => templates.RenameTemplateAsync(templateId, "Renamed"));

        Assert.Equal(1, store.FailedOperations);
        Assert.Equal(summaryBefore, templates.FindTemplate(templateId));
        Assert.Equal(generationBefore, templates.Generation);
        Assert.Equal(fileBefore, fixture.ReadTemplateJson(templateId));

        store.FailWrite = null;
        await templates.RenameTemplateAsync(templateId, "Renamed");
        Assert.Equal("Renamed", templates.FindTemplate(templateId)!.DisplayName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateTemplate_WriteFailure_AddsNothing(bool faultAsync)
    {
        var store = new FaultInjectingStore { FaultAsync = faultAsync };
        var (fixture, templates, templateId) = await SeedAsync(store);
        using var _ = fixture;
        var fileBefore = fixture.ReadTemplateJson(templateId);
        var generationBefore = templates.Generation;
        store.FailWrite = InFolder(fixture.Paths.TemplatesDirectory);

        await Assert.ThrowsAsync<IOException>(() => templates.DuplicateTemplateAsync(templateId));

        Assert.Equal(1, store.FailedOperations);
        Assert.Equal(templateId, Assert.Single(templates.GetOrderedTemplates(), t => t.Kind == TemplateKind.UserSaved).TemplateId);
        Assert.Equal(generationBefore, templates.Generation);
        Assert.Equal(fixture.Paths.GetTemplatePath(templateId), Assert.Single(FilesIn(fixture.Paths.TemplatesDirectory)));
        Assert.Equal(fileBefore, fixture.ReadTemplateJson(templateId));

        store.FailWrite = null;
        var copy = await templates.DuplicateTemplateAsync(templateId);
        Assert.True(templates.FindTemplate(copy)!.IsReady);
        Assert.Equal(fileBefore, fixture.ReadTemplateJson(templateId));
    }

    [Fact]
    public async Task DeleteTemplate_MoveFailure_KeepsTheTemplateListed_AndOutOfTheTrash()
    {
        var store = new FaultInjectingStore();
        var (fixture, templates, templateId) = await SeedAsync(store);
        using var _ = fixture;
        var fileBefore = fixture.ReadTemplateJson(templateId);
        var generationBefore = templates.Generation;
        store.FailMove = InFolder(fixture.Paths.TemplatesDirectory);

        await Assert.ThrowsAsync<IOException>(() => templates.DeleteTemplateAsync(templateId));

        Assert.Equal(1, store.FailedOperations);
        Assert.True(templates.FindTemplate(templateId)!.IsReady);
        Assert.Equal(generationBefore, templates.Generation);
        Assert.Equal(fileBefore, fixture.ReadTemplateJson(templateId));
        Assert.Empty(FilesIn(fixture.Paths.TemplateTrashDirectory));

        store.FailMove = null;
        await templates.DeleteTemplateAsync(templateId);
        Assert.Null(templates.FindTemplate(templateId));
        Assert.Equal(fileBefore, File.ReadAllText(Assert.Single(FilesIn(fixture.Paths.TemplateTrashDirectory))));
    }

    [Fact]
    public async Task InstantiateBindingFailure_KeepsTheNewPlate_AndNeverTouchesTheTemplate()
    {
        var store = new FaultInjectingStore();
        var (fixture, templates, templateId) = await SeedAsync(store);
        using var _ = fixture;
        var templateBefore = fixture.ReadTemplateJson(templateId);
        var platesBefore = FilesIn(fixture.Paths.PlatesDirectory);
        store.FailWrite = InFolder(fixture.Paths.CharactersDirectory);

        // The Plate's document is written before its character link, and the Plate Library reports
        // the half-succeeded creation as a link failure the player can read; what this Library owes
        // is that the Plate exists exactly once and the Template is untouched.
        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => templates.InstantiateAsync(templateId, Characters.Alice));
        Assert.Contains("couldn't be linked", refused.Message);

        Assert.Equal(1, store.FailedOperations);
        var plateFiles = FilesIn(fixture.Paths.PlatesDirectory);
        Assert.Equal(platesBefore.Count + 1, plateFiles.Count);
        var created = Assert.Single(plateFiles.Except(platesBefore));
        Assert.True(PlateStoragePaths.TryParsePlateFileName(created, out var createdId));
        Assert.True(fixture.PlateLibrary.FindPlate(createdId)!.IsReady);
        Assert.Empty(FilesIn(fixture.Paths.CharactersDirectory));
        Assert.Equal(templateBefore, fixture.ReadTemplateJson(templateId));
        Assert.True(templates.FindTemplate(templateId)!.IsReady);

        // Once the link can be written again, using the Template works as usual and still never touches it.
        store.FailWrite = null;
        var result = await templates.InstantiateAsync(templateId, Characters.Alice);
        Assert.True(fixture.PlateLibrary.FindPlate(result.PlateId)!.IsReady);
        Assert.Equal(platesBefore.Count + 2, FilesIn(fixture.Paths.PlatesDirectory).Count);
        Assert.Single(FilesIn(fixture.Paths.CharactersDirectory));
        Assert.Equal(templateBefore, fixture.ReadTemplateJson(templateId));
    }

    [Fact]
    public async Task LibraryScan_Problems_NeverContainLocalPaths()
    {
        var store = new FaultInjectingStore();
        var (fixture, templates, templateId) = await SeedAsync(store);
        using var _ = fixture;
        await templates.DeleteTemplateAsync(templateId);
        var trashed = Assert.Single(FilesIn(fixture.Paths.TemplateTrashDirectory));

        // The operating system's own message names the full path; that message must stay out of
        // the scan's problems, which a cleanup UI may show as they are.
        store.FailRead = InFolder(fixture.Paths.TemplateTrashDirectory);
        store.FaultFactory = path => new IOException($"Could not find file '{path}'.");
        Assert.True(UserFacingError.ContainsPath(store.FaultFactory(trashed).Message));

        var scan = await templates.ScanAssetReferencesAsync();

        Assert.False(scan.IsComplete);
        var problem = Assert.Single(scan.Problems);
        Assert.Contains(Path.GetFileName(trashed), problem);
        Assert.Contains(nameof(IOException), problem);
        Assert.False(UserFacingError.ContainsPath(problem));
        var logged = Assert.Single(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal));
        Assert.Contains(Path.GetFileName(trashed), logged);
        Assert.False(UserFacingError.ContainsPath(logged));
    }
}

/// <summary>
/// A Template file the store had to serve from its backup copy (Dalamud's reliable storage does
/// this silently) is loaded, but never silently: the damage is logged and the on-disk bytes are
/// kept under Recovery before anything can write over them.
/// </summary>
public class TemplateBackupRecoveryTests
{
    private static async Task<(TemplateLibraryFixture Fixture, Guid TemplateId, string Intact)> SeedAsync(BackupSimulatingStore store)
    {
        var fixture = new TemplateLibraryFixture(store);
        var templates = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await templates.SaveAsTemplateAsync(plate.PlateId, "Backed Up");
        return (fixture, templateId, fixture.ReadTemplateJson(templateId));
    }

    private static IReadOnlyList<string> RecoveryFiles(TemplateLibraryFixture fixture) =>
        Directory.Exists(fixture.Paths.RecoveryDirectory) ? Directory.GetFiles(fixture.Paths.RecoveryDirectory) : [];

    [Fact]
    public async Task DamagedTemplate_RecoveredFromBackup_IsLogged_AndTheDamagedFileIsKeptInRecovery()
    {
        var store = new BackupSimulatingStore();
        var (fixture, templateId, intact) = await SeedAsync(store);
        using var _ = fixture;
        var path = fixture.Paths.GetTemplatePath(templateId);
        File.WriteAllText(path, "{ truncated");
        fixture.Log.Messages.Clear();
        fixture.Clock.Tick();

        var reloaded = fixture.CreateService();
        await reloaded.InitializeAsync();

        var summary = reloaded.FindTemplate(templateId)!;
        Assert.True(summary.IsReady);
        Assert.Equal("Backed Up", summary.DisplayName);
        Assert.Empty(reloaded.GetSavedDocument(templateId)!.Elements);
        Assert.Contains("Backed Up", intact, StringComparison.Ordinal);

        // Loading never rewrites the file; the damaged bytes are copied, not moved.
        Assert.Equal("{ truncated", File.ReadAllText(path));
        var recovery = Assert.Single(RecoveryFiles(fixture));
        Assert.Equal("{ truncated", File.ReadAllText(recovery));
        Assert.StartsWith(templateId + ".damaged-", Path.GetFileName(recovery), StringComparison.Ordinal);

        var warnings = fixture.Log.Messages.Where(m => m.StartsWith("W ", StringComparison.Ordinal)).ToList();
        Assert.Contains(warnings, m => m.Contains("recovered from its backup", StringComparison.Ordinal) && m.Contains(templateId.ToString(), StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("Recovery", StringComparison.Ordinal) && m.Contains(Path.GetFileName(recovery), StringComparison.Ordinal));
        Assert.All(fixture.Log.Messages, m => Assert.False(UserFacingError.ContainsPath(m), m));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal));

        // The next write replaces the damaged bytes on disk; the Recovery copy still holds them.
        await reloaded.RenameTemplateAsync(templateId, "Renamed");
        Assert.Contains("Renamed", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal("{ truncated", File.ReadAllText(recovery));
        Assert.Single(RecoveryFiles(fixture));
    }

    [Fact]
    public async Task HealthyTemplate_IsReadOnce_WithNoLogAndNoRecoveryCopy()
    {
        var store = new BackupSimulatingStore();
        var (fixture, templateId, intact) = await SeedAsync(store);
        using var _ = fixture;
        fixture.Log.Messages.Clear();
        var readsBefore = store.ReaderInvocations;

        var reloaded = fixture.CreateService();
        await reloaded.InitializeAsync();

        Assert.True(reloaded.FindTemplate(templateId)!.IsReady);
        Assert.Equal(readsBefore + 1, store.ReaderInvocations);
        Assert.Equal(intact, fixture.ReadTemplateJson(templateId));
        Assert.Empty(RecoveryFiles(fixture));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) || m.StartsWith("E ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewerVersionTemplate_IsNeverReplacedByAnOlderBackup_AndKeepsNoRecoveryCopy()
    {
        var store = new BackupSimulatingStore();
        var (fixture, templateId, _) = await SeedAsync(store);
        using var _ = fixture;
        var path = fixture.Paths.GetTemplatePath(templateId);
        var newer = TemplateSamples.Envelope(templateId, "From the future", "{}", version: 999);
        File.WriteAllText(path, newer);
        fixture.Log.Messages.Clear();
        var readsBefore = store.ReaderInvocations;

        var reloaded = fixture.CreateService();
        await reloaded.InitializeAsync();

        var summary = reloaded.FindTemplate(templateId)!;
        Assert.Equal(TemplateStatus.NewerVersion, summary.Status);
        Assert.Equal("From the future", summary.DisplayName);
        Assert.Equal(readsBefore + 1, store.ReaderInvocations);
        Assert.Equal(newer, File.ReadAllText(path));
        Assert.Empty(RecoveryFiles(fixture));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.Contains("backup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoveryCopyFailure_IsLogged_AndTheTemplateStillLoads()
    {
        var store = new BackupSimulatingStore();
        var (fixture, templateId, _) = await SeedAsync(store);
        using var _ = fixture;
        var path = fixture.Paths.GetTemplatePath(templateId);
        File.WriteAllText(path, "{ truncated");
        fixture.Log.Messages.Clear();

        // A file where the Recovery folder should be makes every copy there fail.
        File.WriteAllText(fixture.Paths.RecoveryDirectory, "in the way");

        var reloaded = fixture.CreateService();
        await reloaded.InitializeAsync();

        Assert.True(reloaded.IsLoaded);
        Assert.True(reloaded.FindTemplate(templateId)!.IsReady);
        Assert.Equal("{ truncated", File.ReadAllText(path));
        var error = Assert.Single(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal));
        Assert.Contains("Recovery copy", error, StringComparison.Ordinal);
        Assert.False(UserFacingError.ContainsPath(error));
    }
}

/// <summary>A Template whose embedded document holds numbers no build writes (beyond float's
/// range) loads with the same in-memory repairs a Plate gets, and every Plate made from it saves
/// finite numbers.</summary>
public class TemplateValueRepairTests
{
    [Fact]
    public async Task TemplateWithOverflowingNumbers_LoadsReady_InstantiatesAndSaves()
    {
        using var fixture = new TemplateLibraryFixture();
        var templateId = Guid.NewGuid();
        var document = JsonNode.Parse(JsonSerializer.Serialize(BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.BlankCanvasId, fixture.Clock.Now, null), JsonOptions.Default))!.AsObject();
        document["CanvasWidth"] = 1e39;
        document["Background"]!["Opacity"] = 1e39;
        var json = TemplateSamples.Envelope(templateId, "Overflowing", document.ToJsonString(JsonOptions.Default));
        fixture.WriteTemplateJson(templateId, json);

        var templates = await fixture.LoadAsync();

        Assert.True(templates.FindTemplate(templateId)!.IsReady);
        Assert.Equal(json, fixture.ReadTemplateJson(templateId));
        var view = templates.GetSavedDocument(templateId)!;
        Assert.Equal(ProfileDocument.LegacyCanvasWidth, view.CanvasWidth);
        Assert.Equal(1f, view.Background!.Opacity);
        var warning = Assert.Single(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal));
        Assert.Contains("repaired", warning, StringComparison.Ordinal);
        Assert.Contains(templateId.ToString(), warning, StringComparison.Ordinal);

        var result = await templates.InstantiateAsync(templateId, null);
        var created = fixture.PlateLibrary.OpenDocumentForEditing(result.PlateId);
        Assert.Equal(ProfileDocument.LegacyCanvasWidth, created.CanvasWidth);
        Assert.Equal(1f, created.Background!.Opacity);
        await fixture.PlateLibrary.SavePlateDocumentAsync(created);
        AssertEveryNumberIsFinite(JsonNode.Parse(File.ReadAllText(fixture.Paths.GetPlatePath(result.PlateId))));
        Assert.Equal(json, fixture.ReadTemplateJson(templateId));
    }

    [Fact]
    public void Materialize_ReportsTheRepair_OnlyWhenOneWasNeeded()
    {
        var id = Guid.NewGuid();
        var document = JsonNode.Parse(JsonSerializer.Serialize(BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.BlankCanvasId, DateTime.UtcNow, null), JsonOptions.Default))!.AsObject();
        var intact = (JsonObject)JsonNode.Parse(TemplateSamples.Envelope(id, "Intact", document.ToJsonString(JsonOptions.Default)))!;
        document["CanvasWidth"] = 1e39;
        var overflowing = (JsonObject)JsonNode.Parse(TemplateSamples.Envelope(id, "Overflowing", document.ToJsonString(JsonOptions.Default)))!;

        TemplateDocuments.Materialize(intact, out var repairedIntact);
        var template = TemplateDocuments.Materialize(overflowing, out var repairedOverflowing);

        Assert.False(repairedIntact);
        Assert.True(repairedOverflowing);
        Assert.Equal(ProfileDocument.LegacyCanvasWidth, template.Document.CanvasWidth);
    }

    private static void AssertEveryNumberIsFinite(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    AssertEveryNumberIsFinite(child);
                }

                break;

            case JsonArray array:
                foreach (var child in array)
                {
                    AssertEveryNumberIsFinite(child);
                }

                break;

            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                Assert.True(value.TryGetValue<double>(out var number) && double.IsFinite(number) && Math.Abs(number) < 1e30, "a saved number is not a usable float");
                break;
        }
    }
}

/// <summary>A load that unloading interrupts in the middle of a file is not a damaged file: it
/// stops, logs nothing about the file, and leaves the Library unloaded rather than failed.</summary>
public class TemplateLoadInterruptionTests
{
    [Fact]
    public Task LoadAbandonedMidFile_DoesNotLogTheTemplateAsDamaged() =>
        AssertInterruptionPropagatesUnrecordedAsync<OperationAbandonedException>(() => new OperationAbandonedException());

    [Fact]
    public Task LoadCanceledMidFile_DoesNotLogTheTemplateAsDamaged() =>
        AssertInterruptionPropagatesUnrecordedAsync<OperationCanceledException>(() => new OperationCanceledException());

    private static async Task AssertInterruptionPropagatesUnrecordedAsync<TInterruption>(Func<Exception> interruption)
        where TInterruption : Exception
    {
        var store = new ReadInterruptingStore();
        using var fixture = new TemplateLibraryFixture(store);
        await fixture.PlateLibrary.InitializeAsync();
        var document = PlateDocuments.ToJson(BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.BlankCanvasId, fixture.Clock.Now, null)).ToJsonString(JsonOptions.Default);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        fixture.WriteTemplateJson(first, TemplateSamples.Envelope(first, "First", document));
        fixture.WriteTemplateJson(second, TemplateSamples.Envelope(second, "Second", document));
        var interrupted = fixture.Paths.GetTemplatePath(second);
        store.Interrupt = path => path == interrupted ? interruption() : null;
        var templates = fixture.CreateService();

        await Assert.ThrowsAnyAsync<TInterruption>(() => templates.InitializeAsync());

        Assert.False(templates.IsLoaded);
        Assert.False(templates.LoadFailed);
        Assert.Equal(BuiltInTemplateCatalog.All.Select(d => d.TemplateId), templates.GetOrderedTemplates().Select(t => t.TemplateId));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.Contains("unreadable", StringComparison.Ordinal));

        // Nothing about the interrupted file was recorded: the next load lists it like any other.
        store.Interrupt = null;
        await templates.InitializeAsync();
        Assert.True(templates.IsLoaded);
        Assert.True(templates.FindTemplate(first)!.IsReady);
        Assert.True(templates.FindTemplate(second)!.IsReady);
    }

    /// <summary>Plain files whose read of a chosen path fails the way the shutdown guard (or a
    /// canceled store) fails it: before the reader is ever invoked.</summary>
    private sealed class ReadInterruptingStore : IPlateFileStore
    {
        private readonly SystemFileStore files = new();

        internal Func<string, Exception?>? Interrupt { get; set; }

        public bool FileExists(string path) => files.FileExists(path);

        public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => files.ListFiles(directory, searchPattern);

        public Task ReadTextAsync(string path, Action<StoredText> reader)
        {
            if (Interrupt?.Invoke(path) is { } interruption)
            {
                throw interruption;
            }

            return files.ReadTextAsync(path, reader);
        }

        public Task WriteTextAsync(string path, string contents) => files.WriteTextAsync(path, contents);

        public void MoveFile(string sourcePath, string destinationPath) => files.MoveFile(sourcePath, destinationPath);

        public void CopyFile(string sourcePath, string destinationPath) => files.CopyFile(sourcePath, destinationPath);

        public void DeleteFile(string path) => files.DeleteFile(path);
    }
}

/// <summary>
/// Loading reads and parses every Template file; in game the dispatcher is the framework thread
/// and the store's reads are synchronous, so that work must leave the dispatcher's thread before
/// the first file is touched (see the class remarks on threading in <see cref="TemplateLibraryService"/>).
/// </summary>
public class TemplateLibraryThreadingTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InitializeAsync_ReadsAndParsesOffTheDispatcherThread()
    {
        var recording = new ThreadRecordingStore(new SystemFileStore());
        using var fixture = new TemplateLibraryFixture(recording);
        var seeded = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var first = await seeded.SaveAsTemplateAsync(plate.PlateId, "First");
        var second = await seeded.SaveAsTemplateAsync(plate.PlateId, "Second");
        var callsBefore = recording.Calls.Count;

        using var dispatcher = new QueuedDispatcher();
        var templates = new TemplateLibraryService(fixture.Paths, recording, fixture.PlateLibrary, fixture.Log, () => fixture.Clock.Now, dispatcher.Dispatch);
        await templates.InitializeAsync().WaitAsync(Generous);

        var loadCalls = recording.Calls.Skip(callsBefore).ToList();
        Assert.Contains(loadCalls, c => c.Operation == nameof(IPlateFileStore.ListFiles) && c.Path == fixture.Paths.TemplatesDirectory);
        Assert.Equal(2, loadCalls.Count(c => c.Operation == nameof(IPlateFileStore.ReadTextAsync)));
        Assert.All(loadCalls, c => Assert.NotEqual(dispatcher.ThreadId, c.ThreadId));
        Assert.True(templates.IsLoaded);
        Assert.Equal(seeded.GetOrderedTemplates().Select(t => t.TemplateId), templates.GetOrderedTemplates().Select(t => t.TemplateId));
        Assert.True(templates.FindTemplate(first)!.IsReady);
        Assert.True(templates.FindTemplate(second)!.IsReady);
    }

    [Fact]
    public async Task InitializeAsync_DispatchedDelegateYieldsBeforeTheFirstRead()
    {
        var blocking = new BlockingReadStore();
        using var fixture = new TemplateLibraryFixture(blocking);
        var seeded = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await seeded.SaveAsTemplateAsync(plate.PlateId, "Held");
        var generationBefore = seeded.Generation;

        using var dispatcher = new QueuedDispatcher();
        var templates = new TemplateLibraryService(fixture.Paths, blocking, fixture.PlateLibrary, fixture.Log, () => fixture.Clock.Now, dispatcher.Dispatch);
        blocking.Hold();
        Task loading;
        try
        {
            loading = templates.InitializeAsync();
            await blocking.ReadStarted.WaitAsync(Generous);

            // The read is blocking its thread right now; the dispatcher's thread is not that thread,
            // so it is free for the next tick's work while the load is still under way.
            await dispatcher.Dispatch(() => Task.CompletedTask).WaitAsync(Generous);
            Assert.False(loading.IsCompleted);
            Assert.False(templates.IsLoaded);
        }
        finally
        {
            blocking.Release();
        }

        await loading.WaitAsync(Generous);
        Assert.True(templates.IsLoaded);
        Assert.True(templates.FindTemplate(templateId)!.IsReady);
        Assert.Equal(seeded.GetOrderedTemplates().Select(t => t.TemplateId), templates.GetOrderedTemplates().Select(t => t.TemplateId));
        Assert.True(templates.Generation > 0);
        Assert.Equal(generationBefore, seeded.Generation);
    }

    /// <summary>Plain files whose next read BLOCKS its calling thread until released, the way
    /// Dalamud's reliable storage reads do (they never yield).</summary>
    private sealed class BlockingReadStore : IPlateFileStore
    {
        private readonly SystemFileStore files = new();
        private readonly ManualResetEventSlim gate = new(initialState: true);
        private TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task ReadStarted => readStarted.Task;

        internal void Hold()
        {
            readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.Reset();
        }

        internal void Release() => gate.Set();

        public bool FileExists(string path) => files.FileExists(path);

        public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => files.ListFiles(directory, searchPattern);

        public Task ReadTextAsync(string path, Action<StoredText> reader)
        {
            readStarted.TrySetResult();
            gate.Wait();
            return files.ReadTextAsync(path, reader);
        }

        public Task WriteTextAsync(string path, string contents) => files.WriteTextAsync(path, contents);

        public void MoveFile(string sourcePath, string destinationPath) => files.MoveFile(sourcePath, destinationPath);

        public void CopyFile(string sourcePath, string destinationPath) => files.CopyFile(sourcePath, destinationPath);

        public void DeleteFile(string path) => files.DeleteFile(path);
    }
}
