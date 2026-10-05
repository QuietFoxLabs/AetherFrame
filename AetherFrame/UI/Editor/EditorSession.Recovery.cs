using AetherFrame.Domain.Profiles;
using AetherFrame.Services;

namespace AetherFrame.UI.Editor;

/// <summary>What continuous recovery reads of the editor session (see <see cref="ContinuousRecovery"/>): never history, selection or edits in progress.</summary>
internal sealed partial class EditorSession
{
    // The document whose unsaved changes now live in another Plate (Save as New Plate opened the copy).
    private ProfileDocument? handedOffDocument;

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

    /// <summary>
    /// Save as New Plate is about to open the copy in place of <paramref name="document"/>: its unsaved
    /// changes are saved in the copy, so its recovery checkpoints are retired rather than offered.
    /// </summary>
    internal void NoteUnsavedChangesHandedOff(ProfileDocument document) => handedOffDocument = document;

    /// <summary>Whether <paramref name="document"/>'s unsaved changes were handed to a new Plate (see <see cref="NoteUnsavedChangesHandedOff"/>).</summary>
    internal bool WereUnsavedChangesHandedOff(ProfileDocument document) => ReferenceEquals(handedOffDocument, document);
}
