using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Keeping an editor's unsaved changes when AetherFrame unloads (ROADMAP.md, section 8, task 4):
/// a draft only when something is unsaved, with whatever is in progress, written once under a name
/// of its own beside the Library, through the reliable chain without unloading's guard, and nothing
/// else in the folder touched.
/// </summary>
public class KeptChangesWriteTests
{
    // ---------------------------------------------------------------- only what is unsaved

    [Fact]
    public async Task ACleanEditor_KeepsNothing()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());

        await game.UnloadAsync();

        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
    }

    [Fact]
    public async Task NoPlateOpen_KeepsNothing()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        await game.CreatePlateAsync();

        await game.UnloadAsync();

        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
    }

    [Fact]
    public async Task AnEditedPlateThatWasDeleted_KeepsNothing()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync();
        game.Open(plateId);
        game.Edit();

        await game.Library.DeletePlateAsync(plateId);
        await game.UnloadAsync();

        Assert.Null(game.Profiles.CurrentProfile);
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
    }

    [Fact]
    public async Task AnEdit_IsKept_AsTheWholeDocument_WithTheEditorAndTheSavedVersionItStartedFrom()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: "Evening Look");
        game.Open(plateId, EditorSurfaceKind.Basic);
        var textId = game.Edit("Only in the draft");
        var saved = game.Library.FindPlate(plateId)!;

        await game.UnloadAsync();

        var path = Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.True(PlateStoragePaths.TryParseDraftFileName(path, out var namedPlate, out var writtenUtc, out _));
        Assert.Equal(plateId, namedPlate);
        Assert.Equal(fixture.Clock.Now, writtenUtc);

        var text = DraftDocuments.Parse(File.ReadAllText(path));
        Assert.Equal(DraftTextStatus.Ready, text.Status);
        var draft = text.Draft!;
        Assert.Equal(plateId, draft.PlateId);
        Assert.Equal("Evening Look", draft.PlateName);
        Assert.Equal(DraftEditor.Basic, draft.Editor);
        Assert.Equal("AetherFrame test", draft.Build);
        Assert.Equal(saved.Revision, draft.BaseRevision);
        Assert.Equal(saved.ModifiedUtc, draft.BaseUpdatedAtUtc);
        Assert.Contains(draft.Document.Elements, e => e.Id == textId && e is TextProfileElement { Text: "Only in the draft" });

        // Nothing but the document: no history, selection or zoom.
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("Undo", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Selected", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Zoom", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASliderStillBeingDragged_IsKept()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync();
        game.Open(plateId);
        var textId = game.Edit();
        await game.Profiles.SaveCurrentProfileAsync();
        game.Session.SyncWithCurrentProfile();
        game.Profiles.CloseDocument();
        game.Open(plateId);

        // The slider has moved, and the edit is not committed to history yet.
        game.Session.BeginOrContinueEdit(textId, element => ((TextProfileElement)element).FontSize = 77f);
        await game.UnloadAsync();

        var draft = DraftDocuments.Parse(File.ReadAllText(Assert.Single(KeptFiles.Drafts(fixture.Paths)))).Draft!;
        Assert.Equal(77f, ((TextProfileElement)draft.Document.Elements.Single(e => e.Id == textId)).FontSize);
    }

    [Fact]
    public async Task ADragStillInProgress_IsKept()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync();
        game.Open(plateId);
        var textId = game.Edit();
        await game.Profiles.SaveCurrentProfileAsync();
        game.Profiles.CloseDocument();
        game.Open(plateId);

        var element = game.Document.Elements.Single(e => e.Id == textId);
        var original = element.Position;
        var start = original + (element.Size / 2f);
        game.Session.BeginDrag(element, start);
        game.Session.UpdateInteraction(start + new Vector2(40f, 25f), snap: false, snapThreshold: 0f);
        var dragged = game.Document.Elements.Single(e => e.Id == textId).Position;
        await game.UnloadAsync();

        var draft = DraftDocuments.Parse(File.ReadAllText(Assert.Single(KeptFiles.Drafts(fixture.Paths)))).Draft!;
        Assert.Equal(dragged, draft.Document.Elements.Single(e => e.Id == textId).Position);
        Assert.NotEqual(original, dragged);
    }

    [Fact]
    public async Task ASaveInFlight_StillLeavesADraft_WhichTheNextLoadRetiresAsTheSavedVersion()
    {
        var store = new HeldWriteStore();
        using var fixture = new LibraryFixture(store);
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync();
        game.Open(plateId);
        game.Edit("Being saved");

        store.Hold();
        var save = game.Session.SaveProfileAsync();
        await store.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(game.Profiles.IsBusy);

        game.Keeper.Capture();
        store.Release();
        Assert.True(await save);
        await game.Keeper.WriteAsync(TimeSpan.FromSeconds(10));
        var draft = Assert.Single(KeptFiles.Drafts(fixture.Paths));

        var next = await GameSession.StartAsync(fixture);
        Assert.Empty(await next.LoadKeptChangesAsync());
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
        Assert.Equal(Path.GetFileName(draft), Path.GetFileName(Assert.Single(KeptFiles.Trashed(fixture.Paths))));
        Assert.False(next.Offer.HasCurrent);
    }

    // ---------------------------------------------------------------- write-once

    [Fact]
    public async Task TwoDraftsInTheSameMillisecond_GetTwoFiles_AndNeitherIsWrittenOver()
    {
        using var fixture = new LibraryFixture();
        var paths = fixture.Paths;
        var store = new DraftStore(paths, fixture.Store, fixture.Log, () => fixture.Clock.Now);
        var plateId = Guid.NewGuid();

        var first = await store.WriteAsync(store.Create(Copy(plateId, "First"), DraftEditor.Basic, "test"));
        var second = await store.WriteAsync(store.Create(Copy(plateId, "Second"), DraftEditor.Basic, "test"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(2, KeptFiles.Drafts(paths).Length);
        Assert.Contains("First", File.ReadAllText(first!));
        Assert.Contains("Second", File.ReadAllText(second!));
    }

    [Fact]
    public async Task ANameThatIsSomehowTaken_GetsANewId_AndTheFileThereStaysAsItIs()
    {
        using var fixture = new LibraryFixture();
        var ids = new[] { Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.Parse("22222222-2222-4222-8222-222222222222") };
        var next = 0;
        var store = new DraftStore(fixture.Paths, fixture.Store, fixture.Log, () => fixture.Clock.Now, () => ids[Math.Min(next++, 1)]);
        var plateId = Guid.NewGuid();
        var taken = KeptFiles.WriteDraftJson(fixture.Paths, plateId, fixture.Clock.Now, ids[0], "someone else's");

        var path = await store.WriteAsync(store.Create(Copy(plateId, "Mine"), DraftEditor.None, "test"));

        Assert.Equal("someone else's", File.ReadAllText(taken));
        Assert.Equal(fixture.Paths.GetDraftPath(plateId, fixture.Clock.Now, ids[1]), path);
        Assert.Equal(ids[1], DraftDocuments.Parse(File.ReadAllText(path!)).Draft!.DraftId);
    }

    // ---------------------------------------------------------------- the proof, failures

    [Fact]
    public async Task TextThatWouldNotLoad_IsNotWritten_AndIsLogged()
    {
        using var fixture = new LibraryFixture();
        var store = new DraftStore(fixture.Paths, fixture.Store, fixture.Log, () => fixture.Clock.Now);
        var copy = Copy(Guid.NewGuid(), "Unloadable");
        copy.Document.Elements.Add(new TextProfileElement { Text = "x", FontSize = float.NaN });

        var path = await store.WriteAsync(store.Create(copy, DraftEditor.Advanced, "test"));

        Assert.Null(path);
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("couldn't be read back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedWrite_IsLogged_AndUnloadingGoesOn()
    {
        var store = new FaultInjectingStore { FailWrite = path => path.Contains("Drafts", StringComparison.Ordinal), FaultAsync = true };
        using var fixture = new LibraryFixture(store);
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit();

        await game.UnloadAsync();

        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("couldn't keep the unsaved changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWriteThatNeverEnds_IsWaitedForOnlyUntilTheTimeout()
    {
        var store = new HeldWriteStore();
        using var fixture = new LibraryFixture(store);
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit();
        game.Keeper.Capture();

        store.Hold();
        var write = game.Keeper.WriteAsync(TimeSpan.FromMilliseconds(100));
        Assert.Same(write, await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(10))));
        await write;

        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("stopped waiting", StringComparison.Ordinal));
        store.Release();
    }

    [Fact]
    public async Task KeepingADraft_LeavesEveryOtherFileByteIdentical_BackupRowsIncluded()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync();
        game.Open(plateId);
        game.Edit();
        await game.Profiles.SaveCurrentProfileAsync();
        game.Session.SyncWithCurrentProfile();
        game.Edit("Unsaved");

        var before = KeptFiles.Snapshot(fixture.Root);
        var backupsBefore = store.Backups.ToDictionary(p => p.Key, p => p.Value);

        await game.UnloadAsync();

        var after = KeptFiles.Snapshot(fixture.Root);
        var draft = Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Equal(before, after.Where(p => p.Key != Path.GetRelativePath(fixture.Root, draft)).ToDictionary(p => p.Key, p => p.Value));
        Assert.Equal(backupsBefore, store.Backups.Where(p => p.Key != draft).ToDictionary(p => p.Key, p => p.Value));
        Assert.Equal(File.ReadAllText(draft), store.Backups[draft]);
    }

    // ---------------------------------------------------------------- how unloading writes

    [Fact]
    public async Task Unloading_KeepsTheDraft_WhenTeardownRunsOffTheFrameworkThread_AndNeverThroughDispatch()
    {
        var dispatched = 0;
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture, dispatch: work =>
        {
            Interlocked.Increment(ref dispatched);
            return work();
        });
        game.Open(await game.CreatePlateAsync());
        game.Edit();
        Volatile.Write(ref dispatched, 0);

        // The framework thread never picks the teardown up: it runs where unloading runs.
        var scheduled = false;
        await Task.Run(() => FrameworkThreadTeardown.RunAsync(
            "UI shutdown",
            game.Keeper.Capture,
            _ =>
            {
                scheduled = true;
                return new TaskCompletionSource().Task;
            },
            () => false,
            TimeSpan.FromMilliseconds(50),
            fixture.Log));
        await Task.Run(() => game.Keeper.WriteAsync(TimeSpan.FromSeconds(10)));

        Assert.True(scheduled);
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Equal(0, Volatile.Read(ref dispatched));
    }

    [Fact]
    public async Task Unloading_KeepsTheDraft_AfterRunningOperationsWereAbandoned()
    {
        using var fixture = new LibraryFixture();
        var operations = new OwnedOperations();
        var game = await GameSession.StartAsync(fixture, operations: operations);
        game.Open(await game.CreatePlateAsync());
        game.Edit();
        game.Keeper.Capture();

        // An operation that outlasts the wait is abandoned: the Libraries' store refuses every step now.
        Assert.True(operations.TryBegin(out var stuck));
        Assert.False(await operations.ShutdownAsync(TimeSpan.FromMilliseconds(20)));
        Assert.Throws<OperationAbandonedException>(() => game.Stores.Guarded.FileExists(fixture.Paths.DraftsDirectory));

        await game.Keeper.WriteAsync(TimeSpan.FromSeconds(10));

        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        stuck.Dispose();
    }

    [Fact]
    public async Task CapturingTwice_KeepsOneDraft()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit();

        await game.UnloadAsync();
        await game.UnloadAsync();

        Assert.Single(KeptFiles.Drafts(fixture.Paths));
    }

    // ---------------------------------------------------------------- privacy

    [Fact]
    public async Task ADraft_HoldsNoContentId()
    {
        const ulong owner = 18014398509481984UL;
        using var fixture = new LibraryFixture();
        var plateId = Guid.Parse("0f0f0f0f-1111-4111-8111-000000000777");
        fixture.WritePlateJson(plateId, LegacyData.VersionOneDocument(plateId, owner, "Owned"));
        var game = await GameSession.StartAsync(fixture);
        game.Open(plateId);
        Assert.Equal(owner, game.Document.OwnerContentId);
        game.Edit();

        await game.UnloadAsync();

        var json = File.ReadAllText(Assert.Single(KeptFiles.Drafts(fixture.Paths)));
        Assert.DoesNotContain(owner.ToString(System.Globalization.CultureInfo.InvariantCulture), json);
        Assert.Equal(0UL, DraftDocuments.Parse(json).Draft!.Document.OwnerContentId);
    }

    [Fact]
    public async Task WhatKeepingLogs_NamesFilesThroughLogPrivacy_AndNoFolder()
    {
        var store = new FaultInjectingStore { FailMove = path => path.Contains("Drafts", StringComparison.Ordinal) };
        using var fixture = new LibraryFixture(store);
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit();
        await game.UnloadAsync();
        var path = Assert.Single(KeptFiles.Drafts(fixture.Paths));
        File.WriteAllText(Path.Combine(fixture.Paths.DraftsDirectory, "1001.json"), "{}");

        await game.Drafts.ReadNewestAsync();
        Assert.Equal(DraftClaim.Failed, game.Drafts.Claim(path));

        var lines = fixture.Log.Messages.Where(m => m.Contains("kept", StringComparison.OrdinalIgnoreCase) || m.Contains("Drafts", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.DoesNotContain(fixture.Root, line, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, line => line.Contains(LogPrivacy.FileName(path), StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(LogPrivacy.CharacterBindingFile, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("1001.json", StringComparison.Ordinal));
    }

    /// <summary>A copy of an open document as ProfileService makes one, for writing drafts directly.</summary>
    private static ProfileService.OpenDocumentCopy Copy(Guid plateId, string name)
    {
        var document = PlateFactory.Create(PlateStartingLayout.Blank, plateId, name, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
        return new ProfileService.OpenDocumentCopy(document, document, 0, document.UpdatedAtUtc);
    }
}
