using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Assets;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The editor session's invariants around failure and timing: a document edit is all or nothing,
/// a baseline is never another Plate's, a save never races an edit in progress, and the
/// unsaved-changes prompt keeps its promises while a save is being written or has failed.
/// </summary>
public class EditorEditAtomicityTests
{
    private static (EditorDocumentCommands Commands, EditorCloseGuard Guard) OpenWindow(BasicHarness harness)
    {
        var commands = new EditorDocumentCommands(harness.Profiles, harness.Session);
        var guard = new EditorCloseGuard(harness.Session, commands);
        Assert.True(guard.PreOpenCheck(isOpen: true)); // the window has been open for a frame
        return (commands, guard);
    }

    /// <summary>Drives the guard's per-frame Advance until the chosen save has finished; its final answer.</summary>
    private static async Task<bool> AdvanceUntilSaveFinishesAsync(EditorCloseGuard guard)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (guard.Advance())
            {
                return true;
            }

            if (!guard.IsSaving)
            {
                return false;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("The save never finished.");
    }

    private static async Task FillToAsync(BasicHarness harness, int elementCount)
    {
        while (harness.Document.Elements.Count < elementCount)
        {
            Assert.NotNull(harness.Session.AddTextElement("x"));
        }

        Assert.True(await harness.Session.SaveProfileAsync());
        harness.Session.SyncWithCurrentProfile();
        harness.Session.ClearHistory();
        Assert.False(harness.Session.IsDirty);
        Assert.Equal(elementCount, harness.Document.Elements.Count);
    }

    // ---------------------------------------------------------------- all-or-nothing document edits

    [Fact]
    public async Task SectionReveal_AtTheElementLimit_ChangesNothing()
    {
        // 255 elements: the heading fits, its value would be the 257th. Without the rollback the
        // heading (and the Basic settings) stayed behind, dirty and not undoable.
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.Blank, null);
        await FillToAsync(harness, ProfileDocument.MaxElementCount - 1);
        var json = harness.Json();

        harness.Basic.SetSectionVisible(BasicSection.World, true);

        Assert.NotNull(harness.Session.ErrorMessage);
        Assert.Contains(ProfileDocument.MaxElementCount.ToString(), harness.Session.ErrorMessage);
        Assert.Equal(ProfileDocument.MaxElementCount - 1, harness.Document.Elements.Count);
        Assert.Null(BasicSections.Find(harness.Document, ProfileElementRole.BasicWorldHeading));
        Assert.Null(harness.Document.BasicPlate);
        Assert.Equal(json, harness.Json());
        Assert.False(harness.Session.CanUndo);
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public async Task AddPlaystyle_WhenTheRoleIsAnImage_ChangesNothing()
    {
        // A hand-edited or imported Plate can put the Playstyle role on an image; the settings
        // were written before the text step refused it.
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.Elements.RemoveAll(e => e.Role == ProfileElementRole.BasicPlaystyle);
        document.Elements.Add(new ImageProfileElement
        {
            AssetId = Guid.NewGuid(),
            Role = ProfileElementRole.BasicPlaystyle,
            Position = new Vector2(1, 2),
            Size = new Vector2(30, 40),
            ZIndex = document.Elements.Max(e => e.ZIndex) + 1,
        });
        using var harness = await BasicHarness.OpenDocumentAsync(document);
        Assert.IsType<ImageProfileElement>(BasicSections.Find(harness.Document, ProfileElementRole.BasicPlaystyle));
        var json = harness.Json();

        harness.Basic.AddPlaystyle("Roleplay");

        Assert.NotNull(harness.Session.ErrorMessage);
        Assert.Empty(harness.Document.BasicPlate?.Playstyles ?? []);
        Assert.Equal(json, harness.Json());
        Assert.False(harness.Session.CanUndo);
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public async Task ApplyDocumentEdit_ThrowingEdit_RollsBack()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var json = harness.Json();
        var profile = harness.Document;
        harness.Session.Select(profile.Elements[0].Id);

        var applied = harness.Session.ApplyDocumentEdit(() =>
        {
            profile.CanvasWidth = 1;
            harness.Profiles.AddTextElement("stray");
            throw new InvalidOperationException("Test failure.");
        });

        Assert.False(applied);
        Assert.Equal("Test failure.", harness.Session.ErrorMessage);
        Assert.Equal(json, harness.Json());
        Assert.False(harness.Session.CanUndo);
        Assert.False(harness.Session.IsDirty);
        Assert.Equal(profile.Elements[0].Id, harness.Session.SelectedElementId); // still there, so still selected
        Assert.DoesNotContain(harness.Fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContinuousDocumentEdit_ThrowingStep_RollsBackTheWholeRun()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var json = harness.Json();
        var profile = harness.Document;

        harness.Session.BeginOrContinueDocumentEdit(() => profile.CanvasWidth = 500);
        Assert.True(harness.Session.HasPendingDocumentEdit);
        harness.Session.BeginOrContinueDocumentEdit(() =>
        {
            profile.CanvasHeight = 1;
            throw new InvalidOperationException("Test failure.");
        });

        Assert.Equal("Test failure.", harness.Session.ErrorMessage);
        Assert.False(harness.Session.HasPendingDocumentEdit);
        Assert.Equal(json, harness.Json());
        harness.Session.CommitPendingEdits();
        Assert.False(harness.Session.CanUndo);
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public async Task ApplyDocumentEdit_WhoseRollbackFails_IsLogged()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var profile = harness.Document;

        // The edit throws after the Plate was closed underneath it, so the restore can't run either.
        var applied = harness.Session.ApplyDocumentEdit(() =>
        {
            profile.CanvasWidth = 1;
            harness.Profiles.CloseDocument();
            throw new InvalidOperationException("Test failure.");
        });

        Assert.False(applied);
        Assert.Contains(harness.Fixture.Log.Messages, m => m.StartsWith("E AetherFrame couldn't roll back", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Fixture.Log.Messages, m => m.Contains(harness.Fixture.Root, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- baseline

    [Fact]
    public async Task CaptureBaseline_FailedCapture_NeverKeepsAStaleBaseline()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        Assert.True(harness.Session.CanRevert);
        var other = await harness.Library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        harness.Profiles.OpenPlate(other.PlateId);

        // A document whose state can't be snapshotted (a corrupted in-memory list).
        harness.Document.Elements.Add(null!);
        harness.Session.SyncWithCurrentProfile();

        Assert.True(harness.Session.IsDirty); // no known clean state: never "Saved"
        Assert.False(harness.Session.CanRevert);
        Assert.Equal(EditorSession.BaselineFailedMessage, harness.Session.ErrorMessage);
        Assert.Contains(harness.Fixture.Log.Messages, m => m.StartsWith("E AetherFrame couldn't capture", StringComparison.Ordinal));

        // Reverting must never apply the previous Plate's content to this one.
        Assert.False(harness.Session.RevertToSaved(undoable: false));
        Assert.Single(harness.Document.Elements);
        harness.Session.SyncWithCurrentProfile(); // the next frame doesn't retry (or throw)
        Assert.False(harness.Session.CanRevert);
    }

    [Fact]
    public async Task FailedSave_DoesNotAdvanceTheBaseline()
    {
        var store = new FaultInjectingStore();
        using var harness = await BasicHarness.NewClassicAsync(store);
        harness.Session.AddTextElement("Unsaved");

        store.FailWrite = _ => true;
        Assert.False(await harness.Session.SaveProfileAsync());
        harness.Session.SyncWithCurrentProfile();

        Assert.True(harness.Session.IsDirty);
        Assert.True(harness.Session.CanRevert);
        Assert.DoesNotContain(harness.Library.GetSavedDocument(harness.PlateId)!.Elements, e => e is TextProfileElement { Text: "Unsaved" });

        store.FailWrite = null;
        Assert.True(await harness.Session.SaveProfileAsync());
        harness.Session.SyncWithCurrentProfile();

        Assert.False(harness.Session.IsDirty);
        Assert.Contains(harness.Library.GetSavedDocument(harness.PlateId)!.Elements, e => e is TextProfileElement { Text: "Unsaved" });
    }

    // ---------------------------------------------------------------- undo of a delete

    [Fact]
    public async Task UndoDelete_RestoresListPosition_ForTiedZIndex()
    {
        // An older or hand-made file where every element shares a ZIndex: ties paint in list order.
        var document = BasicDocuments.Blank();
        foreach (var text in new[] { "a", "b", "c" })
        {
            document.Elements.Add(new TextProfileElement { Text = text, ZIndex = 0, Position = new Vector2(10, 10), Size = new Vector2(100, 40) });
        }

        using var harness = await BasicHarness.OpenDocumentAsync(document);
        Assert.All(harness.Document.Elements, e => Assert.Equal(0, e.ZIndex));
        var order = harness.Document.Elements.Select(e => e.Id).ToList();
        var middle = order[1];

        harness.Session.RemoveElement(middle);
        Assert.Equal([order[0], order[2]], harness.Document.Elements.Select(e => e.Id));
        harness.Session.Undo();

        Assert.Equal(order, harness.Document.Elements.Select(e => e.Id));
        var painted = new List<ProfileElement>();
        ProfilePaintOrder.Fill(harness.Document, painted, includeHidden: true);
        Assert.Equal(order, painted.Select(e => e.Id));
        Assert.False(harness.Session.IsDirty);
        Assert.Equal(middle, harness.Session.SelectedElementId);

        harness.Session.Redo();
        harness.Session.Undo();
        Assert.Equal(order, harness.Document.Elements.Select(e => e.Id));
    }

    // ---------------------------------------------------------------- saving

    [Fact]
    public async Task SaveDuringDrag_CommitsTheDragAsOneEntry()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var element = BasicSections.Find(harness.Document, ProfileElementRole.BasicWorld)!;
        var original = element.Position;
        var start = element.Position + (element.Size / 2f);
        harness.Session.BeginDrag(element, start);
        harness.Session.UpdateInteraction(start + new Vector2(30, 30), snap: false, snapThreshold: 0f);
        Assert.Equal(ElementInteractionKind.Dragging, harness.Session.ActiveInteraction);

        Assert.True(await harness.Session.SaveProfileAsync());

        Assert.Equal(ElementInteractionKind.None, harness.Session.ActiveInteraction);
        Assert.Null(harness.Session.ErrorMessage);
        Assert.True(harness.Session.CanUndo);
        var saved = BasicSections.Find(harness.Library.GetSavedDocument(harness.PlateId)!, ProfileElementRole.BasicWorld)!;
        Assert.Equal(original + new Vector2(30, 30), saved.Position);
        harness.Session.SyncWithCurrentProfile();
        Assert.False(harness.Session.IsDirty);

        harness.Session.Undo();
        Assert.Equal(original, BasicSections.Find(harness.Document, ProfileElementRole.BasicWorld)!.Position);
        Assert.True(harness.Session.IsDirty);
        Assert.Null(harness.Session.ErrorMessage);
    }

    [Fact]
    public async Task AmendLastDocumentEdit_WhileSaving_IsRefusedAndRetried()
    {
        var store = new HeldWriteStore();
        using var harness = await BasicHarness.NewClassicAsync(store);

        // A font whose real widths differ from the editor's estimate, so a refinement is visible.
        var font = new WideFont();
        var identity = new BasicIdentitySession(harness.Profiles, harness.Session, harness.Character, font, new FakeTitles());
        identity.SetCustomTitle("Hello");
        identity.Commit();
        harness.Session.ClearHistory();
        var classic = BasicDocuments.Placements(harness.Document);
        font.FontReady = false;
        identity.SetLayout(IdentityTitleLayout.InlineBefore); // placed from estimated widths
        var estimated = BasicDocuments.Placements(harness.Document);
        Assert.NotEqual(classic, estimated);

        store.Hold();
        var save = harness.Session.SaveProfileAsync();
        await store.WriteStarted;
        Assert.True(harness.Profiles.IsBusy);
        font.FontReady = true;

        identity.RefineLayout();

        Assert.Equal(estimated, BasicDocuments.Placements(harness.Document));
        Assert.Null(harness.Session.ErrorMessage);

        store.Release();
        Assert.True(await save);
        harness.Session.SyncWithCurrentProfile();
        Assert.False(harness.Session.IsDirty);

        identity.RefineLayout();

        Assert.NotEqual(estimated, BasicDocuments.Placements(harness.Document));
        Assert.True(harness.Session.IsDirty);
        Assert.Null(harness.Session.ErrorMessage);

        // Still folded into the layout's own undo step.
        harness.Session.Undo();
        Assert.Equal(classic, BasicDocuments.Placements(harness.Document));
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public async Task AmendLastDocumentEdit_WhileSaving_ReturnsFalseAndTouchesNothing()
    {
        var store = new HeldWriteStore();
        using var harness = await BasicHarness.NewClassicAsync(store);
        Assert.True(harness.Session.ApplyDocumentEdit(() => harness.Document.CanvasWidth = 900));
        var profile = harness.Document;

        store.Hold();
        var save = harness.Session.SaveProfileAsync();
        await store.WriteStarted;

        Assert.False(harness.Session.AmendLastDocumentEdit(() => profile.CanvasWidth = 1));
        Assert.Equal(900f, profile.CanvasWidth);
        Assert.Null(harness.Session.ErrorMessage);

        store.Release();
        Assert.True(await save);
        Assert.True(harness.Session.AmendLastDocumentEdit(() => profile.CanvasWidth = 950));
        Assert.Equal(950f, profile.CanvasWidth);
    }

    // ---------------------------------------------------------------- the unsaved-changes prompt

    [Fact]
    public async Task Discard_WhileSaving_KeepsAsking()
    {
        var store = new HeldWriteStore();
        using var harness = await BasicHarness.NewClassicAsync(store);
        var (commands, guard) = OpenWindow(harness);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        // The action bar's Save, then the title-bar X while it is still being written.
        store.Hold();
        var save = harness.Session.SaveProfileAsync();
        await store.WriteStarted;
        Assert.True(guard.PreOpenCheck(isOpen: false));
        Assert.True(guard.IsAsking);
        Assert.False(guard.CanSave);

        Assert.False(guard.Discard());

        Assert.True(guard.IsAsking);
        Assert.Equal(AdventurePlateOrientation.Mirrored, BasicEditorSession.GetOrientation(harness.Document));
        Assert.True(guard.PreOpenCheck(isOpen: true)); // still open

        store.Release();
        Assert.True(await save);
        harness.Session.SyncWithCurrentProfile(); // the next frame adopts the saved state

        Assert.True(guard.Discard());
        Assert.False(guard.IsAsking);
        Assert.False(commands.IsDirty);
        Assert.Equal(AdventurePlateOrientation.Mirrored, harness.Library.GetSavedDocument(harness.PlateId)!.BasicPlate!.Orientation);
    }

    [Fact]
    public async Task RevertToSaved_WhileSaving_LeavesTheDocumentUntouched()
    {
        var store = new HeldWriteStore();
        using var harness = await BasicHarness.NewClassicAsync(store);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        store.Hold();
        var save = harness.Session.SaveProfileAsync();
        await store.WriteStarted;

        Assert.False(harness.Session.RevertToSaved(undoable: true));
        Assert.Equal(AdventurePlateOrientation.Mirrored, BasicEditorSession.GetOrientation(harness.Document));
        Assert.NotNull(harness.Session.ErrorMessage);

        store.Release();
        Assert.True(await save);
    }

    [Fact]
    public async Task ClosePrompt_SaveFails_KeepsTheEditorOpen_AndTheDirtyState()
    {
        var store = new FaultInjectingStore();
        using var harness = await BasicHarness.NewClassicAsync(store);
        var (commands, guard) = OpenWindow(harness);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        Assert.True(guard.PreOpenCheck(isOpen: false));
        Assert.True(guard.IsAsking);

        store.FailWrite = _ => true;
        guard.Save();
        Assert.False(await AdvanceUntilSaveFinishesAsync(guard));

        Assert.False(guard.IsAsking);
        Assert.False(guard.IsSaving);
        Assert.True(guard.PreOpenCheck(isOpen: true)); // the window stays open
        Assert.True(commands.IsDirty);
        Assert.True(commands.CanUndo);
        Assert.Equal(AdventurePlateOrientation.Mirrored, BasicEditorSession.GetOrientation(harness.Document));
        Assert.StartsWith(EditorSession.SaveFailedMessage, harness.Session.ErrorMessage);
        Assert.DoesNotContain(harness.Fixture.Root, harness.Session.ErrorMessage);
        Assert.Equal(AdventurePlateOrientation.Normal, harness.Library.GetSavedDocument(harness.PlateId)!.BasicPlate!.Orientation);

        // The next close asks again, and once the disk cooperates Save closes the window.
        Assert.True(guard.PreOpenCheck(isOpen: false));
        Assert.True(guard.IsAsking);
        store.FailWrite = null;
        guard.Save();
        Assert.True(await AdvanceUntilSaveFinishesAsync(guard));
        Assert.False(commands.IsDirty);
        Assert.False(guard.PreOpenCheck(isOpen: false));
    }


    /// <summary>A font wider than the editor's estimate (three quarters of the size per character), or none yet.</summary>
    private sealed class WideFont : IIdentityTextMeasurer
    {
        internal bool FontReady { get; set; } = true;

        public bool TryMeasureNaturalWidth(TextProfileElement element, out float width)
        {
            width = FontReady ? element.GetDisplayText().Length * element.FontSize * 0.75f : 0f;
            return FontReady;
        }

        public bool TryCountLines(TextProfileElement element, float fontSize, float maxWidth, out int lines)
        {
            lines = FontReady ? FakeTextWrap.CountLines(element.GetDisplayText(), fontSize * 0.75f, maxWidth) : 0;
            return FontReady;
        }
    }

    /// <summary>Plain files whose next write can be held mid-flight (after it has begun, before
    /// anything reaches disk) and then released, so a save can be observed in progress.</summary>
    private sealed class HeldWriteStore : IPlateFileStore
    {
        private readonly SystemFileStore files = new();
        private TaskCompletionSource? gate;
        private TaskCompletionSource writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WriteStarted => writeStarted.Task;

        internal void Hold()
        {
            writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void Release() => gate?.TrySetResult();

        public bool FileExists(string path) => files.FileExists(path);

        public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => files.ListFiles(directory, searchPattern);

        public Task ReadTextAsync(string path, Action<string> reader) => files.ReadTextAsync(path, reader);

        public async Task WriteTextAsync(string path, string contents)
        {
            if (gate is { } held)
            {
                writeStarted.TrySetResult();
                await held.Task.ConfigureAwait(false);
                gate = null;
            }

            await files.WriteTextAsync(path, contents).ConfigureAwait(false);
        }

        public void MoveFile(string sourcePath, string destinationPath) => files.MoveFile(sourcePath, destinationPath);

        public void CopyFile(string sourcePath, string destinationPath) => files.CopyFile(sourcePath, destinationPath);

        public void DeleteFile(string path) => files.DeleteFile(path);
    }
}
