using System.Threading.Tasks;
using AetherFrame.Services;

namespace AetherFrame.UI.Editor;

/// <summary>
/// The document actions of the shared editor action bar — Undo, Redo, Save, Revert — and exactly
/// when each is available. The Basic and Advanced editors both act through this one definition
/// over the one shared <see cref="EditorSession"/>, so they can never disagree about whether the
/// open Plate can be saved, reverted, undone, or redone (see <c>EditorActionBar</c>).
///
/// <para>Save and Revert only apply to unsaved changes: both are unavailable for a clean Plate, and
/// while a save is in flight. Every action re-checks its own availability, so a shortcut (Ctrl+S)
/// or a stale click can never save a clean Plate or revert one that has nothing to revert.</para>
/// </summary>
internal sealed class EditorDocumentCommands
{
    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;

    internal EditorDocumentCommands(ProfileService profileService, EditorSession editorSession)
    {
        this.profileService = profileService;
        this.editorSession = editorSession;
    }

    internal bool HasPlate => profileService.CurrentProfile is not null;

    /// <summary>Continuous recovery, for the bar's checkpoint indicator; null where there is none (the plugin attaches it).</summary>
    internal ContinuousRecovery? Recovery { get; set; }

    /// <summary>How recovery stands for the open Plate (nothing to show without unsaved changes).</summary>
    internal RecoveryIndicator RecoveryIndicator =>
        Recovery is { } recovery && IsDirty ? recovery.Indicator : new RecoveryIndicator(RecoveryIndicatorKind.None, null, null);

    /// <summary>What the indicator's tooltip says: the last checkpoint that finished, never one still being written.</summary>
    internal static string RecoveryText(RecoveryIndicator indicator)
    {
        var last = indicator.LastCheckpointUtc is { } utc
            ? $"Last recovery checkpoint: {utc.ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentCulture)}."
            : "No recovery checkpoint yet.";
        return indicator.Kind switch
        {
            RecoveryIndicatorKind.Protected => $"{last} If the game closes unexpectedly, these unsaved changes are offered back next time. Only Save changes the Plate.",
            RecoveryIndicatorKind.Pending => $"{last} The newest changes are kept a few seconds after you pause.",
            RecoveryIndicatorKind.Failing => $"{last} The newest changes couldn't be kept for recovery. Save keeps them in the Plate.",
            _ => string.Empty,
        };
    }

    /// <summary>The restrained warning shown while recovery fails, with when it tries again; null otherwise.</summary>
    internal static string? RecoveryWarning(RecoveryIndicator indicator) =>
        indicator.Kind != RecoveryIndicatorKind.Failing ? null
        : indicator.RetryIn is { } wait && wait > System.TimeSpan.Zero
            ? $"Recovery checkpoint couldn't be written. Trying again in {System.Math.Ceiling(wait.TotalSeconds):0}s."
            : "Recovery checkpoint couldn't be written. Trying again now.";

    /// <summary>A save is being written.</summary>
    internal bool IsSaving => profileService.IsBusy;

    /// <summary>The open Plate has unsaved changes (including an edit still in progress).</summary>
    internal bool IsDirty => HasPlate && editorSession.IsDirty;

    internal bool CanUndo => HasPlate && editorSession.CanUndo;

    internal bool CanRedo => HasPlate && editorSession.CanRedo;

    internal bool CanSave => HasPlate && !profileService.IsBusy && editorSession.IsDirty;

    internal bool CanRevert => HasPlate && !profileService.IsBusy && editorSession.IsDirty && editorSession.CanRevert;

    internal void Undo()
    {
        if (CanUndo)
        {
            editorSession.Undo();
        }
    }

    internal void Redo()
    {
        if (CanRedo)
        {
            editorSession.Redo();
        }
    }

    /// <summary>Saves the open Plate when it has unsaved changes (the button and Ctrl+S alike). Returns whether a save started.</summary>
    internal bool Save()
    {
        if (!CanSave)
        {
            return false;
        }

        editorSession.SaveProfile();
        return true;
    }

    /// <summary>
    /// <see cref="Save"/>, for a caller that waits on the outcome (the unsaved-changes prompt's
    /// Save). False without saving when there's nothing to save; otherwise the save's own result.
    /// </summary>
    internal Task<bool> SaveAsync() => CanSave ? editorSession.SaveProfileAsync() : Task.FromResult(false);

    /// <summary>
    /// Restores the last saved version (after the editor has confirmed it). The revert is itself
    /// one undo step, so it can be taken back. Returns whether anything was reverted.
    /// </summary>
    internal bool Revert()
    {
        if (!CanRevert)
        {
            return false;
        }

        return editorSession.RevertToSaved(undoable: true);
    }
}
