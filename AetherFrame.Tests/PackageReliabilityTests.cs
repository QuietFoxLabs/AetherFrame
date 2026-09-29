using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Import and export edge cases found in the Plate Library reliability review: what an export
/// that its own self-check refuses tells the player, what counts as a package image being used,
/// text that isn't UTF-8, and retrying an import whose Plate was already written.
/// </summary>
public class PackageReliabilityTests
{
    /// <summary>A Plate a 0.1.5 editor could save, or a hand edit could leave: fine locally, past a package limit.</summary>
    [Theory]
    [InlineData("font-2000", "font size out of range")]
    [InlineData("duplicate-element-id", "missing or duplicate element id")]
    public async Task ExportRefusedByItsSelfCheck_SaysWhatInThePlateCantBeCarried(string kind, string detail)
    {
        using var fixture = new PackageFixture();
        var plateId = Guid.NewGuid();
        var document = PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Older", fixture.Clock.Now);
        var text = new TextProfileElement { Text = "big", Position = new Vector2(10, 10), Size = new Vector2(200, 60) };
        document.Elements.Add(text);
        if (kind == "font-2000")
        {
            text.FontSize = 2000;
        }
        else
        {
            document.Elements.Add(new TextProfileElement { Id = text.Id, Text = "twin", Position = new Vector2(10, 200), Size = new Vector2(200, 60) });
        }

        fixture.Library.WritePlateJson(plateId, JsonSerializer.Serialize(document, JsonOptions.Default));
        var (library, packages) = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        var destination = Path.Combine(fixture.ExportDirectory, "older.aetherframe");

        var result = packages.Export(plateId, destination, overwrite: false);

        Assert.False(result.Succeeded);
        Assert.Equal($"This Plate holds a value a Plate file can't carry ({detail}). Change it in the editor and save, then export again.", result.FailureMessage);
        Assert.DoesNotContain("in this file", result.FailureMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.Exists(fixture.ExportDirectory) ? Directory.GetFiles(fixture.ExportDirectory) : []);
    }

    [Fact]
    public void SelfCheckRefusal_NeverQuotesADetailThatNamesAPath_AndKeepsTheCheckersOwnAdviceOtherwise()
    {
        var withPath = PackageExporter.DescribeSelfCheckRefusal(
            [new PackageError(PackageErrorCode.ProfileInvalid, "The Plate in this file is damaged.", @"unreadable C:\Users\Someone\x") { IsFieldValue = true }]);
        Assert.Equal("This Plate holds a value a Plate file can't carry. Change it in the editor and save, then export again.", Assert.Single(withPath).Message);

        // Not a value the editor shows: no advice to change it there, and the detail stays in the log.
        var structural = Assert.Single(PackageExporter.DescribeSelfCheckRefusal(
            [new PackageError(PackageErrorCode.ProfileInvalid, "The Plate in this file is damaged.", "malformed JSON at line 1, byte 2345")]));
        Assert.Equal("This Plate holds data a Plate file can't carry, so it can't be exported.", structural.Message);
        Assert.Equal("malformed JSON at line 1, byte 2345", structural.Detail);

        // "Too many elements" already says what to change; it is passed on as it is.
        PackageError[] tooLarge = [new PackageError(PackageErrorCode.PackageTooLarge, "The Plate has too many elements (the limit is 256).")];
        Assert.Equal(tooLarge, PackageExporter.DescribeSelfCheckRefusal(tooLarge));
        Assert.Equal("The Plate couldn't be exported.", Assert.Single(PackageExporter.DescribeSelfCheckRefusal([])).Message);
    }

    /// <summary>
    /// Data the editor keeps without showing it: a hand-edited element that isn't an object is
    /// kept as an unrecognized element and written back on every save, so no edit can clear it.
    /// </summary>
    [Fact]
    public async Task ExportRefusedForDataTheEditorDoesntShow_GivesNoEditorAdvice()
    {
        using var fixture = new PackageFixture();
        var plateId = Guid.NewGuid();
        var document = PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Hand edited", fixture.Clock.Now);
        document.Elements.Add(new TextProfileElement { Text = "kept", Position = new Vector2(10, 10), Size = new Vector2(200, 60) });
        var raw = PlateDocuments.ToJson(document);
        raw["Elements"]!.AsArray().Add(0);
        fixture.Library.WritePlateJson(plateId, raw.ToJsonString(JsonOptions.Default));
        var (library, packages) = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        var destination = Path.Combine(fixture.ExportDirectory, "hand-edited.aetherframe");

        var result = packages.Export(plateId, destination, overwrite: false);

        Assert.False(result.Succeeded);
        Assert.Equal("This Plate holds data a Plate file can't carry, so it can't be exported.", result.FailureMessage);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task ImageMentionedOnlyInATextField_IsNotAUse_AndThePackageIsRefused()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var assetId = fixture.AddImage(TestImages.Png(64, 48));
        var plateId = (await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Small")).PlateId;
        var document = library.OpenDocumentForEditing(plateId);
        document.Elements.Add(new ImageProfileElement { AssetId = assetId, Position = new Vector2(10, 10), Size = new Vector2(64, 48) });
        document.Elements.Add(new TextProfileElement { Text = "hello", Position = new Vector2(100, 10), Size = new Vector2(200, 40), ZIndex = 1 });
        await library.SavePlateDocumentAsync(document);
        var valid = fixture.Export(packages, plateId, "valid.aetherframe");
        var manifest = PackageFiles.Json(PackageFiles.Entry(PackageFiles.Read(valid), PackagePaths.ManifestPath));
        var packageAssetId = manifest["assets"]![0]!["id"]!.GetValue<string>();

        var crafted = PackageFiles.Rewrite(valid, entries => PackageFiles.EditProfile(entries, profile =>
        {
            var elements = profile["Elements"]!.AsArray();
            elements.Remove(elements.First(e => e!["elementType"]!.GetValue<string>() == "image"));
            elements.First(e => e!["elementType"]!.GetValue<string>() == "text")!["Name"] = packageAssetId;
        }));
        var assetsBefore = fixture.Assets.ListAssets().Count;

        using var staged = packages.Inspect(crafted);

        Assert.False(staged.CanImport);
        Assert.Contains(staged.Diagnostics.Errors, e => e.Code == PackageErrorCode.AssetUndeclared);
        Assert.Equal(assetsBefore, fixture.Assets.ListAssets().Count);

        // The genuine package, whose image is used by an image element, still imports.
        using var genuine = packages.Inspect(valid);
        Assert.True(genuine.CanImport, genuine.DescribeForLog());
    }

    /// <summary>
    /// The check that a package's images are used runs again on the document that is actually
    /// imported. An image the reference scan finds only in a form the import doesn't re-point (a
    /// newer build's data holding it in braces-and-hex form), while an exact spelling of it sits in
    /// a text field, would otherwise pass: the import re-points only the text, and the new image is
    /// then referenced by nothing the scan counts.
    /// </summary>
    [Fact]
    public async Task ImageUsedOnlyInAFormTheImportDoesntRepoint_IsRefused()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var assetId = fixture.AddImage(TestImages.Png(64, 48));
        var plateId = (await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Small")).PlateId;
        var document = library.OpenDocumentForEditing(plateId);
        document.Elements.Add(new ImageProfileElement { AssetId = assetId, Position = new Vector2(10, 10), Size = new Vector2(64, 48) });
        document.Elements.Add(new TextProfileElement { Text = "hello", Position = new Vector2(100, 10), Size = new Vector2(200, 40), ZIndex = 1 });
        await library.SavePlateDocumentAsync(document);
        var valid = fixture.Export(packages, plateId, "valid.aetherframe");
        var manifest = PackageFiles.Json(PackageFiles.Entry(PackageFiles.Read(valid), PackagePaths.ManifestPath));
        var packageAssetId = Guid.Parse(manifest["assets"]![0]!["id"]!.GetValue<string>());

        var crafted = PackageFiles.Rewrite(valid, entries => PackageFiles.EditProfile(entries, profile =>
        {
            var elements = profile["Elements"]!.AsArray();
            var image = elements.First(e => e!["elementType"]!.GetValue<string>() == "image")!.AsObject();
            image["elementType"] = "futureImage";
            image["AssetId"] = packageAssetId.ToString("X");
            elements.First(e => e!["elementType"]!.GetValue<string>() == "text")!["Name"] = packageAssetId.ToString("D");
        }));
        var assetsBefore = fixture.Assets.ListAssets().Count;

        using var staged = packages.Inspect(crafted);

        Assert.False(staged.CanImport, staged.DescribeForLog());
        Assert.Contains(staged.Diagnostics.Errors, e => e.Code == PackageErrorCode.AssetUndeclared);
        Assert.Equal(assetsBefore, fixture.Assets.ListAssets().Count);
    }

    [Fact]
    public async Task ProfileTextThatIsntUtf8_IsADamagedPlate_NotAnUnexpectedFailure()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var plateId = (await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Small")).PlateId;
        var document = library.OpenDocumentForEditing(plateId);
        document.Elements.Add(new TextProfileElement { Text = "hello", Position = new Vector2(100, 10), Size = new Vector2(200, 40) });
        await library.SavePlateDocumentAsync(document);
        var valid = fixture.Export(packages, plateId, "valid.aetherframe");
        var crafted = PackageFiles.Rewrite(valid, entries =>
        {
            var profile = PackageFiles.Entry(entries, PackagePaths.ProfilePath);
            var at = profile.Bytes.AsSpan().IndexOf("\"hello\""u8);
            var bytes = profile.Bytes.ToArray();
            bytes[at + 1] = 0xC3;
            bytes[at + 2] = 0x28;
            profile.Bytes = bytes;
        });
        var log = new TestLog();

        using var staged = PackageReader.Open(crafted, fixture.Paths.PackageStagingDirectory, null, log);

        Assert.Equal(PackageCompatibility.Invalid, staged.Compatibility);
        Assert.Contains(staged.Diagnostics.Errors, e => e.Code == PackageErrorCode.ProfileInvalid);
        Assert.DoesNotContain(staged.Diagnostics.Errors, e => e.Code == PackageErrorCode.InvalidArchive);
        Assert.DoesNotContain(log.Messages, m => m.Contains("unexpected", StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestTextThatIsntUtf8_IsReportedAsDamage_NotThrown()
    {
        var json = Encoding.UTF8.GetBytes("{\"format\":\"aetherframe.package\",\"formatVersion\":1,\"requires\":[\"plate\"],\"generator\":\"AB\"}");
        var at = Array.IndexOf(json, (byte)'A');
        json[at] = 0xC3;
        json[at + 1] = 0x28;
        var diagnostics = new PackageDiagnostics();

        var manifest = PackageManifest.Parse(json, diagnostics);

        Assert.Null(manifest);
        Assert.Equal(PackageErrorCode.ManifestInvalid, Assert.Single(diagnostics.Errors).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportWhosePlateWasWrittenButUnfinished_CantBeImportedAgain(bool withImages)
    {
        var store = new FaultInjectingStore();
        using var fixture = new PackageFixture(store: store);
        var (library, packages) = await fixture.LoadAsync();
        var plateId = withImages
            ? (await fixture.CreateRichPlateAsync(library)).PlateId
            : (await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Plain")).PlateId;
        var path = fixture.Export(packages, plateId);
        var platesBefore = Directory.GetFiles(fixture.Paths.PlatesDirectory).Length;

        // The Plate file lands, its write then reports failure, and it can't be moved away.
        store.ThrowAfterWrite = LibraryFiles.IsPlate;
        store.FailMove = LibraryFiles.IsPlate;
        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport);
        var first = await packages.ImportAsync(staged);
        Assert.False(first.Succeeded);
        Assert.Equal(PackageImporter.UnfinishedMessage, first.Error!.Message);
        Assert.False(staged.CanImport);

        store.ThrowAfterWrite = null;
        store.FailMove = null;
        var second = await packages.ImportAsync(staged);

        Assert.False(second.Succeeded);
        Assert.Equal(platesBefore + 1, Directory.GetFiles(fixture.Paths.PlatesDirectory).Length);
        var reloaded = await fixture.Library.LoadAsync();
        Assert.Equal(platesBefore + 1, reloaded.GetOrderedPlates().Count);
        Assert.All(reloaded.GetOrderedPlates(), p => Assert.Equal(PlateStatus.Ready, p.Status));
    }

    [Fact]
    public async Task SuccessfulImport_CantBeRepeatedFromTheSameCheckedFile()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var plateId = (await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Plain")).PlateId;
        using var staged = packages.Inspect(fixture.Export(packages, plateId));

        Assert.True((await packages.ImportAsync(staged)).Succeeded);

        Assert.False(staged.CanImport);
        Assert.False((await packages.ImportAsync(staged)).Succeeded);
        Assert.Equal(2, library.GetOrderedPlates().Count);
        Assert.False(UserFacingError.ContainsPath(PackageImporter.UnfinishedMessage));
    }
}
