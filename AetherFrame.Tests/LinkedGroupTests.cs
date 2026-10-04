using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Two pictures, each with its own Portrait Frame attached by id, and the plan that draws them.</summary>
internal static class LinkedGroupDocuments
{
    internal static readonly Vector2 Image1Position = new(60f, 80f);
    internal static readonly Vector2 Image1Size = new(300f, 400f);
    internal static readonly Vector2 Image2Position = new(700f, 120f);
    internal static readonly Vector2 Image2Size = new(240f, 320f);

    /// <summary>A blank Plate with image 1 and image 2 and two Portrait Frames, frame 1 on image 1 and
    /// frame 2 on image 2. The frames are given in that order.</summary>
    internal static (ProfileDocument Document, ImageProfileElement Image1, ImageProfileElement Image2, PlateComponent Frame1, PlateComponent Frame2) TwoFramedPictures()
    {
        var document = BasicDocuments.Blank();
        var (image1, image2, frame1, frame2) = AddTwoFramedPictures(document);
        return (document, image1, image2, frame1, frame2);
    }

    internal static (ImageProfileElement Image1, ImageProfileElement Image2, PlateComponent Frame1, PlateComponent Frame2) AddTwoFramedPictures(ProfileDocument document)
    {
        var nextZ = document.Elements.Count == 0 ? 0 : document.Elements.Max(e => e.ZIndex) + 1;
        var image1 = new ImageProfileElement { Name = "Image 1", AssetId = Guid.NewGuid(), Position = Image1Position, Size = Image1Size, ZIndex = nextZ };
        var image2 = new ImageProfileElement { Name = "Image 2", AssetId = Guid.NewGuid(), Position = Image2Position, Size = Image2Size, ZIndex = nextZ + 1 };
        document.Elements.Add(image1);
        document.Elements.Add(image2);

        var frame1 = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameBrackets);
        frame1.TargetElementId = image1.Id;
        var frame2 = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameBrackets);
        frame2.TargetElementId = image2.Id;
        document.Components ??= [];
        document.Components.Add(frame1);
        document.Components.Add(frame2);
        return (image1, image2, frame1, frame2);
    }

    internal static List<PaintStep> StepsOf(List<PaintStep> plan, PlateComponent component) =>
        plan.Where(s => ReferenceEquals(s.Component, component)).ToList();

    internal static ComponentPlacement PlacementOf(ProfileDocument document, PlateComponent component) =>
        Assert.Single(StepsOf(ComponentDocuments.Plan(document), component)).Placement;

    /// <summary>What a plan draws, as plain values: each step's element id or component id, its placement,
    /// and the element's position and size. Two plans with the same picture compare equal.</summary>
    internal static List<string> Picture(ProfileDocument document) =>
        ComponentDocuments.Plan(document).Select(step => step.Element is { } element
            ? $"E {element.Id} {element.Position} {element.Size} {element.ZIndex} {(element as TextProfileElement)?.FontSize}"
            : $"C {step.Component!.Id} {step.Placement.Rect.Position} {step.Placement.Rect.Size} {step.Placement.RotationDegrees} {step.Placement.MirrorX} {step.Placement.MirrorY}").ToList();

    internal static void AssertNear(Vector2 expected, Vector2 actual, float tolerance = 0.01f)
    {
        Assert.True(Vector2.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}.");
    }
}

/// <summary>
/// Portrait Frames attached to a picture of the player's choosing, by its id (requirement 3): the exact
/// two pictures, two frames case, following each picture, and every Plate saved before keeping its frame
/// on the Basic portrait.
/// </summary>
public class PortraitFrameTargetTests
{
    [Fact]
    public void TwoPicturesTwoFrames_EachFrameIsDrawnOnItsOwnPicture_RightAfterIt()
    {
        var (document, image1, image2, frame1, frame2) = LinkedGroupDocuments.TwoFramedPictures();
        var plan = ComponentDocuments.Plan(document);

        var placement1 = LinkedGroupDocuments.PlacementOf(document, frame1);
        var placement2 = LinkedGroupDocuments.PlacementOf(document, frame2);
        Assert.Equal(new ElementRect(image1.Position, image1.Size), placement1.Rect);
        Assert.Equal(new ElementRect(image2.Position, image2.Size), placement2.Rect);

        // Each frame paints immediately after its own picture, so a picture never covers its own frame.
        Assert.Equal(plan.FindIndex(s => ReferenceEquals(s.Element, image1)) + 1, plan.FindIndex(s => ReferenceEquals(s.Component, frame1)));
        Assert.Equal(plan.FindIndex(s => ReferenceEquals(s.Element, image2)) + 1, plan.FindIndex(s => ReferenceEquals(s.Component, frame2)));
    }

    [Fact]
    public void MovingResizingOrTurningAPicture_KeepsItsFrameOnIt_AndTheOtherFrameWhereItWas()
    {
        var (document, image1, image2, frame1, frame2) = LinkedGroupDocuments.TwoFramedPictures();
        var frame2Before = LinkedGroupDocuments.PlacementOf(document, frame2);

        image1.Position = new Vector2(100f, 150f);
        image1.Size = new Vector2(180f, 260f);
        image1.RotationDegrees = 30f;

        var placement1 = LinkedGroupDocuments.PlacementOf(document, frame1);
        Assert.Equal(new ElementRect(image1.Position, image1.Size), placement1.Rect);
        Assert.Equal(30f, placement1.RotationDegrees);
        Assert.Equal(frame2Before, LinkedGroupDocuments.PlacementOf(document, frame2));
        Assert.Equal(new ElementRect(image2.Position, image2.Size), frame2Before.Rect);
    }

    [Fact]
    public void AFrameWithNoTarget_StillFollowsTheBasicPortrait_EvenBesideOtherPictures()
    {
        var document = ComponentDocuments.WithAnchors();
        var portrait = BasicSections.Find(document, ProfileElementRole.BasicPortrait)!;
        var (_, _, _, _) = LinkedGroupDocuments.AddTwoFramedPictures(document);
        var legacy = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameBrackets);
        document.Components!.Add(legacy);

        Assert.Equal(new ElementRect(portrait.Position, portrait.Size), LinkedGroupDocuments.PlacementOf(document, legacy).Rect);
    }

    [Fact]
    public void BasicPortraitSlot_IgnoresFramesAttachedToOtherPictures()
    {
        var document = ComponentDocuments.WithAnchors();
        var (_, _, frame1, frame2) = LinkedGroupDocuments.AddTwoFramedPictures(document);
        var catalog = BuiltInComponentCatalog.Instance;

        Assert.Null(PlateComponentEditor.FindSlot(document, PlateComponentKind.PortraitFrame));
        Assert.False(PlateComponentEditor.SetSlot(document, PlateComponentKind.PortraitFrame, null, catalog));
        Assert.Contains(frame1, document.Components!);
        Assert.Contains(frame2, document.Components!);

        // Picking a portrait frame in Basic adds one for the portrait and leaves both attached frames alone.
        Assert.True(PlateComponentEditor.SetSlot(document, PlateComponentKind.PortraitFrame, BuiltInComponentCatalog.PortraitFrameBrackets, catalog));
        var slot = PlateComponentEditor.FindSlot(document, PlateComponentKind.PortraitFrame);
        Assert.NotNull(slot);
        Assert.NotSame(frame1, slot);
        Assert.NotSame(frame2, slot);
        Assert.Null(slot!.TargetElementId);
    }

    [Fact]
    public void AttachingAFrameToTheBasicPortrait_KeepsItFollowingThePortraitRatherThanItsId()
    {
        var document = ComponentDocuments.WithAnchors();
        var portrait = BasicSections.Find(document, ProfileElementRole.BasicPortrait)!;
        var (_, _, frame1, _) = LinkedGroupDocuments.AddTwoFramedPictures(document);

        Assert.True(PlateComponentEditor.SetTarget(document, frame1.Id, portrait.Id));
        Assert.Null(frame1.TargetElementId);
        Assert.False(PlateComponentEditor.SetTarget(document, frame1.Id, portrait.Id));
        Assert.Equal(new ElementRect(portrait.Position, portrait.Size), LinkedGroupDocuments.PlacementOf(document, frame1).Rect);
    }

    [Fact]
    public void ADeletedOrHiddenTarget_DrawsNoFrame_AndBringingThePictureBackDrawsItAgain()
    {
        var (document, image1, _, frame1, frame2) = LinkedGroupDocuments.TwoFramedPictures();

        image1.Visible = false;
        Assert.Empty(LinkedGroupDocuments.StepsOf(ComponentDocuments.Plan(document), frame1));
        image1.Visible = true;

        document.Elements.Remove(image1);
        var plan = ComponentDocuments.Plan(document);
        Assert.Empty(LinkedGroupDocuments.StepsOf(plan, frame1));
        Assert.Single(LinkedGroupDocuments.StepsOf(plan, frame2));

        // Nothing on the canvas is hit where the orphaned frame was, and nothing throws.
        Assert.True(CanvasHitTest.Find(plan, LinkedGroupDocuments.Image1Position + new Vector2(2f), 1f).IsEmpty);

        document.Elements.Add(image1);
        Assert.Single(LinkedGroupDocuments.StepsOf(ComponentDocuments.Plan(document), frame1));
    }

    [Fact]
    public void SetTarget_AcceptsOnlyPicturesOfThePlate_AndFramesOrOverlays()
    {
        var (document, image1, image2, frame1, _) = LinkedGroupDocuments.TwoFramedPictures();
        var text = new TextProfileElement { Text = "Caption" };
        document.Elements.Add(text);
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        document.Components!.Add(ornament);

        Assert.False(PlateComponentEditor.SetTarget(document, frame1.Id, text.Id));
        Assert.False(PlateComponentEditor.SetTarget(document, frame1.Id, Guid.NewGuid()));
        Assert.False(PlateComponentEditor.SetTarget(document, ornament.Id, image1.Id));
        Assert.False(PlateComponentEditor.SetTarget(document, frame1.Id, image1.Id)); // unchanged
        Assert.Equal(image1.Id, frame1.TargetElementId);

        Assert.True(PlateComponentEditor.SetTarget(document, frame1.Id, image2.Id));
        Assert.Equal(new ElementRect(image2.Position, image2.Size), LinkedGroupDocuments.PlacementOf(document, frame1).Rect);
        Assert.True(PlateComponentEditor.SetTarget(document, frame1.Id, null));
        Assert.Null(frame1.TargetElementId);
    }

    [Fact]
    public void ALegacyPlate_LoadsWithoutTargetsOrLinks_AndSavesWithoutTheNewFields()
    {
        var legacy = ComponentDocuments.WithAnchors();
        legacy.Components = ComponentDocuments.OneOfEach();
        var json = PlateDocuments.ToJson(legacy);

        var reloaded = PlateDocuments.Materialize(JsonNode.Parse(json.ToJsonString())!.AsObject());
        Assert.All(reloaded.Components!, c =>
        {
            Assert.Null(c.TargetElementId);
            Assert.Null(c.LinkGroupId);
            Assert.False(c.Locked);
        });
        Assert.All(reloaded.Elements, e => Assert.Null(e.LinkGroupId));

        var saved = PlateDocuments.ToJson(reloaded).ToJsonString();
        Assert.DoesNotContain("TargetElementId", saved);
        Assert.DoesNotContain("LinkGroupId", saved);
        Assert.DoesNotContain("\"Locked\":true", saved);
        Assert.True(JsonNode.DeepEquals(json, JsonNode.Parse(saved)));

        // And it draws exactly as before: the frame on the Basic portrait.
        Assert.Equal(LinkedGroupDocuments.Picture(legacy), LinkedGroupDocuments.Picture(reloaded));
    }

    [Fact]
    public void TargetsLinksAndLocks_RoundTripThroughThePlateFile()
    {
        var (document, image1, image2, frame1, frame2) = LinkedGroupDocuments.TwoFramedPictures();
        var group = Guid.NewGuid();
        image1.LinkGroupId = group;
        frame1.LinkGroupId = group;
        frame2.Locked = true;

        var reloaded = ComponentDocuments.RoundTrip(document);

        Assert.Equal(image1.Id, reloaded.Components![0].TargetElementId);
        Assert.Equal(image2.Id, reloaded.Components[1].TargetElementId);
        Assert.Equal(group, reloaded.Components[0].LinkGroupId);
        Assert.True(reloaded.Components[1].Locked);
        Assert.Equal(group, reloaded.Elements.Single(e => e.Id == image1.Id).LinkGroupId);
        Assert.True(PlateComponent.ListsEqual(document.Components, reloaded.Components));
        Assert.Equal(LinkedGroupDocuments.Picture(document), LinkedGroupDocuments.Picture(reloaded));
    }

    [Fact]
    public void LinkIds_AreNeverPartOfThePicture()
    {
        var (document, image1, image2, frame1, frame2) = LinkedGroupDocuments.TwoFramedPictures();
        var before = LinkedGroupDocuments.Picture(document);

        LinkedGroups.Link(document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id), CanvasItemRef.Component(frame1.Id), CanvasItemRef.Component(frame2.Id)]);

        Assert.Equal(before, LinkedGroupDocuments.Picture(document));
    }
}

/// <summary>
/// Linked groups and direct manipulation of Components through the real editor session: selection,
/// one undo step per link, unlink and gesture, locks, transforms that keep the layout, attachments that
/// never move twice, and duplication (requirements 1, 2 and 4).
/// </summary>
public class LinkedGroupEditorTests
{
    private static async Task<(BasicHarness Harness, ImageProfileElement Image1, ImageProfileElement Image2, PlateComponent Frame1, PlateComponent Frame2)> TwoFramedPicturesAsync()
    {
        var harness = await BasicHarness.NewClassicAsync();
        var (image1, image2, frame1, frame2) = LinkedGroupDocuments.AddTwoFramedPictures(harness.Document);
        harness.Session.ClearHistory();
        return (harness, image1, image2, frame1, frame2);
    }

    private static List<PaintStep> Plan(BasicHarness harness) => ComponentDocuments.Plan(harness.Document);

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

    private static void Drag(EditorSession session, List<PaintStep> plan, Vector2 from, Vector2 to)
    {
        session.BeginSelectionDrag(plan, from);
        session.UpdateInteraction(Vector2.Lerp(from, to, 0.5f), snap: false, snapThreshold: 0f);
        session.UpdateInteraction(to, snap: false, snapThreshold: 0f);
        session.EndInteraction();
    }

    [Fact]
    public async Task OpeningAnotherPlate_MidGesture_LeavesThatPlateItsOwnContent()
    {
        var (harness, image1, image2, _, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        harness.Session.SetSelection([CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id)]);
        harness.Session.BeginSelectionDrag(Plan(harness), image1.Position);
        harness.Session.UpdateInteraction(image1.Position + new Vector2(30f), snap: false, snapThreshold: 0f);

        var created = await harness.Library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, starter: new PlateStarterContent(null));
        harness.Profiles.OpenPlate(created.PlateId);
        var other = harness.Profiles.CurrentProfile!;
        var idsBefore = other.Elements.Select(e => e.Id).ToList();
        var componentsBefore = other.Components?.Count ?? 0;
        harness.Session.SyncWithCurrentProfile();

        Assert.Equal(idsBefore, other.Elements.Select(e => e.Id).ToList());
        Assert.Equal(componentsBefore, other.Components?.Count ?? 0);
        Assert.DoesNotContain(other.Elements, e => e.Id == image1.Id || e.Id == image2.Id);
        Assert.Equal(ElementInteractionKind.None, harness.Session.ActiveInteraction);
    }

    [Fact]
    public async Task AClickWithoutMoving_ChangesNothing_EvenAnOutOfRangeComponent()
    {
        var (harness, _, _, frame1, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        frame1.Scale = 6f;
        frame1.Opacity = 1.5f;
        harness.Session.SelectComponent(frame1.Id);
        var center = LinkedGroupDocuments.PlacementOf(harness.Document, frame1).Rect.Position;

        harness.Session.BeginSelectionDrag(Plan(harness), center);
        harness.Session.UpdateInteraction(center, snap: false, snapThreshold: 0f);
        harness.Session.EndInteraction();

        Assert.Equal(6f, frame1.Scale);
        Assert.Equal(1.5f, frame1.Opacity);
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public async Task DeleteDuplicateNudgeAndLink_WaitUntilTheGestureEnds()
    {
        var (harness, image1, image2, _, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        harness.Session.SetSelection([CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id)]);
        var count = harness.Document.Elements.Count;
        harness.Session.BeginSelectionDrag(Plan(harness), image1.Position);
        harness.Session.UpdateInteraction(image1.Position + new Vector2(20f), snap: false, snapThreshold: 0f);

        harness.Session.DeleteSelection();
        harness.Session.DuplicateSelection();
        harness.Session.NudgeSelected(new Vector2(5f, 0f));
        harness.Session.LinkSelection();
        harness.Session.EndInteraction();

        Assert.Equal(count, harness.Document.Elements.Count);
        Assert.Null(image1.LinkGroupId);
        LinkedGroupDocuments.AssertNear(LinkedGroupDocuments.Image1Position + new Vector2(20f), image1.Position);
        Assert.Equal(1, UndoDepth(harness.Session));
    }

    [Fact]
    public async Task AComponentThatIsNotDrawn_HasNoHandles_ButStillNudges()
    {
        var (harness, image1, _, frame1, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        image1.Visible = false;
        harness.Session.SelectComponent(frame1.Id);

        var gesture = harness.Session.PreviewSelectionGesture(Plan(harness))!;
        Assert.False(gesture.HasHandles);
        harness.Session.BeginSelectionResize(Plan(harness), 2, Vector2.Zero);
        Assert.Equal(ElementInteractionKind.None, harness.Session.ActiveInteraction);

        var offset = frame1.Offset;
        harness.Session.NudgeSelected(new Vector2(1f, 0f));
        Assert.Equal(offset + new Vector2(1f, 0f), harness.Document.Components!.Single(c => c.Id == frame1.Id).Offset);
    }

    [Fact]
    public async Task SeveralComponentsThatAreNotDrawn_HaveNoHandles_ButStillNudge()
    {
        var (harness, image1, image2, frame1, frame2) = await TwoFramedPicturesAsync();
        using var _h = harness;
        image1.Visible = false;
        image2.Visible = false;
        harness.Session.SetSelection([CanvasItemRef.Component(frame1.Id), CanvasItemRef.Component(frame2.Id)]);

        Assert.False(harness.Session.PreviewSelectionGesture(Plan(harness))!.HasHandles);
        harness.Session.BeginSelectionResize(Plan(harness), 2, Vector2.Zero);
        Assert.Equal(ElementInteractionKind.None, harness.Session.ActiveInteraction);

        var offset = frame2.Offset;
        harness.Session.NudgeSelected(new Vector2(0f, 1f));
        Assert.Equal(offset + new Vector2(0f, 1f), harness.Document.Components!.Single(c => c.Id == frame2.Id).Offset);
    }

    [Fact]
    public async Task OneSelectedCornerOrnament_SnapsByTheCornerBeingDragged()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        ornament.Corners = CornerMask.All;
        harness.Document.Components ??= [];
        harness.Document.Components.Add(ornament);
        harness.Session.SelectComponent(ornament.Id);

        var plan = Plan(harness);
        var placements = LinkedGroupDocuments.StepsOf(plan, ornament);
        Assert.True(placements.Count > 1);
        var gesture = harness.Session.PreviewSelectionGesture(plan, 1)!;
        var expected = placements[1].Placement.Rect;
        LinkedGroupDocuments.AssertNear(expected.Position, gesture.Bounds.Min);
        LinkedGroupDocuments.AssertNear(expected.Position + expected.Size, gesture.Bounds.Max);
    }

    [Fact]
    public async Task Linking_ChangesNothingDrawn_AndIsOneUndoStep()
    {
        var (harness, image1, image2, frame1, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        var before = LinkedGroupDocuments.Picture(harness.Document);
        var order = harness.Document.Elements.Select(e => (e.Id, e.ZIndex)).ToList();

        harness.Session.SetSelection([CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id), CanvasItemRef.Component(frame1.Id)]);
        Assert.Null(harness.Session.LinkSelectionBlockedReason);
        harness.Session.LinkSelection();

        Assert.NotNull(image1.LinkGroupId);
        Assert.Equal(image1.LinkGroupId, image2.LinkGroupId);
        Assert.Equal(image1.LinkGroupId, harness.Document.Components!.Single(c => c.Id == frame1.Id).LinkGroupId);
        Assert.Equal(before, LinkedGroupDocuments.Picture(harness.Document));
        Assert.Equal(order, harness.Document.Elements.Select(e => (e.Id, e.ZIndex)).ToList());
        Assert.Equal(1, UndoDepth(harness.Session));
        Assert.Equal(image1.LinkGroupId, harness.Session.SelectedGroupId);

        harness.Session.Undo();
        Assert.All(harness.Document.Elements, e => Assert.Null(e.LinkGroupId));
        harness.Session.Redo();
        Assert.Equal(3, LinkedGroups.MemberCount(harness.Document, harness.Document.Elements.Single(e => e.Id == image1.Id).LinkGroupId!.Value));
    }

    [Fact]
    public async Task Unlinking_ChangesNothingDrawn_AndIsOneUndoStep()
    {
        var (harness, image1, image2, _, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        harness.Session.SetSelection([CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id)]);
        harness.Session.LinkSelection();
        harness.Session.ClearHistory();
        var before = LinkedGroupDocuments.Picture(harness.Document);

        harness.Session.UnlinkSelection();

        Assert.Null(harness.Document.Elements.Single(e => e.Id == image1.Id).LinkGroupId);
        Assert.Null(harness.Document.Elements.Single(e => e.Id == image2.Id).LinkGroupId);
        Assert.Equal(before, LinkedGroupDocuments.Picture(harness.Document));
        Assert.Equal(1, UndoDepth(harness.Session));
    }

    [Fact]
    public async Task Linking_IsRefused_ForOneThing_ABackground_OrAnOrnamentInSeveralCorners()
    {
        var (harness, image1, _, _, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        harness.Document.Components!.Add(ornament);

        harness.Session.SetSelection([CanvasItemRef.Element(image1.Id)]);
        Assert.NotNull(harness.Session.LinkSelectionBlockedReason);

        harness.Session.SetSelection([CanvasItemRef.Element(image1.Id), CanvasItemRef.Component(ornament.Id)]);
        Assert.Contains("corner", harness.Session.LinkSelectionBlockedReason);
        harness.Session.LinkSelection();
        Assert.Null(ornament.LinkGroupId);
        Assert.False(harness.Session.CanUndo);

        ornament.Corners = CornerMask.BottomRight;
        Assert.Null(harness.Session.LinkSelectionBlockedReason);
    }

    [Fact]
    public async Task AClickOnALinkedMember_SelectsTheGroup_ASecondPicksTheMember_AndCtrlTogglesTheGroup()
    {
        var (harness, image1, image2, frame1, frame2) = await TwoFramedPicturesAsync();
        using var _h = harness;
        var group = LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Component(frame1.Id)]);

        harness.Session.SelectOnCanvas(CanvasItemRef.Element(image1.Id), additive: false);
        Assert.Equal(group, harness.Session.SelectedGroupId);
        Assert.Equal(2, harness.Session.SelectedItems.Count);

        // Editing one member on its own (from Layers), then clicking another member, edits that one.
        harness.Session.SelectFromList(CanvasItemRef.Element(image1.Id), additive: false);
        Assert.Equal(image1.Id, harness.Session.SelectedElementId);
        harness.Session.SelectOnCanvas(CanvasItemRef.Component(frame1.Id), additive: false);
        Assert.Equal(frame1.Id, harness.Session.SelectedComponentId);

        // Ctrl adds the unlinked picture, then takes the whole group out again.
        harness.Session.Select(null);
        harness.Session.SelectOnCanvas(CanvasItemRef.Element(image1.Id), additive: false);
        harness.Session.SelectOnCanvas(CanvasItemRef.Element(image2.Id), additive: true);
        Assert.Equal(3, harness.Session.SelectedItems.Count);
        Assert.Null(harness.Session.SelectedGroupId);
        harness.Session.SelectOnCanvas(CanvasItemRef.Component(frame1.Id), additive: true);
        Assert.Equal(image2.Id, harness.Session.SelectedElementId);
        Assert.DoesNotContain(CanvasItemRef.Component(frame2.Id), harness.Session.SelectedItems);

        // Selecting changes nothing on the Plate.
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public async Task DraggingAGroup_MovesEveryMemberTogether_AsOneUndoStep()
    {
        var (harness, image1, image2, _, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        var plateFrame = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameDouble);
        harness.Document.Components!.Add(plateFrame);
        LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id), CanvasItemRef.Component(plateFrame.Id)]);
        var frameBefore = LinkedGroupDocuments.PlacementOf(harness.Document, plateFrame);
        var picture = LinkedGroupDocuments.Picture(harness.Document);

        harness.Session.SelectOnCanvas(CanvasItemRef.Element(image1.Id), additive: false);
        var delta = new Vector2(25f, -30f);
        Drag(harness.Session, Plan(harness), image1.Position + new Vector2(10f), image1.Position + new Vector2(10f) + delta);

        Assert.Equal(LinkedGroupDocuments.Image1Position + delta, image1.Position);
        Assert.Equal(LinkedGroupDocuments.Image2Position + delta, image2.Position);
        Assert.Equal(LinkedGroupDocuments.Image1Size, image1.Size);
        Assert.Equal(delta, plateFrame.Offset);
        LinkedGroupDocuments.AssertNear(frameBefore.Rect.Position + delta, LinkedGroupDocuments.PlacementOf(harness.Document, plateFrame).Rect.Position);
        Assert.Equal(1, UndoDepth(harness.Session));

        harness.Session.Undo();
        Assert.Equal(picture, LinkedGroupDocuments.Picture(harness.Document));
    }

    [Fact]
    public async Task AFrameAttachedToAPictureInTheSameGroup_MovesOnce_WithThePicture()
    {
        var (harness, image1, _, frame1, frame2) = await TwoFramedPicturesAsync();
        using var _h = harness;
        LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Component(frame1.Id)]);
        var frame2Before = LinkedGroupDocuments.PlacementOf(harness.Document, frame2);

        harness.Session.SelectOnCanvas(CanvasItemRef.Element(image1.Id), additive: false);
        var delta = new Vector2(40f, 30f);
        Drag(harness.Session, Plan(harness), image1.Position + new Vector2(5f), image1.Position + new Vector2(5f) + delta);

        // The frame's own Offset is untouched: it moved exactly as far as its picture, not twice as far.
        Assert.Equal(Vector2.Zero, frame1.Offset);
        Assert.Equal(new ElementRect(image1.Position, image1.Size), LinkedGroupDocuments.PlacementOf(harness.Document, frame1).Rect);
        Assert.Equal(LinkedGroupDocuments.Image1Position + delta, image1.Position);
        Assert.Equal(frame2Before, LinkedGroupDocuments.PlacementOf(harness.Document, frame2));
    }

    [Fact]
    public async Task ResizingAGroup_ScalesItAroundTheOppositeCorner_KeepingTheLayout_AsOneUndoStep()
    {
        var (harness, image1, image2, frame1, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        var caption = new TextProfileElement { Text = "Caption", FontSize = 20f, Position = new Vector2(400f, 500f), Size = new Vector2(200f, 40f), ZIndex = 90 };
        harness.Document.Elements.Add(caption);
        frame1.Offset = new Vector2(4f, 6f);
        LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id), CanvasItemRef.Element(caption.Id), CanvasItemRef.Component(frame1.Id)]);
        harness.Session.SelectOnCanvas(CanvasItemRef.Element(caption.Id), additive: false);

        var gesture = harness.Session.PreviewSelectionGesture(Plan(harness))!;
        var anchor = gesture.Corners[0];
        var handle = gesture.Corners[2];
        const float S = 0.5f;
        var image1Center = image1.Position + (image1.Size / 2f);
        var image2Center = image2.Position + (image2.Size / 2f);

        harness.Session.BeginSelectionResize(Plan(harness), 2, handle);
        harness.Session.UpdateInteraction(anchor + ((handle - anchor) * S), snap: false, snapThreshold: 0f);
        harness.Session.EndInteraction();

        LinkedGroupDocuments.AssertNear(LinkedGroupDocuments.Image1Size * S, image1.Size);
        LinkedGroupDocuments.AssertNear(anchor + ((image1Center - anchor) * S), image1.Position + (image1.Size / 2f));
        LinkedGroupDocuments.AssertNear(anchor + ((image2Center - anchor) * S), image2.Position + (image2.Size / 2f));
        Assert.Equal(10f, caption.FontSize, 3);

        // The attached frame still sits on its picture, its own distance from it scaled with the group.
        LinkedGroupDocuments.AssertNear(new Vector2(2f, 3f), frame1.Offset);
        Assert.Equal(1f, frame1.Scale);
        var placement = LinkedGroupDocuments.PlacementOf(harness.Document, frame1);
        LinkedGroupDocuments.AssertNear(image1.Position + (image1.Size / 2f) + frame1.Offset, placement.Rect.Position + (placement.Rect.Size / 2f));
        Assert.Equal(1, UndoDepth(harness.Session));
    }

    [Fact]
    public async Task AGroupResize_StopsWhereAnyMemberWould_SoTheLayoutStaysExact()
    {
        var (harness, image1, image2, _, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        var small = new ImageProfileElement { AssetId = Guid.NewGuid(), Position = new Vector2(500f, 500f), Size = new Vector2(40f, 40f), ZIndex = 50 };
        harness.Document.Elements.Add(small);
        LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(small.Id)]);
        harness.Session.SelectOnCanvas(CanvasItemRef.Element(small.Id), additive: false);
        var gesture = harness.Session.PreviewSelectionGesture(Plan(harness))!;

        harness.Session.BeginSelectionResize(Plan(harness), 2, gesture.Corners[2]);
        harness.Session.UpdateInteraction(gesture.Corners[0] + new Vector2(1f), snap: false, snapThreshold: 0f);
        harness.Session.EndInteraction();

        // The small picture reaches the minimum size; the big one shrinks by the same factor, no more.
        Assert.Equal(EditorSession.MinElementWidth, small.Size.X, 3);
        LinkedGroupDocuments.AssertNear(LinkedGroupDocuments.Image1Size * 0.5f, image1.Size);
        Assert.Equal(LinkedGroupDocuments.Image2Size, image2.Size);
    }

    [Fact]
    public async Task ALockedMember_KeepsTheWholeGroupStill()
    {
        var (harness, image1, image2, frame1, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id), CanvasItemRef.Component(frame1.Id)]);
        frame1.Locked = true;
        harness.Session.SelectOnCanvas(CanvasItemRef.Element(image1.Id), additive: false);

        Assert.NotNull(harness.Session.SelectionTransformBlockedReason);
        Drag(harness.Session, Plan(harness), image1.Position + new Vector2(5f), image1.Position + new Vector2(80f));
        harness.Session.NudgeSelected(new Vector2(5f, 0f));

        Assert.Equal(LinkedGroupDocuments.Image1Position, image1.Position);
        Assert.Equal(LinkedGroupDocuments.Image2Position, image2.Position);
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public async Task NudgingAGroup_MovesItTogether_AsOneUndoStep()
    {
        var (harness, image1, image2, frame1, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id), CanvasItemRef.Component(frame1.Id)]);
        harness.Session.SelectOnCanvas(CanvasItemRef.Element(image2.Id), additive: false);

        harness.Session.NudgeSelected(new Vector2(0f, 10f));

        Assert.Equal(LinkedGroupDocuments.Image1Position + new Vector2(0f, 10f), image1.Position);
        Assert.Equal(LinkedGroupDocuments.Image2Position + new Vector2(0f, 10f), image2.Position);
        Assert.Equal(Vector2.Zero, frame1.Offset);
        Assert.Equal(1, UndoDepth(harness.Session));
    }

    [Fact]
    public async Task AComponent_MovesByItsOffset_AndResizesAroundItsOppositeCorner()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        ornament.Corners = CornerMask.BottomRight;
        harness.Document.Components = [ornament];
        harness.Session.ClearHistory();
        var start = LinkedGroupDocuments.PlacementOf(harness.Document, ornament);
        var center = start.Rect.Position + (start.Rect.Size / 2f);

        // Bottom-right: its Offset is turned on both axes, but it follows the mouse all the same.
        harness.Session.SelectOnCanvas(CanvasItemRef.Component(ornament.Id), additive: false);
        Drag(harness.Session, Plan(harness), center, center + new Vector2(-30f, -20f));
        var moved = LinkedGroupDocuments.PlacementOf(harness.Document, ornament);
        LinkedGroupDocuments.AssertNear(start.Rect.Position + new Vector2(-30f, -20f), moved.Rect.Position);
        Assert.Equal(new Vector2(30f, 20f), ornament.Offset);

        // Its top-left handle doubles it; the bottom-right corner stays put.
        var gesture = harness.Session.PreviewSelectionGesture(Plan(harness))!;
        var fixedCorner = gesture.Corners[2];
        harness.Session.BeginSelectionResize(Plan(harness), 0, gesture.Corners[0]);
        harness.Session.UpdateInteraction(fixedCorner - (moved.Rect.Size * 2f), snap: false, snapThreshold: 0f);
        harness.Session.EndInteraction();

        var resized = LinkedGroupDocuments.PlacementOf(harness.Document, ornament);
        Assert.Equal(2f, ornament.Scale, 3);
        LinkedGroupDocuments.AssertNear(moved.Rect.Size * 2f, resized.Rect.Size);
        LinkedGroupDocuments.AssertNear(fixedCorner, resized.Rect.Position + resized.Rect.Size);
        Assert.Equal(2, UndoDepth(harness.Session));
    }

    [Fact]
    public async Task ALockedComponent_IsSelectableButNeverMoves()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameDouble);
        harness.Document.Components = [frame];
        harness.Session.ClearHistory();

        harness.Session.SetComponentLocked(frame.Id, true);
        Assert.True(harness.Document.Components[0].Locked);
        Assert.Equal(1, UndoDepth(harness.Session));

        harness.Session.SelectComponent(frame.Id);
        Drag(harness.Session, Plan(harness), new Vector2(20f), new Vector2(120f));
        Assert.Equal(Vector2.Zero, harness.Document.Components[0].Offset);
        Assert.Equal(1, UndoDepth(harness.Session));
    }

    [Fact]
    public async Task CancellingAGesture_PutsEverythingBack_WithNothingRecorded()
    {
        var (harness, image1, image2, _, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id)]);
        harness.Session.SelectOnCanvas(CanvasItemRef.Element(image1.Id), additive: false);
        var before = LinkedGroupDocuments.Picture(harness.Document);

        harness.Session.BeginSelectionDrag(Plan(harness), image1.Position);
        harness.Session.UpdateInteraction(image1.Position + new Vector2(50f), snap: false, snapThreshold: 0f);
        Assert.True(harness.Session.IsDirty);
        harness.Session.CancelInteraction();

        Assert.Equal(before, LinkedGroupDocuments.Picture(harness.Document));
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public async Task DuplicatingAGroup_MakesAnIndependentGroup_WithItsFrameOnTheCopiedPicture()
    {
        var (harness, image1, image2, frame1, frame2) = await TwoFramedPicturesAsync();
        using var _h = harness;
        var group = LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Component(frame1.Id)]);
        harness.Session.SelectGroup(group);
        var originals = LinkedGroupDocuments.Picture(harness.Document);

        harness.Session.DuplicateSelection();

        var copies = harness.Session.SelectedItems;
        Assert.Equal(2, copies.Count);
        var copiedImage = harness.Document.Elements.Single(e => e.Id == copies.Single(c => !c.IsComponent).Id);
        var copiedFrame = harness.Document.Components!.Single(c => c.Id == copies.Single(c => c.IsComponent).Id);
        Assert.NotEqual(image1.Id, copiedImage.Id);
        Assert.NotEqual(frame1.Id, copiedFrame.Id);
        Assert.NotNull(copiedImage.LinkGroupId);
        Assert.NotEqual(group, copiedImage.LinkGroupId);
        Assert.Equal(copiedImage.LinkGroupId, copiedFrame.LinkGroupId);
        Assert.Equal(copiedImage.Id, copiedFrame.TargetElementId);
        Assert.Equal(new ElementRect(copiedImage.Position, copiedImage.Size), LinkedGroupDocuments.PlacementOf(harness.Document, copiedFrame).Rect);
        Assert.True(copiedImage.ZIndex > harness.Document.Elements.Where(e => e.Id != copiedImage.Id).Max(e => e.ZIndex));

        // The originals are exactly as they were, frame 2 included.
        Assert.Equal(image1.Id, frame1.TargetElementId);
        Assert.Equal(image2.Id, frame2.TargetElementId);
        Assert.True(originals.All(LinkedGroupDocuments.Picture(harness.Document).Contains));

        // Moving the copy never moves the original.
        harness.Session.NudgeSelected(new Vector2(10f, 0f));
        Assert.Equal(LinkedGroupDocuments.Image1Position, image1.Position);
        Assert.Equal(new ElementRect(image1.Position, image1.Size), LinkedGroupDocuments.PlacementOf(harness.Document, frame1).Rect);

        Assert.Equal(2, UndoDepth(harness.Session));
        harness.Session.Undo();
        harness.Session.Undo();
        Assert.Equal(originals, LinkedGroupDocuments.Picture(harness.Document));
    }

    [Fact]
    public async Task DuplicatingAGroupWithTheBasicPortraitAndItsFrame_AttachesTheCopiedFrameToTheCopy()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var portrait = new ImageProfileElement { Role = ProfileElementRole.BasicPortrait, AssetId = Guid.NewGuid(), Position = new Vector2(40f, 40f), Size = new Vector2(300f, 500f) };
        harness.Document.Elements.Add(portrait);
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameBrackets);
        harness.Document.Components = [frame];
        var group = LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(portrait.Id), CanvasItemRef.Component(frame.Id)]);
        harness.Session.ClearHistory();
        harness.Session.SelectGroup(group);

        harness.Session.DuplicateSelection();

        var copiedPortrait = harness.Document.Elements.Single(e => e.Id == harness.Session.SelectedItems.Single(i => !i.IsComponent).Id);
        var copiedFrame = harness.Document.Components!.Single(c => c.Id == harness.Session.SelectedItems.Single(i => i.IsComponent).Id);
        Assert.Equal(ProfileElementRole.None, copiedPortrait.Role);
        Assert.Equal(copiedPortrait.Id, copiedFrame.TargetElementId);
        Assert.Null(frame.TargetElementId); // the original keeps following the Basic portrait
        Assert.Equal(new ElementRect(portrait.Position, portrait.Size), LinkedGroupDocuments.PlacementOf(harness.Document, frame).Rect);
        Assert.Equal(new ElementRect(copiedPortrait.Position, copiedPortrait.Size), LinkedGroupDocuments.PlacementOf(harness.Document, copiedFrame).Rect);
    }

    [Fact]
    public async Task DeletingALinkedPicture_LeavesItsFrameUndrawn_AndUndoBringsBothBack()
    {
        var (harness, image1, _, frame1, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        var before = LinkedGroupDocuments.Picture(harness.Document);
        harness.Session.Select(image1.Id);

        harness.Session.DeleteSelection();

        Assert.Empty(LinkedGroupDocuments.StepsOf(Plan(harness), frame1));
        Assert.Equal(image1.Id, harness.Document.Components!.Single(c => c.Id == frame1.Id).TargetElementId);

        harness.Session.Undo();
        Assert.Equal(before, LinkedGroupDocuments.Picture(harness.Document));
    }

    [Fact]
    public async Task DeletingAGroup_RemovesEveryMember_AsOneUndoStep()
    {
        var (harness, image1, image2, frame1, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        var group = LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id), CanvasItemRef.Component(frame1.Id)]);
        var before = LinkedGroupDocuments.Picture(harness.Document);
        harness.Session.SelectGroup(group);

        harness.Session.DeleteSelection();

        Assert.DoesNotContain(harness.Document.Elements, e => e.Id == image1.Id || e.Id == image2.Id);
        Assert.DoesNotContain(harness.Document.Components!, c => c.Id == frame1.Id);
        Assert.Empty(harness.Session.SelectedItems);
        Assert.Equal(1, UndoDepth(harness.Session));
        harness.Session.Undo();
        Assert.Equal(before, LinkedGroupDocuments.Picture(harness.Document));
    }

    [Fact]
    public async Task AttachingAFrame_IsOneUndoStep_AndKeepsItsOwnOffset()
    {
        var (harness, image1, image2, frame1, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        frame1.Offset = new Vector2(3f, 3f);

        harness.Session.SetComponentTarget(frame1.Id, image2.Id);

        var placement = LinkedGroupDocuments.PlacementOf(harness.Document, frame1);
        Assert.Equal(new Vector2(3f, 3f), frame1.Offset);
        LinkedGroupDocuments.AssertNear(image2.Position + new Vector2(3f), placement.Rect.Position);
        Assert.Equal(1, UndoDepth(harness.Session));
        harness.Session.Undo();
        Assert.Equal(image1.Id, harness.Document.Components!.Single(c => c.Id == frame1.Id).TargetElementId);
    }

    [Fact]
    public async Task DuplicatingOneLinkedElement_GivesAnUnlinkedCopy()
    {
        var (harness, image1, image2, _, _) = await TwoFramedPicturesAsync();
        using var _h = harness;
        var group = LinkedGroups.Link(harness.Document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Element(image2.Id)]);

        harness.Session.DuplicateElement(image1.Id);

        var copy = harness.Document.Elements.Single(e => e.Id == harness.Session.SelectedElementId);
        Assert.Null(copy.LinkGroupId);
        Assert.Equal(2, LinkedGroups.MemberCount(harness.Document, group));
    }

    [Fact]
    public async Task OpeningALinkedPlate_ChangesNothing_AndEverySurvivesSaveAndReopen()
    {
        var (harness, image1, image2, frame1, frame2) = await TwoFramedPicturesAsync();
        using var _h = harness;
        harness.Session.SetSelection([CanvasItemRef.Element(image1.Id), CanvasItemRef.Component(frame1.Id)]);
        harness.Session.LinkSelection();
        harness.Session.SetComponentLocked(frame2.Id, true);
        Assert.True(await harness.Session.SaveProfileAsync());
        harness.Session.SyncWithCurrentProfile();

        var json = PlateDocuments.ToJson(harness.Document).ToJsonString();
        using var reopened = await BasicHarness.OpenJsonAsync(json, harness.Document.ProfileId);
        reopened.Session.SyncWithCurrentProfile();

        Assert.False(reopened.Session.IsDirty);
        Assert.Equal(LinkedGroupDocuments.Picture(harness.Document), LinkedGroupDocuments.Picture(reopened.Document));
        var reopenedImage = reopened.Document.Elements.Single(e => e.Id == image1.Id);
        Assert.Equal(image1.LinkGroupId, reopenedImage.LinkGroupId);
        reopened.Session.SelectOnCanvas(CanvasItemRef.Element(image1.Id), additive: false);
        Assert.Equal(image1.LinkGroupId, reopened.Session.SelectedGroupId);
        Assert.True(reopened.Document.Components!.Single(c => c.Id == frame2.Id).Locked);
        Assert.Equal(image2.Id, reopened.Document.Components!.Single(c => c.Id == frame2.Id).TargetElementId);
    }
}

/// <summary>Links and frame targets through the Plate Library's copies: Plate duplication, Templates and packages.</summary>
public class LinkedGroupLibraryTests
{
    private static ProfileDocument Linked(ProfileDocument document, Guid image1Asset, Guid image2Asset)
    {
        var (image1, image2, frame1, _) = LinkedGroupDocuments.AddTwoFramedPictures(document);
        image1.AssetId = image1Asset;
        image2.AssetId = image2Asset;
        LinkedGroups.Link(document, [CanvasItemRef.Element(image1.Id), CanvasItemRef.Component(frame1.Id)]);
        return document;
    }

    private static void AssertSameLinks(ProfileDocument expected, ProfileDocument actual)
    {
        Assert.Equal(expected.Elements.Select(e => (e.Id, e.LinkGroupId)), actual.Elements.Select(e => (e.Id, e.LinkGroupId)));
        Assert.Equal(expected.Components!.Select(c => (c.Id, c.LinkGroupId, c.TargetElementId)), actual.Components!.Select(c => (c.Id, c.LinkGroupId, c.TargetElementId)));

        // Every frame still lands on its own picture.
        foreach (var component in actual.Components!.Where(c => c.TargetElementId is not null))
        {
            var target = actual.Elements.Single(e => e.Id == component.TargetElementId);
            Assert.Equal(new ElementRect(target.Position, target.Size), LinkedGroupDocuments.PlacementOf(actual, component).Rect);
        }
    }

    [Fact]
    public async Task DuplicatingAPlate_KeepsItsLinksAndFrameTargets()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Linked");
        var document = Linked(library.OpenDocumentForEditing(created.PlateId), Guid.NewGuid(), Guid.NewGuid());
        await library.SavePlateDocumentAsync(document);

        var copyId = await library.DuplicatePlateAsync(created.PlateId);

        AssertSameLinks(library.OpenDocumentForEditing(created.PlateId), library.OpenDocumentForEditing(copyId));
    }

    [Fact]
    public async Task APlateFromATemplate_KeepsItsLinksAndFrameTargets()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var created = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Linked");
        var document = Linked(fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId), Guid.NewGuid(), Guid.NewGuid());
        await fixture.PlateLibrary.SavePlateDocumentAsync(document);

        var templateId = await templates.SaveAsTemplateAsync(created.PlateId, "Linked");
        var fromTemplate = await templates.InstantiateAsync(templateId, null);

        AssertSameLinks(document, templates.GetSavedDocument(templateId)!);
        AssertSameLinks(document, fixture.PlateLibrary.OpenDocumentForEditing(fromTemplate.PlateId));
    }

    [Fact]
    public async Task APackage_KeepsItsLinksAndFrameTargets()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var image1 = fixture.AddImage(TestImages.Png(64, 64), "one.png");
        var image2 = fixture.AddImage(TestImages.Png(64, 64), "two.png");
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Linked");
        var document = Linked(library.OpenDocumentForEditing(created.PlateId), image1, image2);
        await library.SavePlateDocumentAsync(document);

        var path = fixture.Export(packages, created.PlateId);
        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport, string.Join("; ", staged.Diagnostics.Errors));
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded, result.Error?.ToString());

        AssertSameLinks(document, library.OpenDocumentForEditing(result.PlateId));
    }
}
