using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Save as New Plate's storage (interface task 1): <see cref="PlateLibraryService.SaveCopyAsync"/>,
/// reached through <see cref="ProfileService.SaveCopyOfCurrentAsync"/>. The editor's document as it
/// is becomes a new Plate exactly as Duplicate makes one from the saved state; the source stays
/// exactly as it was saved.
/// </summary>
public class PlateSaveCopyTests
{
    [Fact]
    public async Task TheLiveDocument_BecomesANewPlate_RightAfterItsSource()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var source = (await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, name: "Evening")).PlateId;
        await library.CreatePlateAsync(PlateStartingLayout.Blank, character: null, name: "Other");
        var savedSource = File.ReadAllText(fixture.Paths.GetPlatePath(source));

        var profiles = new ProfileService(library);
        profiles.OpenPlate(source);
        profiles.AddTextElement("Only in the editor");
        var now = fixture.Clock.Tick(60);

        var copy = await profiles.SaveCopyOfCurrentAsync();

        Assert.NotEqual(source, copy);
        var copied = library.GetSavedDocument(copy)!;
        Assert.Equal(copy, copied.ProfileId);
        Assert.Equal("Evening Copy", copied.Name);
        Assert.Equal(0, copied.Revision);
        Assert.Equal(now, copied.CreatedAtUtc);
        Assert.Equal(now, copied.UpdatedAtUtc);
        Assert.Contains(copied.Elements, e => e is TextProfileElement { Text: "Only in the editor" });

        // The source is untouched, still open, and still holds its unsaved change.
        Assert.Equal(savedSource, File.ReadAllText(fixture.Paths.GetPlatePath(source)));
        Assert.Equal(source, profiles.OpenPlateId);
        Assert.Contains(profiles.CurrentProfile!.Elements, e => e is TextProfileElement { Text: "Only in the editor" });
        Assert.False(profiles.IsBusy);

        var order = library.GetOrderedPlates().Select(p => p.PlateId).ToList();
        Assert.Equal(order.IndexOf(source) + 1, order.IndexOf(copy));
    }

    [Fact]
    public async Task TheCopy_KeepsTheSourcesCharacterLinks_ButIsNeverActive()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var source = (await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice)).PlateId;
        var profiles = new ProfileService(library);
        profiles.OpenPlate(source);

        var copy = await profiles.SaveCopyOfCurrentAsync();

        Assert.Equal(source, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Contains(copy, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Empty(library.FindPlate(copy)!.ActiveForContentIds);
    }

    [Fact]
    public async Task TheCopy_KeepsWhatThisBuildDoesntKnow()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var source = (await library.CreatePlateAsync(PlateStartingLayout.Blank, character: null)).PlateId;
        var profiles = new ProfileService(library);
        profiles.OpenPlate(source);
        profiles.CurrentProfile!.ExtensionData = new Dictionary<string, JsonElement> { ["FutureSetting"] = JsonSerializer.SerializeToElement(7) };

        var copy = await profiles.SaveCopyOfCurrentAsync();

        using var saved = JsonDocument.Parse(File.ReadAllText(fixture.Paths.GetPlatePath(copy)));
        Assert.Equal(7, saved.RootElement.GetProperty("FutureSetting").GetInt32());
    }

    [Fact]
    public async Task TheCopysName_FollowsARenameMadeWhileTheSourceWasOpen()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var source = (await library.CreatePlateAsync(PlateStartingLayout.Blank, character: null, name: "Old")).PlateId;
        var profiles = new ProfileService(library);
        profiles.OpenPlate(source);

        await library.RenamePlateAsync(source, "New");
        var copy = await profiles.SaveCopyOfCurrentAsync();

        Assert.Equal("New Copy", library.FindPlate(copy)!.DisplayName);
    }

    [Fact]
    public async Task ACopyOfAPlateThatNoLongerExists_IsRefused_AndWritesNothing()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var source = (await library.CreatePlateAsync(PlateStartingLayout.Blank, character: null)).PlateId;
        var snapshot = library.OpenDocumentForEditing(source);
        await library.DeletePlateAsync(source);

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.SaveCopyAsync(snapshot));

        Assert.Equal("That Plate no longer exists.", refused.Message);
        Assert.Empty(library.GetOrderedPlates());
    }

    [Fact]
    public async Task WithNoPlateOpen_ThereIsNothingToCopy()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var profiles = new ProfileService(library);

        await Assert.ThrowsAsync<InvalidOperationException>(profiles.SaveCopyOfCurrentAsync);
        Assert.False(profiles.IsBusy);
    }
}
