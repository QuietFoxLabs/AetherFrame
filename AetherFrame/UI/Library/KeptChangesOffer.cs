using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
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

    /// <summary>The Plate was saved again since, or can't be written by this build: Restore as New Plate instead.</summary>
    SavedAgain,

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
/// first, through the same Save, Discard or Cancel question My Plates asks (<see cref="PlateOpenGuard"/>).</para>
///
/// <para>Used on the framework thread only: the load hands its drafts over there, and the window and
/// <see cref="Advance"/> run while drawing.</para>
/// </summary>
internal sealed class KeptChangesOffer
{
    internal const string Title = "Unsaved changes kept";
    internal const string Consequence = "Your saved Plate is unchanged.";
    internal const string RestoreLabel = "Restore";
    internal const string RestoreTooltip = "Open the Plate with these changes. Nothing is saved until you choose Save.";
    internal const string RestoreAsNewLabel = "Restore as New Plate";
    internal const string RestoreAsNewTooltip = "The saved Plate stays as it is.";
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

    private readonly List<Entry> entries = new();
    private List<Entry> round = new();
    private bool presented;
    private bool openRequested;

    // The answer waiting on the unsaved-changes question, and a new Plate being made.
    private (Entry Entry, bool RestoreHere)? asking;
    private Task<PlateCreationResult>? creating;
    private Entry? creatingEntry;

    // What the window and My Plates show, rebuilt only when something changes (they draw every frame).
    private int version;
    private int viewVersion = -1;
    private ViewText? view;
    private int reminderVersion = -1;
    private string? reminder;

    /// <param name="files">Where the drafts are, for claiming them.</param>
    /// <param name="showEditor">Shows the open Plate in the Basic or Advanced editor.</param>
    internal KeptChangesOffer(
        DraftStore files, PlateLibraryService library, ProfileService profiles, EditorSession session, Action<EditorSurfaceKind> showEditor, IAetherFrameLog log)
    {
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

    /// <summary>What the variant adds (saved again, deleted, couldn't be opened), or null.</summary>
    internal string? VariantNote => View()?.Note;

    /// <summary>The muted consequence line, or null where there is no saved Plate to speak of.</summary>
    internal string? ConsequenceLine => View()?.Consequence;

    internal string PrimaryLabel => View()?.PrimaryLabel ?? RestoreLabel;

    internal string PrimaryTooltip => View()?.PrimaryTooltip ?? RestoreTooltip;

    /// <summary>Discard is offered for every variant but a Plate that couldn't be opened.</summary>
    internal bool OffersDiscard => View()?.OffersDiscard ?? false;

    internal string DiscardTooltipText => View()?.DiscardTooltip ?? DiscardTooltip;

    /// <summary>The unsaved-changes question to answer before acting, or null when none is asked.</summary>
    internal string? Question => asking is { } waiting && guard.Pending is not null ? View()?.Question(waiting.RestoreHere) : null;

    /// <summary>Save and Discard are unavailable while a save is being written.</summary>
    internal bool CanAnswerQuestion => guard.CanAnswer;

    /// <summary>The question's Save is being written.</summary>
    internal bool IsSavingForQuestion => guard.IsSaving;

    /// <summary>
    /// My Plates' reminder while kept changes wait for a later answer and the offer isn't showing:
    /// "Unsaved changes were kept for N Plates." Null otherwise.
    /// </summary>
    internal string? ReminderText
    {
        get
        {
            if (reminderVersion != version)
            {
                reminderVersion = version;
                var count = Current is null ? entries.Where(e => e.State == EntryState.Deferred).Select(e => e.PlateId).Distinct().Count() : 0;
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

    /// <summary>For the window: true once each time the offer should open.</summary>
    internal bool ConsumeOpenRequest()
    {
        if (!openRequested)
        {
            return false;
        }

        openRequested = false;
        return Current is not null;
    }

    /// <summary>My Plates' Review: offers every draft left for later again.</summary>
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
    internal void Choose()
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

        var restoreHere = entry.Choice == KeptChangesChoice.Restore;
        var basic = EditorFor(entry.Kept) == EditorSurfaceKind.Basic;
        if (guard.Request(entry.PlateId, basic, askEvenIfOpen: true) == PlateOpenDecision.Ask)
        {
            asking = (entry, restoreHere);
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
            entry.State = EntryState.Done;
            log.Information($"AetherFrame discarded kept unsaved changes of Plate {entry.PlateId}; they are in its Trash folder.");
            Changed();
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

    /// <summary>The question's Save: saves the open Plate; once that succeeds, the answer goes ahead (see <see cref="Advance"/>).</summary>
    internal void AnswerSave() => guard.Save();

    /// <summary>The question's Discard: drops the open Plate's own unsaved changes, then the answer goes ahead.</summary>
    internal void AnswerDiscard()
    {
        if (asking is not { } waiting)
        {
            return;
        }

        if (guard.Discard() is null)
        {
            Error = session.ErrorMessage;
            Changed();
            return;
        }

        asking = null;
        Proceed(waiting.Entry, waiting.RestoreHere);
    }

    /// <summary>The question's Cancel: nothing happens, and the draft is still on offer.</summary>
    internal void AnswerCancel()
    {
        guard.Cancel();
        if (!guard.IsSaving)
        {
            asking = null;
            Changed();
        }
    }

    /// <summary>The window closed without an answer: every draft still on offer stays kept, for later.</summary>
    internal void Closed()
    {
        asking = null;
        guard.Cancel();
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
    /// the question's Save started. Call once per frame, on the framework thread.
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
        }

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
        if (outcome.Open is not null)
        {
            Proceed(waiting.Entry, waiting.RestoreHere);
        }
        else
        {
            Error = outcome.Error;
            Changed();
        }
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

    /// <summary>The saved-again variant's sentence.</summary>
    internal static string SavedAgainNote(string plateName) =>
        $"{Quoted(plateName)} was saved again after these changes were made, so restoring them over it would undo that save.";

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

    /// <summary>
    /// Restore over the Plate: claimed first, then the Plate opens (or stays open), its saved state
    /// becomes the editor's baseline, and the kept changes go in as one undoable edit, so the editor
    /// reads as unsaved and nothing is written. The Library's name and the saved file's unknown data
    /// stay as they are. If it can't be done, the draft is put back and stays on offer.
    /// </summary>
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

        if (!TryClaim(entry))
        {
            return;
        }

        try
        {
            profiles.OpenPlate(entry.PlateId);

            // Before the changes go in: a Plate just opened takes its saved state as the baseline and
            // starts its history afresh. Afterwards would make the changes the baseline, read as saved.
            session.SyncWithCurrentProfile();
            if (!session.ApplyRecoveredState(entry.Kept.State))
            {
                throw new InvalidOperationException(session.ErrorMessage ?? EditorSession.EditFailedMessage);
            }
        }
        catch (Exception ex) when (ex is PlateLibraryException or InvalidOperationException)
        {
            PutBack(entry, UserFacingError.Describe(ex, "The Plate couldn't be opened."));
            return;
        }

        entry.State = EntryState.Done;
        Changed();
        log.Information($"AetherFrame restored kept unsaved changes into Plate {entry.PlateId}; nothing is saved until the player saves.");
        showEditor(EditorFor(entry.Kept));
    }

    /// <summary>Restore as New Plate: claimed first, then made off this thread; <see cref="Advance"/> opens it.</summary>
    private void RestoreAsNew(Entry entry)
    {
        if (!Recheck(entry) || !TryClaim(entry))
        {
            return;
        }

        entry.State = EntryState.Acting;
        creatingEntry = entry;
        creating = CreateAsync(entry.Kept);
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
        entry.State = EntryState.Done;
        Changed();
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

        showEditor(EditorFor(entry.Kept));
    }

    /// <summary>Claims the draft (see <see cref="DraftStore.Claim"/>); says so when it can't.</summary>
    private bool TryClaim(Entry entry)
    {
        switch (files.Claim(entry.Kept.Path))
        {
            case DraftClaim.Claimed:
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
        if (files.Unclaim(entry.Kept.Path))
        {
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
        var now = KeptChangesReview.Recheck(entry.Choice, entry.Kept.Draft, library);
        if (now == entry.Choice)
        {
            return true;
        }

        entry.Choice = now;
        Error = ChangedMeanwhileMessage;
        Changed();
        return false;
    }

    private void RequestOpen()
    {
        presented = true;
        openRequested = true;
        round = entries.Where(e => e.State == EntryState.Pending).ToList();
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
        var openName = profiles.CurrentProfile?.Name is { Length: > 0 } open ? open : PlateNaming.DefaultName;
        view = new ViewText(
            Body: BodyText(name, entry.Kept.Draft.Editor, entry.Kept.Draft.WrittenAtUtc),
            Note: variant switch
            {
                KeptChangesVariant.SavedAgain => SavedAgainNote(name),
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
            DiscardTooltip: variant == KeptChangesVariant.Deleted ? DiscardDeletedTooltip : DiscardTooltip,
            Position: round.Count > 1 ? $"{round.IndexOf(entry) + 1} of {round.Count}" : null,
            SamePlateQuestion: $"{Quoted(openName)} is open with other unsaved changes. Save or discard them first?",
            OtherPlateQuestion: $"{Quoted(openName)} has unsaved changes. Save them before opening these?",
            OpenPlateId: profiles.OpenPlateId,
            PlateId: entry.PlateId);
        return view;
    }

    /// <summary>The Library's name for a Plate it can show, else the name kept with the changes.</summary>
    private string DisplayName(Entry entry)
    {
        if (library.FindPlate(entry.PlateId) is { IsReady: true } plate)
        {
            return plate.DisplayName;
        }

        return string.IsNullOrWhiteSpace(entry.Kept.Draft.PlateName) ? PlateNaming.DefaultName : entry.Kept.Draft.PlateName;
    }

    private sealed class Entry(KeptDraft kept)
    {
        internal KeptDraft Kept { get; } = kept;

        internal KeptChangesChoice Choice { get; set; } = kept.Choice;

        internal EntryState State { get; set; } = EntryState.Pending;

        internal Guid PlateId => Kept.PlateId;
    }

    private sealed record ViewText(
        string Body,
        string? Note,
        string? Consequence,
        string PrimaryLabel,
        string PrimaryTooltip,
        bool OffersDiscard,
        string DiscardTooltip,
        string? Position,
        string SamePlateQuestion,
        string OtherPlateQuestion,
        Guid? OpenPlateId,
        Guid PlateId)
    {
        internal string Question(bool restoreHere) => restoreHere && OpenPlateId == PlateId ? SamePlateQuestion : OtherPlateQuestion;
    }
}
