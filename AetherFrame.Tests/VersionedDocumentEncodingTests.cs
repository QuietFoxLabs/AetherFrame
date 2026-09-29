using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Characters;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Existing documents load exactly as they always have, whatever encoding their text is in. Every
/// historical fixture (the pre-release shapes under <c>Fixtures/legacy</c> and every tagged
/// release's installation), re-encoded with a byte order mark as UTF-8, UTF-16 (either byte order)
/// or UTF-32 (either byte order), loads with the same status, names, migrated schema versions, character associations,
/// Plate order and Templates as its ASCII original, and saves to the same bytes. A U+FFFD that a
/// file encodes validly is ordinary text: it is read from the file itself, never from an older
/// backup copy, and nothing is kept in Recovery, at load or at the next write. A version-1
/// binding's pre-migration backup keeps its exact bytes whatever they are.
///
/// <para>For Templates: bytes that aren't valid text read as U+FFFD from the file itself (not
/// its backup), and the file's original bytes go to Recovery before the first write over it,
/// which is refused, leaving the file unchanged, while that copy can't be made. A Template saved
/// by a newer version is read from the file and never written, whatever its text holds.</para>
/// </summary>
public class VersionedDocumentEncodingTests
{
    private const string OlderBackupName = "Older backup copy";
    private const ulong LegacyOwner = 4242;

    private static readonly Guid LegacyPlateId = Guid.Parse("0f0f0f0f-1111-4111-8111-000000000101");
    private static readonly Guid LegacyTemplateId = Guid.Parse("0f0f0f0f-2222-4222-8222-000000000201");

    /// <summary>The pre-release single-profile documents; each is loaded with the legacy binding and Template beside it.</summary>
    private static readonly string[] LegacyPlates = ["plate-unversioned-single-profile.json", "plate-v0-single-profile.json", "plate-v1-single-profile.json"];

    private static readonly string[] ByteOrderMarkEncodings = ["utf8-bom", "utf16-bom", "utf16be-bom", "utf32-bom", "utf32be-bom"];

    public static TheoryData<string> Scenes => new(SceneNames());

    public static TheoryData<string, string> ScenesAndEncodings
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var scene in SceneNames())
            {
                foreach (var encoding in ByteOrderMarkEncodings)
                {
                    data.Add(scene, encoding);
                }
            }

            return data;
        }
    }

    [Fact]
    public void EveryHistoricalFixture_IsPartOfAScene()
    {
        Assert.Equal(
            LegacyPlates.Append("binding-v1.json").Append("template-v1.json").Order(StringComparer.Ordinal),
            Directory.GetFiles(HistoricalFixtures.LegacyDirectory).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal));
        Assert.Equal(
            HistoricalFixtures.Tags,
            Directory.GetDirectories(HistoricalFixtures.Root).Select(p => Path.GetFileName(p)!).Where(n => n != "legacy").Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ScenesAndEncodings))]
    public async Task HistoricalFixture_WithAByteOrderMark_LoadsAndSavesExactlyAsItsAsciiOriginal(string scene, string encoding)
    {
        using var ascii = await LoadSceneAsync(scene, rewrite: null);
        using var encoded = await LoadSceneAsync(scene, text => Encode(text, encoding));

        Assert.All(ascii.Library.GetOrderedPlates(), p => Assert.Equal(PlateStatus.Ready, p.Status));
        Assert.All(ascii.Templates.GetOrderedTemplates(), t => Assert.Equal(TemplateStatus.Ready, t.Status));

        // Every backup row is older and named so: nothing on either side came from one.
        Assert.DoesNotContain(OlderBackupName, Names(ascii));
        Assert.DoesNotContain(OlderBackupName, Names(encoded));
        Assert.Equal(Describe(ascii), Describe(encoded));
        Assert.Equal(ascii.Reads, encoded.Reads);
        Assert.Equal(ascii.ChangedFiles, encoded.ChangedFiles);
        Assert.True(LibraryFiles.RecoveryIsEmpty(encoded.Fixture.Paths));

        await WriteEveryDocumentAsync(ascii);
        await WriteEveryDocumentAsync(encoded);

        Assert.True(LibraryFiles.RecoveryIsEmpty(encoded.Fixture.Paths));
        Assert.All(DocumentFiles(encoded.Fixture.Paths), p => Assert.True(Ascii.IsValid(File.ReadAllBytes(p)), p));
        Assert.Equal(Fingerprints(ascii.Fixture), Fingerprints(encoded.Fixture));
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public async Task HistoricalFixture_WithALiteralReplacementCharacterInItsNames_LoadsFromTheFileItself(string scene)
    {
        // The same names, the character written as a JSON escape: pure ASCII, the plainest file there is.
        using var escaped = await LoadSceneAsync(scene, text => Encoding.ASCII.GetBytes(PrefixNames(text, "\\uFFFD ")));
        using var literal = await LoadSceneAsync(scene, text => Encoding.UTF8.GetBytes(PrefixNames(text, "\uFFFD ")));

        Assert.NotEmpty(Names(literal));
        Assert.All(Names(literal), name => Assert.StartsWith("\uFFFD ", name, StringComparison.Ordinal));
        Assert.Equal(Describe(escaped), Describe(literal));
        Assert.Equal(escaped.Reads, literal.Reads);
        Assert.Equal(escaped.ChangedFiles, literal.ChangedFiles);
        Assert.True(LibraryFiles.RecoveryIsEmpty(literal.Fixture.Paths));

        await WriteEveryDocumentAsync(escaped);
        await WriteEveryDocumentAsync(literal);

        Assert.True(LibraryFiles.RecoveryIsEmpty(literal.Fixture.Paths));
        Assert.Equal(Fingerprints(escaped.Fixture), Fingerprints(literal.Fixture));
    }

    [Theory]
    [InlineData("utf8", "non-ascii")]
    [InlineData("utf8", "replacement")]
    [InlineData("utf8-bom", "ascii")]
    [InlineData("utf8-bom", "replacement")]
    [InlineData("utf16-bom", "ascii")]
    [InlineData("utf16-bom", "replacement")]
    [InlineData("utf16be-bom", "replacement")]
    [InlineData("utf32-bom", "replacement")]
    [InlineData("utf32be-bom", "replacement")]
    public async Task LegacyBinding_WithNonAsciiTextOrAByteOrderMark_IsBackedUpByteForByte_AndKeepsItsPlate(string encoding, string noteKind)
    {
        var note = noteKind switch
        {
            "ascii" => "Plain note",
            "non-ascii" => "Ælfwyn ✦ 🌸",
            _ => "Ælfwyn \uFFFD ✦ 🌸",
        };
        using var fixture = new LibraryFixture();
        fixture.WritePlateJson(LegacyPlateId, File.ReadAllText(HistoricalFixtures.LegacyPath("plate-v1-single-profile.json"), Encoding.UTF8));
        var legacy = File.ReadAllText(HistoricalFixtures.LegacyPath("binding-v1.json"), Encoding.UTF8);
        var withNote = legacy.Replace("\"Version\": 1,", $"\"Version\": 1,\n  \"Note\": \"{note}\",", StringComparison.Ordinal);
        Assert.NotEqual(legacy, withNote);
        var original = Encode(withNote, encoding);
        Directory.CreateDirectory(fixture.Paths.CharactersDirectory);
        File.WriteAllBytes(fixture.Paths.GetBindingPath(LegacyOwner), original);

        var library = await fixture.LoadAsync();

        var backup = Path.Combine(fixture.Paths.MigrationBackupDirectory, "Characters", $"{LegacyOwner}.json");
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(LegacyPlateId, library.GetActivePlateId(LegacyOwner));
        Assert.Equal(CharacterBinding.CurrentVersion, library.GetBinding(LegacyOwner)!.Version);
        Assert.Equal([LegacyPlateId], library.GetBinding(LegacyOwner)!.PlateIds);
        Assert.Contains(LegacyOwner, library.FindPlate(LegacyPlateId)!.ActiveForContentIds);

        var migrated = File.ReadAllBytes(fixture.Paths.GetBindingPath(LegacyOwner));
        Assert.True(Ascii.IsValid(migrated));
        using var json = JsonDocument.Parse(migrated);
        Assert.Equal(CharacterBinding.CurrentVersion, json.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(LegacyPlateId, json.RootElement.GetProperty("ActiveProfileId").GetGuid());
        Assert.Equal([LegacyPlateId], json.RootElement.GetProperty("ProfileIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal(note, json.RootElement.GetProperty("Note").GetString());
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
    }

    [Fact]
    public async Task TemplateWithALiteralReplacementCharacter_AndAnOlderBackup_LoadsReadyFromTheFile_AndIsNeverKeptInRecovery()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new TemplateLibraryFixture(store);
        var templateId = await SaveTemplateAsync(fixture, "Kept");
        var path = fixture.Paths.GetTemplatePath(templateId);
        const string name = "Ælfwyn \uFFFD ✦ 🌸 Template";
        var file = Encoding.UTF8.GetBytes(EditTopLevelStrings(fixture.ReadTemplateJson(templateId), _ => name, "Name"));
        File.WriteAllBytes(path, file);
        Assert.Equal("Kept", JsonNode.Parse(store.Backups[path])!["Name"]!.GetValue<string>());

        var templates = fixture.CreateService();
        var readsBefore = store.ReaderInvocations;
        await templates.InitializeAsync();

        var summary = templates.FindTemplate(templateId)!;
        Assert.Equal(TemplateStatus.Ready, summary.Status);
        Assert.Equal(name, summary.DisplayName);
        Assert.Null(summary.Problem);
        Assert.Equal(1, store.ReaderInvocations - readsBefore);
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.Equal(file, File.ReadAllBytes(path));

        await templates.RenameTemplateAsync(templateId, "Renamed");

        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.True(Ascii.IsValid(File.ReadAllBytes(path)));
        Assert.Equal("Renamed", JsonNode.Parse(File.ReadAllText(path))!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task TemplateWithInvalidBytesInItsName_LoadsFromTheFile_AndItsExactBytesAreKeptBeforeItsRename()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new TemplateLibraryFixture(store);
        var templateId = await SaveTemplateAsync(fixture, "Kept");
        var path = fixture.Paths.GetTemplatePath(templateId);
        var damaged = WithInvalidByteInName(File.ReadAllBytes(path));
        File.WriteAllBytes(path, damaged);
        store.Backups[path] = EditTopLevelStrings(store.Backups[path], _ => OlderBackupName, "Name");

        var templates = fixture.CreateService();
        var readsBefore = store.ReaderInvocations;
        await templates.InitializeAsync();

        var summary = templates.FindTemplate(templateId)!;
        Assert.Equal(TemplateStatus.Ready, summary.Status);
        Assert.Equal("\uFFFDept", summary.DisplayName);
        Assert.NotNull(summary.Problem);
        Assert.NotEqual(TemplateLibraryService.DamagedTemplateProblem, summary.Problem);
        Assert.Equal(1, store.ReaderInvocations - readsBefore);
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.Equal(damaged, File.ReadAllBytes(path));

        await templates.RenameTemplateAsync(templateId, "Renamed");

        var kept = Assert.Single(RecoveryFiles(fixture.Paths));
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"{templateId}.damaged-{fixture.Clock.Now:yyyyMMdd-HHmmss-fff}.json"), Path.GetFileName(kept));
        Assert.Equal(damaged, File.ReadAllBytes(kept));
        Assert.True(Ascii.IsValid(File.ReadAllBytes(path)));
        Assert.Equal("Renamed", JsonNode.Parse(File.ReadAllText(path))!["Name"]!.GetValue<string>());
        Assert.Null(templates.FindTemplate(templateId)!.Problem);

        // Only the first write over the original keeps a copy: the file is now the Library's own.
        fixture.Clock.Tick();
        await templates.RenameTemplateAsync(templateId, "Renamed again");
        Assert.Equal(kept, Assert.Single(RecoveryFiles(fixture.Paths)));
        Assert.Equal(damaged, File.ReadAllBytes(kept));
        Assert.Equal("Renamed again", JsonNode.Parse(File.ReadAllText(path))!["Name"]!.GetValue<string>());

        var reloaded = fixture.CreateService();
        await reloaded.InitializeAsync();
        Assert.Equal(TemplateStatus.Ready, reloaded.FindTemplate(templateId)!.Status);
        Assert.Equal("Renamed again", reloaded.FindTemplate(templateId)!.DisplayName);
        Assert.Null(reloaded.FindTemplate(templateId)!.Problem);
        Assert.Single(RecoveryFiles(fixture.Paths));
    }

    [Theory]
    [InlineData("envelope", "literal")]
    [InlineData("document", "literal")]
    [InlineData("envelope", "invalid-byte")]
    [InlineData("document", "utf16-bom")]
    public async Task NewerTemplate_IsNewerVersion_FromTheFile_AndNeverWritten_WhateverItsBytes(string newerPart, string bytes)
    {
        var store = new BackupSimulatingStore();
        using var fixture = new TemplateLibraryFixture(store);
        var templateId = await SaveTemplateAsync(fixture, "Kept");
        var path = fixture.Paths.GetTemplatePath(templateId);
        var raw = JsonNode.Parse(fixture.ReadTemplateJson(templateId))!.AsObject();
        var versioned = newerPart == "envelope" ? raw : raw["Document"]!.AsObject();
        versioned["Version"] = 99;
        const string name = "Newer \uFFFD Template";
        var text = EditTopLevelStrings(VersionedJson.Serialize(raw), _ => name, "Name");
        var newer = bytes switch
        {
            "invalid-byte" => ReplaceEncodedReplacementCharacter(Encoding.UTF8.GetBytes(text)),
            "utf16-bom" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            _ => Encoding.UTF8.GetBytes(text),
        };
        File.WriteAllBytes(path, newer);

        var templates = fixture.CreateService();
        var readsBefore = store.ReaderInvocations;
        await templates.InitializeAsync();

        var summary = templates.FindTemplate(templateId)!;
        Assert.Equal(TemplateStatus.NewerVersion, summary.Status);
        Assert.Equal(name, summary.DisplayName);
        Assert.Equal(1, store.ReaderInvocations - readsBefore);
        Assert.Null(templates.GetSavedDocument(templateId));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));

        await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.RenameTemplateAsync(templateId, "Renamed"));

        Assert.Equal(newer, File.ReadAllBytes(path));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.Equal(TemplateStatus.NewerVersion, templates.FindTemplate(templateId)!.Status);
    }

    [Fact]
    public async Task TemplateWithInvalidBytes_WhileRecoveryIsBlocked_IsNeverRenamed_UntilItsBytesAreKept()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new TemplateLibraryFixture(store);
        var templateId = await SaveTemplateAsync(fixture, "Kept");
        var path = fixture.Paths.GetTemplatePath(templateId);
        var damaged = WithInvalidByteInName(File.ReadAllBytes(path));
        File.WriteAllBytes(path, damaged);
        store.Backups[path] = EditTopLevelStrings(store.Backups[path], _ => OlderBackupName, "Name");
        BlockRecovery(fixture.Paths);

        var templates = fixture.CreateService();
        await templates.InitializeAsync();
        Assert.Equal("\uFFFDept", templates.FindTemplate(templateId)!.DisplayName);

        var refused = await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.RenameTemplateAsync(templateId, "Renamed"));
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        Assert.Equal(damaged, File.ReadAllBytes(path));
        Assert.Equal("\uFFFDept", templates.FindTemplate(templateId)!.DisplayName);

        UnblockRecovery(fixture.Paths);
        await templates.RenameTemplateAsync(templateId, "Renamed");

        var kept = Assert.Single(RecoveryFiles(fixture.Paths));
        Assert.Equal(damaged, File.ReadAllBytes(kept));
        Assert.Equal("Renamed", templates.FindTemplate(templateId)!.DisplayName);
        Assert.Equal("Renamed", JsonNode.Parse(File.ReadAllText(path))!["Name"]!.GetValue<string>());

        fixture.Clock.Tick();
        await templates.RenameTemplateAsync(templateId, "Renamed again");
        Assert.Equal(kept, Assert.Single(RecoveryFiles(fixture.Paths)));
        Assert.Equal("Renamed again", templates.FindTemplate(templateId)!.DisplayName);
    }

    /// <summary>The file's first validly encoded U+FFFD (EF BF BD) as one byte that is never valid UTF-8, which reads back as the same character.</summary>
    private static byte[] ReplaceEncodedReplacementCharacter(byte[] file)
    {
        var at = file.AsSpan().IndexOf("\uFFFD"u8);
        Assert.True(at >= 0);
        return [.. file[..at], 0xFF, .. file[(at + 3)..]];
    }

    // ---------------------------------------------------------------- scenes

    /// <summary>One historical fixture loaded by both Libraries; disposing it deletes its directory.</summary>
    /// <param name="Reads">How often the store ran a reader during the load (a backup read is a second run).</param>
    /// <param name="ChangedFiles">Every file the load created, changed or removed, by relative path.</param>
    private sealed record LoadedScene(LibraryFixture Fixture, PlateLibraryService Library, TemplateLibraryService Templates, int Reads, IReadOnlyList<string> ChangedFiles) : IDisposable
    {
        public void Dispose() => Fixture.Dispose();
    }

    private static string[] SceneNames() => [.. LegacyPlates.Select(p => "legacy/" + p), .. HistoricalFixtures.Tags];

    /// <summary>
    /// Installs <paramref name="scene"/> over a store whose backup row for every record is an
    /// older, recognizably different copy (see <see cref="OlderCopy"/>), writes each record as
    /// <paramref name="rewrite"/> encodes its original text (left as it is when null), and loads it.
    /// </summary>
    private static async Task<LoadedScene> LoadSceneAsync(string scene, Func<string, byte[]>? rewrite)
    {
        var store = new BackupSimulatingStore();
        var fixture = new LibraryFixture(store);
        try
        {
            Install(scene, fixture.Paths);
            foreach (var path in RecordFiles(fixture.Paths))
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                store.Backups[path] = OlderCopy(fixture.Paths, path, text);
                if (rewrite is not null)
                {
                    File.WriteAllBytes(path, rewrite(text));
                }
            }

            var before = HistoricalFixtures.Snapshot(fixture.Root);
            var library = await fixture.LoadAsync();
            var templates = new TemplateLibraryService(fixture.Paths, fixture.Store, library, fixture.Log, () => fixture.Clock.Now);
            await templates.InitializeAsync();
            var after = HistoricalFixtures.Snapshot(fixture.Root);

            var changed = before.Keys.Union(after.Keys)
                .Where(p => !before.TryGetValue(p, out var old) || !after.TryGetValue(p, out var current) || old != current)
                .Order(StringComparer.Ordinal)
                .ToList();
            return new LoadedScene(fixture, library, templates, store.ReaderInvocations, changed);
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    /// <summary>A tagged release's installation, or a legacy Plate with the legacy binding and Template beside it.</summary>
    private static void Install(string scene, PlateStoragePaths paths)
    {
        const string legacyPrefix = "legacy/";
        if (!scene.StartsWith(legacyPrefix, StringComparison.Ordinal))
        {
            HistoricalFixtures.CopyInstall(scene, paths.Root);
            return;
        }

        CopyFixture(scene[legacyPrefix.Length..], paths.GetPlatePath(LegacyPlateId));
        CopyFixture("binding-v1.json", paths.GetBindingPath(LegacyOwner));
        CopyFixture("template-v1.json", paths.GetTemplatePath(LegacyTemplateId));
    }

    private static void CopyFixture(string legacyFileName, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(HistoricalFixtures.LegacyPath(legacyFileName), destination);
    }

    /// <summary>Every file the Libraries load: Plates, bindings, the Plate order and Templates.</summary>
    private static List<string> RecordFiles(PlateStoragePaths paths) =>
        new[] { paths.PlatesDirectory, paths.CharactersDirectory, paths.LibraryDirectory, paths.TemplatesDirectory }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.json"))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> DocumentFiles(PlateStoragePaths paths) =>
        Directory.GetFiles(paths.PlatesDirectory, "*.json").Concat(Directory.GetFiles(paths.TemplatesDirectory, "*.json")).Order(StringComparer.Ordinal).ToList();

    /// <summary>The Plate and Template files' bytes, hashed, by relative path.</summary>
    private static Dictionary<string, string> Fingerprints(LibraryFixture fixture) =>
        DocumentFiles(fixture.Paths).ToDictionary(p => Path.GetRelativePath(fixture.Root, p), p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));

    /// <summary>
    /// What the store's backup row holds for a record: an older copy of it that a load serving the
    /// backup in the file's place would show (another name, no associations, the order reversed).
    /// </summary>
    private static string OlderCopy(PlateStoragePaths paths, string path, string text)
    {
        var raw = JsonNode.Parse(text)!.AsObject();
        if (path == paths.LibraryFile)
        {
            raw["OrderedPlateIds"] = new JsonArray(Enumerable.Reverse(raw["OrderedPlateIds"]!.AsArray()).Select(n => n!.DeepClone()).ToArray());
        }
        else if (Path.GetDirectoryName(path) == paths.CharactersDirectory)
        {
            raw["ActiveProfileId"] = null;
            raw["ProfileIds"] = new JsonArray();
            raw["LastKnownCharacterName"] = OlderBackupName;
        }
        else
        {
            raw["Name"] = OlderBackupName;
        }

        return raw.ToJsonString();
    }

    /// <summary>Saves every Plate as the editor does and renames every user Template.</summary>
    private static async Task WriteEveryDocumentAsync(LoadedScene scene)
    {
        foreach (var plate in scene.Library.GetOrderedPlates())
        {
            await scene.Library.SavePlateDocumentAsync(scene.Library.OpenDocumentForEditing(plate.PlateId));
        }

        foreach (var template in scene.Templates.GetOrderedTemplates().Where(t => !t.IsBuiltIn).ToList())
        {
            await scene.Templates.RenameTemplateAsync(template.TemplateId, "Renamed by the current build");
        }
    }

    /// <summary>Everything the loaded Libraries show, in order, with the warnings and errors logged.</summary>
    private static List<string> Describe(LoadedScene scene)
    {
        var state = new List<string>();
        foreach (var plate in scene.Library.GetOrderedPlates())
        {
            var document = scene.Library.GetSavedDocument(plate.PlateId);
            state.Add($"Plate {plate.PlateId}: {plate.Status} \"{plate.DisplayName}\" problem={plate.Problem} version={document?.Version} revision={plate.Revision} " +
                $"created={plate.CreatedUtc:O} modified={plate.ModifiedUtc:O} characters=[{string.Join(", ", plate.CharacterNames)}] " +
                $"active-for=[{string.Join(", ", plate.ActiveForContentIds)}] content={Fingerprint(document)}");
        }

        foreach (var contentId in BindingIds(scene.Fixture.Paths))
        {
            var binding = scene.Library.GetBinding(contentId);
            state.Add(binding is null
                ? $"Binding {contentId}: none"
                : $"Binding {contentId}: version={binding.Version} active={binding.ActivePlateId} plates=[{string.Join(", ", binding.PlateIds)}] " +
                  $"name=\"{binding.LastKnownCharacterName}\" world=\"{binding.LastKnownHomeWorld}\"");
        }

        foreach (var template in scene.Templates.GetOrderedTemplates())
        {
            var document = template.IsBuiltIn ? null : scene.Templates.GetSavedDocument(template.TemplateId);
            state.Add($"Template {template.TemplateId}: {template.Kind} {template.Status} \"{template.DisplayName}\" problem={template.Problem} " +
                $"version={document?.Version} content={Fingerprint(document)}");
        }

        state.AddRange(scene.Fixture.Log.Messages
            .Where(m => m.StartsWith("W ", StringComparison.Ordinal) || m.StartsWith("E ", StringComparison.Ordinal))
            .Select(m => "Log " + m.Replace(scene.Fixture.Root, "<root>", StringComparison.Ordinal)));
        return state;
    }

    /// <summary>Every Plate's and user Template's name, and every binding's character name.</summary>
    private static List<string> Names(LoadedScene scene) =>
    [
        .. scene.Library.GetOrderedPlates().Select(p => p.DisplayName),
        .. scene.Templates.GetOrderedTemplates().Where(t => !t.IsBuiltIn).Select(t => t.DisplayName),
        .. BindingIds(scene.Fixture.Paths).Select(id => scene.Library.GetBinding(id)?.LastKnownCharacterName).OfType<string>(),
    ];

    private static List<ulong> BindingIds(PlateStoragePaths paths) =>
        Directory.GetFiles(paths.CharactersDirectory, "*.json")
            .Select(p => PlateStoragePaths.TryParseBindingFileName(p, out var contentId) ? contentId : 0UL)
            .Where(id => id != 0)
            .Order()
            .ToList();

    private static string Fingerprint(ProfileDocument? document) =>
        document is null ? "none" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, JsonOptions.Default))))[..16];

    // ---------------------------------------------------------------- text

    private static byte[] Encode(string text, string encoding)
    {
        Encoding chosen = encoding switch
        {
            "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf16-bom" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf16be-bom" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            "utf32-bom" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
            "utf32be-bom" => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown encoding."),
        };
        return [.. chosen.GetPreamble(), .. chosen.GetBytes(text)];
    }

    /// <summary><paramref name="prefix"/> (raw JSON string content) put before every Plate's, Template's and binding's name.</summary>
    private static string PrefixNames(string json, string prefix) => EditTopLevelStrings(json, value => prefix + value, "Name", "LastKnownCharacterName");

    /// <summary>
    /// Replaces the raw content (between the quotes) of every top-level string property named in
    /// <paramref name="properties"/> with what <paramref name="edit"/> makes of it; nothing else in
    /// the text changes. Only ASCII JSON (all that AetherFrame writes) is edited.
    /// </summary>
    private static string EditTopLevelStrings(string json, Func<string, string> edit, params string[] properties)
    {
        Assert.True(Ascii.IsValid(json), "Only ASCII JSON is edited here.");
        var bytes = Encoding.ASCII.GetBytes(json);
        var edited = new StringBuilder(json);
        foreach (var (start, length) in properties.SelectMany(p => TopLevelStrings(bytes, p)).OrderByDescending(s => s.Start))
        {
            edited.Remove(start, length).Insert(start, edit(json.Substring(start, length)));
        }

        return edited.ToString();
    }

    /// <summary>Where each top-level string value of <paramref name="property"/> sits in <paramref name="json"/>: its first byte after the quote, and its length.</summary>
    private static List<(int Start, int Length)> TopLevelStrings(byte[] json, string property)
    {
        var found = new List<(int Start, int Length)>();
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals(property)
                && reader.Read() && reader.TokenType == JsonTokenType.String)
            {
                found.Add(((int)reader.TokenStartIndex + 1, reader.ValueSpan.Length));
            }
        }

        return found;
    }

    /// <summary>The Template file with the first letter of its name replaced by a byte that is never valid UTF-8.</summary>
    private static byte[] WithInvalidByteInName(byte[] file)
    {
        var damaged = file.ToArray();
        damaged[Assert.Single(TopLevelStrings(file, "Name")).Start] = 0xFF;
        return damaged;
    }

    // ---------------------------------------------------------------- Templates and Recovery

    /// <summary>A user Template saved through the fixture's store, whose backup row then holds that save.</summary>
    private static async Task<Guid> SaveTemplateAsync(TemplateLibraryFixture fixture, string name)
    {
        var seeded = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        return await seeded.SaveAsTemplateAsync(plate.PlateId, name);
    }

    /// <summary>A file where the Recovery folder belongs: every copy into Recovery fails, as on a full disk.</summary>
    private static void BlockRecovery(PlateStoragePaths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RecoveryDirectory)!);
        File.WriteAllText(paths.RecoveryDirectory, "in the way");
    }

    private static void UnblockRecovery(PlateStoragePaths paths) => File.Delete(paths.RecoveryDirectory);

    private static string[] RecoveryFiles(PlateStoragePaths paths) =>
        Directory.Exists(paths.RecoveryDirectory) ? Directory.GetFiles(paths.RecoveryDirectory) : [];
}
