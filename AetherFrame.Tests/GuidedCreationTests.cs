using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using AetherFrame.UI.Tutorial;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Guided creation's persisted state, in memory, counting saves.</summary>
internal sealed class MemoryGuidedStore : IGuidedCreationStore
{
    public GuidedCreationPreferences Preferences { get; set; } = new();

    public BasicWorkspaceMode Workspace { get; set; }

    public int Saves { get; private set; }

    public void Save() => Saves++;
}

/// <summary>
/// Guided creation over a real Plate Library with one Plate open in the editors (Alice's first),
/// the editors' New Plate and Open another Plate, and the editors' Save, in the plugin's frame
/// order: the runner's results, the switcher, the editor, then guided creation.
/// </summary>
internal sealed class GuidedHarness : IDisposable
{
    private GuidedHarness(SwitcherHarness switcher, MemoryGuidedStore store)
    {
        Switcher = switcher;
        Store = store;
        Commands = new EditorDocumentCommands(switcher.Plates.Editor.Profiles, switcher.Plates.Editor.Session);
        Guided = Create(store);
    }

    internal SwitcherHarness Switcher { get; }

    internal MemoryGuidedStore Store { get; }

    internal EditorDocumentCommands Commands { get; }

    internal GuidedCreation Guided { get; private set; }

    /// <summary>The editor guided creation asked to show (when its Plate was already open).</summary>
    internal List<EditorSurfaceKind> Shown { get; } = new();

    /// <summary>Whether the Basic editor's window is open (closing it leaves its Plate open).</summary>
    internal bool BasicEditorOpen { get; set; } = true;

    internal BasicHarness Editor => Switcher.Plates.Editor;

    internal PlateLibraryService Library => Switcher.Library;

    internal Guid? OpenId => Switcher.OpenId;

    internal static async Task<GuidedHarness> CreateAsync(IPlateFileStore? store = null, MemoryGuidedStore? preferences = null) =>
        new(await SwitcherHarness.CreateAsync(store), preferences ?? new MemoryGuidedStore());

    /// <summary>The plugin loaded again over the same configuration and Library (in-memory state is lost).</summary>
    internal void Reload() => Guided = Create(Store);

    internal void Frame()
    {
        Switcher.Frame();
        Guided.Advance();
    }

    internal async Task FramesUntilAsync(Func<bool> done)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            await Switcher.Plates.Runner.Finished;
            Frame();
            if (done())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("It never got there.");
    }

    /// <summary>Starts guided creation and waits until its Plate is open in the editors.</summary>
    internal async Task<Guid> StartAndOpenAsync()
    {
        Guided.Start();
        await FramesUntilAsync(() => !Guided.IsStarting && Guided.Preferences.PlateId is { } id && OpenId == id);
        return Guided.Preferences.PlateId!.Value;
    }

    /// <summary>The Save step's Save, waited for.</summary>
    internal async Task SaveAsync()
    {
        Guided.Save();
        await FramesUntilAsync(() => !Guided.IsSaving && !Commands.IsSaving);
    }

    internal int PlateCount => Library.GetOrderedPlates().Count;

    /// <summary>My Plates emptied, as a new player's is (the editor keeps its open document).</summary>
    internal async Task EmptyLibraryAsync()
    {
        foreach (var plate in Library.GetOrderedPlates().ToList())
        {
            await Library.DeletePlateAsync(plate.PlateId);
        }

        Assert.Equal(0, PlateCount);
    }

    private GuidedCreation Create(MemoryGuidedStore store) => new(
        store,
        Switcher.Library,
        Switcher.Plates.Editor.Profiles,
        Commands,
        Switcher.Plates.Editor.Session,
        Switcher.Switcher,
        Shown.Add,
        () => BasicEditorOpen);

    public void Dispose() => Switcher.Dispose();
}

/// <summary>
/// Guided creation (Choose a Look, Make It Yours, Save): who is welcomed and when (a recovery offer
/// always first; an install that couldn't be judged never), the welcome's answers and My Plates'
/// reminder to continue, one Plate per guided creation however often it is started, closed or
/// reloaded, Back and Next keeping everything entered, completion only after a successful save of
/// exactly what is shown, the saved state ending with the next edit, and the Basic editor's Simple
/// view for new players with no Plate only.
/// </summary>
public class GuidedCreationTests
{
    // ---------------------------------------------------------------- recovery first

    [Theory]
    [InlineData(true, false, false, false, false)] // kept changes not read yet: wait
    [InlineData(true, true, true, false, false)] // a recovery offer waits for an answer (before or after login)
    [InlineData(true, true, false, true, false)] // the recovery window is on screen
    [InlineData(true, true, true, true, false)]
    [InlineData(false, true, false, false, false)] // no character logged in yet: wait
    [InlineData(true, true, false, false, true)] // logged in, nothing about recovery stands in front
    public void TheWelcome_WaitsForALogin_AndForEveryRecoveryOffer(bool loggedIn, bool read, bool awaitsAnswer, bool onScreen, bool expected)
    {
        Assert.Equal(expected, GuidedCreation.WelcomeMayShow(loggedIn, read, awaitsAnswer, onScreen));
    }

    [Fact]
    public async Task AWelcomeThatMayNotShowYet_IsKept_AndShownOnceRecoveryIsAnswered()
    {
        using var harness = await GuidedHarness.CreateAsync();
        await harness.EmptyLibraryAsync();
        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);

        Assert.False(harness.Guided.ConsumeWelcome(mayShow: false));
        Assert.False(harness.Guided.ConsumeWelcome(mayShow: false));
        Assert.True(harness.Guided.WelcomeRequested);
        Assert.Equal(0, harness.Store.Preferences.OfferCount);

        Assert.True(harness.Guided.ConsumeWelcome(mayShow: true));
        Assert.False(harness.Guided.ConsumeWelcome(mayShow: true));
        Assert.Equal(1, harness.Store.Preferences.OfferCount);
    }

    [Fact]
    public async Task AWelcomeStillWaiting_WhenAPlateIsMade_IsDropped()
    {
        // The welcome waited (for a login, or a recovery answer) while the player made a Plate with Create Plate.
        using var harness = await GuidedHarness.CreateAsync();
        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);
        Assert.True(harness.PlateCount > 0);

        Assert.False(harness.Guided.ConsumeWelcome(mayShow: true));
        Assert.False(harness.Guided.WelcomeRequested);
        Assert.Equal(0, harness.Store.Preferences.OfferCount);
    }

    // ---------------------------------------------------------------- who is welcomed

    [Fact]
    public async Task ANewPlayerWithNoPlate_IsWelcomed_AndGetsTheSimpleView()
    {
        using var harness = await GuidedHarness.CreateAsync();

        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);

        Assert.True(harness.Guided.WelcomeRequested);
        Assert.Equal(BasicWorkspaceMode.Simple, harness.Store.Workspace);
        Assert.True(harness.Guided.SimpleWorkspace);
    }

    [Theory]
    [InlineData(false, 0)] // an existing install, an undetermined one, or one whose configuration couldn't be read
    [InlineData(false, 4)]
    public async Task AnyoneNotNew_IsNeverWelcomed_AndKeepsTheirView(bool newPlayer, int plates)
    {
        using var harness = await GuidedHarness.CreateAsync();

        harness.Guided.ResolveWelcome(newPlayer, librariesLoaded: true, plates);

        Assert.False(harness.Guided.WelcomeRequested);
        Assert.False(harness.Guided.ConsumeWelcome(mayShow: true));
        Assert.Equal(BasicWorkspaceMode.Unset, harness.Store.Workspace);
        Assert.False(harness.Guided.SimpleWorkspace);
        Assert.Equal(0, harness.Store.Saves);
    }

    [Fact]
    public async Task UnreadableConfigurationOrLibrary_IsNeverTreatedAsAFreshInstall()
    {
        // The detector's verdicts feed guided creation; neither unreadable case is a new player.
        foreach (var (configurationUnreadable, libraryLoaded) in new[] { (true, true), (true, false), (false, false) })
        {
            var tutorial = new TutorialPreferences();
            var decision = FirstRunDetector.Decide(tutorial, configurationFound: false, configurationUnreadable, libraryLoaded, 0, 0);
            Assert.NotEqual(FirstRunDecision.OfferTutorial, decision);

            using var harness = await GuidedHarness.CreateAsync();
            harness.Guided.ResolveWelcome(newPlayer: decision == FirstRunDecision.OfferTutorial, libraryLoaded, plateCount: 0);
            Assert.False(harness.Guided.WelcomeRequested);
            Assert.Equal(BasicWorkspaceMode.Unset, harness.Store.Workspace);
        }
    }

    [Fact]
    public async Task ALibraryThatDidntLoad_IsNeverWelcomed_WhateverTheStoredVerdictSays()
    {
        // A player who began on this build stays a new install in the tutorial's record: on a launch
        // whose Library fails to load, its count of Plates (0) means nothing.
        var tutorial = new TutorialPreferences { Install = TutorialInstallKind.NewInstall };
        Assert.Equal(FirstRunDecision.OfferTutorial, FirstRunDetector.Decide(tutorial, configurationFound: true, configurationUnreadable: false, libraryLoaded: false, 0, 0));

        using var harness = await GuidedHarness.CreateAsync();
        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: false, plateCount: 0);

        Assert.False(harness.Guided.WelcomeRequested);
        Assert.Equal(BasicWorkspaceMode.Unset, harness.Store.Workspace);
        Assert.Equal(0, harness.Store.Saves);
    }

    [Fact]
    public void ThePlugin_WelcomesOnlyOnceTheLibrariesLoaded_AndReadsKeptChangesFirst()
    {
        var plugin = System.IO.File.ReadAllText(System.IO.Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Plugin.cs"));

        Assert.Contains("guidedCreation.ResolveWelcome(onboarding.IsNewPlayer, libraryLoaded, plateCount);", plugin, StringComparison.Ordinal);

        // With the Plate Library not loaded, kept changes aren't read, so nothing may follow them.
        Assert.Contains("keptChangesRead = plateLibrary.IsLoaded;", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("keptChangesRead = true;", plugin, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)] // a new player who made a Plate before answering the welcome
    [InlineData(6)] // an earlier version's install whose tutorial offer was closed unanswered still counts as new
    public async Task APlayerWithPlates_IsNotWelcomed_AndKeepsTheirView(int plates)
    {
        using var harness = await GuidedHarness.CreateAsync();

        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: plates);

        Assert.False(harness.Guided.WelcomeRequested);
        Assert.Equal(BasicWorkspaceMode.Unset, harness.Store.Workspace);
        Assert.False(harness.Guided.SimpleWorkspace);
        Assert.Equal(0, harness.Store.Saves);
    }

    [Fact]
    public async Task AViewTheNewPlayerChose_IsKept()
    {
        using var harness = await GuidedHarness.CreateAsync();
        harness.Store.Workspace = BasicWorkspaceMode.Detailed;

        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);

        Assert.Equal(BasicWorkspaceMode.Detailed, harness.Store.Workspace);
        Assert.False(harness.Guided.SimpleWorkspace);
    }

    [Fact]
    public async Task ChoosingAView_IsRemembered_AndOnlySavedWhenItChanges()
    {
        using var harness = await GuidedHarness.CreateAsync();

        harness.Guided.SetSimpleWorkspace(true);
        Assert.Equal(BasicWorkspaceMode.Simple, harness.Store.Workspace);
        Assert.Equal(1, harness.Store.Saves);

        harness.Guided.SetSimpleWorkspace(true);
        Assert.Equal(1, harness.Store.Saves);

        harness.Guided.SetSimpleWorkspace(false);
        Assert.Equal(BasicWorkspaceMode.Detailed, harness.Store.Workspace);
        Assert.Equal(2, harness.Store.Saves);

        // An existing player's later launch never switches it back.
        harness.Guided.ResolveWelcome(newPlayer: false, librariesLoaded: true, plateCount: 3);
        Assert.Equal(BasicWorkspaceMode.Detailed, harness.Store.Workspace);
    }

    // ---------------------------------------------------------------- the welcome's answers

    [Fact]
    public async Task ClosingTheWelcome_AsksAgainNextTime_ButOnlyAFewTimes()
    {
        using var harness = await GuidedHarness.CreateAsync();
        await harness.EmptyLibraryAsync();
        for (var launch = 1; launch <= GuidedCreation.MaxOffers + 2; launch++)
        {
            harness.Reload();
            harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);
            if (launch <= GuidedCreation.MaxOffers)
            {
                Assert.True(harness.Guided.ConsumeWelcome(mayShow: true), $"launch {launch}");
            }
            else
            {
                Assert.False(harness.Guided.WelcomeRequested, $"launch {launch}");
            }
        }

        Assert.Equal(GuidedCreation.MaxOffers, harness.Store.Preferences.OfferCount);
        Assert.Equal(GuidedOfferAnswer.Undecided, harness.Store.Preferences.Offer);
        Assert.False(harness.Guided.ShowsResumeReminder); // the empty My Plates offers the steps itself
    }

    [Fact]
    public async Task NotNow_AsksAgainOnALaterLoad_WithinTheSameFewShowings_AndMakesNothing()
    {
        using var harness = await GuidedHarness.CreateAsync();
        await harness.EmptyLibraryAsync();
        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);
        Assert.True(harness.Guided.ConsumeWelcome(mayShow: true));

        harness.Guided.AnswerWelcome(WelcomeAnswer.NotNow);

        Assert.Equal(GuidedOfferAnswer.Deferred, harness.Store.Preferences.Offer);
        Assert.False(harness.Guided.WelcomeRequested); // not again this load
        Assert.False(harness.Guided.ShowsResumeReminder);
        Assert.Equal(0, harness.PlateCount);

        for (var launch = 2; launch <= GuidedCreation.MaxOffers + 1; launch++)
        {
            harness.Reload();
            harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);
            Assert.Equal(launch <= GuidedCreation.MaxOffers, harness.Guided.ConsumeWelcome(mayShow: true));
            harness.Guided.AnswerWelcome(WelcomeAnswer.NotNow);
        }

        Assert.Equal(GuidedCreation.MaxOffers, harness.Store.Preferences.OfferCount);
        Assert.Equal(0, harness.PlateCount);
    }

    [Fact]
    public async Task DontShowAgain_LeavesNoWelcomeAndNoReminder_ButStartStillWorks()
    {
        using var harness = await GuidedHarness.CreateAsync();
        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);
        harness.Guided.ConsumeWelcome(mayShow: true);
        var plates = harness.PlateCount;

        harness.Guided.AnswerWelcome(WelcomeAnswer.Never);

        Assert.Equal(GuidedOfferAnswer.Declined, harness.Store.Preferences.Offer);
        Assert.False(harness.Guided.ShowsResumeReminder);
        Assert.Equal(plates, harness.PlateCount);
        harness.Reload();
        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);
        Assert.False(harness.Guided.WelcomeRequested);

        // Help's Create Step by Step.
        var made = await harness.StartAndOpenAsync();
        Assert.Equal(plates + 1, harness.PlateCount);
        Assert.True(harness.Guided.IsGuiding(made));
    }

    // ---------------------------------------------------------------- starting: one Plate, however often

    [Fact]
    public async Task Create_MakesOneAdventurePlate_RecordsIt_AndOpensItOnTheFirstStep()
    {
        using var harness = await GuidedHarness.CreateAsync();
        harness.Guided.ResolveWelcome(newPlayer: true, librariesLoaded: true, plateCount: 0);
        harness.Guided.ConsumeWelcome(mayShow: true);
        var plates = harness.PlateCount;

        harness.Guided.AnswerWelcome(WelcomeAnswer.Create);
        Assert.True(harness.Guided.IsStarting);
        await harness.FramesUntilAsync(() => !harness.Guided.IsStarting && harness.OpenId == harness.Store.Preferences.PlateId);

        var made = harness.Store.Preferences.PlateId!.Value;
        Assert.Equal(plates + 1, harness.PlateCount);
        Assert.Equal("Adventure Plate Classic", harness.Library.FindPlate(made)!.DisplayName);
        Assert.Equal(GuidedOfferAnswer.Accepted, harness.Store.Preferences.Offer);
        Assert.Equal(GuidedRunStatus.InProgress, harness.Store.Preferences.Run);
        Assert.Equal(GuidedStage.ChooseLook, harness.Guided.Stage);
        Assert.Equal(1, harness.Guided.StepNumber);
        Assert.True(harness.Guided.IsGuiding(harness.OpenId));
        Assert.Equal([EditorSurfaceKind.Basic], harness.Switcher.Shown);
        Assert.Null(harness.Guided.StartError);
        Assert.False(harness.Commands.IsDirty);
        Assert.False(harness.Guided.ShowsResumeReminder); // its steps are on screen
    }

    [Fact]
    public async Task StartingAgain_ClosingAndReloading_ContinueTheSamePlate_OnItsStep_NeverASecond()
    {
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Guided.Next();
        var plates = harness.PlateCount;

        // Started again while it's open: it is only shown.
        harness.Guided.Start();
        harness.Frame();
        Assert.Equal([EditorSurfaceKind.Basic], harness.Shown);
        Assert.Equal(plates, harness.PlateCount);

        // Another Plate opened (the guided one closed), then the plugin reloaded.
        harness.Switcher.Switcher.Open(harness.Switcher.OriginalId);
        await harness.FramesUntilAsync(() => harness.OpenId == harness.Switcher.OriginalId);
        harness.Reload();
        Assert.True(harness.Guided.ShowsResumeReminder);
        Assert.True(harness.Guided.CanContinue);

        await harness.StartAndOpenAsync();

        Assert.Equal(made, harness.OpenId);
        Assert.Equal(plates, harness.PlateCount);
        Assert.Equal(GuidedStage.MakeItYours, harness.Guided.Stage);
        Assert.True(harness.Guided.IsGuiding(made));
    }

    [Fact]
    public async Task TheResumeReminder_CanBeHiddenUntilTheNextLoad()
    {
        using var harness = await GuidedHarness.CreateAsync();
        await harness.StartAndOpenAsync();
        harness.Switcher.Switcher.Open(harness.Switcher.OriginalId);
        await harness.FramesUntilAsync(() => harness.OpenId == harness.Switcher.OriginalId);
        Assert.True(harness.Guided.ShowsResumeReminder);

        var saves = harness.Store.Saves;
        harness.Guided.DismissReminder();
        Assert.False(harness.Guided.ShowsResumeReminder);
        Assert.Equal(saves, harness.Store.Saves); // nothing stored: only until the next load

        harness.Reload();
        Assert.True(harness.Guided.ShowsResumeReminder);
    }

    [Fact]
    public async Task Continue_WithUnsavedChangesElsewhere_AsksFirst_AndWaitsForTheAnswer()
    {
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Guided.Next();
        harness.Switcher.Switcher.Open(harness.Switcher.OriginalId);
        await harness.FramesUntilAsync(() => harness.OpenId == harness.Switcher.OriginalId);
        harness.Switcher.Plates.Edit();

        harness.Guided.Start();
        Assert.NotNull(harness.Switcher.Guard.Pending);
        Assert.True(harness.Guided.IsStarting); // Help and the reminder wait for the answer
        Assert.False(harness.Guided.ShowsResumeReminder);

        harness.Switcher.Guard.Cancel();
        harness.Frame();
        Assert.False(harness.Guided.IsStarting);
        Assert.Null(harness.Guided.StartError);
        Assert.Equal(harness.Switcher.OriginalId, harness.OpenId);
        Assert.True(harness.Guided.CanContinue);

        harness.Guided.Start();
        harness.Switcher.Guard.Save();
        await harness.FramesUntilAsync(() => !harness.Guided.IsStarting && harness.OpenId == made);
        Assert.Null(harness.Guided.StartError);
        Assert.True(harness.Guided.IsGuiding(made));
        Assert.Equal(GuidedStage.MakeItYours, harness.Guided.Stage);
    }

    [Fact]
    public async Task TheResumeReminder_Shows_OnceTheEditorIsClosedMidGuide_AndContinueOpensItAgain()
    {
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Guided.Next();
        Assert.False(harness.Guided.ShowsResumeReminder);

        // Closing the Basic editor leaves its Plate open in the editors.
        harness.BasicEditorOpen = false;
        Assert.Equal(made, harness.OpenId);
        Assert.True(harness.Guided.ShowsResumeReminder);

        harness.Guided.Start();
        harness.Frame();
        Assert.Equal([EditorSurfaceKind.Basic], harness.Shown);
        Assert.True(harness.Guided.IsGuiding(made));
        Assert.Equal(GuidedStage.MakeItYours, harness.Guided.Stage);
    }

    [Fact]
    public async Task WhenTheGuidedPlateWasDeleted_StartMakesANewOne()
    {
        using var harness = await GuidedHarness.CreateAsync();
        var first = await harness.StartAndOpenAsync();
        harness.Switcher.Switcher.Open(harness.Switcher.OriginalId);
        await harness.FramesUntilAsync(() => harness.OpenId == harness.Switcher.OriginalId);
        await harness.Library.DeletePlateAsync(first);

        Assert.False(harness.Guided.CanContinue);
        Assert.False(harness.Guided.ShowsResumeReminder);

        var second = await harness.StartAndOpenAsync();
        Assert.NotEqual(first, second);
        Assert.Equal(GuidedStage.ChooseLook, harness.Guided.Stage);
    }

    [Fact]
    public async Task Start_WithUnsavedChanges_AsksFirst_AndCancelLeavesNoPlate()
    {
        using var harness = await GuidedHarness.CreateAsync();
        harness.Switcher.Plates.Edit();
        var plates = harness.PlateCount;

        harness.Guided.Start();
        Assert.True(harness.Guided.IsStarting);
        Assert.NotNull(harness.Switcher.Guard.Pending);
        harness.Frame();
        Assert.True(harness.Guided.IsStarting); // the question waits

        harness.Switcher.Guard.Cancel();
        harness.Frame();

        Assert.False(harness.Guided.IsStarting);
        Assert.Null(harness.Guided.StartError);
        Assert.Equal(plates, harness.PlateCount);
        Assert.Null(harness.Store.Preferences.PlateId);
        Assert.Equal(GuidedRunStatus.None, harness.Store.Preferences.Run);
        Assert.Equal(GuidedOfferAnswer.Undecided, harness.Store.Preferences.Offer); // nothing was started: the welcome may still come
        Assert.True(harness.Switcher.IsDirty);
        Assert.Equal(harness.Switcher.OriginalId, harness.OpenId);
    }

    [Fact]
    public async Task Start_WithUnsavedChanges_Save_SavesThemFirst_ThenMakesTheGuidedPlate()
    {
        using var harness = await GuidedHarness.CreateAsync();
        harness.Switcher.Plates.Edit();

        harness.Guided.Start();
        harness.Switcher.Guard.Save();
        await harness.FramesUntilAsync(() => !harness.Guided.IsStarting && harness.OpenId == harness.Store.Preferences.PlateId);

        Assert.Contains(harness.Library.GetSavedDocument(harness.Switcher.OriginalId)!.Elements, e => e is TextProfileElement { Text: "An unsaved line" });
        Assert.True(harness.Guided.IsGuiding(harness.OpenId));
    }

    [Fact]
    public async Task Start_WhenThePlateCantBeMade_SaysWhy_AndRecordsNothing()
    {
        var store = new FaultInjectingStore();
        using var harness = await GuidedHarness.CreateAsync(store);
        var plates = harness.PlateCount;
        store.FailWrite = path => !path.Contains(harness.Switcher.OriginalId.ToString(), StringComparison.OrdinalIgnoreCase);

        harness.Guided.Start();
        await harness.FramesUntilAsync(() => !harness.Guided.IsStarting);

        Assert.NotNull(harness.Guided.StartError);
        Assert.Equal(plates, harness.PlateCount);
        Assert.Null(harness.Store.Preferences.PlateId);
        Assert.Equal(GuidedRunStatus.None, harness.Store.Preferences.Run);
        Assert.Equal(GuidedOfferAnswer.Undecided, harness.Store.Preferences.Offer);
        Assert.Equal(harness.Switcher.OriginalId, harness.OpenId);
    }

    // ---------------------------------------------------------------- the steps

    [Fact]
    public async Task NextAndBack_MoveBetweenSteps_KeepingEverythingEntered_AndRememberTheStep()
    {
        using var harness = await GuidedHarness.CreateAsync();
        await harness.StartAndOpenAsync();
        Assert.False(harness.Guided.CanGoBack);
        harness.Guided.Back(); // nothing before the first step
        Assert.Equal(GuidedStage.ChooseLook, harness.Guided.Stage);

        var look = CuratedLooks.Styles()[1];
        harness.Editor.Basic.ApplyTheme(look);
        harness.Guided.Next();
        harness.Editor.Identity.SetNameText("Lyra Moonfall");
        harness.Editor.Identity.Commit();
        harness.Editor.Basic.SetText(ProfileElementRole.BasicMessage, "Find me in Limsa.");
        harness.Editor.Basic.CommitTextEdit();
        harness.Guided.Next();
        Assert.Equal(GuidedStage.Save, harness.Guided.Stage);
        Assert.Equal(3, harness.Guided.StepNumber);
        harness.Guided.Next(); // nothing after the last step
        Assert.Equal(GuidedStage.Save, harness.Guided.Stage);

        harness.Guided.Back();
        harness.Guided.Back();
        harness.Frame();

        Assert.Equal(GuidedStage.ChooseLook, harness.Guided.Stage);
        Assert.Equal(GuidedStage.ChooseLook, harness.Store.Preferences.Stage);
        Assert.Equal(look.Id, PlateStyle.InUse(harness.Editor.Document)?.Id);
        Assert.Equal("Lyra Moonfall", BasicIdentitySession.Find(harness.Editor.Document, ProfileElementRole.BasicName)!.Text);
        Assert.Equal("Find me in Limsa.", BasicSections.FindText(harness.Editor.Document, ProfileElementRole.BasicMessage)!.Text);
        Assert.True(harness.Commands.IsDirty); // moving between steps saves nothing
        Assert.Equal(GuidedRunStatus.InProgress, harness.Store.Preferences.Run);
    }

    [Fact]
    public async Task ExitGuide_KeepsThePlate_AndStartingAgainMakesANewPlate()
    {
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Guided.Next();
        harness.Editor.Identity.SetNameText("Lyra Moonfall");
        harness.Editor.Identity.Commit();

        harness.Guided.Leave();

        Assert.False(harness.Guided.IsGuiding(made));
        Assert.Equal(GuidedRunStatus.Left, harness.Store.Preferences.Run);
        Assert.Equal(made, harness.OpenId);
        Assert.True(harness.Commands.IsDirty); // the edits stay
        Assert.False(harness.Guided.ShowsResumeReminder); // left on purpose: no nagging
        Assert.False(harness.Guided.CanContinue); // and not reopened on an old step later

        Assert.True(await harness.Commands.SaveAsync());
        var plates = harness.PlateCount;
        var next = await harness.StartAndOpenAsync();
        Assert.NotEqual(made, next);
        Assert.Equal(plates + 1, harness.PlateCount);
        Assert.Equal(GuidedStage.ChooseLook, harness.Guided.Stage);
        Assert.Equal("Lyra Moonfall", BasicIdentitySession.Find(harness.Library.GetSavedDocument(made)!, ProfileElementRole.BasicName)!.Text);
    }

    [Fact]
    public async Task CreateStepByStep_AlwaysMakesANewPlate_EvenWithOneUnderWay()
    {
        using var harness = await GuidedHarness.CreateAsync();
        var first = await harness.StartAndOpenAsync();
        harness.Guided.Next();
        var plates = harness.PlateCount;

        harness.Guided.StartNew();
        await harness.FramesUntilAsync(() => !harness.Guided.IsStarting && harness.OpenId is { } open && open != first);

        var second = harness.Store.Preferences.PlateId!.Value;
        Assert.NotEqual(first, second);
        Assert.Equal(second, harness.OpenId);
        Assert.Equal(plates + 1, harness.PlateCount);
        Assert.NotNull(harness.Library.FindPlate(first)); // the first stays, as it was saved
        Assert.Equal(GuidedStage.ChooseLook, harness.Guided.Stage);
        Assert.True(harness.Guided.IsGuiding(second));
    }

    [Theory]
    [InlineData(3)] // a step a newer build added
    [InlineData(-1)]
    [InlineData(99)]
    public async Task AStepThisBuildDoesntKnow_ReadsAsTheFirstStep(int stored)
    {
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Store.Preferences.Stage = (GuidedStage)stored;

        Assert.True(harness.Guided.IsGuiding(made));
        Assert.Equal(GuidedStage.ChooseLook, harness.Guided.Stage);
        Assert.Equal(1, harness.Guided.StepNumber);
        Assert.False(harness.Guided.CanGoBack);

        harness.Guided.Next();
        Assert.Equal(GuidedStage.MakeItYours, harness.Store.Preferences.Stage);
    }

    // ---------------------------------------------------------------- saving

    [Fact]
    public async Task Save_SavesThePlate_ThenCompletes_AndShowsTheSavedState()
    {
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Editor.Basic.ApplyTheme(CuratedLooks.Styles()[0]);
        harness.Guided.Next();
        harness.Editor.Identity.SetNameText("Lyra Moonfall");
        harness.Editor.Identity.Commit();
        harness.Guided.Next();

        await harness.SaveAsync();

        Assert.Null(harness.Guided.SaveError);
        Assert.Equal(GuidedRunStatus.Completed, harness.Store.Preferences.Run);
        Assert.Equal(1, harness.Store.Preferences.CompletedCount);
        Assert.False(harness.Guided.IsGuiding(made));
        Assert.True(harness.Guided.ShowsSuccess(made));
        Assert.Equal(made, harness.Guided.SuccessPlateId);
        Assert.False(harness.Commands.IsDirty);
        var saved = harness.Library.GetSavedDocument(made)!;
        Assert.Equal("Lyra Moonfall", BasicIdentitySession.Find(saved, ProfileElementRole.BasicName)!.Text);
        Assert.Equal(CuratedLooks.Styles()[0].Id, PlateStyle.InUse(saved)?.Id);

        harness.Frame();
        Assert.True(harness.Guided.ShowsSuccess(made)); // until it is dismissed, or the Plate edited, closed or switched
        harness.Guided.DismissSuccess();
        Assert.False(harness.Guided.ShowsSuccess(made));

        // The next guided creation makes a new Plate.
        Assert.False(harness.Guided.CanContinue);
        var next = await harness.StartAndOpenAsync();
        Assert.NotEqual(made, next);
    }

    [Fact]
    public async Task Save_WithNothingUnsaved_CompletesAtOnce()
    {
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Guided.Next();
        harness.Guided.Next();
        Assert.False(harness.Commands.IsDirty);

        harness.Guided.Save();

        Assert.False(harness.Guided.IsSaving);
        Assert.Equal(GuidedRunStatus.Completed, harness.Store.Preferences.Run);
        Assert.True(harness.Guided.ShowsSuccess(made));
    }

    [Fact]
    public async Task AFailedSave_KeepsTheStepAndEverythingEntered_SaysWhy_AndCompletesNothing_UntilASaveSucceeds()
    {
        var store = new FaultInjectingStore();
        using var harness = await GuidedHarness.CreateAsync(store);
        var made = await harness.StartAndOpenAsync();
        harness.Guided.Next();
        harness.Editor.Identity.SetNameText("Lyra Moonfall");
        harness.Editor.Identity.Commit();
        harness.Editor.Basic.SetText(ProfileElementRole.BasicMessage, "Find me in Limsa.");
        harness.Editor.Basic.CommitTextEdit();
        harness.Guided.Next();
        var savedBefore = harness.Library.GetSavedDocument(made)!;
        store.FailWrite = path => path.Contains(made.ToString(), StringComparison.OrdinalIgnoreCase);

        await harness.SaveAsync();

        Assert.NotNull(harness.Guided.SaveError);
        Assert.Equal(GuidedRunStatus.InProgress, harness.Store.Preferences.Run);
        Assert.Equal(GuidedStage.Save, harness.Guided.Stage);
        Assert.Equal(0, harness.Store.Preferences.CompletedCount);
        Assert.True(harness.Guided.IsGuiding(made));
        Assert.False(harness.Guided.ShowsSuccess(made));
        Assert.True(harness.Commands.IsDirty);
        Assert.Equal("Lyra Moonfall", BasicIdentitySession.Find(harness.Editor.Document, ProfileElementRole.BasicName)!.Text);
        Assert.Equal("Find me in Limsa.", BasicSections.FindText(harness.Editor.Document, ProfileElementRole.BasicMessage)!.Text);
        Assert.NotEqual("Lyra Moonfall", BasicIdentitySession.Find(harness.Library.GetSavedDocument(made)!, ProfileElementRole.BasicName)?.Text);
        Assert.Equal(savedBefore.Elements.Count, harness.Library.GetSavedDocument(made)!.Elements.Count);

        // The editor's own error line holds the same failure: the steps show it once, as "Not saved".
        Assert.NotNull(harness.Editor.Session.ErrorMessage);
        Assert.Same(harness.Editor.Session.ErrorMessage, harness.Guided.SaveFailure);

        // Back clears the message, and the header still leaves the failure out until the editor's next operation.
        harness.Guided.Back();
        harness.Frame();
        Assert.Null(harness.Guided.SaveError);
        Assert.Same(harness.Editor.Session.ErrorMessage, harness.Guided.SaveFailure);
        harness.Editor.Basic.SetText(ProfileElementRole.BasicMessage, "Find me in Gridania.");
        harness.Editor.Basic.CommitTextEdit();
        harness.Frame();
        Assert.Null(harness.Guided.SaveFailure);
        harness.Guided.Next();
        store.FailWrite = null;
        await harness.SaveAsync();

        Assert.Null(harness.Guided.SaveError);
        Assert.Equal(GuidedRunStatus.Completed, harness.Store.Preferences.Run);
        Assert.Equal("Lyra Moonfall", BasicIdentitySession.Find(harness.Library.GetSavedDocument(made)!, ProfileElementRole.BasicName)!.Text);
    }

    [Fact]
    public async Task AnUndoWhileTheSaveIsWritten_LeavesItUnsaved_AndCompletesNothing()
    {
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Guided.Next();
        harness.Editor.Identity.SetNameText("Lyra Moonfall");
        harness.Editor.Identity.Commit();
        harness.Guided.Next();

        harness.Guided.Save();
        Assert.True(harness.Guided.IsSaving);
        Assert.False(harness.Guided.CanEdit); // the steps' Undo, Redo, Exit Guide and Ctrl+Z wait
        harness.Guided.Leave(); // Exit Guide can't slip in meanwhile
        Assert.Equal(GuidedRunStatus.InProgress, harness.Store.Preferences.Run);

        // An Undo that reached the editor anyway: the save writes what was there when Save was pressed.
        harness.Commands.Undo();
        await harness.FramesUntilAsync(() => !harness.Guided.IsSaving && !harness.Commands.IsSaving);

        Assert.True(harness.Commands.IsDirty);
        Assert.Equal(GuidedRunStatus.InProgress, harness.Store.Preferences.Run);
        Assert.False(harness.Guided.ShowsSuccess(made));
        Assert.True(harness.Guided.IsGuiding(made));
        Assert.Null(harness.Guided.SaveError);
        Assert.True(harness.Guided.CanEdit);

        // Save Plate again saves what is shown, and completes.
        await harness.SaveAsync();
        Assert.Equal(GuidedRunStatus.Completed, harness.Store.Preferences.Run);
        Assert.False(harness.Commands.IsDirty);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("close")]
    [InlineData("switch")]
    public async Task TheSavedState_EndsOnceThePlateIsEditedClosedOrSwitched(string then)
    {
        using var harness = await GuidedHarness.CreateAsync();
        var made = await harness.StartAndOpenAsync();
        harness.Guided.Next();
        harness.Guided.Next();
        harness.Guided.Save();
        Assert.True(harness.Guided.ShowsSuccess(made));

        switch (then)
        {
            case "edit":
                harness.Editor.Basic.SetText(ProfileElementRole.BasicMessage, "Find me in Limsa.");
                harness.Editor.Basic.CommitTextEdit();
                break;
            case "close":
                harness.BasicEditorOpen = false;
                break;
            default:
                harness.Switcher.Switcher.Open(harness.Switcher.OriginalId);
                await harness.FramesUntilAsync(() => harness.OpenId == harness.Switcher.OriginalId);
                break;
        }

        harness.Frame();
        Assert.Null(harness.Guided.SuccessPlateId);

        // Back on the saved Plate, it is edited normally: no saved state, no steps.
        harness.BasicEditorOpen = true;
        if (harness.OpenId != made)
        {
            harness.Switcher.Switcher.Open(made);
            await harness.FramesUntilAsync(() => harness.OpenId == made);
        }

        Assert.False(harness.Guided.ShowsSuccess(made));
        Assert.False(harness.Guided.IsGuiding(made));
    }

    [Fact]
    public async Task Save_WhenAnotherPlateIsOpen_DoesNothing()
    {
        using var harness = await GuidedHarness.CreateAsync();
        await harness.StartAndOpenAsync();
        harness.Guided.Next();
        harness.Guided.Next();
        harness.Switcher.Switcher.Open(harness.Switcher.OriginalId);
        await harness.FramesUntilAsync(() => harness.OpenId == harness.Switcher.OriginalId);
        harness.Switcher.Plates.Edit();

        harness.Guided.Save();

        Assert.False(harness.Guided.IsSaving);
        Assert.Equal(GuidedRunStatus.InProgress, harness.Store.Preferences.Run);
        Assert.True(harness.Commands.IsDirty);
    }

    [Fact]
    public async Task NothingInGuidedCreation_TouchesTheTutorialOrAnyConsent()
    {
        // Guided creation's state lives in its own block; sharing and the online count are never
        // part of it (the source names neither).
        using var harness = await GuidedHarness.CreateAsync();
        await harness.StartAndOpenAsync();
        harness.Guided.Next();
        harness.Guided.Next();
        await harness.SaveAsync();

        Assert.Equal(GuidedRunStatus.Completed, harness.Store.Preferences.Run);
        var root = RepositoryPaths.Root().FullName;
        foreach (var relative in new[] { "AetherFrame/UI/Tutorial/GuidedCreation.cs", "AetherFrame/UI/Tutorial/GuidedCreationPreferences.cs", "AetherFrame/Windows/Tutorial/WelcomeWindow.cs" })
        {
            var code = System.IO.File.ReadAllLines(System.IO.Path.Combine(root, relative))
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
            foreach (var forbidden in new[] { "Sharing", "Consent", "Acknowledge", "OnlineCount", "Network" })
            {
                Assert.DoesNotContain(code, line => line.Contains(forbidden, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void EachStep_HasOneShortLineOfGuidance_AndContinueOrSaveAsItsOnePrimaryButton()
    {
        // Rich's rules for onboarding (October 6, 2026): one decision per step with a short heading
        // and at most one short guidance sentence, labelled controls instead of paragraphs, one
        // obvious primary button (Continue or Save), extra explanations behind tooltips or Help.
        var root = RepositoryPaths.Root().FullName;
        var steps = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "AetherFrame", "Windows", "BasicProfileEditorWindow.Guided.cs"));

        Assert.DoesNotMatch(@"\bHint\(", steps);
        var guidance = System.Text.RegularExpressions.Regex.Matches(steps, @"AetherControls\.Secondary\(""(?<text>[^""]*)""\)")
            .Select(match => match.Groups["text"].Value)
            .ToList();
        Assert.Equal(3, guidance.Count);
        Assert.All(guidance, line =>
        {
            Assert.True(line.Length <= 40, line);
            Assert.DoesNotContain(". ", line, StringComparison.Ordinal);
        });

        // The footer's one primary button, and the saved state's View Plate: nothing else is primary.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(steps, @"AetherControls\.PrimaryButton\(").Count);
        Assert.Contains("GuidedStage.ChooseLook => (\"Continue\"", steps, StringComparison.Ordinal);
        Assert.Contains("GuidedStage.MakeItYours => (\"Continue\"", steps, StringComparison.Ordinal);
        Assert.Contains("guided.IsSaving ? \"Saving...\" : \"Save\"", steps, StringComparison.Ordinal);

        // The welcome: one line and the steps' names; the rest is in its buttons' tooltips.
        var welcome = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "AetherFrame", "Windows", "Tutorial", "WelcomeWindow.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(welcome, @"ImGui\.TextWrapped\(").Count);
    }

    // ---------------------------------------------------------------- the curated looks

    [Fact]
    public void TheCuratedLooks_AreFourArtStylesWithBundledPreviews_EachWithASimpleThemeFallback()
    {
        var styles = CuratedLooks.Styles();

        Assert.Equal(4, styles.Count);
        Assert.Equal(CuratedLooks.Pairs.Select(p => p.StyleId), styles.Select(s => s.Id));
        Assert.Equal(4, styles.Select(s => s.Id).Distinct().Count());
        foreach (var style in styles)
        {
            Assert.True(style.IsArtStyle, style.Id);
            var fallback = CuratedLooks.FallbackFor(style);
            Assert.NotNull(fallback);
            Assert.False(fallback!.IsArtStyle);
        }

        Assert.NotNull(CuratedLooks.FallbackFor(null));
        Assert.Equal(CuratedLooks.DefaultFallbackThemeId, CuratedLooks.FallbackFor(null)!.Id);
    }

    [Fact]
    public async Task ChangingTheLook_KeepsTheNameMessageAndPortrait()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Identity.SetNameText("Lyra Moonfall");
        harness.Identity.Commit();
        harness.Basic.SetText(ProfileElementRole.BasicMessage, "Find me in Limsa.");
        harness.Basic.CommitTextEdit();
        harness.Basic.SetPortrait(harness.ImportablePng());
        var portrait = harness.Basic.Portrait!.AssetId;

        foreach (var look in CuratedLooks.Styles().Concat(CuratedLooks.Styles().Select(CuratedLooks.FallbackFor).OfType<ProfileThemePreset>()))
        {
            harness.Basic.ApplyTheme(look);

            Assert.Equal(look.Id, PlateStyle.InUse(harness.Document)?.Id);
            Assert.Equal("Lyra Moonfall", BasicIdentitySession.Find(harness.Document, ProfileElementRole.BasicName)!.Text);
            Assert.Equal("Find me in Limsa.", BasicSections.FindText(harness.Document, ProfileElementRole.BasicMessage)!.Text);
            Assert.Equal(portrait, harness.Basic.Portrait?.AssetId);
        }
    }

    [Fact]
    public async Task AMessageTypedOnANewAdventurePlate_IsShown()
    {
        using var harness = await BasicHarness.NewClassicAsync();

        harness.Basic.SetText(ProfileElementRole.BasicMessage, "Find me in Limsa.");
        harness.Basic.CommitTextEdit();

        Assert.True(BasicSections.FindText(harness.Document, ProfileElementRole.BasicMessage)!.Visible);
        Assert.True(BasicSections.IsVisible(harness.Document, BasicSection.Message));
    }

    // ---------------------------------------------------------------- the switcher tells who asked

    [Fact]
    public async Task NewPlate_TellsTheIdOnlyToTheNewThatAskedForIt()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var told = new List<Guid>();

        harness.Switcher.New(BuiltInTemplateCatalog.AdventurePlateClassicId, told.Add);
        await harness.FramesUntilAsync(() => harness.Shown.Count > 0);
        Assert.Equal([harness.OpenId!.Value], told);

        harness.Switcher.New(BuiltInTemplateCatalog.AdventurePlateClassicId);
        await harness.FramesUntilAsync(() => harness.Shown.Count > 1);
        Assert.Single(told);
    }

    [Fact]
    public async Task NewPlate_ACancelledQuestion_ForgetsWhoAsked()
    {
        using var harness = await SwitcherHarness.CreateAsync();
        var told = new List<Guid>();
        harness.Plates.Edit();
        harness.Switcher.New(BuiltInTemplateCatalog.AdventurePlateClassicId, told.Add);
        harness.Guard.Cancel();
        harness.Frame();

        harness.Plates.Editor.Session.RevertToSaved(undoable: false);
        harness.Switcher.New(BuiltInTemplateCatalog.AdventurePlateClassicId);
        await harness.FramesUntilAsync(() => harness.Shown.Count > 0);

        Assert.Empty(told);
    }
}
