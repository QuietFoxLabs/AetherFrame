using System;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The Inspector Canvas tab's Components section: the same <see cref="PlateComponent"/>s the Basic
/// editor's slots choose, with the Advanced refinements on top — style (including image styles),
/// color (where it tints: see <see cref="AppearanceControls.ColorApplies"/>), opacity, offset, scale,
/// rotation, and order within the Component's layer. Components are
/// placed by their layer and anchor (see <see cref="ComponentPaintPlan"/>), so element Z order never
/// moves them out of their layer. On the canvas they move and resize like elements, through their
/// Offset and Size (<see cref="UI.Editor.CanvasGesture"/>), unless locked. A Portrait Frame or Overlay
/// can be attached to any picture instead of the Basic portrait. They are selected like elements
/// (issue #115): by a click on the canvas, from their own entries in the Layers panel, or by opening a
/// row here, and all three stay in step through <see cref="UI.Editor.EditorSession.SelectedComponentId"/>.
/// </summary>
internal sealed partial class ProfileEditorWindow
{
    private const string ComponentsSectionLabel = "Components";

    // Which Component's details are expanded (runtime-only UI state); follows the selected Component.
    private Guid? expandedComponentId;

    // Scrolls the expanded row into view once, after it was selected somewhere else.
    private bool scrollToExpandedComponentPending;

    private void DrawComponentsSection(ProfileDocument profile)
    {
        if (!EditorWidgets.Section(ComponentsSectionLabel))
        {
            return;
        }

        var components = profile.Components;
        if (components is null || components.Count == 0)
        {
            EditorWidgets.Hint("No Components yet. Add frames, backings, and decorations below; the Basic Editor uses the same ones.");
        }
        else
        {
            // A copy of the ids: an action below may change the list this frame.
            var ids = new Guid[components.Count];
            for (var i = 0; i < components.Count; i++)
            {
                ids[i] = components[i].Id;
            }

            foreach (var componentId in ids)
            {
                if (PlateComponentEditor.Find(profile, componentId) is { } component)
                {
                    DrawComponentRow(profile, component);
                }
            }
        }

        if (profile.UnrecognizedComponents is { Count: > 0 } unreadable)
        {
            EditorWidgets.Hint(unreadable.Count == 1
                ? "1 Component couldn't be read. It isn't shown, but it's kept."
                : $"{unreadable.Count} Components couldn't be read. They aren't shown, but they're kept.");
        }

        DrawAddComponentCombo(profile);
    }

    private void DrawAddComponentCombo(ProfileDocument profile)
    {
        var hasCapacity = PlateComponentEditor.HasCapacity(profile);
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.Disabled(!hasCapacity))
        using (var combo = ImRaii.Combo("##AddComponent", "Add Component..."))
        {
            if (combo.Success)
            {
                // No selection here: reopening returns the list to where it was (issue #114).
                var memory = ChooserMemories.For("AddComponent");
                ChooserScroll.Begin(memory, null);
                foreach (var kind in PlateComponentEditor.BasicSlots)
                {
                    DrawAddComponentGroup(kind);
                }

                foreach (var kind in PlateComponentEditor.BasicDecorations)
                {
                    DrawAddComponentGroup(kind);
                }

                ChooserScroll.End(memory, null);
            }
        }

        if (!hasCapacity)
        {
            EditorWidgets.Hint($"A Plate can have at most {PlateComponentLimits.MaxComponentCount} Components.");
        }
    }

    private void DrawAddComponentGroup(PlateComponentKind kind)
    {
        ImGui.TextDisabled(PlateComponentEditor.KindLabel(kind));
        foreach (var definition in BuiltInComponentCatalog.OfKind(kind))
        {
            if (ImGui.Selectable($"   {definition.Name}##Add{definition.Id}") && editorSession.AddComponent(definition.Id) is { } added)
            {
                expandedComponentId = added;
                SelectComponentFromList(added);
            }

            EditorWidgets.Tooltip(definition.Description);
        }
    }

    private void DrawComponentRow(ProfileDocument profile, PlateComponent component)
    {
        using var id = ImRaii.PushId(component.Id.ToString());

        var status = ComponentPaintPlan.Resolve(component, BuiltInComponentCatalog.Instance, out var definition);
        var styleName = definition?.Name ?? "Unavailable";
        var label = $"{PlateComponentEditor.KindLabel(component.Kind)}: {styleName}";

        var visibility = component.Visible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
        if (EditorWidgets.IconButton("Visible", visibility, component.Visible ? "Hide" : "Show"))
        {
            var visible = !component.Visible;
            editorSession.EditComponent(component.Id, c => c.Visible = visible, continuous: false);
        }

        ImGui.SameLine();
        var buttons = (ImGui.GetFrameHeight() * 3f) + (ImGui.GetStyle().ItemSpacing.X * 3f);
        var expanded = expandedComponentId == component.Id;
        if (ImGui.Selectable(label, expanded || editorSession.SelectedComponentId == component.Id, ImGuiSelectableFlags.None, new Vector2(Math.Max(40f, ImGui.GetContentRegionAvail().X - buttons), 0f)))
        {
            // Opening a row selects its Component on the canvas; closing it lets go of it.
            expandedComponentId = expanded ? null : component.Id;
            SelectComponentFromList(expanded ? null : component.Id);
        }

        if (expanded && scrollToExpandedComponentPending)
        {
            ImGui.SetScrollHereY(0.2f);
            scrollToExpandedComponentPending = false;
        }

        if (status is not (ComponentStatus.Ready or ComponentStatus.MissingImage))
        {
            EditorWidgets.Tooltip("Made with a newer version of AetherFrame, so it isn't shown here. It's kept on save.");
        }

        ImGui.SameLine();
        if (EditorWidgets.IconButton("Down", FontAwesomeIcon.ArrowDown, "Move down within its layer"))
        {
            editorSession.MoveComponentInLayer(component.Id, -1);
        }

        ImGui.SameLine();
        if (EditorWidgets.IconButton("Up", FontAwesomeIcon.ArrowUp, "Move up within its layer"))
        {
            editorSession.MoveComponentInLayer(component.Id, +1);
        }

        ImGui.SameLine();
        if (EditorWidgets.IconButton("Remove", FontAwesomeIcon.Trash, "Remove (undoable)"))
        {
            editorSession.RemoveComponent(component.Id);
            if (expandedComponentId == component.Id)
            {
                expandedComponentId = null;
            }

            lastInspectedComponentId = editorSession.SelectedComponentId;

            return;
        }

        if (expanded)
        {
            using (ImRaii.PushIndent())
            {
                DrawComponentDetails(profile, component, status, definition);
            }

            ImGui.Spacing();
        }
    }

    /// <summary>Selects a Component (or lets go of it, null) from a list here or in Layers, without
    /// the Inspector treating it as a selection from elsewhere (it is already where it should be).</summary>
    private void SelectComponentFromList(Guid? componentId)
    {
        if (componentId is null && editorSession.SelectedComponentId is null)
        {
            return;
        }

        editorSession.SelectComponent(componentId);
        lastInspectedComponentId = editorSession.SelectedComponentId;
    }

    private void DrawComponentDetails(ProfileDocument profile, PlateComponent component, ComponentStatus status, ComponentDefinition? definition)
    {
        var componentId = component.Id;

        // Style: any definition of the same kind, image styles included.
        if (ComponentPaintPlan.IsKnownKind(component.Kind))
        {
            EditorWidgets.PropertyLabel("Style");
            using var combo = ImRaii.Combo("##Style", definition?.Name ?? "Unavailable");
            if (combo.Success)
            {
                // The same list memory as Basic's slot for this kind (issue #114).
                var memory = ChooserMemories.For(BasicProfileEditorWindow.ComponentStyleChooserKey(component.Kind));
                var selection = component.DefinitionId;
                var opening = ChooserScroll.Begin(memory, selection, layout: BasicProfileEditorWindow.AdvancedStyleListLayout);
                foreach (var candidate in BuiltInComponentCatalog.OfKind(component.Kind))
                {
                    var isCurrent = candidate.Id == component.DefinitionId;
                    if (ImGui.Selectable(candidate.Name, isCurrent) && !isCurrent)
                    {
                        var definitionId = candidate.Id;
                        editorSession.SetComponentDefinition(componentId, definitionId);
                        selection = definitionId;
                    }

                    EditorWidgets.Tooltip(candidate.Description);
                    ChooserScroll.ScrollHereIfOpening(opening, isCurrent);
                }

                ChooserScroll.End(memory, selection, BasicProfileEditorWindow.AdvancedStyleListLayout);
            }
        }

        if (definition is { RequiresAsset: true })
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
            if (ImGui.Button(component.AssetId is null ? "Choose Image..." : "Replace Image...", new Vector2(-1, 0f)))
            {
                OpenImageFileDialog("Component Image", path => editorSession.SetComponentImage(componentId, path));
            }

            if (status == ComponentStatus.MissingImage)
            {
                EditorWidgets.Hint("Choose an image to show this Component.");
            }
        }

        // Portrait Frames and Overlays: the picture they're drawn on, by its identity, or the Basic portrait.
        if (PlateComponentEditor.CanTarget(component.Kind))
        {
            DrawComponentTarget(profile, component);
        }

        var locked = component.Locked;
        EditorWidgets.PropertyLabel("Locked");
        if (ImGui.Checkbox("##Locked", ref locked))
        {
            editorSession.SetComponentLocked(componentId, locked);
        }

        EditorWidgets.Tooltip("Locked: it can't be moved or resized on the canvas. Its settings here stay editable.");

        if (editorSession.GroupOf(Domain.Components.CanvasItemRef.Component(componentId)) is { } groupId)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
            if (ImGui.Button("Select Linked Group##ComponentGroup", new Vector2(-1, 0f)))
            {
                editorSession.SelectGroup(groupId);
            }

            EditorWidgets.Tooltip("It's linked with other things: they move and resize together. Click any of them on the canvas to select the group.");
        }

        // Corners (Corner Ornaments): this instance's own selection; another instance can take other corners.
        if (component.Kind == PlateComponentKind.CornerOrnament)
        {
            EditorWidgets.PropertyLabel("Corners");
            if (EditorWidgets.CornerToggles(CornerMasks.Effective(component), out var corner, out var enabled))
            {
                editorSession.SetComponentCorner(componentId, corner, enabled);
            }
        }

        // Name Backings and Dividers: follow the name and title (Basic's placement), or stay where they are.
        if (PlateComponentEditor.CanFixAnchor(component.Kind))
        {
            var follows = ComponentPaintPlan.FixedAnchorOf(component) is null;
            EditorWidgets.PropertyLabel("Placement");
            if (ImGui.Checkbox("Follows the name##FollowsName", ref follows))
            {
                editorSession.SetComponentFollowsContent(componentId, follows);
            }

            EditorWidgets.Tooltip("On: moves and resizes with the name and title.\nOff: stays where it is, so you can move the name and this independently.");
        }

        // Color: follows the theme until overridden. Artwork drawn in its own colors takes none (issue
        // #119, AppearanceControls), so it offers none, unless one is kept from before: that one's
        // transparency still applies, so it stays, to be seen and turned off.
        var colorApplies = AppearanceControls.ColorApplies(definition);
        if (!colorApplies && component.Color is null)
        {
            EditorWidgets.PropertyLabel("Color", 0f);
            ImGui.TextDisabled(AppearanceControls.OwnColorsLabel);
            EditorWidgets.Tooltip(AppearanceControls.OwnColorsReason);
        }
        else
        {
            var hasColor = component.Color is not null;
            EditorWidgets.PropertyLabel("Color");
            if (ImGui.Checkbox("Custom##CustomColor", ref hasColor))
            {
                var start = definition?.DefaultColor(profile) ?? Vector4.One;
                editorSession.EditComponent(componentId, c => c.Color = hasColor ? start : null, continuous: false);
            }

            EditorWidgets.Tooltip("Off: the color follows the Plate's theme.");
            if (component.Color is { } color)
            {
                ImGui.SameLine();
                ScreenEyedropper.LeaveRoom();
                if (ImGui.ColorEdit4("##Color", ref color, ImGuiColorEditFlags.AlphaBar))
                {
                    var picked = color;
                    editorSession.EditComponent(componentId, c => c.Color = picked, continuous: true);
                }

                CommitComponentOnRelease();
                if (ScreenEyedropper.Button("ComponentColor", ref color))
                {
                    var picked = color;
                    editorSession.EditComponent(componentId, c => c.Color = picked, continuous: false);
                }
            }

            if (!colorApplies)
            {
                EditorWidgets.Hint(AppearanceControls.KeptColorReason);
            }
        }

        var opacity = component.Opacity * 100f;
        EditorWidgets.PropertyLabel("Opacity");
        if (ImGui.SliderFloat("##Opacity", ref opacity, 0f, 100f, "%.0f%%"))
        {
            var value = opacity / 100f;
            editorSession.EditComponent(componentId, c => c.Opacity = value, continuous: true);
        }

        CommitComponentOnRelease();

        var offset = component.Offset;
        EditorWidgets.PropertyLabel("Offset");
        if (ImGui.DragFloat2("##Offset", ref offset, 1f, -PlateComponentLimits.MaxOffset, PlateComponentLimits.MaxOffset, "%.0f"))
        {
            var value = offset;
            editorSession.EditComponent(componentId, c => c.Offset = value, continuous: true);
        }

        CommitComponentOnRelease();

        var scale = component.Scale * 100f;
        EditorWidgets.PropertyLabel("Size");
        if (ImGui.SliderFloat("##Scale", ref scale, PlateComponentLimits.MinScale * 100f, PlateComponentLimits.MaxScale * 100f, "%.0f%%"))
        {
            var value = scale / 100f;
            editorSession.EditComponent(componentId, c => c.Scale = value, continuous: true);
        }

        CommitComponentOnRelease();

        var rotation = component.RotationDegrees;
        EditorWidgets.PropertyLabel("Rotation");
        if (ImGui.SliderFloat("##Rotation", ref rotation, -180f, 180f, "%.1f deg"))
        {
            var value = rotation;
            editorSession.EditComponent(componentId, c => c.RotationDegrees = value, continuous: true);
        }

        CommitComponentOnRelease();

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
        if (ImGui.Button("Reset Placement & Color", new Vector2(-1, 0f)))
        {
            editorSession.ResetComponentTransform(componentId);
        }

        EditorWidgets.Tooltip("Back to the default placement, full opacity, and the theme color. Style and image are kept.");

        var placedBy = ComponentPaintPlan.FixedAnchorOf(component) is not null ? "its own spot on the Plate (it no longer follows the name)"
            : ComponentPaintPlan.TargetOf(component) is not null ? "the picture it's attached to (it follows that picture's position, size, and rotation)"
            : AnchorDescription(component.Kind);
        EditorWidgets.Hint($"Layer: {PlateComponentEditor.KindLabel(component.Kind)}. Placed by {placedBy}.");
    }

    /// <summary>The "Attached to" choice of a Portrait Frame or Overlay: the Basic portrait, or any picture on
    /// the Plate, by its identity (so two frames can sit on two pictures).</summary>
    private void DrawComponentTarget(ProfileDocument profile, PlateComponent component)
    {
        var componentId = component.Id;
        var target = ComponentPaintPlan.TargetOf(component);
        var targetElement = target is { } targetId ? profile.Elements.Find(e => e.Id == targetId) : null;
        var preview = target is null ? "The portrait" : targetElement is not null ? ProfileElementNames.GetDisplayName(targetElement) : "A deleted picture";

        EditorWidgets.PropertyLabel("Attached to");
        using (var combo = ImRaii.Combo("##AttachedTo", preview))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("The portrait##AttachPortrait", target is null) && target is not null)
                {
                    editorSession.SetComponentTarget(componentId, null);
                }

                EditorWidgets.Tooltip("Follows the Basic editor's portrait, as every frame always has.");

                foreach (var element in profile.Elements)
                {
                    // The Basic portrait is "The portrait" above.
                    if (element is not ImageProfileElement || element.Role == ProfileElementRole.BasicPortrait)
                    {
                        continue;
                    }

                    var isCurrent = target == element.Id;
                    if (ImGui.Selectable($"{ProfileElementNames.GetDisplayName(element)}##Attach{element.Id:N}", isCurrent) && !isCurrent)
                    {
                        editorSession.SetComponentTarget(componentId, element.Id);
                    }
                }
            }
        }

        EditorWidgets.Tooltip("The picture this is drawn on. It follows that picture when it moves, resizes, or turns.");

        if (target is not null && targetElement is null)
        {
            EditorWidgets.Hint("Its picture was deleted, so it isn't shown. Attach it to another picture or the portrait, or undo the deletion.");
        }
        else if (targetElement is { Visible: false })
        {
            EditorWidgets.Hint("Its picture is hidden, so it's hidden too.");
        }
    }

    private void CommitComponentOnRelease()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingDocumentEdit();
        }
    }

    private static string AnchorDescription(PlateComponentKind kind) => kind switch
    {
        PlateComponentKind.Background => "the whole Plate, under the portrait and text (on a Mirrored Plate it is mirrored, with its Offset and Rotation)",
        PlateComponentKind.PortraitFrame or PlateComponentKind.PortraitOverlay => "the portrait (it follows the portrait's position, size, and rotation)",
        PlateComponentKind.NameBacking => "the name and title",
        PlateComponentKind.Divider => "the space under the name and title",
        PlateComponentKind.SectionHeader => "every section heading",
        PlateComponentKind.CornerOrnament => "the Plate's corners you select (add another Corner Ornament for a different style in other corners)",
        _ => "the Plate's edges",
    };
}
