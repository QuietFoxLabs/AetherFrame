using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Lifecycle;
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

        public Task ReadTextAsync(string path, Action<string> reader)
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
