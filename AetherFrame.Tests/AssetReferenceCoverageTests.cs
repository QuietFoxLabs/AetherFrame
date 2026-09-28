using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The reference scan any future asset cleanup must start from (nothing runs cleanup yet) never
/// misses an image something on disk may still need: data a newer build keeps in a Template's
/// envelope, a Plate put back from the trash by hand while AetherFrame runs, or any other file in
/// the Plates folder. Missing one would let cleanup move a wanted image to the asset trash.
/// </summary>
public class AssetReferenceCoverageTests
{
    [Fact]
    public async Task TemplateEnvelopeDataFromANewerBuild_KeepsItsImagesLive_EvenInTheTrash()
    {
        using var fixture = new TemplateLibraryFixture();
        var templateId = Guid.NewGuid();
        var cover = Guid.NewGuid();
        var originArt = Guid.NewGuid();
        var document = BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.BlankCanvasId, fixture.Clock.Now, null);
        document.ProfileId = templateId;
        var envelope = TemplateSamples.Envelope(templateId, "Future", PlateDocuments.ToJson(document).ToJsonString(JsonOptions.Default), originKind: 1)
            .Replace("\"Origin\": { \"Kind\": 1 }", $"\"Origin\": {{ \"Kind\": 1, \"FutureArt\": \"{originArt}\" }}, \"FutureCover\": \"{cover}\"", StringComparison.Ordinal);
        Assert.Contains(cover.ToString(), envelope, StringComparison.Ordinal);
        fixture.WriteTemplateJson(templateId, envelope);
        var templates = await fixture.LoadAsync();

        var live = await LiveAssetReferences.ComputeAsync(fixture.PlateLibrary, templates);

        Assert.True(live.IsComplete);
        Assert.Contains(cover, live.ReferencedAssetIds);
        Assert.Contains(originArt, live.ReferencedAssetIds);

        await templates.DeleteTemplateAsync(templateId);
        var trashed = await templates.ScanAssetReferencesAsync();
        Assert.Contains(cover, trashed.ReferencedAssetIds);
        Assert.Contains(originArt, trashed.ReferencedAssetIds);
    }

    [Fact]
    public async Task PlatePutBackFromTheTrashByHand_WhileLoaded_KeepsItsImagesLive()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(SampleDocuments.Rich(plateId, "Rich", fixture.Clock.Now), JsonOptions.Default));
        var library = await fixture.LoadAsync();
        await library.DeletePlateAsync(plateId);
        File.Move(Directory.GetFiles(fixture.Paths.PlateTrashDirectory).Single(), fixture.Paths.GetPlatePath(plateId));

        var scan = await library.ScanAssetReferencesAsync();

        Assert.True(scan.IsComplete);
        Assert.Contains(SampleDocuments.ImageAsset, scan.ReferencedAssetIds);
        Assert.Contains(SampleDocuments.BackgroundAsset, scan.ReferencedAssetIds);
    }

    [Fact]
    public async Task FileInThePlatesFolderWhoseNameIsntAPlates_StillKeepsItsImagesLive()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        Directory.CreateDirectory(fixture.Paths.PlatesDirectory);
        File.WriteAllText(Path.Combine(fixture.Paths.PlatesDirectory, $"{plateId} - Copy.json"),
            JsonSerializer.Serialize(SampleDocuments.Rich(plateId, "Rich", fixture.Clock.Now), JsonOptions.Default), Encoding.UTF8);
        var library = await fixture.LoadAsync();

        var scan = await library.ScanAssetReferencesAsync();

        Assert.True(scan.IsComplete);
        Assert.Contains(SampleDocuments.ImageAsset, scan.ReferencedAssetIds);
    }

    [Fact]
    public async Task UnreadableFileInThePlatesFolderThatIsntLoaded_MakesTheScanIncomplete()
    {
        using var fixture = new LibraryFixture();
        Directory.CreateDirectory(fixture.Paths.PlatesDirectory);
        File.WriteAllText(Path.Combine(fixture.Paths.PlatesDirectory, "notes.json"), "{ truncated");
        var library = await fixture.LoadAsync();

        var scan = await library.ScanAssetReferencesAsync();

        Assert.False(scan.IsComplete);
        Assert.Contains(scan.Problems, p => p.Contains("notes.json", StringComparison.Ordinal) && !p.Contains(fixture.Root, StringComparison.Ordinal));
    }

    [Fact]
    public void AddImage_WhoseSourceChangesFormatAfterItsFirstCheck_IsStoredUnderTheStoredContentsExtension()
    {
        using var directory = new TempDirectory();
        var paths = new PlateStoragePaths(directory.Path);
        var source = TestImages.Write(directory.Path, "source.img", TestImages.Png(4, 4));
        var swapped = false;

        // The decoder query runs between the first inspection and the copy: the file changes there.
        bool Decoder(string extension)
        {
            if (!swapped)
            {
                swapped = true;
                File.WriteAllBytes(source, TestImages.Jpeg(4, 4));
            }

            return true;
        }

        var storage = new AssetStorageService(paths.AssetsDirectory, paths.AssetStagingDirectory, new AssetMetadataStore(paths.AssetMetadataDirectory), Decoder);

        var id = storage.ImportImage(source);

        var stored = storage.ResolveAssetPath(id)!;
        Assert.Equal(".jpg", Path.GetExtension(stored));
        Assert.Equal("image/jpeg", storage.Metadata.TryLoad(id)!.MediaType);
    }
}
