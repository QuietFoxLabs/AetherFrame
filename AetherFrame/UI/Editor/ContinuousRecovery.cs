using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;

namespace AetherFrame.UI.Editor;

/// <summary>How the open Plate's recovery stands, for the editors' unobtrusive indicator.</summary>
internal enum RecoveryIndicatorKind
{
    /// <summary>No Plate open, or nothing unsaved: nothing to show.</summary>
    None,

    /// <summary>Unsaved changes the last checkpoint doesn't hold yet (or no checkpoint so far).</summary>
    Pending,

    /// <summary>The last checkpoint holds the unsaved changes exactly as they are.</summary>
    Protected,

    /// <summary>The last attempt failed; it is retried.</summary>
    Failing,
}

/// <summary>What the editors show of recovery: the last checkpoint that finished, never one still being written.</summary>
internal readonly record struct RecoveryIndicator(RecoveryIndicatorKind Kind, DateTime? LastCheckpointUtc, TimeSpan? RetryIn);

/// <summary>
/// Continuous crash recovery for the open Plate (October 5, 2026): while it has unsaved changes, a
/// recovery checkpoint is written once editing pauses for <see cref="IdleDelay"/>, and at least every
/// <see cref="MaxDelay"/> while editing goes on, so a crash of the game loses at most those seconds.
/// Both editors share the document, so both are covered. Elapsed time schedules it, never the clock.
///
/// <para><b>On the framework thread.</b> <see cref="Tick"/> runs once a frame where the editors draw,
/// so the document it reads is the one they edit, never half changed. It compares the document with
/// its last sample at most every <see cref="SampleInterval"/> (the structural comparison dirty state
/// uses), and only when it changed takes a new sample. A checkpoint is a copy made as a save makes
/// its snapshot; the writer serializes and writes it on its own thread (<see cref="RecoveryCheckpointWriter"/>).
/// Nothing here commits an edit in progress, ends a drag, touches the undo history or the saved
/// baseline, saves, changes which Plate is Active or shares anything.</para>
///
/// <para><b>Only what changed.</b> A checkpoint is written only when the content differs from the
/// last one that finished (and from one being written). A failed write leaves it due, retried after
/// a growing pause (<see cref="RetryDelays"/>), or at once through <see cref="RetryNow"/>. An editing
/// that starts with unsaved changes (kept changes resumed, a save that left later edits) is written
/// at once, and so is an Undo or Redo over a step that replaced the whole document (an undoable
/// Revert to Saved, kept changes put back), or any Undo or Redo of an editing with no checkpoint
/// yet: one such step can bring back a whole editing's work. Other steps wait for the pause as
/// edits do, so Undo over ordinary edits doesn't crowd the newest checkpoints with its own states
/// and the work one Redo away stays kept. Each step over a whole-document entry is a checkpoint of
/// its own, so five of them in a row can move older work out. A checkpoint is never written of the
/// saved content.</para>
///
/// <para><b>Lifecycle.</b> An editing is one document from when it is opened, saved, reverted,
/// discarded or given kept changes back (<see cref="EditorSession.RecoveryEpoch"/>) until the next of
/// those, with its own id. When one ends that way, its checkpoints are superseded and retired
/// (deleted) at once: they hold saved or discarded content, never the next editing's, and the
/// writer's order means a late write never brings them back. Undo back to the saved state retires
/// them only once nothing can be redone, judged on the live document, never a sample. Another
/// Plate opened in its place: an editing that has nothing unsaved (or whose last state the last
/// save wrote), whose changes Save as New Plate handed to the copy, or whose Plate the player
/// deleted, is retired; one with unsaved changes keeps its last state as a final checkpoint,
/// retried until written, for the next load to offer. A failed save changes nothing. Unloading first takes in a save, revert, delete or other
/// Plate that landed since the last frame, then settles what it can on the live document, stops new
/// checkpoints, and the draft kept at unload joins the open editing's checkpoints in one offer.</para>
/// </summary>
internal sealed class ContinuousRecovery
{
    /// <summary>A checkpoint follows once editing pauses this long.</summary>
    internal static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    /// <summary>While editing goes on, a checkpoint at least this often.</summary>
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>How often the document is compared with its last sample.</summary>
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Pauses before retrying after the first, second, ... failure in a row; the last repeats.</summary>
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private readonly ProfileService profiles;
    private readonly EditorSession session;
    private readonly Func<EditorSurfaceKind?> activeSurface;
    private readonly RecoveryCheckpointWriter writer;
    private readonly Func<TimeSpan> elapsed;
    private readonly Func<DateTime> utcNow;
    private readonly string build;
    private readonly IAetherFrameLog log;
    private readonly ConcurrentQueue<RecoveryWriteResult> results = new();

    // Every editing with work still to do: the open one, and ended ones still to retire or write.
    private readonly Dictionary<Guid, Editing> editings = new();

    private Editing? current;
    private long sequence;
    private bool stopped;
    private bool tickFailureLogged;

    /// <param name="activeSurface">Which editor shows the open Plate, recorded in each checkpoint.</param>
    /// <param name="elapsed">Monotonic elapsed time (a stopwatch): what schedules checkpoints.</param>
    /// <param name="utcNow">The clock, only for the time each checkpoint names.</param>
    internal ContinuousRecovery(
        ProfileService profiles, EditorSession session, Func<EditorSurfaceKind?> activeSurface, RecoveryCheckpointStore store, string build, IAetherFrameLog log,
        Func<TimeSpan> elapsed, Func<DateTime>? utcNow = null)
    {
        this.profiles = profiles;
        this.session = session;
        this.activeSurface = activeSurface;
        this.build = build;
        this.log = log;
        this.elapsed = elapsed;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        writer = new RecoveryCheckpointWriter(store, results.Enqueue, log);
    }

    internal RecoveryCheckpointWriter Writer => writer;

    internal Guid SessionId => writer.Store.SessionId;

    /// <summary>The open document's editing id; null when none is open.</summary>
    internal Guid? CurrentEditId => current?.EditId;

    /// <summary>What the editors show now (see <see cref="RecoveryIndicator"/>).</summary>
    internal RecoveryIndicator Indicator
    {
        get
        {
            if (current is not { Seen: true, Unsaved: true } editing)
            {
                return new RecoveryIndicator(RecoveryIndicatorKind.None, null, null);
            }

            if (editing.Holds(editing.Sample))
            {
                return new RecoveryIndicator(RecoveryIndicatorKind.Protected, editing.LastSuccessUtc, null);
            }

            if (editing.Failures > 0)
            {
                var left = editing.RetryAt - elapsed();
                return new RecoveryIndicator(RecoveryIndicatorKind.Failing, editing.LastSuccessUtc, left > TimeSpan.Zero ? left : TimeSpan.Zero);
            }

            return new RecoveryIndicator(RecoveryIndicatorKind.Pending, editing.LastSuccessUtc, null);
        }
    }

    /// <summary>
    /// The editing <paramref name="document"/> belongs to, for the draft kept at unload, so it joins
    /// that editing's checkpoints: this run's session id and the editing's id, or null when it isn't the open one.
    /// </summary>
    internal (Guid SessionId, Guid EditId)? EditingOf(ProfileDocument document) =>
        current is { } editing && ReferenceEquals(editing.Document, document) ? (SessionId, editing.EditId) : null;

    /// <summary>A failed checkpoint or retirement is tried again at the next frame.</summary>
    internal void RetryNow()
    {
        foreach (var editing in editings.Values)
        {
            editing.RetryAt = TimeSpan.Zero;
            editing.RetireRetryAt = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Once a frame, on the framework thread, after the windows drew: applies what the writer finished,
    /// follows the open document, and asks for a checkpoint or a retirement when one is due. Never throws.
    /// </summary>
    internal void Tick()
    {
        if (stopped)
        {
            return;
        }

        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            if (!tickFailureLogged)
            {
                tickFailureLogged = true;
                log.Error(ex, "AetherFrame's continuous recovery failed while following the open Plate.");
            }
        }
    }

    /// <summary>
    /// Unloading: a save, revert, delete or other Plate since the last frame is taken in first (saves
    /// and deletes finish off this thread), then what can be settled now is: a clean editing retired,
    /// judged on the live document, a superseded or deleted one retired, a final checkpoint still owed
    /// asked for. Then no more checkpoints; the draft kept at unload takes over the open editing. What
    /// was asked for still runs (see <see cref="RecoveryCheckpointWriter.Stop"/>).
    /// </summary>
    internal void Stop()
    {
        if (stopped)
        {
            return;
        }

        try
        {
            var now = elapsed();
            ApplyResults(now);
            Reconcile(now);
            foreach (var editing in editings.Values.ToList())
            {
                if (ReferenceEquals(editing, current))
                {
                    if (editing.MayHaveFiles && editing.RetireRequest is null && IsSavedNow(editing))
                    {
                        editing.RetireRequest = writer.Retire(editing.Document.ProfileId, editing.EditId);
                    }
                }
                else if (editing.RetireWanted)
                {
                    editing.RetireRequest ??= writer.Retire(editing.Document.ProfileId, editing.EditId);
                }
                else if (editing.Final is { } final && editing.Requested is null && !editing.Holds(editing.Sample))
                {
                    RequestWrite(editing, final);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "AetherFrame couldn't settle its recovery checkpoints as it unloaded.");
        }

        stopped = true;
        writer.Stop();
    }

    private void TickCore()
    {
        var now = elapsed();
        ApplyResults(now);
        Reconcile(now);

        if (current is { } editing)
        {
            Follow(editing, now);
        }

        FinishEnded(now);
    }

    /// <summary>A new editing when another document is open, or the open one was saved, reverted, discarded or given kept changes back; the one before it ends.</summary>
    private void Reconcile(TimeSpan now)
    {
        var document = profiles.CurrentProfile;
        var epoch = session.RecoveryEpoch;
        if (!ReferenceEquals(document, current?.Document) || current!.Epoch != epoch)
        {
            var ended = current;
            current = document is null ? null : new Editing(document, Guid.NewGuid(), epoch, ProfileService.DocumentState.Capture(document), now);
            if (current is not null)
            {
                current.HistorySteps = session.RecoveryHistorySteps;
                current.WholeDocumentSteps = session.RecoveryWholeDocumentSteps;
                editings[current.EditId] = current;
            }

            if (ended is not null)
            {
                End(ended, current);
            }
        }
    }

    private void Follow(Editing editing, TimeSpan now)
    {
        // An Undo or Redo since: the document is sampled now. One over a step that replaced the whole
        // document (Undo of Revert to Saved), or of an editing with no checkpoint yet, can bring back
        // a whole editing's work: while unsaved, it is checkpointed at once.
        var stepped = session.RecoveryHistorySteps != editing.HistorySteps;
        if (stepped)
        {
            editing.HistorySteps = session.RecoveryHistorySteps;
            if (session.RecoveryWholeDocumentSteps != editing.WholeDocumentSteps || (editing.LastSuccess is null && editing.Requested is null))
            {
                editing.StepPending = true;
            }

            editing.WholeDocumentSteps = session.RecoveryWholeDocumentSteps;
        }

        if (stepped || now - editing.SampledAt >= SampleInterval)
        {
            editing.SampledAt = now;
            if (!Matches(editing.Sample, editing.Document))
            {
                editing.Sample = ProfileService.DocumentState.Capture(editing.Document);
                editing.ChangedAt = now;
                editing.PendingSince ??= now;
            }
        }

        var (seen, baseline) = session.RecoveryBaseline(editing.Document);
        if (!seen)
        {
            // Opened since the last frame: exactly as saved, and nothing to keep yet.
            return;
        }

        editing.Seen = true;
        editing.Baseline = baseline;
        editing.Unsaved = baseline is null || !editing.SavedMatch.Equal(baseline, editing.Sample);
        if (editing.Unsaved)
        {
            RequestCheckpointIfDue(editing, now);
        }
        else if (session.CanRedo)
        {
            // Undone back to the saved state, with the changes one Redo away: they stay kept.
            editing.PendingSince = null;
            editing.StepPending = false;
        }
        else if (!editing.MayHaveFiles || Matches(editing.Sample, editing.Document))
        {
            editing.StepPending = false;
            RetireIfNeeded(editing, now);
        }

        // Otherwise the sample is behind the document: the next sample decides.
    }

    private void RequestCheckpointIfDue(Editing editing, TimeSpan now)
    {
        if (editing.Holds(editing.Sample) || editing.Asked(editing.Sample))
        {
            editing.PendingSince = null;
            editing.StepPending = false;
            return;
        }

        if (now < editing.RetryAt)
        {
            return;
        }

        // No change since the editing began, yet unsaved: it began so (kept changes resumed), so it is due now.
        var changedAt = editing.ChangedAt ?? (editing.OpenedAt - IdleDelay);
        var pendingSince = editing.PendingSince ?? changedAt;

        // Such a step is due at once, even right after another checkpoint was asked for.
        if (editing.Failures == 0 && !editing.StepPending && now - changedAt < IdleDelay && now - pendingSince < MaxDelay)
        {
            return;
        }

        // A copy made as a save makes its snapshot, here, where the document is edited.
        if (profiles.CopyOpenDocument() is not { } copy || !ReferenceEquals(copy.Source, editing.Document))
        {
            return;
        }

        // The sample can be behind the document: an Undo back to the saved state since it was taken is
        // never written. The next sample catches up; until then, no copy is made again.
        var state = ProfileService.DocumentState.Capture(copy.Document);
        if (editing.Baseline is { } saved && saved.ContentEquals(state))
        {
            editing.RetryAt = now + SampleInterval;
            return;
        }

        RequestWrite(editing, copy, state);
        editing.PendingSince = null;
        editing.StepPending = false;
    }

    private void RequestWrite(Editing editing, ProfileService.OpenDocumentCopy copy, ProfileService.DocumentState? captured = null)
    {
        var state = captured ?? ProfileService.DocumentState.Capture(copy.Document);
        var writtenUtc = utcNow();
        var checkpoint = DraftDocuments.Create(copy.Document, copy.BaseRevision, copy.BaseUpdatedAtUtc, EditorOf(activeSurface()), build, Guid.NewGuid(), writtenUtc);
        checkpoint.SessionId = SessionId;
        checkpoint.EditId = editing.EditId;
        checkpoint.Sequence = ++sequence;
        if (writer.Write(checkpoint) is not { } id)
        {
            return;
        }

        editing.Requested = new Request(id, state, writtenUtc);
        editing.LastWriteRequest = id;
        editing.MayHaveFiles = true;
    }

    private void RetireIfNeeded(Editing editing, TimeSpan now)
    {
        editing.PendingSince = null;
        editing.Requested = null;
        editing.LastSuccess = null;
        editing.Failures = 0;
        editing.RetryAt = TimeSpan.Zero;
        // A retirement already asked for covers every checkpoint asked for before it.
        if (!editing.MayHaveFiles || editing.RetireRequest > editing.LastWriteRequest || now < editing.RetireRetryAt)
        {
            return;
        }

        editing.RetireRequest = writer.Retire(editing.Document.ProfileId, editing.EditId);
    }

    /// <summary>
    /// <paramref name="ended"/> is no longer the open editing. The same document under a new epoch
    /// (saved, reverted, discarded, kept changes put back): superseded, retired at once. Another
    /// document in its place: retired when it has nothing unsaved, its last save wrote it, its changes
    /// went to a new Plate or its Plate was deleted here; otherwise its last state is kept as a final
    /// checkpoint for the next load to offer.
    /// </summary>
    private void End(Editing ended, Editing? next)
    {
        ended.Ended = true;
        if ((next is not null && ReferenceEquals(next.Document, ended.Document)) || !ended.Seen)
        {
            WantRetire(ended);
            return;
        }

        var state = ProfileService.DocumentState.Capture(ended.Document);
        var unsaved = (ended.Baseline is not { } baseline || !baseline.ContentEquals(state)) && !session.WasSavedAs(ended.Document, state);
        if (!unsaved || session.WereUnsavedChangesHandedOff(ended.Document) || profiles.WasDeletedWhileOpen(ended.Document))
        {
            WantRetire(ended);
            return;
        }

        ended.Sample = state;
        if (ended.Holds(state) || profiles.CopyClosedDocument(ended.Document) is not { } copy)
        {
            Forget(ended);
            return;
        }

        ended.Final = copy;
        ended.Failures = 0;
        ended.RetryAt = TimeSpan.Zero;
        RequestWrite(ended, copy);
        log.Information($"AetherFrame keeps the unsaved changes of Plate {ended.Document.ProfileId} as a recovery checkpoint: another Plate was opened in its place.");
    }

    private void WantRetire(Editing ended)
    {
        if (!ended.MayHaveFiles)
        {
            Forget(ended);
            return;
        }

        ended.RetireWanted = true;
    }

    /// <summary>Ended editings' remaining work: retirements, and final checkpoints, until done.</summary>
    private void FinishEnded(TimeSpan now)
    {
        foreach (var editing in editings.Values.ToList())
        {
            if (ReferenceEquals(editing, current))
            {
                continue;
            }

            if (editing.RetireWanted)
            {
                if (editing.RetireRequest is null && now >= editing.RetireRetryAt)
                {
                    editing.RetireRequest = writer.Retire(editing.Document.ProfileId, editing.EditId);
                }
            }
            else if (editing.Final is { } final && editing.Requested is null && !editing.Holds(editing.Sample) && now >= editing.RetryAt)
            {
                RequestWrite(editing, final);
            }
        }
    }

    // The open editing is exactly as saved now, on the live document (a sample can be behind it).
    private bool IsSavedNow(Editing editing)
    {
        var (seen, baseline) = session.RecoveryBaseline(editing.Document);
        return seen && baseline is not null && Matches(baseline, editing.Document);
    }

    private void Forget(Editing editing)
    {
        if (!ReferenceEquals(editing, current))
        {
            editings.Remove(editing.EditId);
        }
    }

    private void ApplyResults(TimeSpan now)
    {
        while (results.TryDequeue(out var result))
        {
            if (!editings.TryGetValue(result.EditId, out var editing))
            {
                continue;
            }

            if (result.IsRetire)
            {
                if (editing.RetireRequest != result.RequestId)
                {
                    continue;
                }

                editing.RetireRequest = null;
                if (result.Succeeded)
                {
                    // A checkpoint asked for after this retirement may still be on its way.
                    editing.MayHaveFiles = editing.LastWriteRequest > result.RequestId;
                    editing.RetireFailures = 0;
                    if (editing.RetireWanted && !editing.MayHaveFiles)
                    {
                        Forget(editing);
                    }
                }
                else
                {
                    editing.RetireRetryAt = now + RetryDelay(++editing.RetireFailures);
                }

                continue;
            }

            // Superseded by a newer request, or asked for before a retirement: only the newest counts.
            if (editing.Requested is not { } request || request.Id != result.RequestId)
            {
                continue;
            }

            editing.Requested = null;
            if (result.Succeeded)
            {
                editing.LastSuccess = request.State;
                editing.LastSuccessUtc = request.WrittenUtc;
                editing.Failures = 0;
                editing.RetryAt = TimeSpan.Zero;
                if (editing.Final is not null && !editing.RetireWanted && editing.Holds(editing.Sample))
                {
                    Forget(editing);
                }
            }
            else
            {
                editing.RetryAt = now + RetryDelay(++editing.Failures);
            }
        }
    }

    private static TimeSpan RetryDelay(int failures) => RetryDelays[Math.Min(failures, RetryDelays.Length) - 1];

    private static bool Matches(ProfileService.DocumentState state, ProfileDocument document) =>
        state.Matches(document.CanvasWidth, document.CanvasHeight, document.Background, document.BasicIdentity, document.BasicPlate, document.Elements, document.Components);

    private static DraftEditor EditorOf(EditorSurfaceKind? surface) => surface switch
    {
        EditorSurfaceKind.Basic => DraftEditor.Basic,
        EditorSurfaceKind.Advanced => DraftEditor.Advanced,
        _ => DraftEditor.None,
    };

    private sealed record Request(long Id, ProfileService.DocumentState State, DateTime WrittenUtc);

    /// <summary>One structural comparison, remembered until either side is another state.</summary>
    private sealed class ContentMatch
    {
        private ProfileService.DocumentState? left;
        private ProfileService.DocumentState? right;
        private bool equal;

        internal bool Equal(ProfileService.DocumentState? a, ProfileService.DocumentState b)
        {
            if (a is null)
            {
                return false;
            }

            if (!ReferenceEquals(a, left) || !ReferenceEquals(b, right))
            {
                left = a;
                right = b;
                equal = a.ContentEquals(b);
            }

            return equal;
        }
    }

    /// <summary>One document from when it was opened, saved, reverted, discarded or given kept changes back, until the next of those.</summary>
    private sealed class Editing(ProfileDocument document, Guid editId, int epoch, ProfileService.DocumentState sample, TimeSpan openedAt)
    {
        private readonly ContentMatch heldMatch = new();
        private readonly ContentMatch askedMatch = new();

        internal ProfileDocument Document { get; } = document;

        internal Guid EditId { get; } = editId;

        internal int Epoch { get; } = epoch;

        internal TimeSpan OpenedAt { get; } = openedAt;

        internal ProfileService.DocumentState Sample { get; set; } = sample;

        internal TimeSpan SampledAt { get; set; } = openedAt;

        internal TimeSpan? ChangedAt { get; set; }

        internal TimeSpan? PendingSince { get; set; }

        internal bool Seen { get; set; }

        internal bool Unsaved { get; set; }

        internal ProfileService.DocumentState? Baseline { get; set; }

        internal ContentMatch SavedMatch { get; } = new();

        internal Request? Requested { get; set; }

        internal ProfileService.DocumentState? LastSuccess { get; set; }

        internal DateTime? LastSuccessUtc { get; set; }

        internal int Failures { get; set; }

        internal TimeSpan RetryAt { get; set; }

        internal bool MayHaveFiles { get; set; }

        internal long? RetireRequest { get; set; }

        internal long LastWriteRequest { get; set; }

        internal int RetireFailures { get; set; }

        internal TimeSpan RetireRetryAt { get; set; }

        /// <summary>No longer the open editing.</summary>
        internal bool Ended { get; set; }

        /// <summary>Ended with nothing to keep: its checkpoints are to be retired.</summary>
        internal bool RetireWanted { get; set; }

        /// <summary>The session's Undo and Redo count when this editing last looked (see <see cref="EditorSession.RecoveryHistorySteps"/>).</summary>
        internal int HistorySteps { get; set; }

        /// <summary>The session's count of steps over whole-document entries when this editing last looked (see <see cref="EditorSession.RecoveryWholeDocumentSteps"/>).</summary>
        internal int WholeDocumentSteps { get; set; }

        /// <summary>An Undo or Redo that can bring back a whole editing's work since the last checkpoint: due at once while unsaved.</summary>
        internal bool StepPending { get; set; }

        /// <summary>Ended with unsaved changes: the copy its final checkpoint is written from, until written.</summary>
        internal ProfileService.OpenDocumentCopy? Final { get; set; }

        /// <summary>Whether the last checkpoint that finished holds <paramref name="state"/>.</summary>
        internal bool Holds(ProfileService.DocumentState state) => heldMatch.Equal(LastSuccess, state);

        /// <summary>Whether the checkpoint being written holds <paramref name="state"/>.</summary>
        internal bool Asked(ProfileService.DocumentState state) => askedMatch.Equal(Requested?.State, state);
    }
}
