using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Basic editor's live view and Portrait Frames attached to a picture (#136; GPT's review of the
/// combined build, October 7, 2026). A click looks through an attached frame, so it never opens the
/// Basic portrait's own Portrait Frame slot for it, and an attached frame selected in the Advanced
/// editor gets no outline in Basic yet stays selected there. The Basic portrait's own frame, and the
/// Advanced editor's selection, dragging, resizing and linking, work as before.
/// </summary>
public class BasicLiveViewAttachedFrameTests
{
    private static readonly Vector2 PictureBPosition = new(900f, 360f);
    private static readonly Vector2 Image1Position = new(660f, 40f);
    private static readonly Vector2 Image2Position = new(980f, 300f);

    /// <summary>The finished rendering's paint plan, as the live view builds it (minus measuring text).</summary>
    private static List<PaintStep> FinishedPlan(ProfileDocument document)
    {
        var paintOrder = new List<ProfileElement>();
        var drawn = new List<ProfileElement>();
        ProfileVisualBounds.FillDrawnElements(document, ProfileRenderOptions.Finished, paintOrder, drawn);
        var plan = new List<PaintStep>();
        ComponentPaintPlan.Build(document, drawn, BuiltInComponentCatalog.Instance, plan);
        return plan;
    }

    private static Vector2 CenterOf(ProfileElement element) => element.Position + (element.Size / 2f);

    /// <summary>
    /// Points on <paramref name="frame"/>'s edges (4 px inside the middle of each side) where it is the
    /// topmost thing in <paramref name="plan"/>, so a click there lands on the frame: the Advanced
    /// canvas's hit test, which looks through nothing, finds it there.
    /// </summary>
    private static List<Vector2> PointsOnFrame(ProfileDocument document, List<PaintStep> plan, Guid frame)
    {
        var rect = Assert.Single(plan, s => s.Component?.Id == frame).Placement.Rect;
        var center = rect.Position + (rect.Size / 2f);
        var unit = ComponentPaintPlan.Unit(document);
        return new[]
        {
            new Vector2(rect.Position.X + 4f, center.Y),
            new Vector2(rect.Position.X + rect.Size.X - 4f, center.Y),
            new Vector2(center.X, rect.Position.Y + 4f),
            new Vector2(center.X, rect.Position.Y + rect.Size.Y - 4f),
        }.Where(point => CanvasHitTest.Find(plan, point, unit).Component?.Id == frame).ToList();
    }

    /// <summary>
    /// A click on the live view at <paramref name="point"/>, made as BasicProfileEditorWindow's
    /// HandlePreviewInput makes it: what <see cref="BasicEditorView.TargetAt"/> finds there in the finished
    /// rendering, then <see cref="BasicEditorSession.SelectFromLiveView"/>. Returns the category the click
    /// opens and the kind of slot it brings into view.
    /// </summary>
    private static (BasicEditorCategory? Category, PlateComponentKind? Revealed) ClickLiveView(BasicHarness harness, Vector2 point)
    {
        var (category, component) = BasicEditorView.TargetAt(harness.Document, FinishedPlan(harness.Document), point);
        harness.Basic.SelectFromLiveView(component);
        return (category, component?.Kind);
    }

    private static void Drag(EditorSession session, List<PaintStep> plan, Vector2 from, Vector2 to)
    {
        session.BeginSelectionDrag(plan, from);
        session.UpdateInteraction(Vector2.Lerp(from, to, 0.5f), snap: false, snapThreshold: 0f);
        session.UpdateInteraction(to, snap: false, snapThreshold: 0f);
        session.EndInteraction();
    }

    private static int UndoDepth(EditorSession session)
    {
        var depth = 0;
        while (session.CanUndo)
        {
            session.Undo();
            depth++;
        }

        for (var i = 0; i < depth; i++)
        {
            session.Redo();
        }

        return depth;
    }

    /// <summary>
    /// The player's steps: a Classic Plate whose Basic portrait has a Portrait Frame from the Basic slot
    /// (frame A), then, in the Advanced editor, a second picture B with a Portrait Frame attached to it
    /// (frame B). Picture B sits on the other side of the Plate from the portrait, so nothing of frame A
    /// lies under frame B. Nothing is selected and the history is empty.
    /// </summary>
    private static async Task<(BasicHarness Harness, ImageProfileElement PictureB, Guid FrameA, Guid FrameB)> PlateWithAFrameOnAnotherPictureAsync()
    {
        var harness = await BasicHarness.NewClassicAsync();
        var session = harness.Session;
        harness.Basic.SetPortrait(harness.ImportablePng());
        session.SetComponentSlot(PlateComponentKind.PortraitFrame, BuiltInComponentCatalog.PortraitFrameLine);
        var frameA = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.PortraitFrame)!.Id;

        session.AddImageElement(harness.ImportablePng("b.png", 300, 300));
        var pictureB = Assert.IsType<ImageProfileElement>(harness.Document.Elements.Single(e => e.Id == session.SelectedElementId));
        var frameB = session.AddComponent(BuiltInComponentCatalog.PortraitFrameBrackets)!.Value;
        session.SetComponentTarget(frameB, pictureB.Id);
        pictureB.Position = PictureBPosition;

        var portrait = BasicSections.Find(harness.Document, ProfileElementRole.BasicPortrait)!;
        Assert.True(portrait.Position.X + portrait.Size.X + CanvasHitTest.FrameBand < PictureBPosition.X - CanvasHitTest.FrameBand);
        Assert.Equal(pictureB.Id, PlateComponentEditor.Find(harness.Document, frameB)!.TargetElementId);
        Assert.Equal(frameA, PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.PortraitFrame)!.Id);

        session.Select(null);
        session.ClearHistory();
        return (harness, pictureB, frameA, frameB);
    }

    [Fact]
    public async Task AClickOnAFrameAttachedToAPicture_IsLookedThrough_AndNeverOpensTheBasicPortraitFrameSlot()
    {
        var (harness, pictureB, frameA, frameB) = await PlateWithAFrameOnAnotherPictureAsync();
        using var _h = harness;
        var session = harness.Session;
        var document = harness.Document;
        var plan = FinishedPlan(document);
        var withoutFrameB = plan.Where(s => s.Component?.Id != frameB).ToList();
        var points = PointsOnFrame(document, plan, frameB);
        Assert.Equal(4, points.Count);

        foreach (var point in points)
        {
            // The click acts exactly as if frame B weren't drawn.
            var target = BasicEditorView.TargetAt(document, plan, point);
            Assert.NotEqual(frameB, target.Component?.Id);
            Assert.Equal(BasicEditorView.TargetAt(document, withoutFrameB, point), target);

            // Nothing is selected, the Portrait Frame slot isn't brought into view, and frame A stays as it is.
            var (_, revealed) = ClickLiveView(harness, point);
            Assert.NotEqual(PlateComponentKind.PortraitFrame, revealed);
            Assert.Null(session.SelectedComponentId);
        }

        Assert.False(session.CanUndo);
        Assert.Equal(frameA, PlateComponentEditor.FindSlot(document, PlateComponentKind.PortraitFrame)!.Id);
        Assert.Equal(BuiltInComponentCatalog.PortraitFrameLine, PlateComponentEditor.Find(document, frameA)!.DefinitionId);
        Assert.Equal(pictureB.Id, PlateComponentEditor.Find(document, frameB)!.TargetElementId);
    }

    [Fact]
    public async Task SwitchingFromAdvancedWithAnAttachedFrameSelected_OutlinesNothingInBasic_AndKeepsItSelectedForAdvanced()
    {
        var (harness, pictureB, frameA, frameB) = await PlateWithAFrameOnAnotherPictureAsync();
        using var _h = harness;
        var session = harness.Session;
        var document = harness.Document;

        // In Advanced, a click on frame B's edge selects frame B.
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        var onFrameB = PointsOnFrame(document, ComponentDocuments.Plan(document), frameB)[0];
        session.SelectOnCanvas(CanvasItemRef.Component(frameB), additive: false);
        Assert.Equal(frameB, session.SelectedComponentId);

        // The Basic | Advanced switch keeps the selection, but the live view outlines nothing, and the
        // Portrait Frame slot (frame A) isn't shown as selected.
        harness.Surfaces.Show(EditorSurfaceKind.Basic);
        Assert.Equal(EditorSurfaceKind.Basic, harness.Surfaces.ActiveSurface);
        Assert.Equal(frameB, session.SelectedComponentId);
        Assert.Null(BasicEditorView.OutlinedComponent(document, session.SelectedComponentId));
        Assert.NotEqual(PlateComponentEditor.FindSlot(document, PlateComponentKind.PortraitFrame)!.Id, session.SelectedComponentId);

        // Clicks on a section, on nothing, and on frame B itself leave frame B selected.
        var name = BasicSections.Find(document, ProfileElementRole.BasicName)!;
        var onSection = ClickLiveView(harness, CenterOf(name));
        Assert.Equal(BasicEditorCategory.Identity, onSection.Category);
        Assert.Null(onSection.Revealed);
        var onNothing = ClickLiveView(harness, new Vector2(-200f, -200f));
        Assert.Null(onNothing.Category);
        Assert.Null(onNothing.Revealed);
        Assert.Null(ClickLiveView(harness, PointsOnFrame(document, FinishedPlan(document), frameB)[0]).Revealed);
        Assert.Equal(frameB, session.SelectedComponentId);
        Assert.Null(BasicEditorView.OutlinedComponent(document, session.SelectedComponentId));

        // Back in Advanced it is still selected, with its handles, and drags on its own as before.
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        Assert.Equal(frameB, session.SelectedComponentId);
        Assert.True(session.PreviewSelectionGesture(ComponentDocuments.Plan(document))!.HasHandles);
        Drag(session, ComponentDocuments.Plan(document), onFrameB, onFrameB + new Vector2(12f, 8f));
        Assert.Equal(new Vector2(12f, 8f), PlateComponentEditor.Find(document, frameB)!.Offset);
        Assert.Equal(PictureBPosition, pictureB.Position);
        Assert.Equal(Vector2.Zero, PlateComponentEditor.Find(document, frameA)!.Offset);
        Assert.Equal(1, UndoDepth(session));
    }

    [Fact]
    public async Task TheBasicPortraitsOwnFrame_IsStillSelectedOutlinedEditedAndLetGo_FromTheLiveView()
    {
        var (harness, _, frameA, frameB) = await PlateWithAFrameOnAnotherPictureAsync();
        using var _h = harness;
        var session = harness.Session;
        var document = harness.Document;

        var onFrameA = PointsOnFrame(document, FinishedPlan(document), frameA);
        Assert.NotEmpty(onFrameA);
        foreach (var point in onFrameA)
        {
            session.Select(null);
            var click = ClickLiveView(harness, point);
            Assert.Equal(BasicEditorCategory.Portrait, click.Category);
            Assert.Equal(PlateComponentKind.PortraitFrame, click.Revealed);
            Assert.Equal(frameA, session.SelectedComponentId);
            Assert.Equal(frameA, BasicEditorView.OutlinedComponent(document, session.SelectedComponentId)?.Id);
            Assert.Equal(frameA, PlateComponentEditor.FindSlot(document, PlateComponentKind.PortraitFrame)!.Id);
        }

        // The slot it brings into view still edits frame A, and only frame A; it stays selected.
        session.SetComponentSlot(PlateComponentKind.PortraitFrame, BuiltInComponentCatalog.PortraitFrameDouble);
        Assert.Equal(BuiltInComponentCatalog.PortraitFrameDouble, PlateComponentEditor.Find(document, frameA)!.DefinitionId);
        Assert.Equal(BuiltInComponentCatalog.PortraitFrameBrackets, PlateComponentEditor.Find(document, frameB)!.DefinitionId);
        Assert.Equal(frameA, session.SelectedComponentId);

        // A click on a section lets go of it, as before.
        var name = BasicSections.Find(document, ProfileElementRole.BasicName)!;
        var onSection = ClickLiveView(harness, CenterOf(name));
        Assert.Equal(BasicEditorCategory.Identity, onSection.Category);
        Assert.Null(onSection.Revealed);
        Assert.Null(session.SelectedComponentId);

        // Selected in Advanced, it is outlined in Basic after the switch, as before.
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        session.SelectOnCanvas(CanvasItemRef.Component(frameA), additive: false);
        harness.Surfaces.Show(EditorSurfaceKind.Basic);
        Assert.Equal(frameA, BasicEditorView.OutlinedComponent(document, session.SelectedComponentId)?.Id);
        Assert.Equal(1, UndoDepth(session));
    }

    [Fact]
    public async Task TwoIndependentlyLinkedPictureAndFramePairs_AreLookedThroughInBasic_AndStaySeparateInAdvanced()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var session = harness.Session;
        harness.Basic.SetPortrait(harness.ImportablePng());
        session.SetComponentSlot(PlateComponentKind.PortraitFrame, BuiltInComponentCatalog.PortraitFrameLine);
        var frameA = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.PortraitFrame)!.Id;
        var (image1, image2, frame1, frame2) = LinkedGroupDocuments.AddTwoFramedPictures(harness.Document);
        image1.Position = Image1Position;
        image2.Position = Image2Position;
        session.Select(null);
        session.ClearHistory();
        var document = harness.Document;

        // In Advanced, each picture is linked with its own frame: Ctrl+click both, then Link Elements.
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        Guid? Link(ImageProfileElement image, PlateComponent frame)
        {
            session.Select(null);
            session.SelectOnCanvas(CanvasItemRef.Element(image.Id), additive: true);
            session.SelectOnCanvas(CanvasItemRef.Component(frame.Id), additive: true);
            Assert.Null(session.LinkSelectionBlockedReason);
            session.LinkSelection();
            return session.GroupOf(CanvasItemRef.Element(image.Id));
        }

        var group1 = Link(image1, frame1);
        var group2 = Link(image2, frame2);
        Assert.NotNull(group1);
        Assert.NotNull(group2);
        Assert.NotEqual(group1, group2);
        Assert.Equal(group1, session.GroupOf(CanvasItemRef.Component(frame1.Id)));
        Assert.Equal(group2, session.GroupOf(CanvasItemRef.Component(frame2.Id)));
        session.Select(null);

        // In Basic, a click on either frame is looked through: it selects nothing and brings no slot into view.
        harness.Surfaces.Show(EditorSurfaceKind.Basic);
        var plan = FinishedPlan(document);
        foreach (var frame in new[] { frame1, frame2 })
        {
            var without = plan.Where(s => !ReferenceEquals(s.Component, frame)).ToList();
            var points = PointsOnFrame(document, plan, frame.Id);
            Assert.Equal(4, points.Count);
            foreach (var point in points)
            {
                Assert.Equal(BasicEditorView.TargetAt(document, without, point), BasicEditorView.TargetAt(document, plan, point));
                var (_, revealed) = ClickLiveView(harness, point);
                Assert.NotEqual(PlateComponentKind.PortraitFrame, revealed);
                Assert.Null(session.SelectedComponentId);
            }
        }

        // A whole group selected in Advanced, or one attached member of it, gets no outline in Basic, and a
        // click on a section leaves it selected.
        var name = BasicSections.Find(document, ProfileElementRole.BasicName)!;
        session.SelectOnCanvas(CanvasItemRef.Component(frame1.Id), additive: false);
        Assert.Equal(group1, session.SelectedGroupId);
        Assert.Null(BasicEditorView.OutlinedComponent(document, session.SelectedComponentId));
        ClickLiveView(harness, CenterOf(name));
        Assert.Equal(group1, session.SelectedGroupId);

        session.SelectFromList(CanvasItemRef.Component(frame2.Id), additive: false);
        Assert.Equal(frame2.Id, session.SelectedComponentId);
        Assert.Null(BasicEditorView.OutlinedComponent(document, session.SelectedComponentId));
        ClickLiveView(harness, CenterOf(name));
        Assert.Equal(frame2.Id, session.SelectedComponentId);

        // In Advanced, a click on frame 1 selects pair 1 only, and dragging it moves picture 1 with its frame
        // on it (moved once), leaving pair 2 where it was.
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        var advancedPlan = ComponentDocuments.Plan(document);
        var onFrame1 = PointsOnFrame(document, advancedPlan, frame1.Id)[0];
        session.Select(null);
        session.SelectOnCanvas(CanvasItemRef.Component(frame1.Id), additive: false);
        Assert.Equal(group1, session.SelectedGroupId);
        Assert.Equal(2, session.SelectedItems.Count);
        var image2Before = new ElementRect(image2.Position, image2.Size);
        var frame2Before = LinkedGroupDocuments.PlacementOf(document, frame2);
        var delta = new Vector2(-30f, 25f);
        Drag(session, advancedPlan, onFrame1, onFrame1 + delta);
        Assert.Equal(Image1Position + delta, image1.Position);
        Assert.Equal(Vector2.Zero, frame1.Offset);
        Assert.Equal(new ElementRect(image1.Position, image1.Size), LinkedGroupDocuments.PlacementOf(document, frame1).Rect);
        Assert.Equal(image2Before, new ElementRect(image2.Position, image2.Size));
        Assert.Equal(frame2Before, LinkedGroupDocuments.PlacementOf(document, frame2));

        // Resizing pair 2 to half its size keeps frame 2 on picture 2 and leaves pair 1 alone.
        session.SelectOnCanvas(CanvasItemRef.Element(image2.Id), additive: false);
        Assert.Equal(group2, session.SelectedGroupId);
        var image1Before = new ElementRect(image1.Position, image1.Size);
        var frame1Before = LinkedGroupDocuments.PlacementOf(document, frame1);
        var gesture = session.PreviewSelectionGesture(ComponentDocuments.Plan(document))!;
        var anchor = gesture.Corners[0];
        var handle = gesture.Corners[2];
        session.BeginSelectionResize(ComponentDocuments.Plan(document), 2, handle);
        session.UpdateInteraction(anchor + ((handle - anchor) * 0.5f), snap: false, snapThreshold: 0f);
        session.EndInteraction();
        LinkedGroupDocuments.AssertNear(LinkedGroupDocuments.Image2Size * 0.5f, image2.Size);
        var frame2After = LinkedGroupDocuments.PlacementOf(document, frame2).Rect;
        LinkedGroupDocuments.AssertNear(image2.Position, frame2After.Position);
        LinkedGroupDocuments.AssertNear(image2.Size, frame2After.Size);
        Assert.Equal(image1Before, new ElementRect(image1.Position, image1.Size));
        Assert.Equal(frame1Before, LinkedGroupDocuments.PlacementOf(document, frame1));

        // Links, attachments and the Basic portrait's frame are unchanged; each link and gesture is one undo step.
        Assert.Equal(group1, session.GroupOf(CanvasItemRef.Component(frame1.Id)));
        Assert.Equal(group2, session.GroupOf(CanvasItemRef.Component(frame2.Id)));
        Assert.Equal(image1.Id, frame1.TargetElementId);
        Assert.Equal(image2.Id, frame2.TargetElementId);
        Assert.Equal(frameA, PlateComponentEditor.FindSlot(document, PlateComponentKind.PortraitFrame)!.Id);
        Assert.Equal(4, UndoDepth(session));
    }
}
