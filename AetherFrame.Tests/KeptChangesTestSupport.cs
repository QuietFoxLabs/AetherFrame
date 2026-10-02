using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;

namespace AetherFrame.Tests;

/// <summary>
/// One game session over a Library folder, as the plugin wires it for unsaved changes kept at
/// unload: the Plate Library, the open Plate and its editor session, which editor shows it, the
/// unload's keeper (over <see cref="PluginFileStores.Unguarded"/>), and the next load's drafts and
/// offer (over <see cref="PluginFileStores.Guarded"/>). Two sessions in a row over the same folder are
/// an unload and the load that follows; two at once are two game clients sharing it.
/// </summary>
internal sealed class GameSession
{
    private int frame;

    private GameSession(LibraryFixture fixture, PlateLibraryService library, IPlateFileStore store, OwnedOperations operations)
    {
        Fixture = fixture;
        Library = library;
        Operations = operations;
        Profiles = new ProfileService(library);
        Assets = new AssetStorageService(fixture.Paths.AssetsDirectory, fixture.Paths.AssetStagingDirectory, new AssetMetadataStore(fixture.Paths.AssetMetadataDirectory));
        Session = new EditorSession(Profiles, Assets, new FakeImages(), fixture.Log, () => ++frame);
        Stores = new PluginFileStores(store, operations, fixture.Log);
        Keeper = new UnsavedChangesKeeper(Session, () => ActiveSurface, fixture.Paths, Stores, "AetherFrame test", fixture.Log, () => fixture.Clock.Now);
        Drafts = new DraftStore(fixture.Paths, Stores.Guarded, fixture.Log, () => fixture.Clock.Now);
        Offer = new KeptChangesOffer(Drafts, library, Profiles, Session, kind =>
        {
            Shown.Add(kind);
            ActiveSurface = kind;
        }, fixture.Log);
    }

    internal LibraryFixture Fixture { get; }

    internal PlateLibraryService Library { get; }

    internal OwnedOperations Operations { get; }

    internal ProfileService Profiles { get; }

    internal AssetStorageService Assets { get; }

    internal EditorSession Session { get; }

    internal PluginFileStores Stores { get; }

    internal UnsavedChangesKeeper Keeper { get; }

    internal DraftStore Drafts { get; }

    internal KeptChangesOffer Offer { get; }

    /// <summary>The editor showing the open Plate, as the editor coordinator would report it.</summary>
    internal EditorSurfaceKind? ActiveSurface { get; set; }

    /// <summary>Every editor the offer asked to show, in order.</summary>
    internal List<EditorSurfaceKind> Shown { get; } = new();

    internal ProfileDocument Document => Profiles.CurrentProfile!;

    /// <summary>
    /// A session over <paramref name="fixture"/>'s folder, its Library loaded through
    /// <paramref name="store"/> (the fixture's own by default).
    /// </summary>
    internal static async Task<GameSession> StartAsync(LibraryFixture fixture, IPlateFileStore? store = null, OwnedOperations? operations = null, Func<Func<Task>, Task>? dispatch = null)
    {
        var used = store ?? fixture.Store;
        var owned = operations ?? new OwnedOperations();
        var library = new PlateLibraryService(fixture.Paths, used, fixture.Log, () => fixture.Clock.Now, dispatch, owned);
        await library.InitializeAsync();
        return new GameSession(fixture, library, used, owned);
    }

    /// <summary>A new Plate, made through My Plates (Blank unless said), and its id.</summary>
    internal async Task<Guid> CreatePlateAsync(PlateStartingLayout layout = PlateStartingLayout.Blank, string? name = null) =>
        (await Library.CreatePlateAsync(layout, character: null, name)).PlateId;

    /// <summary>Opens a Plate the way My Plates does, and shows it in <paramref name="surface"/>.</summary>
    internal void Open(Guid plateId, EditorSurfaceKind? surface = EditorSurfaceKind.Advanced)
    {
        Profiles.OpenPlate(plateId);
        Session.SyncWithCurrentProfile();
        ActiveSurface = surface;
    }

    /// <summary>An ordinary edit, recorded in the history: a text element.</summary>
    internal Guid Edit(string text = "Kept text") => Session.AddTextElement(text)!.Value;

    /// <summary>What unloading does: read as the UI stops, then written.</summary>
    internal async Task UnloadAsync()
    {
        Keeper.Capture();
        await Keeper.WriteAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>What the next load does: reads and judges the drafts, then offers them.</summary>
    internal async Task<IReadOnlyList<KeptDraft>> LoadKeptChangesAsync(bool loggedIn = true)
    {
        var found = await KeptChangesReview.LoadAsync(Drafts, Library, Fixture.Log);
        Offer.Present(found, loggedIn);
        return found;
    }

    /// <summary>The offer's pending work, finished: a new Plate made, a save the question started.</summary>
    internal async Task SettleAsync()
    {
        for (var i = 0; i < 500 && (Offer.IsBusy || Offer.IsSavingForQuestion); i++)
        {
            await Task.Delay(5);
            Offer.Advance();
        }

        Offer.Advance();
    }
}

/// <summary>What is on disk around kept changes.</summary>
internal static class KeptFiles
{
    internal static string[] Drafts(PlateStoragePaths paths) =>
        Directory.Exists(paths.DraftsDirectory) ? Directory.GetFiles(paths.DraftsDirectory).Order(StringComparer.Ordinal).ToArray() : [];

    internal static string[] Trashed(PlateStoragePaths paths) =>
        Directory.Exists(paths.DraftTrashDirectory) ? Directory.GetFiles(paths.DraftTrashDirectory).Order(StringComparer.Ordinal).ToArray() : [];

    /// <summary>The bytes of every file under the folder, hashed, by relative path.</summary>
    internal static Dictionary<string, string> Snapshot(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(root, p), p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));

    /// <summary>A draft file as an earlier unload would have written it, from raw JSON.</summary>
    internal static string WriteDraftJson(PlateStoragePaths paths, Guid plateId, DateTime writtenUtc, Guid draftId, string json)
    {
        var path = paths.GetDraftPath(plateId, writtenUtc, draftId);
        Directory.CreateDirectory(paths.DraftsDirectory);
        File.WriteAllText(path, json);
        return path;
    }
}

/// <summary>Plain files that count every delete, by path.</summary>
internal sealed class DeleteCountingStore : IPlateFileStore
{
    private readonly SystemFileStore files = new();

    internal List<string> Deletes { get; } = new();

    public bool FileExists(string path) => files.FileExists(path);

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => files.ListFiles(directory, searchPattern);

    public Task ReadTextAsync(string path, Action<StoredText> reader) => files.ReadTextAsync(path, reader);

    public Task WriteTextAsync(string path, string contents) => files.WriteTextAsync(path, contents);

    public void MoveFile(string sourcePath, string destinationPath) => files.MoveFile(sourcePath, destinationPath);

    public void CopyFile(string sourcePath, string destinationPath) => files.CopyFile(sourcePath, destinationPath);

    public void DeleteFile(string path)
    {
        Deletes.Add(path);
        files.DeleteFile(path);
    }
}
