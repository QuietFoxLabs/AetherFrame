using System.Collections.Generic;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Tutorial;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// First-run detection and the tutorial's persisted state: a new install is a new player (whom
/// guided creation welcomes; the tutorial itself is never offered), an install upgrading from
/// v0.1.6 (or with Plates) never is, an earlier version's answers are kept, a started tutorial
/// resumes, a completed one knows its version, and nothing here reaches a Plate.
/// </summary>
public class OnboardingCoordinatorTests
{
    private sealed class MemoryStore : ITutorialPreferencesStore
    {
        public TutorialPreferences Preferences { get; set; } = new();

        public int Saves { get; private set; }

        public void Save() => Saves++;
    }

    private static readonly TutorialContextSnapshot Library = new(MyPlatesOpen: true, PlateCount: 1);
    private static readonly TutorialContextSnapshot Basic = new(MyPlatesOpen: true, PlateCount: 1, ActiveEditor: EditorSurfaceKind.Basic, PlateOpen: true);

    private static OnboardingCoordinator Create(MemoryStore store) => new(store, TutorialScript.Chapters, TutorialScript.Version);

    // ---------------------------------------------------------------- first-run detection

    [Theory]
    [InlineData(false, false, true, 0, 0, (int)FirstRunDecision.OfferTutorial)] // new: nothing at all
    [InlineData(true, false, true, 0, 0, (int)FirstRunDecision.ExistingInstall)] // a v0.1.6 configuration, no Plates yet
    [InlineData(false, true, true, 0, 0, (int)FirstRunDecision.ExistingInstall)] // a damaged configuration: only an established install has one
    [InlineData(false, false, true, 3, 0, (int)FirstRunDecision.ExistingInstall)] // pre-0.1.6: no configuration, but Plates
    [InlineData(false, false, true, 0, 1, (int)FirstRunDecision.ExistingInstall)] // or a saved Template
    [InlineData(false, false, false, 0, 0, (int)FirstRunDecision.Undetermined)] // the Library didn't load: can't tell
    [InlineData(true, false, false, 0, 0, (int)FirstRunDecision.ExistingInstall)] // but a configuration settles it without the Library
    public void Decide_TellsANewInstallFromAnExistingOne(bool configurationFound, bool unreadable, bool libraryLoaded, int plates, int templates, int expected)
    {
        Assert.Equal((FirstRunDecision)expected, FirstRunDetector.Decide(new TutorialPreferences(), configurationFound, unreadable, libraryLoaded, plates, templates));
    }

    [Fact]
    public void Decide_OnceTheKindIsStored_TheStoredKindWins()
    {
        // The configuration this build writes on a new player's first load must not make them "existing" next time.
        var newInstall = new TutorialPreferences { Install = TutorialInstallKind.NewInstall };
        Assert.Equal(FirstRunDecision.OfferTutorial, FirstRunDetector.Decide(newInstall, configurationFound: true, false, true, 0, 0));

        var existing = new TutorialPreferences { Install = TutorialInstallKind.ExistingInstall };
        Assert.Equal(FirstRunDecision.AlreadyDecided, FirstRunDetector.Decide(existing, false, false, true, 0, 0));

        foreach (var status in new[] { TutorialStatus.Deferred, TutorialStatus.Declined, TutorialStatus.InProgress, TutorialStatus.Completed, TutorialStatus.Skipped })
        {
            Assert.Equal(FirstRunDecision.AlreadyDecided, FirstRunDetector.Decide(new TutorialPreferences { Status = status }, false, false, true, 0, 0));
        }
    }

    [Fact]
    public void Decide_APendingInstall_IsJudgedByTheLibraryAlone_EvenThoughAConfigurationNowExists()
    {
        // The first load found no configuration but saved one (for the guidance flag) before the
        // Library was read; the next launch must not take that file for an established install.
        var pending = new TutorialPreferences { Install = TutorialInstallKind.PendingDecision };

        Assert.Equal(FirstRunDecision.OfferTutorial, FirstRunDetector.Decide(pending, configurationFound: true, false, true, 0, 0));
        Assert.Equal(FirstRunDecision.ExistingInstall, FirstRunDetector.Decide(pending, configurationFound: true, false, true, 2, 0));
        Assert.Equal(FirstRunDecision.Undetermined, FirstRunDetector.Decide(pending, configurationFound: true, false, libraryLoaded: false, 0, 0));

        var store = new MemoryStore { Preferences = pending };
        var coordinator = new OnboardingCoordinator(store, TutorialScript.Chapters, TutorialScript.Version);
        coordinator.ResolveFirstRun(configurationFound: true, false, true, 0, 0);
        Assert.True(coordinator.IsNewPlayer);
        Assert.Equal(TutorialInstallKind.NewInstall, store.Preferences.Install);
    }

    [Fact]
    public void Decide_AnUnreadableConfiguration_IsAnExistingInstall_WhateverTheStoredKindAndTheLibrary()
    {
        // A file that exists but can't be read belongs to an established install (only v0.1.6 or
        // later wrote one). The plugin records that before the file is rewritten, so the stored
        // kind is never "pending" for such a file; the detector must not depend on that either.
        Assert.Equal(FirstRunDecision.ExistingInstall, FirstRunDetector.Decide(new TutorialPreferences(), configurationFound: false, configurationUnreadable: true, libraryLoaded: false, 0, 0));
        var pending = new TutorialPreferences { Install = TutorialInstallKind.PendingDecision };
        Assert.Equal(FirstRunDecision.ExistingInstall, FirstRunDetector.Decide(pending, configurationFound: false, configurationUnreadable: true, libraryLoaded: true, 0, 0));
        Assert.Equal(FirstRunDecision.AlreadyDecided, FirstRunDetector.Decide(new TutorialPreferences { Install = TutorialInstallKind.ExistingInstall }, false, true, true, 0, 0));

        var store = new MemoryStore { Preferences = pending };
        var coordinator = Create(store);
        coordinator.ResolveFirstRun(configurationFound: false, configurationUnreadable: true, libraryLoaded: true, 0, 0);
        Assert.False(coordinator.IsNewPlayer);
        Assert.Equal(FirstRunDecision.ExistingInstall, coordinator.LastDecision);
        Assert.Equal(TutorialInstallKind.ExistingInstall, store.Preferences.Install);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void Decide_StopsOfferingAfterAFewUnansweredShowings()
    {
        var prefs = new TutorialPreferences { Install = TutorialInstallKind.NewInstall, OfferCount = FirstRunDetector.MaxOffers };
        Assert.Equal(FirstRunDecision.AlreadyDecided, FirstRunDetector.Decide(prefs, false, false, true, 0, 0));
    }

    [Fact]
    public void ANewInstall_IsANewPlayer_AndTheKindIsStored_ButTheTutorialIsNotOffered()
    {
        var store = new MemoryStore();
        var coordinator = Create(store);

        coordinator.ResolveFirstRun(configurationFound: false, configurationUnreadable: false, libraryLoaded: true, plateCount: 0, userTemplateCount: 0);

        Assert.Equal(FirstRunDecision.OfferTutorial, coordinator.LastDecision);
        Assert.True(coordinator.IsNewPlayer);
        Assert.Equal(TutorialInstallKind.NewInstall, store.Preferences.Install);
        Assert.Equal(0, store.Preferences.OfferCount);
        Assert.Equal(TutorialStatus.Undecided, store.Preferences.Status);
        Assert.Equal(1, store.Saves);
        Assert.False(coordinator.IsTutorialActive);
        Assert.False(coordinator.ShowReminder);

        // The next launch: still a new player (guided creation decides whether to welcome again), nothing written.
        var next = Create(store);
        next.ResolveFirstRun(configurationFound: true, false, true, 0, 0);
        Assert.True(next.IsNewPlayer);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void AnExistingInstall_IsNeverOffered_AndIsRememberedAsExisting()
    {
        var store = new MemoryStore();
        var coordinator = Create(store);

        coordinator.ResolveFirstRun(configurationFound: true, configurationUnreadable: false, libraryLoaded: true, plateCount: 0, userTemplateCount: 0);

        Assert.Equal(FirstRunDecision.ExistingInstall, coordinator.LastDecision);
        Assert.False(coordinator.IsNewPlayer);
        Assert.Equal(TutorialInstallKind.ExistingInstall, store.Preferences.Install);
        Assert.Equal(1, store.Saves);

        // Every later launch: nothing to decide, nothing written.
        coordinator.ResolveFirstRun(true, false, true, 5, 1);
        Assert.Equal(FirstRunDecision.AlreadyDecided, coordinator.LastDecision);
        Assert.Equal(1, store.Saves);
        Assert.False(coordinator.ShowReminder);
    }

    [Fact]
    public void AnUndeterminedInstall_AsksNothing_AndStoresNothing()
    {
        var store = new MemoryStore();
        var coordinator = Create(store);

        coordinator.ResolveFirstRun(false, false, libraryLoaded: false, 0, 0);

        Assert.Equal(FirstRunDecision.Undetermined, coordinator.LastDecision);
        Assert.False(coordinator.IsNewPlayer);
        Assert.Equal(TutorialInstallKind.Unknown, store.Preferences.Install);
        Assert.Equal(0, store.Saves);
    }

    // ---------------------------------------------------------------- an earlier version's answer to the tutorial offer

    [Fact]
    public void AnEarlierMaybeLater_KeepsItsQuietReminder_UntilDismissed_AndIsNoLongerANewPlayer()
    {
        var store = new MemoryStore { Preferences = new TutorialPreferences { Install = TutorialInstallKind.NewInstall, Status = TutorialStatus.Deferred, OfferCount = 1 } };
        var coordinator = Create(store);
        coordinator.ResolveFirstRun(true, false, true, 0, 0);

        Assert.Equal(FirstRunDecision.AlreadyDecided, coordinator.LastDecision);
        Assert.False(coordinator.IsNewPlayer);
        Assert.True(coordinator.ShowReminder);

        coordinator.DismissReminder();
        Assert.False(coordinator.ShowReminder);
        Assert.True(store.Preferences.ReminderDismissed);
    }

    [Fact]
    public void AnEarlierOfferLeftUnansweredEnoughTimes_KeepsItsReminder()
    {
        var store = new MemoryStore { Preferences = new TutorialPreferences { Install = TutorialInstallKind.NewInstall, OfferCount = FirstRunDetector.MaxOffers } };
        var coordinator = Create(store);
        coordinator.ResolveFirstRun(true, false, true, 0, 0);

        Assert.False(coordinator.IsNewPlayer);
        Assert.True(coordinator.ShowReminder);
        Assert.Equal(FirstRunDetector.MaxOffers, store.Preferences.OfferCount);
    }

    [Fact]
    public void AnEarlierDoNotShowAgain_HasNoReminder_ButHelpCanStillStartTheTutorial()
    {
        var store = new MemoryStore { Preferences = new TutorialPreferences { Install = TutorialInstallKind.NewInstall, Status = TutorialStatus.Declined } };
        var coordinator = Create(store);
        coordinator.ResolveFirstRun(true, false, true, 0, 0);

        Assert.False(coordinator.IsNewPlayer);
        Assert.False(coordinator.ShowReminder);

        coordinator.StartTutorial(Library);
        Assert.True(coordinator.IsTutorialActive);
        Assert.Equal(TutorialStatus.InProgress, store.Preferences.Status);
        Assert.NotNull(coordinator.Tick(Library, _ => true));
    }

    // ---------------------------------------------------------------- running, skipping, completing, resuming

    [Fact]
    public void Skipping_RemembersThePlace_AndHelpCanResumeIt()
    {
        var store = new MemoryStore();
        var coordinator = Create(store);
        coordinator.StartTutorial(Library);
        coordinator.Next(Library);
        coordinator.Next(Library);
        coordinator.Next(Library);
        var chapter = coordinator.Session.ChapterIndex;
        var step = coordinator.Session.StepIndex;
        Assert.True(chapter > 0);

        coordinator.SkipTutorial();

        Assert.False(coordinator.IsTutorialActive);
        Assert.Equal(TutorialStatus.Skipped, store.Preferences.Status);
        Assert.Equal(chapter, store.Preferences.LastChapter);
        Assert.Equal(step, store.Preferences.LastStep);
        Assert.False(coordinator.CanResume); // skipped is a decision; resume is for a tutorial left in progress

        coordinator.SkipTutorial(); // idempotent
        Assert.Equal(TutorialStatus.Skipped, store.Preferences.Status);
    }

    [Fact]
    public void Suspending_KeepsTheTutorialInProgress_AndResumesWhereItStopped()
    {
        var store = new MemoryStore();
        var coordinator = Create(store);
        coordinator.StartTutorial(Library);
        coordinator.Next(Library);
        coordinator.Next(Library);
        coordinator.Next(Library);
        var chapter = coordinator.Session.ChapterIndex;
        var step = coordinator.Session.StepIndex;

        coordinator.Suspend();
        Assert.False(coordinator.IsTutorialActive);
        Assert.Equal(TutorialStatus.InProgress, store.Preferences.Status);

        var next = Create(store);
        Assert.True(next.CanResume);
        next.ResumeTutorial(Library);
        Assert.True(next.IsTutorialActive);
        Assert.Equal(chapter, next.Session.ChapterIndex);
        Assert.Equal(step, next.Session.StepIndex);
    }

    [Fact]
    public void ResumingAnOlderScriptVersion_StartsOver()
    {
        var store = new MemoryStore { Preferences = new TutorialPreferences { Status = TutorialStatus.InProgress, Version = TutorialScript.Version - 1, LastChapter = 3, LastStep = 1 } };
        var coordinator = Create(store);

        Assert.False(coordinator.CanResume);
        coordinator.ResumeTutorial(Library);

        Assert.True(coordinator.IsTutorialActive);
        Assert.Equal(0, coordinator.Session.ChapterIndex);
        Assert.Equal(TutorialScript.Version, store.Preferences.Version);
    }

    [Fact]
    public void ResumingAnOutOfRangePlace_StartsOver()
    {
        var store = new MemoryStore { Preferences = new TutorialPreferences { Status = TutorialStatus.InProgress, Version = TutorialScript.Version, LastChapter = 99, LastStep = 99 } };
        var coordinator = Create(store);

        Assert.False(coordinator.CanResume);
        coordinator.ResumeTutorial(Library);
        Assert.Equal(0, coordinator.Session.ChapterIndex);
    }

    [Fact]
    public void Completing_RecordsTheVersion_AndAnUpdatedScriptIsNoticed()
    {
        var store = new MemoryStore();
        var coordinator = Create(store);
        coordinator.StartTutorial(Basic);
        var guard = 0;
        while (coordinator.IsTutorialActive)
        {
            if (!coordinator.Next(Basic))
            {
                // A held step (the Advanced Editor's steps, "Add text"): the player does what it
                // shows, and then Next moves on.
                var met = Basic;
                for (var i = 0; coordinator.Session.IsNextHeld(met); i++)
                {
                    Assert.True(i < 4, coordinator.Session.CurrentStep!.Id);
                    met = TutorialSessionTests.Meeting(met, coordinator.Session.NextWaitsFor(met));
                }

                Assert.True(coordinator.Next(met));
            }

            Assert.True(++guard < 200);
        }

        Assert.Equal(TutorialStatus.Completed, store.Preferences.Status);
        Assert.Equal(TutorialScript.Version, store.Preferences.CompletedVersion);
        Assert.False(coordinator.IsUpdatedSinceCompletion);
        Assert.False(coordinator.ShowReminder);
        Assert.Null(coordinator.Tick(Basic, _ => true));

        var newer = new OnboardingCoordinator(store, TutorialScript.Chapters, TutorialScript.Version + 1);
        Assert.True(newer.IsUpdatedSinceCompletion);
        newer.ResolveFirstRun(true, false, true, 3, 0);
        Assert.False(newer.IsNewPlayer); // an update never re-welcomes
    }

    [Fact]
    public void ManualRestart_AndChapterPicker_Work_WhileRunningOrNot()
    {
        var store = new MemoryStore();
        var coordinator = Create(store);

        coordinator.StartChapter(Basic, 3);
        Assert.True(coordinator.IsTutorialActive);
        Assert.Equal(3, coordinator.Session.ChapterIndex);
        Assert.Equal(3, store.Preferences.LastChapter);

        coordinator.StartChapter(Basic, 1);
        Assert.Equal(1, coordinator.Session.ChapterIndex);

        coordinator.StartTutorial(Basic);
        Assert.Equal(0, coordinator.Session.ChapterIndex);
        Assert.Equal(TutorialStatus.InProgress, store.Preferences.Status);
    }

    [Fact]
    public void Back_AndNext_KeepProgressWithoutSavingEveryClick()
    {
        var store = new MemoryStore();
        var coordinator = Create(store);
        coordinator.StartTutorial(Library);
        var saves = store.Saves;

        coordinator.Next(Library); // within the first chapter
        coordinator.Back(Library);
        Assert.Equal(saves, store.Saves);

        coordinator.Next(Library);
        coordinator.Next(Library);
        coordinator.Next(Library); // into the next chapter
        Assert.Equal(1, coordinator.Session.ChapterIndex);
        Assert.Equal(saves + 1, store.Saves);
        Assert.Equal(1, store.Preferences.LastChapter);
    }

    [Fact]
    public void Next_OnAStepThatWaitsForThePlayer_ReportsItWasHeld_AndMovesNothing()
    {
        var store = new MemoryStore();
        var coordinator = Create(store);
        coordinator.StartChapter(Library, 2);
        Assert.Equal("first.create", coordinator.Session.CurrentStep!.Id);
        var saves = store.Saves;

        Assert.False(coordinator.Next(Library));
        Assert.Equal("first.create", coordinator.Session.CurrentStep!.Id);
        Assert.Equal(saves, store.Saves);

        Assert.True(coordinator.Next(Library with { TemplateChooserOpen = true }));
        Assert.Equal("first.template", coordinator.Session.CurrentStep!.Id);
    }

    [Fact]
    public void Tick_AutoAdvancesAndReportsTheView_AndStopsAtCompletion()
    {
        var store = new MemoryStore();
        var coordinator = Create(store);
        coordinator.StartChapter(Library, 2); // Your First Plate
        Assert.Equal("first.create", coordinator.Session.CurrentStep!.Id);

        var waiting = coordinator.Tick(Library, _ => true);
        Assert.Equal("first.create", waiting!.Value.Step.Id);
        var advanced = coordinator.Tick(Library with { TemplateChooserOpen = true }, _ => true);
        Assert.Equal("first.template", advanced!.Value.Step.Id);
    }

    [Fact]
    public void Preferences_CloneAndRoundTripByConvention()
    {
        var prefs = new TutorialPreferences { Install = TutorialInstallKind.NewInstall, Status = TutorialStatus.InProgress, OfferCount = 2, Version = 1, CompletedVersion = 0, LastChapter = 4, LastStep = 2, ReminderDismissed = true };
        var clone = prefs.Clone();
        Assert.NotSame(prefs, clone);
        Assert.Equal(prefs.LastChapter, clone.LastChapter);

        // Plain properties and integer enums: what Newtonsoft (the plugin configuration) writes by convention.
        var json = System.Text.Json.JsonSerializer.Serialize(prefs);
        var back = System.Text.Json.JsonSerializer.Deserialize<TutorialPreferences>(json)!;
        Assert.Equal(TutorialStatus.InProgress, back.Status);
        Assert.Equal(4, back.LastChapter);
        Assert.True(back.ReminderDismissed);
        Assert.Contains("\"Status\":3", json);
    }

    [Fact]
    public void TheTutorialHoldsNoReferenceToPlatesLibrariesOrEditors()
    {
        // Structural guard: the coordinator and session only see the interface through a
        // read-only snapshot, so no tutorial navigation can reach a Plate, a Library or a service.
        foreach (var type in new[] { typeof(OnboardingCoordinator), typeof(TutorialSession), typeof(TutorialAnchorRegistry) })
        {
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
            {
                var name = field.FieldType.FullName ?? string.Empty;
                Assert.DoesNotContain("AetherFrame.Domain", name);
                Assert.DoesNotContain("AetherFrame.Services", name);
                Assert.DoesNotContain("AetherFrame.Persistence", name);
                Assert.DoesNotContain("AetherFrame.Windows", name);
                Assert.DoesNotContain("EditorSession", name);
            }
        }

        // And a snapshot is a value: handing one to the tutorial can't hand it a live object.
        Assert.True(typeof(TutorialContextSnapshot).IsValueType);
        foreach (var property in typeof(TutorialContextSnapshot).GetProperties())
        {
            Assert.True(property.PropertyType.IsValueType || property.PropertyType == typeof(EditorSurfaceKind?), property.Name);
        }
    }
}
