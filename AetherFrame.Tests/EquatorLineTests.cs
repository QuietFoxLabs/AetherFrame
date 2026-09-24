using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Persistence;
using AetherFrame.Services.Packages;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Celestial Dream Equator Line: the first wide bundled artwork, drawn as a Divider through the shared art pipeline.</summary>
public class EquatorLineTests
{
    private const string EquatorDefinition = BuiltInComponentCatalog.DividerEquatorLine;

    /// <summary>The approved source is 2172 x 724 (AetherFrameAssets); the runtime copy keeps its shape.</summary>
    private const float SourceAspect = 2172f / 724f;

    private static ComponentDefinition Equator => BuiltInComponentCatalog.Find(EquatorDefinition)!;

    // ---- Identity and catalog ------------------------------------------------------------------

    [Fact]
    public void StableIds_AreFrozen()
    {
        Assert.Equal("af.divider.equator-line", BuiltInComponentCatalog.DividerEquatorLine);
        Assert.Equal("af.asset.celestial-dream.divider.equator-line", BuiltInArtCatalog.CelestialDreamEquatorLine);
        Assert.Same(BuiltInArtCatalog.EquatorLine, Equator.Art);
        Assert.Same(BuiltInArtCatalog.EquatorLine, BuiltInArtCatalog.Find(BuiltInArtCatalog.CelestialDreamEquatorLine));
    }

    [Fact]
    public void Metadata_DescribesATintableWideDivider()
    {
        var art = BuiltInArtCatalog.EquatorLine;
        Assert.Equal("Equator Line", art.Name);
        Assert.Equal(PlateComponentKind.Divider, art.Kind);
        Assert.Equal(BuiltInArtCatalog.ResourcePrefix + "Components.CelestialDream.Dividers.EquatorLine.png", art.ResourceName);
        Assert.Equal(1536, art.PixelWidth);
        Assert.Equal(512, art.PixelHeight);
        Assert.Equal(SourceAspect, art.AspectRatio, 0.001f);
        Assert.True(art.Tintable);
        Assert.InRange(art.DefaultOpacity, 0.01f, 1f);
        Assert.Equal(4f, art.SizeFactor);

        var definition = Equator;
        Assert.Equal("Equator Line", definition.Name);
        Assert.Equal(PlateComponentKind.Divider, definition.Kind);
        Assert.Equal(ComponentShape.Art, definition.Shape);
        Assert.Equal(ComponentColorSource.ThemeAccent, definition.ColorSource);
        Assert.Equal(art.DefaultOpacity, definition.DefaultAlpha);
        Assert.False(definition.RequiresAsset);
    }

    [Fact]
    public void Catalog_AppendsEquatorLine_AfterTheProceduralDividers_ForDividersOnly()
    {
        Assert.Equal(
            [BuiltInComponentCatalog.DividerLine, BuiltInComponentCatalog.DividerDiamond, EquatorDefinition],
            BuiltInComponentCatalog.OfKind(PlateComponentKind.Divider).Select(d => d.Id));
        foreach (var kind in Enum.GetValues<PlateComponentKind>().Where(k => k != PlateComponentKind.Divider))
        {
            Assert.DoesNotContain(BuiltInComponentCatalog.OfKind(kind), d => d.Id == EquatorDefinition);
        }

        Assert.Null(BuiltInComponentCatalog.Find("Equator Line"));
        Assert.Null(BuiltInComponentCatalog.Find(EquatorDefinition.ToUpperInvariant()));
        Assert.Null(BuiltInArtCatalog.Find(BuiltInArtCatalog.EquatorLine.ResourceName));
        Assert.Equal([BuiltInArtCatalog.AstrolabePivot, BuiltInArtCatalog.EquatorLine], BuiltInArtCatalog.All);
    }

    [Fact]
    public void KindMismatch_IsNeverDrawn()
    {
        var component = new PlateComponent { Kind = PlateComponentKind.CornerOrnament, DefinitionId = EquatorDefinition };
        Assert.Equal(ComponentStatus.KindMismatch, ComponentPaintPlan.Resolve(component, BuiltInComponentCatalog.Instance, out _));
        Assert.Equal(ComponentStatus.Ready, ComponentPaintPlan.Resolve(ComponentDocuments.Of(EquatorDefinition), BuiltInComponentCatalog.Instance, out _));
    }

    // ---- Basic and Advanced ----------------------------------------------------------------------

    [Fact]
    public void BasicDividerSlot_OffersEquatorLine_UnderFrameAndDecorations()
    {
        Assert.Contains(PlateComponentKind.Divider, PlateComponentEditor.BasicDecorations);
        Assert.Contains(BuiltInComponentCatalog.OfKind(PlateComponentKind.Divider), d => d.Id == EquatorDefinition && !d.RequiresAsset);

        var document = ComponentDocuments.WithAnchors();
        Assert.True(PlateComponentEditor.SetSlot(document, PlateComponentKind.Divider, EquatorDefinition, BuiltInComponentCatalog.Instance));
        var slot = PlateComponentEditor.FindSlot(document, PlateComponentKind.Divider)!;
        Assert.Equal(EquatorDefinition, slot.DefinitionId);
        Assert.Null(slot.AssetId);
        Assert.Null(slot.Color); // follows the theme accent automatically
        Assert.Throws<ArgumentException>(() => PlateComponentEditor.SetSlot(document, PlateComponentKind.CornerOrnament, EquatorDefinition, BuiltInComponentCatalog.Instance));
    }

    [Fact]
    public async Task BasicSlot_SwitchingStyles_IsOneUndoStep_AndKeepsRefinements()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        harness.Session.SetComponentSlot(PlateComponentKind.Divider, BuiltInComponentCatalog.DividerDiamond);
        var id = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.Divider)!.Id;
        harness.Session.EditComponent(id, c =>
        {
            c.Scale = 1.5f;
            c.Offset = new Vector2(3, -4);
            c.Opacity = 0.6f;
        }, continuous: false);

        harness.Session.SetComponentSlot(PlateComponentKind.Divider, EquatorDefinition);
        var slot = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.Divider)!;
        Assert.Equal(id, slot.Id);
        Assert.Equal(EquatorDefinition, slot.DefinitionId);
        Assert.Equal(1.5f, slot.Scale);
        Assert.Equal(new Vector2(3, -4), slot.Offset);
        Assert.Equal(0.6f, slot.Opacity);

        harness.Session.Undo();
        Assert.Equal(BuiltInComponentCatalog.DividerDiamond, PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.Divider)!.DefinitionId);
        harness.Session.Redo();
        Assert.Equal(EquatorDefinition, PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.Divider)!.DefinitionId);
    }

    [Fact]
    public async Task AdvancedAddComponent_AddsAnEquatorDivider_WithTheStandardControls()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        var id = harness.Session.AddComponent(EquatorDefinition)!.Value;

        var component = PlateComponentEditor.Find(harness.Document, id)!;
        Assert.Equal(PlateComponentKind.Divider, component.Kind);
        Assert.Equal(EquatorDefinition, component.DefinitionId);
        Assert.True(component.Visible);

        harness.Session.EditComponent(id, c =>
        {
            c.Color = new Vector4(0.4f, 0.7f, 1f, 1f);
            c.Opacity = 0.5f;
            c.Scale = 2f;
            c.RotationDegrees = -12f;
            c.Offset = new Vector2(10, 20);
            c.LayerOrder = 3;
            c.Visible = false;
        }, continuous: false);

        component = PlateComponentEditor.Find(harness.Document, id)!;
        Assert.Equal((0.5f, 2f, -12f, new Vector2(10, 20), 3, false), (component.Opacity, component.Scale, component.RotationDegrees, component.Offset, component.LayerOrder, component.Visible));
        Assert.DoesNotContain(ComponentDocuments.Plan(harness.Document), s => ReferenceEquals(s.Component, component)); // hidden

        harness.Session.Undo();
        Assert.Equal(1f, PlateComponentEditor.Find(harness.Document, id)!.Scale);
        harness.Session.Undo();
        Assert.Null(PlateComponentEditor.Find(harness.Document, id));
    }

    // ---- Placement and rendering -------------------------------------------------------------------

    [Fact]
    public void Placement_IsCenteredOnTheDividerLine_AtTheArtworksAspectRatio()
    {
        var document = ComponentDocuments.WithAnchors();
        var unit = ComponentPaintPlan.Unit(document);
        var rule = PlacementOf(document, BuiltInComponentCatalog.DividerLine);
        var equator = PlacementOf(document, EquatorDefinition);

        Assert.Equal(Center(rule.Rect), Center(equator.Rect)); // same line, same center
        Assert.Equal(SourceAspect, equator.Rect.Size.X / equator.Rect.Size.Y, 0.001f); // never stretched
        Assert.Equal(ComponentPaintPlan.DividerHeight * unit * 4f, equator.Rect.Size.Y, 0.001f); // a wide name: the band height limits it
        Assert.True(equator.Rect.Size.X <= rule.Rect.Size.X);
        Assert.Equal(0f, equator.RotationDegrees);
        Assert.False(equator.MirrorX || equator.MirrorY);
    }

    [Fact]
    public void Placement_UnderAShortName_IsLimitedByTheNamesWidth()
    {
        var document = ComponentDocuments.WithAnchors();
        foreach (var element in document.Elements.Where(e => e.Role is ProfileElementRole.BasicName or ProfileElementRole.BasicTitle))
        {
            element.Size = element.Size with { X = 150f };
        }

        var rule = PlacementOf(document, BuiltInComponentCatalog.DividerLine);
        var equator = PlacementOf(document, EquatorDefinition);

        Assert.Equal(150f, equator.Rect.Size.X, 0.001f);
        Assert.Equal(150f / SourceAspect, equator.Rect.Size.Y, 0.001f);
        Assert.Equal(Center(rule.Rect), Center(equator.Rect));
    }

    [Fact]
    public void ScaleRotationAndOffset_UseTheStandardComponentTransform()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(EquatorDefinition);
        document.Components = [component];
        var before = Single(document, component).Placement;

        component.Scale = 2f;
        component.RotationDegrees = 30f;
        component.Offset = new Vector2(12, -8);
        var after = Single(document, component).Placement;

        Assert.Equal(before.Rect.Size * 2f, after.Rect.Size);
        Assert.Equal(Center(before.Rect) + new Vector2(12, -8), Center(after.Rect));
        Assert.Equal(30f, after.RotationDegrees);
        Assert.Equal(SourceAspect, after.Rect.Size.X / after.Rect.Size.Y, 0.001f);

        // The drawn quad rotates around the Divider's own center and keeps the artwork's proportions.
        var quad = Primitive(document, component, after);
        Assert.Equal(Center(after.Rect).X, (quad.A.X + quad.C.X) / 2f, 0.01f);
        Assert.Equal(Center(after.Rect).Y, (quad.A.Y + quad.C.Y) / 2f, 0.01f);
        Assert.Equal(SourceAspect, Vector2.Distance(quad.A, quad.B) / Vector2.Distance(quad.A, quad.D), 0.001f);
        Assert.Equal(30f, MathF.Atan2(quad.B.Y - quad.A.Y, quad.B.X - quad.A.X) * 180f / MathF.PI, 0.01f);
    }

    [Fact]
    public void Scale_IsBoundedLikeEveryComponent()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(EquatorDefinition);
        document.Components = [component];
        var normal = Single(document, component).Placement.Rect.Size;

        component.Scale = 100f;
        Assert.Equal(normal * PlateComponentLimits.MaxScale, Single(document, component).Placement.Rect.Size);
        component.Scale = 0f;
        Assert.Equal(normal * PlateComponentLimits.MinScale, Single(document, component).Placement.Rect.Size);
    }

    [Fact]
    public void Tint_IsTheThemeAccentByDefault_ThenComponentColorTimesOpacity()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(EquatorDefinition);
        document.Components = [component];
        var placement = Single(document, component).Placement;

        var themed = Primitive(document, component, placement);
        Assert.Equal(ComponentPrimitiveKind.Art, themed.Kind);
        Assert.Equal(Equator.DefaultColor(document), themed.Color);

        component.Color = new Vector4(0.3f, 0.6f, 0.9f, 0.8f);
        component.Opacity = 0.5f;
        Assert.Equal(new Vector4(0.3f, 0.6f, 0.9f, 0.4f), Primitive(document, component, placement).Color);

        component.Opacity = 0f;
        var output = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, component, Equator, placement, output);
        Assert.Empty(output);
    }

    [Fact]
    public void DecorationLayer_AndLayerOrder_AreTheStandardDividerOnes()
    {
        var document = ComponentDocuments.WithAnchors();
        var equator = ComponentDocuments.Of(EquatorDefinition, layerOrder: 5);
        var rule = ComponentDocuments.Of(BuiltInComponentCatalog.DividerLine, layerOrder: -5);
        document.Components = [equator, rule];

        var steps = ComponentDocuments.Plan(document).Where(s => !s.IsElement).ToList();
        Assert.All(steps, s => Assert.Equal(PlateLayer.Decorations, s.Layer));
        Assert.Same(rule, steps[0].Component);
        Assert.Same(equator, steps[1].Component);
    }

    // ---- Visual bounds -----------------------------------------------------------------------------

    [Fact]
    public void VisualBounds_WithoutOverflow_AreTheLogicalCanvas()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(EquatorDefinition)];
        Assert.Equal(ProfileVisualBounds.Logical(document), ProfileVisualBounds.Compute(document));
    }

    [Fact]
    public void VisualBounds_IncludeOverflow_FromTheDrawnPlacement()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(EquatorDefinition);
        component.Scale = 3f;
        component.RotationDegrees = 20f;
        component.Offset = new Vector2(400, -150);
        document.Components = [component];

        var plan = ComponentDocuments.Plan(document);
        var step = Assert.Single(plan, s => ReferenceEquals(s.Component, component));
        var (min, max) = ComponentPaintPlan.GetVisualBounds(plan, component)!.Value;
        var visual = ProfileVisualBounds.Compute(document);

        Assert.True(max.X > document.CanvasWidth && min.Y < 0f, "the Divider is pushed past the top-right of the Plate");
        Assert.Equal(Vector2.Min(Vector2.Zero, min), visual.Min);
        Assert.Equal(Vector2.Max(new Vector2(document.CanvasWidth, document.CanvasHeight), max), visual.Max);

        // The bounds are the fitted artwork's, not the taller band's or the name's width.
        var expected = RotationGeometry.GetVisualBounds(step.Placement.Rect.Position, step.Placement.Rect.Size, 20f);
        Assert.Equal(expected.Min, min);
        Assert.Equal(expected.Max, max);
        Assert.Equal(SourceAspect, step.Placement.Rect.Size.X / step.Placement.Rect.Size.Y, 0.001f);
        Assert.Equal(1280f, document.CanvasWidth); // the logical Plate is never changed
    }

    [Fact]
    public void VisualBounds_WithTheRenderersMeasurer_FollowTheDividerUnderTheMeasuredName()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(EquatorDefinition);
        component.Offset = new Vector2(-900, 0); // far past the Plate's left edge
        document.Components = [component];
        var measurer = new FakeMeasurer();
        foreach (var text in document.Elements.OfType<TextProfileElement>().Where(e => e.Role is ProfileElementRole.BasicName or ProfileElementRole.BasicTitle))
        {
            text.LayoutVersion = TextProfileElement.CurrentLayoutVersion; // a current single-line name: measured, like a Basic Plate's
            text.Wrap = false;
        }

        // The plan the renderer draws: the Divider sits under the name's measured text, not its whole box.
        var drawn = document.Elements.Where(e => e.Visible).OrderBy(e => e.ZIndex).ToList();
        var rendered = new List<PaintStep>();
        ComponentPaintPlan.Build(document, drawn, BuiltInComponentCatalog.Instance, rendered, e => measurer.TryMeasureNaturalWidth(e, out var w) ? w : null);
        var step = Assert.Single(rendered, s => ReferenceEquals(s.Component, component));
        var unmeasured = Single(document, component).Placement;
        Assert.NotEqual(unmeasured.Rect, step.Placement.Rect);

        var visual = ProfileVisualBounds.Compute(document, measurer);
        Assert.Equal(step.Placement.Rect.Position.X, visual.Min.X, 0.001f);
        Assert.True(visual.Min.X < 0f);
        Assert.Equal(ProfileVisualBounds.Compute(document).Min.X, unmeasured.Rect.Position.X, 0.001f); // no measurer: the whole-box fallback
    }

    // ---- Bundled resource ---------------------------------------------------------------------------

    [Fact]
    public void Resource_IsEmbedded_AndDecodesAtItsDeclaredSize()
    {
        var bytes = BuiltInArtTests.ReadResource(BuiltInArtCatalog.EquatorLine.ResourceName);
        Assert.True(bytes.Length < 512 * 1024, $"runtime PNG is {bytes.Length} bytes");

        var image = BundledArtImage.DecodePng(bytes);
        Assert.Equal((1536, 512), (image.Width, image.Height));
        Assert.Equal(SourceAspect, image.Width / (float)image.Height, 0.001f);
    }

    [Fact]
    public void Resource_HasGenuineSoftAlpha_AndNoBakedBackground()
    {
        var image = BundledArtImage.DecodePng(BuiltInArtTests.ReadResource(BuiltInArtCatalog.EquatorLine.ResourceName));
        var count = image.Width * image.Height;
        var alpha = Enumerable.Range(0, count).Select(i => image.Rgba[(i * 4) + 3]).ToArray();

        var transparent = alpha.Count(a => a == 0) / (double)count;
        var partial = alpha.Count(a => a is > 0 and < 255) / (double)count;
        Assert.InRange(transparent, 0.6, 0.95); // mostly empty: no background
        Assert.True(partial > 0.1, "the soft glow must survive as partial alpha");
        Assert.Contains(alpha, a => a >= 200); // the line core

        // All four image corners are empty, and no dense texel is dark (nothing black baked in).
        foreach (var (x, y) in new[] { (0, 0), (image.Width - 1, 0), (0, image.Height - 1), (image.Width - 1, image.Height - 1) })
        {
            Assert.Equal(0, image.Rgba[(((y * image.Width) + x) * 4) + 3]);
        }

        for (var i = 0; i < image.Rgba.Length; i += 4)
        {
            if (image.Rgba[i + 3] >= 16)
            {
                Assert.True(image.Rgba[i] >= 64, $"texel {i / 4} is dark under alpha {image.Rgba[i + 3]}");
            }
        }
    }

    [Fact]
    public void Resource_IsGreyscale_WithWhiteTransparentTexels_SoTheTintIsExact()
    {
        var image = BundledArtImage.DecodePng(BuiltInArtTests.ReadResource(BuiltInArtCatalog.EquatorLine.ResourceName));
        for (var i = 0; i < image.Rgba.Length; i += 4)
        {
            Assert.True(image.Rgba[i] == image.Rgba[i + 1] && image.Rgba[i + 1] == image.Rgba[i + 2], $"texel {i / 4} is not grey");
            if (image.Rgba[i + 3] == 0)
            {
                Assert.Equal(255, image.Rgba[i]);
            }
        }

        Assert.Contains(Enumerable.Range(0, image.Width * image.Height), i => image.Rgba[i * 4] == 255 && image.Rgba[(i * 4) + 3] >= 200); // a white core
    }

    [Fact]
    public void Resource_LineRunsThroughTheVerticalCenter()
    {
        var image = BundledArtImage.DecodePng(BuiltInArtTests.ReadResource(BuiltInArtCatalog.EquatorLine.ResourceName));
        var rowCoverage = Enumerable.Range(0, image.Height)
            .Select(y => Enumerable.Range(0, image.Width).Sum(x => image.Rgba[(((y * image.Width) + x) * 4) + 3]))
            .ToArray();
        var peak = Array.IndexOf(rowCoverage, rowCoverage.Max());
        Assert.InRange(peak, (image.Height / 2) - 6, (image.Height / 2) + 6); // so centering on the Divider line is right
    }

    [Fact]
    public void Levels_HalveTheWideArtwork_KeepingItsShapeAndCoverage()
    {
        var levels = BundledArtImage.BuildLevels(BundledArtImage.DecodePng(BuiltInArtTests.ReadResource(BuiltInArtCatalog.EquatorLine.ResourceName)));

        Assert.Equal([(1536, 512), (768, 256), (384, 128), (192, 64), (96, 32)], levels.Select(l => (l.Width, l.Height)));
        Assert.Equal([1536, 768, 384, 192, 96], levels.Select(l => l.LongSide));
        var coverage = levels.Select(l => Enumerable.Range(0, l.Width * l.Height).Average(i => l.Rgba[(i * 4) + 3] / 255.0)).ToList();
        Assert.All(coverage, c => Assert.InRange(c, coverage[0] * 0.97, coverage[0] * 1.03));
        foreach (var level in levels)
        {
            for (var i = 0; i < level.Rgba.Length; i += 4)
            {
                Assert.True(level.Rgba[i] == level.Rgba[i + 1] && level.Rgba[i + 1] == level.Rgba[i + 2]);
                if (level.Rgba[i + 3] == 0)
                {
                    Assert.Equal(255, level.Rgba[i]);
                }
            }
        }
    }

    [Theory]
    [InlineData(80f, 4)]
    [InlineData(96f, 4)]
    [InlineData(97f, 3)]
    [InlineData(288f, 2)]
    [InlineData(540f, 1)]
    [InlineData(840f, 0)]
    [InlineData(3000f, 0)]
    public void LevelSelection_ComparesTheDrawnWidthWithEachLevelsLongSide(float screenWidth, int expected)
    {
        Assert.Equal(expected, BundledArtImage.SelectLevel([1536, 768, 384, 192, 96], screenWidth));
    }

    [Theory]
    [InlineData(512, 512, true)]
    [InlineData(1536, 512, true)]
    [InlineData(512, 1536, true)]
    [InlineData(64, 16, true)]
    [InlineData(4, 4, true)]
    [InlineData(1024, 768, false)] // 1024 isn't a whole multiple of 768
    [InlineData(600, 200, false)] // 200 isn't a power of two
    [InlineData(4096, 1024, false)] // larger than MaxSize
    [InlineData(0, 512, false)]
    public void Decoder_AcceptsSquareAndWholeMultipleWideArt_Only(int width, int height, bool supported)
    {
        Assert.Equal(supported, BundledArtImage.IsSupportedSize(width, height));
    }

    [Fact]
    public void Decoder_ReadsWideArt_AndRejectsUnsupportedShapes()
    {
        var pixels = new byte[12 * 4 * 4];
        new Random(3).NextBytes(pixels);
        var decoded = BundledArtImage.DecodePng(BuiltInArtTests.EncodePng(12, 4, pixels, 4));
        Assert.Equal((12, 4), (decoded.Width, decoded.Height));
        Assert.Equal(pixels, decoded.Rgba);

        Assert.Throws<InvalidDataException>(() => BundledArtImage.DecodePng(BuiltInArtTests.EncodePng(6, 4, new byte[6 * 4 * 4], 0)));
    }

    // ---- Persistence and portability ---------------------------------------------------------------

    [Fact]
    public void Serialization_StoresOnlyTheLogicalDefinitionId()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(EquatorDefinition)];

        var stored = PlateDocuments.ToJson(document)["Components"]!.AsArray().Single()!.AsObject();
        Assert.Equal(EquatorDefinition, (string?)stored["DefinitionId"]);
        Assert.Equal((int)PlateComponentKind.Divider, (int)stored["Kind"]!);
        AssertNoPhysicalReference(PlateDocuments.ToJson(document).ToJsonString());
    }

    [Fact]
    public async Task SaveAndReload_PreservesTheDivider()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plateId = await CreatePlateWithEquatorAsync(library);

        var reloaded = await fixture.LoadAsync();
        var component = reloaded.OpenDocumentForEditing(plateId).Components!.Single();
        Assert.Equal(EquatorDefinition, component.DefinitionId);
        Assert.Equal(PlateComponentKind.Divider, component.Kind);
        Assert.Equal(new Vector4(0.7f, 0.8f, 1f, 1f), component.Color);
        Assert.Equal((1.5f, 8f, new Vector2(0, 6), 0.75f), (component.Scale, component.RotationDegrees, component.Offset, component.Opacity));
        Assert.Null(component.AssetId);
        AssertNoPhysicalReference(fixture.ReadPlateJson(plateId));
    }

    [Fact]
    public async Task Template_RoundTrip_PreservesTheDivider()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var sourceId = await CreatePlateWithEquatorAsync(fixture.PlateLibrary);
        var source = fixture.PlateLibrary.OpenDocumentForEditing(sourceId);

        var templateId = await templates.SaveAsTemplateAsync(sourceId, "Equator");
        Assert.True(PlateComponent.ListsEqual(source.Components, templates.GetSavedDocument(templateId)!.Components));
        AssertNoPhysicalReference(fixture.ReadTemplateJson(templateId));

        var created = await templates.InstantiateAsync(templateId, null);
        Assert.True(PlateComponent.ListsEqual(source.Components, fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId).Components));
    }

    [Fact]
    public async Task Duplicate_PreservesTheDivider_WithNoImageData()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var sourceId = await CreatePlateWithEquatorAsync(library);

        var copyId = await library.DuplicatePlateAsync(sourceId);

        Assert.True(PlateComponent.ListsEqual(library.OpenDocumentForEditing(sourceId).Components, library.OpenDocumentForEditing(copyId).Components));
        var scan = await library.ScanAssetReferencesAsync();
        Assert.True(scan.IsComplete);
        Assert.Empty(scan.ReferencedAssetIds);
        AssertNoPhysicalReference(fixture.ReadPlateJson(copyId));
    }

    [Fact]
    public async Task Package_RoundTrip_CarriesTheId_NotTheArtwork()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var plateId = await CreatePlateWithEquatorAsync(library);

        var path = fixture.Export(packages, plateId);
        var entries = PackageFiles.Read(path);
        Assert.DoesNotContain(entries, e => e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        foreach (var entry in entries)
        {
            AssertNoPhysicalReference(Encoding.UTF8.GetString(entry.Bytes));
        }

        var profile = PackageFiles.Json(PackageFiles.Entry(entries, PackagePaths.ProfilePath));
        Assert.Equal(EquatorDefinition, (string?)profile["Components"]![0]!["DefinitionId"]);

        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport, string.Join("; ", staged.Diagnostics.Errors));
        Assert.Empty(staged.Diagnostics.Warnings);
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(library.OpenDocumentForEditing(plateId).Components!.Single().ContentEquals(library.OpenDocumentForEditing(result.PlateId).Components!.Single()));
    }

    [Fact]
    public void BuildWithoutTheArt_KeepsItUndrawn_NeverSubstitutesAnotherDivider()
    {
        var older = new CatalogWithout(EquatorDefinition);
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(EquatorDefinition);
        component.Scale = 1.5f;
        document.Components = [component];

        Assert.Equal(ComponentStatus.MissingDefinition, ComponentPaintPlan.Resolve(component, older, out var definition));
        Assert.Null(definition);
        var steps = new List<PaintStep>();
        ComponentPaintPlan.Build(document, document.Elements.OrderBy(e => e.ZIndex).ToList(), older, steps);
        Assert.All(steps, s => Assert.True(s.IsElement));

        // Kept verbatim through save and load, still bound to the Basic Divider slot.
        var reloaded = ComponentDocuments.RoundTrip(document);
        Assert.Equal(EquatorDefinition, reloaded.Components!.Single().DefinitionId);
        Assert.Equal(1.5f, reloaded.Components!.Single().Scale);
        Assert.Same(component, PlateComponentEditor.FindSlot(document, PlateComponentKind.Divider));
    }

    [Fact]
    public void UnknownFutureDivider_IsPreservedVerbatim_AndNotDrawn()
    {
        var json = PlateDocuments.ToJson(ComponentDocuments.WithAnchors());
        json["Components"] = JsonNode.Parse("""
            [ { "Kind": 6, "DefinitionId": "af.divider.meridian-arc", "Scale": 1.25, "FutureArtHint": { "Variant": 3 } } ]
            """);
        var document = PlateDocuments.Materialize(JsonNode.Parse(json.ToJsonString())!.AsObject());

        var component = document.Components!.Single();
        Assert.Equal(ComponentStatus.MissingDefinition, ComponentPaintPlan.Resolve(component, BuiltInComponentCatalog.Instance, out _));
        Assert.DoesNotContain(ComponentDocuments.Plan(document), s => ReferenceEquals(s.Component, component));

        var saved = PlateDocuments.ToJson(document)["Components"]![0]!;
        Assert.Equal("af.divider.meridian-arc", (string?)saved["DefinitionId"]);
        Assert.Equal(3, (int)saved["FutureArtHint"]!["Variant"]!);
    }

    // ---- Nothing else changed ------------------------------------------------------------------------

    [Fact]
    public void ProceduralDividers_KeepTheirExactBand_AndGeometry()
    {
        var document = ComponentDocuments.WithAnchors();
        var unit = ComponentPaintPlan.Unit(document);
        var identity = new ElementRect(new Vector2(480, 60), new Vector2(700, 95)); // name box united with title box
        var band = new ElementRect(
            new Vector2(identity.Position.X, identity.Position.Y + identity.Size.Y + (ComponentPaintPlan.DividerGap * unit)),
            new Vector2(identity.Size.X, ComponentPaintPlan.DividerHeight * unit));

        foreach (var id in new[] { BuiltInComponentCatalog.DividerLine, BuiltInComponentCatalog.DividerDiamond })
        {
            Assert.Null(BuiltInComponentCatalog.Find(id)!.Art);
            Assert.Equal(1f, ComponentPaintPlan.ArtSizeFactor(BuiltInComponentCatalog.Find(id)!));
            var placement = PlacementOf(document, id);
            Assert.Equal(band, placement.Rect);
            Assert.NotEmpty(Build(document, id, placement));
        }
    }

    [Fact]
    public void AstrolabePivot_KeepsItsExactSquarePlacement()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot);
        component.Scale = 1.37f;
        document.Components = [component];
        var size = ComponentPaintPlan.CornerSize * ComponentPaintPlan.Unit(document) * 2f * 1.37f;

        var steps = ComponentDocuments.Plan(document).Where(s => !s.IsElement).ToList();
        Assert.Equal(4, steps.Count);
        Assert.All(steps, s => Assert.Equal(new Vector2(size), s.Placement.Rect.Size));
        Assert.Equal(1f, BuiltInArtCatalog.AstrolabePivot.AspectRatio);
        Assert.Equal(new Vector2(size), ComponentPaintPlan.FitAspect(new Vector2(size), 1f)); // square art in a square box: untouched
    }

    [Fact]
    public void FitAspect_FitsInsideTheBox_WithoutStretching()
    {
        Assert.Equal(new Vector2(300, 100), ComponentPaintPlan.FitAspect(new Vector2(700, 100), 3f));
        Assert.Equal(new Vector2(150, 50), ComponentPaintPlan.FitAspect(new Vector2(150, 100), 3f));
        Assert.Equal(new Vector2(90, 30), ComponentPaintPlan.FitAspect(new Vector2(90, 30), 3f));
        Assert.Equal(new Vector2(90, 30), ComponentPaintPlan.FitAspect(new Vector2(90, 30), float.NaN));
        Assert.Equal(Vector2.Zero, ComponentPaintPlan.FitAspect(Vector2.Zero, 3f));
    }

    [Fact]
    public void OldPlates_LoadAndSaveUnchanged()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = ComponentDocuments.OneOfEach();
        var before = PlateDocuments.ToJson(document);

        var after = PlateDocuments.ToJson(PlateDocuments.Deserialize((JsonObject)before.DeepClone())!);

        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.DoesNotContain(document.Components, c => c.DefinitionId == EquatorDefinition);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private static Vector2 Center(ElementRect rect) => rect.Position + (rect.Size / 2f);

    private static ComponentPlacement PlacementOf(ProfileDocument document, string definitionId)
    {
        var component = ComponentDocuments.Of(definitionId);
        document.Components = [component];
        return Single(document, component).Placement;
    }

    private static PaintStep Single(ProfileDocument document, PlateComponent component) =>
        Assert.Single(ComponentDocuments.Plan(document), s => ReferenceEquals(s.Component, component));

    private static ComponentPrimitive Primitive(ProfileDocument document, PlateComponent component, ComponentPlacement placement)
    {
        var output = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, component, Equator, placement, output);
        var primitive = Assert.Single(output);
        Assert.Equal(ComponentPrimitiveKind.Art, primitive.Kind);
        return primitive;
    }

    private static List<ComponentPrimitive> Build(ProfileDocument document, string definitionId, ComponentPlacement placement)
    {
        var output = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, ComponentDocuments.Of(definitionId), BuiltInComponentCatalog.Find(definitionId)!, placement, output);
        return output;
    }

    private static async Task<Guid> CreatePlateWithEquatorAsync(Services.Plates.PlateLibraryService library)
    {
        var created = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Equator", new PlateStarterContent(null));
        var document = library.OpenDocumentForEditing(created.PlateId);
        var component = ComponentDocuments.Of(EquatorDefinition);
        component.Color = new Vector4(0.7f, 0.8f, 1f, 1f);
        component.Scale = 1.5f;
        component.RotationDegrees = 8f;
        component.Offset = new Vector2(0, 6);
        component.Opacity = 0.75f;
        document.Components = [component];
        await library.SavePlateDocumentAsync(document);
        return created.PlateId;
    }

    private static void AssertNoPhysicalReference(string text)
    {
        Assert.DoesNotContain(".png", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EquatorLine", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CelestialDream", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dividers.", text, StringComparison.Ordinal);
        Assert.DoesNotContain(BuiltInArtCatalog.ResourcePrefix, text, StringComparison.Ordinal);
        Assert.DoesNotContain(BuiltInArtCatalog.CelestialDreamEquatorLine, text, StringComparison.Ordinal);
        Assert.DoesNotContain("AetherFrameAssets", text, StringComparison.Ordinal);
    }

    private sealed class CatalogWithout(string excludedId) : IComponentCatalog
    {
        public ComponentDefinition? Find(string? id) => id == excludedId ? null : BuiltInComponentCatalog.Find(id);
    }
}
