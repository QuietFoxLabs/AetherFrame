using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A file names the Plate, Template or character it holds only in the exact spelling the Library
/// writes. Any other spelling of the same id would let two files claim it while every write
/// targets the canonical name, so such a file is ignored (with a warning) and never touched.
/// </summary>
public class PlateStoragePathsTests
{
    private static readonly Guid Id = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    public static TheoryData<string, bool> GuidSpellings => new()
    {
        { Id.ToString("D"), true },
        { Id.ToString("D").ToUpperInvariant(), true },
        { Id.ToString("N"), false },
        { Id.ToString("B"), false },
        { Id.ToString("P"), false },
        { Id.ToString("X"), false },
        { " " + Id.ToString("D"), false },
        { Id.ToString("D") + " ", false },
        { Guid.Empty.ToString("D"), false },
        { "notes", false },
    };

    [Theory]
    [MemberData(nameof(GuidSpellings))]
    public void PlateAndTemplateFileNames_AreOnlyTheDashedSpelling(string stem, bool accepted)
    {
        var path = Path.Combine("Profiles", stem + ".json");

        Assert.Equal(accepted, PlateStoragePaths.TryParsePlateFileName(path, out var plateId));
        Assert.Equal(accepted, PlateStoragePaths.TryParseTemplateFileName(path, out var templateId));
        if (accepted)
        {
            Assert.Equal(Id, plateId);
            Assert.Equal(Id, templateId);
        }
    }

    [Theory]
    [InlineData("1001", true)]
    [InlineData("18446744073709551615", true)]
    [InlineData("0001001", false)]
    [InlineData("0", false)]
    [InlineData("+1001", false)]
    [InlineData(" 1001", false)]
    [InlineData("1001 ", false)]
    [InlineData("18446744073709551616", false)]
    [InlineData("1001.0", false)]
    public void BindingFileNames_AreOnlyPlainDecimal(string stem, bool accepted)
    {
        Assert.Equal(accepted, PlateStoragePaths.TryParseBindingFileName(Path.Combine("Characters", stem + ".json"), out var contentId));
        if (accepted)
        {
            Assert.Equal(ulong.Parse(stem), contentId);
        }
    }

    [Fact]
    public async Task ADashedAndADashlessFileForOnePlate_LoadTheDashedOne_AndTheOtherIsNeverTouched()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Canonical", fixture.Clock.Now), JsonOptions.Default));
        var impostorPath = Path.Combine(fixture.Paths.PlatesDirectory, plateId.ToString("N") + ".json");
        var impostor = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Impostor", fixture.Clock.Now), JsonOptions.Default);
        File.WriteAllText(impostorPath, impostor);

        var library = await fixture.LoadAsync();

        var plate = Assert.Single(library.GetOrderedPlates());
        Assert.Equal("Canonical", plate.DisplayName);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("W ") && m.Contains(plateId.ToString("N") + ".json"));

        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));
        await library.DeletePlateAsync(plateId);

        Assert.Equal(impostor, File.ReadAllText(impostorPath));
        Assert.False(File.Exists(fixture.Paths.GetPlatePath(plateId)));
    }

    [Fact]
    public async Task ABindingFileWithLeadingZeros_IsIgnored()
    {
        using var fixture = new LibraryFixture();
        Directory.CreateDirectory(fixture.Paths.CharactersDirectory);
        var padded = Path.Combine(fixture.Paths.CharactersDirectory, "0001001.json");
        File.WriteAllText(padded, LegacyData.VersionOneBinding(1001, null));

        var library = await fixture.LoadAsync();

        Assert.Null(library.GetBinding(1001));
        Assert.True(File.Exists(padded));
    }

    [Fact]
    public async Task ADashlessTemplateFile_IsIgnoredWithAWarning()
    {
        using var fixture = new TemplateLibraryFixture();
        var templateId = Guid.NewGuid();
        var document = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Inner", fixture.Clock.Now), JsonOptions.Default);
        Directory.CreateDirectory(fixture.Paths.TemplatesDirectory);
        File.WriteAllText(Path.Combine(fixture.Paths.TemplatesDirectory, templateId.ToString("N") + ".json"), TemplateSamples.Envelope(templateId, "Dashless", document));

        var templates = await fixture.LoadAsync();

        Assert.DoesNotContain(templates.GetOrderedTemplates(), t => t.TemplateId == templateId);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("W ") && m.Contains(templateId.ToString("N") + ".json"));
    }
}
