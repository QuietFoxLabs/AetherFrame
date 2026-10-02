using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.UI.Editor;

namespace AetherFrame.UI.Tutorial;

/// <summary>How a step relates to its target.</summary>
internal enum TutorialStepMode
{
    /// <summary>Point at the control and explain it; the player reads and moves on. The control isn't clickable meanwhile.</summary>
    Inspect,

    /// <summary>Point at the control and let the player use it; the control stays clickable inside the spotlight.</summary>
    Interact,

    /// <summary>No control: a card in the middle of the screen (a chapter's opening, the completion).</summary>
    Narrative,
}

/// <summary>Something the tutorial can observe about the interface (never something it changes).</summary>
internal enum TutorialCondition
{
    None = 0,
    MyPlatesOpen,
    TemplatesViewOpen,
    TemplateChooserOpen,
    AnyEditorOpen,
    BasicEditorOpen,
    AdvancedEditorOpen,
    PlateOpen,
    LibraryHasPlates,
    ElementSelected,
    TextElementSelected,

    /// <summary>The Create Plate chooser is open, or a Plate is open in an editor (the player created one, or opened one they had).</summary>
    CreatingOrEditingPlate,

    /// <summary>The Sharing window is open (the sharing build only; never true in a player build, which has none).</summary>
    SharingWindowOpen,
}

/// <summary>
/// A safe, non-destructive thing the card can offer to do for the player when a step's
/// prerequisite isn't met: each opens or brings forward a window the player could open
/// themselves, and none of them changes a Plate.
/// </summary>
internal enum TutorialAction
{
    None = 0,
    OpenMyPlates,
    OpenBasicEditor,
    OpenAdvancedEditor,
}

/// <summary>
/// One step: a card's worth of explanation about one control (or none). Steps are data; the
/// session decides what to show from them and the interface's state.
/// </summary>
/// <param name="Id">Stable, unique across the whole script (used to remember progress).</param>
/// <param name="Title">The card's heading.</param>
/// <param name="Body">The explanation: what the control does and when to use it. Short.</param>
/// <param name="Target">The control to spotlight (None for a narrative step).</param>
/// <param name="Mode">Inspect, Interact or Narrative.</param>
/// <param name="Requires">What must be true for the step to make sense (an editor open, say).</param>
/// <param name="AdvanceWhen">The step is done, and the tutorial moves on, once this becomes true.</param>
/// <param name="SkipIfUnmet">When <paramref name="Requires"/> isn't met, skip the step instead of asking the player to meet it.</param>
/// <param name="FallbackBody">What the card says while <paramref name="Requires"/> isn't met (a navigation hint).</param>
/// <param name="FallbackTarget">The control to spotlight meanwhile (the way to meet the requirement).</param>
/// <param name="FallbackAction">A button on the card that meets the requirement safely.</param>
/// <param name="WaitsForAction">
/// Next doesn't pass this step until <paramref name="AdvanceWhen"/> is met: the player has to do
/// the thing (create their first Plate, say). Pressing Next meanwhile shows <paramref name="WaitHint"/>.
/// Back, the chapter picker and Skip tour still work, so the player is never trapped.
/// </param>
/// <param name="WaitHint">What the card says when Next is pressed on a step that waits for the player.</param>
internal sealed record TutorialStep(
    string Id,
    string Title,
    string Body,
    TutorialTarget Target = TutorialTarget.None,
    TutorialStepMode Mode = TutorialStepMode.Inspect,
    TutorialCondition Requires = TutorialCondition.None,
    TutorialCondition AdvanceWhen = TutorialCondition.None,
    bool SkipIfUnmet = false,
    string? FallbackBody = null,
    TutorialTarget FallbackTarget = TutorialTarget.None,
    TutorialAction FallbackAction = TutorialAction.None,
    bool WaitsForAction = false,
    string? WaitHint = null)
{
    /// <summary>Whether the step points at a control at all.</summary>
    internal bool HasTarget => Mode != TutorialStepMode.Narrative && Target != TutorialTarget.None;
}

/// <summary>A short, self-contained group of steps the player can also revisit on its own.</summary>
/// <param name="Id">Stable and unique.</param>
/// <param name="Title">As listed in the chapter picker.</param>
/// <param name="Summary">One line: what the chapter teaches.</param>
/// <param name="Steps">In order; at least one.</param>
/// <param name="SharingOnly">
/// The chapter teaches sharing, which only the sharing build has: a player build, with no sharing
/// code at all, leaves it out (<see cref="TutorialScript.ForBuild"/>).
/// </param>
internal sealed record TutorialChapter(string Id, string Title, string Summary, IReadOnlyList<TutorialStep> Steps, bool SharingOnly = false)
{
    /// <summary>What must be true for the chapter to be worth entering (the first step's requirement, if any).</summary>
    internal TutorialCondition Requires => Steps.Count > 0 ? Steps[0].Requires : TutorialCondition.None;
}

/// <summary>Checks a script the way the tests do, so authoring mistakes fail at build time, not in game.</summary>
internal static class TutorialScriptValidation
{
    /// <summary>Every problem with <paramref name="chapters"/>; empty when the script is sound.</summary>
    internal static IReadOnlyList<string> Validate(IReadOnlyList<TutorialChapter> chapters)
    {
        var problems = new List<string>();
        if (chapters.Count == 0)
        {
            problems.Add("The script has no chapters.");
            return problems;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chapter in chapters)
        {
            if (string.IsNullOrWhiteSpace(chapter.Id) || !ids.Add("chapter:" + chapter.Id))
            {
                problems.Add($"Chapter id '{chapter.Id}' is missing or repeated.");
            }

            if (string.IsNullOrWhiteSpace(chapter.Title) || string.IsNullOrWhiteSpace(chapter.Summary))
            {
                problems.Add($"Chapter '{chapter.Id}' needs a title and a summary.");
            }

            if (chapter.Steps.Count == 0)
            {
                problems.Add($"Chapter '{chapter.Id}' has no steps.");
            }

            foreach (var step in chapter.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Id) || !ids.Add("step:" + step.Id))
                {
                    problems.Add($"Step id '{step.Id}' is missing or repeated.");
                }

                if (string.IsNullOrWhiteSpace(step.Title) || string.IsNullOrWhiteSpace(step.Body))
                {
                    problems.Add($"Step '{step.Id}' needs a title and a body.");
                }

                if (step.Mode != TutorialStepMode.Narrative && step.Target == TutorialTarget.None)
                {
                    problems.Add($"Step '{step.Id}' points at nothing; make it Narrative or give it a target.");
                }

                if (step.Mode == TutorialStepMode.Narrative && step.Target != TutorialTarget.None)
                {
                    problems.Add($"Step '{step.Id}' is Narrative but has a target.");
                }

                if (step.Requires != TutorialCondition.None && !step.SkipIfUnmet && string.IsNullOrWhiteSpace(step.FallbackBody))
                {
                    problems.Add($"Step '{step.Id}' requires {step.Requires} but says nothing when it's unmet.");
                }

                if (step.SkipIfUnmet && step.Requires == TutorialCondition.None)
                {
                    problems.Add($"Step '{step.Id}' skips when unmet but requires nothing.");
                }

                if (step.Body.Length > MaxBodyLength)
                {
                    problems.Add($"Step '{step.Id}' is too long ({step.Body.Length} characters; at most {MaxBodyLength}).");
                }

                if (step.WaitsForAction && (step.Mode != TutorialStepMode.Interact || step.AdvanceWhen == TutorialCondition.None || string.IsNullOrWhiteSpace(step.WaitHint)))
                {
                    problems.Add($"Step '{step.Id}' waits for the player, so it must be Interact, say when it's done, and say what to do.");
                }
            }
        }

        return problems;
    }

    /// <summary>A card is a glance, not a page.</summary>
    internal const int MaxBodyLength = 420;

    /// <summary>All steps of all chapters, in order.</summary>
    internal static IEnumerable<TutorialStep> AllSteps(IReadOnlyList<TutorialChapter> chapters) => chapters.SelectMany(c => c.Steps);
}

/// <summary>
/// What the tutorial can see of the interface this frame. Filled in by the plugin from state that
/// already exists (which windows are open, whether a Plate is open, what's selected); the tutorial
/// only reads it, so navigating the tutorial can never change a Plate, nor anything about sharing.
/// </summary>
internal readonly record struct TutorialContextSnapshot(
    bool MyPlatesOpen = false,
    bool TemplatesViewOpen = false,
    bool TemplateChooserOpen = false,
    EditorSurfaceKind? ActiveEditor = null,
    bool PlateOpen = false,
    int PlateCount = 0,
    bool ElementSelected = false,
    bool TextElementSelected = false,
    bool SharingWindowOpen = false)
{
    internal bool Satisfies(TutorialCondition condition) => condition switch
    {
        TutorialCondition.None => true,
        TutorialCondition.MyPlatesOpen => MyPlatesOpen,
        TutorialCondition.TemplatesViewOpen => MyPlatesOpen && TemplatesViewOpen,
        TutorialCondition.TemplateChooserOpen => TemplateChooserOpen,
        TutorialCondition.AnyEditorOpen => ActiveEditor is not null && PlateOpen,
        TutorialCondition.BasicEditorOpen => ActiveEditor == EditorSurfaceKind.Basic && PlateOpen,
        TutorialCondition.AdvancedEditorOpen => ActiveEditor == EditorSurfaceKind.Advanced && PlateOpen,
        TutorialCondition.PlateOpen => PlateOpen,
        TutorialCondition.LibraryHasPlates => PlateCount > 0,
        TutorialCondition.ElementSelected => ActiveEditor == EditorSurfaceKind.Advanced && PlateOpen && ElementSelected,
        TutorialCondition.TextElementSelected => ActiveEditor == EditorSurfaceKind.Advanced && PlateOpen && TextElementSelected,
        TutorialCondition.CreatingOrEditingPlate => TemplateChooserOpen || (ActiveEditor is not null && PlateOpen),
        TutorialCondition.SharingWindowOpen => SharingWindowOpen,
        _ => false,
    };
}
