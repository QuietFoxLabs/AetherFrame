using System;
using System.Collections.Generic;
using AetherFrame.Domain.Plates;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;

namespace AetherFrame.UI.Library;

/// <summary>
/// The editors' Open another Plate and New Plate (interface task 2): the open Plate replaced by
/// another one, or by a new one made from a Template, behind the unsaved-changes question
/// (<see cref="Guard"/>, which the editors' Plate menu draws).
///
/// <para>Open another Plate lists the Plates in My Plates' own order, since nothing records recent
/// Plates, and opens one as a double-click on its card does: in the editor its content suits.
/// New Plate makes its Plate only once nothing unsaved stands in the way. With unsaved changes the
/// question comes first, so its Cancel leaves nothing behind; Save or Discard then make the Plate
/// (<see cref="PlateActions.UseTemplate"/>) and open it. A Template that can't be used is refused
/// before the question, and again at its Discard (<see cref="Discard"/>). My Plates keeps its own
/// order for now: its Use Template makes the Plate, then asks (interface task 10 moves it to this
/// one).</para>
///
/// <para>Every open happens at the start of an editor's frame (<see cref="Advance"/>), before the
/// editor reads the open Plate, never while it is drawing the one it replaces.</para>
/// </summary>
internal sealed class PlateSwitcher
{
    /// <summary>How many Plates Open another Plate shows at once; a longer list scrolls, under a search field.</summary>
    internal const int VisibleRows = 10;

    /// <summary>Why Open another Plate doesn't offer the Plate being edited.</summary>
    internal const string EditingNote = "You're editing this Plate.";

    /// <summary>What shows when a Plate can't be opened and says nothing of its own.</summary>
    internal const string CannotOpenNote = "This Plate can't be opened.";

    private readonly PlateLibraryService library;
    private readonly ProfileService profileService;
    private readonly PlateActions actions;
    private readonly Func<CharacterContext?> character;
    private readonly Func<PlateStarterContent> starter;
    private readonly Action<EditorSurfaceKind> showEditor;
    private readonly Action askFirst;
    private readonly IAetherFrameLog log;

    // An open that goes ahead at the start of the next frame (see Advance).
    private PlateOpenRequest? queued;

    /// <param name="library">The Plates, in My Plates' order.</param>
    /// <param name="profileService">The open Plate.</param>
    /// <param name="editorSession">Whether the open Plate has unsaved changes, and their Save or Discard.</param>
    /// <param name="actions">The editors' Plate actions, whose runner makes new Plates and shows what went wrong.</param>
    /// <param name="character">The logged-in character, if any, when a new Plate is made: it belongs to it.</param>
    /// <param name="starter">What a built-in Template fills in from the character, when a new Plate is made.</param>
    /// <param name="showEditor">Shows the open Plate in the Basic or Advanced Editor.</param>
    /// <param name="askFirst">Shows the unsaved-changes question for <see cref="Guard"/>'s pending request.</param>
    /// <param name="log">Where an unexpected failure to open a Plate is logged.</param>
    internal PlateSwitcher(
        PlateLibraryService library,
        ProfileService profileService,
        EditorSession editorSession,
        PlateActions actions,
        Func<CharacterContext?> character,
        Func<PlateStarterContent> starter,
        Action<EditorSurfaceKind> showEditor,
        Action askFirst,
        IAetherFrameLog log)
    {
        this.library = library;
        this.profileService = profileService;
        this.actions = actions;
        this.character = character;
        this.starter = starter;
        this.showEditor = showEditor;
        this.askFirst = askFirst;
        this.log = log;
        Guard = new PlateOpenGuard(profileService, editorSession);
    }

    /// <summary>The unsaved-changes question's state and answers, which the editors' Plate menu draws.</summary>
    internal PlateOpenGuard Guard { get; }

    /// <summary>Whether Open another Plate shows a search field, and scrolls: My Plates holds more Plates than <see cref="VisibleRows"/>.</summary>
    internal bool NeedsSearch => library.GetOrderedPlates().Count > VisibleRows;

    /// <summary>Whether My Plates holds a Plate other than the open one (Open another Plate is greyed out otherwise).</summary>
    internal bool HasAnotherPlate
    {
        get
        {
            var open = profileService.OpenPlateId;
            var plates = library.GetOrderedPlates();
            for (var i = 0; i < plates.Count; i++)
            {
                if (plates[i].PlateId != open)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The Plates Open another Plate lists: My Plates' own order, filtered by
    /// <paramref name="search"/> as My Plates' search filters its cards. The open Plate is listed too,
    /// marked as the one being edited (see <see cref="WhyNotOpen"/>).
    /// </summary>
    internal IReadOnlyList<PlateSummary> Plates(string? search) => library.Search(search);

    /// <summary>Why Open another Plate can't open <paramref name="plate"/>: it is the one being edited, or it can't be opened. Null when it can.</summary>
    internal string? WhyNotOpen(PlateSummary plate) =>
        plate.PlateId == profileService.OpenPlateId ? EditingNote
        : !plate.IsReady ? plate.Problem ?? CannotOpenNote
        : null;

    /// <summary>
    /// Open another Plate: asks first when the open Plate has unsaved changes (the question shows,
    /// and <see cref="PlateOpenDecision.Ask"/> is returned); otherwise the Plate opens at the start of
    /// the next frame, in the editor its content suits. Nothing happens for the open Plate itself.
    /// </summary>
    internal PlateOpenDecision Open(Guid plateId)
    {
        actions.Runner.Error = null;
        var request = new PlateOpenRequest(plateId, EditorFor(plateId) == EditorSurfaceKind.Basic);
        var decision = Guard.Request(request.PlateId, request.Basic);
        GoAhead(decision, request);
        return decision;
    }

    /// <summary>
    /// New Plate's Use Template: asks first when the open Plate has unsaved changes (the question
    /// shows, and <see cref="PlateOpenDecision.Ask"/> is returned), before anything is written;
    /// otherwise the new Plate is made now, and opens once it has been. A Template that can't be
    /// used, or another action still running, is refused before anything else
    /// (<see cref="PlateOpenDecision.Refused"/>, with why on the error line), and the question's
    /// Discard checks again (<see cref="Discard"/>), so Discard doesn't drop unsaved changes for a
    /// Plate the Template can't make.
    /// </summary>
    internal PlateOpenDecision New(Guid templateId)
    {
        actions.Runner.Error = null;
        if (WhyNotNew(templateId) is { } why)
        {
            actions.Runner.Error = why;
            return PlateOpenDecision.Refused;
        }

        var decision = Guard.RequestNew(templateId);
        GoAhead(decision, PlateOpenRequest.NewPlate(templateId));
        return decision;
    }

    /// <summary>
    /// The question's Discard, in the editors. For a new Plate it checks again first: when the
    /// Template can no longer be used, or another action is running, the question closes with why
    /// on the error line, and the unsaved changes stay. Otherwise as <see cref="PlateOpenGuard.Discard"/>:
    /// the open to perform, once the changes are really gone.
    /// </summary>
    internal PlateOpenRequest? Discard()
    {
        if (Guard.Pending is { TemplateId: { } templateId } && Guard.CanAnswer && WhyNotNew(templateId) is { } why)
        {
            Guard.Cancel();
            actions.Runner.Error = why;
            return null;
        }

        return Guard.Discard();
    }

    /// <summary>
    /// Nothing unsaved stands in the way any more (the question's Discard, or its Save once the save
    /// succeeded): the new Plate is made, or the other Plate opens at the start of the next frame.
    /// </summary>
    internal void Proceed(PlateOpenRequest request)
    {
        if (request.TemplateId is { } templateId)
        {
            Create(templateId);
            return;
        }

        queued = request;
    }

    /// <summary>
    /// Once a frame, at the start of an editor's Draw before it reads the open Plate (and once a frame
    /// when no editor drew): after the question's Save, the open goes ahead, or why the save failed
    /// shows; then a Plate waiting to open opens.
    /// </summary>
    internal void Advance()
    {
        if (Guard.Advance() is { } outcome)
        {
            if (outcome.Open is { } open)
            {
                Proceed(open);
            }
            else
            {
                actions.Runner.Error = outcome.Error;
            }
        }

        if (queued is not { } request)
        {
            return;
        }

        queued = null;
        OpenNow(request.PlateId, request.Basic);
    }

    /// <summary>The editor a saved Plate opens in: the one its content suits, as for a double-click on its card.</summary>
    private EditorSurfaceKind EditorFor(Guid plateId) => EditorSurfaceChooser.ForDocument(library.GetSavedDocument(plateId));

    /// <summary>Why a new Plate can't be made from the Template now: another action is running, or the Template can't be used. Null when it can.</summary>
    private string? WhyNotNew(Guid templateId) =>
        actions.Runner.IsBusy ? PlateOperationRunner.BusyMessage : actions.TemplateProblem(templateId);

    private void GoAhead(PlateOpenDecision decision, PlateOpenRequest request)
    {
        switch (decision)
        {
            case PlateOpenDecision.Ask:
                askFirst();
                break;

            case PlateOpenDecision.Open:
                Proceed(request);
                break;
        }
    }

    private void Create(Guid templateId) =>
        actions.UseTemplate(templateId, character(), starter(), result => OpenCreated(result.PlateId));

    /// <summary>
    /// The new Plate is made. Nothing unsaved stood in the way when it was asked for, but an edit
    /// made while it was being made is asked about, never dropped: the new Plate is in My Plates
    /// whatever the answer.
    /// </summary>
    private void OpenCreated(Guid plateId)
    {
        var request = new PlateOpenRequest(plateId, EditorFor(plateId) == EditorSurfaceKind.Basic);
        GoAhead(Guard.Request(request.PlateId, request.Basic), request);
    }

    private void OpenNow(Guid plateId, bool basic)
    {
        try
        {
            profileService.OpenPlate(plateId);
            showEditor(basic ? EditorSurfaceKind.Basic : EditorSurfaceKind.Advanced);
        }
        catch (Exception ex) when (ex is PlateLibraryException or InvalidOperationException)
        {
            actions.Runner.Error = UserFacingError.Describe(ex, "That Plate couldn't be opened.");
        }
        catch (Exception ex)
        {
            actions.Runner.Error = "That Plate couldn't be opened. See the Dalamud log for details.";
            log.Error(ex, "AetherFrame failed to open a Plate.");
        }
    }
}
