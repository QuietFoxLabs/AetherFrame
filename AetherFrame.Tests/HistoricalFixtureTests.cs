using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Characters;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The checked-in <c>Fixtures/</c> directory (copied next to the test binaries): one installation
/// per tagged release, written by that release's OWN persistence code — its PlateLibraryService,
/// TemplateLibraryService, editor sessions and package exporter — plus the pre-release shapes under
/// <c>legacy/</c>. Every file is byte for byte what that build wrote; nothing here is synthesized by
/// current code, so a change that moves both writer and reader still has to face these.
/// </summary>
internal static class HistoricalFixtures
{
    /// <summary>Every tagged release with a fixture set, oldest first.</summary>
    internal static readonly string[] Tags = ["v0.1.0", "v0.1.1", "v0.1.2", "v0.1.3", "v0.1.4"];

    internal static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    internal static string LegacyDirectory => Path.Combine(Root, "legacy");

    internal static string TagDirectory(string tag) => Path.Combine(Root, tag);

    internal static string InstallDirectory(string tag) => Path.Combine(TagDirectory(tag), "install");

    internal static string ExportPath(string tag, string fileName) => Path.Combine(TagDirectory(tag), "exports", fileName);

    internal static string LegacyPath(string fileName) => Path.Combine(LegacyDirectory, fileName);

    internal static HistoricalManifest ReadManifest(string tag) =>
        HistoricalManifest.Parse(File.ReadAllText(Path.Combine(TagDirectory(tag), "manifest.json"), Encoding.UTF8));

    /// <summary>Copies a tag's installation into <paramref name="destination"/> (a fresh Library root).</summary>
    internal static void CopyInstall(string tag, string destination)
    {
        var source = InstallDirectory(tag);
        Assert.True(Directory.Exists(source), $"Fixture set {tag} is missing from the test output ({source}).");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    /// <summary>The bytes of every file under <paramref name="root"/>, hashed, by relative path.</summary>
    internal static Dictionary<string, string> Snapshot(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(root, p), p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));

    /// <summary>
    /// A file's exact content for byte-level comparison: decoded without dropping a byte-order mark
    /// (no build writes one), with only the line terminator normalized — the JSON writer follows the
    /// platform's, so a build on Windows separates lines with CRLF where the host that generated the
    /// fixtures used LF, and nothing else about the bytes may differ.
    /// </summary>
    internal static string ExactText(string path) => NormalizeLineEndings(Encoding.UTF8.GetString(File.ReadAllBytes(path)));

    internal static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}

/// <summary>What the emitting build recorded about its fixture set (ids, owners, expectations).</summary>
internal sealed class HistoricalManifest
{
    private HistoricalManifest(JsonObject json)
    {
        Tag = json["tag"]!.GetValue<string>();
        Owner = json["owner"]!.GetValue<ulong>();
        Bob = json["bob"]!.GetValue<ulong>();
        Plates = json["plates"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<Guid>());
        Assets = json["assets"]!.AsArray().Select(n => n!.GetValue<Guid>()).ToList();
        ComponentCount = json["componentCount"]!.GetValue<int>();
        Deleted = json["deleted"]!.GetValue<Guid>();
        ExpectedActiveOwner = json["expectedActiveOwner"]!.GetValue<Guid>();
        ExpectedActiveBob = json["expectedActiveBob"]!.GetValue<Guid>();
        Template = json["template"]!.GetValue<Guid>();
        DeletedTemplate = json["deletedTemplate"]!.GetValue<Guid>();
        Exports = json["exports"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
    }

    internal string Tag { get; }

    /// <summary>The character most Plates were created for (see <see cref="PackageFixture.Owner"/>).</summary>
    internal ulong Owner { get; }

    internal ulong Bob { get; }

    /// <summary>Every Plate the build wrote, by the emitter's name for it.</summary>
    internal IReadOnlyDictionary<string, Guid> Plates { get; }

    internal IReadOnlyList<Guid> Assets { get; }

    internal int ComponentCount { get; }

    /// <summary>A Plate the build deleted (in the trash, absent from the Library).</summary>
    internal Guid Deleted { get; }

    internal Guid ExpectedActiveOwner { get; }

    internal Guid ExpectedActiveBob { get; }

    /// <summary>The one saved (user) Template.</summary>
    internal Guid Template { get; }

    internal Guid DeletedTemplate { get; }

    /// <summary>Packages the build exported, by the emitter's name for them.</summary>
    internal IReadOnlyDictionary<string, string> Exports { get; }

    /// <summary>Every Plate that should be in the Library (the deleted one excluded).</summary>
    internal IEnumerable<KeyValuePair<string, Guid>> LivePlates => Plates.Where(p => p.Value != Deleted);

    internal static HistoricalManifest Parse(string json) => new(JsonNode.Parse(json)!.AsObject());
}

/// <summary>
/// Every tagged release's files, loaded by the current build: nothing is rewritten, repaired,
/// warned about, or dirtied on open, and everything the old build wrote reads and re-saves exactly.
/// A failure here means a change to the models, the repairs or the JSON shape would silently alter
/// a tester's existing installation.
/// </summary>
public class HistoricalFixtureTests
{
    public static TheoryData<string> Tags => new(HistoricalFixtures.Tags);

    [Theory]
    [MemberData(nameof(Tags))]
    public void EveryTag_EveryRecordParsesAsTheCurrentSchema(string tag)
    {
        var manifest = HistoricalFixtures.ReadManifest(tag);
        var paths = new PlateStoragePaths(HistoricalFixtures.InstallDirectory(tag));

        var plateFiles = Directory.GetFiles(paths.PlatesDirectory, "*.json").Concat(Directory.GetFiles(paths.PlateTrashDirectory, "*.json")).ToList();
        Assert.Equal(manifest.Plates.Count, plateFiles.Count);
        foreach (var file in plateFiles)
        {
            var result = VersionedJson.Parse(File.ReadAllText(file, Encoding.UTF8), PersistenceSchemas.ProfileDocument, PlateDocuments.Deserialize);
            Assert.True(result.IsUsable, $"{Path.GetFileName(file)}: {result.Migration.Error}");
            Assert.Equal(SchemaMigrationOutcome.Current, result.Migration.Outcome);
            Assert.False(result.Value!.HasUnsupportedElements, Path.GetFileName(file));
        }

        var bindingFiles = Directory.GetFiles(paths.CharactersDirectory, "*.json");
        Assert.Equal(2, bindingFiles.Length);
        foreach (var file in bindingFiles)
        {
            var result = VersionedJson.Parse<CharacterBinding>(File.ReadAllText(file, Encoding.UTF8), PersistenceSchemas.CharacterBinding);
            Assert.True(result.IsUsable, $"{Path.GetFileName(file)}: {result.Migration.Error}");
            Assert.Equal(SchemaMigrationOutcome.Current, result.Migration.Outcome);
        }

        var index = VersionedJson.Parse<Domain.Plates.PlateLibraryState>(File.ReadAllText(paths.LibraryFile, Encoding.UTF8), PersistenceSchemas.PlateLibrary);
        Assert.True(index.IsUsable, index.Migration.Error);
        Assert.Equal(SchemaMigrationOutcome.Current, index.Migration.Outcome);
        Assert.Equal(manifest.LivePlates.Select(p => p.Value).ToHashSet(), index.Value!.OrderedPlateIds.ToHashSet());

        var templateFiles = Directory.GetFiles(paths.TemplatesDirectory, "*.json").Concat(Directory.GetFiles(paths.TemplateTrashDirectory, "*.json")).ToList();
        Assert.Equal(2, templateFiles.Count);
        foreach (var file in templateFiles)
        {
            var raw = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8))!.AsObject();
            Assert.Equal(SchemaMigrationOutcome.Current, PersistenceSchemas.Template.Migrate(raw).Outcome);
            Assert.Equal(SchemaMigrationOutcome.Current, PersistenceSchemas.ProfileDocument.Migrate(raw["Document"]!.AsObject()).Outcome);
            Assert.NotNull(TemplateDocuments.Deserialize(raw));
        }

        var metadata = new AssetMetadataStore(paths.AssetMetadataDirectory);
        var assets = new AssetStorageService(paths.AssetsDirectory, paths.AssetStagingDirectory, metadata);
        Assert.Equal(6, manifest.Assets.Count);
        foreach (var assetId in manifest.Assets)
        {
            var sidecar = metadata.TryLoad(assetId);
            Assert.NotNull(sidecar);
            Assert.Equal(AssetMetadata.CurrentVersion, sidecar!.Version);
            var assetPath = assets.ResolveAssetPath(assetId);
            Assert.NotNull(assetPath);
            Assert.Equal(sidecar.Sha256, AssetMetadataStore.ComputeSha256(assetPath!));
        }
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public async Task EveryTag_LoadsReady_RewritesNothing_AndLogsNoWarnings(string tag)
    {
        var manifest = HistoricalFixtures.ReadManifest(tag);
        using var fixture = new LibraryFixture();
        HistoricalFixtures.CopyInstall(tag, fixture.Root);
        var before = HistoricalFixtures.Snapshot(fixture.Root);

        var library = await fixture.LoadAsync();

        Assert.Equal(before, HistoricalFixtures.Snapshot(fixture.Root));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) || m.StartsWith("E ", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.Contains("building the Plate Library", StringComparison.Ordinal));
        Assert.Equal(manifest.LivePlates.Count(), library.GetOrderedPlates().Count);
        foreach (var (name, plateId) in manifest.Plates)
        {
            var summary = library.FindPlate(plateId);
            if (plateId == manifest.Deleted)
            {
                Assert.Null(summary);
                continue;
            }

            Assert.NotNull(summary);
            Assert.True(summary!.Status == PlateStatus.Ready, $"{name}: {summary.Status} {summary.Problem}");
            Assert.False(summary.HasUnsupportedElements, name);
            Assert.NotNull(library.GetSavedDocument(plateId));
        }

        // The limits Plate holds every element the build allowed and every built-in Component it
        // shipped, each read as a Component (none set aside as malformed).
        var limits = library.GetSavedDocument(manifest.Plates["blank"])!;
        Assert.Equal(Domain.Profiles.ProfileDocument.MaxElementCount, limits.Elements.Count);
        Assert.Equal(manifest.ComponentCount, limits.Components!.Count);
        Assert.All(manifest.LivePlates, p => Assert.Null(library.GetSavedDocument(p.Value)!.UnrecognizedComponents));

        var templates = new TemplateLibraryService(fixture.Paths, fixture.Store, library, fixture.Log, () => fixture.Clock.Now);
        await templates.InitializeAsync();

        Assert.Equal(before, HistoricalFixtures.Snapshot(fixture.Root));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) || m.StartsWith("E ", StringComparison.Ordinal));
        var template = templates.FindTemplate(manifest.Template);
        Assert.NotNull(template);
        Assert.Equal(TemplateStatus.Ready, template!.Status);
        Assert.Equal(TemplateKind.UserSaved, template.Kind);
        Assert.Equal("Rich Template ✦ renamed", template.DisplayName);
        Assert.Null(templates.FindTemplate(manifest.DeletedTemplate));
        Assert.Single(templates.GetOrderedTemplates(), t => t.Kind == TemplateKind.UserSaved);
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public async Task EveryTag_BindingsAndActivePlatesArePreserved(string tag)
    {
        var manifest = HistoricalFixtures.ReadManifest(tag);
        using var fixture = new LibraryFixture();
        HistoricalFixtures.CopyInstall(tag, fixture.Root);
        var bindingsBefore = Directory.GetFiles(fixture.Paths.CharactersDirectory).ToDictionary(p => Path.GetFileName(p), HistoricalFixtures.ExactText);

        var library = await fixture.LoadAsync();

        Assert.Equal(manifest.ExpectedActiveOwner, library.GetActivePlateId(manifest.Owner));
        Assert.Equal(manifest.ExpectedActiveBob, library.GetActivePlateId(manifest.Bob));

        var owner = library.GetBinding(manifest.Owner);
        Assert.NotNull(owner);
        Assert.Equal(CharacterBinding.CurrentVersion, owner!.Version);
        Assert.Equal(PackageFixture.PrivateCharacterName, owner.LastKnownCharacterName);
        Assert.Equal(PackageFixture.PrivateWorld, owner.LastKnownHomeWorld);
        Assert.DoesNotContain(manifest.Deleted, owner.PlateIds);

        // The emitter created its Plates for the owner except the two edited through the editor
        // sessions and the one made from the user Template (created logged out).
        var expectedOwned = manifest.LivePlates.Where(p => p.Key is not ("from-user-template" or "basic-session" or "advanced-session")).Select(p => p.Value).ToHashSet();
        Assert.Equal(expectedOwned, owner.PlateIds.ToHashSet());

        var bob = library.GetBinding(manifest.Bob);
        Assert.NotNull(bob);
        Assert.Equal([manifest.ExpectedActiveBob], bob!.PlateIds);
        Assert.Equal("Bob Sample", bob.LastKnownCharacterName);

        var ownerSummary = library.FindPlate(manifest.ExpectedActiveOwner)!;
        Assert.Contains(manifest.Owner, ownerSummary.ActiveForContentIds);
        Assert.Contains(PackageFixture.PrivateCharacterName, ownerSummary.CharacterNames);

        // Loading is read-only for bindings that need no migration.
        Assert.Equal(bindingsBefore, Directory.GetFiles(fixture.Paths.CharactersDirectory).ToDictionary(p => Path.GetFileName(p), HistoricalFixtures.ExactText));
        Assert.False(Directory.Exists(fixture.Paths.MigrationBackupDirectory));
        Assert.False(Directory.Exists(fixture.Paths.RecoveryDirectory));
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public async Task EveryTag_OpenSaveIsByteIdenticalAndIdempotent(string tag)
    {
        var manifest = HistoricalFixtures.ReadManifest(tag);
        using var fixture = new LibraryFixture();
        HistoricalFixtures.CopyInstall(tag, fixture.Root);
        var library = await fixture.LoadAsync();

        foreach (var (name, plateId) in manifest.LivePlates)
        {
            var path = fixture.Paths.GetPlatePath(plateId);
            var original = HistoricalFixtures.ExactText(path);

            // The raw-JSON path the Library uses for rename and duplicate.
            var raw = JsonNode.Parse(original)!.AsObject();
            Assert.Equal(original, HistoricalFixtures.NormalizeLineEndings(VersionedJson.Serialize(PlateDocuments.ToJson(PlateDocuments.Materialize(raw)))));

            // The model path every editor save takes.
            await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));
            var afterFirstSave = HistoricalFixtures.ExactText(path);
            Assert.True(original == afterFirstSave, $"{name}: the first save after opening rewrote the file.");

            await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));
            Assert.True(afterFirstSave == HistoricalFixtures.ExactText(path), $"{name}: saving again changed the file.");
        }

        var templatePath = fixture.Paths.GetTemplatePath(manifest.Template);
        var templateOriginal = HistoricalFixtures.ExactText(templatePath);
        var templateRaw = JsonNode.Parse(templateOriginal)!.AsObject();
        Assert.Equal(templateOriginal, HistoricalFixtures.NormalizeLineEndings(VersionedJson.Serialize(TemplateDocuments.ToJson(TemplateDocuments.Materialize(templateRaw)))));
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public async Task EveryTag_IsNeverDirtyOnOpen(string tag)
    {
        var manifest = HistoricalFixtures.ReadManifest(tag);
        var paths = new PlateStoragePaths(HistoricalFixtures.InstallDirectory(tag));

        foreach (var (name, plateId) in manifest.LivePlates)
        {
            var json = File.ReadAllText(paths.GetPlatePath(plateId), Encoding.UTF8);
            using var harness = await BasicHarness.OpenJsonAsync(json, plateId);
            var opened = harness.Json();

            for (var i = 0; i < 3; i++)
            {
                harness.SimulateBasicFrame();
                harness.Surfaces.Show(EditorSurfaceKind.Advanced);
                harness.Surfaces.Show(EditorSurfaceKind.Basic);
            }

            Assert.False(harness.Session.IsDirty, $"{name}: dirty after opening.");
            Assert.False(harness.Session.CanUndo, $"{name}: an undo step was recorded by opening.");
            Assert.True(opened == harness.Json(), $"{name}: frames changed the in-memory document.");
            Assert.True(json == harness.Fixture.ReadPlateJson(plateId), $"{name}: opening touched the file.");

            // No in-memory legacy repair applies: what the editor holds is exactly what the file holds.
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(opened)), $"{name}: the materialized document differs from the file.");
        }
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public async Task EveryTag_AssetReferenceScansAreComplete_AndCoverEveryHistoricalAsset(string tag)
    {
        var manifest = HistoricalFixtures.ReadManifest(tag);
        using var fixture = new LibraryFixture();
        HistoricalFixtures.CopyInstall(tag, fixture.Root);
        var library = await fixture.LoadAsync();
        var templates = new TemplateLibraryService(fixture.Paths, fixture.Store, library, fixture.Log, () => fixture.Clock.Now);
        await templates.InitializeAsync();

        var plateScan = await library.ScanAssetReferencesAsync();
        var templateScan = await templates.ScanAssetReferencesAsync();

        Assert.True(plateScan.IsComplete, string.Join(" | ", plateScan.Problems));
        Assert.True(templateScan.IsComplete, string.Join(" | ", templateScan.Problems));
        Assert.All(manifest.Assets, assetId => Assert.Contains(assetId, plateScan.ReferencedAssetIds));
        Assert.Contains(templateScan.ReferencedAssetIds, assetId => manifest.Assets.Contains(assetId));
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public async Task EveryTag_UserTemplate_InstantiatesAndRenames(string tag)
    {
        var manifest = HistoricalFixtures.ReadManifest(tag);
        using var fixture = new LibraryFixture();
        HistoricalFixtures.CopyInstall(tag, fixture.Root);
        var library = await fixture.LoadAsync();
        var templates = new TemplateLibraryService(fixture.Paths, fixture.Store, library, fixture.Log, () => fixture.Clock.Now);
        await templates.InitializeAsync();
        var templatePath = fixture.Paths.GetTemplatePath(manifest.Template);
        var before = JsonNode.Parse(HistoricalFixtures.ExactText(templatePath))!.AsObject();

        var created = await templates.InstantiateAsync(manifest.Template, null);
        await templates.RenameTemplateAsync(manifest.Template, "renamed by the current build");

        // The new Plate carries the Template's creative content exactly, under its own identity.
        Assert.Equal(PlateStatus.Ready, library.FindPlate(created.PlateId)!.Status);
        Assert.NotEqual(manifest.Template, created.PlateId);
        var instantiated = JsonNode.Parse(fixture.ReadPlateJson(created.PlateId))!.AsObject();
        Assert.True(JsonNode.DeepEquals(before["Document"]!["Elements"], instantiated["Elements"]));
        Assert.True(JsonNode.DeepEquals(before["Document"]!["Background"], instantiated["Background"]));
        Assert.True(JsonNode.DeepEquals(before["Document"]!["Components"], instantiated["Components"]));

        var after = JsonNode.Parse(HistoricalFixtures.ExactText(templatePath))!.AsObject();
        Assert.Equal("renamed by the current build", templates.FindTemplate(manifest.Template)!.DisplayName);
        Assert.Equal("renamed by the current build", after["Name"]!.GetValue<string>());
        after["Name"] = before["Name"]!.DeepClone();
        after["UpdatedAtUtc"] = before["UpdatedAtUtc"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(before, after), "Renaming a Template touched more than its name and modified time.");
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public async Task EveryTag_PackagesImportIntoCurrentBuild(string tag)
    {
        var manifest = HistoricalFixtures.ReadManifest(tag);
        using var fixture = new PackageFixture();
        HistoricalFixtures.CopyInstall(tag, fixture.Library.Root);
        var (library, packages) = await fixture.LoadAsync();
        Assert.Equal(3, manifest.Exports.Count);

        foreach (var (name, fileName) in manifest.Exports)
        {
            var packagePath = HistoricalFixtures.ExportPath(tag, fileName);
            Assert.True(File.Exists(packagePath), $"{name}: {fileName} is missing from the fixture set.");

            using var staged = packages.Inspect(packagePath);
            Assert.True(staged.CanImport, $"{name}: {staged.Compatibility}; {string.Join(" | ", staged.Diagnostics.Errors)}");
            Assert.Equal(PackageCompatibility.Supported, staged.Compatibility);
            Assert.Empty(staged.Diagnostics.Warnings);

            var result = await packages.ImportAsync(staged);
            Assert.True(result.Succeeded, $"{name}: {result.Error}");
            Assert.Equal(PlateStatus.Ready, library.FindPlate(result.PlateId)!.Status);

            var path = fixture.Paths.GetPlatePath(result.PlateId);
            var imported = HistoricalFixtures.ExactText(path);
            using (var harness = await BasicHarness.OpenJsonAsync(imported, result.PlateId))
            {
                var opened = harness.Json();
                harness.SimulateBasicFrame();
                harness.Surfaces.Show(EditorSurfaceKind.Advanced);
                harness.SimulateBasicFrame();
                Assert.False(harness.Session.IsDirty, $"{name}: the imported Plate is dirty on open.");
                Assert.False(harness.Session.CanUndo, name);
                Assert.True(opened == harness.Json(), $"{name}: frames changed the imported Plate.");
            }

            // The importer writes the package's own property order; a save re-serializes the model,
            // which may order the same content differently but never changes it — and is then stable.
            await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(result.PlateId));
            var afterFirstSave = HistoricalFixtures.ExactText(path);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(imported), JsonNode.Parse(afterFirstSave)), $"{name}: saving the imported Plate changed its content.");
            await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(result.PlateId));
            Assert.True(afterFirstSave == HistoricalFixtures.ExactText(path), $"{name}: saving the imported Plate again changed the file.");
        }

        Assert.True(fixture.StagingIsEmpty);
        Assert.Equal(manifest.LivePlates.Count() + 3, library.GetOrderedPlates().Count);
    }
}
