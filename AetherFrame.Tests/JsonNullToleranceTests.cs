using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// An explicit JSON null in a list or string field — something no AetherFrame build ever writes,
/// but a hand-edited file or a crafted package can hold — reads as empty, so the document can be
/// cloned, compared, drawn and saved like any other. Every consumer that used to dereference such a
/// field (the editor's baseline capture, Basic placements, Active Hours text, automatic element
/// names, search) is exercised here.
/// </summary>
public class JsonNullToleranceTests
{
    [Fact]
    public void BasicSettings_WithNullListsAndStrings_CloneCompareAndLookUpPlacements()
    {
        const string json = """{ "Placements": null, "Playstyles": null, "FavoriteJobIds": null, "ThemeName": null, "ActiveHours": { "TimeZone": null } }""";

        var settings = JsonSerializer.Deserialize<BasicPlateSettings>(json, JsonOptions.Default)!;

        Assert.Empty(settings.Placements);
        Assert.Empty(settings.Playstyles);
        Assert.Empty(settings.FavoriteJobIds);
        Assert.Equal(string.Empty, settings.ThemeId);
        Assert.Equal(string.Empty, settings.ActiveHours!.TimeZone);
        Assert.Null(settings.GetPlacement(ProfileElementRole.BasicPortrait));
        Assert.True(settings.ContentEquals(settings.Clone()));
        Assert.Equal("8 PM - 11 PM", BasicPlateText.TimeRange(settings.ActiveHours));
    }

    [Fact]
    public void TextElement_WithNullStrings_ReadsAsEmpty_AndKeepsTheDefaultFont()
    {
        const string json = """{ "elementType": "text", "Name": null, "Text": null, "Prefix": null, "Suffix": null, "FontFamily": null }""";

        var element = Assert.IsType<TextProfileElement>(JsonSerializer.Deserialize<ProfileElement>(json, JsonOptions.Default));

        Assert.Equal(string.Empty, element.Name);
        Assert.Equal(string.Empty, element.Text);
        Assert.Equal(string.Empty, element.Prefix);
        Assert.Equal(string.Empty, element.Suffix);
        Assert.Equal(ProfileFontFamilies.DalamudDefault, element.FontFamily);
        Assert.Equal(string.Empty, element.GetDisplayText());
        Assert.True(element.ContentEquals(element.Clone()));
        Assert.Equal("Text 1", ProfileElementNames.NextSequentialName([element], new TextProfileElement()));
    }

    [Fact]
    public void IdentityHeader_WithNullTitleAndStyleValues_Clones()
    {
        const string json = """{ "CustomTitle": null, "LayoutStyle": { "Layout": 4, "Applied": null, "Previous": null } }""";

        var identity = JsonSerializer.Deserialize<BasicIdentityHeader>(json, JsonOptions.Default)!;

        Assert.Equal(string.Empty, identity.CustomTitle);
        Assert.Null(identity.LayoutStyle!.Applied.FontSize);
        Assert.True(identity.ContentEquals(identity.Clone()));
    }

    [Fact]
    public void Document_WithNullElementsAndName_IsUsable_AndSavesEmptyValues()
    {
        const string json = """{ "Version": 2, "Elements": null, "Name": null, "CanvasWidth": 1280, "CanvasHeight": 720 }""";

        var result = VersionedJson.Parse(json, PersistenceSchemas.ProfileDocument, PlateDocuments.Deserialize);

        Assert.True(result.IsUsable, result.Migration.Error);
        var document = result.Value!;
        Assert.Empty(document.Elements);
        Assert.Equal(string.Empty, document.Name);
        PlateDocuments.ApplyLegacyRepairs(document);

        var saved = PlateDocuments.ToJson(document);
        Assert.Empty(saved["Elements"]!.AsArray());
        Assert.Equal(string.Empty, saved["Name"]!.GetValue<string>());
    }

    [Fact]
    public void Template_WithNullName_ReadsAsEmpty()
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Inner", DateTime.UtcNow), JsonOptions.Default))!;
        var envelope = new JsonObject { ["Version"] = 1, ["TemplateId"] = Guid.NewGuid().ToString(), ["Name"] = null, ["Document"] = document };

        var template = TemplateDocuments.Deserialize(envelope);

        Assert.NotNull(template);
        Assert.Equal(string.Empty, template!.Name);
    }

    [Fact]
    public void LibraryStateAndBinding_WithNullLists_ReadAsEmpty()
    {
        var state = JsonSerializer.Deserialize<PlateLibraryState>("""{ "Version": 1, "OrderedPlateIds": null }""", JsonOptions.Default)!;
        var binding = JsonSerializer.Deserialize<Domain.Characters.CharacterBinding>("""{ "Version": 2, "ContentId": 5, "ProfileIds": null }""", JsonOptions.Default)!;

        Assert.Empty(state.OrderedPlateIds);
        Assert.Empty(binding.PlateIds);
        Assert.Empty(binding.Clone().PlateIds);
    }

    [Fact]
    public void Search_TreatsANullNameAsNoMatch()
    {
        Assert.False(PlateSearch.Matches("x", null));
        Assert.True(PlateSearch.Matches("", null));
        Assert.True(PlateSearch.Matches("xav", null, ["Xavier"]));
    }

    // ---------------------------------------------------------------- local files

    [Fact]
    public async Task LocalPlate_WithNullName_LoadsReady_AndSearchDoesNotThrow()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        var json = JsonNode.Parse(JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Named", fixture.Clock.Now), JsonOptions.Default))!.AsObject();
        json["Name"] = null;
        fixture.WritePlateJson(plateId, json.ToJsonString());

        var library = await fixture.LoadAsync();

        var summary = library.GetOrderedPlates().Single(p => p.PlateId == plateId);
        Assert.Equal(PlateStatus.Ready, summary.Status);
        Assert.Equal(string.Empty, summary.DisplayName);
        Assert.Empty(library.Search("x"));
        Assert.Single(library.Search(""));
    }

    [Theory]
    [InlineData("Playstyles")]
    [InlineData("Placements")]
    public async Task LocalPlate_WithANullBasicList_LoadsReady_AndTheEditorBaselineCaptures(string field)
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        var json = JsonNode.Parse(JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Named", fixture.Clock.Now), JsonOptions.Default))!.AsObject();
        json["BasicPlate"] = new JsonObject { [field] = null };
        fixture.WritePlateJson(plateId, json.ToJsonString());

        var library = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);

        var profiles = new ProfileService(library);
        profiles.OpenPlate(plateId);
        var state = ProfileService.DocumentState.Capture(profiles.CurrentProfile!);
        Assert.True(state.BasicPlate!.ContentEquals(profiles.CurrentProfile!.BasicPlate));
        Assert.Null(profiles.CurrentProfile!.BasicPlate!.GetPlacement(ProfileElementRole.BasicPortrait));
    }

    [Fact]
    public async Task LocalPlate_WithNulls_SavesEmptyValues_NeverNull()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        var document = PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Named", fixture.Clock.Now);
        document.Elements.Add(new TextProfileElement { Text = "hi" });
        var json = JsonNode.Parse(JsonSerializer.Serialize(document, JsonOptions.Default))!.AsObject();
        json["Name"] = null;
        json["BasicPlate"] = new JsonObject { ["Playstyles"] = null, ["ActiveHours"] = new JsonObject { ["TimeZone"] = null } };
        var element = json["Elements"]![0]!;
        element["Name"] = null;
        element["Text"] = null;
        fixture.WritePlateJson(plateId, json.ToJsonString());
        var library = await fixture.LoadAsync();

        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        var saved = JsonNode.Parse(fixture.ReadPlateJson(plateId))!.AsObject();
        Assert.Equal(string.Empty, saved["Name"]!.GetValue<string>());
        Assert.Empty(saved["BasicPlate"]!["Playstyles"]!.AsArray());
        Assert.Equal(string.Empty, saved["BasicPlate"]!["ActiveHours"]!["TimeZone"]!.GetValue<string>());
        Assert.Equal(string.Empty, saved["Elements"]![0]!["Name"]!.GetValue<string>());
        Assert.Equal(string.Empty, saved["Elements"]![0]!["Text"]!.GetValue<string>());
    }

    [Fact]
    public async Task LocalPlate_WithoutNulls_RoundTripsByteIdentically()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(SampleDocuments.Rich(plateId, "Rich", fixture.Clock.Now), JsonOptions.Default);
        fixture.WritePlateJson(plateId, json);
        var library = await fixture.LoadAsync();

        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        Assert.Equal(json, fixture.ReadPlateJson(plateId));
    }

    [Fact]
    public async Task LocalBindingV2_WithNullProfileIds_LoadsAsABindingWithNoPlates_AndIsNotDamaged()
    {
        using var fixture = new LibraryFixture();
        var character = new CharacterContext(4242, "Keep Me", "Phoenix");
        fixture.WriteBindingJson(character.ContentId,
            """{"Version":2,"ContentId":4242,"ActiveProfileId":null,"ProfileIds":null,"LastKnownCharacterName":"Keep Me","LastKnownHomeWorld":"Phoenix"}""");
        var library = await fixture.LoadAsync();

        var binding = library.GetBinding(character.ContentId);
        Assert.NotNull(binding);
        Assert.Empty(binding!.PlateIds);
        Assert.Equal("Keep Me", binding.LastKnownCharacterName);

        // A damaged binding would be preserved in Recovery before its first rewrite; this one is simply used.
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, character);
        Assert.Equal([plate.PlateId], library.GetBinding(character.ContentId)!.PlateIds);
        Assert.False(Directory.Exists(fixture.Paths.RecoveryDirectory));
    }

    [Fact]
    public async Task LocalLibraryIndex_WithNullOrder_LoadsAndIsRebuilt()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "A", fixture.Clock.Now), JsonOptions.Default));
        fixture.WriteLibraryJson("""{ "Version": 1, "OrderedPlateIds": null }""");

        var library = await fixture.LoadAsync();

        Assert.Equal([plateId], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal([plateId], fixture.ReadLibraryOrder());
    }

    [Fact]
    public async Task LocalTemplate_WithNullName_LoadsReady_AndSearchDoesNotThrow()
    {
        using var fixture = new TemplateLibraryFixture();
        var templateId = Guid.NewGuid();
        var document = JsonNode.Parse(JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Inner", fixture.Clock.Now), JsonOptions.Default))!;
        var envelope = new JsonObject
        {
            ["Version"] = PlateTemplate.CurrentSchemaVersion,
            ["TemplateId"] = templateId.ToString(),
            ["Name"] = null,
            ["CreatedAtUtc"] = "2025-01-02T03:04:05Z",
            ["UpdatedAtUtc"] = "2025-02-03T04:05:06Z",
            ["Document"] = document,
        };
        fixture.WriteTemplateJson(templateId, envelope.ToJsonString());

        var templates = await fixture.LoadAsync();

        Assert.Equal(TemplateStatus.Ready, templates.FindTemplate(templateId)!.Status);
        Assert.DoesNotContain(templates.Search("x"), t => t.TemplateId == templateId);
    }

    // ---------------------------------------------------------------- imported packages

    /// <summary>A valid exported Plate with one text element, and the tools to hand-edit its profile.json.</summary>
    private sealed class NullPackages : IDisposable
    {
        private NullPackages(PackageFixture fixture, PlateLibraryService library, PlatePackageService packages, string validPath)
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

        internal static async Task<NullPackages> CreateAsync()
        {
            var fixture = new PackageFixture();
            var (library, packages) = await fixture.LoadAsync();
            var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Small");
            var document = library.OpenDocumentForEditing(created.PlateId);
            document.Elements.Add(new TextProfileElement { Text = "hello", Position = new(100, 10), Size = new(200, 40), ZIndex = 1 });
            await library.SavePlateDocumentAsync(document);
            var path = fixture.Export(packages, created.PlateId, "valid.aetherframe");
            return new NullPackages(fixture, library, packages, path);
        }

        internal string Craft(Action<JsonObject> edit) => PackageFiles.Rewrite(ValidPath, entries => PackageFiles.EditProfile(entries, edit));

        /// <summary>Inspects and imports; null when the package was refused, which is the other acceptable outcome.</summary>
        internal async Task<Guid?> ImportOrRefuseAsync(string path)
        {
            using var staged = Packages.Inspect(path);
            if (!staged.CanImport)
            {
                Assert.Contains(staged.Compatibility, new[] { PackageCompatibility.Invalid, PackageCompatibility.Unsupported });
                return null;
            }

            var result = await Packages.ImportAsync(staged);
            Assert.True(result.Succeeded, result.Error?.Message);
            return result.PlateId;
        }

        internal EditorSession OpenInEditor(Guid plateId, out ProfileService profiles)
        {
            profiles = new ProfileService(Library);
            profiles.OpenPlate(plateId);
            return new EditorSession(profiles, Fixture.Assets, new FakeImages(), Fixture.Log, () => 1);
        }

        public void Dispose() => Fixture.Dispose();
    }

    [Theory]
    [InlineData("Playstyles")]
    [InlineData("Placements")]
    public async Task ImportedPlate_WithANullBasicList_OpensInTheEditorWithoutThrowing(string field)
    {
        using var setup = await NullPackages.CreateAsync();
        var path = setup.Craft(p => p["BasicPlate"] = new JsonObject { [field] = null });

        var plateId = await setup.ImportOrRefuseAsync(path);
        if (plateId is null)
        {
            return;
        }

        // The first editor frame: SyncWithCurrentProfile captures the baseline through BasicPlateSettings.Clone.
        var session = setup.OpenInEditor(plateId.Value, out var profiles);
        session.SyncWithCurrentProfile();
        Assert.False(session.IsDirty);
        Assert.Null(profiles.CurrentProfile!.BasicPlate!.GetPlacement(ProfileElementRole.BasicPortrait));
    }

    [Fact]
    public async Task ImportedPlate_WithANullTimeZone_FormatsActiveHoursWithoutThrowing()
    {
        using var setup = await NullPackages.CreateAsync();
        var path = setup.Craft(p => p["BasicPlate"] = new JsonObject { ["ActiveHours"] = new JsonObject { ["TimeZone"] = null } });

        var plateId = await setup.ImportOrRefuseAsync(path);
        if (plateId is null)
        {
            return;
        }

        var document = setup.Library.OpenDocumentForEditing(plateId.Value);
        Assert.Equal("8 PM - 11 PM", BasicPlateText.TimeRange(document.BasicPlate!.ActiveHours!));
    }

    [Fact]
    public async Task ImportedPlate_WithANullElementName_CanAddAnElement()
    {
        using var setup = await NullPackages.CreateAsync();
        var path = setup.Craft(p =>
        {
            var text = ((JsonArray)p["Elements"]!).First(e => e!["elementType"]!.GetValue<string>() == "text")!;
            text["Name"] = null;
        });

        var plateId = await setup.ImportOrRefuseAsync(path);
        if (plateId is null)
        {
            return;
        }

        var session = setup.OpenInEditor(plateId.Value, out var profiles);
        session.SyncWithCurrentProfile();
        var before = profiles.CurrentProfile!.Elements.Count;

        var added = session.AddTextElement();

        Assert.Null(session.ErrorMessage);
        Assert.NotNull(added);
        Assert.Equal(before + 1, profiles.CurrentProfile!.Elements.Count);
    }
}
