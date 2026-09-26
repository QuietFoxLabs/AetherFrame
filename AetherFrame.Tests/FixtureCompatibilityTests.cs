using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Characters;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Every checked-in fixture file against the current schemas: the tagged releases' files are all
/// current (no shipped build ever wrote another version), and the hand-written pre-release shapes
/// under <c>Fixtures/legacy</c> — the single-profile document (versions 0 and 1, and unversioned),
/// the version-1 binding and a version-1 Template around a version-1 document — migrate exactly as
/// the migration tests describe. Those shapes predate v0.1.0 and never shipped externally; they
/// exist on disk here so the migration code keeps facing verbatim input rather than JSON built by
/// the current serializer.
/// </summary>
public class FixtureCompatibilityTests
{
    private static readonly Guid LegacyPlateId = Guid.Parse("0f0f0f0f-1111-4111-8111-000000000101");
    private static readonly Guid LegacyTemplateId = Guid.Parse("0f0f0f0f-2222-4222-8222-000000000201");
    private const ulong LegacyOwnerContentId = 4242;

    /// <summary>Every JSON record in the fixture tree, by path relative to the fixture root.</summary>
    public static TheoryData<string> RecordFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.GetFiles(HistoricalFixtures.Root, "*.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(HistoricalFixtures.Root, file).Replace('\\', '/');
                if (!relative.EndsWith("/manifest.json", StringComparison.Ordinal))
                {
                    data.Add(relative);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RecordFiles))]
    public void EveryFixtureRecord_ParsesUsable_NeverInvalidOrNewer(string relativePath)
    {
        var json = File.ReadAllText(Path.Combine(HistoricalFixtures.Root, relativePath), Encoding.UTF8);
        var isLegacy = relativePath.StartsWith("legacy/", StringComparison.Ordinal);
        var expected = isLegacy ? SchemaMigrationOutcome.Migrated : SchemaMigrationOutcome.Current;
        var name = Path.GetFileName(relativePath);

        SchemaMigrationResult migration;
        if (relativePath.Contains("/Profiles/", StringComparison.Ordinal) || relativePath.Contains("/Trash/Plates/", StringComparison.Ordinal) || name.StartsWith("plate-", StringComparison.Ordinal))
        {
            var result = VersionedJson.Parse(json, PersistenceSchemas.ProfileDocument, PlateDocuments.Deserialize);
            Assert.True(result.IsUsable, result.Migration.Error);
            migration = result.Migration;
        }
        else if (relativePath.Contains("/Characters/", StringComparison.Ordinal) || name.StartsWith("binding-", StringComparison.Ordinal))
        {
            var result = VersionedJson.Parse<CharacterBinding>(json, PersistenceSchemas.CharacterBinding);
            Assert.True(result.IsUsable, result.Migration.Error);
            migration = result.Migration;
        }
        else if (relativePath.Contains("/Library/", StringComparison.Ordinal))
        {
            var result = VersionedJson.Parse<PlateLibraryState>(json, PersistenceSchemas.PlateLibrary);
            Assert.True(result.IsUsable, result.Migration.Error);
            migration = result.Migration;
        }
        else if (relativePath.Contains("/Templates/", StringComparison.Ordinal) || name.StartsWith("template-", StringComparison.Ordinal))
        {
            var raw = JsonNode.Parse(json)!.AsObject();
            migration = PersistenceSchemas.Template.Migrate(raw);
            var document = PersistenceSchemas.ProfileDocument.Migrate(raw["Document"]!.AsObject());
            Assert.True(document.IsUsable, document.Error);
            Assert.Equal(expected, document.Outcome);
            Assert.NotNull(TemplateDocuments.Deserialize(raw));

            // The envelope has only ever had one version; only the embedded document migrates.
            expected = SchemaMigrationOutcome.Current;
        }
        else if (relativePath.Contains("/asset-metadata/", StringComparison.Ordinal))
        {
            var result = VersionedJson.Parse<AssetMetadata>(json, PersistenceSchemas.AssetMetadata);
            Assert.True(result.IsUsable, result.Migration.Error);
            migration = result.Migration;
        }
        else
        {
            throw new Xunit.Sdk.XunitException($"No schema is known for fixture file {relativePath}; add it here when adding a new record kind.");
        }

        Assert.Equal(expected, migration.Outcome);
        Assert.True(migration.IsUsable, migration.Error);
    }

    // ---------------------------------------------------------------- single-profile documents

    [Theory]
    [InlineData("plate-v1-single-profile.json", 1)]
    [InlineData("plate-v0-single-profile.json", 0)]
    [InlineData("plate-unversioned-single-profile.json", 1)]
    public void LegacyPlate_MaterializesToThePinnedValues(string fileName, int originalVersion)
    {
        var result = VersionedJson.Parse(ReadLegacy(fileName), PersistenceSchemas.ProfileDocument, PlateDocuments.Deserialize);
        Assert.Equal(SchemaMigrationOutcome.Migrated, result.Migration.Outcome);
        Assert.Equal(originalVersion, result.Migration.OriginalVersion);

        var document = PlateDocuments.Materialize(result.Raw!, out var repairedValues);

        Assert.False(repairedValues, "Legacy repairs are expected, not value repairs: nothing here is a non-number or a repeated id.");
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, document.Version);
        Assert.Equal(LegacyPlateId, document.ProfileId);
        Assert.Equal(LegacyOwnerContentId, document.OwnerContentId);
        Assert.Equal("Default", document.Name);
        Assert.Equal(3, document.Revision);
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), document.CreatedAtUtc);
        Assert.Equal(new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc), document.UpdatedAtUtc);

        // The implicit canvas of the single-profile era, resolved in memory.
        Assert.Equal(ProfileDocument.LegacyCanvasWidth, document.CanvasWidth);
        Assert.Equal(ProfileDocument.LegacyCanvasHeight, document.CanvasHeight);

        // The legacy background fields become the background model and are never written again.
        Assert.NotNull(document.Background);
        Assert.Equal(ProfileBackgroundMode.Image, document.Background!.Mode);
        Assert.Equal(LegacyData.BackgroundAsset, document.Background.ImageAssetId);
        Assert.Equal(ProfileImageFit.Fit, document.Background.ImageFit);
        Assert.Equal(0.8f, document.Background.Opacity, 3);
        Assert.Null(document.LegacyBackgroundAssetId);
        Assert.Null(document.LegacyBackgroundFitMode);
        Assert.Null(document.LegacyBackgroundOpacity);

        // Never configured in that era: null stays null (only an explicit Basic edit creates them).
        Assert.Null(document.BasicIdentity);
        Assert.Null(document.BasicPlate);
        Assert.Null(document.Components);
        Assert.Null(document.ExtensionData);
        Assert.False(document.HasUnsupportedElements);

        Assert.Equal(3, document.Elements.Count);
        var text = Assert.IsType<TextProfileElement>(document.Elements[0]);
        Assert.Equal(Guid.Parse("0f0f0f0f-0000-0000-0000-000000000001"), text.Id);
        Assert.Equal("Hello from the past", text.Text);
        Assert.Equal(string.Empty, text.Name);
        Assert.Equal(ProfileElementRole.None, text.Role);
        Assert.True(text.UsesLegacyLayout, "A text element saved without LayoutVersion keeps the legacy layout rules.");
        Assert.Equal(string.Empty, text.Prefix);
        Assert.Equal(string.Empty, text.Suffix);
        Assert.Equal(22f, text.FontSize);
        Assert.Equal(new Vector4(0.5f, 0.25f, 1f, 1f), text.Color);
        Assert.Equal(new Vector2(123.5f, 456.25f), text.Position);
        Assert.Equal(new Vector2(300f, 60f), text.Size);
        Assert.True(text.Visible);
        Assert.Equal(0, text.ZIndex);

        var image = Assert.IsType<ImageProfileElement>(document.Elements[1]);
        Assert.Equal(LegacyData.PortraitAsset, image.AssetId);
        Assert.Equal(ProfileElementRole.BasicPortrait, image.Role);
        Assert.Equal(new Vector2(10f, 20f), image.Position);
        Assert.Equal(new Vector2(400f, 800f), image.Size);
        Assert.Equal(1, image.ZIndex);

        // A zero-size element gets the default box, is clamped into the resolved canvas, and is
        // made visible (it could never have been seen or deliberately hidden).
        var unsized = Assert.IsType<TextProfileElement>(document.Elements[2]);
        Assert.Equal("Never sized", unsized.Text);
        Assert.Equal(new Vector2(ProfileElement.DefaultWidth, ProfileElement.DefaultHeight), unsized.Size);
        Assert.Equal(new Vector2(ProfileDocument.LegacyCanvasWidth - ProfileElement.DefaultWidth, 0f), unsized.Position);
        Assert.True(unsized.Visible);
        Assert.Equal(2, unsized.ZIndex);
    }

    [Fact]
    public void LegacyPlate_VersionZeroAndUnversioned_MaterializeExactlyLikeVersionOne()
    {
        var v1 = MaterializedJson("plate-v1-single-profile.json");

        Assert.Equal(v1, MaterializedJson("plate-v0-single-profile.json"));
        Assert.Equal(v1, MaterializedJson("plate-unversioned-single-profile.json"));
    }

    [Fact]
    public void LegacyPlate_MaterializedSave_IsCurrent_AndStableOnASecondRoundTrip()
    {
        var first = MaterializedJson("plate-v1-single-profile.json");
        var saved = JsonNode.Parse(first)!.AsObject();

        Assert.Equal(ProfileDocument.CurrentSchemaVersion, saved["Version"]!.GetValue<int>());
        Assert.False(saved.ContainsKey("BackgroundAssetId"));
        Assert.False(saved.ContainsKey("BackgroundFitMode"));
        Assert.False(saved.ContainsKey("BackgroundOpacity"));
        Assert.Equal(LegacyData.BackgroundAsset, saved["Background"]!["ImageAssetId"]!.GetValue<Guid>());
        Assert.Equal(ProfileDocument.LegacyCanvasWidth, saved["CanvasWidth"]!.GetValue<float>());

        // Materializing what the first save wrote is a current document: nothing left to repair.
        var second = PlateDocuments.Materialize(saved, out var repairedValues);
        Assert.False(repairedValues);
        Assert.Equal(first, VersionedJson.Serialize(PlateDocuments.ToJson(second)));
    }

    [Fact]
    public async Task LegacyPlate_LoadsReady_OpensClean_AndIsNeverRewrittenByLoading()
    {
        using var fixture = new LibraryFixture();
        var original = ReadLegacy("plate-v1-single-profile.json");
        fixture.WritePlateJson(LegacyPlateId, original);

        var library = await fixture.LoadAsync();
        await fixture.LoadAsync();

        var summary = library.FindPlate(LegacyPlateId);
        Assert.NotNull(summary);
        Assert.Equal(PlateStatus.Ready, summary!.Status);
        Assert.Equal("Default", summary.DisplayName);
        Assert.False(summary.HasUnsupportedElements);
        Assert.DoesNotContain(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) || m.StartsWith("E ", StringComparison.Ordinal));
        Assert.Equal(original, fixture.ReadPlateJson(LegacyPlateId));

        using var harness = await BasicHarness.OpenJsonAsync(original, LegacyPlateId);
        harness.SimulateBasicFrame();
        Assert.False(harness.Session.IsDirty);
        Assert.False(harness.Session.CanUndo);
        Assert.Equal(original, harness.Fixture.ReadPlateJson(LegacyPlateId));
        Assert.Equal(MaterializedJson("plate-v1-single-profile.json"), harness.Json());
    }

    // ---------------------------------------------------------------- bindings

    [Fact]
    public void LegacyBindingV1_MigratesToV2_WithTheActivePlateAmongTheAssociations()
    {
        var result = VersionedJson.Parse<CharacterBinding>(ReadLegacy("binding-v1.json"), PersistenceSchemas.CharacterBinding);

        Assert.Equal(SchemaMigrationOutcome.Migrated, result.Migration.Outcome);
        Assert.Equal(1, result.Migration.OriginalVersion);
        Assert.Equal(CharacterBinding.CurrentVersion, result.Migration.Version);
        var binding = result.Value!;
        Assert.Equal(CharacterBinding.CurrentVersion, binding.Version);
        Assert.Equal(LegacyOwnerContentId, binding.ContentId);
        Assert.Equal(LegacyPlateId, binding.ActivePlateId);
        Assert.Equal([LegacyPlateId], binding.PlateIds);
        Assert.Null(binding.LastKnownCharacterName);
        Assert.Null(binding.LastKnownHomeWorld);
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), binding.CreatedAtUtc);
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), binding.UpdatedAtUtc);
        Assert.Null(binding.ExtensionData);
    }

    [Fact]
    public async Task LegacyBindingV1_BecomesThePlateLibrary_WithThatPlateActive_AndTheOriginalKept()
    {
        using var fixture = new LibraryFixture();
        var plate = ReadLegacy("plate-v1-single-profile.json");
        var binding = ReadLegacy("binding-v1.json");
        fixture.WritePlateJson(LegacyPlateId, plate);
        fixture.WriteBindingJson(LegacyOwnerContentId, binding);

        var library = await fixture.LoadAsync();

        Assert.Equal(LegacyPlateId, library.GetActivePlateId(LegacyOwnerContentId));
        Assert.Equal([LegacyPlateId], library.GetBinding(LegacyOwnerContentId)!.PlateIds);
        Assert.Equal([LegacyPlateId], fixture.ReadLibraryOrder());
        Assert.Equal(plate, fixture.ReadPlateJson(LegacyPlateId));

        var rewritten = fixture.ReadBinding(LegacyOwnerContentId);
        Assert.Equal(CharacterBinding.CurrentVersion, rewritten.GetProperty("Version").GetInt32());
        Assert.Equal(LegacyOwnerContentId, rewritten.GetProperty("ContentId").GetUInt64());
        Assert.Equal(LegacyPlateId, rewritten.GetProperty("ActiveProfileId").GetGuid());
        Assert.Equal([LegacyPlateId], rewritten.GetProperty("ProfileIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal("2025-01-02T03:04:05Z", rewritten.GetProperty("CreatedAtUtc").GetString());

        var backup = Path.Combine(fixture.Paths.MigrationBackupDirectory, "Characters", $"{LegacyOwnerContentId}.json");
        Assert.Equal(binding, File.ReadAllText(backup, Encoding.UTF8));
    }

    // ---------------------------------------------------------------- templates

    [Fact]
    public void LegacyTemplateV1_MaterializesItsEmbeddedDocument_LikeAPlate()
    {
        var raw = JsonNode.Parse(ReadLegacy("template-v1.json"))!.AsObject();
        Assert.Equal(SchemaMigrationOutcome.Current, PersistenceSchemas.Template.Migrate(raw).Outcome);
        Assert.Equal(SchemaMigrationOutcome.Migrated, PersistenceSchemas.ProfileDocument.Migrate(raw["Document"]!.AsObject()).Outcome);

        var template = TemplateDocuments.Materialize(raw);

        Assert.Equal(PlateTemplate.CurrentSchemaVersion, template.Version);
        Assert.Equal(LegacyTemplateId, template.TemplateId);
        Assert.Equal("Legacy Template", template.Name);
        Assert.Equal(TemplateOriginKind.SavedFromPlate, template.Origin.Kind);
        Assert.Null(template.ExtensionData);

        var document = template.Document;
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, document.Version);
        Assert.Equal(0UL, document.OwnerContentId);
        Assert.Equal(ProfileDocument.LegacyCanvasWidth, document.CanvasWidth);
        Assert.Equal(ProfileBackgroundMode.Image, document.Background!.Mode);
        Assert.Equal(ProfileImageFit.Fill, document.Background.ImageFit);
        Assert.Null(document.LegacyBackgroundAssetId);
        var portrait = Assert.IsType<ImageProfileElement>(document.Elements[1]);
        Assert.Equal(ProfileElementRole.BasicPortrait, portrait.Role);
        Assert.Equal(new Vector2(ProfileElement.DefaultWidth, ProfileElement.DefaultHeight), portrait.Size);
        Assert.Equal(new Vector2(10f, 20f), portrait.Position);

        var saved = TemplateDocuments.ToJson(template);
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, saved["Document"]!["Version"]!.GetValue<int>());
        Assert.False(saved["Document"]!.AsObject().ContainsKey("BackgroundAssetId"));
    }

    [Fact]
    public async Task LegacyTemplateV1_LoadsReady_AndIsNeverRewrittenByLoading()
    {
        using var fixture = new TemplateLibraryFixture();
        var original = ReadLegacy("template-v1.json");
        fixture.WriteTemplateJson(LegacyTemplateId, original);

        var templates = await fixture.LoadAsync();

        var summary = templates.FindTemplate(LegacyTemplateId);
        Assert.NotNull(summary);
        Assert.Equal(TemplateStatus.Ready, summary!.Status);
        Assert.Equal(TemplateKind.UserSaved, summary.Kind);
        Assert.Equal("Legacy Template", summary.DisplayName);
        Assert.DoesNotContain(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) || m.StartsWith("E ", StringComparison.Ordinal));
        Assert.Equal(original, fixture.ReadTemplateJson(LegacyTemplateId));

        var document = templates.GetSavedDocument(LegacyTemplateId);
        Assert.NotNull(document);
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, document!.Version);
        Assert.Equal(ProfileDocument.LegacyCanvasHeight, document.CanvasHeight);
        Assert.Contains(document.Elements, e => e is ImageProfileElement { AssetId: var assetId } && assetId == LegacyData.PortraitAsset);

        var created = await templates.InstantiateAsync(LegacyTemplateId, null);
        Assert.Equal(PlateStatus.Ready, fixture.PlateLibrary.FindPlate(created.PlateId)!.Status);
        Assert.Equal(original, fixture.ReadTemplateJson(LegacyTemplateId));
    }

    private static string ReadLegacy(string fileName) => File.ReadAllText(HistoricalFixtures.LegacyPath(fileName), Encoding.UTF8);

    /// <summary>What saving the legacy file as-is would write: migrated, materialized, serialized.</summary>
    private static string MaterializedJson(string fileName)
    {
        var result = VersionedJson.Parse(ReadLegacy(fileName), PersistenceSchemas.ProfileDocument, PlateDocuments.Deserialize);
        Assert.True(result.IsUsable, result.Migration.Error);
        return VersionedJson.Serialize(PlateDocuments.ToJson(PlateDocuments.Materialize(result.Raw!)));
    }
}
