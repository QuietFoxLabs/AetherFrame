using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
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
