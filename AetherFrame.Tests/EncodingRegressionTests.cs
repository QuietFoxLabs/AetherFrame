using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A Plate file's encoding never decides which copy of it is read. A file whose content is usable
/// is read from the file itself, even when the store holds an older backup of it: whether it holds
/// a validly encoded U+FFFD, a byte order mark, or bytes that aren't valid text (which read as
/// U+FFFD, exactly as <see cref="File.ReadAllText(string)"/> reads them). Only content that isn't
/// usable falls back to the backup, and then the damaged bytes are kept in Recovery. A file saved
/// by a newer version is never read from an older backup and never written.
///
/// <para>A file read with invalid bytes is left on disk as it is. Its exact bytes go to Recovery
/// right before the first write that replaces it, and while that copy can't be made the write is
/// refused and the file keeps every byte. A file read cleanly never gets a Recovery copy, and every
/// save writes plain ASCII JSON.</para>
///
/// <para>These pin the regressions an earlier revision of this change introduced by treating any
/// U+FFFD as damage: valid Plates read from older backups or listed as unreadable, and a newer
/// version's Plate replaced by an older, writable backup.</para>
/// </summary>
public class EncodingRegressionTests
{
    private const string OlderName = "Older Plate";

    private const string ReplacementName = "Newer \uFFFD Plate";

    /// <summary>Where <see cref="Encode"/> writes its raw bytes.</summary>
    private const string Marker = "@@";

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static TheoryData<string, byte[]> InvalidUtf8 => new()
    {
        { "lone continuation byte", [0x80] },
        { "overlong encoding", [0xC0, 0xAF] },
        { "encoded surrogate", [0xED, 0xA0, 0x80] },
        { "invalid lead byte F5", [0xF5] },
        { "invalid lead byte FF", [0xFF] },
        { "value above U+10FFFF", [0xF4, 0x90, 0x80, 0x80] },
        { "truncated three-byte sequence", [0xE2, 0x82] },
    };

    /// <summary>A Plate saved through the Library, so a backup-keeping store holds it, named <see cref="OlderName"/>, as its backup.</summary>
    private static async Task<(Guid PlateId, string Path)> SeedAsync(LibraryFixture fixture)
    {
        var seeded = await fixture.LoadAsync();
        var plateId = (await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, OlderName)).PlateId;
        return (plateId, fixture.Paths.GetPlatePath(plateId));
    }

    private static string ReplaceOnce(string text, string oldValue, string newValue)
    {
        var at = text.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(at >= 0 && at == text.LastIndexOf(oldValue, StringComparison.Ordinal), $"Expected exactly one {oldValue}.");
        return string.Concat(text.AsSpan(0, at), newValue, text.AsSpan(at + oldValue.Length));
    }

    private static string WithName(string json, string name) => ReplaceOnce(json, $"\"Name\": \"{OlderName}\"", $"\"Name\": \"{name}\"");

    private static string WithVersion(string json, int version) =>
        ReplaceOnce(json, $"\"Version\": {ProfileDocument.CurrentSchemaVersion},", $"\"Version\": {version},");

    /// <summary><paramref name="text"/> encoded with <paramref name="encoding"/>, its byte order mark (if
    /// it has one) first, and <paramref name="raw"/> written as they are where <see cref="Marker"/> stands.</summary>
    private static byte[] Encode(string text, Encoding encoding, byte[]? raw = null)
    {
        if (raw is null)
        {
            return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        }

        var at = text.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(at >= 0);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text[..at]), .. raw, .. encoding.GetBytes(text[(at + Marker.Length)..])];
    }

    private static string NameIn(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Name").GetString()!;
    }

    /// <summary>The Plate's name exactly as <see cref="File.ReadAllText(string)"/> decodes its file.</summary>
    private static string NameAsReadAllTextReadsIt(string path) => NameIn(File.ReadAllText(path));

    /// <summary>A file where the Recovery folder belongs: every copy into Recovery fails, as on a full disk.</summary>
    private static void BlockRecovery(PlateStoragePaths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RecoveryDirectory)!);
        File.WriteAllText(paths.RecoveryDirectory, "in the way");
    }

    private static void UnblockRecovery(PlateStoragePaths paths) => File.Delete(paths.RecoveryDirectory);

    /// <summary>Recovery holds exactly one file: <paramref name="original"/>, byte for byte, named as a
    /// copy of <paramref name="path"/> made at <paramref name="copiedAt"/>.</summary>
    private static void AssertKeptInRecovery(PlateStoragePaths paths, string path, byte[] original, DateTime copiedAt)
    {
        Assert.True(Directory.Exists(paths.RecoveryDirectory), "Nothing was kept in Recovery.");
        var kept = Assert.Single(Directory.GetFiles(paths.RecoveryDirectory));
        var stamp = copiedAt.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        Assert.Equal($"{Path.GetFileNameWithoutExtension(path)}.damaged-{stamp}{Path.GetExtension(path)}", Path.GetFileName(kept));
        Assert.Equal(original, File.ReadAllBytes(kept));
    }

    /// <summary>What every save writes: UTF-8 without a byte order mark, everything outside ASCII escaped.</summary>
    private static void AssertSavedAsAscii(string path) => Assert.True(Ascii.IsValid(File.ReadAllBytes(path)));

    /// <summary>A Ready Plate read with invalid text carries a note, and not one of the notes of a
    /// Plate that couldn't be read at all.</summary>
    private static void AssertReadWithInvalidText(PlateSummary summary)
    {
        Assert.NotNull(summary.Problem);
        Assert.NotEqual(PlateLibraryService.DamagedPlateProblem, summary.Problem);
        Assert.NotEqual(PlateLibraryService.UnavailablePlateProblem, summary.Problem);
    }

    private static void AssertNoTemporaryFiles(string root) => Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidReplacementCharacter_IsOrdinaryText_ReadFromTheFileItself(bool olderBackup)
    {
        var backups = olderBackup ? new BackupSimulatingStore() : null;
        using var fixture = new LibraryFixture(backups ?? (IPlateFileStore)new SystemFileStore());
        var (plateId, path) = await SeedAsync(fixture);
        var original = Encode(WithName(File.ReadAllText(path), ReplacementName), Utf8);
        File.WriteAllBytes(path, original);
        if (backups is not null)
        {
            Assert.Equal(OlderName, NameIn(backups.Backups[path]));
        }

        var library = await fixture.LoadAsync();

        var summary = library.FindPlate(plateId)!;
        Assert.Equal(PlateStatus.Ready, summary.Status);
        Assert.Equal(ReplacementName, summary.DisplayName);
        Assert.Null(summary.Problem);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));

        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        AssertSavedAsAscii(path);
        Assert.Contains("\\uFFFD", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, reloaded.FindPlate(plateId)!.Status);
        Assert.Equal(ReplacementName, reloaded.FindPlate(plateId)!.DisplayName);
        Assert.Null(reloaded.FindPlate(plateId)!.Problem);
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8))]
    public async Task InvalidUtf8_InAPlateThatParses_IsReadFromTheFile_NotItsOlderBackup_AndKeptExactlyBeforeTheFirstSave(string sequence, byte[] invalid)
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var (plateId, path) = await SeedAsync(fixture);

        // Directly before the closing quote, so a truncated sequence ends at it.
        var original = Encode(WithName(File.ReadAllText(path), "Newer " + Marker), Utf8, invalid);
        File.WriteAllBytes(path, original);
        var fileName = NameAsReadAllTextReadsIt(path);
        Assert.StartsWith("Newer ", fileName, StringComparison.Ordinal);
        Assert.Contains('\uFFFD', fileName);

        var library = await fixture.LoadAsync();

        var summary = library.FindPlate(plateId)!;
        Assert.Equal(PlateStatus.Ready, summary.Status);
        Assert.Equal(fileName, summary.DisplayName);
        AssertReadWithInvalidText(summary);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths), sequence);

        fixture.Clock.Tick();
        var copiedAt = fixture.Clock.Now;
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        AssertKeptInRecovery(fixture.Paths, path, original, copiedAt);
        AssertSavedAsAscii(path);
        Assert.Null(library.FindPlate(plateId)!.Problem);

        // Only the first write over the original keeps a copy: the file is now the Library's own.
        fixture.Clock.Tick();
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));
        await library.RenamePlateAsync(plateId, "Renamed Plate");
        AssertKeptInRecovery(fixture.Paths, path, original, copiedAt);
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, reloaded.FindPlate(plateId)!.Status);
        Assert.Equal("Renamed Plate", reloaded.FindPlate(plateId)!.DisplayName);
        Assert.Null(reloaded.FindPlate(plateId)!.Problem);
    }

    [Fact]
    public async Task InvalidBytesOutsideAnyString_AreDamage_ReadFromTheBackup_WithTheDamagedBytesKept()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var (plateId, path) = await SeedAsync(fixture);
        var seeded = File.ReadAllBytes(path);
        byte[] damaged = [seeded[0], 0xFF, .. seeded[1..]];
        File.WriteAllBytes(path, damaged);
        var loadedAt = fixture.Clock.Now;

        var library = await fixture.LoadAsync();

        var summary = library.FindPlate(plateId)!;
        Assert.Equal(PlateStatus.Ready, summary.Status);
        Assert.Equal(OlderName, summary.DisplayName);
        AssertKeptInRecovery(fixture.Paths, path, damaged, loadedAt);

        fixture.Clock.Tick();
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        AssertKeptInRecovery(fixture.Paths, path, damaged, loadedAt);
        AssertSavedAsAscii(path);
        Assert.Equal(PlateStatus.Ready, (await fixture.LoadAsync()).FindPlate(plateId)!.Status);
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16 little-endian")]
    [InlineData("UTF-16 big-endian")]
    [InlineData("UTF-32 little-endian")]
    [InlineData("UTF-32 big-endian")]
    public async Task ByteOrderMark_WithNonAsciiText_IsReadFromTheFileItself_AndNeverKeptInRecovery(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "UTF-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "UTF-16 little-endian" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "UTF-16 big-endian" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            "UTF-32 little-endian" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
            _ => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
        };
        const string name = "\u00C6lfwyn \uFFFD \u2726 \U0001F338 Plate";
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var (plateId, path) = await SeedAsync(fixture);
        var original = Encode(WithName(File.ReadAllText(path), name), encoding);
        File.WriteAllBytes(path, original);

        var library = await fixture.LoadAsync();

        var summary = library.FindPlate(plateId)!;
        Assert.Equal(PlateStatus.Ready, summary.Status);
        Assert.Equal(name, summary.DisplayName);
        Assert.Null(summary.Problem);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));

        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        AssertSavedAsAscii(path);
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        var reloaded = (await fixture.LoadAsync()).FindPlate(plateId)!;
        Assert.Equal(PlateStatus.Ready, reloaded.Status);
        Assert.Equal(name, reloaded.DisplayName);
        Assert.Null(reloaded.Problem);
    }

    [Fact]
    public async Task LoneSurrogate_InAUtf16File_IsReadFromTheFileItself_AndKeptExactlyBeforeTheFirstSave()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var (plateId, path) = await SeedAsync(fixture);
        var original = Encode(WithName(File.ReadAllText(path), $"Newer {Marker} Plate"), new UnicodeEncoding(bigEndian: false, byteOrderMark: true), [0x00, 0xD8]);
        File.WriteAllBytes(path, original);
        var fileName = NameAsReadAllTextReadsIt(path);
        Assert.Equal(ReplacementName, fileName);

        var library = await fixture.LoadAsync();

        var summary = library.FindPlate(plateId)!;
        Assert.Equal(PlateStatus.Ready, summary.Status);
        Assert.Equal(fileName, summary.DisplayName);
        AssertReadWithInvalidText(summary);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));

        fixture.Clock.Tick();
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        AssertKeptInRecovery(fixture.Paths, path, original, fixture.Clock.Now);
        AssertSavedAsAscii(path);
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, reloaded.FindPlate(plateId)!.Status);
        Assert.Equal(fileName, reloaded.FindPlate(plateId)!.DisplayName);
    }

    /// <summary>Invalid text in each of the other encodings the reader honours, behind its byte order mark.</summary>
    [Theory]
    [InlineData("UTF-8 with a byte order mark, invalid byte")]
    [InlineData("UTF-16 big-endian, lone surrogate")]
    [InlineData("UTF-32 little-endian, value above U+10FFFF")]
    [InlineData("UTF-32 big-endian, surrogate value")]
    public async Task InvalidTextBehindAByteOrderMark_IsReadFromTheFileItself_AndKeptExactlyBeforeTheFirstSave(string kind)
    {
        var (encoding, invalid) = kind switch
        {
            "UTF-16 big-endian, lone surrogate" => ((Encoding)new UnicodeEncoding(bigEndian: true, byteOrderMark: true), new byte[] { 0xD8, 0x00 }),
            "UTF-32 little-endian, value above U+10FFFF" => (new UTF32Encoding(bigEndian: false, byteOrderMark: true), new byte[] { 0x00, 0x00, 0x11, 0x00 }),
            "UTF-32 big-endian, surrogate value" => (new UTF32Encoding(bigEndian: true, byteOrderMark: true), new byte[] { 0x00, 0x00, 0xD8, 0x00 }),
            _ => (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), new byte[] { 0xFF }),
        };
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var (plateId, path) = await SeedAsync(fixture);
        var original = Encode(WithName(File.ReadAllText(path), $"Newer {Marker} Plate"), encoding, invalid);
        File.WriteAllBytes(path, original);
        var fileName = NameAsReadAllTextReadsIt(path);
        Assert.Contains('\uFFFD', fileName);

        var library = await fixture.LoadAsync();

        var summary = library.FindPlate(plateId)!;
        Assert.Equal(PlateStatus.Ready, summary.Status);
        Assert.Equal(fileName, summary.DisplayName);
        AssertReadWithInvalidText(summary);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths), kind);

        fixture.Clock.Tick();
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        AssertKeptInRecovery(fixture.Paths, path, original, fixture.Clock.Now);
        AssertSavedAsAscii(path);
        Assert.Equal(fileName, (await fixture.LoadAsync()).FindPlate(plateId)!.DisplayName);
    }

    [Theory]
    [InlineData("valid U+FFFD")]
    [InlineData("invalid bytes")]
    [InlineData("byte order mark")]
    public async Task NewerVersionPlate_IsNeverReplacedByItsOlderBackup_NorWritten_WhateverItsBytes(string text)
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var (plateId, path) = await SeedAsync(fixture);
        var newer = WithVersion(File.ReadAllText(path), 99);
        var original = text switch
        {
            "invalid bytes" => Encode(WithName(newer, $"Newer {Marker} Plate"), Utf8, [0xFF]),
            "byte order mark" => Encode(WithName(newer, ReplacementName), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)),
            _ => Encode(WithName(newer, ReplacementName), Utf8),
        };
        File.WriteAllBytes(path, original);
        Assert.Equal(ReplacementName, NameAsReadAllTextReadsIt(path));

        var library = await fixture.LoadAsync();

        var summary = library.FindPlate(plateId)!;
        Assert.Equal(PlateStatus.NewerVersion, summary.Status);
        Assert.Equal(ReplacementName, summary.DisplayName);

        Assert.Throws<PlateLibraryException>(() => library.OpenDocumentForEditing(plateId));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.SavePlateDocumentAsync(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Replacement", fixture.Clock.Now)));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.RenamePlateAsync(plateId, "Renamed Plate"));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.SetActivePlateAsync(Characters.Alice, plateId));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.DuplicatePlateAsync(plateId));

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.Single(library.GetOrderedPlates());
        Assert.Null(library.GetBinding(Characters.Alice.ContentId));
        Assert.Equal(PlateStatus.NewerVersion, (await fixture.LoadAsync()).FindPlate(plateId)!.Status);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedPlate_ReadFromItsBackup_SavesAndRenames_AndItsTruncatedBytesStayInRecovery(bool backupHoldsReplacementCharacter)
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var (plateId, path) = await SeedAsync(fixture);
        var backupName = backupHoldsReplacementCharacter ? ReplacementName : OlderName;
        store.Backups[path] = WithName(store.Backups[path], backupName);
        var seeded = File.ReadAllBytes(path);
        var truncated = seeded[..(seeded.Length / 2)];
        File.WriteAllBytes(path, truncated);
        var loadedAt = fixture.Clock.Now;

        var library = await fixture.LoadAsync();

        var summary = library.FindPlate(plateId)!;
        Assert.Equal(PlateStatus.Ready, summary.Status);
        Assert.Equal(backupName, summary.DisplayName);
        AssertKeptInRecovery(fixture.Paths, path, truncated, loadedAt);

        fixture.Clock.Tick();
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));
        fixture.Clock.Tick();
        await library.RenamePlateAsync(plateId, "Renamed Plate");

        var reloaded = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, reloaded.FindPlate(plateId)!.Status);
        Assert.Equal("Renamed Plate", reloaded.FindPlate(plateId)!.DisplayName);
        Assert.Null(reloaded.FindPlate(plateId)!.Problem);
        AssertSavedAsAscii(path);
        AssertKeptInRecovery(fixture.Paths, path, truncated, loadedAt);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("rename")]
    public async Task InvalidBytesWhoseRecoveryCopyFails_AreNeverWrittenOver_UntilTheCopyIsKept(string retried)
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var (plateId, path) = await SeedAsync(fixture);
        var backup = store.Backups[path];
        var original = Encode(WithName(File.ReadAllText(path), $"Newer {Marker} Plate"), Utf8, [0xFF]);
        File.WriteAllBytes(path, original);
        BlockRecovery(fixture.Paths);

        var library = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        Assert.Equal(ReplacementName, library.FindPlate(plateId)!.DisplayName);

        Task Save() => library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));
        Task Rename() => library.RenamePlateAsync(plateId, "Renamed Plate");

        foreach (var write in new Func<Task>[] { Save, Rename })
        {
            var refused = await Assert.ThrowsAsync<PlateLibraryException>(write);
            Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
            Assert.Equal(original, File.ReadAllBytes(path));
        }

        Assert.Equal(ReplacementName, library.FindPlate(plateId)!.DisplayName);
        Assert.Equal(backup, store.Backups[path]);

        // A copy is a new file: nothing it writes replaces the original's bytes. (That Duplicate
        // goes ahead is what the Library does today, not a rule; the rule is the original's bytes.)
        var copyId = await library.DuplicatePlateAsync(plateId);
        Assert.Equal(PlateStatus.Ready, library.FindPlate(copyId)!.Status);
        Assert.Equal(original, File.ReadAllBytes(path));
        AssertNoTemporaryFiles(fixture.Root);

        UnblockRecovery(fixture.Paths);
        fixture.Clock.Tick();
        await (retried == "save" ? Save() : Rename());

        var copiedAt = fixture.Clock.Now;
        AssertKeptInRecovery(fixture.Paths, path, original, copiedAt);
        AssertSavedAsAscii(path);
        AssertNoTemporaryFiles(fixture.Root);
        Assert.Null(library.FindPlate(plateId)!.Problem);

        fixture.Clock.Tick();
        await (retried == "save" ? Save() : Rename());
        AssertKeptInRecovery(fixture.Paths, path, original, copiedAt);
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, reloaded.FindPlate(plateId)!.Status);
        Assert.Equal(retried == "save" ? ReplacementName : "Renamed Plate", reloaded.FindPlate(plateId)!.DisplayName);
        Assert.Equal(PlateStatus.Ready, reloaded.FindPlate(copyId)!.Status);
    }
}
