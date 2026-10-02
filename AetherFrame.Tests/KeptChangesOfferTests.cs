using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// What the "Unsaved changes kept" window says and offers (<see cref="KeptChangesOffer"/>): its
/// wording, each variant's buttons, one draft at a time with "1 of N", My Plates' reminder after
/// closing without an answer, when it opens, and the unsaved-changes question before restoring
/// over an open Plate's own changes, whichever Plate is open.
/// </summary>
public class KeptChangesOfferTests
{
    private static readonly string Open = ((char)0x201C).ToString();
    private static readonly string Close = ((char)0x201D).ToString();

    // ---------------------------------------------------------------- wording

    [Fact]
    public void TheOffer_SaysWhichPlate_WhichEditor_AndWhen()
    {
        var written = new DateTime(2026, 10, 2, 14, 3, 4, DateTimeKind.Utc);
        var when = written.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

        Assert.Equal(
            $"AetherFrame closed while {Open}Evening Look{Close} had unsaved changes (Basic editor, {when}). They were kept beside your Plates.",
            KeptChangesOffer.BodyText("Evening Look", DraftEditor.Basic, written));
        Assert.Equal(
            $"AetherFrame closed while {Open}Evening Look{Close} had unsaved changes (Advanced editor, {when}). They were kept beside your Plates.",
            KeptChangesOffer.BodyText("Evening Look", DraftEditor.Advanced, written));
        Assert.Equal(
            $"AetherFrame closed while {Open}Evening Look{Close} had unsaved changes ({when}). They were kept beside your Plates.",
            KeptChangesOffer.BodyText("Evening Look", DraftEditor.None, written));
        Assert.Equal(
            $"{Open}Evening Look{Close} was saved again after these changes were made, so restoring them over it would undo that save.",
            KeptChangesOffer.SavedAgainNote("Evening Look"));
        Assert.Equal(
            $"{Open}Evening Look{Close} can't be opened by this version of AetherFrame, so these changes can only be restored as a new Plate.",
            KeptChangesOffer.CannotOpenNote("Evening Look"));
        Assert.Equal(
            $"{Open}Evening Look{Close} is open with unsaved changes of its own. Discard them to restore these over it, or save them, and these can then be restored as a new Plate.",
            KeptChangesOffer.SamePlateQuestion("Evening Look"));
        Assert.Equal(
            $"{Open}Evening Look{Close} has unsaved changes. Save them before opening these?",
            KeptChangesOffer.OtherPlateQuestion("Evening Look"));
        Assert.Equal("The open Plate changed, so choose again.", KeptChangesOffer.OpenPlateChangedMessage);
        Assert.Equal("Unsaved changes kept", KeptChangesOffer.Title);
        Assert.Equal("Your saved Plate is unchanged.", KeptChangesOffer.Consequence);
    }

    [Fact]
    public void TheOffersSources_HoldNoRawSpecialCharacters()
    {
        // The curly quotes are built from code points. Every file the feature added is plain ASCII;
        // in every file it changed, only older comments hold anything else: em dashes, ellipses and
        // arrows, never in code or a string.
        var root = RepositoryPaths.Root().FullName;
        var added = new[]
        {
            "AetherFrame/UI/Library/KeptChangesOffer.cs",
            "AetherFrame/UI/Editor/UnsavedChangesKeeper.cs",
            "AetherFrame/Windows/KeptChangesWindow.cs",
            "AetherFrame/Services/Plates/DraftStore.cs",
            "AetherFrame/Services/Plates/KeptChanges.cs",
            "AetherFrame/Services/Lifecycle/PluginFileStores.cs",
            "AetherFrame/Persistence/DraftDocuments.cs",
            "AetherFrame/Domain/Plates/PlateDraft.cs",
            "AetherFrame.Tests/KeptChangesClaimTests.cs",
            "AetherFrame.Tests/KeptChangesConflictTests.cs",
            "AetherFrame.Tests/KeptChangesOfferTests.cs",
            "AetherFrame.Tests/KeptChangesReadTests.cs",
            "AetherFrame.Tests/KeptChangesTestSupport.cs",
            "AetherFrame.Tests/KeptChangesWriteTests.cs",
        };
        var changed = new[]
        {
            "AetherFrame/Domain/Plates/PlateNaming.cs",
            "AetherFrame/Persistence/PlateStoragePaths.cs",
            "AetherFrame/Persistence/Schema/PersistenceSchemas.cs",
            "AetherFrame/Plugin.cs",
            "AetherFrame/Services/Plates/PlateLibraryService.cs",
            "AetherFrame/Services/ProfileService.cs",
            "AetherFrame/UI/Editor/EditorSession.cs",
            "AetherFrame/UI/Library/PlateOpenGuard.cs",
            "AetherFrame/UI/Tutorial/TutorialScript.cs",
            "AetherFrame/Windows/PlateLibraryWindow.cs",
        };

        static bool Plain(char c) => c is '\r' or '\n' or (>= ' ' and <= '~');
        var inOlderComments = new[] { (char)0x2014, (char)0x2026, (char)0x2192 };
        foreach (var relative in added)
        {
            var source = File.ReadAllText(Path.Combine(root, relative));
            Assert.All(source, c => Assert.True(Plain(c), $"{relative}: U+{(int)c:X4}"));
        }

        foreach (var relative in changed)
        {
            var lines = File.ReadAllLines(Path.Combine(root, relative));
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var comment = line.TrimStart().StartsWith("//", StringComparison.Ordinal);
                Assert.All(line, c => Assert.True(Plain(c) || (comment && inOlderComments.Contains(c)), $"{relative}:{i + 1}: U+{(int)c:X4}"));
            }
        }
    }

    [Theory]
    [InlineData((int)KeptChangesChoice.Restore, (int)KeptChangesVariant.Restore, KeptChangesOffer.RestoreLabel, KeptChangesOffer.RestoreTooltip, KeptChangesOffer.DiscardTooltip)]
    [InlineData((int)KeptChangesChoice.SavedAgain, (int)KeptChangesVariant.SavedAgain, KeptChangesOffer.RestoreAsNewLabel, KeptChangesOffer.RestoreAsNewTooltip, KeptChangesOffer.DiscardTooltip)]
    [InlineData((int)KeptChangesChoice.CannotOpen, (int)KeptChangesVariant.CannotOpen, KeptChangesOffer.RestoreAsNewLabel, KeptChangesOffer.RestoreAsNewTooltip, KeptChangesOffer.DiscardTooltip)]
    [InlineData((int)KeptChangesChoice.Deleted, (int)KeptChangesVariant.Deleted, KeptChangesOffer.RestoreAsNewLabel, KeptChangesOffer.RestoreAsNewDeletedTooltip, KeptChangesOffer.DiscardDeletedTooltip)]
    [InlineData((int)KeptChangesChoice.Unavailable, (int)KeptChangesVariant.Unavailable, KeptChangesOffer.RestoreAsNewLabel, KeptChangesOffer.RestoreAsNewTooltip, null)]
    public async Task EachVariant_HasItsWordsAndButtons(int choiceValue, int variantValue, string primary, string tooltip, string? discardTooltip)
    {
        var choice = (KeptChangesChoice)choiceValue;
        var variant = (KeptChangesVariant)variantValue;
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);

        game.Offer.Present([Draft("Evening Look", choice)], loggedIn: true);

        Assert.Equal(variant, game.Offer.CurrentVariant);
        Assert.Equal(primary, game.Offer.PrimaryLabel);
        Assert.Equal(tooltip, game.Offer.PrimaryTooltip);
        Assert.Equal(discardTooltip is not null, game.Offer.OffersDiscard);
        Assert.Equal(discardTooltip, game.Offer.DiscardTooltipText);
        Assert.StartsWith($"AetherFrame closed while {Open}Evening Look{Close} had unsaved changes (Basic editor, ", game.Offer.Body, StringComparison.Ordinal);
        Assert.Equal(variant switch
        {
            KeptChangesVariant.Restore => null,
            KeptChangesVariant.SavedAgain => KeptChangesOffer.SavedAgainNote("Evening Look"),
            KeptChangesVariant.CannotOpen => KeptChangesOffer.CannotOpenNote("Evening Look"),
            KeptChangesVariant.Deleted => KeptChangesOffer.DeletedNote,
            _ => KeptChangesOffer.UnavailableNote,
        }, game.Offer.VariantNote);
        Assert.Equal(variant == KeptChangesVariant.Deleted ? null : KeptChangesOffer.Consequence, game.Offer.ConsequenceLine);
        Assert.Null(game.Offer.PositionText);
        Assert.Null(game.Offer.Question);
        Assert.Equal("Keep the saved Plate as it is. The kept changes move to AetherFrame's Trash folder.", KeptChangesOffer.DiscardTooltip);
        Assert.Equal("The kept changes move to AetherFrame's Trash folder.", KeptChangesOffer.DiscardDeletedTooltip);
        Assert.Equal("Keep them. My Plates reminds you, and AetherFrame asks again next time.", KeptChangesOffer.DecideLaterTooltip);
        Assert.Equal("Open the Plate with these changes. Nothing is saved until you choose Save.", KeptChangesOffer.RestoreTooltip);
        Assert.Equal("The saved Plate stays as it is.", KeptChangesOffer.RestoreAsNewTooltip);
    }

    // ---------------------------------------------------------------- one at a time, and later

    [Fact]
    public async Task SeveralDrafts_AreOfferedOneAtATime_NewestFirst_WithOneOfN()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var drafts = new[] { Draft("Newest", KeptChangesChoice.Deleted), Draft("Middle", KeptChangesChoice.Deleted), Draft("Oldest", KeptChangesChoice.Deleted) };

        game.Offer.Present(drafts, loggedIn: true);
        Assert.True(game.Offer.ConsumeOpenRequest());
        Assert.False(game.Offer.ConsumeOpenRequest());

        var seen = new List<(string?, Guid?)>();
        while (game.Offer.HasCurrent)
        {
            seen.Add((game.Offer.PositionText, game.Offer.CurrentPlateId));
            game.Offer.DecideLater();
        }

        Assert.Equal(
            new (string?, Guid?)[] { ("1 of 3", drafts[0].PlateId), ("2 of 3", drafts[1].PlateId), ("3 of 3", drafts[2].PlateId) },
            seen);
        Assert.Equal("Unsaved changes were kept for 3 Plates.", game.Offer.ReminderText);
    }

    [Fact]
    public async Task ClosingWithoutAnAnswer_KeepsEverything_MyPlatesReminds_AndReviewAsksAgain()
    {
        using var fixture = new LibraryFixture();
        await KeepEditsAsync(fixture, "First", "Second");
        var before = KeptFiles.Snapshot(fixture.Paths.DraftsDirectory);
        var game = await GameSession.StartAsync(fixture);
        await game.LoadKeptChangesAsync();
        Assert.True(game.Offer.ConsumeOpenRequest());
        Assert.Null(game.Offer.ReminderText);

        game.Offer.Closed();

        Assert.False(game.Offer.HasCurrent);
        Assert.Equal("Unsaved changes were kept for 2 Plates.", game.Offer.ReminderText);
        Assert.Equal(before, KeptFiles.Snapshot(fixture.Paths.DraftsDirectory));
        Assert.Null(game.Profiles.CurrentProfile);

        game.Offer.Review();

        Assert.True(game.Offer.ConsumeOpenRequest());
        Assert.Equal("1 of 2", game.Offer.PositionText);
        Assert.Null(game.Offer.ReminderText);

        game.Offer.Discard();
        game.Offer.Closed();
        Assert.Equal("Unsaved changes were kept for 1 Plate.", game.Offer.ReminderText);
    }

    [Fact]
    public async Task TheOffer_WaitsForALoggedInCharacter()
    {
        using var fixture = new LibraryFixture();
        await KeepEditsAsync(fixture, "Waiting");
        var game = await GameSession.StartAsync(fixture);

        await game.LoadKeptChangesAsync(loggedIn: false);
        Assert.False(game.Offer.ConsumeOpenRequest());
        Assert.False(game.Offer.HasCurrent);

        game.Offer.OnLogin();
        Assert.True(game.Offer.ConsumeOpenRequest());
        Assert.True(game.Offer.HasCurrent);

        // A later login asks nothing new: the offer was made.
        game.Offer.Closed();
        game.Offer.OnLogin();
        Assert.False(game.Offer.ConsumeOpenRequest());
    }

    [Fact]
    public async Task Discard_MovesTheDraftToTheTrash_AndTheSavedPlateStaysAsItIs()
    {
        using var fixture = new LibraryFixture();
        var plateIds = await KeepEditsAsync(fixture, "Discarded");
        var platePath = fixture.Paths.GetPlatePath(plateIds[0]);
        var savedBytes = File.ReadAllBytes(platePath);
        var draft = Assert.Single(KeptFiles.Drafts(fixture.Paths));
        var draftBytes = File.ReadAllBytes(draft);
        var game = await GameSession.StartAsync(fixture);
        await game.LoadKeptChangesAsync();

        game.Offer.Discard();

        Assert.False(game.Offer.HasCurrent);
        Assert.Null(game.Offer.ReminderText);
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
        Assert.Equal(draftBytes, File.ReadAllBytes(Assert.Single(KeptFiles.Trashed(fixture.Paths))));
        Assert.Equal(savedBytes, File.ReadAllBytes(platePath));
        Assert.Null(game.Profiles.CurrentProfile);
    }

    // ---------------------------------------------------------------- the open Plate's own unsaved changes

    [Fact]
    public async Task AnotherPlateOpenWithUnsavedChanges_IsAskedAbout_Cancel_ThenDiscard()
    {
        using var fixture = new LibraryFixture();
        var kept = (await KeepEditsAsync(fixture, "Kept"))[0];
        var game = await GameSession.StartAsync(fixture);
        var otherId = await game.CreatePlateAsync(name: "Other");
        game.Open(otherId);
        game.Edit("Change on the other Plate");
        await game.LoadKeptChangesAsync();

        game.Offer.Choose();

        Assert.Equal($"{Open}Other{Close} has unsaved changes. Save them before opening these?", game.Offer.Question);
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Equal(otherId, game.Profiles.OpenPlateId);

        game.Offer.AnswerCancel();
        Assert.Null(game.Offer.Question);
        Assert.True(game.Offer.HasCurrent);
        Assert.True(game.Session.IsDirty);

        game.Offer.Choose();
        game.Offer.AnswerDiscard();

        Assert.Null(game.Offer.Question);
        Assert.Equal(kept, game.Profiles.OpenPlateId);
        Assert.True(game.Session.IsDirty);
        Assert.DoesNotContain("Change on the other Plate", File.ReadAllText(fixture.Paths.GetPlatePath(otherId)), StringComparison.Ordinal);
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
    }

    [Fact]
    public async Task AnotherPlateOpenWithUnsavedChanges_CanBeSavedFirst()
    {
        using var fixture = new LibraryFixture();
        var kept = (await KeepEditsAsync(fixture, "Kept"))[0];
        var game = await GameSession.StartAsync(fixture);
        var otherId = await game.CreatePlateAsync(name: "Other");
        game.Open(otherId);
        game.Edit("Change on the other Plate");
        await game.LoadKeptChangesAsync();

        game.Offer.Choose();
        game.Offer.AnswerSave();
        await game.SettleAsync();

        Assert.Contains("Change on the other Plate", File.ReadAllText(fixture.Paths.GetPlatePath(otherId)), StringComparison.Ordinal);
        Assert.Equal(kept, game.Profiles.OpenPlateId);
        Assert.True(game.Session.IsDirty);
    }

    [Fact]
    public async Task TheSamePlateOpenWithUnsavedChanges_IsAskedAboutToo()
    {
        using var fixture = new LibraryFixture();
        var kept = (await KeepEditsAsync(fixture, "Kept"))[0];
        var game = await GameSession.StartAsync(fixture);
        game.Open(kept);
        game.Edit("A change of this session");
        await game.LoadKeptChangesAsync();

        game.Offer.Choose();

        Assert.Equal(KeptChangesOffer.SamePlateQuestion("Kept"), game.Offer.Question);
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Contains(game.Document.Elements, e => e is TextProfileElement { Text: "A change of this session" });

        game.Offer.AnswerDiscard();

        Assert.Equal(kept, game.Profiles.OpenPlateId);
        Assert.True(game.Session.IsDirty);
        Assert.DoesNotContain(game.Document.Elements, e => e is TextProfileElement { Text: "A change of this session" });
        Assert.Contains(game.Document.Elements, e => e is TextProfileElement { Text: "Kept text" });

        // One Undo: the saved Plate, with neither change.
        game.Session.Undo();
        Assert.False(game.Session.IsDirty);
        Assert.False(game.Session.CanUndo);
    }

    [Fact]
    public async Task TheSamePlateSavedAtTheQuestion_CanOnlyBeRestoredAsANewPlateAfterwards()
    {
        using var fixture = new LibraryFixture();
        var kept = (await KeepEditsAsync(fixture, "Kept"))[0];
        var game = await GameSession.StartAsync(fixture);
        game.Open(kept);
        game.Edit("A change of this session");
        await game.LoadKeptChangesAsync();

        game.Offer.Choose();
        game.Offer.AnswerSave();
        await game.SettleAsync();

        Assert.Equal(KeptChangesOffer.ChangedMeanwhileMessage, game.Offer.Error);
        Assert.Equal(KeptChangesVariant.SavedAgain, game.Offer.CurrentVariant);
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Contains("A change of this session", File.ReadAllText(fixture.Paths.GetPlatePath(kept)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSamePlateOpenAndClean_IsRestoredWithoutAQuestion()
    {
        using var fixture = new LibraryFixture();
        var kept = (await KeepEditsAsync(fixture, "Kept"))[0];
        var game = await GameSession.StartAsync(fixture);
        game.Open(kept);
        await game.LoadKeptChangesAsync();

        game.Offer.Choose();

        Assert.Null(game.Offer.Question);
        Assert.True(game.Session.IsDirty);
        Assert.Contains(game.Document.Elements, e => e is TextProfileElement { Text: "Kept text" });
    }

    [Fact]
    public async Task RestoreWhileASaveIsBeingWritten_SaysSo_AndTakesNothing()
    {
        var store = new HeldWriteStore();
        using var fixture = new LibraryFixture(store);
        var kept = (await KeepEditsAsync(fixture, "Kept"))[0];
        var game = await GameSession.StartAsync(fixture);
        var otherId = await game.CreatePlateAsync(name: "Other");
        game.Open(otherId);
        await game.LoadKeptChangesAsync();

        // The open Plate is clean, but its save (the editor's Save) is being written.
        store.Hold();
        var save = game.Session.SaveProfileAsync();
        await store.WriteStarted;
        Assert.True(game.Profiles.IsBusy);

        game.Offer.Choose();

        Assert.Equal(KeptChangesOffer.BusyMessage, game.Offer.Error);
        Assert.Null(game.Offer.Question);
        Assert.Equal(otherId, game.Profiles.OpenPlateId);
        Assert.Empty(game.Shown);
        Assert.True(game.Offer.HasCurrent);
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Empty(KeptFiles.Trashed(fixture.Paths));

        // Once it's saved, the same offer restores.
        store.Release();
        Assert.True(await save);
        game.Offer.Choose();

        Assert.Null(game.Offer.Error);
        Assert.Equal(kept, game.Profiles.OpenPlateId);
        Assert.True(game.Session.IsDirty);
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
    }

    [Fact]
    public async Task TwoDraftsForTheSamePlate_AreBothRestorable_AndNothingIsLost()
    {
        using var fixture = new LibraryFixture();
        var first = await GameSession.StartAsync(fixture);
        var plateId = await first.CreatePlateAsync(name: "Same");
        first.Open(plateId, EditorSurfaceKind.Basic);
        first.Edit("First edit");
        fixture.Clock.Tick();
        await first.UnloadAsync();

        // The next session leaves the offer unanswered and edits the same Plate again, unsaved.
        var second = await GameSession.StartAsync(fixture);
        await second.LoadKeptChangesAsync();
        second.Offer.Closed();
        second.Open(plateId, EditorSurfaceKind.Basic);
        second.Edit("Second edit");
        fixture.Clock.Tick();
        await second.UnloadAsync();

        var third = await GameSession.StartAsync(fixture);
        var found = await third.LoadKeptChangesAsync();
        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Equal(KeptChangesChoice.Restore, f.Choice));
        Assert.Equal("1 of 2", third.Offer.PositionText);
        third.Offer.DecideLater();
        third.Offer.DecideLater();
        Assert.Equal("Unsaved changes were kept for 1 Plate.", third.Offer.ReminderText);

        // The newer draft restores over the Plate.
        third.Offer.Review();
        third.Offer.Choose();
        Assert.Contains(third.Document.Elements, e => e is TextProfileElement { Text: "Second edit" });
        Assert.True(third.Session.IsDirty);

        // The older one: the Plate is open with the newer one's changes, unsaved. Saving them first
        // leaves the older draft only Restore as New Plate.
        Assert.True(third.Offer.HasCurrent);
        third.Offer.Choose();
        Assert.Equal(KeptChangesOffer.SamePlateQuestion("Same"), third.Offer.Question);
        third.Offer.AnswerSave();
        await third.SettleAsync();
        Assert.Equal(KeptChangesVariant.SavedAgain, third.Offer.CurrentVariant);
        third.Offer.Choose();
        await third.SettleAsync();

        Assert.False(third.Offer.HasCurrent);
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
        Assert.Equal(2, KeptFiles.Trashed(fixture.Paths).Length);
        var plates = third.Library.GetOrderedPlates();
        Assert.Equal(2, plates.Count);
        Assert.Contains("Second edit", fixture.ReadPlateJson(plateId), StringComparison.Ordinal);
        var copy = plates.Single(p => p.PlateId != plateId);
        Assert.Equal("Same (kept changes)", copy.DisplayName);
        Assert.Contains("First edit", fixture.ReadPlateJson(copy.PlateId), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the question, while it waits

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AQuestionLeftWaiting_WhileAnotherPlateIsOpenedAndEdited_IsDropped_AndNeverAnswersForIt(bool answerSave, bool aFramePasses)
    {
        using var fixture = new LibraryFixture();
        var kept = (await KeepEditsAsync(fixture, "Kept"))[0];
        var game = await GameSession.StartAsync(fixture);
        var otherId = await game.CreatePlateAsync(name: "Other");
        var thirdId = await game.CreatePlateAsync(name: "Third");
        game.Open(otherId);
        game.Edit("Work on Other");
        await game.LoadKeptChangesAsync();
        game.Offer.Choose();
        Assert.Equal(KeptChangesOffer.OtherPlateQuestion("Other"), game.Offer.Question);

        // Meanwhile, in the editor and My Plates (the window isn't modal): Other is saved, Third
        // is opened (no question: Other is clean) and edited.
        Assert.True(await game.Session.SaveProfileAsync());
        game.Open(thirdId);
        game.Edit("Work on Third, never saved");
        Assert.True(game.Session.IsDirty);
        var thirdBytes = File.ReadAllBytes(fixture.Paths.GetPlatePath(thirdId));
        if (aFramePasses)
        {
            game.Offer.Advance();
            Assert.Null(game.Offer.Question);
        }

        if (answerSave)
        {
            game.Offer.AnswerSave();
        }
        else
        {
            game.Offer.AnswerDiscard();
        }

        await game.SettleAsync();

        // Third's edit is untouched, unsaved and still undoable; the kept changes are still on offer.
        Assert.Equal(KeptChangesOffer.OpenPlateChangedMessage, game.Offer.Error);
        Assert.Null(game.Offer.Question);
        Assert.Equal(thirdId, game.Profiles.OpenPlateId);
        Assert.True(game.Session.IsDirty);
        Assert.True(game.Session.CanUndo);
        Assert.Contains(game.Document.Elements, e => e is TextProfileElement { Text: "Work on Third, never saved" });
        Assert.Equal(thirdBytes, File.ReadAllBytes(fixture.Paths.GetPlatePath(thirdId)));
        Assert.DoesNotContain("Work on Third", File.ReadAllText(fixture.Paths.GetPlatePath(thirdId)), StringComparison.Ordinal);
        Assert.Empty(game.Shown);
        Assert.True(game.Offer.HasCurrent);
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Empty(KeptFiles.Trashed(fixture.Paths));

        // Asked again, it asks about Third.
        game.Offer.Choose();
        Assert.Equal(KeptChangesOffer.OtherPlateQuestion("Third"), game.Offer.Question);
        Assert.NotEqual(kept, game.Profiles.OpenPlateId);
    }

    [Fact]
    public async Task ASaveThatLandsWhileTheQuestionWaits_DropsIt()
    {
        var store = new HeldWriteStore();
        using var fixture = new LibraryFixture(store);
        var kept = (await KeepEditsAsync(fixture, "Kept"))[0];
        var game = await GameSession.StartAsync(fixture);
        var otherId = await game.CreatePlateAsync(name: "Other");
        game.Open(otherId);
        game.Edit("Work on Other");
        await game.LoadKeptChangesAsync();
        game.Offer.Choose();
        Assert.NotNull(game.Offer.Question);

        // The editor's Save, while the question waits: it can't be answered while that is written.
        store.Hold();
        var save = game.Session.SaveProfileAsync();
        await store.WriteStarted;
        game.Offer.Advance();
        Assert.Equal(KeptChangesOffer.OtherPlateQuestion("Other"), game.Offer.Question);
        Assert.False(game.Offer.CanAnswerQuestion);

        store.Release();
        Assert.True(await save);
        game.Offer.Advance();

        Assert.Null(game.Offer.Question);
        Assert.Equal(KeptChangesOffer.OpenPlateChangedMessage, game.Offer.Error);
        Assert.Equal(otherId, game.Profiles.OpenPlateId);
        Assert.Single(KeptFiles.Drafts(fixture.Paths));

        // Other is clean now: choosing again restores without a question.
        game.Offer.Choose();
        Assert.Null(game.Offer.Question);
        Assert.Equal(kept, game.Profiles.OpenPlateId);
        Assert.True(game.Session.IsDirty);
        Assert.Contains("Work on Other", fixture.ReadPlateJson(otherId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheQuestionsSave_FinishingAfterAnotherPlateWasOpenedAndEdited_RestoresNothingOverIt()
    {
        var store = new HeldWriteStore();
        using var fixture = new LibraryFixture(store);
        var kept = (await KeepEditsAsync(fixture, "Kept"))[0];
        var game = await GameSession.StartAsync(fixture);
        var otherId = await game.CreatePlateAsync(name: "Other");
        var thirdId = await game.CreatePlateAsync(name: "Third");
        game.Open(otherId);
        game.Edit("Work on Other");
        await game.LoadKeptChangesAsync();
        game.Offer.Choose();

        store.Hold();
        game.Offer.AnswerSave();
        await store.WriteStarted;
        Assert.True(game.Offer.IsSavingForQuestion);
        store.Release();
        for (var i = 0; i < 500 && game.Profiles.IsBusy; i++)
        {
            await Task.Delay(5);
        }

        // Saved, but before the offer hears of it, Third is opened and edited.
        Assert.False(game.Profiles.IsBusy);
        game.Open(thirdId);
        game.Edit("Work on Third, never saved");
        await game.SettleAsync();

        Assert.Equal(KeptChangesOffer.OpenPlateChangedMessage, game.Offer.Error);
        Assert.Equal(thirdId, game.Profiles.OpenPlateId);
        Assert.Contains(game.Document.Elements, e => e is TextProfileElement { Text: "Work on Third, never saved" });
        Assert.NotEqual(kept, game.Profiles.OpenPlateId);
        Assert.Contains("Work on Other", fixture.ReadPlateJson(otherId), StringComparison.Ordinal);
        Assert.True(game.Offer.HasCurrent);
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
    }

    [Fact]
    public async Task TheQuestion_NamesTheOpenPlateAsItIsNow()
    {
        using var fixture = new LibraryFixture();
        await KeepEditsAsync(fixture, "Kept");
        var game = await GameSession.StartAsync(fixture);
        var otherId = await game.CreatePlateAsync(name: "Other");
        game.Open(otherId);
        game.Edit("Work on Other");
        await game.LoadKeptChangesAsync();
        game.Offer.Choose();
        Assert.Equal(KeptChangesOffer.OtherPlateQuestion("Other"), game.Offer.Question);

        await game.Library.RenamePlateAsync(otherId, "Renamed");
        game.Offer.Advance();

        Assert.Equal(KeptChangesOffer.OtherPlateQuestion("Renamed"), game.Offer.Question);
    }

    // ---------------------------------------------------------------- closing while a new Plate is made

    [Fact]
    public async Task ClosingWhileANewPlateIsMade_ThatThenFails_OpensTheWindowAgain_WithTheDraftStillOffered()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var ids = await KeepEditsAsync(fixture, "Deferred", "Failing");
        var failingId = ids[1];
        var game = await GameSession.StartAsync(fixture);
        await game.Library.DeletePlateAsync(failingId);
        Assert.Equal(2, (await game.LoadKeptChangesAsync()).Count);
        Assert.True(game.Offer.ConsumeOpenRequest());
        Assert.Equal(failingId, game.Offer.CurrentPlateId);

        store.FailWrite = path => path.StartsWith(fixture.Paths.PlatesDirectory, StringComparison.OrdinalIgnoreCase);
        game.Offer.Choose();
        Assert.True(game.Offer.IsBusy);

        // The window's close button (or Escape) while it reads "Restoring...": the other draft is
        // left for later, and My Plates counts it even while this one is being restored.
        game.Offer.Closed();
        Assert.Equal("Unsaved changes were kept for 1 Plate.", game.Offer.ReminderText);
        await game.SettleAsync();

        Assert.True(game.Offer.ConsumeOpenRequest());
        Assert.Equal(failingId, game.Offer.CurrentPlateId);
        Assert.EndsWith("The changes are still kept.", game.Offer.Error, StringComparison.Ordinal);
        Assert.Equal("Unsaved changes were kept for 1 Plate.", game.Offer.ReminderText);
        Assert.Equal(2, KeptFiles.Drafts(fixture.Paths).Length);
        Assert.Empty(KeptFiles.Trashed(fixture.Paths));

        // Offered again, it can be made once writing works.
        store.FailWrite = null;
        game.Offer.Choose();
        await game.SettleAsync();
        Assert.Contains(game.Library.GetOrderedPlates(), p => p.DisplayName == "Failing (kept changes)");
        Assert.Single(KeptFiles.Drafts(fixture.Paths));
    }

    [Fact]
    public async Task ReviewWhileANewPlateIsMade_ShowsIt_AndAFailureIsStillOffered()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var ids = await KeepEditsAsync(fixture, "Deferred", "Failing");
        var failingId = ids[1];
        var game = await GameSession.StartAsync(fixture);
        await game.Library.DeletePlateAsync(failingId);
        await game.LoadKeptChangesAsync();
        store.FailWrite = path => path.StartsWith(fixture.Paths.PlatesDirectory, StringComparison.OrdinalIgnoreCase);
        game.Offer.Choose();
        game.Offer.Closed();

        // My Plates' Review, while the new Plate is still being made.
        game.Offer.Review();
        Assert.True(game.Offer.ConsumeOpenRequest());
        Assert.Equal(failingId, game.Offer.CurrentPlateId);
        Assert.True(game.Offer.IsBusy);
        Assert.Equal("1 of 2", game.Offer.PositionText);

        await game.SettleAsync();
        Assert.Equal(failingId, game.Offer.CurrentPlateId);
        Assert.EndsWith("The changes are still kept.", game.Offer.Error, StringComparison.Ordinal);

        game.Offer.DecideLater();
        game.Offer.DecideLater();
        Assert.False(game.Offer.HasCurrent);
        Assert.Equal("Unsaved changes were kept for 2 Plates.", game.Offer.ReminderText);
        Assert.Equal(2, KeptFiles.Drafts(fixture.Paths).Length);
    }

    [Fact]
    public async Task ClosingWhileANewPlateIsMade_AndTheOpenPlateIsEdited_SaysWhereTheNewPlateIs()
    {
        var store = new HeldWriteStore();
        using var fixture = new LibraryFixture(store);
        var plateId = (await KeepEditsAsync(fixture, "Gone"))[0];
        var game = await GameSession.StartAsync(fixture);
        await game.Library.DeletePlateAsync(plateId);
        var openId = await game.CreatePlateAsync(name: "Open");
        game.Open(openId);
        await game.LoadKeptChangesAsync();
        Assert.True(game.Offer.ConsumeOpenRequest());

        store.Hold();
        game.Offer.Choose();
        await store.WriteStarted;
        game.Edit("Edited while it was made");
        game.Offer.Closed();
        Assert.False(game.Offer.ConsumeOpenRequest());

        store.Release();
        await game.SettleAsync();

        Assert.True(game.Offer.ConsumeOpenRequest());
        Assert.False(game.Offer.HasCurrent);
        Assert.Equal($"Restored as {Open}Gone (kept changes){Close} in My Plates.", game.Offer.Notice);
        Assert.Equal(openId, game.Profiles.OpenPlateId);
        Assert.True(game.Session.IsDirty);
        Assert.Contains(game.Library.GetOrderedPlates(), p => p.DisplayName == "Gone (kept changes)");
    }

    /// <summary>One unload per name: a Plate made, opened, given one unsaved edit, and kept as a draft.</summary>
    private static async Task<List<Guid>> KeepEditsAsync(LibraryFixture fixture, params string[] names)
    {
        var ids = new List<Guid>();
        foreach (var name in names)
        {
            var game = await GameSession.StartAsync(fixture);
            var plateId = await game.CreatePlateAsync(name: name);
            game.Open(plateId, EditorSurfaceKind.Basic);
            game.Edit();
            fixture.Clock.Tick();
            await game.UnloadAsync();
            ids.Add(plateId);
        }

        return ids;
    }

    /// <summary>A draft as a load would offer it, for a Plate that isn't in the Library.</summary>
    private static KeptDraft Draft(string name, KeptChangesChoice choice)
    {
        var document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), name, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
        var draft = new PlateDraft
        {
            DraftId = Guid.NewGuid(),
            PlateId = document.ProfileId,
            PlateName = name,
            WrittenAtUtc = new DateTime(2026, 10, 2, 14, 3, 4, DateTimeKind.Utc),
            Editor = DraftEditor.Basic,
            BaseUpdatedAtUtc = document.UpdatedAtUtc,
            Document = document,
        };
        return new KeptDraft(Path.Combine(Path.GetTempPath(), "not-on-disk", name + ".json"), draft, VersionedJson.Serialize(PlateDocuments.ToJson(document)), choice);
    }
}
