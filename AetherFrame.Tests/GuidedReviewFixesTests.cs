using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Theme;
using AetherFrame.UI.Tutorial;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// What GPT's review of the onboarding pull request at <c>2b1441a</c> (October 6, 2026) found: the
/// welcome left on screen over a logout or a recovery offer once it showed a failed start, recovery
/// counted as read when reading it had failed, a live view height that threw in a short window, and
/// the guided header without the crash recovery mark, its tooltip, warning and Retry now.
/// </summary>
public class GuidedReviewFixesTests
{
    // ---------------------------------------------------------------- 1. the welcome after a failed start

    [Theory]
    [InlineData(false, true, false, false, false, (int)WelcomeOnScreen.StepsAside)] // a failed start's error, then a logout or a recovery offer
    [InlineData(false, true, true, true, false, (int)WelcomeOnScreen.StepsAside)] // the same, the guided Plate made but not opened
    [InlineData(true, false, false, false, false, (int)WelcomeOnScreen.StepsAside)] // its start still under way
    [InlineData(false, false, false, false, false, (int)WelcomeOnScreen.StepsAside)]
    [InlineData(false, false, true, false, false, (int)WelcomeOnScreen.StepsAside)]
    [InlineData(false, true, false, false, true, (int)WelcomeOnScreen.Stays)] // says why nothing was made; Create My First Plate tries again
    [InlineData(false, true, true, true, true, (int)WelcomeOnScreen.Stays)] // says why the guided Plate didn't open; trying again opens it
    [InlineData(false, true, true, false, true, (int)WelcomeOnScreen.Closes)] // an old error, and a Plate made another way since
    [InlineData(false, false, true, false, true, (int)WelcomeOnScreen.Closes)] // a Plate made another way
    [InlineData(true, false, true, true, true, (int)WelcomeOnScreen.Stays)] // its own Plate made, opening: the start's outcome decides
    [InlineData(false, false, false, false, true, (int)WelcomeOnScreen.Stays)]
    public void TheWelcome_StepsAsideOnLogoutOrRecovery_WhateverErrorItShows(bool starting, bool showsStartError, bool hasPlate, bool guidedPlateExists, bool mayShow, int expected)
    {
        Assert.Equal((WelcomeOnScreen)expected, GuidedCreation.WelcomeOnScreenNow(starting, showsStartError, hasPlate, guidedPlateExists, mayShow));
    }

    [Fact]
    public void TheWelcomeWindow_AsksTheRuleEveryFrame_WithNoErrorGateAroundIt()
    {
        var welcome = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Windows", "Tutorial", "WelcomeWindow.cs"));
        var check = welcome[welcome.IndexOf("public override void PreOpenCheck()", StringComparison.Ordinal)..welcome.IndexOf("public override void PreDraw()", StringComparison.Ordinal)];

        Assert.Contains("GuidedCreation.WelcomeOnScreenNow(creating, startError is not null, guided.HasPlate, guided.CanContinue && !guided.StepsOnScreen, mayShow())", check, StringComparison.Ordinal);
        var refresh = check.IndexOf("startError = GuidedCreation.WelcomeErrorNow(startError, guided.StartError);", StringComparison.Ordinal);
        Assert.True(refresh > 0 && refresh < check.IndexOf("GuidedCreation.WelcomeOnScreenNow(", StringComparison.Ordinal));
        Assert.DoesNotContain("startError is null)", check.Replace("startError is null && guided.CanContinue", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("guided.WithdrawWelcome();", check, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartThatFails_ReportsWhy_AndTheRuleStillStepsTheWelcomeAsideFromIt()
    {
        // A start that fails reports why (the guided Plate is gone by the time its open runs), and the
        // welcome's rule for that state: it stays to be read, but steps aside on a logout or a recovery
        // offer. The window itself asks the rule every frame with no error gate (the source check above).
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Switcher.Switcher.Open(harness.Switcher.OriginalId);
        await harness.FramesUntilAsync(() => harness.OpenId == harness.Switcher.OriginalId);
        harness.Guided.Start();
        Assert.True(harness.Guided.IsStarting);
        await harness.Library.DeletePlateAsync(made);
        harness.Frame();
        Assert.False(harness.Guided.IsStarting);
        Assert.NotNull(harness.Guided.StartError);
        await harness.EmptyLibraryAsync();

        // With the error on screen it stays to be read and answered, until a logout or a recovery offer.
        Assert.Equal(WelcomeOnScreen.Stays, GuidedCreation.WelcomeOnScreenNow(false, true, harness.Guided.HasPlate, harness.Guided.CanContinue, mayShow: true));
        Assert.Equal(WelcomeOnScreen.StepsAside, GuidedCreation.WelcomeOnScreenNow(false, true, harness.Guided.HasPlate, harness.Guided.CanContinue, mayShow: false));
    }

    [Fact]
    public async Task AWelcomeWhoseStartFailed_StepsAside_AndComesBackOnce_CountedOnce()
    {
        // The welcome shows (counted), its Create My First Plate fails without making a Plate, and the
        // character logs out with the error on screen.
        var store = new FaultInjectingStore();
        using var harness = await GuidedHarness.CreateAsync(store);
        await harness.EmptyLibraryAsync();
        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);
        Assert.True(harness.Guided.ConsumeWelcome(mayShow: true));
        Assert.Equal(1, harness.Store.Preferences.OfferCount);
        store.FailWrite = _ => true;
        harness.Guided.AnswerWelcome(WelcomeAnswer.Create);
        await harness.FramesUntilAsync(() => !harness.Guided.IsStarting);
        store.FailWrite = null;
        var shown = GuidedCreation.WelcomeErrorNow(harness.Guided.StartError, harness.Guided.StartError);
        Assert.NotNull(shown);
        Assert.False(harness.Guided.HasPlate);
        Assert.False(harness.Guided.WelcomeRequested);
        Assert.Equal(WelcomeOnScreen.Stays, GuidedCreation.WelcomeOnScreenNow(false, shown is not null, harness.Guided.HasPlate, harness.Guided.CanContinue, mayShow: true));
        Assert.Equal(WelcomeOnScreen.StepsAside, GuidedCreation.WelcomeOnScreenNow(false, shown is not null, harness.Guided.HasPlate, harness.Guided.CanContinue, mayShow: false));
        harness.Guided.WithdrawWelcome();

        // It waits while logged out, comes back once on the next login, and that showing isn't counted
        // again; a load that ended before it came back has still counted the first.
        Assert.True(harness.Guided.WelcomeRequested);
        Assert.Equal(1, harness.Store.Preferences.OfferCount);
        Assert.False(harness.Guided.ConsumeWelcome(mayShow: false));
        Assert.True(harness.Guided.ConsumeWelcome(mayShow: true));
        Assert.Equal(1, harness.Store.Preferences.OfferCount);
        Assert.False(harness.Guided.ConsumeWelcome(mayShow: true));
        Assert.Equal(GuidedRunStatus.None, harness.Store.Preferences.Run);

        // A later load counts its own showing.
        harness.Reload();
        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);
        Assert.True(harness.Guided.ConsumeWelcome(mayShow: true));
        Assert.Equal(2, harness.Store.Preferences.OfferCount);
    }

    [Fact]
    public async Task AWelcomeSteppedAside_DoesntComeBack_OnceMyPlatesHoldsAPlate_TheReminderCarriesTheSteps()
    {
        // The welcome stepped aside (a logout) while its guided Plate exists, unopened: with a Plate in
        // My Plates it has nothing to offer, so it doesn't come back; My Plates' reminder (and Help)
        // continue that Plate instead.
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Switcher.Switcher.Open(harness.Switcher.OriginalId);
        await harness.FramesUntilAsync(() => harness.OpenId == harness.Switcher.OriginalId);

        harness.Guided.WithdrawWelcome();
        Assert.False(harness.Guided.ConsumeWelcome(mayShow: true));
        Assert.False(harness.Guided.WelcomeRequested);
        Assert.True(harness.Guided.ShowsResumeReminder);
        Assert.Equal(made, harness.Store.Preferences.PlateId);
    }

    [Fact]
    public async Task AWelcomesOldError_GoesOnceAStartFromElsewhereRuns_SoTheOpenedStepsCloseIt()
    {
        // The welcome's start fails without making a Plate: the welcome keeps its error and stays.
        var store = new FaultInjectingStore();
        using var harness = await GuidedHarness.CreateAsync(store);
        await harness.EmptyLibraryAsync();
        store.FailWrite = _ => true;
        harness.Guided.AnswerWelcome(WelcomeAnswer.Create);
        await harness.FramesUntilAsync(() => !harness.Guided.IsStarting);
        store.FailWrite = null;
        var shown = GuidedCreation.WelcomeErrorNow(harness.Guided.StartError, harness.Guided.StartError);
        Assert.NotNull(shown);
        Assert.Equal(shown, GuidedCreation.WelcomeErrorNow(shown, harness.Guided.StartError));

        // My Plates' Create My First Plate (or Help's Create Step by Step) makes and opens the guided
        // Plate. Kept as it was, the old error would hold the welcome open over the steps with a second
        // primary button; no longer reported, it is dropped, and the welcome closes.
        harness.Guided.Start();
        await harness.FramesUntilAsync(() => !harness.Guided.IsStarting);
        Assert.Null(harness.Guided.StartError);
        Assert.True(harness.Guided.CanContinue);
        Assert.Equal(WelcomeOnScreen.Stays, GuidedCreation.WelcomeOnScreenNow(false, true, harness.Guided.HasPlate, harness.Guided.CanContinue, mayShow: true));
        shown = GuidedCreation.WelcomeErrorNow(shown, harness.Guided.StartError);
        Assert.Null(shown);
        Assert.Equal(WelcomeOnScreen.Closes, GuidedCreation.WelcomeOnScreenNow(false, shown is not null, harness.Guided.HasPlate, harness.Guided.CanContinue, mayShow: true));
    }

    [Fact]
    public async Task AWelcomeShowingWhyItsPlateDidntOpen_Closes_OnceThatPlatesStepsAreOnScreen_HoweverOpened()
    {
        // The welcome's start made the guided Plate but it didn't open: the welcome stays, saying why,
        // since Create My First Plate opens that Plate again.
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Switcher.Switcher.Open(harness.Switcher.OriginalId);
        await harness.FramesUntilAsync(() => harness.OpenId == harness.Switcher.OriginalId);
        Assert.False(harness.Guided.StepsOnScreen);
        Assert.Equal(WelcomeOnScreen.Stays, GuidedCreation.WelcomeOnScreenNow(false, true, harness.Guided.HasPlate, harness.Guided.CanContinue && !harness.Guided.StepsOnScreen, mayShow: true));

        // The player opens that Plate from its card instead (no start, so the reason is still reported):
        // its steps are on screen, so the welcome has nothing left to offer and closes.
        harness.Switcher.Switcher.Open(made);
        await harness.FramesUntilAsync(() => harness.OpenId == made);
        Assert.True(harness.Guided.StepsOnScreen);
        Assert.Equal(WelcomeOnScreen.Closes, GuidedCreation.WelcomeOnScreenNow(false, true, harness.Guided.HasPlate, harness.Guided.CanContinue && !harness.Guided.StepsOnScreen, mayShow: true));
    }

    [Fact]
    public async Task ContinueStepByStep_WaitsWhileARecoveryOfferAwaitsAnAnswer()
    {
        // A guided creation left partway, its Plate not open: My Plates' reminder and Help offer to continue.
        using var harness = await GuidedHarness.CreateAsync();
        await harness.StartAndOpenAsync();
        harness.Switcher.Switcher.Open(harness.Switcher.OriginalId);
        await harness.FramesUntilAsync(() => harness.OpenId == harness.Switcher.OriginalId);
        Assert.True(harness.Guided.ShowsResumeReminder);
        Assert.False(harness.Guided.ContinueWaitsForRecovery);

        // A recovery offer waits (its kept changes may be this Plate's): continuing, which opens the Plate
        // as saved, waits for its answer; the reminder hides and Help's item is greyed out.
        var waits = true;
        harness.Guided.RecoveryWaits = () => waits;
        Assert.False(harness.Guided.ShowsResumeReminder);
        Assert.True(harness.Guided.ContinueWaitsForRecovery);

        waits = false;
        Assert.True(harness.Guided.ShowsResumeReminder);
        Assert.False(harness.Guided.ContinueWaitsForRecovery);
    }

    [Fact]
    public void ThePlugin_TellsGuidedCreationWhenRecoveryWaits_AndHelpGreysContinue()
    {
        var root = RepositoryPaths.Root().FullName;
        var plugin = File.ReadAllText(Path.Combine(root, "AetherFrame", "Plugin.cs"));
        var help = File.ReadAllText(Path.Combine(root, "AetherFrame", "Windows", "Tutorial", "HelpMenu.cs"));

        Assert.Contains("guidedCreation.RecoveryWaits = () => recoveryOffer.AwaitsAnswer;", plugin, StringComparison.Ordinal);
        Assert.Contains("using (ImRaii.Disabled(guided.IsStarting || onSteps || waitsForRecovery))", help, StringComparison.Ordinal);
        Assert.Contains("Answer Unsaved Changes Kept first: it may hold this Plate's changes.", help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "Why", null)] // another route's error is that route's to show
    [InlineData("Why", "Why", "Why")]
    [InlineData("Why", null, null)] // a start since cleared it
    [InlineData("Why", "Another reason", null)]
    public void TheWelcomesError_IsKeptOnlyWhileGuidedCreationStillReportsIt(string? shown, string? current, string? expected)
    {
        Assert.Equal(expected, GuidedCreation.WelcomeErrorNow(shown, current));
    }

    // ---------------------------------------------------------------- 2. recovery read, or unknown

    [Fact]
    public async Task NothingKept_IsAComplete_Read()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);

        var scan = await Review(game);

        Assert.True(scan.Complete);
        Assert.Empty(scan.Offers);
    }

    [Fact]
    public async Task ADraftAndACrashedEditing_ReadIntact_AreAComplete_Read()
    {
        using var fixture = new LibraryFixture();
        await KeepAtUnloadAsync(fixture, "kept");
        await CrashAsync(fixture, "crashed");

        var scan = await Review(await GameSession.StartAsync(fixture));

        Assert.True(scan.Complete);
        Assert.Equal(2, scan.Offers.Count);
    }

    [Fact]
    public async Task ALibraryThatDidntLoad_IsNeverAComplete_Read()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var unloaded = new PlateLibraryService(fixture.Paths, fixture.Store, fixture.Log, () => fixture.Clock.Now);

        var scan = await KeptChangesReview.ReviewAsync(game.Drafts, unloaded, fixture.Log, game.Recovery.Store);

        Assert.False(scan.Complete);
        Assert.Empty(scan.Offers);
    }

    [Fact]
    public async Task ADamagedDraft_LeavesTheRead_Incomplete_AndTheFileAsItIs()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        var plateId = await game.CreatePlateAsync(name: "Damaged");
        KeptFiles.WriteDraftJson(fixture.Paths, plateId, fixture.Clock.Now, Guid.NewGuid(), "{");
        var before = KeptFiles.Snapshot(fixture.Paths.DraftsDirectory);

        var scan = await Review(await GameSession.StartAsync(fixture));

        Assert.False(scan.Complete);
        Assert.Empty(scan.Offers);
        Assert.Equal(before, KeptFiles.Snapshot(fixture.Paths.DraftsDirectory));
    }

    [Fact]
    public async Task ARecoveryFolderThatCantBeListed_LeavesTheRead_Incomplete_AndStillOffersTheDrafts()
    {
        using var fixture = new LibraryFixture();
        await KeepAtUnloadAsync(fixture, "kept");
        await CrashAsync(fixture, "crashed");
        var before = KeptFiles.Snapshot(fixture.Paths.DraftsDirectory);
        var next = await GameSession.StartAsync(fixture);
        next.Recovery.Files.FailListDirectories = true;

        var scan = await Review(next);

        Assert.False(scan.Complete);
        Assert.Single(scan.Offers);
        Assert.Equal(before, KeptFiles.Snapshot(fixture.Paths.DraftsDirectory));
    }

    [Fact]
    public async Task ACheckpointThatCantBeOpened_LeavesTheRead_Incomplete_AndItsEditingUntouched()
    {
        using var fixture = new LibraryFixture();
        await CrashAsync(fixture, "held open");
        var before = KeptFiles.Snapshot(fixture.Paths.DraftsDirectory);
        var next = await GameSession.StartAsync(fixture);
        next.Recovery.Files.FailRead = _ => true;

        var scan = await Review(next);

        Assert.False(scan.Complete);
        Assert.Empty(scan.Offers);
        Assert.Equal(before, KeptFiles.Snapshot(fixture.Paths.DraftsDirectory));
    }

    [Fact]
    public async Task AnEditingWithNoIntactCheckpoint_LeavesTheRead_Incomplete_AndItsFilesAsTheyAre()
    {
        using var fixture = new LibraryFixture();
        foreach (var checkpoint in await CrashAsync(fixture, "one", "two"))
        {
            var bytes = File.ReadAllBytes(checkpoint);
            File.WriteAllBytes(checkpoint, bytes[..(bytes.Length / 2)]);
        }

        var before = KeptFiles.Snapshot(fixture.Paths.DraftsDirectory);
        var scan = await Review(await GameSession.StartAsync(fixture));

        Assert.False(scan.Complete);
        Assert.Empty(scan.Offers);
        Assert.Equal(before, KeptFiles.Snapshot(fixture.Paths.DraftsDirectory));
    }

    [Fact]
    public async Task ANewerVersionsCheckpoint_LeavesTheRead_Incomplete()
    {
        using var fixture = new LibraryFixture();
        var newest = (await CrashAsync(fixture, "newer")).Max(StringComparer.Ordinal)!;
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(newest))!.AsObject();
        envelope["Version"] = 99;
        File.WriteAllText(newest, envelope.ToJsonString());

        var scan = await Review(await GameSession.StartAsync(fixture));

        Assert.False(scan.Complete);
        Assert.Empty(scan.Offers);
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public async Task MoreEditingsThanOneLoadReads_LeaveTheRead_Incomplete()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        for (var i = 0; i <= DraftStore.MaxDraftsRead; i++)
        {
            game.Open(await game.CreatePlateAsync(name: $"Plate {i}"));
            await game.Recovery.FrameAsync();
            game.Edit($"edit {i}");
            fixture.Clock.Tick();
            await game.Recovery.RunAsync(6);
        }

        game.Recovery.Files.Crash();

        var scan = await Review(await GameSession.StartAsync(fixture));

        Assert.False(scan.Complete);
        Assert.Equal(DraftStore.MaxDraftsRead, scan.Offers.Count);
    }

    [Fact]
    public async Task ADraftASaveAlreadyHolds_WaitsWhileRecoveryFoldersCantBeListed_ThenGoesWithItsCheckpoints()
    {
        // An editing checkpointed once ("one"), edited again ("two") and kept at unload; the same
        // changes then saved elsewhere, so the kept draft is the Plate's saved content.
        using var fixture = new LibraryFixture();
        var first = await GameSession.StartAsync(fixture);
        var plateId = await first.CreatePlateAsync();
        first.Open(plateId);
        await first.Recovery.FrameAsync();
        first.Edit("one");
        fixture.Clock.Tick();
        await first.Recovery.RunAsync(6);
        Assert.Single(first.Recovery.OwnCheckpoints());
        first.Edit("two");
        await EndAsync(first, unload: true);
        var draft = Assert.Single(KeptFiles.Drafts(fixture.Paths));
        Assert.Single(KeptFiles.Checkpoints(fixture.Paths));

        var drafted = DraftDocuments.Parse(File.ReadAllText(draft)).Draft!;
        var other = await GameSession.StartAsync(fixture);
        other.Open(plateId);
        Assert.True(other.Session.ApplyRecoveredState(ProfileService.DocumentState.Capture(drafted.Document)));
        Assert.True(await other.Session.SaveProfileAsync());
        await EndAsync(other, unload: false);

        // A load that can't list the recovery folders: the draft isn't retired without its checkpoint
        // (which a later load would otherwise offer alone, behind the save); every file stays.
        var before = KeptFiles.Snapshot(fixture.Paths.DraftsDirectory);
        var next = await GameSession.StartAsync(fixture);
        next.Recovery.Files.FailListDirectories = true;
        var scan = await Review(next);
        Assert.False(scan.Complete);
        Assert.Empty(scan.Offers);
        Assert.Equal(before, KeptFiles.Snapshot(fixture.Paths.DraftsDirectory));
        Assert.Contains(fixture.Log.Messages, m => m.Contains("a save already holds as they are", StringComparison.Ordinal));

        // A load that lists everything retires the editing whole, and offers nothing.
        next.Recovery.Files.FailListDirectories = false;
        scan = await Review(next);
        Assert.True(scan.Complete);
        Assert.Empty(scan.Offers);
        Assert.Empty(KeptFiles.Drafts(fixture.Paths));
        Assert.Empty(KeptFiles.Checkpoints(fixture.Paths));
    }

    [Fact]
    public async Task ARunWhoseLockCantBeProbed_LeavesTheRead_Incomplete_AndItsCheckpointsForALaterLoad()
    {
        using var fixture = new LibraryFixture();
        var crashed = await CrashAsync(fixture, "crashed");
        var before = KeptFiles.Snapshot(fixture.Paths.DraftsDirectory);

        // Neither held nor free as far as this load can tell: unknown, never a running client's. This
        // covers the store's handling of a probe that can't tell; that the real probe reports it, rather
        // than answering "held", is TheLockProbe_NeverTakesCantTellForHeld's.
        var next = await GameSession.StartAsync(fixture);
        next.Recovery.Files.CantTellLock = path => path.EndsWith("session.lock", StringComparison.OrdinalIgnoreCase);
        next.Recovery.Store.Sweep();
        var scan = await Review(next);
        Assert.False(scan.Complete);
        Assert.Empty(scan.Offers);
        Assert.Equal(before, KeptFiles.Snapshot(fixture.Paths.DraftsDirectory));
        Assert.DoesNotContain(fixture.Log.Messages, m => m.Contains("other running game client", StringComparison.Ordinal));

        next.Recovery.Files.CantTellLock = null;
        scan = await Review(next);
        Assert.True(scan.Complete);
        Assert.Equal(crashed[^1], Assert.Single(scan.Offers).Path);
    }

    [Fact]
    public void TheLockProbe_NeverTakesCantTellForHeld()
    {
        // The real probe lets "can't tell" (UnauthorizedAccessException) reach its callers: no catch in
        // it may cover that exception, nor SystemException or Exception, which include it.
        var files = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Services", "Plates", "RecoveryFiles.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = files.IndexOf("public bool IsLockHeld(string lockPath)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var probe = files[start..files.IndexOf("\n    }\n", start, StringComparison.Ordinal)];

        Assert.Contains("catch (IOException)", probe, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"catch\s*\(\s*(System\.)?(UnauthorizedAccessException|SystemException|Exception)\b", probe);
        Assert.DoesNotMatch(@"catch\s*(\{|when)", probe);
        Assert.DoesNotMatch(@"when\s*\(", probe);
    }

    [Fact]
    public void ThePlugin_RecordsRecoveryAsRead_OnlyFromACompleteRead()
    {
        var plugin = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Plugin.cs"));

        Assert.Single(Regex.Matches(plugin, @"keptChangesRead = "));
        Assert.Contains("keptChangesRead = scan.Complete;", plugin, StringComparison.Ordinal);
        Assert.Contains("KeptChangesReview.ReviewAsync(keptChangesFiles, plateLibrary, log, recoveryCheckpoints)", plugin, StringComparison.Ordinal);

        // Set after the offer has what was found, and never by a failure's handler.
        var load = plugin[plugin.IndexOf("private async Task LoadKeptChangesAsync(", StringComparison.Ordinal)..];
        var present = load.IndexOf("keptChanges.Present(scan.Offers", StringComparison.Ordinal);
        Assert.True(present >= 0 && present < load.IndexOf("keptChangesRead = scan.Complete;", StringComparison.Ordinal));
        Assert.DoesNotContain("finally", plugin[plugin.IndexOf("await LoadKeptChangesAsync(cancellationToken)", StringComparison.Ordinal)..plugin.IndexOf("private async Task LoadKeptChangesAsync(", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 3. the live view's height in a short window

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void TheGuidedSteps_AtTheMinimumWindow_WithAnErrorAndARecoveryWarning_FitWithoutAScrollbar(float scale)
    {
        // The Basic editor at its minimum size, measured as AetherFrame's style draws it (AetherMetrics,
        // Dalamud's 16 px interface font, the 14 pt heading face at 18.7 px), all times the scale:
        // narrow, so the live view sits above the step, with the header's buttons on a row of their
        // own, a recovery warning and an error line each wrapped onto two lines, then the footer.
        var m = Metrics.At(scale);
        var width = (BasicEditorView.MinimumWindowSize.X * scale) - (m.WindowPadding * 2f);
        Assert.True(width < 700f * scale); // narrow: the steps' side-by-side layout needs 700

        var header = (m.HeadingFrame + m.Spacing) + (m.Frame + m.Spacing);
        var footer = m.Frame + (m.Spacing * 3f) + (AetherMetrics.SpaceXs * scale) + 1f;
        var body = m.Content - header - m.WrappedLine - m.WrappedLine - m.Separator - footer;

        // Before: Math.Clamp's minimum (120) was above its maximum (42% of this height), which throws.
        Assert.True(120f * scale > body * BasicEditorView.GuidedPreviewShare);

        var preview = BasicEditorView.StackedPreviewHeight(width, 1200f, 675f, m.Frame + m.Spacing, body, BasicEditorView.GuidedPreviewShare, 120f * scale, (100f * scale) + m.Spacing);
        Assert.InRange(preview, 0f, body * BasicEditorView.GuidedPreviewShare);
        Assert.True(preview > m.Frame + m.Spacing, "the live view keeps room below its toolbar");

        // The window's body floor (160) and the step panel's (100) are never reached: nothing scrolls the window.
        Assert.True(body >= 160f * scale);
        Assert.True(body - preview - m.Spacing >= 100f * scale);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void TheStackedBasicEditor_AtTheMinimumWindow_WithAnErrorAndARecoveryWarning_FitsWithoutAScrollbar(float scale)
    {
        // The ordinary Basic editor at its minimum size: the action bar on two rows, the recovery
        // warning on two, the error line on two (a save the system refused says why at length), the
        // separator, then the categories, counted as two rows (they fit one at 496 px; the second
        // stands for the strip's spacing and keeps the model conservative), above the live view.
        // The live view's toolbar is one row here; while artwork downloads it can take two, which
        // leaves the canvas smaller but changes none of the heights below.
        var m = Metrics.At(scale);
        var width = (BasicEditorView.MinimumWindowSize.X * scale) - (m.WindowPadding * 2f);
        Assert.Equal(BasicEditorLayoutMode.Stacked, BasicEditorView.ChooseLayout(width, scale));

        var remaining = m.Content - ((m.Frame + m.Spacing) * 2f) - m.WrappedLine - ((m.Font * 2f) + m.Spacing) - m.Separator - ((m.Frame + m.Spacing) * 2f);

        // Before: Math.Clamp's minimum (140) was above its maximum (45% of this height), which throws.
        Assert.True(140f * scale > remaining * BasicEditorView.StackedPreviewShare);

        // 45% of this height would leave the inspector short of its 120: the live view gives way.
        Assert.True(remaining - (remaining * BasicEditorView.StackedPreviewShare) - m.Spacing < 120f * scale);
        var preview = BasicEditorView.StackedPreviewHeight(width, 1200f, 675f, m.Frame + m.Spacing, remaining, BasicEditorView.StackedPreviewShare, 140f * scale, (120f * scale) + m.Spacing);
        Assert.InRange(preview, 0f, remaining * BasicEditorView.StackedPreviewShare);
        Assert.True(preview > m.Frame + m.Spacing, "the live view keeps room below its toolbar");
        Assert.True(remaining - preview - m.Spacing >= (120f * scale) - 0.01f, "the inspector keeps its minimum");
    }

    [Theory]
    [InlineData(400f, 300f, 0.42f, 120f, 0f, 126f)] // the share caps the canvas' shape
    [InlineData(400f, 1000f, 0.42f, 120f, 106f, 255f)] // the canvas' shape (400 x 675/1200 = 225) plus its toolbar
    [InlineData(400f, 200f, 0.42f, 120f, 0f, 84f)] // a short window: the share, below the usual minimum
    [InlineData(400f, 225f, 0.45f, 140f, 126f, 99f)] // the controls below keep their minimum: the live view gives way
    [InlineData(400f, 100f, 0.45f, 140f, 126f, 0f)] // not even that minimum fits: no live view (0, not drawn) rather than a negative one
    [InlineData(400f, 150f, 0.45f, 140f, 126f, 0f)] // 24 px left, less than the 30 px toolbar: nothing of the Plate would show, so none
    [InlineData(400f, 0f, 0.42f, 120f, 0f, 0f)]
    [InlineData(400f, -50f, 0.45f, 140f, 126f, 0f)] // content already past the window's edge
    [InlineData(0f, 500f, 0.45f, 140f, 126f, 140f)]
    public void TheStackedLiveView_NeverThrows_AndKeepsWithinItsShare(float width, float available, float share, float minimum, float keepBelow, float expected)
    {
        Assert.Equal(expected, BasicEditorView.StackedPreviewHeight(width, 1200f, 675f, 30f, available, share, minimum, keepBelow), 3);
    }

    [Fact]
    public void TheStackedLiveView_SurvivesNonsense()
    {
        Assert.Equal(0f, BasicEditorView.StackedPreviewHeight(400f, 1200f, 675f, 30f, float.NaN, 0.42f, 120f, 106f));
        Assert.Equal(120f, BasicEditorView.StackedPreviewHeight(400f, 0f, float.NaN, 30f, 1000f, 0.42f, 120f, 106f));
        Assert.Equal(0f, BasicEditorView.StackedPreviewHeight(400f, 1200f, 675f, 30f, float.PositiveInfinity, 0.42f, 120f, 106f));
        Assert.Equal(0f, BasicEditorView.StackedPreviewHeight(400f, 1200f, 675f, 30f, 1000f, 0.42f, 120f, float.NaN));
        Assert.Equal(126f, BasicEditorView.StackedPreviewHeight(400f, 1200f, 675f, 30f, 300f, 0.42f, 120f, -50f), 3);
    }

    [Fact]
    public void BothNarrowLayouts_UseTheBoundedHeight_AndNoUnguardedClamp()
    {
        var root = RepositoryPaths.Root().FullName;
        var basic = File.ReadAllText(Path.Combine(root, "AetherFrame", "Windows", "BasicProfileEditorWindow.cs"));
        var steps = File.ReadAllText(Path.Combine(root, "AetherFrame", "Windows", "BasicProfileEditorWindow.Guided.cs"));

        foreach (var code in new[] { basic, steps })
        {
            Assert.Contains("BasicEditorView.StackedPreviewHeight(", code, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"Math\.Clamp\(\s*\(body\.X \* profile\.CanvasHeight", code);
        }

        // Each keeps the room its controls below need: the minimum it then gives them, plus the spacing
        // before them. A live view of 0 isn't drawn (a child 0 tall takes all the height left), and the
        // controls then take that height.
        Assert.Contains("140f * scale, inspectorMinimum + style.ItemSpacing.Y);", basic, StringComparison.Ordinal);
        Assert.Contains("120f * scale, panelMinimum + style.ItemSpacing.Y);", steps, StringComparison.Ordinal);
        foreach (var code in new[] { basic, steps })
        {
            Assert.Matches(@"if \(previewHeight > 0f\)\s*\{\s*DrawPreview\(profile, new Vector2\(-1f, previewHeight\)\);", code);
            Assert.Single(Regex.Matches(code, @"DrawPreview\(profile, new Vector2\(-1f, previewHeight\)\)"));
        }

        Assert.Contains("DrawInspector(profile, new Vector2(-1f, Math.Max(inspectorMinimum, remaining)), withCategoryStrip: false);", basic, StringComparison.Ordinal);
        Assert.Contains("DrawGuidedPanel(profile, guided, success, new Vector2(-1f, Math.Max(panelMinimum, panelHeight)));", steps, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEditorBarsError_WrapsAtTheWindowsEdge()
    {
        var bar = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Windows", "EditorActionBar.cs"));
        var error = bar.IndexOf("ImGui.TextColored(EditorWidgets.ErrorColor, error);", StringComparison.Ordinal);

        Assert.True(error > 0);
        var wrap = bar.LastIndexOf("using (ImRaii.TextWrapPos(0f))", error, StringComparison.Ordinal);
        var guard = bar.IndexOf("if (errorMessage is { } error)", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < wrap);

        // The Plate menu's result line above it: right-aligned when it fits, otherwise from the left and wrapped.
        var menu = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Windows", "EditorPlateMenu.cs"));
        Assert.Contains("var fits = rowEnd - ImGui.GetCursorPosX() >= width;", menu, StringComparison.Ordinal);
        Assert.Contains("AetherControls.StatusLine(error is null ? AetherTone.Success : AetherTone.Danger, text, wrap: !fits);", menu, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 4. crash recovery in the guided header

    [Fact]
    public void TheGuidedHeader_ShowsTheRecoveryMarkAndWarning_InRoomItReserves()
    {
        var root = RepositoryPaths.Root().FullName;
        var steps = File.ReadAllText(Path.Combine(root, "AetherFrame", "Windows", "BasicProfileEditorWindow.Guided.cs"));
        var bar = File.ReadAllText(Path.Combine(root, "AetherFrame", "Windows", "EditorActionBar.cs"));
        var header = steps[steps.IndexOf("private void DrawGuidedHeader(", StringComparison.Ordinal)..steps.IndexOf("private void DrawGuidedPanel(", StringComparison.Ordinal)];

        // The header's right group reserves the mark's room (the bar's own measure) beside the save
        // state's widest wording, and draws the mark (with its checkpoint tooltip) after it, before Undo.
        Assert.Contains("var markWidth = actionBar.RecoveryMarkWidth;", header, StringComparison.Ordinal);
        Assert.Contains("stateWidth + markWidth + Width(\"Undo\")", header, StringComparison.Ordinal);
        var mark = header.IndexOf("actionBar.DrawRecoveryMarkHere(commands.RecoveryIndicator);", StringComparison.Ordinal);
        var stateRoom = header.IndexOf("ImGui.SameLine(stateStart + stateWidth + style.ItemSpacing.X);", StringComparison.Ordinal);
        Assert.True(stateRoom >= 0 && stateRoom < mark);
        Assert.True(mark < header.IndexOf("ImGui.Button(\"Undo##GuidedUndo\")", StringComparison.Ordinal));
        Assert.Contains("EditorWidgets.Tooltip(EditorDocumentCommands.RecoveryText(recovery));", bar, StringComparison.Ordinal);

        // The warning with Retry now on its own row under the header, before the error line and the
        // separator, so the steps' height is measured after it; it wraps beside Retry now in a narrow window.
        var body = steps[steps.IndexOf("private void DrawGuidedBody(", StringComparison.Ordinal)..steps.IndexOf("private void DrawGuidedHeader(", StringComparison.Ordinal)];
        var warning = body.IndexOf("actionBar.DrawRecoveryWarning(actionBar.Commands.RecoveryIndicator);", StringComparison.Ordinal);
        var headerCall = body.IndexOf("DrawGuidedHeader(guided, success);", StringComparison.Ordinal);
        Assert.True(headerCall >= 0 && headerCall < warning);
        Assert.True(warning < body.IndexOf("AetherControls.StatusLine(AetherTone.Danger, error, wrap: true);", StringComparison.Ordinal));
        Assert.True(warning < body.IndexOf("ImGui.Separator();", StringComparison.Ordinal));
        Assert.True(warning < body.IndexOf("var body = ImGui.GetContentRegionAvail();", StringComparison.Ordinal));
        Assert.Contains("using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + textRoom))", bar, StringComparison.Ordinal);
        Assert.Contains("ImGui.SmallButton(RetryLabel + \"##RecoveryRetry\")", bar, StringComparison.Ordinal);
        Assert.Contains("commands.Recovery?.RetryNow();", bar, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWelcome_SaysWhatAPlateIs_InItsOneLine()
    {
        var welcome = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Windows", "Tutorial", "WelcomeWindow.cs"));

        Assert.Contains("ImGui.TextWrapped(\"Create a character card in three short steps.\");", welcome, StringComparison.Ordinal);
        Assert.DoesNotContain("Make your first Plate in three short steps.", welcome, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<KeptChangesScan> Review(GameSession game) =>
        KeptChangesReview.ReviewAsync(game.Drafts, game.Library, game.Fixture.Log, game.Recovery.Store);

    /// <summary>A Plate edited and left unsaved as AetherFrame unloads: a draft kept at unload.</summary>
    private static async Task KeepAtUnloadAsync(LibraryFixture fixture, string text)
    {
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: text));
        game.Edit(text);
        await game.UnloadAsync();
        game.Recovery.Recovery.Stop();
        Assert.True(await game.Recovery.Recovery.Writer.WaitIdleAsync(TimeSpan.FromSeconds(10)));
        game.Recovery.Store.CloseSession();
        fixture.Clock.Tick(60);
    }

    /// <summary>A game ends: as it unloads (keeping a draft of unsaved changes) or just its recovery settled, its folder closed.</summary>
    private static async Task EndAsync(GameSession game, bool unload)
    {
        if (unload)
        {
            await game.UnloadAsync();
        }

        game.Recovery.Recovery.Stop();
        Assert.True(await game.Recovery.Recovery.Writer.WaitIdleAsync(TimeSpan.FromSeconds(10)));
        game.Recovery.Store.CloseSession();
        game.Fixture.Clock.Tick(60);
    }

    /// <summary>A Plate edited with one checkpoint per edit, then the game crashes. Returns the checkpoints.</summary>
    private static async Task<string[]> CrashAsync(LibraryFixture fixture, params string[] edits)
    {
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: edits[0]));
        await game.Recovery.FrameAsync();
        foreach (var text in edits)
        {
            game.Edit(text);
            fixture.Clock.Tick();
            await game.Recovery.RunAsync(6);
        }

        var checkpoints = game.Recovery.OwnCheckpoints();
        Assert.Equal(edits.Length, checkpoints.Length);
        game.Recovery.Files.Crash();
        fixture.Clock.Tick(60);
        return checkpoints;
    }

    /// <summary>AetherFrame's style at a scale: what the Basic editor's rows measure.</summary>
    private sealed record Metrics(float Scale)
    {
        internal float Font => 16f * Scale;

        internal float Frame => Font + (AetherMetrics.FramePaddingY * 2f * Scale);

        internal float HeadingFrame => (18.7f * Scale) + (AetherMetrics.FramePaddingY * 2f * Scale);

        internal float Spacing => AetherMetrics.ItemSpacingY * Scale;

        internal float WindowPadding => AetherMetrics.WindowPadding * Scale;

        // A line drawn after AlignTextToFramePadding that wraps onto two.
        internal float WrappedLine => (AetherMetrics.FramePaddingY * Scale) + (Font * 2f) + Spacing;

        internal float Separator => 1f + Spacing;

        // The window's height inside its title bar (one frame tall) and padding.
        internal float Content => (BasicEditorView.MinimumWindowSize.Y * Scale) - Frame - (WindowPadding * 2f);

        internal static Metrics At(float scale) => new(scale);
    }
}
