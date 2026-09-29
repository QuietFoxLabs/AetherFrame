using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Tutorial;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The tutorial's progression on a small script and on the real one: Next, Back, chapter jumps,
/// skipping, completion, steps that don't apply, steps already done, controls that aren't on
/// screen, and invalid positions. Nothing the session does touches a Plate: it only reads a
/// snapshot of the interface.
/// </summary>
public class TutorialSessionTests
{
    private static readonly TutorialContextSnapshot Nothing = new();
    private static readonly TutorialContextSnapshot Library = new(MyPlatesOpen: true, PlateCount: 2);
    private static readonly TutorialContextSnapshot Basic = new(MyPlatesOpen: true, PlateCount: 2, ActiveEditor: EditorSurfaceKind.Basic, PlateOpen: true);
    private static readonly TutorialContextSnapshot Advanced = new(MyPlatesOpen: false, PlateCount: 2, ActiveEditor: EditorSurfaceKind.Advanced, PlateOpen: true);

    private static IReadOnlyList<TutorialChapter> SmallScript() =>
    [
        new TutorialChapter("a", "A", "First.",
        [
            new TutorialStep("a1", "Intro", "Hello.", Mode: TutorialStepMode.Narrative),
            new TutorialStep("a2", "Create", "Click it.", TutorialTarget.LibraryCreatePlate, TutorialStepMode.Interact,
                Requires: TutorialCondition.MyPlatesOpen, AdvanceWhen: TutorialCondition.TemplateChooserOpen, FallbackBody: "Open My Plates.", FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("a3", "Card", "A card.", TutorialTarget.LibraryFirstPlateCard, Requires: TutorialCondition.LibraryHasPlates, SkipIfUnmet: true),
        ]),
        new TutorialChapter("b", "B", "Second.",
        [
            new TutorialStep("b1", "Navigator", "Sections.", TutorialTarget.BasicNavigator, Requires: TutorialCondition.BasicEditorOpen,
                FallbackBody: "Switch to Basic.", FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenBasicEditor),
            new TutorialStep("b2", "Done", "Bye.", Mode: TutorialStepMode.Narrative),
        ]),
    ];

    private static bool AllAvailable(TutorialTarget target) => target != TutorialTarget.None;

    private static bool NoneAvailable(TutorialTarget target) => false;

    [Fact]
    public void Start_ShowsTheFirstStep_AsNarrative()
    {
        var session = new TutorialSession(SmallScript());
        Assert.Equal(TutorialSessionStatus.Idle, session.Status);
        Assert.Null(session.CurrentStep);
        Assert.Equal(0, session.StepNumber);

        session.Start(Nothing);

        Assert.True(session.IsRunning);
        var view = session.Evaluate(Nothing, AllAvailable);
        Assert.Equal("a1", view.Step.Id);
        Assert.Equal(TutorialStepPresentation.Narrative, view.Presentation);
        Assert.Equal(1, view.StepNumber);
        Assert.Equal(5, view.TotalSteps);
        Assert.Equal(2, view.ChapterCount);
        Assert.False(view.CanGoBack);
        Assert.False(view.IsLastStep);
        Assert.Equal(TutorialTarget.None, view.Target);
    }

    [Fact]
    public void Next_And_Back_WalkTheScript_AcrossChapters()
    {
        var session = new TutorialSession(SmallScript());
        session.Start(Library);

        Assert.True(session.Next(Library));
        Assert.Equal("a2", session.CurrentStep!.Id);
        Assert.True(session.Next(Library));
        Assert.Equal("a3", session.CurrentStep!.Id);
        Assert.True(session.Next(Library));
        Assert.Equal("b1", session.CurrentStep!.Id);
        Assert.Equal(1, session.ChapterIndex);
        Assert.Equal(4, session.StepNumber);

        Assert.True(session.Back(Library));
        Assert.Equal("a3", session.CurrentStep!.Id);
        Assert.Equal(0, session.ChapterIndex);
        Assert.True(session.Back(Library));
        Assert.True(session.Back(Library));
        Assert.Equal("a1", session.CurrentStep!.Id);
        Assert.False(session.Back(Library));
        Assert.Equal("a1", session.CurrentStep!.Id);
        Assert.True(session.IsAtFirstStep);
    }

    [Fact]
    public void Next_PastTheLastStep_Completes()
    {
        var session = new TutorialSession(SmallScript());
        session.Start(Basic);
        while (session.Next(Basic))
        {
        }

        Assert.Equal(TutorialSessionStatus.Completed, session.Status);
        Assert.Null(session.CurrentStep);
        Assert.False(session.Next(Basic));
        Assert.False(session.Back(Basic));
        Assert.Throws<InvalidOperationException>(() => session.Evaluate(Basic, AllAvailable));
    }

    [Fact]
    public void Skip_LeavesTheTutorial_AndStop_IsNeither()
    {
        var session = new TutorialSession(SmallScript());
        session.Skip();
        Assert.Equal(TutorialSessionStatus.Idle, session.Status);

        session.Start(Nothing);
        session.Skip();
        Assert.Equal(TutorialSessionStatus.Skipped, session.Status);

        session.Start(Nothing);
        session.Stop();
        Assert.Equal(TutorialSessionStatus.Idle, session.Status);
    }

    [Fact]
    public void AStepWhoseRequirementIsUnmet_ShowsItsFallback_WithItsActionAndTarget()
    {
        var session = new TutorialSession(SmallScript());
        session.Start(Nothing);
        session.Next(Nothing);

        var view = session.Evaluate(Nothing, AllAvailable);

        Assert.Equal("a2", view.Step.Id);
        Assert.Equal(TutorialStepPresentation.Prerequisite, view.Presentation);
        Assert.Equal("Open My Plates.", view.Body);
        Assert.Equal(TutorialAction.OpenMyPlates, view.Action);
        Assert.Equal(TutorialTarget.None, view.Target); // this step has no fallback target
        Assert.False(view.AllowInteraction);

        session.Start(Nothing, chapter: 1);
        var fallbackWithTarget = session.Evaluate(new TutorialContextSnapshot(ActiveEditor: EditorSurfaceKind.Advanced, PlateOpen: true), AllAvailable);
        Assert.Equal(TutorialStepPresentation.Prerequisite, fallbackWithTarget.Presentation);
        Assert.Equal(TutorialTarget.EditorModeSwitch, fallbackWithTarget.Target);
        Assert.True(fallbackWithTarget.AllowInteraction);
        Assert.Equal(TutorialAction.OpenBasicEditor, fallbackWithTarget.Action);

        // The fallback target isn't on screen either: nothing is pointed at, the hint still shows.
        var noTargets = session.Evaluate(Nothing, NoneAvailable);
        Assert.Equal(TutorialStepPresentation.Prerequisite, noTargets.Presentation);
        Assert.Equal(TutorialTarget.None, noTargets.Target);
        Assert.False(noTargets.AllowInteraction);
    }

    [Fact]
    public void AStepWhoseControlIsNotOnScreen_SaysSo_AndStillAllowsNext()
    {
        var session = new TutorialSession(SmallScript());
        session.Start(Library);
        session.Next(Library);

        var view = session.Evaluate(Library, NoneAvailable);

        Assert.Equal(TutorialStepPresentation.MissingTarget, view.Presentation);
        Assert.Contains("isn't in view", view.Body);
        Assert.StartsWith("Click it.", view.Body);
        Assert.Equal(TutorialTarget.None, view.Target);
        Assert.True(session.Next(Library));
    }

    [Fact]
    public void AnInteractStep_SpotlightsItsControl_AndLetsThePlayerUseIt()
    {
        var session = new TutorialSession(SmallScript());
        session.Start(Library);
        session.Next(Library);

        var view = session.Evaluate(Library, AllAvailable);

        Assert.Equal(TutorialStepPresentation.Spotlight, view.Presentation);
        Assert.Equal(TutorialTarget.LibraryCreatePlate, view.Target);
        Assert.True(view.AllowInteraction);
        Assert.Equal(TutorialAction.None, view.Action);
    }

    [Fact]
    public void AnInspectStep_SpotlightsItsControl_WithoutInteraction()
    {
        var session = new TutorialSession(SmallScript());
        session.Start(Library);
        session.Next(Library);
        session.Next(Library);

        var view = session.Evaluate(Library, AllAvailable);

        Assert.Equal("a3", view.Step.Id);
        Assert.Equal(TutorialStepPresentation.Spotlight, view.Presentation);
        Assert.False(view.AllowInteraction);
    }

    [Fact]
    public void AutoAdvance_WaitsForTheConditionToTurnTrue_WhileTheStepShows()
    {
        var session = new TutorialSession(SmallScript());
        session.Start(Library);
        session.Next(Library);
        Assert.Equal("a2", session.CurrentStep!.Id);

        Assert.False(session.TryAutoAdvance(Library)); // chooser not open: arms
        Assert.True(session.TryAutoAdvance(Library with { TemplateChooserOpen = true }));
        Assert.Equal("a3", session.CurrentStep!.Id);
        Assert.False(session.TryAutoAdvance(Library)); // a3 has no condition
    }

    [Fact]
    public void AStepAlreadyDone_IsPassedOverGoingForward_ButShowsAgainGoingBack()
    {
        var chooserOpen = Library with { TemplateChooserOpen = true };
        var session = new TutorialSession(SmallScript());
        session.Start(chooserOpen);

        session.Next(chooserOpen);
        Assert.Equal("a3", session.CurrentStep!.Id); // a2 passed over: its "done when" already holds

        session.Back(chooserOpen);
        Assert.Equal("a2", session.CurrentStep!.Id);
        Assert.False(session.TryAutoAdvance(chooserOpen)); // not armed: it just shows again
        Assert.Equal("a2", session.CurrentStep!.Id);
    }

    [Fact]
    public void AStepThatDoesNotApply_IsSkippedInBothDirections()
    {
        var noPlates = new TutorialContextSnapshot(MyPlatesOpen: true, PlateCount: 0);
        var session = new TutorialSession(SmallScript());
        session.Start(noPlates);
        session.Next(noPlates);
        Assert.Equal("a2", session.CurrentStep!.Id);

        session.Next(noPlates);
        Assert.Equal("b1", session.CurrentStep!.Id); // a3 skipped: no Plates

        session.Back(noPlates);
        Assert.Equal("a2", session.CurrentStep!.Id); // and skipped again going back
    }

    [Fact]
    public void Back_FromAStepWhoseEveryPredecessorDoesNotApply_SettlesForwardFromTheStart()
    {
        IReadOnlyList<TutorialChapter> script =
        [
            new TutorialChapter("c", "C", "Only skippable steps first.",
            [
                new TutorialStep("c1", "One", "x", TutorialTarget.LibraryFirstPlateCard, Requires: TutorialCondition.LibraryHasPlates, SkipIfUnmet: true),
                new TutorialStep("c2", "Two", "x", TutorialTarget.LibraryFirstPlateCard, Requires: TutorialCondition.LibraryHasPlates, SkipIfUnmet: true),
                new TutorialStep("c3", "Three", "x", Mode: TutorialStepMode.Narrative),
                new TutorialStep("c4", "Four", "x", Mode: TutorialStepMode.Narrative),
            ]),
        ];
        var session = new TutorialSession(script);
        session.Start(Nothing);
        Assert.Equal("c3", session.CurrentStep!.Id);
        session.Next(Nothing);
        Assert.Equal("c4", session.CurrentStep!.Id);

        Assert.True(session.Back(Nothing));
        Assert.Equal("c3", session.CurrentStep!.Id);
        Assert.True(session.Back(Nothing)); // nothing applicable before c3: lands on c3 again, never traps
        Assert.Equal("c3", session.CurrentStep!.Id);
        Assert.True(session.IsRunning);
    }

    [Fact]
    public void Start_OnAScriptWhereNothingApplies_Completes()
    {
        IReadOnlyList<TutorialChapter> script =
        [
            new TutorialChapter("c", "C", "x", [new TutorialStep("c1", "One", "x", TutorialTarget.LibraryFirstPlateCard, Requires: TutorialCondition.LibraryHasPlates, SkipIfUnmet: true)]),
        ];
        var session = new TutorialSession(script);

        session.Start(Nothing);

        Assert.Equal(TutorialSessionStatus.Completed, session.Status);
    }

    [Fact]
    public void JumpToChapter_And_Resume_ClampInvalidPositions()
    {
        var session = new TutorialSession(SmallScript());
        session.JumpToChapter(Nothing, 1);
        Assert.Equal("b1", session.CurrentStep!.Id);
        session.JumpToChapter(Nothing, 99); // ignored
        Assert.Equal("b1", session.CurrentStep!.Id);
        session.JumpToChapter(Nothing, -1);
        Assert.Equal("b1", session.CurrentStep!.Id);

        session.Resume(Nothing, chapter: 1, step: 1);
        Assert.Equal("b2", session.CurrentStep!.Id);
        session.Resume(Nothing, chapter: 5, step: 0);
        Assert.Equal("a1", session.CurrentStep!.Id);
        session.Resume(Nothing, chapter: 0, step: 40);
        Assert.Equal("a1", session.CurrentStep!.Id);

        session.Start(Nothing, chapter: 40);
        Assert.Equal("b1", session.CurrentStep!.Id); // clamped to the last chapter
    }

    [Fact]
    public void AnInvalidScript_IsRefusedUpFront()
    {
        IReadOnlyList<TutorialChapter> duplicateIds =
        [
            new TutorialChapter("a", "A", "x", [new TutorialStep("s", "S", "x", Mode: TutorialStepMode.Narrative)]),
            new TutorialChapter("b", "B", "x", [new TutorialStep("s", "S", "x", Mode: TutorialStepMode.Narrative)]),
        ];
        Assert.Throws<ArgumentException>(() => new TutorialSession(duplicateIds));
        Assert.Throws<ArgumentException>(() => new TutorialSession([]));

        var problems = TutorialScriptValidation.Validate(
        [
            new TutorialChapter("", "", "", []),
            new TutorialChapter("c", "C", "x",
            [
                new TutorialStep("t1", "", "", TutorialTarget.None),
                new TutorialStep("t2", "T", "x", TutorialTarget.LibraryImport, TutorialStepMode.Narrative),
                new TutorialStep("t3", "T", "x", TutorialTarget.LibraryImport, Requires: TutorialCondition.MyPlatesOpen),
                new TutorialStep("t4", "T", "x", TutorialTarget.LibraryImport, SkipIfUnmet: true),
                new TutorialStep("t5", "T", new string('x', TutorialScriptValidation.MaxBodyLength + 1), TutorialTarget.LibraryImport),
            ]),
        ]);
        Assert.Contains(problems, p => p.Contains("Chapter id"));
        Assert.Contains(problems, p => p.Contains("no steps"));
        Assert.Contains(problems, p => p.Contains("t1") && p.Contains("points at nothing"));
        Assert.Contains(problems, p => p.Contains("t2") && p.Contains("Narrative but has a target"));
        Assert.Contains(problems, p => p.Contains("t3") && p.Contains("says nothing"));
        Assert.Contains(problems, p => p.Contains("t4") && p.Contains("requires nothing"));
        Assert.Contains(problems, p => p.Contains("t5") && p.Contains("too long"));
    }

    [Fact]
    public void Snapshot_ConditionsFollowTheInterface()
    {
        var advancedWithText = Advanced with { ElementSelected = true, TextElementSelected = true };
        Assert.True(Nothing.Satisfies(TutorialCondition.None));
        Assert.True(Library.Satisfies(TutorialCondition.MyPlatesOpen));
        Assert.True(Library.Satisfies(TutorialCondition.LibraryHasPlates));
        Assert.False(Library.Satisfies(TutorialCondition.AnyEditorOpen));
        Assert.True(Basic.Satisfies(TutorialCondition.AnyEditorOpen));
        Assert.True(Basic.Satisfies(TutorialCondition.BasicEditorOpen));
        Assert.False(Basic.Satisfies(TutorialCondition.AdvancedEditorOpen));
        Assert.True(Advanced.Satisfies(TutorialCondition.AdvancedEditorOpen));
        Assert.False(Advanced.Satisfies(TutorialCondition.TextElementSelected));
        Assert.True(advancedWithText.Satisfies(TutorialCondition.ElementSelected));
        Assert.True(advancedWithText.Satisfies(TutorialCondition.TextElementSelected));
        Assert.False((Basic with { ElementSelected = true, TextElementSelected = true }).Satisfies(TutorialCondition.TextElementSelected));
        Assert.False(new TutorialContextSnapshot(ActiveEditor: EditorSurfaceKind.Basic, PlateOpen: false).Satisfies(TutorialCondition.BasicEditorOpen));
        Assert.True(new TutorialContextSnapshot(MyPlatesOpen: true, TemplatesViewOpen: true).Satisfies(TutorialCondition.TemplatesViewOpen));
        Assert.False(new TutorialContextSnapshot(MyPlatesOpen: false, TemplatesViewOpen: true).Satisfies(TutorialCondition.TemplatesViewOpen));
        Assert.True(new TutorialContextSnapshot(TemplateChooserOpen: true).Satisfies(TutorialCondition.TemplateChooserOpen));
        Assert.True(new TutorialContextSnapshot(PlateOpen: true).Satisfies(TutorialCondition.PlateOpen));
    }

    [Fact]
    public void DefaultWording_ExistsForEveryCondition()
    {
        foreach (var condition in Enum.GetValues<TutorialCondition>())
        {
            Assert.False(string.IsNullOrWhiteSpace(TutorialSession.DefaultPrerequisiteText(condition)));
        }
    }

    // ---------------------------------------------------------------- the real script

    [Fact]
    public void RealScript_IsValid_AndHasTheTwelveChapters()
    {
        Assert.Empty(TutorialScriptValidation.Validate(TutorialScript.Chapters));
        Assert.Equal(12, TutorialScript.Chapters.Count);
        Assert.Equal("welcome", TutorialScript.Chapters[0].Id);
        Assert.Equal("done", TutorialScript.Chapters[^1].Id);
        Assert.True(TutorialScriptValidation.AllSteps(TutorialScript.Chapters).Count() >= 35);
        Assert.All(TutorialScript.Chapters, c => Assert.InRange(c.Steps.Count, 1, 7));
    }

    [Fact]
    public void RealScript_EveryTargetIsAKnownControl_AndEveryFallbackNamesTheWay()
    {
        foreach (var step in TutorialScriptValidation.AllSteps(TutorialScript.Chapters))
        {
            Assert.True(Enum.IsDefined(step.Target));
            Assert.True(Enum.IsDefined(step.FallbackTarget));
            if (step.Requires != TutorialCondition.None && !step.SkipIfUnmet)
            {
                Assert.False(string.IsNullOrWhiteSpace(step.FallbackBody), step.Id);
            }

            // Interact steps that wait for something say what they wait for, and never for a destructive act.
            if (step.Mode == TutorialStepMode.Interact)
            {
                Assert.DoesNotContain("delete", step.Body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("overwrite", step.Body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void RealScript_CanBeWalkedEndToEnd_FromAnyState_WithoutTrapping()
    {
        foreach (var snapshot in new[] { Nothing, Library, Basic, Advanced, Advanced with { ElementSelected = true, TextElementSelected = true } })
        {
            var session = new TutorialSession(TutorialScript.Chapters);
            session.Start(snapshot);
            var guard = 0;
            while (session.IsRunning)
            {
                var view = session.Evaluate(snapshot, NoneAvailable);
                Assert.False(string.IsNullOrWhiteSpace(view.Body));
                Assert.NotEqual(TutorialStepPresentation.Spotlight, view.Presentation);
                session.Next(snapshot);
                Assert.True(++guard < 200, "the tutorial never ends");
            }

            Assert.Equal(TutorialSessionStatus.Completed, session.Status);
        }
    }

    [Fact]
    public void RealScript_EveryChapterIsReachableFromThePicker()
    {
        for (var i = 0; i < TutorialScript.Chapters.Count; i++)
        {
            var session = new TutorialSession(TutorialScript.Chapters);
            session.JumpToChapter(Basic, i);
            Assert.True(session.Status is TutorialSessionStatus.Running or TutorialSessionStatus.Completed);
            if (session.IsRunning)
            {
                Assert.True(session.ChapterIndex >= i);
            }
        }
    }

    [Fact]
    public void RealScript_TheFirstPlateChapter_FollowsThePlayerCreatingAPlate()
    {
        var session = new TutorialSession(TutorialScript.Chapters);
        session.JumpToChapter(Library, 2);
        Assert.Equal("first.create", session.CurrentStep!.Id);
        Assert.True(session.Evaluate(Library, AllAvailable).AllowInteraction);

        Assert.False(session.TryAutoAdvance(Library));
        Assert.True(session.TryAutoAdvance(Library with { TemplateChooserOpen = true }));
        Assert.Equal("first.template", session.CurrentStep!.Id);

        Assert.False(session.TryAutoAdvance(Library with { TemplateChooserOpen = true }));
        Assert.True(session.TryAutoAdvance(Basic));
        Assert.Equal("first.workspace", session.CurrentStep!.Id);
        Assert.Equal(TutorialStepPresentation.Spotlight, session.Evaluate(Basic, AllAvailable).Presentation);
    }
}
