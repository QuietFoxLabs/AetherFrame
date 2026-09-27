using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// An element's "elementType" is recognized wherever it appears in the element's JSON (a tool that
/// sorts keys moves it), not only first; AetherFrame itself still writes it first.
/// </summary>
public class OutOfOrderDiscriminatorTests
{
    /// <summary>Rewrites every element so its "elementType" comes last.</summary>
    private static void MoveDiscriminatorsLast(JsonObject document)
    {
        var elements = document["Elements"]!.AsArray();
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i]!.AsObject();
            var reordered = new JsonObject();
            foreach (var (name, value) in element.Where(p => p.Key != ProfileElement.TypeDiscriminatorPropertyName).ToList())
            {
                reordered[name] = value?.DeepClone();
            }

            reordered[ProfileElement.TypeDiscriminatorPropertyName] = element[ProfileElement.TypeDiscriminatorPropertyName]!.DeepClone();
            elements[i] = reordered;
        }
    }

    private static string DocumentWithTrailingDiscriminators(Guid plateId, DateTime now)
    {
        var document = PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Sorted", now);
        document.Elements.Add(new TextProfileElement { Text = "hello", FontSize = 30f });
        document.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = 1 });
        var json = JsonNode.Parse(JsonSerializer.Serialize(document, JsonOptions.Default))!.AsObject();
        MoveDiscriminatorsLast(json);
        return json.ToJsonString(JsonOptions.Default);
    }

    [Fact]
    public void Elements_WithTheDiscriminatorLast_Deserialize_AndAreWrittenWithItFirst()
    {
        var raw = JsonNode.Parse(DocumentWithTrailingDiscriminators(Guid.NewGuid(), DateTime.UtcNow))!.AsObject();
        Assert.Equal(ProfileElement.TypeDiscriminatorPropertyName, raw["Elements"]![0]!.AsObject().Last().Key);

        var document = PlateDocuments.Deserialize(raw)!;

        Assert.Equal("hello", Assert.IsType<TextProfileElement>(document.Elements[0]).Text);
        Assert.IsType<ImageProfileElement>(document.Elements[1]);
        Assert.Null(document.UnrecognizedElements);
        var written = PlateDocuments.ToJson(document)["Elements"]!.AsArray();
        Assert.All(written, e => Assert.Equal(ProfileElement.TypeDiscriminatorPropertyName, e!.AsObject().First().Key));
    }

    [Fact]
    public async Task LocalPlate_WithTheDiscriminatorLast_LoadsReady_AndRoundTrips()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, DocumentWithTrailingDiscriminators(plateId, fixture.Clock.Now));

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        var document = library.OpenDocumentForEditing(plateId);
        Assert.Equal(30f, Assert.IsType<TextProfileElement>(document.Elements[0]).FontSize);
        await library.SavePlateDocumentAsync(document);
        var saved = JsonNode.Parse(fixture.ReadPlateJson(plateId))!["Elements"]!.AsArray();
        Assert.Equal(2, saved.Count);
        Assert.All(saved, e => Assert.Equal(ProfileElement.TypeDiscriminatorPropertyName, e!.AsObject().First().Key));
        Assert.Equal("hello", saved[0]!["Text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Package_WithTheDiscriminatorLast_ImportsTheElementAsText()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Sorted");
        var document = library.OpenDocumentForEditing(created.PlateId);
        document.Elements.Add(new TextProfileElement { Text = "hello", Position = new(100, 10), Size = new(200, 40) });
        await library.SavePlateDocumentAsync(document);
        var path = PackageFiles.Rewrite(fixture.Export(packages, created.PlateId), entries => PackageFiles.EditProfile(entries, MoveDiscriminatorsLast));

        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport, staged.DescribeForLog());
        var result = await packages.ImportAsync(staged);

        Assert.True(result.Succeeded, result.Error?.Message);
        var imported = library.OpenDocumentForEditing(result.PlateId);
        Assert.Equal("hello", Assert.IsType<TextProfileElement>(Assert.Single(imported.Elements)).Text);
    }
}
