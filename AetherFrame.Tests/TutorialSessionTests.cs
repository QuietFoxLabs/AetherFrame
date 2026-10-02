using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Tutorial;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The tutorial's progression on a small script and on the real one: Next, Back, chapter jumps,
/// skipping, completion, steps that don't apply, steps already done, controls that aren't on
/// screen, and invalid positions. Nothing the session does touches a Plate: it only reads a
/// snapshot of the interface. The sharing chapters are held to more: only the sharing build shows
/// them, they only point and explain, they work whether or not the character shares, the windows
/// mark every control they point at, and they name each control by its own label.
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

    /// <summary><paramref name="snapshot"/> after the player has done what <paramref name="condition"/> waits for.</summary>
    internal static TutorialContextSnapshot Meeting(TutorialContextSnapshot snapshot, TutorialCondition condition) => condition switch
    {
        TutorialCondition.CreatingOrEditingPlate or TutorialCondition.TemplateChooserOpen => snapshot with { TemplateChooserOpen = true },
        TutorialCondition.MyPlatesOpen => snapshot with { MyPlatesOpen = true },
        TutorialCondition.AnyEditorOpen or TutorialCondition.BasicEditorOpen or TutorialCondition.PlateOpen => snapshot with { TemplateChooserOpen = false, ActiveEditor = EditorSurfaceKind.Basic, PlateOpen = true },
        TutorialCondition.AdvancedEditorOpen => snapshot with { TemplateChooserOpen = false, ActiveEditor = EditorSurfaceKind.Advanced, PlateOpen = true },
        TutorialCondition.ElementSelected => snapshot with { TemplateChooserOpen = false, ActiveEditor = EditorSurfaceKind.Advanced, PlateOpen = true, ElementSelected = true },
        TutorialCondition.TextElementSelected => snapshot with { TemplateChooserOpen = false, ActiveEditor = EditorSurfaceKind.Advanced, PlateOpen = true, ElementSelected = true, TextElementSelected = true },
        TutorialCondition.SharingWindowOpen => snapshot with { SharingWindowOpen = true },
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Add how the player meets this condition."),
    };

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
        Assert.True(new TutorialContextSnapshot(SharingWindowOpen: true).Satisfies(TutorialCondition.SharingWindowOpen));
        Assert.False(Library.Satisfies(TutorialCondition.SharingWindowOpen));
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
    public void RealScript_IsValid_AndHasTheFourteenChapters_TwelveInThePlayerBuild()
    {
        Assert.Empty(TutorialScriptValidation.Validate(TutorialScript.Chapters));
        Assert.Equal(14, TutorialScript.Chapters.Count);
        Assert.Equal("welcome", TutorialScript.Chapters[0].Id);
        Assert.Equal("done", TutorialScript.Chapters[^1].Id);
        Assert.True(TutorialScriptValidation.AllSteps(TutorialScript.Chapters).Count() >= 45);
        Assert.All(TutorialScript.Chapters, c => Assert.InRange(c.Steps.Count, 1, 7));

        var player = TutorialScript.ForBuild(sharing: false);
        Assert.Empty(TutorialScriptValidation.Validate(player));
        Assert.Equal(12, player.Count);
        Assert.Equal("welcome", player[0].Id);
        Assert.Equal("done", player[^1].Id);
        Assert.Same(TutorialScript.Chapters, TutorialScript.ForBuild(sharing: true));
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
        foreach (var snapshot in new[] { Nothing, Library, Basic, Advanced, Advanced with { ElementSelected = true, TextElementSelected = true }, Library with { SharingWindowOpen = true } })
        {
            var session = new TutorialSession(TutorialScript.Chapters);
            session.Start(snapshot);
            var guard = 0;
            while (session.IsRunning)
            {
                var view = session.Evaluate(snapshot, NoneAvailable);
                Assert.False(string.IsNullOrWhiteSpace(view.Body));
                Assert.NotEqual(TutorialStepPresentation.Spotlight, view.Presentation);
                Assert.True(++guard < 200, "the tutorial never ends");
                if (session.IsNextHeld(snapshot))
                {
                    // A held step shows the way (its own control, or the one that gets there) and
                    // Next waits until the player has done it; then Next moves on, and the walk
                    // continues in the original state, so every later step is still walked from it.
                    Assert.True(view.NextHeld);
                    var held = session.CurrentStep!;
                    Assert.True(held.WaitsForAction || held.FallbackTarget != TutorialTarget.None, held.Id);
                    Assert.True(session.Next(snapshot));
                    Assert.Same(held, session.CurrentStep);
                    var met = snapshot;
                    for (var i = 0; session.IsNextHeld(met); i++)
                    {
                        Assert.True(i < 4, held.Id);
                        met = Meeting(met, session.NextWaitsFor(met));
                    }

                    session.Next(met);
                    Assert.NotSame(held, session.CurrentStep);
                    continue;
                }

                session.Next(snapshot);
            }

            Assert.Equal(TutorialSessionStatus.Completed, session.Status);
        }
    }

    [Fact]
    public void RealScript_EveryStepThatAsksForAClick_CannotBeSkippedWithNext()
    {
        // The owner's rule (September 30): a step that asks the player to use the highlighted
        // control, and moves on once they have, never lets Next past it before they do.
        var steps = TutorialScriptValidation.AllSteps(TutorialScript.Chapters)
            .Where(s => s.Mode == TutorialStepMode.Interact && s.AdvanceWhen != TutorialCondition.None)
            .ToList();
        Assert.Contains(steps, s => s.Id == "first.create");
        Assert.Contains(steps, s => s.Id == "text.add");
        Assert.All(steps, s =>
        {
            Assert.True(s.WaitsForAction, s.Id);
            Assert.False(string.IsNullOrWhiteSpace(s.WaitHint), s.Id);
        });

        var session = new TutorialSession(TutorialScript.Chapters);
        session.JumpToChapter(Advanced, 5);
        Assert.Equal("text.add", session.CurrentStep!.Id);
        session.Next(Advanced);
        Assert.Equal("text.add", session.CurrentStep!.Id);
        session.Next(Advanced with { ElementSelected = true, TextElementSelected = true });
        Assert.Equal("text.content", session.CurrentStep!.Id);
    }

    [Fact]
    public void RealScript_AStepThatNeedsAnotherEditor_HoldsNextUntilTheSwitch()
    {
        // The owner's report (September 30): step 19, the Advanced Editor's tools, let Next past it
        // while the Basic Editor was open. It now waits for the switch it points at.
        var session = new TutorialSession(TutorialScript.Chapters);
        session.JumpToChapter(Basic, 4);
        Assert.Equal("advanced.switch", session.CurrentStep!.Id);
        session.Next(Basic);
        Assert.Equal("advanced.toolbar", session.CurrentStep!.Id);

        var view = session.Evaluate(Basic, AllAvailable);
        Assert.Equal(TutorialStepPresentation.Prerequisite, view.Presentation);
        Assert.Equal(TutorialTarget.EditorModeSwitch, view.Target);
        Assert.True(view.NextHeld);
        Assert.Equal(TutorialCondition.AdvancedEditorOpen, session.NextWaitsFor(Basic));
        session.Next(Basic);
        Assert.Equal("advanced.toolbar", session.CurrentStep!.Id);

        var advanced = Basic with { ActiveEditor = EditorSurfaceKind.Advanced };
        Assert.False(session.IsNextHeld(advanced));
        session.Next(advanced);
        Assert.Equal("advanced.layers", session.CurrentStep!.Id);

        // A step whose requirement only offers a button (no control to point at) still lets Next on.
        var library = new TutorialSession(TutorialScript.Chapters);
        library.JumpToChapter(Nothing, 1);
        Assert.Equal("library.home", library.CurrentStep!.Id);
        Assert.False(library.IsNextHeld(Nothing));
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

    [Fact]
    public void RealScript_CreatingTheFirstPlate_CannotBeSkippedWithNext()
    {
        var session = new TutorialSession(TutorialScript.Chapters);
        session.JumpToChapter(Library, 2);
        Assert.Equal("first.create", session.CurrentStep!.Id);

        // Next on "Start a new Plate" waits for Create Plate.
        Assert.True(session.IsNextHeld(Library));
        Assert.True(session.Evaluate(Library, AllAvailable).NextHeld);
        session.Next(Library);
        Assert.Equal("first.create", session.CurrentStep!.Id);

        // Next on "Choose a Template" waits for the new Plate, with the chooser open or closed again.
        var chooser = Library with { TemplateChooserOpen = true };
        Assert.False(session.TryAutoAdvance(Library));
        Assert.True(session.TryAutoAdvance(chooser));
        Assert.Equal("first.template", session.CurrentStep!.Id);
        session.Next(chooser);
        Assert.Equal("first.template", session.CurrentStep!.Id);
        var closedAgain = session.Evaluate(Library, AllAvailable);
        Assert.Equal(TutorialStepPresentation.Prerequisite, closedAgain.Presentation);
        Assert.True(closedAgain.NextHeld);
        Assert.DoesNotContain("Next", closedAgain.Body, StringComparison.Ordinal);
        session.Next(Library);
        Assert.Equal("first.template", session.CurrentStep!.Id);

        // Back still works from a held step, and so do the chapter picker and leaving the tour.
        Assert.True(session.Back(Library));
        Assert.Equal("first.create", session.CurrentStep!.Id);
        session.JumpToChapter(Library, 3);
        Assert.Equal(3, session.ChapterIndex);
        session.JumpToChapter(Library, 2);
        Assert.Equal("first.create", session.CurrentStep!.Id);
        session.Skip();
        Assert.Equal(TutorialSessionStatus.Skipped, session.Status);
    }

    [Fact]
    public void RealScript_OpeningAnExistingPlate_AlsoPassesTheFirstPlateSteps()
    {
        var session = new TutorialSession(TutorialScript.Chapters);
        session.JumpToChapter(Library, 2);
        Assert.Equal("first.create", session.CurrentStep!.Id);

        // Double-clicking a card opens it in an editor: that counts, and the chooser step doesn't apply.
        Assert.False(session.TryAutoAdvance(Library));
        Assert.True(session.TryAutoAdvance(Basic));
        Assert.Equal("first.workspace", session.CurrentStep!.Id);
        Assert.False(session.IsNextHeld(Basic));

        // Entering the chapter with a Plate already open starts at the editor, even with My Plates closed.
        var again = new TutorialSession(TutorialScript.Chapters);
        again.JumpToChapter(Basic, 2);
        Assert.Equal("first.workspace", again.CurrentStep!.Id);
        Assert.False(Advanced.MyPlatesOpen);
        again.JumpToChapter(Advanced, 2);
        Assert.Equal("first.workspace", again.CurrentStep!.Id);

        // Going back from there still shows "Start a new Plate" again, and doesn't bounce forward.
        Assert.True(again.Back(Advanced));
        Assert.Equal("first.create", again.CurrentStep!.Id);
        Assert.False(again.TryAutoAdvance(Advanced));
        Assert.Equal("first.create", again.CurrentStep!.Id);
    }

    [Fact]
    public void AStepThatWaitsForThePlayer_HoldsNextUntilDone_AndMustSayWhatItWaitsFor()
    {
        IReadOnlyList<TutorialChapter> script =
        [
            new TutorialChapter("a", "A", "First.",
            [
                new TutorialStep("a1", "Intro", "Hello.", Mode: TutorialStepMode.Narrative),
                new TutorialStep("a2", "Create", "Click it.", TutorialTarget.LibraryCreatePlate, TutorialStepMode.Interact,
                    Requires: TutorialCondition.MyPlatesOpen, AdvanceWhen: TutorialCondition.TemplateChooserOpen, FallbackBody: "Open My Plates.",
                    WaitsForAction: true, WaitHint: "Click Create Plate to continue."),
                new TutorialStep("a3", "Done", "Bye.", Mode: TutorialStepMode.Narrative),
            ]),
        ];
        var session = new TutorialSession(script);
        session.Start(Library);
        Assert.False(session.IsNextHeld(Library));
        session.Next(Library);
        Assert.Equal("a2", session.CurrentStep!.Id);

        Assert.True(session.IsNextHeld(Library));
        Assert.True(session.Next(Library));
        Assert.Equal("a2", session.CurrentStep!.Id);

        var chooser = Library with { TemplateChooserOpen = true };
        Assert.False(session.IsNextHeld(chooser));
        Assert.False(session.Evaluate(chooser, AllAvailable).NextHeld);
        session.Next(chooser);
        Assert.Equal("a3", session.CurrentStep!.Id);

        var problems = TutorialScriptValidation.Validate(
        [
            new TutorialChapter("c", "C", "x",
            [
                new TutorialStep("w1", "T", "x", TutorialTarget.LibraryImport, TutorialStepMode.Interact, AdvanceWhen: TutorialCondition.TemplateChooserOpen, WaitsForAction: true),
                new TutorialStep("w2", "T", "x", TutorialTarget.LibraryImport, TutorialStepMode.Interact, WaitsForAction: true, WaitHint: "Do it."),
                new TutorialStep("w3", "T", "x", TutorialTarget.LibraryImport, AdvanceWhen: TutorialCondition.TemplateChooserOpen, WaitsForAction: true, WaitHint: "Do it."),
            ]),
        ]);
        Assert.Contains(problems, p => p.Contains("w1") && p.Contains("waits for the player"));
        Assert.Contains(problems, p => p.Contains("w2") && p.Contains("waits for the player"));
        Assert.Contains(problems, p => p.Contains("w3") && p.Contains("waits for the player"));
    }

    [Fact]
    public void Snapshot_CreatingOrEditingPlate_MeansTheChooserOrAnOpenPlate()
    {
        Assert.False(Library.Satisfies(TutorialCondition.CreatingOrEditingPlate));
        Assert.True((Library with { TemplateChooserOpen = true }).Satisfies(TutorialCondition.CreatingOrEditingPlate));
        Assert.True(Basic.Satisfies(TutorialCondition.CreatingOrEditingPlate));
        Assert.True(Advanced.Satisfies(TutorialCondition.CreatingOrEditingPlate));
        Assert.False(new TutorialContextSnapshot(ActiveEditor: EditorSurfaceKind.Basic, PlateOpen: false).Satisfies(TutorialCondition.CreatingOrEditingPlate));
    }

    // ---------------------------------------------------------------- the sharing chapters

    private static readonly TutorialContextSnapshot SharingOpen = Library with { SharingWindowOpen = true };

    private static readonly string[] SharingChapterIds = ["online", "viewing"];

    /// <summary>The controls only the sharing build draws.</summary>
    private static readonly TutorialTarget[] SharingTargets =
    [
        TutorialTarget.LibrarySharing, TutorialTarget.SharingWindow, TutorialTarget.SharingConsent, TutorialTarget.SharingLodestoneCheck,
        TutorialTarget.SharingStatus, TutorialTarget.SharingPauseAndTurnOff, TutorialTarget.SharingFindPlayer, TutorialTarget.PlateSearch,
        TutorialTarget.ViewerOtherPlayersPlate,
    ];

    private static IEnumerable<TutorialStep> SharingSteps() => TutorialScript.Chapters.Where(c => c.SharingOnly).SelectMany(c => c.Steps);

    private static TutorialStep Step(string id) => TutorialScriptValidation.AllSteps(TutorialScript.Chapters).Single(s => s.Id == id);

    private static int IndexOf(string chapterId) => TutorialScript.Chapters.ToList().FindIndex(c => c.Id == chapterId);

    private sealed class Store : ITutorialPreferencesStore
    {
        public TutorialPreferences Preferences { get; } = new();

        public void Save()
        {
        }
    }

    [Fact]
    public void RealScript_OnlyTheSharingChapters_AreLeftOutOfThePlayerBuild()
    {
        Assert.Equal(SharingChapterIds, TutorialScript.Chapters.Where(c => c.SharingOnly).Select(c => c.Id));
        var player = TutorialScript.ForBuild(sharing: false);
        Assert.Equal(TutorialScript.Chapters.Where(c => !c.SharingOnly), player);

        // The chapters before them keep their places, so a tour resumed in the other build lands where it stopped.
        var first = TutorialScript.Chapters.ToList().FindIndex(c => c.SharingOnly);
        Assert.True(first > 0);
        for (var i = 0; i < first; i++)
        {
            Assert.Same(TutorialScript.Chapters[i], player[i]);
        }

        // Nothing a player build shows points at, or waits for, what only the sharing build draws.
        foreach (var step in TutorialScriptValidation.AllSteps(player))
        {
            Assert.DoesNotContain(step.Target, SharingTargets);
            Assert.DoesNotContain(step.FallbackTarget, SharingTargets);
            Assert.NotEqual(TutorialCondition.SharingWindowOpen, step.Requires);
            Assert.NotEqual(TutorialCondition.SharingWindowOpen, step.AdvanceWhen);
        }
    }

    [Fact]
    public void RealScript_TheSharingChapters_AreInHelpsChapterList_InTheSharingBuildOnly()
    {
        // Help's "Jump to a chapter" and the card's Chapters list the coordinator's chapters, a row each.
        var sharingBuild = new OnboardingCoordinator(new Store(), TutorialScript.ForBuild(sharing: true), TutorialScript.Version);
        foreach (var id in SharingChapterIds)
        {
            var index = sharingBuild.Session.Chapters.ToList().FindIndex(c => c.Id == id);
            Assert.True(index > 0, id);
            sharingBuild.StartChapter(SharingOpen, index);
            Assert.Equal(id, sharingBuild.Session.CurrentChapter!.Id);
            sharingBuild.StartChapter(Nothing, index);
            Assert.Equal(id, sharingBuild.Session.CurrentChapter!.Id);
        }

        var playerBuild = new OnboardingCoordinator(new Store(), TutorialScript.ForBuild(sharing: false), TutorialScript.Version);
        Assert.DoesNotContain(playerBuild.Session.Chapters, c => c.SharingOnly || SharingChapterIds.Contains(c.Id));
    }

    [Fact]
    public void RealScript_TheSharingChapters_OnlyPointAndExplain()
    {
        // The tutorial can do nothing about sharing: what a card offers only opens My Plates or switches editors.
        Assert.Equal(
            new[] { TutorialAction.None, TutorialAction.OpenMyPlates, TutorialAction.OpenBasicEditor, TutorialAction.OpenAdvancedEditor },
            Enum.GetValues<TutorialAction>());

        foreach (var step in SharingSteps())
        {
            Assert.True(step.FallbackAction is TutorialAction.None or TutorialAction.OpenMyPlates, step.Id);

            // The two controls a player may use from a step only open a window (the Sharing window, the
            // search). Every control that sends something is only looked at, under the spotlight's cover.
            if (step.Mode == TutorialStepMode.Interact)
            {
                Assert.Contains(step.Target, new[] { TutorialTarget.LibrarySharing, TutorialTarget.SharingFindPlayer });
            }
            else
            {
                Assert.Equal(TutorialStepMode.Inspect, step.Mode);
            }

            // Nothing waits for the player to do anything but open the Sharing window.
            Assert.True(step.AdvanceWhen is TutorialCondition.None or TutorialCondition.SharingWindowOpen, step.Id);
            Assert.True(step.Requires is TutorialCondition.None or TutorialCondition.MyPlatesOpen or TutorialCondition.SharingWindowOpen, step.Id);
            Assert.False(step.SkipIfUnmet, step.Id);
            Assert.True(step.Requires != TutorialCondition.SharingWindowOpen || step.FallbackTarget == TutorialTarget.LibrarySharing, step.Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealScript_TheSharingChapters_WorkWhetherOrNotTheCharacterShares(bool shares)
    {
        // What the windows draw: the consent for a character that doesn't share; its status, the pause
        // and turn-off buttons and the way to other players' Plates for one that does. Neither has the
        // search or another player's Plate open. A control that isn't drawn says so, and Next goes on.
        var drawn = new HashSet<TutorialTarget> { TutorialTarget.LibrarySharing, TutorialTarget.LibraryPlateGrid, TutorialTarget.SharingWindow };
        if (shares)
        {
            drawn.UnionWith([TutorialTarget.SharingStatus, TutorialTarget.SharingPauseAndTurnOff, TutorialTarget.SharingFindPlayer]);
        }
        else
        {
            drawn.Add(TutorialTarget.SharingConsent);
        }

        var session = new TutorialSession(TutorialScript.Chapters);
        session.JumpToChapter(SharingOpen, IndexOf("online"));
        var seen = new List<string>();
        while (session.IsRunning && session.CurrentChapter!.SharingOnly)
        {
            var step = session.CurrentStep!;
            seen.Add(step.Id);
            var view = session.Evaluate(SharingOpen, drawn.Contains);
            Assert.False(view.NextHeld, step.Id);
            if (drawn.Contains(step.Target))
            {
                Assert.Equal(TutorialStepPresentation.Spotlight, view.Presentation);
                Assert.Equal(step.Target, view.Target);
                Assert.Equal(step.Body, view.Body);
            }
            else
            {
                Assert.Equal(TutorialStepPresentation.MissingTarget, view.Presentation);
                Assert.Equal(TutorialTarget.None, view.Target);
                Assert.StartsWith(step.Body, view.Body, StringComparison.Ordinal);
                Assert.Contains("isn't in view", view.Body, StringComparison.Ordinal);
            }

            Assert.True(session.Next(SharingOpen));
        }

        // With the Sharing window open already, "Sharing" (which waits for it) is done and passed over.
        Assert.Equal(
            new[] { "online.window", "online.consent", "online.check", "online.shared", "online.stop", "online.preview", "viewing.find", "viewing.search", "viewing.viewer" },
            seen);
        Assert.Equal("done", session.CurrentChapter!.Id);
    }

    [Fact]
    public void RealScript_TheSharingChapters_WithTheSharingWindowClosed_ShowTheWayThere()
    {
        var session = new TutorialSession(TutorialScript.Chapters);
        session.JumpToChapter(Library, IndexOf("online"));
        Assert.Equal("online.open", session.CurrentStep!.Id);
        var open = session.Evaluate(Library, AllAvailable);
        Assert.Equal(TutorialStepPresentation.Spotlight, open.Presentation);
        Assert.Equal(TutorialTarget.LibrarySharing, open.Target);
        Assert.True(open.AllowInteraction); // the player opens the window; the tour never does
        Assert.True(open.NextHeld);
        session.Next(Library);
        Assert.Equal("online.open", session.CurrentStep!.Id);

        // Opening it moves the tour on.
        Assert.False(session.TryAutoAdvance(Library));
        Assert.True(session.TryAutoAdvance(SharingOpen));
        Assert.Equal("online.window", session.CurrentStep!.Id);
        Assert.Equal(TutorialStepPresentation.Spotlight, session.Evaluate(SharingOpen, AllAvailable).Presentation);

        // Closed again: the step says how to get it back, spotlights Sharing in My Plates, and waits.
        var closed = session.Evaluate(Library, AllAvailable);
        Assert.Equal(TutorialStepPresentation.Prerequisite, closed.Presentation);
        Assert.Equal(TutorialTarget.LibrarySharing, closed.Target);
        Assert.True(closed.AllowInteraction);
        Assert.Equal(TutorialAction.OpenMyPlates, closed.Action);
        Assert.Contains("click Sharing", closed.Body, StringComparison.Ordinal);
        Assert.True(closed.NextHeld);
        Assert.Equal(TutorialCondition.SharingWindowOpen, session.NextWaitsFor(Library));
        session.Next(Library);
        Assert.Equal("online.window", session.CurrentStep!.Id);

        // My Plates closed as well: the card's button brings it forward, where the way is shown.
        var nothing = session.Evaluate(Nothing, NoneAvailable);
        Assert.Equal(TutorialStepPresentation.Prerequisite, nothing.Presentation);
        Assert.Equal(TutorialAction.OpenMyPlates, nothing.Action);
        Assert.Equal(TutorialTarget.None, nothing.Target);

        // Back, the picker and Skip still work.
        Assert.True(session.Back(Library));
        Assert.Equal("online.open", session.CurrentStep!.Id);
        session.JumpToChapter(Library, IndexOf("viewing"));
        Assert.Equal("viewing.find", session.CurrentStep!.Id);
        Assert.True(session.IsNextHeld(Library));
        session.Skip();
        Assert.Equal(TutorialSessionStatus.Skipped, session.Status);
    }

    [Fact]
    public void RealScript_EveryTargetIsMarkedByTheWindowThatDrawsIt_InEachBuild()
    {
        var project = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame");
        var tutorialFolder = Path.Combine("UI", "Tutorial") + Path.DirectorySeparatorChar;
        string[] networking = [Path.Combine("Services", "Network"), Path.Combine("Hosting", "Network"), Path.Combine("Windows", "Network")];
        var sources = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Select(file => (Relative: Path.GetRelativePath(project, file), Text: File.ReadAllText(file)))
            .Where(source => source.Relative.Split(Path.DirectorySeparatorChar)[0] is not ("obj" or "bin") && !source.Relative.StartsWith(tutorialFolder, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sources);

        static Regex Named(TutorialTarget target) => new(@"TutorialTarget\." + target + @"\b");
        static IEnumerable<TutorialTarget> TargetsOf(IEnumerable<TutorialStep> steps) =>
            steps.SelectMany(step => new[] { step.Target, step.FallbackTarget }).Where(target => target != TutorialTarget.None).Distinct();

        // Every control the sharing build's tour points at is named by a window that draws it.
        foreach (var target in TargetsOf(TutorialScriptValidation.AllSteps(TutorialScript.ForBuild(sharing: true))))
        {
            Assert.True(sources.Any(source => Named(target).IsMatch(source.Text)), target + " is never marked by a window");
        }

        // The sharing chapters' own controls are marked where they are drawn.
        foreach (var target in TargetsOf(SharingSteps()).Intersect(SharingTargets))
        {
            var mark = new Regex(@"TutorialAnchorMarks\.Mark(?:Rect|Window)?\(TutorialTarget\." + target + @"\b");
            Assert.True(sources.Any(source => mark.IsMatch(source.Text)), target + " is never marked");
        }

        // A player build compiles nothing in the networking folders: what its tour points at is marked elsewhere.
        foreach (var target in TargetsOf(TutorialScriptValidation.AllSteps(TutorialScript.ForBuild(sharing: false))))
        {
            Assert.True(
                sources.Any(source => !networking.Any(folder => source.Relative.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)) && Named(target).IsMatch(source.Text)),
                target + " is marked only by the sharing build");
        }
    }

    [Theory]
    [InlineData("online.open", "Sharing", "Windows/PlateLibraryWindow.cs")]
    [InlineData("online.consent", "Turn on sharing", "Services/Network/Sharing/SharingText.cs")]
    [InlineData("online.check", "Copy", "Windows/Network/SharingWindow.cs")]
    [InlineData("online.check", "Get a code", "Windows/Network/SharingWindow.cs")]
    [InlineData("online.check", "Check", "Windows/Network/SharingWindow.cs")]
    [InlineData("online.check", "Cancel", "Windows/Network/SharingWindow.cs")]
    [InlineData("online.shared", "Shared", "Windows/PlateLibraryWindow.cs")]
    [InlineData("online.shared", "Try again", "Windows/Network/SharingProgressWindow.cs")]
    [InlineData("online.stop", "Pause sharing", "Windows/Network/SharingWindow.cs")]
    [InlineData("online.stop", "Resume sharing", "Windows/Network/SharingWindow.cs")]
    [InlineData("online.stop", "Turn off sharing for this character", "Windows/Network/SharingWindow.cs")]
    [InlineData("online.stop", "Turn off sharing for every character", "Windows/Network/SharingWindow.cs")]
    [InlineData("online.preview", "Check what would be shared (preview)", "Windows/PlateMenu.cs")]
    [InlineData("viewing.find", "View AetherFrame Plate", "Hosting/Network/ViewPlateMenu.cs")]
    [InlineData("viewing.find", "Find a player's Plate", "Windows/Network/SharingWindow.cs")]
    [InlineData("viewing.search", "AetherFrame Plates", "Windows/Network/PlateViewerWindow.cs")]
    [InlineData("viewing.search", "View", "Windows/Network/PlateViewerWindow.cs")]
    [InlineData("viewing.viewer", "Refresh", "Windows/Network/ServedPlatePresentation.cs")]
    [InlineData("viewing.viewer", "Hide this player", "Windows/Network/ServedPlatePresentation.cs")]
    [InlineData("viewing.viewer", "Report", "Windows/Network/ServedPlatePresentation.cs")]
    [InlineData("viewing.viewer", "Show their Plate again", "Windows/Network/ServedPlatePresentation.cs")]
    public void RealScript_TheSharingChapters_NameEachControlByItsOwnLabel(string stepId, string label, string source)
    {
        Assert.Contains(label, Step(stepId).Body, StringComparison.Ordinal);
        var text = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", source.Replace('/', Path.DirectorySeparatorChar)));

        // The control's own string: the label whole, or before an ImGui id or a trailing "...".
        Assert.Matches(new Regex("\"" + Regex.Escape(label) + "(\"|##|\\.\\.\\.\")"), text);
    }

    [Fact]
    public void RealScript_TheSharingChapters_SayWhatTheSharingWindowSays()
    {
        // The opening is the Sharing window's own introduction.
        var intro = SharingText.Intro[..(SharingText.Intro.IndexOf(". ", StringComparison.Ordinal) + 1)];
        Assert.StartsWith(intro, Step("online.open").Body, StringComparison.Ordinal);

        // The Lodestone check, as the window words its steps, and the notice once it passes.
        foreach (var words in new[] { "Character Profile", "Lodestone page" })
        {
            Assert.Contains(words, SharingText.CodeStepPaste + SharingText.CodeStepAddress, StringComparison.Ordinal);
            Assert.Contains(words, Step("online.check").Body, StringComparison.Ordinal);
        }

        Assert.Contains("delete the code", SharingText.Notice(SharingNoticeKind.CheckPassed), StringComparison.Ordinal);
        Assert.Contains("delete the code", Step("online.check").Body, StringComparison.Ordinal);

        // Sharing the Active Plate: without asking, as the consent says, on a save and on another Active Plate.
        Assert.Contains("without asking", string.Join(" ", SharingText.Consent), StringComparison.Ordinal);
        Assert.Contains("without asking", Step("online.shared").Body, StringComparison.Ordinal);
        Assert.Contains("making another Plate Active shares that one", SharingText.SavingShares, StringComparison.Ordinal);
        Assert.Contains("making another Plate Active shares that one", Step("online.shared").Body, StringComparison.Ordinal);

        // Pausing keeps the check; turning off deletes the Plate, its images and its check, and needs a new check.
        Assert.Contains("keeps its check", SharingText.PausedLine, StringComparison.Ordinal);
        Assert.Contains("keeps its check", Step("online.stop").Body, StringComparison.Ordinal);
        foreach (var words in new[] { "its images and its check", "new Lodestone check" })
        {
            Assert.Contains(words, SharingText.TurnOffConfirm, StringComparison.Ordinal);
            Assert.Contains(words, Step("online.stop").Body, StringComparison.Ordinal);
        }
    }
}
