using System;
using System.Collections.Generic;

namespace AetherFrame.UI.Tutorial;

/// <summary>The player's answer to the first-run offer.</summary>
internal enum FirstRunAnswer
{
    StartTutorial,
    MaybeLater,
    DoNotShowAgain,
}

/// <summary>
/// The one owner of the tutorial's state: the first-run offer, the running
/// <see cref="TutorialSession"/>, and the persisted <see cref="TutorialPreferences"/>. Windows
/// and the overlay talk to this and nothing else. It holds no reference to a Plate, a Library or
/// an editor: everything it knows about the interface arrives as a
/// <see cref="TutorialContextSnapshot"/>, read-only, so nothing the tutorial does can change a
/// Plate. Render thread only.
/// </summary>
internal sealed class OnboardingCoordinator
{
    private readonly ITutorialPreferencesStore store;
    private readonly int scriptVersion;
    private bool offerRequested;

    internal OnboardingCoordinator(ITutorialPreferencesStore store, IReadOnlyList<TutorialChapter> chapters, int scriptVersion)
    {
        this.store = store;
        this.scriptVersion = scriptVersion;
        Session = new TutorialSession(chapters);
    }

    internal TutorialSession Session { get; }

    /// <summary>Raised whenever the tutorial starts or resumes (the plugin uses it to settle other one-time prompts).</summary>
    internal event Action? Started;

    internal TutorialPreferences Preferences => store.Preferences;

    internal int ScriptVersion => scriptVersion;

    /// <summary>Whether the first-run offer should be on screen.</summary>
    internal bool IsOfferOpen { get; private set; }

    /// <summary>True once per requested offer: the window that shows it opens.</summary>
    internal bool ConsumeOfferRequest()
    {
        var requested = offerRequested;
        offerRequested = false;
        return requested;
    }

    internal bool IsTutorialActive => Session.IsRunning;

    /// <summary>
    /// A quiet reminder belongs in My Plates: the player said Maybe Later (or left an offer
    /// unanswered often enough) and hasn't dismissed it, taken the tour, or declined.
    /// </summary>
    internal bool ShowReminder =>
        !Preferences.ReminderDismissed
        && (Preferences.Status == TutorialStatus.Deferred || (Preferences.Status == TutorialStatus.Undecided && Preferences.Install == TutorialInstallKind.NewInstall && Preferences.OfferCount >= FirstRunDetector.MaxOffers));

    /// <summary>A started tutorial stopped partway, at a place that still exists in this script version.</summary>
    internal bool CanResume =>
        Preferences.Status == TutorialStatus.InProgress && Preferences.Version == scriptVersion
        && Preferences.LastChapter >= 0 && Preferences.LastChapter < Session.ChapterCount;

    /// <summary>The script changed since the player completed it (Help can say "updated").</summary>
    internal bool IsUpdatedSinceCompletion => Preferences.Status == TutorialStatus.Completed && Preferences.CompletedVersion < scriptVersion;

    /// <summary>
    /// At load, once the Library's state is known: records what kind of install this is (once), and
    /// requests the first-run offer for a new player. Never touches a Plate.
    /// </summary>
    internal void ResolveFirstRun(bool configurationFound, bool configurationUnreadable, bool libraryLoaded, int plateCount, int userTemplateCount)
    {
        var decision = FirstRunDetector.Decide(Preferences, configurationFound, configurationUnreadable, libraryLoaded, plateCount, userTemplateCount);
        switch (decision)
        {
            case FirstRunDecision.ExistingInstall:
                if (Preferences.Install != TutorialInstallKind.ExistingInstall)
                {
                    Preferences.Install = TutorialInstallKind.ExistingInstall;
                    store.Save();
                }

                break;

            case FirstRunDecision.OfferTutorial:
                Preferences.Install = TutorialInstallKind.NewInstall;
                Preferences.OfferCount++;
                store.Save();
                IsOfferOpen = true;
                offerRequested = true;
                break;
        }

        LastDecision = decision;
    }

    /// <summary>What <see cref="ResolveFirstRun"/> decided (for the log and tests).</summary>
    internal FirstRunDecision? LastDecision { get; private set; }

    /// <summary>The player answered the offer.</summary>
    internal void AnswerOffer(FirstRunAnswer answer, TutorialContextSnapshot snapshot)
    {
        IsOfferOpen = false;
        offerRequested = false;
        switch (answer)
        {
            case FirstRunAnswer.StartTutorial:
                StartTutorial(snapshot);
                return;
            case FirstRunAnswer.MaybeLater:
                Preferences.Status = TutorialStatus.Deferred;
                break;
            case FirstRunAnswer.DoNotShowAgain:
                Preferences.Status = TutorialStatus.Declined;
                Preferences.ReminderDismissed = true;
                break;
        }

        store.Save();
    }

    /// <summary>The offer closed without an answer (its close button): asked again next time, up to the limit.</summary>
    internal void DismissOffer()
    {
        IsOfferOpen = false;
        offerRequested = false;
    }

    /// <summary>Starts the tutorial from its first chapter (the offer's Start, or Help's Start Tutorial).</summary>
    internal void StartTutorial(TutorialContextSnapshot snapshot) => StartChapter(snapshot, 0);

    /// <summary>Starts at <paramref name="chapter"/> (Help's chapter picker, or the card's).</summary>
    internal void StartChapter(TutorialContextSnapshot snapshot, int chapter)
    {
        IsOfferOpen = false;
        Preferences.Status = TutorialStatus.InProgress;
        Preferences.Version = scriptVersion;
        Preferences.ReminderDismissed = true;
        if (Session.IsRunning)
        {
            Session.JumpToChapter(snapshot, chapter);
        }
        else
        {
            Session.Start(snapshot, chapter);
        }

        RememberPlaceOrFinish();
        Started?.Invoke();
    }

    /// <summary>Continues a tutorial that stopped partway; starts over when there's nothing to resume.</summary>
    internal void ResumeTutorial(TutorialContextSnapshot snapshot)
    {
        if (!CanResume)
        {
            StartTutorial(snapshot);
            return;
        }

        IsOfferOpen = false;
        Session.Resume(snapshot, Preferences.LastChapter, Preferences.LastStep);
        RememberPlaceOrFinish();
        Started?.Invoke();
    }

    /// <summary>The card's Next. Returns false when Next is held because the step waits for the player (nothing moved).</summary>
    internal bool Next(TutorialContextSnapshot snapshot)
    {
        if (Session.IsNextHeld(snapshot))
        {
            return false;
        }

        Session.Next(snapshot);
        RememberPlaceOrFinish();
        return true;
    }

    internal void Back(TutorialContextSnapshot snapshot)
    {
        Session.Back(snapshot);
        RememberPlace();
    }

    /// <summary>The player leaves before the end (Skip, or the card's close): remembered as skipped, resumable from Help.</summary>
    internal void SkipTutorial()
    {
        if (!Session.IsRunning)
        {
            return;
        }

        RememberPlace();
        Session.Skip();
        Preferences.Status = TutorialStatus.Skipped;
        store.Save();
    }

    /// <summary>The plugin is unloading or the overlay can't show: the place is kept, nothing else changes.</summary>
    internal void Suspend()
    {
        if (Session.IsRunning)
        {
            RememberPlace();
            store.Save();
            Session.Stop();
        }
    }

    /// <summary>The My Plates reminder's dismiss.</summary>
    internal void DismissReminder()
    {
        if (!Preferences.ReminderDismissed)
        {
            Preferences.ReminderDismissed = true;
            store.Save();
        }
    }

    /// <summary>
    /// Every frame while the tutorial runs: auto-advances a step whose condition came true, and
    /// returns what to draw. Null when nothing is running.
    /// </summary>
    internal TutorialStepView? Tick(TutorialContextSnapshot snapshot, Func<TutorialTarget, bool> targetAvailable)
    {
        if (!Session.IsRunning)
        {
            return null;
        }

        if (Session.TryAutoAdvance(snapshot))
        {
            RememberPlaceOrFinish();
            if (!Session.IsRunning)
            {
                return null;
            }
        }

        return Session.Evaluate(snapshot, targetAvailable);
    }

    private void RememberPlaceOrFinish()
    {
        if (Session.Status == TutorialSessionStatus.Completed)
        {
            Preferences.Status = TutorialStatus.Completed;
            Preferences.CompletedVersion = scriptVersion;
            Preferences.LastChapter = 0;
            Preferences.LastStep = 0;
            store.Save();
            return;
        }

        RememberPlace();
    }

    /// <summary>Progress is saved only when the chapter changes, so paging through steps doesn't write the configuration every click.</summary>
    private void RememberPlace()
    {
        if (!Session.IsRunning)
        {
            return;
        }

        var chapterChanged = Preferences.LastChapter != Session.ChapterIndex;
        Preferences.LastChapter = Session.ChapterIndex;
        Preferences.LastStep = Session.StepIndex;
        if (chapterChanged || Preferences.Status != TutorialStatus.InProgress)
        {
            Preferences.Status = TutorialStatus.InProgress;
            store.Save();
        }
    }
}
