using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A Plate or Template file that couldn't be opened (locked by another program, access denied)
/// is left untouched like a damaged one, but it is most likely intact, so the player is told that
/// and how to get it back — never that it is damaged, which could make them give up on it.
/// </summary>
public class UnopenableFileDiagnosticsTests
{
    [Fact]
    public async Task LockedPlate_IsListedAsUnopenable_NotDamaged()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var locked = Guid.NewGuid();
        var damaged = Guid.NewGuid();
        fixture.WritePlateJson(locked, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, locked, "Locked", fixture.Clock.Now), JsonOptions.Default));
        fixture.WritePlateJson(damaged, "{ truncated");
        store.FailRead = p => p == fixture.Paths.GetPlatePath(locked);
        store.FaultFactory = p => new IOException($"The process cannot access the file '{p}' because it is being used by another process.");

        var library = await fixture.LoadAsync();

        var lockedSummary = library.FindPlate(locked)!;
        Assert.Equal(PlateStatus.Unreadable, lockedSummary.Status);
        Assert.Equal(PlateLibraryService.UnavailablePlateProblem, lockedSummary.Problem);
        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.RenamePlateAsync(locked, "x"));
        Assert.Contains("couldn't be opened", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("damaged", refused.Message, StringComparison.Ordinal);

        Assert.Equal(PlateLibraryService.DamagedPlateProblem, library.FindPlate(damaged)!.Problem);
        var damagedRefusal = await Assert.ThrowsAsync<PlateLibraryException>(() => library.RenamePlateAsync(damaged, "x"));
        Assert.Contains("damaged", damagedRefusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockedTemplate_IsListedAsUnopenable_NotDamaged()
    {
        var store = new FaultInjectingStore();
        using var fixture = new TemplateLibraryFixture(store);
        var seeded = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await seeded.SaveAsTemplateAsync(plate.PlateId, "Locked");
        store.FailRead = p => p == fixture.Paths.GetTemplatePath(templateId);

        var templates = fixture.CreateService();
        await templates.InitializeAsync();

        var summary = templates.FindTemplate(templateId)!;
        Assert.Equal(TemplateStatus.Unreadable, summary.Status);
        Assert.Equal(TemplateLibraryService.UnavailableTemplateProblem, summary.Problem);
        var refused = await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.RenameTemplateAsync(templateId, "x"));
        Assert.Contains("couldn't be opened", refused.Message, StringComparison.Ordinal);
    }
}
