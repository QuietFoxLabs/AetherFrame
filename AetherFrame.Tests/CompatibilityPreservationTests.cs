using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Characters;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// What existing data may count on: every record a released build wrote comes back byte for byte
/// through every writer that can touch it, a Library write changes only the fields it means to,
/// a file from a newer version is never written by any operation, and a migration that fails
/// partway finishes at the next startup with the originals kept.
/// </summary>
public class CompatibilityPreservationTests
{
    public static TheoryData<string> Tags => new(HistoricalFixtures.Tags);

    /// <summary>
    /// Every JSON file of every released build's installation — Plates, bindings, the index, asset
    /// metadata, trashed Plates and Templates — through the raw writer rename and duplicate use
    /// and, where one exists, the typed writer its kind is saved with.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tags))]
    public void EveryRecordOfARelease_RoundTripsByteForByte_ThroughEveryWriter(string tag)
    {
        var root = HistoricalFixtures.InstallDirectory(tag);
        var failures = new List<string>();
        foreach (var file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var text = HistoricalFixtures.ExactText(file);
            var raw = JsonNode.Parse(text)!.AsObject();
            if (HistoricalFixtures.NormalizeLineEndings(VersionedJson.Serialize(raw)) != text)
            {
                failures.Add($"{relative}: raw writer");
            }

            var typed = relative switch
            {
                _ when relative.StartsWith("Characters/", StringComparison.Ordinal) => VersionedJson.Serialize(VersionedJson.Parse<CharacterBinding>(text, PersistenceSchemas.CharacterBinding).Value!),
                _ when relative.StartsWith("Library/", StringComparison.Ordinal) => VersionedJson.Serialize(VersionedJson.Parse<PlateLibraryState>(text, PersistenceSchemas.PlateLibrary).Value!),
                _ when relative.StartsWith("asset-metadata/", StringComparison.Ordinal) => VersionedJson.Serialize(VersionedJson.Parse<AssetMetadata>(text, PersistenceSchemas.AssetMetadata).Value!),
                _ when relative.StartsWith("Profiles/", StringComparison.Ordinal) || relative.StartsWith("Trash/Plates/", StringComparison.Ordinal)
                    => VersionedJson.Serialize(PlateDocuments.ToJson(PlateDocuments.Materialize(raw))),
                _ when relative.StartsWith("Templates/", StringComparison.Ordinal) || relative.StartsWith("Trash/Templates/", StringComparison.Ordinal)
                    => VersionedJson.Serialize(TemplateDocuments.ToJson(TemplateDocuments.Materialize(raw))),
                _ => null,
            };

            if (typed is not null && HistoricalFixtures.NormalizeLineEndings(typed) != text)
            {
                failures.Add($"{relative}: typed writer");
            }
        }

        Assert.Empty(failures);
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public async Task RenamingEveryPlateOfARelease_ChangesOnlyItsNameAndModifiedTime(string tag)
    {
        var manifest = HistoricalFixtures.ReadManifest(tag);
        using var fixture = new LibraryFixture();
        HistoricalFixtures.CopyInstall(tag, fixture.Root);
        var library = await fixture.LoadAsync();

        foreach (var (name, plateId) in manifest.LivePlates)
        {
            var path = fixture.Paths.GetPlatePath(plateId);
            var before = JsonNode.Parse(HistoricalFixtures.ExactText(path))!.AsObject();
            fixture.Clock.Tick();

            await library.RenamePlateAsync(plateId, "Renamed " + name);

            var after = JsonNode.Parse(HistoricalFixtures.ExactText(path))!.AsObject();
            Assert.Equal("Renamed " + name, after["Name"]!.GetValue<string>());
            Assert.Equal(fixture.Clock.Now, after["UpdatedAtUtc"]!.GetValue<DateTime>());
            after["Name"] = before["Name"]!.DeepClone();
            after["UpdatedAtUtc"] = before["UpdatedAtUtc"]!.DeepClone();
            Assert.True(JsonNode.DeepEquals(before, after), $"{name}: renaming changed more than the name and modified time.");
        }

        var reloaded = await fixture.LoadAsync();
        Assert.All(manifest.LivePlates, p => Assert.Equal(PlateStatus.Ready, reloaded.FindPlate(p.Value)!.Status));
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public async Task RewritingABindingOfARelease_ChangesOnlyItsModifiedTime(string tag)
    {
        var manifest = HistoricalFixtures.ReadManifest(tag);
        using var fixture = new LibraryFixture();
        HistoricalFixtures.CopyInstall(tag, fixture.Root);
        var path = fixture.Paths.GetBindingPath(manifest.Bob);
        var before = JsonNode.Parse(HistoricalFixtures.ExactText(path))!.AsObject();
        var library = await fixture.LoadAsync();

        await library.SetActivePlateAsync(new CharacterContext(manifest.Bob, before["LastKnownCharacterName"]?.GetValue<string>(), before["LastKnownHomeWorld"]?.GetValue<string>()), manifest.ExpectedActiveBob);

        var after = JsonNode.Parse(HistoricalFixtures.ExactText(path))!.AsObject();
        after["UpdatedAtUtc"] = before["UpdatedAtUtc"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(before, after));
    }

    [Fact]
    public async Task NewerVersionTemplate_CantBeRenamedOrDuplicated_AndItsFileNeverChanges()
    {
        using var fixture = new TemplateLibraryFixture();
        var templateId = Guid.NewGuid();
        var json = TemplateSamples.Envelope(templateId, "Future", "{}", version: 999);
        fixture.WriteTemplateJson(templateId, json);
        var templates = await fixture.LoadAsync();

        await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.RenameTemplateAsync(templateId, "Renamed"));
        await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.DuplicateTemplateAsync(templateId));

        Assert.Equal(json, fixture.ReadTemplateJson(templateId));
        Assert.Single(Directory.GetFiles(fixture.Paths.TemplatesDirectory));
    }

    [Fact]
    public async Task NewerVersionBinding_IsNeverWritten_ByDuplicatingOrDeletingItsPlates()
    {
        using var fixture = new LibraryFixture();
        var seeded = await fixture.LoadAsync();
        var first = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "First");
        var second = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "Second");
        var future = $$"""{ "Version": 7, "ContentId": 1001, "ActiveProfileId": "{{first.PlateId}}", "ProfileIds": ["{{first.PlateId}}", "{{second.PlateId}}"], "Future": 1 }""";
        fixture.WriteBindingJson(Characters.Alice.ContentId, future);
        var library = await fixture.LoadAsync();

        await library.DuplicatePlateAsync(second.PlateId);
        await library.DeletePlateAsync(first.PlateId);

        Assert.Equal(future, fixture.ReadBindingJson(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task NewerVersionIndex_IsNeverWritten_ByAMove()
    {
        using var fixture = new LibraryFixture();
        var seeded = await fixture.LoadAsync();
        var first = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "First");
        fixture.Clock.Tick();
        var second = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "Second");
        var future = $$"""{ "Version": 5, "OrderedPlateIds": ["{{second.PlateId}}", "{{first.PlateId}}"], "Future": true }""";
        fixture.WriteLibraryJson(future);
        var library = await fixture.LoadAsync();

        await library.MovePlateAsync(first.PlateId, second.PlateId, placeAfter: false);

        Assert.Equal([first.PlateId, second.PlateId], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal(future, File.ReadAllText(fixture.Paths.LibraryFile));
    }

    [Fact]
    public async Task CurrentVersionIndex_KeepsFieldsItDoesntKnow_ThroughAMove()
    {
        using var fixture = new LibraryFixture();
        var seeded = await fixture.LoadAsync();
        var first = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "First");
        fixture.Clock.Tick();
        var second = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "Second");
        fixture.WriteLibraryJson($$"""{ "Version": 1, "OrderedPlateIds": ["{{second.PlateId}}", "{{first.PlateId}}"], "Pinned": ["{{first.PlateId}}"] }""");
        var library = await fixture.LoadAsync();

        await library.MovePlateAsync(first.PlateId, second.PlateId, placeAfter: false);

        var index = JsonNode.Parse(File.ReadAllText(fixture.Paths.LibraryFile))!.AsObject();
        Assert.Equal(first.PlateId.ToString(), index["Pinned"]![0]!.GetValue<string>());
        Assert.Equal([first.PlateId, second.PlateId], fixture.ReadLibraryOrder());
    }

    [Fact]
    public async Task NewerVersionPlate_IsDeletedIntact_AndLeavesTheImageScanIncomplete()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        var json = $$"""{ "Version": 99, "ProfileId": "{{plateId}}", "Name": "From the future" }""";
        fixture.WritePlateJson(plateId, json);
        var library = await fixture.LoadAsync();

        await library.DeletePlateAsync(plateId);

        Assert.Equal(json, File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Paths.PlateTrashDirectory))));
        Assert.False((await library.ScanAssetReferencesAsync()).IsComplete);
    }

    [Fact]
    public async Task MigrationFailingPartway_FinishesAtTheNextStartup_AndItsBackupsKeepTheOriginals()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        ulong[] owners = [4242, 4343, 4444];
        var originals = new Dictionary<ulong, string>();
        foreach (var owner in owners)
        {
            var plateId = Guid.NewGuid();
            fixture.WritePlateJson(plateId, LegacyData.VersionOneDocument(plateId, owner));
            originals[owner] = LegacyData.VersionOneBinding(owner, plateId, plateId);
            fixture.WriteBindingJson(owner, originals[owner]);
        }

        store.FailWriteAfter = 1;
        var first = await fixture.LoadAsync();

        Assert.True(first.IsLoaded);
        Assert.False(File.Exists(fixture.Paths.LibraryFile));
        var versions = owners.Select(o => fixture.ReadBinding(o).GetProperty("Version").GetInt32()).ToList();
        Assert.Equal(1, versions.Count(v => v == 2));

        store.FailWriteAfter = null;
        var second = await fixture.LoadAsync();

        Assert.All(owners, o => Assert.Equal(2, fixture.ReadBinding(o).GetProperty("Version").GetInt32()));
        Assert.All(owners, o => Assert.NotNull(second.GetActivePlateId(o)));
        Assert.Equal(3, fixture.ReadLibraryOrder().Count);
        Assert.All(owners, o => Assert.Equal(originals[o],
            File.ReadAllText(Path.Combine(fixture.Paths.MigrationBackupDirectory, "Characters", $"{o}.json"), Encoding.UTF8)));

        // Idempotent from here: a third startup writes nothing, not even the same bytes again (in
        // game every write also replaces the file's backup row).
        var snapshot = HistoricalFixtures.Snapshot(fixture.Root);
        var writes = new List<string>();
        store.FailWrite = path =>
        {
            writes.Add(Path.GetFileName(path));
            return false;
        };
        await fixture.LoadAsync();
        Assert.Empty(writes);
        Assert.Equal(snapshot, HistoricalFixtures.Snapshot(fixture.Root));
    }
}
