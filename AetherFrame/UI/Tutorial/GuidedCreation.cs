using System;
using System.Threading.Tasks;
using AetherFrame.Domain.Templates;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;

namespace AetherFrame.UI.Tutorial;

/// <summary>How the player answered the welcome.</summary>
internal enum WelcomeAnswer
{
    /// <summary>Create my first Plate: guided creation starts.</summary>
    Create,

    /// <summary>Not now: the welcome may ask again on a later load, while there is still no Plate.</summary>
    NotNow,

    /// <summary>Don't show again: no welcome from now on; an empty My Plates and Help still offer the steps.</summary>
    Never,
}

/// <summary>What the welcome on screen does this frame (see <see cref="GuidedCreation.WelcomeOnScreenNow"/>).</summary>
internal enum WelcomeOnScreen
{
    /// <summary>It stays.</summary>
    Stays,

    /// <summary>It closes: My Plates holds a Plate, so it has nothing left to offer.</summary>
    Closes,

    /// <summary>
    /// It steps aside: the character logged out, or a recovery offer came up. It shows again once
    /// nothing stands in front of it while My Plates is still empty, and that showing isn't counted twice.
    /// </summary>
    StepsAside,
}

/// <summary>
/// Guided creation: a short route to a personalized, saved Plate in three steps (Choose a Look, Make
/// It Yours, Save), shown by the Basic editor around its live view while the open Plate is the one
/// the route made (<see cref="IsGuiding"/>). It is the introductory route for a new player, offered
/// by the welcome once the Libraries have loaded, after any recovery offer has been answered
/// (<see cref="WelcomeMayShow"/>), and reachable by anyone from Help, Create Plate's chooser and an
/// empty My Plates.
///
/// <para>Everything it changes goes through the existing commands: the Plate is made by the editors'
/// New Plate (<see cref="PlateSwitcher.New"/>, Adventure Plate Classic, asking first about unsaved
/// changes), edited through the Basic editor's own session (so Undo, Redo, Revert, recovery
/// checkpoints and image ownership work as they always do), and saved by the editors' Save. The Plate
/// is made once: its id is stored the moment it exists, and every later start, Back, close, reload
/// or crash opens that Plate again on the step it reached. Completion is recorded only when Save at
/// the Save step succeeds.</para>
///
/// <para>Sharing and its consent and verification are never touched here, nor is any other consent:
/// sharing stays a separate action in My Plates. Render thread only.</para>
/// </summary>
internal sealed class GuidedCreation
{
    /// <summary>The welcome stops after this many unanswered showings, as if Not now were chosen.</summary>
    internal const int MaxOffers = FirstRunDetector.MaxOffers;

    /// <summary>What the Save step says when the save failed and the editor gave no reason.</summary>
    internal const string SaveFailedMessage = "Your Plate couldn't be saved.";

    private readonly IGuidedCreationStore store;
    private readonly PlateLibraryService library;
    private readonly ProfileService profiles;
    private readonly EditorDocumentCommands commands;
    private readonly EditorSession session;
    private readonly PlateSwitcher switcher;
    private readonly Action<EditorSurfaceKind> showEditor;
    private readonly Func<bool> basicEditorOpen;

    private bool offerRequested;
    private bool welcomeWithdrawn;
    private bool starting;
    private Task<bool>? saving;
    private Guid? savingPlateId;
    private Guid? successPlateId;
    private bool resumeHidden;
    private string? saveFailure;

    /// <param name="store">Where the state and the Basic editor's view are kept.</param>
    /// <param name="library">Whether the guided Plate still exists.</param>
    /// <param name="profiles">The open Plate.</param>
    /// <param name="commands">The editors' Save, and whether the open Plate has unsaved changes.</param>
    /// <param name="session">Why a save failed.</param>
    /// <param name="switcher">The editors' New Plate and Open another Plate, which ask first about unsaved changes.</param>
    /// <param name="showEditor">Shows the open Plate in the Basic or Advanced editor.</param>
    /// <param name="basicEditorOpen">Whether the Basic editor's window is open (the open Plate stays open after it closes).</param>
    internal GuidedCreation(
        IGuidedCreationStore store,
        PlateLibraryService library,
        ProfileService profiles,
        EditorDocumentCommands commands,
        EditorSession session,
        PlateSwitcher switcher,
        Action<EditorSurfaceKind> showEditor,
        Func<bool> basicEditorOpen)
    {
        this.store = store;
        this.library = library;
        this.profiles = profiles;
        this.commands = commands;
        this.session = session;
        this.switcher = switcher;
        this.showEditor = showEditor;
        this.basicEditorOpen = basicEditorOpen;
    }

    internal GuidedCreationPreferences Preferences => store.Preferences;

    // ---------------------------------------------------------------- the Basic editor's view

    /// <summary>Whether the Basic editor shows its Simple view (the everyday controls first, the rest folded away).</summary>
    internal bool SimpleWorkspace => store.Workspace == BasicWorkspaceMode.Simple;

    /// <summary>The player's explicit choice of view. Kept from then on.</summary>
    internal void SetSimpleWorkspace(bool simple)
    {
        var mode = simple ? BasicWorkspaceMode.Simple : BasicWorkspaceMode.Detailed;
        if (store.Workspace != mode)
        {
            store.Workspace = mode;
            store.Save();
        }
    }

    // ---------------------------------------------------------------- the welcome

    /// <summary>
    /// Whether the welcome may show now: a character is logged in (so the Plate it makes starts with
    /// that character's details and becomes their Active Plate, as any first Plate does), and nothing
    /// about recovery stands in front of it: every kept unsaved change and recovery checkpoint has
    /// been read (<paramref name="recoveryRead"/>, false while any is unread or the read failed, so
    /// what recovery holds is unknown), and none waits for an answer or is on screen. A recovery offer
    /// always comes first; the welcome waits for it.
    /// </summary>
    internal static bool WelcomeMayShow(bool loggedIn, bool recoveryRead, bool recoveryAwaitsAnswer, bool recoveryOnScreen) =>
        loggedIn && recoveryRead && !recoveryAwaitsAnswer && !recoveryOnScreen;

    /// <summary>
    /// At load, once the Libraries' state is known. A new player (<see cref="OnboardingCoordinator.IsNewPlayer"/>)
    /// with no Plate gets the Simple view unless a view was already chosen, and the welcome when they
    /// haven't turned it off with Don't Show Again, haven't started guided creation, and it hasn't been
    /// shown too often. A player with Plates keeps the view they have (an earlier version's install can
    /// still count as new). An install that couldn't be judged is never welcomed: a Library that
    /// didn't load (whatever the stored verdict says, since its count of Plates means nothing then),
    /// or a configuration that couldn't be read (never a new player). Never touches a Plate.
    /// </summary>
    internal void ResolveWelcome(bool newPlayer, bool librariesLoaded, int plateCount)
    {
        if (!newPlayer || !librariesLoaded || plateCount > 0)
        {
            return;
        }

        if (store.Workspace == BasicWorkspaceMode.Unset)
        {
            store.Workspace = BasicWorkspaceMode.Simple;
            store.Save();
        }

        offerRequested = Preferences.Offer is GuidedOfferAnswer.Undecided or GuidedOfferAnswer.Deferred
            && Preferences.Run == GuidedRunStatus.None
            && Preferences.OfferCount < MaxOffers;
    }

    /// <summary>Whether a welcome waits to be shown.</summary>
    internal bool WelcomeRequested => offerRequested;

    /// <summary>
    /// For the welcome window, once a frame: true once, when a welcome waits,
    /// <paramref name="mayShow"/> (<see cref="WelcomeMayShow"/>) says nothing stands in front of it,
    /// and My Plates is still empty (a Plate made meanwhile, from Create Plate, ends the wait).
    /// Counts the showing, unless it is the same showing back after stepping aside (<see cref="WithdrawWelcome"/>).
    /// </summary>
    internal bool ConsumeWelcome(bool mayShow)
    {
        if (!offerRequested || !mayShow)
        {
            return false;
        }

        offerRequested = false;
        var returning = welcomeWithdrawn;
        welcomeWithdrawn = false;
        if (library.GetOrderedPlates().Count > 0)
        {
            return false;
        }

        if (!returning)
        {
            Preferences.OfferCount++;
            store.Save();
        }

        return true;
    }

    /// <summary>Whether My Plates holds a Plate: a welcome on screen then has nothing left to offer.</summary>
    internal bool HasPlate => library.GetOrderedPlates().Count > 0;

    /// <summary>
    /// The welcome on screen, once a frame. A logout or a recovery offer (<paramref name="mayShow"/>
    /// false, see <see cref="WelcomeMayShow"/>) moves it aside whatever it shows, an error from its
    /// last start included, so it is never on screen without a character or over recovery. Otherwise
    /// it stays while its own start is under way (that start's outcome decides), and a Plate in My
    /// Plates closes it, except while it says why its start failed and the guided Plate that start
    /// made still exists: Create My First Plate then opens that Plate again. The error is the one
    /// guided creation still reports (<see cref="WelcomeErrorNow"/>): a start made since, from Help,
    /// My Plates or Create Plate, ends it, so a Plate made or opened that way closes the welcome.
    /// </summary>
    /// <param name="starting">The welcome's own start is under way.</param>
    /// <param name="showsStartError">The welcome shows why its last start failed.</param>
    /// <param name="hasPlate">My Plates holds a Plate (<see cref="HasPlate"/>).</param>
    /// <param name="guidedPlateExists">A guided creation is under way and its Plate exists (<see cref="CanContinue"/>).</param>
    /// <param name="mayShow">Nothing stands in front of the welcome (<see cref="WelcomeMayShow"/>).</param>
    internal static WelcomeOnScreen WelcomeOnScreenNow(bool starting, bool showsStartError, bool hasPlate, bool guidedPlateExists, bool mayShow)
    {
        if (!mayShow)
        {
            return WelcomeOnScreen.StepsAside;
        }

        if (starting)
        {
            return WelcomeOnScreen.Stays;
        }

        return hasPlate && !(showsStartError && guidedPlateExists) ? WelcomeOnScreen.Closes : WelcomeOnScreen.Stays;
    }

    /// <summary>
    /// What the welcome still shows of its own last start's error, once a frame while no start of its
    /// own is under way: <paramref name="shown"/> while guided creation still reports it
    /// (<paramref name="current"/>, <see cref="StartError"/>), nothing once it doesn't. Every start
    /// clears that report, so the welcome never holds a reason a later start, from anywhere, replaced.
    /// </summary>
    internal static string? WelcomeErrorNow(string? shown, string? current) =>
        shown is not null && string.Equals(shown, current, StringComparison.Ordinal) ? shown : null;

    /// <summary>
    /// The welcome on screen closed unanswered because something now stands in front of it (the
    /// character logged out, or a recovery offer came up): it waits to show again in this load, while
    /// My Plates is still empty. The showing stays counted, so a load that ends before it shows
    /// again has still used one, and showing again isn't counted twice.
    /// </summary>
    internal void WithdrawWelcome()
    {
        offerRequested = true;
        welcomeWithdrawn = true;
    }

    /// <summary>The player answered the welcome.</summary>
    internal void AnswerWelcome(WelcomeAnswer answer)
    {
        offerRequested = false;
        switch (answer)
        {
            case WelcomeAnswer.Create:
                Start();
                return;
            case WelcomeAnswer.NotNow:
                Preferences.Offer = GuidedOfferAnswer.Deferred;
                break;
            case WelcomeAnswer.Never:
                Preferences.Offer = GuidedOfferAnswer.Declined;
                break;
        }

        store.Save();
    }

    // ---------------------------------------------------------------- My Plates' reminder

    /// <summary>
    /// Whether My Plates shows its reminder to continue a guided creation left partway: its Plate
    /// still exists and the Basic editor isn't showing its steps, until the reminder's Not Now hides
    /// it for this load.
    /// </summary>
    internal bool ShowsResumeReminder =>
        !resumeHidden && !starting && CanContinue && !StepsOnScreen;

    /// <summary>Whether the Basic editor is showing the steps now: continuing them has nothing to do.</summary>
    internal bool StepsOnScreen => IsGuiding(profiles.OpenPlateId) && basicEditorOpen();

    /// <summary>The reminder's Not Now: hidden until AetherFrame next loads. Help can still continue.</summary>
    internal void DismissReminder() => resumeHidden = true;

    // ---------------------------------------------------------------- starting and resuming

    /// <summary>A start is under way: the new Plate is being made, or the unsaved-changes question waits.</summary>
    internal bool IsStarting => starting;

    /// <summary>Why the last start couldn't go ahead, or null.</summary>
    internal string? StartError { get; private set; }

    /// <summary>
    /// Whether a guided creation is under way, not saved at its Save step and not left with Exit
    /// Guide, and its Plate still exists: Start continues it.
    /// </summary>
    internal bool CanContinue =>
        Preferences.Run == GuidedRunStatus.InProgress && Preferences.PlateId is { } plateId && PlateExists(plateId);

    /// <summary>
    /// Starts guided creation: continues the guided creation under way (<see cref="CanContinue"/>),
    /// on the step it reached; otherwise makes a new Plate (see <see cref="StartNew"/>).
    /// </summary>
    internal void Start() => Begin(continueUnderWay: true);

    /// <summary>
    /// Makes a new Plate (Adventure Plate Classic, through the editors' New Plate, which asks first
    /// about unsaved changes) and opens it on Choose a Look, even while another guided creation is
    /// under way (that Plate stays in My Plates as it is). Create Plate's Create Step by Step. The new
    /// Plate's id is stored as soon as it exists, so nothing ever makes a second one for one start.
    /// </summary>
    internal void StartNew() => Begin(continueUnderWay: false);

    private void Begin(bool continueUnderWay)
    {
        StartError = null;
        if (starting)
        {
            return;
        }

        resumeHidden = false;
        if (continueUnderWay && CanContinue)
        {
            OpenGuidedPlate(Preferences.PlateId!.Value);
            return;
        }

        var decision = switcher.New(BuiltInTemplateCatalog.AdventurePlateClassicId, Created);
        if (decision == PlateOpenDecision.Refused)
        {
            StartError = switcher.Runner.Error ?? PlateSwitcher.CannotOpenNote;
            return;
        }

        starting = true;
    }

    /// <summary>
    /// The guided Plate exists: recorded before it opens, on Choose a Look. The start stays under way
    /// until the Plate has opened (<see cref="Advance"/>), so a failure to open it is reported too.
    /// </summary>
    private void Created(Guid plateId)
    {
        Preferences.Offer = GuidedOfferAnswer.Accepted;
        Preferences.PlateId = plateId;
        Preferences.Run = GuidedRunStatus.InProgress;
        Preferences.Stage = GuidedStage.ChooseLook;
        store.Save();
    }

    private bool PlateExists(Guid plateId) => FindPlate(plateId) is not null;

    private PlateSummary? FindPlate(Guid plateId)
    {
        foreach (var plate in library.GetOrderedPlates())
        {
            if (plate.PlateId == plateId)
            {
                return plate;
            }
        }

        return null;
    }

    private void OpenGuidedPlate(Guid plateId)
    {
        if (profiles.OpenPlateId == plateId)
        {
            showEditor(EditorSurfaceKind.Basic);
            return;
        }

        // A Plate My Plates lists but can't open (damaged, locked, or saved by a newer version) says
        // why at once, before any question about unsaved changes.
        if (FindPlate(plateId) is { } plate && switcher.WhyNotOpen(plate) is { } why)
        {
            StartError = why;
            return;
        }

        // Opened at the start of the next frame, or once the unsaved-changes question is answered: the
        // start stays under way until the open has run, so its failure is reported too (Advance).
        var decision = switcher.Open(plateId);
        if (decision == PlateOpenDecision.Refused)
        {
            StartError = switcher.Runner.Error ?? PlateSwitcher.CannotOpenNote;
            return;
        }

        starting = true;
    }

    /// <summary>
    /// Once a frame: a start that ended (the Plate opened, the question's Cancel, or a failure) stops
    /// waiting and says why it failed; a save that finished completes the route or says why it
    /// failed; the saved state ends once its Plate is closed, another is opened, or it is edited.
    /// </summary>
    internal void Advance()
    {
        if (starting && !switcher.Runner.IsBusy && switcher.Guard.Pending is null && !switcher.Guard.IsSaving && !switcher.OpenQueued)
        {
            starting = false;
            StartError = switcher.Runner.Error;
        }

        // The Save step's failure stays the Save step's to show until the editor's next operation replaces it.
        if (saveFailure is not null && !ReferenceEquals(session.ErrorMessage, saveFailure))
        {
            saveFailure = null;
        }

        if (successPlateId is { } shown && (profiles.OpenPlateId != shown || commands.IsDirty || !basicEditorOpen()))
        {
            successPlateId = null;
        }

        // A failed Save's "Not saved" stands only while what it kept is still unsaved in the steps: a
        // later save or Discard (the editor's close question, or another Plate's open) ends it.
        if (SaveError is not null && (!commands.IsDirty || !IsGuiding(profiles.OpenPlateId)))
        {
            SaveError = null;
        }

        if (saving is not { IsCompleted: true } finished)
        {
            return;
        }

        saving = null;
        var plateId = savingPlateId;
        savingPlateId = null;
        if (!(finished.IsCompletedSuccessfully && finished.Result))
        {
            SaveError = session.ErrorMessage ?? SaveFailedMessage;
            saveFailure = session.ErrorMessage;
            return;
        }

        // Saved: complete only while the steps still stand where Save was pressed, and only when
        // nothing changed during the save (an Undo then leaves unsaved changes, so Save stays).
        if (plateId is { } saved && IsGuiding(saved) && profiles.OpenPlateId == saved && !commands.IsDirty)
        {
            Complete(saved);
        }
    }

    // ---------------------------------------------------------------- the steps

    /// <summary>Whether the Basic editor shows the steps for <paramref name="openPlateId"/>: it is the guided Plate, and the route is under way.</summary>
    internal bool IsGuiding(Guid? openPlateId) =>
        openPlateId is { } open && Preferences.Run == GuidedRunStatus.InProgress && Preferences.PlateId == open;

    /// <summary>Whether the Basic editor shows the saved state for <paramref name="openPlateId"/> (View Plate, Keep Editing).</summary>
    internal bool ShowsSuccess(Guid? openPlateId) => openPlateId is { } open && successPlateId == open;

    /// <summary>The step reached. A step this build doesn't know (one a newer build stored) is Choose a Look.</summary>
    internal GuidedStage Stage =>
        Preferences.Stage is >= GuidedStage.ChooseLook and <= GuidedStage.Save ? Preferences.Stage : GuidedStage.ChooseLook;

    /// <summary>The step's position, 1 to 3.</summary>
    internal int StepNumber => (int)Stage + 1;

    internal const int StepCount = 3;

    internal bool CanGoBack => Stage > GuidedStage.ChooseLook && !IsSaving;

    /// <summary>Whether Undo, Redo and Exit Guide may act: not while the Save step's save is being written.</summary>
    internal bool CanEdit => !IsSaving;

    /// <summary>Next: the following step. Nothing is saved by moving; the Plate's edits stay in the editor.</summary>
    internal void Next()
    {
        if (Stage < GuidedStage.Save)
        {
            Preferences.Stage = Stage + 1;
            SaveError = null;
            store.Save();
        }
    }

    /// <summary>Back: the previous step, with everything entered kept.</summary>
    internal void Back()
    {
        if (CanGoBack)
        {
            Preferences.Stage = Stage - 1;
            SaveError = null;
            store.Save();
        }
    }

    /// <summary>
    /// Exit Guide: the Plate stays as it is and is edited normally from now on. It is no longer a
    /// guided creation under way: starting again makes a new Plate.
    /// </summary>
    internal void Leave()
    {
        if (Preferences.Run == GuidedRunStatus.InProgress && !IsSaving)
        {
            Preferences.Run = GuidedRunStatus.Left;
            SaveError = null;
            store.Save();
        }
    }

    // ---------------------------------------------------------------- saving

    /// <summary>The Save step's save is being written.</summary>
    internal bool IsSaving => saving is not null;

    /// <summary>Why the Save step's last save failed, or null. Everything entered stays in the editor.</summary>
    internal string? SaveError { get; private set; }

    /// <summary>
    /// The editor's own error line for the Save step's failed save, while it still stands: the steps
    /// show it once, as the Save step's "Not saved", so the header leaves this one out.
    /// </summary>
    internal string? SaveFailure => saveFailure;

    /// <summary>
    /// The Save step's Save: the editors' Save, for the guided Plate. A Plate with nothing unsaved is
    /// already saved as it is, so it completes at once. Completion is recorded only once the save
    /// has succeeded (<see cref="Advance"/>); a failed save keeps the route on this step with
    /// everything entered, and says why.
    /// </summary>
    internal void Save()
    {
        if (saving is not null || !IsGuiding(profiles.OpenPlateId) || commands.IsSaving)
        {
            return;
        }

        SaveError = null;
        var plateId = Preferences.PlateId!.Value;
        if (!commands.IsDirty)
        {
            Complete(plateId);
            return;
        }

        savingPlateId = plateId;
        saving = commands.SaveAsync();
    }

    private void Complete(Guid plateId)
    {
        Preferences.Run = GuidedRunStatus.Completed;
        Preferences.Stage = GuidedStage.ChooseLook;
        Preferences.CompletedCount++;
        store.Save();
        successPlateId = plateId;
    }

    /// <summary>
    /// The saved state's Keep Editing or View Plate: the Plate is edited normally from now on. (It
    /// also ends by itself once the Plate is edited, closed, or another Plate is opened.)
    /// </summary>
    internal void DismissSuccess() => successPlateId = null;

    /// <summary>The Plate the saved state is about, while it shows.</summary>
    internal Guid? SuccessPlateId => successPlateId;
}
