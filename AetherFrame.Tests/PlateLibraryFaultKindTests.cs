using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Library over failures shaped like Dalamud's reliable storage rather than plain IO: its
/// plugin-scoped store fails through a faulted thread-pool Task carrying a Win32Exception (here
/// error 32, "in use by another process"), not by throwing an IOException synchronously, and its
/// backup rows are keyed by the exact path. Every outcome must match the IOException tests in
/// <see cref="FailureInjectionTests"/> and <see cref="UserFacingErrorTests"/>: nothing here may
/// depend on the exception's type or on when it surfaces.
/// </summary>
public class PlateLibraryFaultKindTests
{
    private static FaultInjectingStore Win32FaultingStore() => new()
    {
        FaultAsync = true,
        FaultFactory = _ => new Win32Exception(32),
    };

    /// <summary>Every write from now on fails (the counter is already past zero).</summary>
    private static void FailEveryWrite(FaultInjectingStore store) => store.FailWriteAfter = 0;

    private static void ClearFaults(FaultInjectingStore store) => store.FailWriteAfter = null;

    [Fact]
    public async Task CreateFailure_FromAFaultedTask_AddsNothing()
    {
        var store = Win32FaultingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        FailEveryWrite(store);

        await Assert.ThrowsAsync<Win32Exception>(() => library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice));

        Assert.Empty(library.GetOrderedPlates());
        Assert.Null(library.GetBinding(Characters.Alice.ContentId));
        Assert.Empty(FilesIn(fixture.Paths.PlatesDirectory));
        Assert.Empty(FilesIn(fixture.Paths.CharactersDirectory));
        Assert.Equal(1, store.FailedOperations);

        ClearFaults(store);
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        Assert.True(created.BecameActive);
        Assert.Single(library.GetOrderedPlates());
    }

    [Fact]
    public async Task UseTemplateFailure_FromAFaultedTask_AddsNothing()
    {
        var store = Win32FaultingStore();
        using var fixture = new TemplateLibraryFixture(store);
        var templates = await fixture.LoadAsync();
        FailEveryWrite(store);

        await Assert.ThrowsAsync<Win32Exception>(() => templates.InstantiateAsync(BuiltInTemplateCatalog.BlankCanvasId, Characters.Alice));

        Assert.Empty(fixture.PlateLibrary.GetOrderedPlates());
        Assert.Null(fixture.PlateLibrary.GetBinding(Characters.Alice.ContentId));
        Assert.Empty(FilesIn(fixture.Paths.PlatesDirectory));
        Assert.Empty(FilesIn(fixture.Paths.CharactersDirectory));

        ClearFaults(store);
        var created = await templates.InstantiateAsync(BuiltInTemplateCatalog.BlankCanvasId, Characters.Alice);
        Assert.Equal(PlateStatus.Ready, fixture.PlateLibrary.FindPlate(created.PlateId)!.Status);
    }

    [Fact]
    public async Task RenameFailure_FromAFaultedTask_ChangesNothing()
    {
        var store = Win32FaultingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Original");
        var before = fixture.ReadPlateJson(plate.PlateId);
        var renamed = false;
        library.PlateRenamed += (_, _) => renamed = true;
        FailEveryWrite(store);

        await Assert.ThrowsAsync<Win32Exception>(() => library.RenamePlateAsync(plate.PlateId, "New"));

        Assert.Equal("Original", library.FindPlate(plate.PlateId)!.DisplayName);
        Assert.Equal(before, fixture.ReadPlateJson(plate.PlateId));
        Assert.False(renamed);

        ClearFaults(store);
        await library.RenamePlateAsync(plate.PlateId, "New");
        Assert.Equal("New", library.FindPlate(plate.PlateId)!.DisplayName);
        Assert.True(renamed);
    }

    [Fact]
    public async Task SaveFailure_FromAFaultedTask_LeavesTheSavedStateAndTheFileUnchanged()
    {
        var store = Win32FaultingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var before = fixture.ReadPlateJson(plate.PlateId);
        var saved = 0;
        library.PlateSaved += _ => saved++;
        var document = library.OpenDocumentForEditing(plate.PlateId);
        document.Revision = 42;
        FailEveryWrite(store);

        await Assert.ThrowsAsync<Win32Exception>(() => library.SavePlateDocumentAsync(document));

        Assert.Equal(before, fixture.ReadPlateJson(plate.PlateId));
        Assert.Equal(0, library.GetSavedDocument(plate.PlateId)!.Revision);
        Assert.Equal(0, saved);
        Assert.Equal(PlateStatus.Ready, library.FindPlate(plate.PlateId)!.Status);

        ClearFaults(store);
        await library.SavePlateDocumentAsync(document);
        Assert.Equal(42, library.GetSavedDocument(plate.PlateId)!.Revision);
        Assert.Equal(1, saved);
    }

    [Fact]
    public async Task SaveFailure_FromAFaultedTask_KeepsTheUnsavedEditsInTheEditor()
    {
        var store = Win32FaultingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var editor = new ProfileService(library);
        editor.OpenPlate(plate.PlateId);
        editor.AddTextElement("Precious");
        FailEveryWrite(store);

        await Assert.ThrowsAsync<Win32Exception>(() => editor.SaveCurrentProfileAsync());

        Assert.False(editor.IsBusy);
        Assert.Equal(0, editor.CurrentProfile!.Revision);
        Assert.Equal("Precious", Assert.IsType<TextProfileElement>(Assert.Single(editor.CurrentProfile.Elements)).Text);

        ClearFaults(store);
        await editor.SaveCurrentProfileAsync();
        Assert.Single(library.OpenDocumentForEditing(plate.PlateId).Elements);
    }

    [Fact]
    public async Task EditorSaveFailure_FromAFaultedTask_ShowsAPlainMessage_KeepsTheDraft_AndIsLogged()
    {
        var store = Win32FaultingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var profiles = new ProfileService(library);
        profiles.OpenPlate(created.PlateId);
        var assets = new AssetStorageService(fixture.Paths.AssetsDirectory, fixture.Paths.AssetStagingDirectory, new AssetMetadataStore(fixture.Paths.AssetMetadataDirectory));
        var session = new EditorSession(profiles, assets, new FakeImages(), fixture.Log, () => 1);
        session.SyncWithCurrentProfile();
        session.AddTextElement("Unsaved");
        FailEveryWrite(store);

        Assert.False(await session.SaveProfileAsync());

        Assert.StartsWith(EditorSession.SaveFailedMessage, session.ErrorMessage);
        Assert.False(UserFacingErrorContainsPath(session.ErrorMessage!, fixture.Root));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E AetherFrame failed to save", StringComparison.Ordinal));
        Assert.True(session.IsDirty);

        ClearFaults(store);
        Assert.True(await session.SaveProfileAsync());
        Assert.False(session.IsDirty);
        Assert.Single(library.OpenDocumentForEditing(created.PlateId).Elements);
    }

    [Fact]
    public async Task DeleteBindingAndOrderUpdateFailure_FromAFaultedTask_StillDeletes_AndThePlateStaysRecoverable()
    {
        var store = Win32FaultingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var original = fixture.ReadPlateJson(plate.PlateId);
        var deleted = false;
        library.PlateDeleted += _ => deleted = true;
        FailEveryWrite(store);

        var result = await library.DeletePlateAsync(plate.PlateId);

        Assert.Equal([Characters.Alice.ContentId], result.ClearedActiveForContentIds);
        Assert.True(deleted);
        Assert.Null(library.FindPlate(plate.PlateId));
        Assert.Null(library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Paths.PlateTrashDirectory))));
        Assert.Equal(2, store.FailedOperations);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("binding", StringComparison.Ordinal));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("order", StringComparison.Ordinal));
        Assert.All(fixture.Log.Messages, m => Assert.DoesNotContain(fixture.Root, m));

        // The binding and index on disk still name the deleted Plate; both stale references are harmless.
        ClearFaults(store);
        Assert.Equal(plate.PlateId, fixture.ReadBinding(Characters.Alice.ContentId).GetProperty("ActiveProfileId").GetGuid());
        Assert.Contains(plate.PlateId, fixture.ReadLibraryOrder());
        var reloaded = await fixture.LoadAsync();
        Assert.Null(reloaded.FindPlate(plate.PlateId));
        Assert.Null(reloaded.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Empty(reloaded.GetOrderedPlates());
    }

    [Fact]
    public async Task SetActiveFailure_FromAFaultedTask_KeepsThePreviousActivePlate()
    {
        var store = Win32FaultingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var other = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var before = fixture.ReadBindingJson(Characters.Alice.ContentId);
        FailEveryWrite(store);

        await Assert.ThrowsAsync<Win32Exception>(() => library.SetActivePlateAsync(Characters.Alice, other.PlateId));

        Assert.Equal(active.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.DoesNotContain(other.PlateId, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Equal(before, fixture.ReadBindingJson(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task DamagedPlate_WithABackupUnderADifferentlyCasedKey_IsNotRecovered()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = Guid.NewGuid();
        var path = fixture.Paths.GetPlatePath(plateId);
        var backup = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "From backup", fixture.Clock.Now), JsonOptions.Default);
        var differentlyCased = path.ToUpperInvariant();
        Assert.NotEqual(path, differentlyCased);
        store.Backups[differentlyCased] = backup;
        fixture.WritePlateJson(plateId, "{ truncated");

        var library = await fixture.LoadAsync();

        // Dalamud keys backup rows with SQLite BINARY equality, so a row under another spelling is
        // simply a different file — the damaged Plate stays damaged rather than reading a stale copy.
        Assert.Equal(PlateStatus.Unreadable, library.FindPlate(plateId)!.Status);
        Assert.Equal(1, store.ReaderInvocations);
        Assert.Equal("{ truncated", fixture.ReadPlateJson(plateId));
        Assert.Throws<PlateLibraryException>(() => library.OpenDocumentForEditing(plateId));

        // The exact key is what recovers it.
        store.Backups.Remove(differentlyCased);
        store.Backups[path] = backup;
        var recovered = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, recovered.FindPlate(plateId)!.Status);
        Assert.Equal("From backup", recovered.FindPlate(plateId)!.DisplayName);
    }

    private static string[] FilesIn(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory) : [];

    private static bool UserFacingErrorContainsPath(string message, string root) =>
        message.Contains(root, StringComparison.OrdinalIgnoreCase) || AetherFrame.Services.Diagnostics.UserFacingError.ContainsPath(message);
}
