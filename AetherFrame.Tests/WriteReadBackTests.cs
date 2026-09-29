using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A Plate write puts on disk exactly the text the Library proved, first, to load again as a Ready
/// Plate through the reader startup uses. Anything that wouldn't is refused before a file is
/// touched: in game the write would also replace the storage's backup copy, so the Plate could be
/// lost for good at the next startup.
/// </summary>
public class WriteReadBackTests
{
    /// <summary>A current document that the startup reader would take for a newer version's.</summary>
    private static JsonObject NewerVersionDocument(Guid plateId, DateTime now)
    {
        var raw = PlateDocuments.ToJson(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "From elsewhere", now));
        raw[nameof(Domain.Profiles.ProfileDocument.Version)] = 99;
        return raw;
    }

    [Fact]
    public async Task ImportedDocumentThatWouldNotLoadAsReady_IsRefused_BeforeAnythingIsWritten()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plateId = Guid.NewGuid();
        var generation = library.Generation;

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.ImportPlateAsync(plateId, NewerVersionDocument(plateId, fixture.Clock.Now)));

        Assert.Equal(PlateLibraryService.UnloadableWriteMessage, refused.Message);
        Assert.False(File.Exists(fixture.Paths.GetPlatePath(plateId)));
        Assert.Null(library.FindPlate(plateId));
        Assert.Equal(generation, library.Generation);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains(plateId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlateFromADocumentThatWouldNotLoadAsReady_IsRefused_AndNothingIsCreated()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(
            () => library.CreatePlateFromTemplateAsync(NewerVersionDocument(Guid.NewGuid(), fixture.Clock.Now), "Made", Characters.Alice));

        Assert.Equal(PlateLibraryService.UnloadableWriteMessage, refused.Message);
        Assert.Empty(library.GetOrderedPlates());
        Assert.Empty(fixture.Store.ListFiles(fixture.Paths.PlatesDirectory, "*.json"));
        Assert.False(File.Exists(fixture.Paths.GetBindingPath(Characters.Alice.ContentId)));
    }

    [Fact]
    public async Task EveryWrite_PutsOnDiskTheTextTheLibraryKeeps()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plateId = (await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, Characters.Alice, "Written")).PlateId;
        await library.RenamePlateAsync(plateId, "Renamed");
        var copyId = await library.DuplicatePlateAsync(plateId);
        var document = library.OpenDocumentForEditing(plateId);
        document.CanvasWidth = 1600;
        await library.SavePlateDocumentAsync(document);

        foreach (var id in new[] { plateId, copyId })
        {
            Assert.Equal(library.GetSavedJsonForExport(id).Json, fixture.ReadPlateJson(id));
        }

        var reloaded = await fixture.LoadAsync();
        Assert.All(reloaded.GetOrderedPlates(), p => Assert.Equal(PlateStatus.Ready, p.Status));
    }

    [Fact]
    public async Task DuplicateWhoseOrderWriteFails_StillReturnsTheCopy_SoARetryMakesNoSecondOne()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var sourceId = (await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source")).PlateId;
        store.FailWrite = LibraryFiles.IsLibrary;

        var copyId = await library.DuplicatePlateAsync(sourceId);

        Assert.Equal([sourceId, copyId], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.True(File.Exists(fixture.Paths.GetPlatePath(copyId)));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("Library order", StringComparison.Ordinal));

        store.FailWrite = null;
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(2, reloaded.GetOrderedPlates().Count);
    }
}
