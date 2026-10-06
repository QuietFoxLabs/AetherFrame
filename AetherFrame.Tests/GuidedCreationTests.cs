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

    private GuidedCreation Create(MemoryGuidedStore store) => new(
        store,
        Switcher.Library,
        Switcher.Plates.Editor.Profiles,
        Commands,
        Switcher.Plates.Editor.Session,
        Switcher.Switcher,
        Shown.Add);

    public void Dispose() => Switcher.Dispose();
}

/// <summary>
/// Guided creation (Choose a Look, Make It Yours, Save): who is welcomed and when (a recovery offer
/// always first; an install that couldn't be judged never), the welcome's answers and My Plates'
/// reminder, one Plate per guided creation however often it is started, closed or reloaded, Back
/// and Next keeping everything entered, completion only after a successful save, and the Basic
/// editor's Simple view for new players only.
/// </summary>
public class GuidedCreationTests
{
    // ---------------------------------------------------------------- recovery first

    [Theory]
    [InlineData(false, false, false, false)] // kept changes not read yet: wait
    [InlineData(true, true, false, false)] // a recovery offer waits for an answer
    [InlineData(true, false, true, false)] // the recovery window is on screen
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, true)] // nothing about recovery stands in front
    public void TheWelcome_WaitsForEveryRecoveryOffer(bool read, bool awaitsAnswer, bool onScreen, bool expected)
    {
        Assert.Equal(expected, GuidedCreation.WelcomeMayShow(read, awaitsAnswer, onScreen));
    }

    [Fact]
    public async Task AWelcomeThatMayNotShowYet_IsKept_AndShownOnceRecoveryIsAnswered()
    {
        using var harness = await GuidedHarness.CreateAsync();
        harness.Guided.ResolveWelcome(newPlayer: true, plateCount: 0);

        Assert.False(harness.Guided.ConsumeWelcome(mayShow: false));
        Assert.False(harness.Guided.ConsumeWelcome(mayShow: false));
        Assert.True(harness.Guided.WelcomeRequested);
        Assert.Equal(0, harness.Store.Preferences.OfferCount);

        Assert.True(harness.Guided.ConsumeWelcome(mayShow: true));
        Assert.False(harness.Guided.ConsumeWelcome(mayShow: true));
        Assert.Equal(1, harness.Store.Preferences.OfferCount);
    }

    // ---------------------------------------------------------------- who is welcomed

    [Fact]
    public async Task ANewPlayerWithNoPlate_IsWelcomed_AndGetsTheSimpleView()
    {
        using var harness = await GuidedHarness.CreateAsync();

        harness.Guided.ResolveWelcome(newPlayer: true, plateCount: 0);

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

        harness.Guided.ResolveWelcome(newPlayer, plates);

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
            harness.Guided.ResolveWelcome(newPlayer: decision == FirstRunDecision.OfferTutorial, plateCount: 0);
            Assert.False(harness.Guided.WelcomeRequested);
            Assert.Equal(BasicWorkspaceMode.Unset, harness.Store.Workspace);
        }
    }

    [Fact]
    public async Task ANewPlayerWhoAlreadyMadeAPlate_IsNotWelcomed_ButGetsTheSimpleView()
    {
        using var harness = await GuidedHarness.CreateAsync();

        harness.Guided.ResolveWelcome(newPlayer: true, plateCount: 1);

        Assert.False(harness.Guided.WelcomeRequested);
        Assert.True(harness.Guided.SimpleWorkspace);
    }

    [Fact]
    public async Task AViewTheNewPlayerChose_IsKept()
    {
        using var harness = await GuidedHarness.CreateAsync();
        harness.Store.Workspace = BasicWorkspaceMode.Detailed;

        harness.Guided.ResolveWelcome(newPlayer: true, plateCount: 0);

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
        harness.Guided.ResolveWelcome(newPlayer: false, plateCount: 3);
        Assert.Equal(BasicWorkspaceMode.Detailed, harness.Store.Workspace);
    }

    // ---------------------------------------------------------------- the welcome's answers

    [Fact]
    public async Task ClosingTheWelcome_AsksAgainNextTime_ButOnlyAFewTimes_ThenTheReminderStays()
    {
        using var harness = await GuidedHarness.CreateAsync();
        for (var launch = 1; launch <= GuidedCreation.MaxOffers + 2; launch++)
        {
            harness.Reload();
            harness.Guided.ResolveWelcome(newPlayer: true, plateCount: 0);
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
        Assert.Equal(GuidedReminder.Offer, harness.Guided.Reminder);
    }

    [Fact]
    public async Task NotNow_LeavesAQuietReminder_UntilItIsDismissed_ForGood()
    {
        using var harness = await GuidedHarness.CreateAsync();
        harness.Guided.ResolveWelcome(newPlayer: true, plateCount: 0);
        harness.Guided.ConsumeWelcome(mayShow: true);

        harness.Guided.AnswerWelcome(WelcomeAnswer.NotNow);

        Assert.Equal(GuidedOfferAnswer.Deferred, harness.Store.Preferences.Offer);
        Assert.Equal(GuidedReminder.Offer, harness.Guided.Reminder);
        Assert.Equal(harness.PlateCount, harness.Library.GetOrderedPlates().Count);

        harness.Reload();
        harness.Guided.ResolveWelcome(newPlayer: true, plateCount: 0);
        Assert.False(harness.Guided.WelcomeRequested); // not a nag
        Assert.Equal(GuidedReminder.Offer, harness.Guided.Reminder);

        harness.Guided.DismissReminder();
        Assert.Equal(GuidedReminder.None, harness.Guided.Reminder);
        harness.Reload();
        Assert.Equal(GuidedReminder.None, harness.Guided.Reminder);
    }

    [Fact]
    public async Task DontShowAgain_LeavesNoWelcomeAndNoReminder_ButStartStillWorks()
    {
        using var harness = await GuidedHarness.CreateAsync();
        harness.Guided.ResolveWelcome(newPlayer: true, plateCount: 0);
        harness.Guided.ConsumeWelcome(mayShow: true);
        var plates = harness.PlateCount;

        harness.Guided.AnswerWelcome(WelcomeAnswer.Never);

        Assert.Equal(GuidedOfferAnswer.Declined, harness.Store.Preferences.Offer);
        Assert.Equal(GuidedReminder.None, harness.Guided.Reminder);
        Assert.Equal(plates, harness.PlateCount);
        harness.Reload();
        harness.Guided.ResolveWelcome(newPlayer: true, plateCount: 0);
        Assert.False(harness.Guided.WelcomeRequested);

        // Help's "Create a Plate step by step".
        var made = await harness.StartAndOpenAsync();
        Assert.Equal(plates + 1, harness.PlateCount);
        Assert.True(harness.Guided.IsGuiding(made));
    }

    // ---------------------------------------------------------------- starting: one Plate, however often

    [Fact]
    public async Task Create_MakesOneAdventurePlate_RecordsIt_AndOpensItOnTheFirstStep()
    {
        using var harness = await GuidedHarness.CreateAsync();
        harness.Guided.ResolveWelcome(newPlayer: true, plateCount: 0);
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
        Assert.Equal(GuidedReminder.None, harness.Guided.Reminder); // it's open
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
        Assert.Equal(GuidedReminder.Resume, harness.Guided.Reminder);
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
        Assert.Equal(GuidedReminder.Resume, harness.Guided.Reminder);

        harness.Guided.DismissReminder();
        Assert.Equal(GuidedReminder.None, harness.Guided.Reminder);
        Assert.False(harness.Store.Preferences.ReminderDismissed);

        harness.Reload();
        Assert.Equal(GuidedReminder.Resume, harness.Guided.Reminder);
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
        Assert.Equal(GuidedReminder.None, harness.Guided.Reminder);

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
    public async Task ExitGuide_KeepsThePlate_AndHelpBringsTheStepsBack()
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
        Assert.Equal(GuidedReminder.None, harness.Guided.Reminder); // left on purpose: no nagging
        Assert.True(harness.Guided.CanContinue);

        harness.Guided.Start();
        harness.Frame();
        Assert.True(harness.Guided.IsGuiding(made));
        Assert.Equal(GuidedStage.MakeItYours, harness.Guided.Stage);
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

        // Back clears the message; the next try works.
        harness.Guided.Back();
        Assert.Null(harness.Guided.SaveError);
        harness.Guided.Next();
        store.FailWrite = null;
        await harness.SaveAsync();

        Assert.Null(harness.Guided.SaveError);
        Assert.Equal(GuidedRunStatus.Completed, harness.Store.Preferences.Run);
        Assert.Equal("Lyra Moonfall", BasicIdentitySession.Find(harness.Library.GetSavedDocument(made)!, ProfileElementRole.BasicName)!.Text);
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
