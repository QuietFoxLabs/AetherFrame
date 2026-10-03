using System;
using System.Threading.Tasks;
using AetherFrame.Services;
using AetherFrame.UI.Editor;

namespace AetherFrame.UI.Library;

/// <summary>
/// What was decided about opening a Plate: by <see cref="PlateOpenGuard.Request"/>, or by the
/// editors' <see cref="PlateSwitcher"/>, which alone can refuse.
/// </summary>
internal enum PlateOpenDecision
{
    /// <summary>It is the open Plate already: only show the editor.</summary>
    AlreadyOpen,

    /// <summary>Nothing unsaved stands in the way: open it now.</summary>
    Open,

    /// <summary>The open Plate has unsaved changes: ask Save, Discard or Cancel first.</summary>
    Ask,

    /// <summary>
    /// It can't go ahead at all (the editors' New Plate, from a Template that can't be used): why
    /// shows on the error line, and nothing is asked, discarded or made.
    /// </summary>
    Refused,
}

/// <summary>
/// A Plate to open, and in which editor. With <paramref name="TemplateId"/>, a new Plate instead,
/// made from that Template only once nothing unsaved stands in the way (the editors' New Plate,
/// see <see cref="PlateSwitcher"/>); it opens in the editor its content suits, so
/// <paramref name="PlateId"/> and <paramref name="Basic"/> mean nothing then.
/// </summary>
internal readonly record struct PlateOpenRequest(Guid PlateId, bool Basic, Guid? TemplateId = null)
{
    /// <summary>A new Plate, from <paramref name="templateId"/>.</summary>
    internal static PlateOpenRequest NewPlate(Guid templateId) => new(Guid.Empty, false, templateId);
}

/// <summary>
/// The unsaved-changes question in front of opening another Plate: Save (the other Plate opens
/// once the save succeeds; a failed save keeps the open Plate, with the error showing), Discard
/// (only once the edits are really gone) or Cancel. Opening the Plate that is already open never
/// asks, since it never replaces the document. The editors' New Plate asks the same question
/// before its Plate is made (<see cref="RequestNew"/>). The prompt itself is drawn by the window
/// that asks; this holds its state and rules, so every window that opens Plates asks the same way.
/// </summary>
internal sealed class PlateOpenGuard
{
    /// <summary>What shows when the save the player chose fails without a message of its own.</summary>
    internal const string SaveFailedMessage = "The open Plate couldn't be saved, so it's still open.";

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;

    private Task<bool>? saveTask;

    internal PlateOpenGuard(ProfileService profileService, EditorSession editorSession)
    {
        this.profileService = profileService;
        this.editorSession = editorSession;
    }

    /// <summary>The open waiting on the player's answer, if any.</summary>
    internal PlateOpenRequest? Pending { get; private set; }

    /// <summary>The player chose Save, and the save hasn't finished.</summary>
    internal bool IsSaving => saveTask is not null;

    /// <summary>
    /// Asks to open <paramref name="plateId"/>. Commits an edit still in progress first, so it
    /// counts as unsaved. On <see cref="PlateOpenDecision.Ask"/>, <see cref="Pending"/> holds the
    /// request until the player answers. With <paramref name="askEvenIfOpen"/>, the Plate that is
    /// already open asks too when it has unsaved changes: what comes next replaces them (restoring
    /// the unsaved changes AetherFrame kept, see <see cref="KeptChangesOffer"/>).
    /// </summary>
    internal PlateOpenDecision Request(Guid plateId, bool basic, bool askEvenIfOpen = false)
    {
        var alreadyOpen = profileService.OpenPlateId == plateId;
        if (alreadyOpen && !askEvenIfOpen)
        {
            return PlateOpenDecision.AlreadyOpen;
        }

        if (AsksFor(new PlateOpenRequest(plateId, basic)))
        {
            return PlateOpenDecision.Ask;
        }

        return alreadyOpen ? PlateOpenDecision.AlreadyOpen : PlateOpenDecision.Open;
    }

    /// <summary>
    /// Asks to replace the open Plate with a new one from <paramref name="templateId"/>, before the
    /// new Plate is made: on <see cref="PlateOpenDecision.Ask"/>, <see cref="Pending"/> holds the
    /// request (<see cref="PlateOpenRequest.TemplateId"/> set) until the player answers, so Cancel
    /// leaves nothing behind. Otherwise <see cref="PlateOpenDecision.Open"/>: nothing unsaved
    /// stands in the way. Commits an edit still in progress first, so it counts as unsaved.
    /// </summary>
    internal PlateOpenDecision RequestNew(Guid templateId) =>
        AsksFor(PlateOpenRequest.NewPlate(templateId)) ? PlateOpenDecision.Ask : PlateOpenDecision.Open;

    // Whether the open Plate's unsaved changes stand in the way of request: then it waits for the answer.
    private bool AsksFor(PlateOpenRequest request)
    {
        editorSession.CommitPendingEdits();
        if (profileService.CurrentProfile is null || !editorSession.IsDirty)
        {
            return false;
        }

        Pending = request;
        saveTask = null;
        return true;
    }

    /// <summary>Save and Discard are unavailable while any save is being written.</summary>
    internal bool CanAnswer => !profileService.IsBusy && saveTask is null;

    /// <summary>"Save": saves the open Plate; <see cref="Advance"/> then gives the open to perform.</summary>
    internal void Save()
    {
        if (Pending is null || !CanAnswer)
        {
            return;
        }

        saveTask = editorSession.SaveProfileAsync();
    }

    /// <summary>
    /// "Discard": drops the unsaved changes and returns the open to perform. Null, with the
    /// question still open, when the revert was refused (a save landed meanwhile); the editor's own
    /// message says why.
    /// </summary>
    internal PlateOpenRequest? Discard()
    {
        if (Pending is not { } open || !CanAnswer || !editorSession.DiscardChanges())
        {
            return null;
        }

        Pending = null;
        return open;
    }

    /// <summary>"Cancel": nothing opens, and the open Plate keeps its changes.</summary>
    internal void Cancel()
    {
        if (saveTask is null)
        {
            Pending = null;
        }
    }

    /// <summary>
    /// After Save: once the save has finished, the open to perform (when it succeeded) or the error
    /// to show (when it didn't). Null while there's nothing to report. Call once per frame.
    /// </summary>
    internal (PlateOpenRequest? Open, string? Error)? Advance()
    {
        if (saveTask is not { IsCompleted: true } task || Pending is not { } open)
        {
            return null;
        }

        saveTask = null;
        Pending = null;

        return task.IsCompletedSuccessfully && task.Result
            ? (open, null)
            : (null, editorSession.ErrorMessage ?? SaveFailedMessage);
    }
}
