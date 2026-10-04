using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.UI.Rendering;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Selection and linked-group half of <see cref="EditorSession"/>: selecting several elements and
/// Components (Ctrl+click, Layers), linked groups (<see cref="LinkedGroups"/>), and moving or resizing a
/// selection of several things, or one Component, from the canvas (<see cref="CanvasGesture"/>).
///
/// <para><b>Groups.</b> A click on the canvas on a linked member selects its whole group; a second
/// click on a member of the selected group (or a double-click, or picking it in Layers) selects just
/// that member, to edit it on its own, crop and image included. Moving or resizing the group moves all
/// of it. Linking, unlinking and every completed gesture are one undo step each, recorded through whole
/// document snapshots (<see cref="ApplyDocumentEdit"/>), and only change what they say: linking never
/// moves anything.</para>
/// </summary>
internal sealed partial class EditorSession
{
    private readonly List<CanvasItemRef> selection = new();
    private readonly List<CanvasItemRef> scratchItems = new();

    // A move or resize of a selection of several things, or of one Component, in progress; runtime only.
    private CanvasGesture? itemsGesture;
    private ProfileService.DocumentState? itemsGestureBefore;
    private int itemsGestureCorner;

    /// <summary>Everything selected, in the order it was selected (an element or a Component each).</summary>
    internal IReadOnlyList<CanvasItemRef> SelectedItems => selection;

    internal bool IsSelected(CanvasItemRef item) => selection.Contains(item);

    /// <summary>The linked group the selection is, exactly (all of its members, nothing else), or null.</summary>
    internal Guid? SelectedGroupId
    {
        get
        {
            if (selection.Count < 2 || profileService.CurrentProfile is not { } profile || LinkedGroups.GroupOf(profile, selection[0]) is not { } groupId)
            {
                return null;
            }

            foreach (var item in selection)
            {
                if (LinkedGroups.GroupOf(profile, item) != groupId)
                {
                    return null;
                }
            }

            return LinkedGroups.MemberCount(profile, groupId) == selection.Count ? groupId : null;
        }
    }

    /// <summary>The linked group <paramref name="item"/> is a member of, or null.</summary>
    internal Guid? GroupOf(CanvasItemRef item) => profileService.CurrentProfile is { } profile ? LinkedGroups.GroupOf(profile, item) : null;

    /// <summary>
    /// A click on <paramref name="item"/> on the canvas. A linked member selects its whole group, unless
    /// one member of that group is already selected on its own (then the clicked member is, to go on
    /// editing members). With <paramref name="additive"/> (Ctrl), the item, or its whole group, is added
    /// to the selection or taken out of it.
    /// </summary>
    internal void SelectOnCanvas(CanvasItemRef item, bool additive)
    {
        if (profileService.CurrentProfile is not { } profile || !LinkedGroups.Exists(profile, item))
        {
            return;
        }

        var group = LinkedGroups.GroupOf(profile, item);
        scratchItems.Clear();
        if (group is { } groupId)
        {
            LinkedGroups.Members(profile, groupId, scratchItems);
        }
        else
        {
            scratchItems.Add(item);
        }

        if (additive)
        {
            var next = new List<CanvasItemRef>(selection);
            if (scratchItems.TrueForAll(selection.Contains))
            {
                next.RemoveAll(scratchItems.Contains);
            }
            else
            {
                foreach (var member in scratchItems)
                {
                    if (!next.Contains(member))
                    {
                        next.Add(member);
                    }
                }
            }

            SetSelection(next);
            return;
        }

        // Editing members of a group one at a time: a click on another member selects that member.
        if (group is not null && selection.Count == 1 && LinkedGroups.GroupOf(profile, selection[0]) == group)
        {
            SetSelection([item]);
            return;
        }

        // A click on something already in a selection of several keeps it (to drag them all).
        if (selection.Count > 1 && selection.Contains(item))
        {
            return;
        }

        SetSelection(scratchItems);
    }

    /// <summary>A pick in a list (Layers): <paramref name="item"/> on its own, even inside a linked group
    /// (the explicit way to edit one member); with <paramref name="additive"/> (Ctrl), added or taken out.</summary>
    internal void SelectFromList(CanvasItemRef item, bool additive)
    {
        if (!additive)
        {
            SetSelection([item]);
            return;
        }

        var next = new List<CanvasItemRef>(selection);
        if (!next.Remove(item))
        {
            next.Add(item);
        }

        SetSelection(next);
    }

    /// <summary>Selects every member of a linked group.</summary>
    internal void SelectGroup(Guid groupId)
    {
        if (profileService.CurrentProfile is not { } profile)
        {
            return;
        }

        var members = new List<CanvasItemRef>();
        LinkedGroups.Members(profile, groupId, members);
        SetSelection(members);
    }

    /// <summary>Selects exactly <paramref name="items"/> (the ones on the Plate).</summary>
    internal void SetSelection(IReadOnlyList<CanvasItemRef> items)
    {
        var profile = profileService.CurrentProfile;
        var next = new List<CanvasItemRef>(items.Count);
        foreach (var item in items)
        {
            if (profile is not null && LinkedGroups.Exists(profile, item) && !next.Contains(item))
            {
                next.Add(item);
            }
        }

        if (next.Count != selection.Count || !next.TrueForAll(selection.Contains))
        {
            CommitPendingEdits();
        }

        selection.Clear();
        selection.AddRange(next);
    }

    private void Deselect(CanvasItemRef item) => selection.Remove(item);

    // ---------------------------------------------------------------- linking

    /// <summary>Why the selection can't be linked right now, or null when it can.</summary>
    internal string? LinkSelectionBlockedReason =>
        profileService.CurrentProfile is { } profile ? LinkedGroups.WhyNotLinkable(profile, selection) : "No Plate is open.";

    /// <summary>Links the selection into one group, keeping everything where it is. One undo step.</summary>
    internal void LinkSelection()
    {
        if (LinkSelectionBlockedReason is { } reason)
        {
            ErrorMessage = reason;
            return;
        }

        var items = new List<CanvasItemRef>(selection);
        ApplyDocumentEdit(() => LinkedGroups.Link(RequireProfileForComponents(), items));
    }

    /// <summary>True when any selected item is in a linked group.</summary>
    internal bool SelectionHasLinks
    {
        get
        {
            foreach (var item in selection)
            {
                if (GroupOf(item) is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Unlinks every group a selected item is in, keeping everything where it is (the members stay
    /// selected, now on their own). One undo step.</summary>
    internal void UnlinkSelection()
    {
        var groups = new HashSet<Guid>();
        foreach (var item in selection)
        {
            if (GroupOf(item) is { } groupId)
            {
                groups.Add(groupId);
            }
        }

        if (groups.Count == 0)
        {
            return;
        }

        ApplyDocumentEdit(() =>
        {
            var profile = RequireProfileForComponents();
            foreach (var groupId in groups)
            {
                LinkedGroups.Unlink(profile, groupId);
            }
        });
    }

    // ---------------------------------------------------------------- selection-wide actions

    /// <summary>Deletes everything selected. One undo step.</summary>
    internal void DeleteSelection()
    {
        if (SelectedElementId is { } elementId)
        {
            RemoveElement(elementId);
            return;
        }

        if (selection.Count == 0)
        {
            return;
        }

        var items = new List<CanvasItemRef>(selection);
        ApplyDocumentEdit(() =>
        {
            var profile = RequireProfileForComponents();
            foreach (var item in items)
            {
                if (item.IsComponent)
                {
                    PlateComponentEditor.Remove(profile, item.Id);
                }
                else if (profile.Elements.Exists(e => e.Id == item.Id))
                {
                    profileService.RemoveElement(item.Id);
                }
            }
        });
    }

    /// <summary>
    /// Duplicates the selection: one element as Duplicate always has; anything else (a linked group,
    /// several things, a Component) as independent copies, moved a little, which form a new linked group
    /// when the selection was one. The copies are selected. One undo step.
    /// </summary>
    internal void DuplicateSelection()
    {
        if (SelectedElementId is { } elementId && GroupOf(CanvasItemRef.Element(elementId)) is null)
        {
            DuplicateElement(elementId);
            return;
        }

        if (selection.Count == 0)
        {
            return;
        }

        var items = new List<CanvasItemRef>(selection);
        var linkCopies = SelectedGroupId is not null;
        List<CanvasItemRef>? copies = null;
        var applied = ApplyDocumentEdit(() =>
        {
            var profile = RequireProfileForComponents();
            var offset = DuplicateOffset(profile, items);
            copies = LinkedGroups.Duplicate(profile, items, offset, component => ContentAnchor(profile, component.Kind));
            if (!linkCopies)
            {
                foreach (var copy in copies)
                {
                    if (copy.IsComponent)
                    {
                        PlateComponentEditor.Find(profile, copy.Id)!.LinkGroupId = null;
                    }
                    else
                    {
                        profile.Elements.Find(e => e.Id == copy.Id)!.LinkGroupId = null;
                    }
                }
            }
        });

        if (applied && copies is not null)
        {
            SetSelection(copies);
        }
    }

    /// <summary>Where copies go: 16 down and right, as one element's Duplicate, or up and left when that
    /// would push a copied element off the Plate.</summary>
    private static Vector2 DuplicateOffset(ProfileDocument profile, IReadOnlyList<CanvasItemRef> items)
    {
        const float Step = 16f;
        var canvas = new Vector2(profile.CanvasWidth, profile.CanvasHeight);
        var max = new Vector2(float.MinValue);
        var min = new Vector2(float.MaxValue);
        foreach (var item in items)
        {
            if (!item.IsComponent && profile.Elements.Find(e => e.Id == item.Id) is { } element)
            {
                var (low, high) = RotationGeometry.GetVisualBounds(element);
                min = Vector2.Min(min, low);
                max = Vector2.Max(max, high);
            }
        }

        return new Vector2(
            max.X + Step <= canvas.X || min.X - Step < 0f ? Step : -Step,
            max.Y + Step <= canvas.Y || min.Y - Step < 0f ? Step : -Step);
    }

    // ---------------------------------------------------------------- Component settings

    /// <summary>Attaches a Portrait Frame or Overlay to a picture (null: the Basic portrait again), keeping
    /// its own Offset, Scale and Rotation. One undo step; nothing if unchanged.</summary>
    internal void SetComponentTarget(Guid componentId, Guid? elementId) =>
        ApplyDocumentEdit(() => PlateComponentEditor.SetTarget(RequireProfileForComponents(), componentId, elementId));

    /// <summary>Locks or unlocks a Component against moving and resizing from the canvas. One undo step.</summary>
    internal void SetComponentLocked(Guid componentId, bool locked) =>
        ApplyDocumentEdit(() => PlateComponentEditor.Update(RequireProfileForComponents(), componentId, component => component.Locked = locked));

    // ---------------------------------------------------------------- moving and resizing several things

    /// <summary>Why the selection can't be moved or resized from the canvas (a locked member), or null.</summary>
    internal string? SelectionTransformBlockedReason
    {
        get
        {
            if (profileService.CurrentProfile is not { } profile || selection.Count == 0)
            {
                return "Nothing is selected.";
            }

            foreach (var item in selection)
            {
                if (LinkedGroups.IsLocked(profile, item))
                {
                    return selection.Count == 1 ? "It's locked." : "Something in the selection is locked, so it moves only when that is unlocked.";
                }
            }

            return null;
        }
    }

    /// <summary>
    /// True when the selection moves through <see cref="CanvasGesture"/> rather than as one element: a
    /// Component, or more than one thing.
    /// </summary>
    internal bool SelectionUsesGestures => selection.Count > 1 || (selection.Count == 1 && selection[0].IsComponent);

    /// <summary>The gesture the selection would start now, for drawing its handles; null when there is none.</summary>
    internal CanvasGesture? PreviewSelectionGesture(IReadOnlyList<PaintStep> plan, int placementIndex = 0) =>
        profileService.CurrentProfile is { } profile && SelectionUsesGestures ? CanvasGesture.Create(profile, selection, plan, placementIndex) : null;

    /// <summary>Starts moving the selection with the mouse. No-op while a member is locked.</summary>
    internal void BeginSelectionDrag(IReadOnlyList<PaintStep> plan, Vector2 mouseCanvasPosition, int placementIndex = 0) =>
        BeginSelectionGesture(ElementInteractionKind.Dragging, plan, mouseCanvasPosition, -1, placementIndex);

    /// <summary>Starts resizing the selection from handle corner <paramref name="corner"/> (0 top-left,
    /// 1 top-right, 2 bottom-right, 3 bottom-left of <see cref="CanvasGesture.Corners"/>), uniformly
    /// around the opposite corner. No-op while a member is locked.</summary>
    internal void BeginSelectionResize(IReadOnlyList<PaintStep> plan, int corner, Vector2 mouseCanvasPosition, int placementIndex = 0) =>
        BeginSelectionGesture(ElementInteractionKind.Resizing, plan, mouseCanvasPosition, Math.Clamp(corner, 0, 3), placementIndex);

    private void BeginSelectionGesture(ElementInteractionKind kind, IReadOnlyList<PaintStep> plan, Vector2 mouseCanvasPosition, int corner, int placementIndex)
    {
        if (ActiveInteraction != ElementInteractionKind.None || SelectionTransformBlockedReason is not null || profileService.CurrentProfile is not { } profile)
        {
            return;
        }

        CommitPendingEdits();
        if (CanvasGesture.Create(profile, selection, plan, placementIndex) is not { } gesture)
        {
            return;
        }

        try
        {
            itemsGestureBefore = profileService.CaptureDocumentState();
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return;
        }

        itemsGesture = gesture;
        itemsGestureCorner = corner;
        ActiveInteraction = kind;
        ActiveResizeHandle = ResizeHandle.None;
        dragStartMousePosition = mouseCanvasPosition;

        var moving = new HashSet<Guid>(gesture.ElementIds);
        snapEngine.Begin(profile, moving.Contains);
    }

    private void UpdateSelectionGesture(CanvasGesture gesture, Vector2 mouseCanvasPosition, bool snap, float snapThreshold)
    {
        var profile = RequireProfileForComponents();
        var canvas = CurrentCanvasSize;
        void UpdateElement(Guid id, Action<ProfileElement> update) => profileService.UpdateElement(id, update);
        void UpdateComponent(Guid id, Action<PlateComponent> update) => PlateComponentEditor.Update(profile, id, update);

        if (!snap)
        {
            snapEngine.ClearGuides();
        }

        var (min, max) = gesture.Bounds;
        if (ActiveInteraction == ElementInteractionKind.Dragging)
        {
            var delta = mouseCanvasPosition - dragStartMousePosition;
            if (snap)
            {
                delta += snapEngine.SnapMove(min + delta, max + delta, snapThreshold);
            }

            delta = gesture.ClampTranslation(delta, canvas);
            if (snap)
            {
                snapEngine.UpdateGuides(min + delta, max + delta, EdgeMask.All);
            }

            gesture.Translate(delta, UpdateElement, UpdateComponent);
            return;
        }

        var corners = gesture.Corners;
        var handle = corners[itemsGestureCorner];
        var anchor = corners[(itemsGestureCorner + 2) % 4];
        var target = mouseCanvasPosition;
        var axisAligned = corners[0].Y == corners[1].Y && corners[0].X == corners[3].X;
        if (axisAligned)
        {
            // The dragged corner stays on the Plate (or wherever it already was), as one element's does.
            target = Vector2.Clamp(target, Vector2.Min(Vector2.Zero, handle), Vector2.Max(canvas, handle));
            if (snap)
            {
                target += new Vector2(snapEngine.SnapX(target.X, snapThreshold), snapEngine.SnapY(target.Y, snapThreshold));
            }
        }
        else
        {
            snapEngine.ClearGuides();
        }

        var factor = CanvasGesture.FactorFor(anchor, handle, target);
        gesture.Scale(anchor, factor, UpdateElement, UpdateComponent);
        if (snap && axisAligned)
        {
            var (low, high) = gesture.ScaleLimits;
            var s = Math.Clamp(factor, low, high);
            var moved = anchor + ((handle - anchor) * s);
            snapEngine.UpdateGuides(Vector2.Min(anchor, moved), Vector2.Max(anchor, moved), EdgeMask.All);
        }
    }

    private void EndSelectionGesture()
    {
        if (itemsGestureBefore is { } before)
        {
            try
            {
                RecordDocumentEdit(before, profileService.CaptureDocumentState());
            }
            catch
            {
                // The Plate can't be read now (closing); nothing to record.
            }
        }

        itemsGesture = null;
        itemsGestureBefore = null;
        DropSelectionIfMissing();
    }

    private void CancelSelectionGesture()
    {
        if (itemsGestureBefore is { } before)
        {
            try
            {
                profileService.RestoreDocumentState(before);
            }
            catch
            {
                // Profile no longer editable; nothing to restore.
            }

            InvalidateDirtyMemo();
        }

        itemsGesture = null;
        itemsGestureBefore = null;
        DropSelectionIfMissing();
    }

    /// <summary>Moves the selection by an arrow-key nudge: one undo step. One element moves as before
    /// (<see cref="NudgeSelected"/>); several things, or a Component, move together.</summary>
    private void NudgeSelection(Vector2 delta)
    {
        if (SelectionTransformBlockedReason is not null || profileService.CurrentProfile is not { } profile)
        {
            return;
        }

        if (CanvasGesture.Create(profile, selection, Array.Empty<PaintStep>()) is not { } gesture)
        {
            return;
        }

        ApplyDocumentEdit(() =>
        {
            var editable = RequireProfileForComponents();
            gesture.Translate(
                gesture.ClampTranslation(delta, CurrentCanvasSize),
                (id, update) => profileService.UpdateElement(id, update),
                (id, update) => PlateComponentEditor.Update(editable, id, update));
        });
    }
}
