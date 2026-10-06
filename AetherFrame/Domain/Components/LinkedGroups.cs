using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Components;

/// <summary>One thing on the canvas the Advanced editor can select: an element or a Component, by id.</summary>
public readonly record struct CanvasItemRef(Guid Id, bool IsComponent)
{
    public static CanvasItemRef Element(Guid id) => new(id, false);

    public static CanvasItemRef Component(Guid id) => new(id, true);
}

/// <summary>
/// Linked groups: elements and Components the Advanced editor selects, moves and resizes together.
/// A group is every element and Component carrying the same <see cref="ProfileElement.LinkGroupId"/>
/// (<see cref="PlateComponent.LinkGroupId"/>); one with fewer than two members on the Plate is no
/// group, so deleting members never needs to tidy the others. The ids are editor metadata only:
/// linking and unlinking change nothing that is drawn, and nothing that draws a Plate reads them.
/// </summary>
public static class LinkedGroups
{
    /// <summary>Why a Component can't join a linked group, or null when it can.</summary>
    public static string? WhyNotLinkable(PlateComponent component)
    {
        if (!ComponentPaintPlan.IsKnownKind(component.Kind))
        {
            return "It was made with a newer version of AetherFrame.";
        }

        return component.Kind switch
        {
            PlateComponentKind.Background => "A Background covers the whole Plate, so it can't be linked.",
            PlateComponentKind.SectionHeader => "A Section Header decorates every heading at once, so it can't be linked.",
            PlateComponentKind.CornerOrnament when !IsSingleCorner(CornerMasks.Effective(component)) =>
                "A Corner Ornament in several corners moves them all symmetrically. Keep one corner to link it.",
            _ => null,
        };
    }

    /// <summary>Why <paramref name="items"/> can't be linked into one group, or null when they can: at
    /// least two that are on the Plate, and every Component among them linkable.</summary>
    public static string? WhyNotLinkable(ProfileDocument profile, IReadOnlyList<CanvasItemRef> items)
    {
        var count = 0;
        foreach (var item in items)
        {
            if (item.IsComponent)
            {
                if (PlateComponentEditor.Find(profile, item.Id) is not { } component)
                {
                    continue;
                }

                if (WhyNotLinkable(component) is { } reason)
                {
                    return reason;
                }
            }
            else if (FindElement(profile, item.Id) is null)
            {
                continue;
            }

            count++;
        }

        return count < 2 ? "Select at least two things to link (Ctrl+click on the canvas or in Layers)." : null;
    }

    /// <summary>
    /// Links <paramref name="items"/> into one new group (a member of another group leaves it). Only the
    /// group ids change: every position, size, layer and look stays exactly as it was. Returns the new
    /// group's id; throws when <see cref="WhyNotLinkable(ProfileDocument, IReadOnlyList{CanvasItemRef})"/> says no.
    /// </summary>
    public static Guid Link(ProfileDocument profile, IReadOnlyList<CanvasItemRef> items)
    {
        if (WhyNotLinkable(profile, items) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        var groupId = Guid.NewGuid();
        foreach (var item in items)
        {
            SetGroup(profile, item, groupId);
        }

        return groupId;
    }

    /// <summary>Unlinks every member of <paramref name="groupId"/>. Only the group ids change. False when no member had it.</summary>
    public static bool Unlink(ProfileDocument profile, Guid groupId)
    {
        var changed = false;
        foreach (var element in profile.Elements)
        {
            if (element.LinkGroupId == groupId)
            {
                element.LinkGroupId = null;
                changed = true;
            }
        }

        foreach (var component in profile.Components ?? [])
        {
            if (component.LinkGroupId == groupId)
            {
                component.LinkGroupId = null;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>The group <paramref name="item"/> is a member of: its group id when at least two members
    /// carrying it are on the Plate, otherwise null (it isn't linked).</summary>
    public static Guid? GroupOf(ProfileDocument profile, CanvasItemRef item)
    {
        Guid? id = item.IsComponent ? PlateComponentEditor.Find(profile, item.Id)?.LinkGroupId : FindElement(profile, item.Id)?.LinkGroupId;
        return id is { } groupId && groupId != Guid.Empty && MemberCount(profile, groupId) >= 2 ? groupId : null;
    }

    /// <summary>How many elements and Components carry <paramref name="groupId"/>.</summary>
    public static int MemberCount(ProfileDocument profile, Guid groupId)
    {
        var count = 0;
        foreach (var element in profile.Elements)
        {
            if (element.LinkGroupId == groupId)
            {
                count++;
            }
        }

        foreach (var component in profile.Components ?? [])
        {
            if (component.LinkGroupId == groupId)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Fills <paramref name="output"/> (cleared first) with the members of <paramref name="groupId"/>:
    /// its elements in list order, then its Components in list order.</summary>
    public static void Members(ProfileDocument profile, Guid groupId, List<CanvasItemRef> output)
    {
        output.Clear();
        foreach (var element in profile.Elements)
        {
            if (element.LinkGroupId == groupId)
            {
                output.Add(CanvasItemRef.Element(element.Id));
            }
        }

        foreach (var component in profile.Components ?? [])
        {
            if (component.LinkGroupId == groupId)
            {
                output.Add(CanvasItemRef.Component(component.Id));
            }
        }
    }

    /// <summary>True when <paramref name="item"/> is on the Plate.</summary>
    public static bool Exists(ProfileDocument profile, CanvasItemRef item) =>
        item.IsComponent ? PlateComponentEditor.Find(profile, item.Id) is not null : FindElement(profile, item.Id) is not null;

    /// <summary>True when <paramref name="item"/> is locked (a missing item counts as locked: it can't move).</summary>
    public static bool IsLocked(ProfileDocument profile, CanvasItemRef item) =>
        item.IsComponent ? PlateComponentEditor.Find(profile, item.Id)?.Locked ?? true : FindElement(profile, item.Id)?.Locked ?? true;

    /// <summary>
    /// The elements whose place decides where <paramref name="component"/> is drawn: the picture a Portrait
    /// Frame or Overlay is attached to (or the Basic portrait), the name and title a following Name Backing
    /// or Divider sits by, every heading a Section Header decorates. Empty for Components placed by the
    /// canvas. When those elements move, the Component moves with them on its own: a group that moves both
    /// must not move it again.
    /// </summary>
    public static void FollowedElements(ProfileDocument profile, PlateComponent component, List<Guid> output)
    {
        output.Clear();
        if (PlateComponentEditor.CanTarget(component.Kind))
        {
            if (ComponentPaintPlan.TargetOf(component) is { } target)
            {
                output.Add(target);
            }
            else if (BasicSections.Find(profile, ProfileElementRole.BasicPortrait) is { } portrait)
            {
                output.Add(portrait.Id);
            }

            return;
        }

        if (PlateComponentEditor.CanFixAnchor(component.Kind))
        {
            if (ComponentPaintPlan.FixedAnchorOf(component) is null)
            {
                AddRole(ProfileElementRole.BasicName);
                AddRole(ProfileElementRole.BasicTitle);
            }

            return;
        }

        if (component.Kind == PlateComponentKind.SectionHeader)
        {
            foreach (var element in profile.Elements)
            {
                if (element is TextProfileElement && BasicSections.IsHeading(element.Role))
                {
                    output.Add(element.Id);
                }
            }
        }

        void AddRole(ProfileElementRole role)
        {
            if (BasicSections.Find(profile, role) is { } element)
            {
                output.Add(element.Id);
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="items"/> as an independent group: new ids for every copy, a new group id when
    /// two or more are copied, each copy moved by <paramref name="offset"/>, the copied elements over every
    /// element in their own order, the copied Components on top of their layers in theirs. A copied frame
    /// attached to a copied picture is attached to the copy; one following the Basic portrait, the name or
    /// the title that is copied too follows the copy instead (the copies of Basic's elements are ordinary
    /// elements), so moving the copy never moves the original. <paramref name="contentAnchor"/> gives a
    /// following Name Backing's or Divider's current box (see <see cref="ComponentPaintPlan.ContentAnchor"/>).
    /// Returns the copies, in the order of <paramref name="items"/>; throws when the Plate has no room.
    /// </summary>
    public static List<CanvasItemRef> Duplicate(
        ProfileDocument profile, IReadOnlyList<CanvasItemRef> items, Vector2 offset, Func<PlateComponent, ElementRect?> contentAnchor)
    {
        var elementCopies = new Dictionary<Guid, Guid>();
        var sourceElements = new List<ProfileElement>();
        var sourceComponents = new List<PlateComponent>();
        foreach (var item in items)
        {
            if (item.IsComponent)
            {
                if (PlateComponentEditor.Find(profile, item.Id) is { } component && !sourceComponents.Contains(component))
                {
                    sourceComponents.Add(component);
                }
            }
            else if (FindElement(profile, item.Id) is { } element && !sourceElements.Contains(element))
            {
                sourceElements.Add(element);
            }
        }

        if (profile.Elements.Count + profile.UnsupportedElementCount + sourceElements.Count > ProfileDocument.MaxElementCount)
        {
            throw new InvalidOperationException($"A Plate can have at most {ProfileDocument.MaxElementCount} elements.");
        }

        if (PlateComponentEditor.Count(profile) + sourceComponents.Count > PlateComponentLimits.MaxComponentCount)
        {
            throw new InvalidOperationException($"A Plate can have at most {PlateComponentLimits.MaxComponentCount} Components.");
        }

        Guid? groupId = sourceElements.Count + sourceComponents.Count >= 2 ? Guid.NewGuid() : null;

        // Elements: above everything, keeping their paint order among themselves.
        var paintOrder = new List<ProfileElement>(sourceElements);
        paintOrder.Sort((a, b) =>
        {
            var byZ = a.ZIndex.CompareTo(b.ZIndex);
            return byZ != 0 ? byZ : profile.Elements.IndexOf(a).CompareTo(profile.Elements.IndexOf(b));
        });

        var nextZ = 0;
        foreach (var element in profile.Elements)
        {
            nextZ = Math.Max(nextZ, element.ZIndex + 1);
        }

        var copies = new Dictionary<object, CanvasItemRef>(ReferenceEqualityComparer.Instance);
        foreach (var source in paintOrder)
        {
            var copy = source.Clone();
            copy.Id = Guid.NewGuid();
            copy.Name = ProfileElementNames.MakeCopyName(profile.Elements, source);
            copy.Role = ProfileElementRole.None; // a copy of a Basic element is an ordinary one, as Duplicate makes it
            copy.Position += offset;
            copy.ZIndex = nextZ++;
            copy.LinkGroupId = groupId;
            profile.Elements.Add(copy);
            elementCopies[source.Id] = copy.Id;
            copies[source] = CanvasItemRef.Element(copy.Id);
        }

        var components = profile.Components ??= new List<PlateComponent>();
        var followed = new List<Guid>();
        foreach (var source in sourceComponents)
        {
            var copy = source.Clone();
            copy.Id = Guid.NewGuid();
            copy.LinkGroupId = groupId;

            FollowedElements(profile, source, followed);
            var followsACopy = followed.Count > 0 && followed.TrueForAll(elementCopies.ContainsKey);
            if (PlateComponentEditor.CanTarget(source.Kind) && followed.Count == 1 && elementCopies.TryGetValue(followed[0], out var copiedPicture))
            {
                // On the copied picture: it moves with it, so no offset of its own.
                copy.TargetElementId = copiedPicture;
            }
            else if (PlateComponentEditor.CanFixAnchor(source.Kind) && ComponentPaintPlan.FixedAnchorOf(source) is null && contentAnchor(source) is { } anchor)
            {
                // The copied name and title are ordinary text, which a following backing can't follow:
                // fixed where the original is drawn, moved with the copies.
                copy.FixedAnchorPosition = anchor.Position + (followsACopy ? offset : Vector2.Zero);
                copy.FixedAnchorSize = anchor.Size;
                if (!followsACopy)
                {
                    copy.Offset = source.Offset + offset;
                }
            }
            else if (!followsACopy)
            {
                var (flipX, flipY) = OffsetFlips(profile, source);
                copy.Offset = source.Offset + new Vector2(flipX ? -offset.X : offset.X, flipY ? -offset.Y : offset.Y);
            }

            copy.LayerOrder = PlateComponentLimits.ClampLayerOrder(source.LayerOrder);
            components.Add(copy);
            copies[source] = CanvasItemRef.Component(copy.Id);
        }

        // Back in the order asked for.
        var result = new List<CanvasItemRef>(copies.Count);
        foreach (var item in items)
        {
            object? source = item.IsComponent ? PlateComponentEditor.Find(profile, item.Id) : FindElement(profile, item.Id);
            if (source is not null && copies.TryGetValue(source, out var copied) && !result.Contains(copied))
            {
                result.Add(copied);
            }
        }

        return result;
    }

    /// <summary>Whether <paramref name="component"/>'s Offset moves its first placement the other way along
    /// X and Y: a Background on a Mirrored Plate is drawn mirrored, its Offset with it; a right or bottom
    /// Corner Ornament (with no left or top corner) is drawn with its Offset turned. The same flips the
    /// paint plan gives that placement (<see cref="ComponentPlacement.OffsetFlipX"/>), for a Component
    /// that isn't drawn right now.</summary>
    public static (bool X, bool Y) OffsetFlips(ProfileDocument profile, PlateComponent component)
    {
        if (component.Kind == PlateComponentKind.Background)
        {
            return (profile.BasicPlate?.Orientation == AdventurePlateOrientation.Mirrored, false);
        }

        if (component.Kind == PlateComponentKind.CornerOrnament)
        {
            var corners = CornerMasks.Effective(component);
            return ((corners & (CornerMask.TopLeft | CornerMask.BottomLeft)) == 0, (corners & (CornerMask.TopLeft | CornerMask.TopRight)) == 0);
        }

        return (false, false);
    }

    private static bool IsSingleCorner(CornerMask mask) => mask is CornerMask.TopLeft or CornerMask.TopRight or CornerMask.BottomLeft or CornerMask.BottomRight;

    private static void SetGroup(ProfileDocument profile, CanvasItemRef item, Guid? groupId)
    {
        if (item.IsComponent)
        {
            if (PlateComponentEditor.Find(profile, item.Id) is { } component)
            {
                component.LinkGroupId = groupId;
            }
        }
        else if (FindElement(profile, item.Id) is { } element)
        {
            element.LinkGroupId = groupId;
        }
    }

    private static ProfileElement? FindElement(ProfileDocument profile, Guid id) => profile.Elements.Find(e => e.Id == id);
}
