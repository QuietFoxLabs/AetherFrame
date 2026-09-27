using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Explicit JSON nulls in a package: no AetherFrame build ever writes one, so a package holding one
/// is either refused outright or imported as a Plate every consumer can use — and opening the
/// package never throws, so its staging folder is always cleaned up. A null Elements is refused
/// (the Plate would import with nothing on it); a null entry inside a Basic list is refused on the
/// raw JSON, because typed reading would otherwise quietly leave it out.
/// </summary>
public class PackageNullToleranceTests
{
    /// <summary>An exported Adventure Plate Classic with Favorite Jobs and Playstyles, and the tools to hand-edit its profile.json.</summary>
    private sealed class Setup : IDisposable
    {
        private Setup(PackageFixture fixture, PlateLibraryService library, PlatePackageService packages, string validPath)
        {
            Fixture = fixture;
            Library = library;
            Packages = packages;
            ValidPath = validPath;
        }

        internal PackageFixture Fixture { get; }

        internal PlateLibraryService Library { get; }

        internal PlatePackageService Packages { get; }

        internal string ValidPath { get; }

        internal static async Task<Setup> CreateAsync()
        {
            var fixture = new PackageFixture();
            var (library, packages) = await fixture.LoadAsync();
            var created = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Classic", new PlateStarterContent(PackageFixture.BasicTestCharacter));
            var document = library.OpenDocumentForEditing(created.PlateId);
            var editor = BasicDocuments.Editor(document);
            editor.SetFavoriteJobs([FakeJobs.Paladin, FakeJobs.WhiteMage]);
            editor.SetPlaystyles(["Casual", "Roleplay"]);
            Assert.NotEmpty(document.BasicPlate!.Placements);
            await library.SavePlateDocumentAsync(document);
            return new Setup(fixture, library, packages, fixture.Export(packages, created.PlateId, "valid.aetherframe"));
        }

        internal string Craft(Action<JsonObject> edit) => PackageFiles.Rewrite(ValidPath, entries => PackageFiles.EditProfile(entries, edit));

        /// <summary>
        /// Opening never throws. The package is then either refused (Invalid, nothing prepared,
        /// staging gone once disposed) or imported as a Plate that materializes, clones, serializes
        /// and saves — the operations every editor frame and every save depend on.
        /// </summary>
        internal async Task<bool> AssertRefusedOrUsableAsync(string path)
        {
            var before = Fixture.SnapshotInstallation();
            var staged = Packages.Inspect(path);
            try
            {
                Assert.True(Directory.Exists(staged.StagingDirectory));
                if (!staged.CanImport)
                {
                    Assert.Equal(PackageCompatibility.Invalid, staged.Compatibility);
                    Assert.Contains(staged.Diagnostics.Errors, e => e.Code == PackageErrorCode.ProfileInvalid);
                    Assert.Null(staged.PreparedProfile);
                    Assert.Null(staged.PreviewDocument);
                    Assert.False((await Packages.ImportAsync(staged)).Succeeded);
                    return false;
                }

                var result = await Packages.ImportAsync(staged);
                Assert.True(result.Succeeded, result.Error?.Message);
                var document = Library.OpenDocumentForEditing(result.PlateId);
                var state = ProfileService.DocumentState.Capture(document);
                Assert.True(state.BasicPlate is null || state.BasicPlate.ContentEquals(document.BasicPlate));
                Assert.NotNull(PlateDocuments.ToJson(document));
                await Library.SavePlateDocumentAsync(document);
                var reloaded = await Fixture.Library.LoadAsync();
                Assert.True(reloaded.FindPlate(result.PlateId)!.IsReady);
                return true;
            }
            finally
            {
                staged.Dispose();
                Assert.False(Directory.Exists(staged.StagingDirectory));
            }
        }

        public void Dispose() => Fixture.Dispose();
    }

    [Fact]
    public async Task Inspect_WithNullElements_NeverThrows_AndLeavesNoStaging()
    {
        using var setup = await Setup.CreateAsync();
        var path = setup.Craft(p => p["Elements"] = null);
        var before = setup.Fixture.SnapshotInstallation();

        var staged = setup.Packages.Inspect(path);
        try
        {
            Assert.Equal(PackageCompatibility.Invalid, staged.Compatibility);
            var error = Assert.Single(staged.Diagnostics.Errors);
            Assert.Equal(PackageErrorCode.ProfileInvalid, error.Code);
            Assert.Equal("Elements is not an array", error.Detail);
            Assert.Null(staged.PreparedProfile);
            Assert.True(Directory.Exists(staged.StagingDirectory));
            Assert.False((await setup.Packages.ImportAsync(staged)).Succeeded);
        }
        finally
        {
            staged.Dispose();
        }

        Assert.False(Directory.Exists(staged.StagingDirectory));
        Assert.True(setup.Fixture.StagingIsEmpty);
        Assert.Equal(before, setup.Fixture.SnapshotInstallation());
    }

    [Fact]
    public async Task NullPlacements_WithAFavoriteJobElement_NeverThrows()
    {
        using var setup = await Setup.CreateAsync();
        var path = setup.Craft(p =>
        {
            Assert.Contains(p["Elements"]!.AsArray(), e => e!["Role"]?.GetValue<int>() == (int)ProfileElementRole.BasicJob);
            p["BasicPlate"]!["Placements"] = null;
        });

        var imported = await setup.AssertRefusedOrUsableAsync(path);

        // Nothing is silently repaired at import: the null reads as an empty list, exactly as a local file's would.
        if (imported)
        {
            var plate = setup.Library.GetOrderedPlates()[0];
            Assert.Empty(setup.Library.OpenDocumentForEditing(plate.PlateId).BasicPlate!.Placements);
        }
    }

    [Theory]
    [InlineData("Playstyles")]
    [InlineData("Placements")]
    [InlineData("Elements")]
    [InlineData("ActiveHours")]
    public async Task NullFields_NeverThrow_AndAreRefusedOrUsable(string field)
    {
        using var setup = await Setup.CreateAsync();
        var path = setup.Craft(p =>
        {
            if (field == "Elements")
            {
                p["Elements"] = null;
            }
            else
            {
                p["BasicPlate"]![field] = null;
            }
        });

        var imported = await setup.AssertRefusedOrUsableAsync(path);
        Assert.Equal(field != "Elements", imported);
    }

    [Theory]
    [InlineData("Playstyles")]
    [InlineData("Placements")]
    public async Task NullEntryInABasicList_IsRefusedOnTheRawJson(string list)
    {
        using var setup = await Setup.CreateAsync();
        var path = setup.Craft(p =>
        {
            var entries = p["BasicPlate"]![list]!.AsArray();
            Assert.NotEmpty(entries);
            entries.Insert(1, null);
        });
        var before = setup.Fixture.SnapshotInstallation();

        var staged = setup.Packages.Inspect(path);
        try
        {
            Assert.Equal(PackageCompatibility.Invalid, staged.Compatibility);
            var error = Assert.Single(staged.Diagnostics.Errors);
            Assert.Equal(PackageErrorCode.ProfileInvalid, error.Code);
            Assert.Equal($"{list} holds an empty entry", error.Detail);
            Assert.False((await setup.Packages.ImportAsync(staged)).Succeeded);
        }
        finally
        {
            staged.Dispose();
        }

        Assert.True(setup.Fixture.StagingIsEmpty);
        Assert.Equal(before, setup.Fixture.SnapshotInstallation());
    }

    [Fact]
    public async Task TheSameListsWithoutNullEntries_StillImport()
    {
        using var setup = await Setup.CreateAsync();
        var path = setup.Craft(p => Assert.All(p["BasicPlate"]!["Placements"]!.AsArray(), e => Assert.NotNull(e)));

        Assert.True(await setup.AssertRefusedOrUsableAsync(path));
    }

    [Fact]
    public void Validator_ReportsANullWhereTheModelNeverHoldsOne_AsContent()
    {
        // Typed reading of a document the model can't hold surfaces as a refusal, never as an escaped exception.
        var raw = JsonNode.Parse("""{ "Version": 2, "Name": "Plate", "CanvasWidth": 1280, "CanvasHeight": 720, "Elements": null }""")!.AsObject();
        var manifest = new PackageManifest { PlateName = "Plate", PlateSchemaVersion = ProfileDocument.CurrentSchemaVersion };
        var diagnostics = new PackageDiagnostics();

        var validated = PackageProfileValidator.Validate(raw, manifest, diagnostics);

        Assert.Null(validated);
        Assert.Equal(PackageErrorCode.ProfileInvalid, Assert.Single(diagnostics.Errors).Code);
    }

    [Fact]
    public async Task ImportedPlate_WithNullTextFields_OpensClonesAndSaves()
    {
        using var setup = await Setup.CreateAsync();
        var path = setup.Craft(p =>
        {
            var text = p["Elements"]!.AsArray().First(e => e!["elementType"]!.GetValue<string>() == "text")!;
            text["Name"] = null;
            text["Text"] = null;
            text["Prefix"] = null;
            text["FontFamily"] = null;
            p["BasicPlate"]!["ThemeName"] = null;
        });

        if (await setup.AssertRefusedOrUsableAsync(path))
        {
            var document = setup.Library.OpenDocumentForEditing(setup.Library.GetOrderedPlates()[0].PlateId);
            Assert.All(document.Elements.OfType<TextProfileElement>(), t => Assert.NotNull(t.Text));
            Assert.Contains(document.Elements.OfType<TextProfileElement>(), t => t.Text.Length == 0 && t.Name.Length == 0 && t.FontFamily == ProfileFontFamilies.DalamudDefault);
            Assert.Equal(string.Empty, document.BasicPlate!.ThemeId);
        }
    }
}
