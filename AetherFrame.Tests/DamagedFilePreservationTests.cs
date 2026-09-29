using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A file the store could only read from its backup copy is damaged on disk, but its bytes may be
/// newer than the backup (a write that had to bypass the storage's journal leaves the backup
/// stale). Those bytes go to Recovery before anything writes over the file; when that copy fails
/// at load (a full disk), no later write may replace them until a copy has been kept.
/// </summary>
public class DamagedFilePreservationTests
{
    private const string Damaged = "{ truncated";

    /// <summary>A file where the Recovery folder belongs: every copy into Recovery fails, as on a full disk.</summary>
    private static void BlockRecovery(PlateStoragePaths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RecoveryDirectory)!);
        File.WriteAllText(paths.RecoveryDirectory, "in the way");
    }

    private static void UnblockRecovery(PlateStoragePaths paths) => File.Delete(paths.RecoveryDirectory);

    private static string[] RecoveryContents(PlateStoragePaths paths) =>
        Directory.Exists(paths.RecoveryDirectory) ? Directory.GetFiles(paths.RecoveryDirectory).Select(File.ReadAllText).ToArray() : [];

    /// <summary>A Plate saved through the backup-keeping store, associated with Alice (so a binding
    /// and an index exist on disk and in the backup too).</summary>
    private static async Task<Guid> SeedAsync(LibraryFixture fixture)
    {
        var seeded = await fixture.LoadAsync();
        var created = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Kept");
        return created.PlateId;
    }

    [Theory]
    [InlineData("save")]
    [InlineData("rename")]
    public async Task DamagedPlateWithoutRecoveryCopy_IsNeverWrittenOver_UntilACopyIsKept(string operation)
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = await SeedAsync(fixture);
        var path = fixture.Paths.GetPlatePath(plateId);
        File.WriteAllText(path, Damaged);
        BlockRecovery(fixture.Paths);

        var library = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);

        Task Write() => operation == "save"
            ? library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId))
            : library.RenamePlateAsync(plateId, "Renamed");

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(Write);
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        Assert.False(UserFacingError.ContainsPath(UserFacingError.Describe(refused, "The Plate couldn't be saved.")));
        Assert.Equal(Damaged, File.ReadAllText(path));
        Assert.Equal("Kept", library.FindPlate(plateId)!.DisplayName);

        // Space freed: the damaged bytes are kept first, then the write lands.
        UnblockRecovery(fixture.Paths);
        await Write();

        Assert.Contains(Damaged, RecoveryContents(fixture.Paths));
        Assert.NotEqual(Damaged, File.ReadAllText(path));
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, reloaded.FindPlate(plateId)!.Status);
        Assert.Equal(operation == "save" ? "Kept" : "Renamed", reloaded.FindPlate(plateId)!.DisplayName);
    }

    [Fact]
    public async Task DamagedPlateWhoseCopySucceedsAtLoad_SavesAsBefore()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = await SeedAsync(fixture);
        File.WriteAllText(fixture.Paths.GetPlatePath(plateId), Damaged);

        var library = await fixture.LoadAsync();
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        Assert.Equal([Damaged], RecoveryContents(fixture.Paths));
    }

    [Fact]
    public async Task DamagedBindingWithoutRecoveryCopy_IsNeverWrittenOver_UntilACopyIsKept()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = await SeedAsync(fixture);
        var path = fixture.Paths.GetBindingPath(Characters.Alice.ContentId);
        File.WriteAllText(path, Damaged);
        BlockRecovery(fixture.Paths);

        var library = await fixture.LoadAsync();
        Assert.Equal(plateId, library.GetActivePlateId(Characters.Alice.ContentId));

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.SetActivePlateAsync(Characters.Alice, plateId));
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        Assert.Equal(Damaged, File.ReadAllText(path));

        UnblockRecovery(fixture.Paths);
        await library.SetActivePlateAsync(Characters.Alice, plateId);

        Assert.Contains(Damaged, RecoveryContents(fixture.Paths));
        Assert.NotEqual(Damaged, File.ReadAllText(path));
    }

    [Fact]
    public async Task DamagedIndexWithoutRecoveryCopy_IsNeverWrittenOver_UntilACopyIsKept()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var first = await SeedAsync(fixture);
        var second = (await (await fixture.LoadAsync()).CreatePlateAsync(PlateStartingLayout.Blank, null, "Second")).PlateId;
        File.WriteAllText(fixture.Paths.LibraryFile, Damaged);
        BlockRecovery(fixture.Paths);

        var library = await fixture.LoadAsync();

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.MovePlateAsync(first, second, placeAfter: false));
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        Assert.Equal(Damaged, File.ReadAllText(fixture.Paths.LibraryFile));

        // The refused move isn't kept in memory, so the same move is made again.
        UnblockRecovery(fixture.Paths);
        await library.MovePlateAsync(first, second, placeAfter: false);

        Assert.Contains(Damaged, RecoveryContents(fixture.Paths));
        Assert.Equal([first, second], fixture.ReadLibraryOrder());
    }

    /// <summary>
    /// A refused order write leaves the order as it was, in memory too: otherwise the player sees
    /// the move, and repeating it once space is freed is a no-op that never saves it.
    /// </summary>
    [Fact]
    public async Task RefusedMove_LeavesTheOrderAsItWas_AndTheSameMoveLaterSavesIt()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var first = await SeedAsync(fixture);
        var second = (await (await fixture.LoadAsync()).CreatePlateAsync(PlateStartingLayout.Blank, null, "Second")).PlateId;
        File.WriteAllText(fixture.Paths.LibraryFile, Damaged);
        BlockRecovery(fixture.Paths);
        var library = await fixture.LoadAsync();
        var shown = library.GetOrderedPlates().Select(p => p.PlateId).ToList();
        var generation = library.Generation;

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.MovePlateAsync(shown[1], shown[0], placeAfter: false));

        Assert.Equal(shown, library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.NotEqual(generation, library.Generation);
        Assert.Equal(Damaged, File.ReadAllText(fixture.Paths.LibraryFile));

        UnblockRecovery(fixture.Paths);
        await library.MovePlateAsync(shown[1], shown[0], placeAfter: false);

        Assert.Equal([shown[1], shown[0]], fixture.ReadLibraryOrder());
        Assert.Contains(Damaged, RecoveryContents(fixture.Paths));
        Assert.Equal([shown[1], shown[0]], (await fixture.LoadAsync()).GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal(new[] { first, second }.Order(), shown.Order());
    }

    [Fact]
    public async Task DamagedTemplateWithoutRecoveryCopy_IsNeverWrittenOver_UntilACopyIsKept()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new TemplateLibraryFixture(store);
        var seeded = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await seeded.SaveAsTemplateAsync(plate.PlateId, "Original");
        var path = fixture.Paths.GetTemplatePath(templateId);
        File.WriteAllText(path, Damaged);
        BlockRecovery(fixture.Paths);

        var templates = fixture.CreateService();
        await templates.InitializeAsync();
        Assert.True(templates.FindTemplate(templateId)!.IsReady);

        var refused = await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.RenameTemplateAsync(templateId, "Renamed"));
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        Assert.Equal(Damaged, File.ReadAllText(path));
        Assert.Equal("Original", templates.FindTemplate(templateId)!.DisplayName);

        UnblockRecovery(fixture.Paths);
        await templates.RenameTemplateAsync(templateId, "Renamed");

        Assert.Contains(Damaged, RecoveryContents(fixture.Paths));
        Assert.Equal("Renamed", templates.FindTemplate(templateId)!.DisplayName);
        Assert.NotEqual(Damaged, File.ReadAllText(path));
    }
}
