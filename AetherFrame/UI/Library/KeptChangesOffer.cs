using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;

namespace AetherFrame.UI.Library;

/// <summary>How the offer of one kept draft reads: its words and its buttons.</summary>
internal enum KeptChangesVariant
{
    /// <summary>The Plate is saved as the changes started from: Restore, Discard or Decide Later.</summary>
    Restore,

    /// <summary>The Plate was saved again since: Restore as New Plate instead.</summary>
    SavedAgain,

    /// <summary>The Plate is a newer version's or damaged, so this version can't open it: Restore as New Plate instead.</summary>
    CannotOpen,

    /// <summary>The Plate was deleted: Restore as New Plate instead.</summary>
    Deleted,

    /// <summary>The Plate couldn't be opened this session: Restore as New Plate or Decide Later.</summary>
    Unavailable,
}

/// <summary>
/// The offer of an editor's unsaved changes AetherFrame kept when it last unloaded (see
/// <see cref="UnsavedChangesKeeper"/> and <see cref="KeptChangesReview"/>): what the "Unsaved
/// changes kept" window says and does, free of Dalamud so all of it is tested. The window draws it.
///
/// <para>One draft at a time, newest first, with "1 of N". Each can be restored (over its Plate
/// when that is still saved as the changes started from, otherwise as a new Plate), discarded
/// (moved to AetherFrame's Trash folder) or left for later. Closing without answering leaves them
/// all kept: My Plates says so, with Review to ask again, and the next load asks again too. There
/// is no limit on asking: they are the player's work.</para>
///
/// <para>Whatever acts on a draft claims it first (<see cref="DraftStore.Claim"/>), so a draft
/// another game window took is reported, never acted on twice. Restoring never saves: the editor
/// holds the changes as unsaved, one Undo away from the saved Plate, and the Plate's file is
/// untouched until the player chooses Save. Replacing an open Plate's own unsaved changes asks
/// first, through the same Save, Discard or Cancel question My Plates asks (<see cref="PlateOpenGuard"/>).
/// Unlike My Plates' prompt it isn't modal, so the editors and My Plates stay usable while it waits:
/// it is about the one open document it asked about, and once that isn't open with unsaved changes
/// any more (another Plate opened, or its changes saved or undone), it is dropped, never answered.
/// Its Discard throws those changes away only once the draft is checked again and claimed, so a
/// draft that can't be acted on costs the open Plate nothing. Its Cancel while its Save is written
/// lets the save finish and restores nothing. My Plates' Review meanwhile keeps the draft it waits
/// on (or whose new Plate is being made) the one on offer.</para>
///
/// <para>Used on the framework thread only: the load hands its drafts over there, and the window and
/// <see cref="Advance"/> run while drawing.</para>
/// </summary>
internal sealed class KeptChangesOffer
{
    internal const string Title = "Unsaved changes kept";
    internal const string Consequence = "Your saved Plate is unchanged.";
    internal const string RestoreLabel = "Resume Editing";
    internal const string RestoreTooltip = "Open the Plate with these changes. Nothing is saved or shared until you choose Save.";
    internal const string RestoreAsNewLabel = "Recover as New Plate";
    internal const string RestoreAsNewTooltip = "The saved Plate stays as it is.";
    internal const string RecoverAsNewSecondaryTooltip = "Add these changes to My Plates as a new Plate. The saved Plate stays as it is.";
    internal const string CheckpointLabel = "Recovery point";
    internal const string CheckpointTooltip = "AetherFrame keeps the last few recovery checkpoints of this editing. Choose an earlier one to recover that instead.";
    internal const string RestoreAsNewDeletedTooltip = "Add these changes to My Plates as a new Plate.";
    internal const string DiscardLabel = "Discard";
    internal const string DiscardTooltip = "Keep the saved Plate as it is. The kept changes move to AetherFrame's Trash folder.";
    internal const string DiscardDeletedTooltip = "The kept changes move to AetherFrame's Trash folder.";
    internal const string DecideLaterLabel = "Decide Later";
    internal const string DecideLaterTooltip = "Keep them. My Plates reminds you, and AetherFrame asks again next time.";
    internal const string DeletedNote = "The Plate these changes belong to was deleted.";
    internal const string UnavailableNote = "The Plate couldn't be opened just now; restarting the game usually fixes that.";
    internal const string AlreadyHandledMessage = "These changes were already handled in another game window.";
    internal const string ClaimFailedMessage = "These changes couldn't be moved to AetherFrame's Trash folder, so nothing was done. They're still kept.";
    internal const string ChangedMeanwhileMessage = "The Plate changed meanwhile, so check the choices again.";
    internal const string OpenPlateChangedMessage = "The open Plate changed, so choose again.";
    internal const string BusyMessage = "The open Plate is being saved. Try again once it's saved.";
    internal const string ReviewLabel = "Review";
    internal const string ReviewTooltip = "Show the kept changes again.";

    private static readonly string OpenQuote = ((char)0x201C).ToString();
    private static readonly string CloseQuote = ((char)0x201D).ToString();

    private readonly DraftStore files;
    private readonly PlateLibraryService library;
    private readonly ProfileService profiles;
    private readonly EditorSession session;
    private readonly Action<EditorSurfaceKind> showEditor;
    private readonly IAetherFrameLog log;
    private readonly PlateOpenGuard guard;
    private readonly RecoveryCheckpointStore? checkpoints;

    private readonly List<Entry> entries = new();
    private List<Entry> round = new();
    private bool presented;
    private bool openRequested;

    // The window was closed (and not opened again since): what a new Plate's making comes to is shown again.
    private bool windowClosed;

    // The answer waiting on the unsaved-changes question, and a new Plate being made.
    private Asked? asking;
    private Task<PlateCreationResult>? creating;
    private Entry? creatingEntry;

    // What the window and My Plates show, rebuilt only when something changes (they draw every frame).
    private int version;
    private int viewVersion = -1;
    private ViewText? view;
    private int reminderVersion = -1;
    private string? reminder;

    // The question, rebuilt only when it is asked again or the open Plate's name changes.
    private Asked? questionAsked;
    private string? questionName;
    private string? questionText;

    /// <param name="files">Where the drafts are, for claiming them.</param>
    /// <param name="showEditor">Shows the open Plate in the Basic or Advanced editor.</param>
    /// <param name="checkpoints">Where recovery checkpoints are, for removing an answered editing's older ones; null where there are none.</param>
    internal KeptChangesOffer(
        DraftStore files, PlateLibraryService library, ProfileService profiles, EditorSession session, Action<EditorSurfaceKind> showEditor, IAetherFrameLog log,
        RecoveryCheckpointStore? checkpoints = null)
    {
        this.checkpoints = checkpoints;
        this.files = files;
        this.library = library;
        this.profiles = profiles;
        this.session = session;
        this.showEditor = showEditor;
        this.log = log;
        guard = new PlateOpenGuard(profiles, session);
    }

    private enum EntryState
    {
        Pending,
        Acting,
        Deferred,
        Done,
    }

    /// <summary>The last failure or refusal, shown in the error colour.</summary>
    internal string? Error { get; private set; }

    /// <summary>The last result worth saying (another game window had the changes, a new Plate made), shown quietly.</summary>
    internal string? Notice { get; private set; }

    /// <summary>A new Plate is being made from kept changes.</summary>
    internal bool IsBusy => creating is not null;

    /// <summary>A draft is on offer (or being acted on): the window shows it.</summary>
    internal bool HasCurrent => Current is not null;

    internal KeptChangesVariant? CurrentVariant => Current is { } entry ? VariantOf(entry.Choice) : null;

    internal Guid? CurrentPlateId => Current?.PlateId;

    /// <summary>Which of this round's drafts is on offer (1-based), and how many the round has.</summary>
    internal int Position => Current is { } entry ? round.IndexOf(entry) + 1 : 0;

    internal int Count => round.Count;

    /// <summary>"1 of 3" while a round offers more than one draft; null otherwise.</summary>
    internal string? PositionText => View()?.Position;

    internal string Body => View()?.Body ?? string.Empty;

    /// <summary>What the variant adds (saved again, this version can't open it, deleted, couldn't be opened just now), or null.</summary>
    internal string? VariantNote => View()?.Note;

    /// <summary>The muted consequence line, or null where there is no saved Plate to speak of.</summary>
    internal string? ConsequenceLine => View()?.Consequence;

    internal string PrimaryLabel => View()?.PrimaryLabel ?? RestoreLabel;

    internal string PrimaryTooltip => View()?.PrimaryTooltip ?? RestoreTooltip;

    /// <summary>Discard is offered for every variant but a Plate that couldn't be opened.</summary>
    internal bool OffersDiscard => View()?.OffersDiscard ?? false;

    /// <summary>Discard's tooltip, or null where Discard isn't offered.</summary>
    internal string? DiscardTooltipText => View()?.DiscardTooltip;

    /// <summary>
    /// The unsaved-changes question to answer before acting, naming the open Plate as it is now, or
    /// null when none is asked (or the document it asked about isn't open any more: <see cref="Advance"/>
    /// then drops it).
    /// </summary>
    internal string? Question
    {
        get
        {
            if (asking is not { } waiting || guard.Pending is null || !ReferenceEquals(profiles.CurrentProfile, waiting.Document))
            {
                return null;
            }

            var name = waiting.Document.Name is { Length: > 0 } open ? open : PlateNaming.DefaultName;
            if (!ReferenceEquals(waiting, questionAsked) || !ReferenceEquals(name, questionName))
            {
                questionAsked = waiting;
                questionName = name;
                questionText = waiting.RestoreHere && waiting.PlateId == waiting.Entry.PlateId ? SamePlateQuestion(name) : OtherPlateQuestion(name);
            }

            return questionText;
        }
    }

    /// <summary>Save and Discard are unavailable while a save is being written.</summary>
    internal bool CanAnswerQuestion => guard.CanAnswer;

    /// <summary>The question's Save is being written.</summary>
    internal bool IsSavingForQuestion => guard.IsSaving;

    /// <summary>
    /// My Plates' reminder while kept changes wait for a later answer: "Unsaved changes were kept for
    /// N Plates." Null otherwise. It counts the drafts left for later (Decide Later, or the window
    /// closed), whatever else is on offer or being acted on meanwhile.
    /// </summary>
    internal string? ReminderText
    {
        get
        {
            if (reminderVersion != version)
            {
                reminderVersion = version;
                var count = entries.Where(e => e.State == EntryState.Deferred).Select(e => e.PlateId).Distinct().Count();
                reminder = count == 0 ? null : $"Unsaved changes were kept for {count} {(count == 1 ? "Plate" : "Plates")}.";
            }

            return reminder;
        }
    }

    private Entry? Current => round.FirstOrDefault(e => e.State is EntryState.Pending or EntryState.Acting);

    /// <summary>
    /// What a load found to offer (see <see cref="KeptChangesReview.LoadAsync"/>). Shown at once when a
    /// character is logged in, otherwise at the next login (<see cref="OnLogin"/>).
    /// </summary>
    internal void Present(IReadOnlyList<KeptDraft> drafts, bool loggedIn)
    {
        entries.Clear();
        entries.AddRange(drafts.Where(d => d.Choice != KeptChangesChoice.Identical).Select(d => new Entry(d)));
        round = new List<Entry>();
        presented = false;
        Changed();
        if (loggedIn)
        {
            RequestOpen();
        }
    }

    /// <summary>A character logged in: the offer shows now if it was waiting for one.</summary>
    internal void OnLogin()
    {
        if (!presented && entries.Any(e => e.State == EntryState.Pending))
        {
            RequestOpen();
        }
    }

    /// <summary>For the window: true once each time the offer should open, with a draft on offer or a message to read.</summary>
    internal bool ConsumeOpenRequest()
    {
        if (!openRequested)
        {
            return false;
        }

        openRequested = false;
        return Current is not null || Error is not null || Notice is not null;
    }

    /// <summary>
    /// My Plates' Review: offers every draft left for later again. A draft being acted on (its
    /// question waiting, or its new Plate being made) stays the one on offer (see <see cref="RequestOpen"/>).
    /// </summary>
    internal void Review()
    {
        foreach (var entry in entries.Where(e => e.State == EntryState.Deferred))
        {
            entry.State = EntryState.Pending;
        }

        RequestOpen();
    }

    /// <summary>
    /// The primary choice: Restore over the Plate, or Restore as New Plate. Asks first when the open
    /// Plate has unsaved changes of its own (<see cref="Question"/>), then claims the draft and acts.
    /// </summary>
    internal void Choose() => Choose(asNewPlate: false);

    /// <summary>Recover as New Plate where Resume Editing is the primary choice: the saved Plate stays as it is.</summary>
    internal void ChooseNewPlate()
    {
        if (OffersNewPlateToo)
        {
            Choose(asNewPlate: true);
        }
    }

    /// <summary>Whether Recover as New Plate is offered beside Resume Editing.</summary>
    internal bool OffersNewPlateToo => CurrentVariant == KeptChangesVariant.Restore;

    /// <summary>
    /// The recovery points of the draft on offer, newest first, for the window's list: null when
    /// there is only one. The newest is chosen unless the player picks an older one.
    /// </summary>
    internal IReadOnlyList<string>? CheckpointOptions => View()?.Checkpoints;

    /// <summary>Which of <see cref="CheckpointOptions"/> an answer acts on.</summary>
    internal int SelectedCheckpoint => Current?.Selected ?? 0;

    /// <summary>Chooses which recovery point an answer acts on; not while one is being acted on.</summary>
    internal void SelectCheckpoint(int index)
    {
        if (Current is not { State: EntryState.Pending } entry || asking is not null || IsBusy || index < 0 || index >= entry.Points.Count || index == entry.Selected)
        {
            return;
        }

        ClearMessages();
        entry.Selected = index;
        Changed();
    }

    private void Choose(bool asNewPlate)
    {
        if (Current is not { } entry || IsBusy || asking is not null)
        {
            return;
        }

        ClearMessages();
        if (!Recheck(entry))
        {
            return;
        }

        var restoreHere = entry.Choice == KeptChangesChoice.Restore && !asNewPlate;
        var basic = EditorFor(entry.Point) == EditorSurfaceKind.Basic;
        if (guard.Request(entry.PlateId, basic, askEvenIfOpen: true) == PlateOpenDecision.Ask)
        {
            // What the question is about: the document open now (it asks only when one is), with its
            // unsaved changes. Once that isn't so, the question is dropped (see DropStaleQuestion).
            var open = profiles.CurrentProfile!;
            asking = new Asked(entry, restoreHere, open, open.ProfileId);
            Changed();
            return;
        }

        Proceed(entry, restoreHere);
    }

    /// <summary>Discard: the saved Plate stays as it is, and the draft moves to the trash.</summary>
    internal void Discard()
    {
        if (Current is not { } entry || IsBusy || asking is not null || !OffersDiscard)
        {
            return;
        }

        ClearMessages();
        if (TryClaim(entry))
        {
            Answered(entry);
            log.Information($"AetherFrame discarded kept unsaved changes of Plate {entry.PlateId}; they are in its Trash folder.");
        }
    }

    /// <summary>Decide Later: this draft stays kept, and the next one is offered.</summary>
    internal void DecideLater()
    {
        if (Current is not { State: EntryState.Pending } entry || asking is not null)
        {
            return;
        }

        ClearMessages();
        entry.State = EntryState.Deferred;
        Changed();
    }

    /// <summary>
    /// The question's Save: saves the open Plate; once that succeeds, the answer goes ahead (see
    /// <see cref="Advance"/>). Nothing is saved when the question is about a document that isn't open
    /// with unsaved changes any more: it is dropped instead.
    /// </summary>
    internal void AnswerSave()
    {
        if (DropStaleQuestion())
        {
            return;
        }

        guard.Save();
    }

    /// <summary>
    /// The question's Discard: secures the draft, then drops the open Plate's own unsaved changes,
    /// and the answer goes ahead. Dropping them is a revert with no undo, so the draft is checked
    /// again and claimed first: when it can't be acted on (its Plate changed meanwhile, another game
    /// window took it, or it can't be moved), the question goes, nothing is discarded, and the open
    /// Plate keeps its changes, undoable as before. Nothing is discarded either when the question is
    /// about a document that isn't open with unsaved changes any more: it is dropped instead.
    /// </summary>
    internal void AnswerDiscard()
    {
        if (asking is not { } waiting || DropStaleQuestion())
        {
            return;
        }

        var entry = waiting.Entry;
        if (!Recheck(entry))
        {
            EndQuestion();
            return;
        }

        // Discarding waits for any save, and claiming first would only put the draft back.
        if (!guard.CanAnswer)
        {
            Error = BusyMessage;
            Changed();
            return;
        }

        if (!TryClaim(entry))
        {
            EndQuestion();
            return;
        }

        if (guard.Discard() is null)
        {
            EndQuestion();
            PutBack(entry, session.ErrorMessage ?? EditorSession.EditFailedMessage);
            return;
        }

        // Claimed already: claiming again would find it gone, and read as another game window's.
        asking = null;
        if (waiting.RestoreHere)
        {
            RestoreClaimed(entry);
        }
        else
        {
            RestoreAsNewClaimed(entry);
        }
    }

    /// <summary>
    /// The question's Cancel: nothing is restored, and the draft is still on offer. While the
    /// question's Save is being written, that save goes on, and the Plate ends saved; once it lands,
    /// the question is only cleared (see <see cref="Advance"/>).
    /// </summary>
    internal void AnswerCancel()
    {
        if (guard.IsSaving)
        {
            if (asking is { } waiting)
            {
                waiting.Cancelled = true;
            }

            return;
        }

        EndQuestion();
    }

    /// <summary>
    /// The window closed without an answer: every draft still on offer stays kept, for later. A new
    /// Plate still being made carries on, and if it can't be made, or ends with something to say,
    /// the window opens again to say so (see <see cref="Advance"/>).
    /// </summary>
    internal void Closed()
    {
        asking = null;
        guard.Cancel();
        windowClosed = true;
        foreach (var entry in round.Where(e => e.State == EntryState.Pending))
        {
            entry.State = EntryState.Deferred;
        }

        ClearMessages();
        Changed();
    }

    /// <summary>For the window: the message shown once nothing is on offer has been read.</summary>
    internal void DismissMessages()
    {
        ClearMessages();
        Changed();
    }

    /// <summary>
    /// Applies what finished since the last frame: a new Plate made from kept changes, and the save
    /// the question's Save started (which restores nothing when the question was cancelled while it
    /// was written). Drops a question whose document isn't open with unsaved changes any more (see
    /// <see cref="DropStaleQuestion"/>). Call once per frame, on the framework thread, before the
    /// window draws.
    /// </summary>
    internal void Advance()
    {
        if (creating is { IsCompleted: true } made && creatingEntry is { } entry)
        {
            creating = null;
            creatingEntry = null;
            if (made.IsCompletedSuccessfully)
            {
                OpenNewPlate(entry, made.Result.PlateId);
            }
            else
            {
                PutBack(entry, made.Exception?.GetBaseException() is PlateLibraryException refused
                    ? refused.Message
                    : "The new Plate couldn't be made. See the Dalamud log for details.");
            }

            // The window was closed while it was made: what it came to is shown, never left unseen.
            if (windowClosed && (Error is not null || Notice is not null))
            {
                windowClosed = false;
                openRequested = true;
            }
        }

        DropStaleQuestion();
        if (guard.Advance() is not { } outcome)
        {
            return;
        }

        // A question the window was closed on is only cleared, never acted on.
        if (asking is not { } waiting || waiting.Entry.State != EntryState.Pending)
        {
            asking = null;
            return;
        }

        asking = null;
        if (outcome.Open is null)
        {
            Error = outcome.Error;
            Changed();
            return;
        }

        // Cancelled while its save was written: the Plate is saved, and nothing is restored.
        if (waiting.Cancelled)
        {
            return;
        }

        // Saved: the document asked about is open and clean. Anything else since (another Plate
        // opened, or edited again) is never replaced.
        if (!ReferenceEquals(profiles.CurrentProfile, waiting.Document) || session.IsDirty)
        {
            Error = OpenPlateChangedMessage;
            Changed();
            return;
        }

        Proceed(waiting.Entry, waiting.RestoreHere);
    }

    /// <summary>The opening sentence: which Plate, which editor, when, and where the changes are.</summary>
    internal static string BodyText(string plateName, DraftEditor editor, DateTime writtenUtc)
    {
        var when = writtenUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var where = editor switch
        {
            DraftEditor.Basic => "Basic editor, " + when,
            DraftEditor.Advanced => "Advanced editor, " + when,
            _ => when,
        };
        return $"AetherFrame closed while {Quoted(plateName)} had unsaved changes ({where}). They were kept beside your Plates.";
    }

    /// <summary>The opening sentence for a recovery checkpoint: which Plate, which editor, and the checkpoint's time.</summary>
    internal static string CheckpointBodyText(string plateName, DraftEditor editor, DateTime writtenUtc)
    {
        var when = writtenUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
        var where = editor switch
        {
            DraftEditor.Basic => "Basic editor, recovery checkpoint " + when,
            DraftEditor.Advanced => "Advanced editor, recovery checkpoint " + when,
            _ => "recovery checkpoint " + when,
        };
        return $"{Quoted(plateName)} had unsaved changes when AetherFrame last stopped ({where}). They were kept beside your Plates.";
    }

    /// <summary>One recovery point in the window's list: its time, and what it is.</summary>
    internal static string CheckpointOption(KeptDraft point, bool newest)
    {
        var when = point.Draft.WrittenAtUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
        var what = point.IsCheckpoint ? "checkpoint" : "kept when AetherFrame closed";
        return newest ? $"{when} ({what}, newest)" : $"{when} ({what})";
    }

    /// <summary>The saved-again variant's sentence.</summary>
    internal static string SavedAgainNote(string plateName) =>
        $"{Quoted(plateName)} was saved again after these changes were made, so restoring them over it would undo that save.";

    /// <summary>The sentence for a Plate that is a newer version's, or damaged.</summary>
    internal static string CannotOpenNote(string plateName) =>
        $"{Quoted(plateName)} can't be opened by this version of AetherFrame, so these changes can only be restored as a new Plate.";

    /// <summary>
    /// The question when the Plate the changes belong to is open with unsaved changes of its own:
    /// only Discard lets these be restored over it, since saving it makes it a later version.
    /// </summary>
    internal static string SamePlateQuestion(string plateName) =>
        $"{Quoted(plateName)} is open with unsaved changes of its own. Discard them to restore these over it, or save them, and these can then be restored as a new Plate.";

    /// <summary>The question when another Plate is open with unsaved changes.</summary>
    internal static string OtherPlateQuestion(string plateName) =>
        $"{Quoted(plateName)} has unsaved changes. Save them before opening these?";

    internal static string Quoted(string text) => OpenQuote + text + CloseQuote;

    private void Proceed(Entry entry, bool restoreHere)
    {
        if (restoreHere)
        {
            Restore(entry);
        }
        else
        {
            RestoreAsNew(entry);
        }
    }

    /// <summary>Restore over the Plate: checked again and claimed first, then <see cref="RestoreClaimed"/>.</summary>
    private void Restore(Entry entry)
    {
        // A save chosen at the question bumps the Plate's revision: restoring over it would undo that.
        if (!Recheck(entry))
        {
            return;
        }

        if (profiles.IsBusy)
        {
            Error = BusyMessage;
            Changed();
            return;
        }

        if (TryClaim(entry))
        {
            RestoreClaimed(entry);
        }
    }

    /// <summary>
    /// Restore over the Plate, its draft claimed: the Plate opens (or stays open), its saved state
    /// becomes the editor's baseline, and the kept changes go in as one undoable edit, so the editor
    /// reads as unsaved and nothing is written. The Library's name and the saved file's unknown data
    /// stay as they are. If it can't be done, the draft is put back and stays on offer.
    /// </summary>
    private void RestoreClaimed(Entry entry)
    {
        try
        {
            profiles.OpenPlate(entry.PlateId);

            // Before the changes go in: a Plate just opened takes its saved state as the baseline and
            // starts its history afresh. Afterwards would make the changes the baseline, read as saved.
            session.SyncWithCurrentProfile();
            if (!session.ApplyRecoveredState(entry.Point.State))
            {
                throw new InvalidOperationException(session.ErrorMessage ?? EditorSession.EditFailedMessage);
            }
        }
        catch (Exception ex) when (ex is PlateLibraryException or InvalidOperationException)
        {
            PutBack(entry, UserFacingError.Describe(ex, "The Plate couldn't be opened."));
            return;
        }

        Answered(entry);
        log.Information($"AetherFrame restored kept unsaved changes into Plate {entry.PlateId}; nothing is saved until the player saves.");
        showEditor(EditorFor(entry.Point));
    }

    /// <summary>Restore as New Plate: checked again and claimed first, then <see cref="RestoreAsNewClaimed"/>.</summary>
    private void RestoreAsNew(Entry entry)
    {
        if (Recheck(entry) && TryClaim(entry))
        {
            RestoreAsNewClaimed(entry);
        }
    }

    /// <summary>Restore as New Plate, its draft claimed: made off this thread; <see cref="Advance"/> opens it.</summary>
    private void RestoreAsNewClaimed(Entry entry)
    {
        entry.State = EntryState.Acting;
        creatingEntry = entry;
        creating = CreateAsync(entry.Point);
        Changed();
    }

    private async Task<PlateCreationResult> CreateAsync(KeptDraft kept)
    {
        try
        {
            return await library.CreatePlateFromKeptChangesAsync(kept.DocumentRaw(), kept.Draft.PlateName).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PlateLibraryException)
        {
            log.Error(ex, $"AetherFrame couldn't restore kept unsaved changes of Plate {kept.PlateId} as a new Plate.");
            throw;
        }
    }

    /// <summary>
    /// The new Plate opens, clean, in the editor the changes were made in, unless the open Plate was
    /// edited while it was made: then it stays open, and the new Plate waits in My Plates.
    /// </summary>
    private void OpenNewPlate(Entry entry, Guid plateId)
    {
        Answered(entry);
        var name = library.FindPlate(plateId)?.DisplayName ?? PlateNaming.DefaultName;
        if (profiles.OpenPlateId != plateId && profiles.CurrentProfile is not null && session.IsDirty)
        {
            Notice = $"Restored as {Quoted(name)} in My Plates.";
            return;
        }

        try
        {
            profiles.OpenPlate(plateId);
            session.SyncWithCurrentProfile();
        }
        catch (Exception ex) when (ex is PlateLibraryException or InvalidOperationException)
        {
            Notice = $"Restored as {Quoted(name)} in My Plates, but it couldn't be opened here.";
            return;
        }

        showEditor(EditorFor(entry.Point));
    }

    /// <summary>
    /// The editing is answered (restored, recovered as a new Plate or discarded), its token in the
    /// trash: its other checkpoint files go too, so nothing of it is offered again.
    /// </summary>
    private void Answered(Entry entry)
    {
        entry.State = EntryState.Done;
        Changed();
        KeptChangesReview.RemoveFiles(checkpoints, entry.Kept.OtherFiles, log);
    }

    /// <summary>Claims the draft (see <see cref="DraftStore.Claim"/>); says so when it can't.</summary>
    private bool TryClaim(Entry entry)
    {
        switch (files.Claim(entry.Kept.Path, out var trashPath))
        {
            case DraftClaim.Claimed:
                entry.TrashPath = trashPath;
                return true;

            case DraftClaim.AlreadyHandled:
                entry.State = EntryState.Done;
                Notice = AlreadyHandledMessage;
                log.Information($"AetherFrame left kept unsaved changes of Plate {entry.PlateId} alone: another game window already took them.");
                Changed();
                return false;

            default:
                Error = ClaimFailedMessage;
                Changed();
                return false;
        }
    }

    /// <summary>A claimed draft whose restore failed before changing anything: back where it was, and on offer again.</summary>
    private void PutBack(Entry entry, string reason)
    {
        if (entry.TrashPath is { } trashPath && files.Unclaim(entry.Kept.Path, trashPath))
        {
            entry.TrashPath = null;
            entry.State = EntryState.Pending;
            Error = reason + " The changes are still kept.";
        }
        else
        {
            entry.State = EntryState.Done;
            Error = reason + " The changes are in AetherFrame's Trash folder.";
        }

        Changed();
    }

    /// <summary>
    /// The draft's choice again, just before acting: true when it still holds; otherwise the offer
    /// changes to what the Library allows now, says so, and nothing is done.
    /// </summary>
    private bool Recheck(Entry entry)
    {
        var now = KeptChangesReview.Recheck(entry.Choice, entry.Point.Draft, library);
        if (now == entry.Choice)
        {
            return true;
        }

        entry.Choice = now;
        Error = ChangedMeanwhileMessage;
        Changed();
        return false;
    }

    /// <summary>
    /// The question waits on one document's unsaved changes (see <see cref="Choose"/>). The window
    /// isn't modal, so meanwhile another Plate can be opened and edited, or those changes saved or
    /// undone: its Save would then save, and its Discard throw away, what it never asked about. So
    /// once the document isn't open, or has no unsaved changes, the question is dropped, nothing is
    /// saved or discarded, and the draft is still on offer. True when it was dropped. A save the
    /// question's own Save is writing is left to finish (see <see cref="Advance"/>).
    /// </summary>
    private bool DropStaleQuestion()
    {
        if (asking is not { } waiting
            || guard.IsSaving
            || (ReferenceEquals(profiles.CurrentProfile, waiting.Document) && profiles.OpenPlateId == waiting.PlateId && session.IsDirty))
        {
            return false;
        }

        Error = OpenPlateChangedMessage;
        EndQuestion();
        return true;
    }

    /// <summary>The question goes unanswered: nothing is saved or discarded, and the open Plate keeps its changes.</summary>
    private void EndQuestion()
    {
        guard.Cancel();
        asking = null;
        Changed();
    }

    /// <summary>
    /// Opens the window on a new round: every draft on offer or being acted on, newest first, except
    /// that one being acted on here (its question waiting, or its new Plate being made) comes first
    /// and stays the one on offer, so the window shows the draft its answer acts on.
    /// </summary>
    private void RequestOpen()
    {
        presented = true;
        openRequested = true;
        windowClosed = false;
        var held = asking?.Entry ?? creatingEntry;
        round = entries
            .Where(e => e.State is EntryState.Pending or EntryState.Acting)
            .OrderBy(e => ReferenceEquals(e, held) ? 0 : 1)
            .ToList();
        ClearMessages();
        Changed();
    }

    private void ClearMessages()
    {
        Error = null;
        Notice = null;
    }

    private void Changed() => version++;

    private static EditorSurfaceKind EditorFor(KeptDraft kept) => kept.Draft.Editor switch
    {
        DraftEditor.Basic => EditorSurfaceKind.Basic,
        DraftEditor.Advanced => EditorSurfaceKind.Advanced,
        _ => EditorSurfaceChooser.ForDocument(kept.Draft.Document),
    };

    private static KeptChangesVariant VariantOf(KeptChangesChoice choice) => choice switch
    {
        KeptChangesChoice.Restore => KeptChangesVariant.Restore,
        KeptChangesChoice.CannotOpen => KeptChangesVariant.CannotOpen,
        KeptChangesChoice.Deleted => KeptChangesVariant.Deleted,
        KeptChangesChoice.Unavailable => KeptChangesVariant.Unavailable,
        _ => KeptChangesVariant.SavedAgain,
    };

    /// <summary>The current draft's words, built once each time it or its choice changes.</summary>
    private ViewText? View()
    {
        if (viewVersion == version)
        {
            return view;
        }

        viewVersion = version;
        if (Current is not { } entry)
        {
            view = null;
            return null;
        }

        var name = DisplayName(entry);
        var variant = VariantOf(entry.Choice);
        view = new ViewText(
            Body: entry.Point.IsCheckpoint
                ? CheckpointBodyText(name, entry.Point.Draft.Editor, entry.Point.Draft.WrittenAtUtc)
                : BodyText(name, entry.Point.Draft.Editor, entry.Point.Draft.WrittenAtUtc),
            Note: variant switch
            {
                KeptChangesVariant.SavedAgain => SavedAgainNote(name),
                KeptChangesVariant.CannotOpen => CannotOpenNote(name),
                KeptChangesVariant.Deleted => DeletedNote,
                KeptChangesVariant.Unavailable => UnavailableNote,
                _ => null,
            },
            Consequence: variant == KeptChangesVariant.Deleted ? null : Consequence,
            PrimaryLabel: variant == KeptChangesVariant.Restore ? RestoreLabel : RestoreAsNewLabel,
            PrimaryTooltip: variant switch
            {
                KeptChangesVariant.Restore => RestoreTooltip,
                KeptChangesVariant.Deleted => RestoreAsNewDeletedTooltip,
                _ => RestoreAsNewTooltip,
            },
            OffersDiscard: variant != KeptChangesVariant.Unavailable,
            DiscardTooltip: variant switch
            {
                KeptChangesVariant.Unavailable => null,
                KeptChangesVariant.Deleted => DiscardDeletedTooltip,
                _ => DiscardTooltip,
            },
            Position: round.Count > 1 ? $"{round.IndexOf(entry) + 1} of {round.Count}" : null,
            Checkpoints: entry.Points.Count > 1 ? entry.Points.Select((p, i) => CheckpointOption(p, i == 0)).ToList() : null);
        return view;
    }

    /// <summary>The Library's name for a Plate it can show, else the name kept with the changes.</summary>
    private string DisplayName(Entry entry)
    {
        if (library.FindPlate(entry.PlateId) is { IsReady: true } plate)
        {
            return plate.DisplayName;
        }

        return string.IsNullOrWhiteSpace(entry.Point.Draft.PlateName) ? PlateNaming.DefaultName : entry.Point.Draft.PlateName;
    }

    private sealed class Entry(KeptDraft kept)
    {
        private readonly KeptChangesChoice[] choices = [kept.Choice, .. kept.Older.Select(o => o.Choice)];

        /// <summary>The editing's newest point: its token, the file every answer claims first.</summary>
        internal KeptDraft Kept { get; } = kept;

        /// <summary>Every point on offer, newest first: the token, then the older checkpoints.</summary>
        internal IReadOnlyList<KeptDraft> Points { get; } = [kept, .. kept.Older];

        internal int Selected { get; set; }

        /// <summary>The point an answer acts on: the newest unless an older one was chosen.</summary>
        internal KeptDraft Point => Points[Selected];

        internal KeptChangesChoice Choice
        {
            get => choices[Selected];
            set => choices[Selected] = value;
        }

        internal EntryState State { get; set; } = EntryState.Pending;

        /// <summary>Where its claim moved the draft, while claimed (see <see cref="DraftStore.Claim(string, out string?)"/>).</summary>
        internal string? TrashPath { get; set; }

        internal Guid PlateId => Kept.PlateId;
    }

    /// <summary>
    /// The unsaved-changes question waiting for an answer: the draft and how it is to be restored,
    /// and what it is about, the open document then (the instance itself) and its Plate.
    /// </summary>
    private sealed class Asked(Entry entry, bool restoreHere, ProfileDocument document, Guid plateId)
    {
        internal Entry Entry { get; } = entry;

        internal bool RestoreHere { get; } = restoreHere;

        internal ProfileDocument Document { get; } = document;

        internal Guid PlateId { get; } = plateId;

        /// <summary>Cancel was chosen while its Save was being written: once that lands, nothing is restored.</summary>
        internal bool Cancelled { get; set; }
    }

    private sealed record ViewText(
        string Body,
        string? Note,
        string? Consequence,
        string PrimaryLabel,
        string PrimaryTooltip,
        bool OffersDiscard,
        string? DiscardTooltip,
        string? Position,
        IReadOnlyList<string>? Checkpoints);
}
