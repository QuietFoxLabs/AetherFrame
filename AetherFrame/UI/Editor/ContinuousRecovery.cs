using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// uses), and every frame once it may have checkpoints, so a Discard or Revert retires them in the
/// frame it happens; only when it changed takes a new sample. A checkpoint is a copy made as a save makes
/// its snapshot; the writer serializes and writes it on its own thread (<see cref="RecoveryCheckpointWriter"/>).
/// Nothing here commits an edit in progress, ends a drag, touches the undo history or the saved
/// baseline, saves, changes which Plate is Active or shares anything.</para>
///
/// <para><b>Only what changed.</b> A checkpoint is written only when the content differs from the
/// last one that finished (and from one being written). A failed write leaves it due, retried after
/// a growing pause (<see cref="RetryDelays"/>), or at once through <see cref="RetryNow"/>.</para>
///
/// <para><b>Lifecycle.</b> Each document opened is one editing, with its own id. Its checkpoints are
/// retired (deleted) once it has nothing unsaved: after Save, Discard and Revert to Saved, and when
/// undo brings it back to its saved state. Save as New Plate that opens the copy retires them too: the
/// changes are in the copy. Another Plate opened, or this one closed or deleted, while it still has
/// unsaved changes leaves its last state as a final checkpoint, kept for the next load to offer. A
/// failed save changes nothing: the checkpoints stay. Unloading stops new checkpoints, lets
/// retirements finish, and the draft kept at unload joins this editing's checkpoints in one offer.</para>
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

    // Editings left behind with unsaved changes: their final checkpoint's outcome is only logged.
    private readonly Dictionary<long, Guid> leftBehind = new();

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

    /// <summary>The open document's editing id, once a frame has seen it; null otherwise.</summary>
    internal Guid? CurrentEditId => current?.EditId;

    /// <summary>What the editors show now (see <see cref="RecoveryIndicator"/>).</summary>
    internal RecoveryIndicator Indicator
    {
        get
        {
            if (current is not { Unsaved: true } editing)
            {
                return new RecoveryIndicator(RecoveryIndicatorKind.None, null, null);
            }

            if (editing.LastSuccess is { } done && done.ContentEquals(editing.Sample))
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
    /// that editing's checkpoints: this run's session id and the editing's id, or null when no frame has seen it.
    /// </summary>
    internal (Guid SessionId, Guid EditId)? EditingOf(ProfileDocument document) =>
        current is { } editing && ReferenceEquals(editing.Document, document) ? (SessionId, editing.EditId) : null;

    /// <summary>A failed checkpoint or retirement is tried again at the next frame.</summary>
    internal void RetryNow()
    {
        if (current is { } editing)
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

    /// <summary>Unloading: no more checkpoints (the draft kept at unload takes over); retirements already asked for still run.</summary>
    internal void Stop()
    {
        stopped = true;
        writer.Stop();
    }

    private void TickCore()
    {
        var now = elapsed();
        ApplyResults(now);

        var document = profiles.CurrentProfile;
        if (!ReferenceEquals(document, current?.Document))
        {
            if (current is { } left)
            {
                Leave(left);
            }

            current = document is null ? null : new Editing(document, Guid.NewGuid(), ProfileService.DocumentState.Capture(document), now);
        }

        if (current is not { } editing)
        {
            return;
        }

        if (editing.MayHaveFiles || now - editing.SampledAt >= SampleInterval)
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

        editing.Baseline = baseline;
        editing.Seen = true;
        editing.Unsaved = baseline is null || !baseline.ContentEquals(editing.Sample);
        if (editing.Unsaved)
        {
            RequestCheckpointIfDue(editing, now);
        }
        else
        {
            RetireIfNeeded(editing, now);
        }
    }

    private void RequestCheckpointIfDue(Editing editing, TimeSpan now)
    {
        if ((editing.LastSuccess is { } done && done.ContentEquals(editing.Sample))
            || (editing.Requested is { } asked && asked.State.ContentEquals(editing.Sample)))
        {
            editing.PendingSince = null;
            return;
        }

        if (now < editing.RetryAt)
        {
            return;
        }

        var changedAt = editing.ChangedAt ?? editing.OpenedAt;
        var pendingSince = editing.PendingSince ?? changedAt;
        if (editing.Failures == 0 && now - changedAt < IdleDelay && now - pendingSince < MaxDelay)
        {
            return;
        }

        // A copy made as a save makes its snapshot, here, where the document is edited.
        if (profiles.CopyOpenDocument() is not { } copy || !ReferenceEquals(copy.Source, editing.Document))
        {
            return;
        }

        RequestWrite(editing, copy);
        editing.PendingSince = null;
    }

    private void RequestWrite(Editing editing, ProfileService.OpenDocumentCopy copy)
    {
        var state = ProfileService.DocumentState.Capture(copy.Document);
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
    /// The document is no longer the open one. Nothing unsaved (saved, discarded or reverted before it
    /// went), or its changes handed to a new Plate: its checkpoints are retired. Otherwise its last state
    /// is kept as a final checkpoint, when the last one doesn't hold it, for the next load to offer.
    /// </summary>
    private void Leave(Editing editing)
    {
        if (!editing.Seen)
        {
            return;
        }

        var state = ProfileService.DocumentState.Capture(editing.Document);
        var unsaved = editing.Baseline is not { } baseline || !baseline.ContentEquals(state);
        if (!unsaved || session.WereUnsavedChangesHandedOff(editing.Document))
        {
            if (editing.MayHaveFiles)
            {
                writer.Retire(editing.Document.ProfileId, editing.EditId);
            }

            return;
        }

        if (editing.LastSuccess is { } done && done.ContentEquals(state))
        {
            return;
        }

        if (profiles.CopyClosedDocument(editing.Document) is { } copy)
        {
            RequestWrite(editing, copy);
            if (editing.Requested is { } request)
            {
                leftBehind[request.Id] = editing.Document.ProfileId;
            }

            log.Information($"AetherFrame kept the unsaved changes of Plate {editing.Document.ProfileId} as a recovery checkpoint: another Plate was opened in its place.");
        }
    }

    private void ApplyResults(TimeSpan now)
    {
        while (results.TryDequeue(out var result))
        {
            if (leftBehind.Remove(result.RequestId) || current is not { } editing || result.EditId != editing.EditId)
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

    /// <summary>One open document: one editing, from opening it to another Plate taking its place.</summary>
    private sealed class Editing(ProfileDocument document, Guid editId, ProfileService.DocumentState sample, TimeSpan openedAt)
    {
        internal ProfileDocument Document { get; } = document;

        internal Guid EditId { get; } = editId;

        internal TimeSpan OpenedAt { get; } = openedAt;

        internal ProfileService.DocumentState Sample { get; set; } = sample;

        internal TimeSpan SampledAt { get; set; } = openedAt;

        internal TimeSpan? ChangedAt { get; set; }

        internal TimeSpan? PendingSince { get; set; }

        internal bool Seen { get; set; }

        internal bool Unsaved { get; set; }

        internal ProfileService.DocumentState? Baseline { get; set; }

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
    }
}
