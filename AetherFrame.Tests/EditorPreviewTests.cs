using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Preview has one meaning in both editors: the finished Plate alone, as a viewer sees it, in the
/// Plate Viewer. Both action bars show it through <see cref="EditorPreview"/> over the shared
/// session, so Basic's Preview and Advanced's Preview open the same Plate the same way, and neither
/// ever changes the Plate.
/// </summary>
public class EditorPreviewTests
{
    [Fact]
    public async Task PreviewFromBasic_AndFromAdvanced_IsTheSamePreview()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var viewed = new List<Guid>();

        // From the Basic editor.
        harness.SimulateBasicFrame();
        EditorPreview.Show(harness.Session, harness.Document.ProfileId, viewed.Add);

        // From the Advanced editor: the very same session, the very same Plate.
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        EditorPreview.Show(harness.Session, harness.Document.ProfileId, viewed.Add);

        Assert.Equal([harness.Document.ProfileId, harness.Document.ProfileId], viewed);
    }

    [Fact]
    public async Task Preview_NeverChangesThePlate()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.SimulateBasicFrame();
        var before = harness.Json();

        EditorPreview.Show(harness.Session, harness.Document.ProfileId, _ => { });
        harness.SimulateBasicFrame();

        Assert.Equal(before, harness.Json());
        Assert.False(harness.Session.IsDirty);
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public void BothEditors_DescribePreviewTheSameWay()
    {
        Assert.Equal("Preview: the finished Plate in the Plate Viewer, over the game, as others see it. It follows your edits.", EditorPreview.Tooltip);
    }

    [Fact]
    public async Task Preview_OpensTheOpenPlateInThePlateViewer_AfterFinishingATypingRun()
    {
        // Both editors' Preview opens the Plate Viewer on the open Plate (the owner's request of
        // October 1, 2026), whose live document it draws: a typing run in progress is committed
        // first, as one undo step, and nothing else changes.
        using var harness = await BasicHarness.NewClassicAsync();
        harness.SimulateBasicFrame();
        harness.Basic.SetText(ProfileElementRole.BasicMessage, "Hel");
        harness.Basic.SetText(ProfileElementRole.BasicMessage, "Hello");
        var viewed = new List<Guid>();

        EditorPreview.Show(harness.Session, harness.Document.ProfileId, viewed.Add);

        Assert.Equal([harness.Document.ProfileId], viewed);
        Assert.Equal("Hello", BasicSections.FindText(harness.Document, ProfileElementRole.BasicMessage)!.Text);
        harness.Session.Undo();
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public async Task PreviewFromAdvanced_FinishesACanvasDragFirst()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        var element = BasicSections.Find(harness.Document, ProfileElementRole.BasicWorld)!;
        var start = element.Position + (element.Size / 2f);
        harness.Session.BeginDrag(element, start);
        harness.Session.UpdateInteraction(start + new Vector2(30f, 0f), snap: false, snapThreshold: 0f);

        EditorPreview.Show(harness.Session, harness.Document.ProfileId, _ => { });

        Assert.Equal(ElementInteractionKind.None, harness.Session.ActiveInteraction);
        Assert.True(harness.Session.CanUndo);
    }
}
