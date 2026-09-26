using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Element ids in a hand-edited file may be missing or repeated; the editors key selection,
/// removal and undo by id, so a repeated id would make one delete remove two elements. Loading for
/// editing gives such elements fresh ids in memory, exactly as Components already get.
/// </summary>
public class ElementIdRepairTests
{
    [Fact]
    public void NormalizeElementIds_GivesFreshIdsToMissingAndRepeatedOnes_AndChangesNothingElse()
    {
        var shared = Guid.NewGuid();
        var document = BasicDocuments.Blank();
        document.Elements.Add(new TextProfileElement { Id = shared, Text = "first", ZIndex = 3 });
        document.Elements.Add(new TextProfileElement { Id = shared, Text = "second", ZIndex = 3 });
        document.Elements.Add(new ImageProfileElement { Id = Guid.Empty, ZIndex = 1 });

        Assert.True(document.NormalizeElementIds());

        Assert.Equal(shared, document.Elements[0].Id);
        Assert.Equal(3, document.Elements.Select(e => e.Id).Distinct().Count());
        Assert.DoesNotContain(document.Elements, e => e.Id == Guid.Empty);
        Assert.Equal(["first", "second"], document.Elements.OfType<TextProfileElement>().Select(t => t.Text));
        Assert.Equal([3, 3, 1], document.Elements.Select(e => e.ZIndex));
        Assert.False(document.NormalizeElementIds());
    }

    [Fact]
    public async Task DuplicateElementIds_AreRepairedInMemory_AndNotWrittenUntilSave()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var document = PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Twins", fixture.Clock.Now);
        document.Elements.Add(new TextProfileElement { Id = shared, Text = "keep" });
        document.Elements.Add(new TextProfileElement { Id = shared, Text = "remove" });
        var json = JsonSerializer.Serialize(document, JsonOptions.Default);
        fixture.WritePlateJson(plateId, json);

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        Assert.Equal(json, fixture.ReadPlateJson(plateId));
        var opened = library.OpenDocumentForEditing(plateId);
        Assert.Equal(2, opened.Elements.Select(e => e.Id).Distinct().Count());
        Assert.Equal(shared, opened.Elements[0].Id);

        var profiles = new ProfileService(library);
        profiles.OpenPlate(plateId);
        var removed = profiles.CurrentProfile!.Elements[1];
        profiles.RemoveElement(removed.Id);
        Assert.Equal("keep", ((TextProfileElement)Assert.Single(profiles.CurrentProfile.Elements)).Text);
        await profiles.SaveCurrentProfileAsync();

        var saved = JsonNode.Parse(fixture.ReadPlateJson(plateId))!["Elements"]!.AsArray();
        Assert.Equal("keep", Assert.Single(saved)!["Text"]!.GetValue<string>());
        Assert.Equal(shared, Guid.Parse(saved[0]!["Id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ElementIdRepair_KeepsTheEditorClean_AndSavesUniqueIds()
    {
        var shared = Guid.NewGuid();
        var document = BasicDocuments.Blank();
        document.Elements.Add(new TextProfileElement { Id = shared, Text = "a" });
        document.Elements.Add(new TextProfileElement { Id = shared, Text = "b" });
        document.Elements.Add(new TextProfileElement { Id = Guid.Empty, Text = "c" });

        using var harness = await BasicHarness.OpenDocumentAsync(document);

        Assert.False(harness.Session.IsDirty);
        Assert.True(await harness.Session.SaveProfileAsync(), harness.Session.ErrorMessage);
        var ids = JsonNode.Parse(harness.Fixture.ReadPlateJson(harness.PlateId))!["Elements"]!.AsArray().Select(e => Guid.Parse(e!["Id"]!.GetValue<string>())).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, ids);
    }
}
