using System;
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
/// AetherFrame only ever writes valid UTF-8, so a Plate file holding an invalid byte sequence is
/// damaged even when it still parses as JSON. It must be handled as damage — the intact backup
/// copy used and the damaged bytes kept in Recovery, or the file listed unreadable and left alone
/// — never loaded with U+FFFD in place of the damaged text, which the next save would then write
/// over the file and its backup alike.
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
    public async Task InvalidUtf8_InAFileThatStillParses_IsReadFromTheBackup_AndTheDamagedBytesAreKept()
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
        Assert.Equal("Kept", summary.DisplayName);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("backup copy", StringComparison.Ordinal));
        var kept = Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory));
        Assert.Equal(damaged, File.ReadAllBytes(kept));

        // The next save writes the intact name, not a replacement character.
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));
        Assert.DoesNotContain("�", fixture.ReadPlateJson(plateId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidUtf8_WithNoBackup_IsListedUnreadable_AndLeftUntouched()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Kept", fixture.Clock.Now), JsonOptions.Default));
        var path = fixture.Paths.GetPlatePath(plateId);
        var damaged = CorruptName(File.ReadAllBytes(path), "Kept");
        File.WriteAllBytes(path, damaged);

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Unreadable, library.FindPlate(plateId)!.Status);
        Assert.Throws<PlateLibraryException>(() => library.OpenDocumentForEditing(plateId));
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf16-bom")]
    [InlineData("non-ascii")]
    public async Task ValidText_InAnyEncodingTheReaderHonours_LoadsFromTheFileItself(string encoding)
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = Guid.NewGuid();
        const string name = "Ælfwyn ✦ Plate";
        var json = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Escaped", fixture.Clock.Now), JsonOptions.Default)
            .Replace("\"Escaped\"", $"\"{name}\"", StringComparison.Ordinal);
        byte[] bytes = encoding switch
        {
            "utf8-bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(json)],
            "utf16-bom" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(json)],
            _ => Encoding.UTF8.GetBytes(json),
        };
        Directory.CreateDirectory(fixture.Paths.PlatesDirectory);
        File.WriteAllBytes(fixture.Paths.GetPlatePath(plateId), bytes);

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        Assert.Equal(name, library.FindPlate(plateId)!.DisplayName);
        Assert.Equal(1, store.ReaderInvocations);
        Assert.True(LibraryFiles.RecoveryIsEmpty(fixture.Paths));
    }

    [Fact]
    public async Task InvalidUtf8_InATemplate_IsReadFromTheBackup_AndTheDamagedBytesAreKept()
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

        Assert.True(templates.FindTemplate(templateId)!.IsReady);
        Assert.Equal("Kept", templates.FindTemplate(templateId)!.DisplayName);
        var kept = Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory));
        Assert.Equal(damaged, File.ReadAllBytes(kept));
    }

    [Fact]
    public void ALiteralReplacementCharacter_IsDamage_ButAnEscapedOneIsText()
    {
        Assert.Throws<InvalidDataException>(() => VersionedJson.RejectUndecodableText("{ \"Name\": \"\uFFFD\" }", "Plate document"));
        VersionedJson.RejectUndecodableText("{ \"Name\": \"\\uFFFD\" }", "Plate document");
    }
}
