using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A character binding and the Plate order are read from their own files whatever text those
/// hold. A binding with bytes that aren't valid text keeps every Plate association and its Active
/// Plate (the bytes read as U+FFFD, exactly as 0.1.6 read them): never an older backup in its
/// place, never "unreadable" and replaced. An index holding unusual Unicode, a byte order mark or
/// invalid bytes keeps the player's manual order and the properties this build doesn't know, and
/// is never rebuilt. A file read with invalid bytes is left untouched at load and copied to
/// Recovery, byte for byte, right before the first write that replaces it; that write is refused
/// while the copy fails. A file read cleanly, including a validly encoded U+FFFD, gets no Recovery
/// copy. A newer version's file is read from itself, never its older backup, and never written.
/// </summary>
public class BindingAndIndexEncodingTests
{
    /// <summary>A validly encoded U+FFFD, an emoji (a surrogate pair), a right-to-left mark, a
    /// U+FEFF inside the text, a combining mark and the noncharacter U+FFFE.</summary>
    private const string UnusualText = "Order \uFFFD \U0001F338 \u200F \uFEFF e\u0301 \uFFFE end";

    private static readonly byte[] InvalidByte = [0xFF];

    private static readonly byte[] EncodedReplacementCharacter = [0xEF, 0xBF, 0xBD];

    /// <summary>A file where the Recovery folder belongs: every copy into Recovery fails, as on a full disk.</summary>
    private static void BlockRecovery(PlateStoragePaths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RecoveryDirectory)!);
        File.WriteAllText(paths.RecoveryDirectory, "in the way");
    }

    private static void UnblockRecovery(PlateStoragePaths paths) => File.Delete(paths.RecoveryDirectory);

    /// <summary>Recovery holds exactly one file: the copy of <paramref name="path"/> stamped
    /// <paramref name="copiedAt"/>, byte for byte <paramref name="original"/>.</summary>
    private static void AssertKeptInRecovery(PlateStoragePaths paths, string path, DateTime copiedAt, byte[] original)
    {
        var kept = Assert.Single(Directory.Exists(paths.RecoveryDirectory) ? Directory.GetFiles(paths.RecoveryDirectory) : []);
        Assert.Equal($"{Path.GetFileNameWithoutExtension(path)}.damaged-{copiedAt:yyyyMMdd-HHmmss-fff}{Path.GetExtension(path)}", Path.GetFileName(kept));
        Assert.Equal(original, File.ReadAllBytes(kept));
    }

    /// <summary><paramref name="file"/> with the first letter of the JSON string <paramref name="value"/>
    /// replaced by <paramref name="replacement"/>.</summary>
    private static byte[] ReplaceFirstLetter(byte[] file, string value, byte[] replacement)
    {
        var at = file.AsSpan().IndexOf(Encoding.UTF8.GetBytes($"\"{value}\"")) + 1;
        Assert.True(at > 0);
        return [.. file[..at], .. replacement, .. file[(at + 1)..]];
    }

    /// <summary>Three Plates associated with Alice, a second apart, the second Active, all saved
    /// through the fixture's store. Also returns her binding as it was saved when only the first
    /// was associated (Active too): what a stale backup row holds.</summary>
    private static async Task<(Guid[] Plates, string OlderBinding)> SeedAliceAsync(LibraryFixture fixture)
    {
        var library = await fixture.LoadAsync();
        var first = (await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "First")).PlateId;
        var olderBinding = fixture.ReadBindingJson(Characters.Alice.ContentId);
        fixture.Clock.Tick();
        var second = (await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Second")).PlateId;
        fixture.Clock.Tick();
        var third = (await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Third")).PlateId;
        await library.SetActivePlateAsync(Characters.Alice, second);
        fixture.Clock.Tick();
        return ([first, second, third], olderBinding);
    }

    /// <summary>Alice's binding on disk with the first letter of her name replaced by
    /// <paramref name="replacement"/>, over a backup row that is older (see <see cref="SeedAliceAsync"/>)
    /// when the store keeps one.</summary>
    private static byte[] DamageAliceName(LibraryFixture fixture, string olderBinding, byte[] replacement)
    {
        var path = fixture.Paths.GetBindingPath(Characters.Alice.ContentId);
        if (fixture.Store is BackupSimulatingStore store)
        {
            store.Backups[path] = olderBinding;
        }

        var damaged = ReplaceFirstLetter(File.ReadAllBytes(path), Characters.Alice.Name!, replacement);
        File.WriteAllBytes(path, damaged);
        return damaged;
    }

    private static void AssertSavedBinding(LibraryFixture fixture, IEnumerable<Guid> plateIds, Guid active)
    {
        var saved = fixture.ReadBinding(Characters.Alice.ContentId);
        Assert.Equal(plateIds, saved.GetProperty("ProfileIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal(active, saved.GetProperty("ActiveProfileId").GetGuid());
    }

    /// <summary>Three unbound Plates, a second apart, saved through the fixture's store, which keeps
    /// (and backs up) the order newest first: the reverse of the returned creation order.</summary>
    private static async Task<Guid[]> SeedPlatesAsync(LibraryFixture fixture)
    {
        var library = await fixture.LoadAsync();
        var created = new List<Guid>();
        foreach (var name in new[] { "First", "Second", "Third" })
        {
            created.Add((await library.CreatePlateAsync(PlateStartingLayout.Blank, null, name)).PlateId);
            fixture.Clock.Tick();
        }

        Assert.Equal(created.AsEnumerable().Reverse(), fixture.ReadLibraryOrder());
        return created.ToArray();
    }

    private static string IndexJson(int version, IEnumerable<Guid> order, string note) => $$"""
        {
          "Version": {{version}},
          "OrderedPlateIds": [{{string.Join(", ", order.Select(id => $"\"{id}\""))}}],
          "FutureNote": "{{note}}"
        }
        """;

    /// <summary>An index in the player's order (oldest first, unlike a rebuilt one) whose unknown
    /// property holds <see cref="UnusualText"/>, as a file in <paramref name="encoding"/>; with
    /// "invalid-byte", a 0xFF byte precedes that text inside the property's string.</summary>
    private static byte[] ManualIndex(Guid[] order, string encoding) => encoding switch
    {
        "utf8-bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(IndexJson(1, order, UnusualText))],
        "utf16le-bom" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(IndexJson(1, order, UnusualText))],
        "invalid-byte" => ReplaceFirstLetter(Encoding.UTF8.GetBytes(IndexJson(1, order, "X" + UnusualText)), "X" + UnusualText, InvalidByte),
        _ => Encoding.UTF8.GetBytes(IndexJson(1, order, UnusualText)),
    };

    private static string SavedFutureNote(LibraryFixture fixture) =>
        JsonDocument.Parse(File.ReadAllText(fixture.Paths.LibraryFile)).RootElement.GetProperty("FutureNote").GetString()!;

    [Theory]
    [InlineData("invalid-byte", "older-backup")]
    [InlineData("literal-fffd", "older-backup")]
    [InlineData("invalid-byte", "no-backup")]
    [InlineData("literal-fffd", "no-backup")]
    public async Task BindingWithUnusualText_KeepsEveryAssociationAndItsActivePlate_ThroughItsNextWrites(string text, string backup)
    {
        using var fixture = new LibraryFixture(backup == "older-backup" ? new BackupSimulatingStore() : null);
        var (plates, olderBinding) = await SeedAliceAsync(fixture);
        var path = fixture.Paths.GetBindingPath(Characters.Alice.ContentId);
        var invalid = text == "invalid-byte";
        var original = DamageAliceName(fixture, olderBinding, invalid ? InvalidByte : EncodedReplacementCharacter);

        var library = await fixture.LoadAsync();

        var binding = library.GetBinding(Characters.Alice.ContentId);
        Assert.NotNull(binding);
        Assert.Equal(plates, binding.PlateIds);
        Assert.Equal(plates[1], binding.ActivePlateId);
        Assert.Equal("\uFFFDlice Example", binding.LastKnownCharacterName);
        Assert.Equal(plates[1], library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.Equal(original, File.ReadAllBytes(path));

        var firstWrite = fixture.Clock.Now;
        await library.SetActivePlateAsync(Characters.Alice, plates[2]);

        binding = library.GetBinding(Characters.Alice.ContentId)!;
        Assert.Equal(plates, binding.PlateIds);
        Assert.Equal(plates[2], binding.ActivePlateId);
        Assert.Equal(Characters.Alice.Name, binding.LastKnownCharacterName);
        AssertSavedBinding(fixture, plates, plates[2]);
        Assert.Equal(Characters.Alice.Name, fixture.ReadBinding(Characters.Alice.ContentId).GetProperty("LastKnownCharacterName").GetString());
        if (invalid)
        {
            AssertKeptInRecovery(fixture.Paths, path, firstWrite, original);
        }
        else
        {
            Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        }

        fixture.Clock.Tick();
        await library.SetActivePlateAsync(Characters.Alice, plates[0]);

        if (invalid)
        {
            AssertKeptInRecovery(fixture.Paths, path, firstWrite, original);
        }
        else
        {
            Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        }

        var reloaded = await fixture.LoadAsync();
        Assert.Equal(plates, reloaded.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Equal(plates[0], reloaded.GetActivePlateId(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task BindingWithAnInvalidByte_KeepsEveryAssociation_WhenANewPlateJoinsThem()
    {
        using var fixture = new LibraryFixture(new BackupSimulatingStore());
        var (plates, olderBinding) = await SeedAliceAsync(fixture);
        var path = fixture.Paths.GetBindingPath(Characters.Alice.ContentId);
        var original = DamageAliceName(fixture, olderBinding, InvalidByte);
        var library = await fixture.LoadAsync();

        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Fourth");

        Assert.False(created.BecameActive);
        Guid[] associated = [.. plates, created.PlateId];
        Assert.Equal(associated, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Equal(plates[1], library.GetActivePlateId(Characters.Alice.ContentId));
        AssertSavedBinding(fixture, associated, plates[1]);
        Assert.Equal(Characters.Alice.Name, fixture.ReadBinding(Characters.Alice.ContentId).GetProperty("LastKnownCharacterName").GetString());
        AssertKeptInRecovery(fixture.Paths, path, fixture.Clock.Now, original);
    }

    [Fact]
    public async Task BindingWithAnInvalidByte_KeepsItsOtherAssociations_WhenOneOfItsPlatesIsDeleted()
    {
        using var fixture = new LibraryFixture(new BackupSimulatingStore());
        var (plates, olderBinding) = await SeedAliceAsync(fixture);
        var path = fixture.Paths.GetBindingPath(Characters.Alice.ContentId);
        var original = DamageAliceName(fixture, olderBinding, InvalidByte);
        var library = await fixture.LoadAsync();

        await library.DeletePlateAsync(plates[0]);

        Assert.Equal([plates[1], plates[2]], library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Equal(plates[1], library.GetActivePlateId(Characters.Alice.ContentId));
        AssertSavedBinding(fixture, [plates[1], plates[2]], plates[1]);
        AssertKeptInRecovery(fixture.Paths, path, fixture.Clock.Now, original);

        var reloaded = await fixture.LoadAsync();
        Assert.Equal([plates[1], plates[2]], reloaded.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Equal(plates[1], reloaded.GetActivePlateId(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task NewerVersionBinding_HoldingAReplacementCharacter_IsNeverReadFromItsOlderBackup_NorWritten()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var (plates, olderBinding) = await SeedAliceAsync(fixture);
        var path = fixture.Paths.GetBindingPath(Characters.Alice.ContentId);
        store.Backups[path] = olderBinding;
        var newer = Encoding.UTF8.GetBytes($$"""
            {
              "Version": 99,
              "ContentId": {{Characters.Alice.ContentId}},
              "ActiveProfileId": "{{plates[1]}}",
              "ProfileIds": ["{{plates[0]}}", "{{plates[1]}}", "{{plates[2]}}"],
              "LastKnownCharacterName": "{{"\uFFFD"}}lice Example",
              "Hologram": { "Shimmer": 3 }
            }
            """);
        File.WriteAllBytes(path, newer);

        var library = await fixture.LoadAsync();

        Assert.Null(library.GetBinding(Characters.Alice.ContentId));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.Contains("backup copy", StringComparison.Ordinal));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.SetActivePlateAsync(Characters.Alice, plates[2]));
        Assert.Contains("newer version", refused.Message, StringComparison.Ordinal);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Fourth");
        await library.DeletePlateAsync(plates[0]);

        Assert.Equal(newer, File.ReadAllBytes(path));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16le-bom")]
    [InlineData("invalid-byte")]
    public async Task IndexWithUnusualText_KeepsTheManualOrderAndItsUnknownProperty_ThroughAMove(string encoding)
    {
        using var fixture = new LibraryFixture(new BackupSimulatingStore());
        var plates = await SeedPlatesAsync(fixture);
        var invalid = encoding == "invalid-byte";
        var original = ManualIndex(plates, encoding);
        File.WriteAllBytes(fixture.Paths.LibraryFile, original);

        var library = await fixture.LoadAsync();

        Assert.Equal(plates, library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.Equal(original, File.ReadAllBytes(fixture.Paths.LibraryFile));

        await library.MovePlateAsync(plates[2], plates[0], placeAfter: false);

        Guid[] moved = [plates[2], plates[0], plates[1]];
        Assert.Equal(moved, library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal(moved, fixture.ReadLibraryOrder());
        Assert.Equal(invalid ? "\uFFFD" + UnusualText : UnusualText, SavedFutureNote(fixture));
        Assert.True(Ascii.IsValid(File.ReadAllBytes(fixture.Paths.LibraryFile)));
        if (invalid)
        {
            AssertKeptInRecovery(fixture.Paths, fixture.Paths.LibraryFile, fixture.Clock.Now, original);
        }
        else
        {
            Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        }

        var reloaded = await fixture.LoadAsync();
        Assert.Equal(moved, reloaded.GetOrderedPlates().Select(p => p.PlateId));
    }

    [Fact]
    public async Task NewerVersionIndex_HoldingAReplacementCharacter_IsUsedReadOnly_NeverItsOlderBackup()
    {
        using var fixture = new LibraryFixture(new BackupSimulatingStore());
        var plates = await SeedPlatesAsync(fixture);
        var newer = Encoding.UTF8.GetBytes(IndexJson(99, plates, UnusualText));
        File.WriteAllBytes(fixture.Paths.LibraryFile, newer);

        var library = await fixture.LoadAsync();

        Assert.Equal(plates, library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));

        await library.MovePlateAsync(plates[2], plates[0], placeAfter: false);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Fourth");

        Assert.Equal(newer, File.ReadAllBytes(fixture.Paths.LibraryFile));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
    }

    [Fact]
    public async Task BindingWithAnInvalidByte_IsNeverWrittenOver_UntilItsOriginalBytesAreKept()
    {
        using var fixture = new LibraryFixture(new BackupSimulatingStore());
        var (plates, olderBinding) = await SeedAliceAsync(fixture);
        var path = fixture.Paths.GetBindingPath(Characters.Alice.ContentId);
        var original = DamageAliceName(fixture, olderBinding, InvalidByte);
        BlockRecovery(fixture.Paths);

        var library = await fixture.LoadAsync();
        Assert.Equal(plates, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.SetActivePlateAsync(Characters.Alice, plates[2]));
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(plates[1], library.GetActivePlateId(Characters.Alice.ContentId));

        UnblockRecovery(fixture.Paths);
        fixture.Clock.Tick();
        await library.SetActivePlateAsync(Characters.Alice, plates[2]);

        AssertKeptInRecovery(fixture.Paths, path, fixture.Clock.Now, original);
        AssertSavedBinding(fixture, plates, plates[2]);
        Assert.Equal(plates[2], library.GetActivePlateId(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task IndexWithAnInvalidByte_IsNeverWrittenOver_UntilItsOriginalBytesAreKept()
    {
        using var fixture = new LibraryFixture(new BackupSimulatingStore());
        var plates = await SeedPlatesAsync(fixture);
        var original = ManualIndex(plates, "invalid-byte");
        File.WriteAllBytes(fixture.Paths.LibraryFile, original);
        BlockRecovery(fixture.Paths);

        var library = await fixture.LoadAsync();
        Assert.Equal(plates, library.GetOrderedPlates().Select(p => p.PlateId));

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.MovePlateAsync(plates[2], plates[0], placeAfter: false));
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        Assert.Equal(original, File.ReadAllBytes(fixture.Paths.LibraryFile));

        UnblockRecovery(fixture.Paths);
        fixture.Clock.Tick();
        await library.MovePlateAsync(plates[2], plates[0], placeAfter: false);

        Assert.Equal([plates[2], plates[0], plates[1]], fixture.ReadLibraryOrder());
        AssertKeptInRecovery(fixture.Paths, fixture.Paths.LibraryFile, fixture.Clock.Now, original);
        Assert.Equal("\uFFFD" + UnusualText, SavedFutureNote(fixture));
    }
}
