using System.Threading;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;

namespace AetherFrame.UI.Editor;

/// <summary>What continuous recovery reads of the editor session (see <see cref="ContinuousRecovery"/>): never history, selection or edits in progress.</summary>
internal sealed partial class EditorSession
{
    // The document whose unsaved changes now live in another Plate (Save as New Plate opened the copy).
    private ProfileDocument? handedOffDocument;

    // The last save that completed, kept when another Plate opens (unlike completedSave): a save and
    // the open that follows it can land between two of recovery's frames.
    private volatile CompletedSave? lastSaveForRecovery;

    // Counts every save, revert, discard and recovered state: each ends the editing recovery follows.
    private int recoveryEpoch;

    // Counts every Undo and Redo applied.
    private int recoveryHistorySteps;

    /// <summary>
    /// Changes whenever the open document's content was replaced or saved as a whole: a save that
    /// completed, Revert to Saved, Discard, or kept changes put back (<see cref="ApplyRecoveredState"/>).
    /// Recovery starts a new editing then, and retires the one before it. Safe from any thread.
    /// </summary>
    internal int RecoveryEpoch => Volatile.Read(ref recoveryEpoch);

    /// <summary>
    /// Changes with every Undo and Redo: one step can bring back a whole editing's work (Undo of an
    /// undoable Revert to Saved), so recovery checkpoints it at once. Framework thread only.
    /// </summary>
    internal int RecoveryHistorySteps => recoveryHistorySteps;

    /// <summary>
    /// Whether this session has taken <paramref name="document"/>'s saved state yet (a frame has seen
    /// it), and that state: what recovery compares the document against. A save that completed since
    /// the last frame is the baseline that frame would adopt. Null baseline, when seen, means it
    /// couldn't be captured, and the document counts as unsaved. Reads only; adopts nothing.
    /// </summary>
    internal (bool Seen, ProfileService.DocumentState? Baseline) RecoveryBaseline(ProfileDocument document)
    {
        if (!ReferenceEquals(document, baselineSourceProfile))
        {
            return (false, null);
        }

        return (true, completedSave is { } save && ReferenceEquals(save.Profile, document) ? save.State : savedBaseline);
    }

    /// <summary>Whether the last save that completed wrote <paramref name="document"/> exactly as <paramref name="state"/>, even once another Plate is open.</summary>
    internal bool WasSavedAs(ProfileDocument document, ProfileService.DocumentState state) =>
        lastSaveForRecovery is { } save && ReferenceEquals(save.Profile, document) && save.State.ContentEquals(state);

    /// <summary>
    /// Save as New Plate is about to open the copy in place of <paramref name="document"/>: its unsaved
    /// changes are saved in the copy, so its recovery checkpoints are retired rather than offered.
    /// </summary>
    internal void NoteUnsavedChangesHandedOff(ProfileDocument document) => handedOffDocument = document;

    /// <summary>Whether <paramref name="document"/>'s unsaved changes were handed to a new Plate (see <see cref="NoteUnsavedChangesHandedOff"/>).</summary>
    internal bool WereUnsavedChangesHandedOff(ProfileDocument document) => ReferenceEquals(handedOffDocument, document);

    private void NoteSavedForRecovery(CompletedSave save)
    {
        lastSaveForRecovery = save;
        Interlocked.Increment(ref recoveryEpoch);
    }

    private void NoteReplacedForRecovery() => Interlocked.Increment(ref recoveryEpoch);

    private void NoteHistoryStepForRecovery() => recoveryHistorySteps++;
}
