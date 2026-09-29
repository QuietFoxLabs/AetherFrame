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
/// envelope, a Plate or Template put back from the trash by hand while AetherFrame runs, any other
/// file in the Plates or Templates folder, or a loaded file whose bytes on disk aren't what memory
/// holds. Missing one would let cleanup move a wanted image to the asset trash.
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

    private static string RichTemplate(Guid templateId, DateTime now) =>
        TemplateSamples.Envelope(templateId, "Rich", JsonSerializer.Serialize(SampleDocuments.Rich(Guid.NewGuid(), "Rich", now), JsonOptions.Default));

    [Fact]
    public async Task TemplatePutBackFromTheTrashByHand_WhileLoaded_KeepsItsImagesLive()
    {
        using var fixture = new TemplateLibraryFixture();
        var templateId = Guid.NewGuid();
        fixture.WriteTemplateJson(templateId, RichTemplate(templateId, fixture.Clock.Now));
        var templates = await fixture.LoadAsync();
        await templates.DeleteTemplateAsync(templateId);
        File.Move(Directory.GetFiles(fixture.Paths.TemplateTrashDirectory).Single(), fixture.Paths.GetTemplatePath(templateId));

        var scan = await templates.ScanAssetReferencesAsync();

        Assert.True(scan.IsComplete);
        Assert.Contains(SampleDocuments.ImageAsset, scan.ReferencedAssetIds);
        Assert.Contains(SampleDocuments.BackgroundAsset, scan.ReferencedAssetIds);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("built-in-id")]
    public async Task FileInTheTemplatesFolderThatIsntLoaded_StillKeepsItsImagesLive(string kind)
    {
        using var fixture = new TemplateLibraryFixture();
        var templateId = kind == "copy" ? Guid.NewGuid() : BuiltInTemplateCatalog.BlankCanvasId;
        var name = kind == "copy" ? $"{templateId} - Copy.json" : $"{templateId}.json";
        Directory.CreateDirectory(fixture.Paths.TemplatesDirectory);
        File.WriteAllText(Path.Combine(fixture.Paths.TemplatesDirectory, name), RichTemplate(templateId, fixture.Clock.Now), Encoding.UTF8);
        var templates = await fixture.LoadAsync();
        Assert.DoesNotContain(templates.GetOrderedTemplates(), t => !t.IsBuiltIn);

        var scan = await templates.ScanAssetReferencesAsync();

        Assert.True(scan.IsComplete);
        Assert.Contains(SampleDocuments.ImageAsset, scan.ReferencedAssetIds);
    }

    [Fact]
    public async Task UnreadableFileInTheTemplatesFolderThatIsntLoaded_MakesTheScanIncomplete()
    {
        using var fixture = new TemplateLibraryFixture();
        Directory.CreateDirectory(fixture.Paths.TemplatesDirectory);
        File.WriteAllText(Path.Combine(fixture.Paths.TemplatesDirectory, "notes.json"), "{ truncated");
        var templates = await fixture.LoadAsync();

        var scan = await templates.ScanAssetReferencesAsync();

        Assert.False(scan.IsComplete);
        Assert.Contains(scan.Problems, p => p.Contains("notes.json", StringComparison.Ordinal) && !p.Contains(fixture.Root, StringComparison.Ordinal));
    }

    /// <summary>
    /// A Plate read from its backup keeps the backup's references in memory, but its damaged file
    /// on disk may be newer and need other images. Until that file has a Recovery copy, it is the
    /// only copy of that content: the scan reads it too, and can't call itself complete when the
    /// file doesn't parse.
    /// </summary>
    [Fact]
    public async Task PlateReadFromItsBackup_WhoseDamagedFileHasNoRecoveryCopy_LeavesTheScanIncomplete()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = Guid.NewGuid();
        await store.WriteTextAsync(fixture.Paths.GetPlatePath(plateId), JsonSerializer.Serialize(SampleDocuments.Rich(plateId, "Rich", fixture.Clock.Now), JsonOptions.Default));
        File.WriteAllText(fixture.Paths.GetPlatePath(plateId), "{ truncated");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.RecoveryDirectory)!);
        File.WriteAllText(fixture.Paths.RecoveryDirectory, "in the way");
        var library = await fixture.LoadAsync();

        var scan = await library.ScanAssetReferencesAsync();

        Assert.False(scan.IsComplete);
        Assert.Contains(SampleDocuments.ImageAsset, scan.ReferencedAssetIds);

        // Once the damaged file is kept and replaced, the Plate on disk is the one in memory.
        File.Delete(fixture.Paths.RecoveryDirectory);
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));
        Assert.True((await library.ScanAssetReferencesAsync()).IsComplete);
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
