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
        Assert.Equal("Unsaved changes kept", KeptChangesOffer.Title);
        Assert.Equal("Your saved Plate is unchanged.", KeptChangesOffer.Consequence);
    }

    [Fact]
    public void TheOffersSources_HoldNoRawSpecialCharacters()
    {
        // The curly quotes are built from code points: every source of the feature is plain ASCII.
        var project = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame");
        foreach (var relative in new[]
        {
            Path.Combine("UI", "Library", "KeptChangesOffer.cs"),
            Path.Combine("UI", "Editor", "UnsavedChangesKeeper.cs"),
            Path.Combine("Windows", "KeptChangesWindow.cs"),
            Path.Combine("Services", "Plates", "DraftStore.cs"),
            Path.Combine("Services", "Plates", "KeptChanges.cs"),
            Path.Combine("Persistence", "DraftDocuments.cs"),
            Path.Combine("Domain", "Plates", "PlateDraft.cs"),
        })
        {
            var source = File.ReadAllText(Path.Combine(project, relative));
            Assert.All(source, c => Assert.True(c is '\r' or '\n' or (>= ' ' and <= '~'), $"{relative}: U+{(int)c:X4}"));
        }
    }

    [Theory]
    [InlineData((int)KeptChangesChoice.Restore, (int)KeptChangesVariant.Restore, KeptChangesOffer.RestoreLabel, KeptChangesOffer.RestoreTooltip, true)]
    [InlineData((int)KeptChangesChoice.SavedAgain, (int)KeptChangesVariant.SavedAgain, KeptChangesOffer.RestoreAsNewLabel, KeptChangesOffer.RestoreAsNewTooltip, true)]
    [InlineData((int)KeptChangesChoice.Deleted, (int)KeptChangesVariant.Deleted, KeptChangesOffer.RestoreAsNewLabel, KeptChangesOffer.RestoreAsNewDeletedTooltip, true)]
    [InlineData((int)KeptChangesChoice.Unavailable, (int)KeptChangesVariant.Unavailable, KeptChangesOffer.RestoreAsNewLabel, KeptChangesOffer.RestoreAsNewTooltip, false)]
    public async Task EachVariant_HasItsWordsAndButtons(int choiceValue, int variantValue, string primary, string tooltip, bool discard)
    {
        var choice = (KeptChangesChoice)choiceValue;
        var variant = (KeptChangesVariant)variantValue;
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);

        game.Offer.Present([Draft("Evening Look", choice)], loggedIn: true);

        Assert.Equal(variant, game.Offer.CurrentVariant);
        Assert.Equal(primary, game.Offer.PrimaryLabel);
        Assert.Equal(tooltip, game.Offer.PrimaryTooltip);
        Assert.Equal(discard, game.Offer.OffersDiscard);
        Assert.StartsWith($"AetherFrame closed while {Open}Evening Look{Close} had unsaved changes (Basic editor, ", game.Offer.Body, StringComparison.Ordinal);
        Assert.Equal(variant switch
        {
            KeptChangesVariant.Restore => null,
            KeptChangesVariant.SavedAgain => KeptChangesOffer.SavedAgainNote("Evening Look"),
            KeptChangesVariant.Deleted => KeptChangesOffer.DeletedNote,
            _ => KeptChangesOffer.UnavailableNote,
        }, game.Offer.VariantNote);
        Assert.Equal(variant == KeptChangesVariant.Deleted ? null : KeptChangesOffer.Consequence, game.Offer.ConsequenceLine);
        Assert.Null(game.Offer.PositionText);
        Assert.Null(game.Offer.Question);
        Assert.Equal("Keep the saved Plate as it is. The kept changes move to AetherFrame's Trash folder.", KeptChangesOffer.DiscardTooltip);
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

        Assert.Equal($"{Open}Kept{Close} is open with other unsaved changes. Save or discard them first?", game.Offer.Question);
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
