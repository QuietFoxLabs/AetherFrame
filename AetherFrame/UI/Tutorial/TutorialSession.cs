using System;
using System.Collections.Generic;

namespace AetherFrame.UI.Tutorial;

internal enum TutorialSessionStatus
{
    /// <summary>Not showing.</summary>
    Idle,
    Running,

    /// <summary>The player reached the end.</summary>
    Completed,

    /// <summary>The player left before the end.</summary>
    Skipped,
}

/// <summary>What the card shows for the current step.</summary>
internal enum TutorialStepPresentation
{
    /// <summary>The step's own control, spotlit.</summary>
    Spotlight,

    /// <summary>The step's requirement isn't met: a navigation hint, with the way there spotlit when it's on screen.</summary>
    Prerequisite,

    /// <summary>The requirement is met but the control isn't on screen (scrolled away, collapsed, in another view).</summary>
    MissingTarget,

    /// <summary>A card with no control.</summary>
    Narrative,
}

/// <summary>Everything the overlay needs to draw one step; computed fresh every frame from the session and the interface's state.</summary>
internal readonly record struct TutorialStepView(
    TutorialChapter Chapter,
    TutorialStep Step,
    int ChapterIndex,
    int ChapterCount,
    int StepNumber,
    int TotalSteps,
    TutorialStepPresentation Presentation,
    string Title,
    string Body,
    TutorialTarget Target,
    bool AllowInteraction,
    TutorialAction Action,
    bool CanGoBack,
    bool IsLastStep);

/// <summary>
/// The tutorial's progression: which chapter and step is showing, and how Next, Back, chapter
/// jumps, skipping and completion move through the script. It reads the interface's state (a
/// <see cref="TutorialContextSnapshot"/>) and never writes anything: a step whose requirement
/// isn't met asks the player to meet it (or is skipped, when the author said so), a step whose
/// "done when" condition is already true is passed over on the way forward, and a step's control
/// that isn't on screen is reported as missing rather than pointed at. Nothing here can trap the
/// player: Next, Back and Skip always work.
/// </summary>
internal sealed class TutorialSession
{
    private readonly IReadOnlyList<TutorialChapter> chapters;
    private readonly int[] firstStepNumber;
    private readonly Dictionary<TutorialStep, string> missingTargetTexts = new();
    private int chapterIndex;
    private int stepIndex;

    // Auto-advance arms only after the step's condition has been seen false while the step
    // showed, so going Back onto a done step just shows it again instead of bouncing forward.
    private bool advanceArmed;

    /// <exception cref="ArgumentException">The script fails <see cref="TutorialScriptValidation"/>.</exception>
    internal TutorialSession(IReadOnlyList<TutorialChapter> chapters)
    {
        var problems = TutorialScriptValidation.Validate(chapters);
        if (problems.Count > 0)
        {
            throw new ArgumentException("The tutorial script is invalid: " + string.Join(" ", problems), nameof(chapters));
        }

        this.chapters = chapters;
        firstStepNumber = new int[chapters.Count];
        var running = 0;
        for (var i = 0; i < chapters.Count; i++)
        {
            firstStepNumber[i] = running;
            running += chapters[i].Steps.Count;
        }

        TotalSteps = running;
    }

    internal TutorialSessionStatus Status { get; private set; } = TutorialSessionStatus.Idle;

    internal bool IsRunning => Status == TutorialSessionStatus.Running;

    internal IReadOnlyList<TutorialChapter> Chapters => chapters;

    internal int ChapterCount => chapters.Count;

    internal int TotalSteps { get; }

    /// <summary>The showing chapter's index, or -1.</summary>
    internal int ChapterIndex => IsRunning ? chapterIndex : -1;

    /// <summary>The showing step's index within its chapter, or -1.</summary>
    internal int StepIndex => IsRunning ? stepIndex : -1;

    internal TutorialChapter? CurrentChapter => IsRunning ? chapters[chapterIndex] : null;

    internal TutorialStep? CurrentStep => IsRunning ? chapters[chapterIndex].Steps[stepIndex] : null;

    /// <summary>The showing step's number across the whole script, 1-based; 0 when not running.</summary>
    internal int StepNumber => IsRunning ? firstStepNumber[chapterIndex] + stepIndex + 1 : 0;

    internal bool IsAtFirstStep => IsRunning && chapterIndex == 0 && stepIndex == 0;

    internal bool IsAtLastStep => IsRunning && chapterIndex == chapters.Count - 1 && stepIndex == chapters[chapterIndex].Steps.Count - 1;

    /// <summary>Starts (or restarts) at the first step of <paramref name="chapter"/>, passing over steps that are already done or don't apply.</summary>
    internal void Start(TutorialContextSnapshot snapshot, int chapter = 0)
    {
        Status = TutorialSessionStatus.Running;
        MoveTo(Math.Clamp(chapter, 0, chapters.Count - 1), 0);
        SettleForward(snapshot);
    }

    /// <summary>Starts at a remembered place (a resumed tutorial); an out-of-range position starts at the beginning of the script.</summary>
    internal void Resume(TutorialContextSnapshot snapshot, int chapter, int step)
    {
        Status = TutorialSessionStatus.Running;
        if (chapter < 0 || chapter >= chapters.Count || step < 0 || step >= chapters[chapter].Steps.Count)
        {
            chapter = 0;
            step = 0;
        }

        MoveTo(chapter, step);
        SettleForward(snapshot);
    }

    /// <summary>Jumps to the first step of <paramref name="chapter"/> (the chapter picker).</summary>
    internal void JumpToChapter(TutorialContextSnapshot snapshot, int chapter)
    {
        if (chapter < 0 || chapter >= chapters.Count)
        {
            return;
        }

        Status = TutorialSessionStatus.Running;
        MoveTo(chapter, 0);
        SettleForward(snapshot);
    }

    /// <summary>The next step; past the last, the tutorial completes. Returns false when nothing is running afterwards.</summary>
    internal bool Next(TutorialContextSnapshot snapshot)
    {
        if (!IsRunning)
        {
            return false;
        }

        if (!StepForward())
        {
            Status = TutorialSessionStatus.Completed;
            return false;
        }

        return SettleForward(snapshot);
    }

    /// <summary>The previous step, across chapters; at the first step, stays there. Returns false when it couldn't move.</summary>
    internal bool Back(TutorialContextSnapshot snapshot)
    {
        if (!IsRunning || !StepBackward())
        {
            return false;
        }

        // Going back passes over only steps that don't apply; a done step shows again.
        while (CurrentStep is { SkipIfUnmet: true } step && !snapshot.Satisfies(step.Requires))
        {
            if (!StepBackward())
            {
                // Nothing applicable before it: settle forward from the first step instead.
                MoveTo(0, 0);
                SettleForward(snapshot);
                return true;
            }
        }

        advanceArmed = false;
        return true;
    }

    /// <summary>The player leaves before the end.</summary>
    internal void Skip()
    {
        if (IsRunning)
        {
            Status = TutorialSessionStatus.Skipped;
        }
    }

    /// <summary>The tutorial stops showing (the plugin is unloading, say) without counting as skipped or completed.</summary>
    internal void Stop() => Status = TutorialSessionStatus.Idle;

    /// <summary>
    /// Called every frame: once the showing step's "done when" condition turns true, having been
    /// false while the step showed, the tutorial moves on. Returns true when it moved.
    /// </summary>
    internal bool TryAutoAdvance(TutorialContextSnapshot snapshot)
    {
        if (CurrentStep is not { } step || step.AdvanceWhen == TutorialCondition.None)
        {
            return false;
        }

        var satisfied = snapshot.Satisfies(step.AdvanceWhen);
        if (!satisfied)
        {
            advanceArmed = true;
            return false;
        }

        if (!advanceArmed)
        {
            return false;
        }

        Next(snapshot);
        return true;
    }

    /// <summary>
    /// What to show for the current step given the interface's state and whether a target is on
    /// screen (<paramref name="targetAvailable"/>). Throws when nothing is running.
    /// </summary>
    internal TutorialStepView Evaluate(TutorialContextSnapshot snapshot, Func<TutorialTarget, bool> targetAvailable)
    {
        if (CurrentStep is not { } step || CurrentChapter is not { } chapter)
        {
            throw new InvalidOperationException("No tutorial step is showing.");
        }

        TutorialStepPresentation presentation;
        var body = step.Body;
        var target = TutorialTarget.None;
        var allowInteraction = false;
        var action = TutorialAction.None;

        if (step.Mode == TutorialStepMode.Narrative)
        {
            presentation = TutorialStepPresentation.Narrative;
        }
        else if (!snapshot.Satisfies(step.Requires))
        {
            presentation = TutorialStepPresentation.Prerequisite;
            body = step.FallbackBody ?? DefaultPrerequisiteText(step.Requires);
            target = targetAvailable(step.FallbackTarget) ? step.FallbackTarget : TutorialTarget.None;
            allowInteraction = target != TutorialTarget.None;
            action = step.FallbackAction;
        }
        else if (!targetAvailable(step.Target))
        {
            presentation = TutorialStepPresentation.MissingTarget;
            if (!missingTargetTexts.TryGetValue(step, out var cached))
            {
                cached = MissingTargetText(step);
                missingTargetTexts[step] = cached;
            }

            body = cached;
        }
        else
        {
            presentation = TutorialStepPresentation.Spotlight;
            target = step.Target;
            allowInteraction = step.Mode == TutorialStepMode.Interact;
        }

        return new TutorialStepView(
            chapter, step, chapterIndex, chapters.Count, StepNumber, TotalSteps, presentation, step.Title, body, target, allowInteraction, action,
            CanGoBack: !IsAtFirstStep, IsLastStep: IsAtLastStep);
    }

    /// <summary>The card's wording when a step's requirement isn't met and the author gave none.</summary>
    internal static string DefaultPrerequisiteText(TutorialCondition requirement) => requirement switch
    {
        TutorialCondition.MyPlatesOpen => "Open My Plates to continue: type /af, or use the My Plates button in an editor.",
        TutorialCondition.TemplatesViewOpen => "Open Manage Templates from the Create Plate chooser to continue.",
        TutorialCondition.TemplateChooserOpen => "Choose Create Plate in My Plates to continue.",
        TutorialCondition.AnyEditorOpen or TutorialCondition.PlateOpen => "Open a Plate to continue: double-click one in My Plates, or create one with Create Plate.",
        TutorialCondition.BasicEditorOpen => "Switch to the Basic Editor to continue: use the Basic | Advanced switch at the top of the editor.",
        TutorialCondition.AdvancedEditorOpen => "Switch to the Advanced Editor to continue: use the Basic | Advanced switch at the top of the editor.",
        TutorialCondition.LibraryHasPlates => "Create a Plate first: choose Create Plate in My Plates.",
        TutorialCondition.ElementSelected => "Select an element to continue: click one on the canvas or in Layers.",
        TutorialCondition.TextElementSelected => "Select a text element to continue: click one on the canvas or in Layers, or add one with + Text.",
        _ => "This step isn't available right now. Use Next to continue.",
    };

    /// <summary>The card's wording when a step's control isn't on screen.</summary>
    internal static string MissingTargetText(TutorialStep step) =>
        $"{step.Body}\n\nThis control isn't in view right now. Scroll or open its section to see it, or use Next to continue.";

    private void MoveTo(int chapter, int step)
    {
        chapterIndex = chapter;
        stepIndex = step;
        advanceArmed = false;
    }

    private bool StepForward()
    {
        if (stepIndex + 1 < chapters[chapterIndex].Steps.Count)
        {
            MoveTo(chapterIndex, stepIndex + 1);
            return true;
        }

        if (chapterIndex + 1 < chapters.Count)
        {
            MoveTo(chapterIndex + 1, 0);
            return true;
        }

        return false;
    }

    private bool StepBackward()
    {
        if (stepIndex > 0)
        {
            MoveTo(chapterIndex, stepIndex - 1);
            return true;
        }

        if (chapterIndex > 0)
        {
            MoveTo(chapterIndex - 1, chapters[chapterIndex - 1].Steps.Count - 1);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Passes over, forward, every step that doesn't apply (its requirement unmet and skippable) or
    /// is already done (its "done when" already true). Completes the tutorial if that runs off the
    /// end. Returns whether the session is still running.
    /// </summary>
    private bool SettleForward(TutorialContextSnapshot snapshot)
    {
        while (CurrentStep is { } step && ShouldPassOver(step, snapshot))
        {
            if (!StepForward())
            {
                Status = TutorialSessionStatus.Completed;
                return false;
            }
        }

        return IsRunning;
    }

    private static bool ShouldPassOver(TutorialStep step, TutorialContextSnapshot snapshot) =>
        (step.SkipIfUnmet && !snapshot.Satisfies(step.Requires))
        || (step.AdvanceWhen != TutorialCondition.None && snapshot.Satisfies(step.Requires) && snapshot.Satisfies(step.AdvanceWhen));
}
