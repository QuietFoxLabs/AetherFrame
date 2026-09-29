using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A file's encoding never decides which copy of it is read. Bytes that aren't valid text read as
/// U+FFFD, exactly as 0.1.6 read them, from the file itself: never an older backup in its place
/// (which would lose whatever is newer in the file), never "unreadable". Because that text isn't a
/// faithful copy of the file, the file's original bytes go to Recovery before anything writes over
/// it. A U+FFFD the file encodes validly is ordinary text.
///
/// <para>These replace the tests of an earlier revision of this change, which asserted the
/// opposite (a file holding U+FFFD, valid or not, read as damage): an independent review
/// confirmed that made valid Plates load older backups or become unreadable.</para>
/// </summary>
public class InvalidTextEncodingTests
{
    /// <summary>The Plate's file with the first letter of its name replaced by a byte that is never valid UTF-8.</summary>
    private static byte[] CorruptName(byte[] file, string name)
    {
        var at = Encoding.ASCII.GetString(file).IndexOf($"\"{name}\"", StringComparison.Ordinal) + 1;
        Assert.True(at > 0);
        var damaged = file.ToArray();
        damaged[at] = 0xFF;
        return damaged;
    }

    [Fact]
    public async Task InvalidUtf8_InAFileThatParses_IsReadFromTheFileItself_AndKeptBeforeTheNextSave()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var seeded = await fixture.LoadAsync();
        var plateId = (await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "Kept")).PlateId;
        var path = fixture.Paths.GetPlatePath(plateId);
        var damaged = CorruptName(File.ReadAllBytes(path), "Kept");
        File.WriteAllBytes(path, damaged);

        var library = await fixture.LoadAsync();

        var summary = library.FindPlate(plateId)!;
        Assert.Equal(PlateStatus.Ready, summary.Status);
        Assert.Equal("�ept", summary.DisplayName);
        Assert.Equal(PlateLibraryService.InvalidTextPlateProblem, summary.Problem);
        Assert.Equal("Kept", JsonDocument.Parse(store.Backups[path]).RootElement.GetProperty("Name").GetString());
        Assert.DoesNotContain(fixture.Log.Messages, m => m.Contains("backup copy", StringComparison.Ordinal));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.Equal(damaged, File.ReadAllBytes(path));

        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        var kept = Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory));
        Assert.Equal(damaged, File.ReadAllBytes(kept));
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, reloaded.FindPlate(plateId)!.Status);
        Assert.Null(reloaded.FindPlate(plateId)!.Problem);
    }

    [Fact]
    public async Task InvalidUtf8_WithNoBackup_LoadsAsBefore_AndIsLeftUntouched()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Kept", fixture.Clock.Now), JsonOptions.Default));
        var path = fixture.Paths.GetPlatePath(plateId);
        var damaged = CorruptName(File.ReadAllBytes(path), "Kept");
        File.WriteAllBytes(path, damaged);

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        Assert.Equal("�ept", library.OpenDocumentForEditing(plateId).Name);
        Assert.Equal(damaged, File.ReadAllBytes(path));
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
    }

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf16-bom")]
    [InlineData("utf16be-bom")]
    [InlineData("utf32-bom")]
    [InlineData("utf32be-bom")]
    [InlineData("non-ascii")]
    public async Task ValidText_InAnyEncodingTheReaderHonours_LoadsFromTheFileItself(string encoding)
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = Guid.NewGuid();

        // A literal U+FFFD, validly encoded, is text like any other.
        const string name = "Ælfwyn � ✦ 🌸 Plate";
        var json = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Escaped", fixture.Clock.Now), JsonOptions.Default)
            .Replace("\"Escaped\"", $"\"{name}\"", StringComparison.Ordinal);
        Encoding withMark = encoding switch
        {
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf16-bom" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf16be-bom" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            "utf32-bom" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
            "utf32be-bom" => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        byte[] bytes = [.. withMark.GetPreamble(), .. withMark.GetBytes(json)];
        Directory.CreateDirectory(fixture.Paths.PlatesDirectory);
        File.WriteAllBytes(fixture.Paths.GetPlatePath(plateId), bytes);

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        Assert.Equal(name, library.FindPlate(plateId)!.DisplayName);
        Assert.Null(library.FindPlate(plateId)!.Problem);
        Assert.Equal(1, store.ReaderInvocations);
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
    }

    [Fact]
    public async Task InvalidUtf8_InATemplate_IsReadFromTheFileItself_AndKeptBeforeTheNextWrite()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new TemplateLibraryFixture(store);
        var seeded = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await seeded.SaveAsTemplateAsync(plate.PlateId, "Kept");
        var path = fixture.Paths.GetTemplatePath(templateId);
        var damaged = CorruptName(File.ReadAllBytes(path), "Kept");
        File.WriteAllBytes(path, damaged);

        var templates = fixture.CreateService();
        await templates.InitializeAsync();

        var summary = templates.FindTemplate(templateId)!;
        Assert.True(summary.IsReady);
        Assert.Equal("�ept", summary.DisplayName);
        Assert.Equal(TemplateLibraryService.InvalidTextTemplateProblem, summary.Problem);
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
        Assert.Equal(damaged, File.ReadAllBytes(path));

        await templates.RenameTemplateAsync(templateId, "Renamed");

        var kept = Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory));
        Assert.Equal(damaged, File.ReadAllBytes(kept));
        Assert.Equal("Renamed", templates.FindTemplate(templateId)!.DisplayName);
    }

    [Fact]
    public void TheDecoder_TellsAValidReplacementCharacterFromAnInvalidByte()
    {
        var valid = StoredTextDecoder.Decode([.. "{ \"Name\": \""u8, 0xEF, 0xBF, 0xBD, .. "\" }"u8]);
        Assert.False(valid.HasInvalidBytes);
        Assert.Equal("{ \"Name\": \"�\" }", valid.Text);

        var invalid = StoredTextDecoder.Decode([.. "{ \"Name\": \""u8, 0xFF, .. "\" }"u8]);
        Assert.True(invalid.HasInvalidBytes);
        Assert.Equal("{ \"Name\": \"�\" }", invalid.Text);
    }
}
