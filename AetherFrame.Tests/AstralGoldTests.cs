using System;
using System.Collections.Generic;
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

/// <summary>
/// The Astral Gold family: seven authored full-color built-in graphical Components, the Authored
/// Color art policy they introduce, their placements, their bundled runtime PNGs and their portability.
/// </summary>
public class AstralGoldTests
{
    /// <summary>Every Astral Gold definition: (definition id, asset id, kind, display name).</summary>
    private static readonly (string Id, string AssetId, PlateComponentKind Kind, string Name)[] Family =
    [
        ("af.plate-frame.astral-gold-orbital-ring", "af.asset.astral-gold.plate-frame.orbital-ring", PlateComponentKind.PlateFrame, "Orbital Ring"),
        ("af.portrait-frame.astral-gold-crescent-cradle", "af.asset.astral-gold.portrait-frame.crescent-cradle", PlateComponentKind.PortraitFrame, "Crescent Cradle"),
        ("af.portrait-overlay.astral-gold-falling-stardust", "af.asset.astral-gold.portrait-overlay.falling-stardust", PlateComponentKind.PortraitOverlay, "Falling Stardust"),
        ("af.corner-ornament.astral-gold-astrolabe-pivot", "af.asset.astral-gold.corner-ornament.astrolabe-pivot", PlateComponentKind.CornerOrnament, "Astrolabe Pivot"),
        ("af.name-backing.astral-gold-orbital-constellation-underlay", "af.asset.astral-gold.name-backing.orbital-constellation-underlay", PlateComponentKind.NameBacking, "Orbital Constellation Underlay"),
        ("af.divider.astral-gold-equator-line", "af.asset.astral-gold.divider.equator-line", PlateComponentKind.Divider, "Equator Line"),
        ("af.section-header.astral-gold-star-pinned-underline", "af.asset.astral-gold.section-header.star-pinned-underline", PlateComponentKind.SectionHeader, "Star Pinned Underline"),
    ];

    public static IEnumerable<object[]> FamilyIds() => Family.Select(f => new object[] { f.Id });

    private static ComponentDefinition Definition(string id) => BuiltInComponentCatalog.Find(id)!;

    // ---- Catalog and identity ----------------------------------------------------------------------

    [Fact]
    public void AllSevenDefinitions_AreRegistered_WithFrozenIds_KindsAndNames()
    {
        Assert.Equal(
            [
                BuiltInComponentCatalog.PlateFrameAstralGoldOrbitalRing, BuiltInComponentCatalog.PortraitFrameAstralGoldCrescentCradle,
                BuiltInComponentCatalog.PortraitOverlayAstralGoldFallingStardust, BuiltInComponentCatalog.CornerOrnamentAstralGoldAstrolabePivot,
                BuiltInComponentCatalog.NameBackingAstralGoldOrbitalConstellationUnderlay, BuiltInComponentCatalog.DividerAstralGoldEquatorLine,
                BuiltInComponentCatalog.SectionHeaderAstralGoldStarPinnedUnderline,
            ],
            Family.Select(f => f.Id));

        foreach (var (id, assetId, kind, name) in Family)
        {
            var definition = Definition(id);
            Assert.Equal(kind, definition.Kind);
            Assert.Equal(name, definition.Name);
            Assert.Equal(ComponentShape.Art, definition.Shape);
            Assert.False(definition.RequiresAsset);
            Assert.Equal(assetId, definition.Art!.Id);
            Assert.Same(definition.Art, BuiltInArtCatalog.Find(assetId));
            Assert.Equal(kind, definition.Art.Kind);
            Assert.StartsWith("Astral Gold:", definition.Description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Ids_AreUnique_WellFormed_AndNeverCollideWithCelestialDream()
    {
        var definitionIds = BuiltInComponentCatalog.All.Select(d => d.Id).ToList();
        var assetIds = BuiltInArtCatalog.All.Select(a => a.Id).ToList();
        Assert.Equal(definitionIds.Count, definitionIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(assetIds.Count, assetIds.Distinct(StringComparer.Ordinal).Count());

        foreach (var (id, assetId, _, _) in Family)
        {
            Assert.Matches("^af\\.[a-z-]+\\.astral-gold-[a-z-]+$", id);
            Assert.Matches("^af\\.asset\\.astral-gold\\.[a-z-]+\\.[a-z-]+$", assetId);
            Assert.True(id.Length <= PlateComponentLimits.MaxDefinitionIdLength, id);
        }

        // Same display names, separate identities.
        Assert.NotEqual(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot, BuiltInComponentCatalog.CornerOrnamentAstralGoldAstrolabePivot);
        Assert.NotEqual(BuiltInComponentCatalog.DividerEquatorLine, BuiltInComponentCatalog.DividerAstralGoldEquatorLine);
        Assert.NotSame(BuiltInArtCatalog.AstrolabePivot, BuiltInArtCatalog.AstralAstrolabePivot);
        Assert.NotSame(BuiltInArtCatalog.EquatorLine, BuiltInArtCatalog.AstralEquatorLine);
    }

    [Fact]
    public void FamilyMetadata_IdentifiesAstralGold_AndPickersLabelTheFamily()
    {
        foreach (var (id, _, _, name) in Family)
        {
            var definition = Definition(id);
            Assert.Equal(BuiltInArtCatalog.AstralGold, definition.Family);
            Assert.Equal("Astral Gold", definition.Art!.Family);
            Assert.Equal($"{name} (Astral Gold)", definition.DisplayName);
        }

        Assert.Equal("Astrolabe Pivot (Celestial Dream)", Definition(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot).DisplayName);
        Assert.Equal("Equator Line (Celestial Dream)", Definition(BuiltInComponentCatalog.DividerEquatorLine).DisplayName);
        Assert.All(BuiltInComponentCatalog.All.Where(d => d.Art is null), d => Assert.Equal(d.Name, d.DisplayName));

        // No two styles of one kind ever show the same label.
        foreach (var kind in Enum.GetValues<PlateComponentKind>())
        {
            var labels = BuiltInComponentCatalog.OfKind(kind).Select(d => d.DisplayName).ToList();
            Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Theory]
    [MemberData(nameof(FamilyIds))]
    public void EachDefinition_IsOfferedOnlyForItsOwnKind_InBasicAndAdvanced(string id)
    {
        var definition = Definition(id);
        Assert.Contains(BuiltInComponentCatalog.OfKind(definition.Kind), d => d.Id == id);
        foreach (var kind in Enum.GetValues<PlateComponentKind>().Where(k => k != definition.Kind))
        {
            Assert.DoesNotContain(BuiltInComponentCatalog.OfKind(kind), d => d.Id == id);
        }

        // Basic: every Astral Gold kind has a Basic slot, and the slot accepts only its own kind's styles.
        Assert.Contains(definition.Kind, PlateComponentEditor.BasicSlots.Concat(PlateComponentEditor.BasicDecorations));
        var document = ComponentDocuments.WithAnchors();
        Assert.True(PlateComponentEditor.SetSlot(document, definition.Kind, id, BuiltInComponentCatalog.Instance));
        Assert.Equal(id, PlateComponentEditor.FindSlot(document, definition.Kind)!.DefinitionId);
        var wrongKind = definition.Kind == PlateComponentKind.Divider ? PlateComponentKind.SectionHeader : PlateComponentKind.Divider;
        Assert.Throws<ArgumentException>(() => PlateComponentEditor.SetSlot(document, wrongKind, id, BuiltInComponentCatalog.Instance));

        var mismatched = new PlateComponent { Kind = wrongKind, DefinitionId = id };
        Assert.Equal(ComponentStatus.KindMismatch, ComponentPaintPlan.Resolve(mismatched, BuiltInComponentCatalog.Instance, out _));
    }

    [Fact]
    public async Task AdvancedAddComponent_AddsEveryAstralGoldStyle_WithUndo()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        foreach (var (id, _, kind, _) in Family)
        {
            var added = harness.Session.AddComponent(id)!.Value;
            var component = PlateComponentEditor.Find(harness.Document, added)!;
            Assert.Equal((kind, id), (component.Kind, component.DefinitionId));
        }

        Assert.Equal(Family.Length, harness.Document.Components!.Count);
        harness.Session.Undo();
        Assert.Equal(Family.Length - 1, harness.Document.Components!.Count);
    }

    // ---- Authored Color ------------------------------------------------------------------------------

    [Fact]
    public void AuthoredColor_IsTheFamilysPolicy_AndCelestialDreamStaysTintable()
    {
        foreach (var (id, _, _, _) in Family)
        {
            var definition = Definition(id);
            Assert.Equal(ArtColorMode.AuthoredColor, definition.Art!.ColorMode);
            Assert.False(definition.Art.Tintable);
            Assert.True(definition.UsesAuthoredColor);
            Assert.Equal(ComponentColorSource.White, definition.ColorSource);
            Assert.Equal(1f, definition.DefaultAlpha);
        }

        foreach (var art in new[] { BuiltInArtCatalog.AstrolabePivot, BuiltInArtCatalog.EquatorLine })
        {
            Assert.Equal(ArtColorMode.Tintable, art.ColorMode);
            Assert.True(art.Tintable);
        }

        Assert.Equal(ComponentColorSource.ThemeAccent, Definition(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot).ColorSource);
        Assert.False(Definition(BuiltInComponentCatalog.DividerEquatorLine).UsesAuthoredColor);
        Assert.All(BuiltInComponentCatalog.All.Where(d => d.Art is null), d => Assert.False(d.UsesAuthoredColor));
    }

    [Theory]
    [MemberData(nameof(FamilyIds))]
    public void AuthoredColor_IgnoresTheComponentColor_AndOnlyOpacityApplies(string id)
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(id);
        var placement = new ComponentPlacement(new ElementRect(new Vector2(20, 20), new Vector2(300, 100)), 0f, false, false);

        Assert.Equal(new Vector4(1f, 1f, 1f, 1f), Primitive(document, component, placement).Color); // theme never tints it

        component.Color = new Vector4(1f, 0f, 0f, 0.25f); // a stored color (e.g. from a tintable style) changes nothing
        Assert.Equal(new Vector4(1f, 1f, 1f, 1f), Primitive(document, component, placement).Color);

        component.Opacity = 0.4f;
        Assert.Equal(new Vector4(1f, 1f, 1f, 0.4f), Primitive(document, component, placement).Color);
        Assert.Equal(new Vector4(1f, 0f, 0f, 0.25f), component.Color); // kept, for a later switch back

        component.Opacity = 0f;
        var output = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, component, Definition(id), placement, output);
        Assert.Empty(output);
    }

    [Fact]
    public void TintableArt_StillTakesTheComponentColorTimesOpacity()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.DividerEquatorLine);
        var placement = new ComponentPlacement(new ElementRect(new Vector2(20, 20), new Vector2(300, 100)), 0f, false, false);
        var definition = Definition(BuiltInComponentCatalog.DividerEquatorLine);

        Assert.Equal(definition.DefaultColor(document), Primitive(document, component, placement).Color);
        component.Color = new Vector4(0.2f, 0.4f, 0.6f, 0.8f);
        component.Opacity = 0.5f;
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 0.4f), Primitive(document, component, placement).Color);
    }

    [Fact]
    public void SwitchingBetweenTintableAndAuthoredStyles_KeepsTheStoredColorAndTransforms()
    {
        var document = ComponentDocuments.WithAnchors();
        PlateComponentEditor.SetSlot(document, PlateComponentKind.Divider, BuiltInComponentCatalog.DividerEquatorLine, BuiltInComponentCatalog.Instance);
        var id = PlateComponentEditor.FindSlot(document, PlateComponentKind.Divider)!.Id;
        PlateComponentEditor.Update(document, id, c =>
        {
            c.Color = new Vector4(0.3f, 0.9f, 0.5f, 1f);
            c.Scale = 1.4f;
            c.Offset = new Vector2(5, -3);
            c.RotationDegrees = 7f;
            c.Opacity = 0.7f;
        });

        PlateComponentEditor.SetSlot(document, PlateComponentKind.Divider, BuiltInComponentCatalog.DividerAstralGoldEquatorLine, BuiltInComponentCatalog.Instance);
        var astral = PlateComponentEditor.FindSlot(document, PlateComponentKind.Divider)!;
        Assert.Equal((id, 1.4f, new Vector2(5, -3), 7f, 0.7f), (astral.Id, astral.Scale, astral.Offset, astral.RotationDegrees, astral.Opacity));
        Assert.Equal(new Vector4(0.3f, 0.9f, 0.5f, 1f), astral.Color);

        PlateComponentEditor.SetSlot(document, PlateComponentKind.Divider, BuiltInComponentCatalog.DividerEquatorLine, BuiltInComponentCatalog.Instance);
        var placement = Single(document, astral).Placement;
        Assert.Equal(new Vector4(0.3f, 0.9f, 0.5f, 0.7f), Primitive(document, astral, placement).Color); // tinted again
    }

    // ---- Placement ---------------------------------------------------------------------------------------

    [Fact]
    public void PlateFrame_OrbitalRing_CoversTheWholePlate_AtItsAspect_WhileProceduralFramesKeepTheirInset()
    {
        var document = ComponentDocuments.WithAnchors();
        var ring = PlacementOf(document, BuiltInComponentCatalog.PlateFrameAstralGoldOrbitalRing);
        Assert.Equal(new ElementRect(Vector2.Zero, new Vector2(1280, 720)), ring.Rect); // 16:9 art on a 16:9 Plate: exactly the canvas

        var inset = ComponentPaintPlan.PlateFrameInset * ComponentPaintPlan.Unit(document);
        foreach (var id in new[] { BuiltInComponentCatalog.PlateFrameLine, BuiltInComponentCatalog.PlateFrameDouble, BuiltInComponentCatalog.PlateFrameNotched })
        {
            Assert.Equal(new ElementRect(new Vector2(inset), new Vector2(1280 - (2 * inset), 720 - (2 * inset))), PlacementOf(document, id).Rect);
        }

        // Another canvas shape: fitted, centered, never stretched.
        document.CanvasHeight = 800f;
        var tall = PlacementOf(document, BuiltInComponentCatalog.PlateFrameAstralGoldOrbitalRing);
        Assert.Equal(1280f, tall.Rect.Size.X, 0.01f);
        Assert.Equal(1280f * 864f / 1536f, tall.Rect.Size.Y, 0.01f);
        Assert.Equal(400f, tall.Rect.Position.Y + (tall.Rect.Size.Y / 2f), 0.01f);
    }

    [Fact]
    public void PortraitFrame_CrescentCradle_FitsThePortraitExactly_AndFollowsItsRotation()
    {
        var document = ComponentDocuments.WithAnchors();
        var portrait = document.Elements.OfType<ImageProfileElement>().Single(e => e.Role == ProfileElementRole.BasicPortrait);
        var placement = PlacementOf(document, BuiltInComponentCatalog.PortraitFrameAstralGoldCrescentCradle);
        Assert.Equal(new ElementRect(portrait.Position, portrait.Size), placement.Rect); // 5:8 art on the 5:8 portrait
        Assert.Equal(0f, placement.RotationDegrees);

        portrait.RotationDegrees = 12f;
        Assert.Equal(12f, PlacementOf(document, BuiltInComponentCatalog.PortraitFrameAstralGoldCrescentCradle).RotationDegrees);

        // A procedural portrait frame is unchanged: the portrait's own box.
        Assert.Equal(new ElementRect(portrait.Position, portrait.Size), PlacementOf(document, BuiltInComponentCatalog.PortraitFrameLine).Rect);
    }

    [Fact]
    public void PortraitOverlay_FallingStardust_SitsOnThePortrait_AndOverflowsItWhenScaled_NeverClipped()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitOverlayAstralGoldFallingStardust);
        document.Components = [component];
        Assert.Equal(new ElementRect(new Vector2(40, 40), new Vector2(400, 640)), Single(document, component).Placement.Rect);
        Assert.Equal(ProfileVisualBounds.Logical(document), ProfileVisualBounds.Compute(document)); // inside the Plate by default

        component.Scale = 1.5f;
        component.Offset = new Vector2(-60, -40);
        var rect = Single(document, component).Placement.Rect;
        Assert.Equal(new Vector2(600, 960), rect.Size);
        Assert.True(rect.Position.X < 40f && rect.Position.Y < 0f, "overflows the portrait and the Plate");

        var visual = ProfileVisualBounds.Compute(document);
        Assert.Equal(rect.Position, visual.Min);
    }

    [Fact]
    public void CornerOrnament_AstralAstrolabe_IsPlacedByRotation_FromATopLeftRuntimeDrawing()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentAstralGoldAstrolabePivot)];
        var steps = ComponentDocuments.Plan(document).Where(s => !s.IsElement).ToList();

        var size = ComponentPaintPlan.CornerSize * ComponentPaintPlan.Unit(document) * 2.5f;
        Assert.Equal(4, steps.Count);
        Assert.All(steps, s => Assert.Equal(new Vector2(size), s.Placement.Rect.Size));
        Assert.Equal([0f, 90f, 270f, 180f], steps.Select(s => s.Placement.RotationDegrees));
        Assert.All(steps, s => Assert.False(s.Placement.MirrorX || s.Placement.MirrorY));

        // The source is drawn for the top-right corner; the runtime drawing is the top-left one (its
        // bright pivot star sits in the top-left quadrant), so the top-right corner shows the source as authored.
        var image = Decode(BuiltInArtCatalog.AstralAstrolabePivot);
        var (cx, cy) = BrightBlueCentroid(image);
        Assert.True(cx < image.Width / 2f && cy < image.Height / 2f, $"pivot star at ({cx}, {cy})");
    }

    [Fact]
    public void NameBacking_Underlay_IsCenteredOnTheName_AsWideAsItsOrnaments_AtItsAspect()
    {
        var document = ComponentDocuments.WithAnchors();
        var procedural = PlacementOf(document, BuiltInComponentCatalog.NameBackingBar).Rect; // the padded name and title
        var underlay = PlacementOf(document, BuiltInComponentCatalog.NameBackingAstralGoldOrbitalConstellationUnderlay).Rect;

        Assert.Equal(Center(procedural).X, Center(underlay).X, 0.01f);
        Assert.Equal(Center(procedural).Y, Center(underlay).Y, 0.01f);
        Assert.Equal(3f, underlay.Size.X / underlay.Size.Y, 0.001f);

        // 1.15x the backing's width, up to twice its height: this 700 px name box is long enough to be
        // capped by height; a shorter name is sized by its width.
        Assert.Equal(procedural.Size.Y * 2f, underlay.Size.Y, 0.01f);
        foreach (var element in document.Elements.Where(e => e.Role is ProfileElementRole.BasicName or ProfileElementRole.BasicTitle))
        {
            element.Size = element.Size with { X = 240f };
        }

        var shortBacking = PlacementOf(document, BuiltInComponentCatalog.NameBackingBar).Rect;
        var shortUnderlay = PlacementOf(document, BuiltInComponentCatalog.NameBackingAstralGoldOrbitalConstellationUnderlay).Rect;
        Assert.Equal(shortBacking.Size.X * 1.15f, shortUnderlay.Size.X, 0.01f);
        Assert.Equal(Center(shortBacking).X, Center(shortUnderlay).X, 0.01f);

        // Painted behind the identity text, like every Name Backing (readability styling is untouched).
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingAstralGoldOrbitalConstellationUnderlay)];
        var plan = ComponentDocuments.Plan(document);
        var backing = plan.FindIndex(s => !s.IsElement);
        var name = plan.FindIndex(s => s.Element?.Role == ProfileElementRole.BasicName);
        Assert.True(backing < name);
    }

    [Fact]
    public void Divider_AstralEquator_GetsAFiveTimesBand_OnTheSameLine()
    {
        var document = ComponentDocuments.WithAnchors();
        var unit = ComponentPaintPlan.Unit(document);
        var rule = PlacementOf(document, BuiltInComponentCatalog.DividerLine).Rect;
        var astral = PlacementOf(document, BuiltInComponentCatalog.DividerAstralGoldEquatorLine).Rect;
        var celestial = PlacementOf(document, BuiltInComponentCatalog.DividerEquatorLine).Rect;

        Assert.Equal(Center(rule), Center(astral));
        Assert.Equal(ComponentPaintPlan.DividerHeight * unit * 5f, astral.Size.Y, 0.001f);
        Assert.Equal(3f, astral.Size.X / astral.Size.Y, 0.001f);
        Assert.Equal(ComponentPaintPlan.DividerHeight * unit * 4f, celestial.Size.Y, 0.001f); // Celestial Dream unchanged
    }

    [Fact]
    public void SectionHeader_StarPinnedUnderline_GetsAnArtBand_PinnedToTheHeadingsStartAndLine()
    {
        var document = ComponentDocuments.WithAnchors();
        var heading = document.Elements.OfType<TextProfileElement>().Single(e => e.Role == ProfileElementRole.BasicWorldHeading);
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.SectionHeaderAstralGoldStarPinnedUnderline);
        document.Components = [component];

        var rect = Single(document, component).Placement.Rect;
        var art = BuiltInArtCatalog.AstralStarPinnedUnderline;
        Assert.Equal(heading.Size.Y * 2.5f, rect.Size.Y, 0.001f); // not squeezed into the heading's height
        Assert.Equal(3f, rect.Size.X / rect.Size.Y, 0.001f);
        var pin = rect.Position + (rect.Size * art.Pivot);
        Assert.Equal(heading.Position.X, pin.X, 0.01f); // the medallion ends where the text starts
        Assert.Equal(heading.Position.Y + heading.Size.Y, pin.Y, 0.01f); // the line runs along the heading's bottom
        Assert.True(rect.Position.X < heading.Position.X, "the medallion sits before the text");

        // Scale grows it around that pin; Offset moves the pin.
        component.Scale = 2f;
        component.Offset = new Vector2(6, 3);
        var scaled = Single(document, component).Placement.Rect;
        Assert.Equal(rect.Size * 2f, scaled.Size);
        var scaledPin = scaled.Position + (scaled.Size * art.Pivot);
        Assert.Equal(heading.Position.X + 6f, scaledPin.X, 0.01f);
        Assert.Equal(heading.Position.Y + heading.Size.Y + 3f, scaledPin.Y, 0.01f);

        // Centered and right-aligned headings pin at their text's center and end.
        component.Scale = 1f;
        component.Offset = Vector2.Zero;
        heading.Alignment = TextAlignment.Center;
        var centered = Single(document, component).Placement.Rect;
        Assert.Equal(heading.Position.X + (heading.Size.X / 2f), (centered.Position + (centered.Size * art.Pivot)).X, 0.01f);
        heading.Alignment = TextAlignment.Right;
        var right = Single(document, component).Placement.Rect;
        Assert.Equal(heading.Position.X + heading.Size.X, (right.Position + (right.Size * art.Pivot)).X, 0.01f);
    }

    [Fact]
    public void SectionHeader_ProceduralStyles_KeepTheHeadingsBox()
    {
        var document = ComponentDocuments.WithAnchors();
        var heading = document.Elements.Single(e => e.Role == ProfileElementRole.BasicWorldHeading);
        foreach (var id in new[] { BuiltInComponentCatalog.SectionHeaderUnderline, BuiltInComponentCatalog.SectionHeaderTick })
        {
            Assert.Equal(new ElementRect(heading.Position, heading.Size), PlacementOf(document, id).Rect);
        }
    }

    [Theory]
    [MemberData(nameof(FamilyIds))]
    public void Transforms_ScaleRotationAndOffset_ApplyToEveryStyle_WithoutStretching(string id)
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(id);
        document.Components = [component];
        var before = ComponentDocuments.Plan(document).First(s => ReferenceEquals(s.Component, component)).Placement;

        component.Scale = 1.5f;
        component.RotationDegrees = 20f;
        component.Offset = new Vector2(8, 4);
        var after = ComponentDocuments.Plan(document).First(s => ReferenceEquals(s.Component, component)).Placement;

        var aspect = Definition(id).Art!.AspectRatio;
        Assert.Equal(aspect, after.Rect.Size.X / after.Rect.Size.Y, 0.002f);
        Assert.Equal(before.Rect.Size.X * 1.5f, after.Rect.Size.X, 0.01f);
        Assert.Equal(before.RotationDegrees + 20f, after.RotationDegrees);

        // The offset moves it (mirrored per corner for Corner Ornaments; the top-left corner isn't mirrored).
        var pivot = Definition(id).Art!.Pivot;
        var pinBefore = before.Rect.Position + (before.Rect.Size * pivot);
        var pinAfter = after.Rect.Position + (after.Rect.Size * pivot);
        Assert.Equal(pinBefore.X + 8f, pinAfter.X, 0.01f);
        Assert.Equal(pinBefore.Y + 4f, pinAfter.Y, 0.01f);
    }

    [Fact]
    public void LayerOrder_WorksWithinEachLayer()
    {
        var document = ComponentDocuments.WithAnchors();
        var astral = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentAstralGoldAstrolabePivot, layerOrder: 4);
        var bracket = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket, layerOrder: -4);
        document.Components = [astral, bracket];

        var steps = ComponentDocuments.Plan(document).Where(s => !s.IsElement).ToList();
        Assert.All(steps.Take(4), s => Assert.Same(bracket, s.Component));
        Assert.All(steps.Skip(4), s => Assert.Same(astral, s.Component));
    }

    [Fact]
    public void AllSeven_OnOnePlate_StayInsideThePlateByDefault()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = Family.Select(f => ComponentDocuments.Of(f.Id)).ToList();
        Assert.Equal(Family.Length, document.Components.Count(c => ComponentDocuments.Plan(document).Any(s => ReferenceEquals(s.Component, c))));

        var visual = ProfileVisualBounds.Compute(document);
        Assert.Equal(ProfileVisualBounds.Logical(document), visual);
    }

    // ---- Bundled runtime art --------------------------------------------------------------------------

    public static IEnumerable<object[]> FamilyArt() => Family.Select(f => new object[] { f.AssetId });

    [Theory]
    [MemberData(nameof(FamilyArt))]
    public void RuntimePng_IsEmbedded_ValidRgba_AtItsDeclaredSize(string assetId)
    {
        var art = BuiltInArtCatalog.Find(assetId)!;
        Assert.StartsWith(BuiltInArtCatalog.ResourcePrefix + "Components.AstralGold.", art.ResourceName, StringComparison.Ordinal);
        var image = Decode(art);
        Assert.Equal((art.PixelWidth, art.PixelHeight), (image.Width, image.Height));
        Assert.True(BundledArtImage.IsSupportedSize(image.Width, image.Height));
        Assert.Equal(image.Width * image.Height * 4, image.Rgba.Length);
    }

    [Theory]
    [MemberData(nameof(FamilyArt))]
    public void RuntimePng_HasGenuineAlpha_AndNoBakedBackground(string assetId)
    {
        var image = Decode(BuiltInArtCatalog.Find(assetId)!);
        var count = image.Width * image.Height;
        var alpha = Enumerable.Range(0, count).Select(i => image.Rgba[(i * 4) + 3]).ToArray();

        Assert.InRange(alpha.Count(a => a == 0) / (double)count, 0.5, 0.97);
        Assert.True(alpha.Count(a => a is > 0 and < 255) / (double)count > 0.03, "soft edges and glow survive as partial alpha");
        Assert.Contains(alpha, a => a >= 250); // solid gold
        foreach (var (x, y) in new[] { (0, 0), (image.Width - 1, 0), (0, image.Height - 1), (image.Width - 1, image.Height - 1) })
        {
            Assert.True(image.Rgba[(((y * image.Width) + x) * 4) + 3] <= 2, $"corner ({x}, {y}) is covered");
        }
    }

    [Theory]
    [MemberData(nameof(FamilyArt))]
    public void RuntimePng_KeepsItsAuthoredGoldAndBlue_NotGreyscale(string assetId)
    {
        var image = Decode(BuiltInArtCatalog.Find(assetId)!);
        int gold = 0, blue = 0;
        for (var i = 0; i < image.Rgba.Length; i += 4)
        {
            if (image.Rgba[i + 3] < 200)
            {
                continue;
            }

            int r = image.Rgba[i], b = image.Rgba[i + 2];
            gold += r - b > 80 ? 1 : 0;
            blue += b - r > 60 ? 1 : 0;
        }

        Assert.True(gold > 500, $"{gold} gold texels");
        Assert.True(blue > 20, $"{blue} blue texels");
    }

    [Theory]
    [MemberData(nameof(FamilyArt))]
    public void RuntimePng_TransparentTexelsBesideTheArt_CarryItsColor_NotBlackOrWhite(string assetId)
    {
        // Bilinear filtering blends a covered texel with its transparent neighbors' RGB: those carry the
        // artwork's own nearby color, so edges get no dark or white fringe.
        var image = Decode(BuiltInArtCatalog.Find(assetId)!);
        long sum = 0, n = 0;
        for (var y = 1; y < image.Height - 1; y++)
        {
            for (var x = 1; x < image.Width - 1; x++)
            {
                var i = ((y * image.Width) + x) * 4;
                var stride = image.Width * 4;
                var beside = Math.Max(Math.Max(image.Rgba[i + 7], image.Rgba[i - 1]), Math.Max(image.Rgba[i + stride + 3], image.Rgba[i - stride + 3]));
                if (image.Rgba[i + 3] != 0 || beside < 32)
                {
                    continue; // only a transparent texel right beside visible art
                }

                sum += image.Rgba[i] + image.Rgba[i + 1] + image.Rgba[i + 2];
                n++;
            }
        }

        Assert.True(n > 0);
        var mean = sum / (3.0 * n);
        Assert.InRange(mean, 40.0, 235.0);
    }

    [Theory]
    [MemberData(nameof(FamilyArt))]
    public void RuntimeLevels_HalveExactly_AndConserveCoverage(string assetId)
    {
        var levels = BundledArtImage.BuildLevels(Decode(BuiltInArtCatalog.Find(assetId)!));
        Assert.True(levels.Count >= 4, $"{levels.Count} levels");
        for (var i = 1; i < levels.Count; i++)
        {
            Assert.Equal((levels[i - 1].Width / 2, levels[i - 1].Height / 2), (levels[i].Width, levels[i].Height));
        }

        Assert.True(Math.Min(levels[^1].Width, levels[^1].Height) >= BundledArtImage.MinLevelSize);
        var coverage = levels.Select(l => Enumerable.Range(0, l.Width * l.Height).Average(i => l.Rgba[(i * 4) + 3] / 255.0)).ToList();
        Assert.All(coverage, c => Assert.InRange(c, coverage[0] * 0.97, coverage[0] * 1.03));
    }

    [Fact]
    public void Levels_OfTransparentBlocks_KeepTheirNeighborsColor_WhiteStaysWhite()
    {
        // A fully transparent 2x2 block of gold-tinted texels stays gold; of white texels stays white
        // (what the tintable Celestial Dream art stores, so its levels are unchanged).
        byte[] Block(byte r, byte g, byte b) => [.. Enumerable.Range(0, 64 * 64).SelectMany(_ => new[] { r, g, b, (byte)0 })];
        var gold = BundledArtImage.BuildLevels(new ArtLevel(64, 64, Block(200, 150, 60)))[1];
        Assert.Equal([200, 150, 60, 0], gold.Rgba.Take(4).Select(v => (int)v));
        var white = BundledArtImage.BuildLevels(new ArtLevel(64, 64, Block(255, 255, 255)))[1];
        Assert.Equal([255, 255, 255, 0], white.Rgba.Take(4).Select(v => (int)v));
    }

    [Fact]
    public void RuntimeContent_KeepsEachSourcesAspectRatio()
    {
        // Preserved sources (AetherFrameAssets/source/CelestialDream/BlueGold): drawings were resized
        // uniformly, only padded (Falling Stardust, below) or turned (Astrolabe Pivot), never stretched.
        Assert.Equal(1672f / 941f, 1536f / 864f, 0.002f);
        Assert.Equal(992f / 1586f, 800f / 1280f, 0.002f);
        Assert.Equal(1f, BuiltInArtCatalog.AstralAstrolabePivot.AspectRatio);
        Assert.Equal(3f, BuiltInArtCatalog.AstralOrbitalConstellationUnderlay.AspectRatio);
        Assert.Equal(3f, BuiltInArtCatalog.AstralEquatorLine.AspectRatio);
        Assert.Equal(3f, BuiltInArtCatalog.AstralStarPinnedUnderline.AspectRatio);

        // Falling Stardust: the 1086 x 1448 (3:4) drawing across the full width, transparent below.
        var stardust = Decode(BuiltInArtCatalog.AstralFallingStardust);
        var drawn = 640 * 1448 / 1086; // 853 rows
        for (var y = drawn + 2; y < stardust.Height; y++)
        {
            for (var x = 0; x < stardust.Width; x++)
            {
                Assert.Equal(0, stardust.Rgba[(((y * stardust.Width) + x) * 4) + 3]);
            }
        }
    }

    // ---- Persistence and portability --------------------------------------------------------------------

    [Fact]
    public void Serialization_StoresOnlyDefinitionIds_ForAllSeven()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = Family.Select(f => ComponentDocuments.Of(f.Id)).ToList();

        var json = PlateDocuments.ToJson(document);
        var stored = json["Components"]!.AsArray();
        Assert.Equal(Family.Select(f => f.Id), stored.Select(c => (string?)c!["DefinitionId"]));
        Assert.All(stored, c => Assert.Null(c!["AssetId"]));
        AssertNoPhysicalReference(json.ToJsonString());

        var reloaded = ComponentDocuments.RoundTrip(document);
        Assert.True(PlateComponent.ListsEqual(document.Components, reloaded.Components));
    }

    [Fact]
    public async Task SaveAndReload_PreservesAllSeven()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plateId = await CreatePlateWithFamilyAsync(library);
        var saved = library.OpenDocumentForEditing(plateId).Components;

        var reloaded = (await fixture.LoadAsync()).OpenDocumentForEditing(plateId).Components;
        Assert.True(PlateComponent.ListsEqual(saved, reloaded));
        Assert.Equal(Family.Select(f => f.Id), reloaded!.Select(c => c.DefinitionId));
        Assert.Equal(0.6f, reloaded![0].Opacity);
        AssertNoPhysicalReference(fixture.ReadPlateJson(plateId));
    }

    [Fact]
    public async Task DuplicatePlate_PreservesAllSeven_WithNoImageData()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var sourceId = await CreatePlateWithFamilyAsync(library);

        var copyId = await library.DuplicatePlateAsync(sourceId);

        Assert.True(PlateComponent.ListsEqual(library.OpenDocumentForEditing(sourceId).Components, library.OpenDocumentForEditing(copyId).Components));
        var scan = await library.ScanAssetReferencesAsync();
        Assert.True(scan.IsComplete);
        Assert.Empty(scan.ReferencedAssetIds);
        AssertNoPhysicalReference(fixture.ReadPlateJson(copyId));
    }

    [Fact]
    public async Task Templates_SaveAndUse_PreserveAllSeven()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var sourceId = await CreatePlateWithFamilyAsync(fixture.PlateLibrary);
        var source = fixture.PlateLibrary.OpenDocumentForEditing(sourceId);

        var templateId = await templates.SaveAsTemplateAsync(sourceId, "Astral");
        Assert.True(PlateComponent.ListsEqual(source.Components, templates.GetSavedDocument(templateId)!.Components));
        AssertNoPhysicalReference(fixture.ReadTemplateJson(templateId));

        var created = await templates.InstantiateAsync(templateId, null);
        Assert.True(PlateComponent.ListsEqual(source.Components, fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId).Components));
    }

    [Fact]
    public async Task Package_RoundTrip_RebuildsAllSevenFromTheirIds_WithoutArtworkOrPaths()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var plateId = await CreatePlateWithFamilyAsync(library);

        var path = fixture.Export(packages, plateId);
        var entries = PackageFiles.Read(path);
        Assert.DoesNotContain(entries, e => e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        foreach (var entry in entries)
        {
            AssertNoPhysicalReference(Encoding.UTF8.GetString(entry.Bytes));
        }

        var profile = PackageFiles.Json(PackageFiles.Entry(entries, PackagePaths.ProfilePath));
        Assert.Equal(Family.Select(f => f.Id), profile["Components"]!.AsArray().Select(c => (string?)c!["DefinitionId"]));

        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport, string.Join("; ", staged.Diagnostics.Errors));
        Assert.Empty(staged.Diagnostics.Warnings);
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded, result.Error?.ToString());

        var imported = library.OpenDocumentForEditing(result.PlateId).Components!;
        Assert.True(PlateComponent.ListsEqual(library.OpenDocumentForEditing(plateId).Components, imported));
        Assert.All(imported, c => Assert.Equal(ComponentStatus.Ready, ComponentPaintPlan.Resolve(c, BuiltInComponentCatalog.Instance, out _)));
    }

    [Fact]
    public void UnknownFutureAstralGoldId_IsPreservedVerbatim_AndNotDrawn()
    {
        var json = PlateDocuments.ToJson(ComponentDocuments.WithAnchors());
        json["Components"] = JsonNode.Parse("""
            [ { "Kind": 1, "DefinitionId": "af.plate-frame.astral-gold-nebula-veil", "Opacity": 0.5, "FutureArtHint": { "Glow": 2 } } ]
            """);
        var document = PlateDocuments.Materialize(JsonNode.Parse(json.ToJsonString())!.AsObject());
        var component = document.Components!.Single();

        Assert.Equal(ComponentStatus.MissingDefinition, ComponentPaintPlan.Resolve(component, BuiltInComponentCatalog.Instance, out _));
        Assert.DoesNotContain(ComponentDocuments.Plan(document), s => ReferenceEquals(s.Component, component));
        var saved = PlateDocuments.ToJson(document)["Components"]![0]!;
        Assert.Equal("af.plate-frame.astral-gold-nebula-veil", (string?)saved["DefinitionId"]);
        Assert.Equal(2, (int)saved["FutureArtHint"]!["Glow"]!);
    }

    [Theory]
    [MemberData(nameof(FamilyIds))]
    public void BuildWithoutTheDefinition_KeepsItUndrawn_NeverSubstitutesAnotherStyle(string id)
    {
        var older = new CatalogWithout(id);
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(id);
        document.Components = [component];

        Assert.Equal(ComponentStatus.MissingDefinition, ComponentPaintPlan.Resolve(component, older, out var definition));
        Assert.Null(definition);
        var steps = new List<PaintStep>();
        ComponentPaintPlan.Build(document, document.Elements.OrderBy(e => e.ZIndex).ToList(), older, steps);
        Assert.All(steps, s => Assert.True(s.IsElement));
        Assert.Equal(id, ComponentDocuments.RoundTrip(document).Components!.Single().DefinitionId);
    }

    // ---- Nothing else changed -------------------------------------------------------------------------------

    [Fact]
    public void CelestialDream_KeepsItsExactPlacementsAndTint()
    {
        var document = ComponentDocuments.WithAnchors();
        var unit = ComponentPaintPlan.Unit(document);

        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot)];
        var corners = ComponentDocuments.Plan(document).Where(s => !s.IsElement).ToList();
        Assert.All(corners, s => Assert.Equal(new Vector2(ComponentPaintPlan.CornerSize * unit * 2f), s.Placement.Rect.Size));

        var rule = PlacementOf(document, BuiltInComponentCatalog.DividerLine).Rect;
        var equator = PlacementOf(document, BuiltInComponentCatalog.DividerEquatorLine).Rect;
        Assert.Equal(Center(rule), Center(equator));
        Assert.Equal(new Vector2(288f, 96f) * unit, equator.Size);

        Assert.Equal(1f, BuiltInArtCatalog.AstrolabePivot.WidthFactor);
        Assert.Equal(new Vector2(0.5f), BuiltInArtCatalog.EquatorLine.Pivot);
    }

    [Fact]
    public void ProceduralNameBackingAndPortraitComponents_KeepTheirBoxes()
    {
        var document = ComponentDocuments.WithAnchors();
        var identity = new ElementRect(new Vector2(480, 60), new Vector2(700, 95));
        var pad = new Vector2(ComponentPaintPlan.NameBackingPadX, ComponentPaintPlan.NameBackingPadY) * ComponentPaintPlan.Unit(document);
        foreach (var id in new[] { BuiltInComponentCatalog.NameBackingBar, BuiltInComponentCatalog.NameBackingRibbon, BuiltInComponentCatalog.NameBackingFade })
        {
            Assert.Equal(new ElementRect(identity.Position - pad, identity.Size + (2f * pad)), PlacementOf(document, id).Rect);
        }

        foreach (var id in new[] { BuiltInComponentCatalog.PortraitOverlayFade, BuiltInComponentCatalog.PortraitOverlayVignette, BuiltInComponentCatalog.PortraitFrameDouble })
        {
            Assert.Equal(new ElementRect(new Vector2(40, 40), new Vector2(400, 640)), PlacementOf(document, id).Rect);
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    private static Vector2 Center(ElementRect rect) => rect.Position + (rect.Size / 2f);

    private static ComponentPlacement PlacementOf(ProfileDocument document, string definitionId)
    {
        var component = ComponentDocuments.Of(definitionId);
        document.Components = [component];
        return ComponentDocuments.Plan(document).First(s => ReferenceEquals(s.Component, component)).Placement;
    }

    private static PaintStep Single(ProfileDocument document, PlateComponent component) =>
        Assert.Single(ComponentDocuments.Plan(document), s => ReferenceEquals(s.Component, component));

    private static ComponentPrimitive Primitive(ProfileDocument document, PlateComponent component, ComponentPlacement placement)
    {
        var output = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, component, BuiltInComponentCatalog.Find(component.DefinitionId)!, placement, output);
        var primitive = Assert.Single(output);
        Assert.Equal(ComponentPrimitiveKind.Art, primitive.Kind);
        return primitive;
    }

    private static ArtLevel Decode(BuiltInArtAsset art) => BundledArtImage.DecodePng(BuiltInArtTests.ReadResource(art.ResourceName));

    /// <summary>Centroid of the bright, strongly blue texels (the pivot star's glow).</summary>
    private static (float X, float Y) BrightBlueCentroid(ArtLevel image)
    {
        double sx = 0, sy = 0, n = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var i = ((y * image.Width) + x) * 4;
                if (image.Rgba[i + 3] > 200 && image.Rgba[i + 2] > 200 && image.Rgba[i + 2] - image.Rgba[i] > 60)
                {
                    sx += x;
                    sy += y;
                    n++;
                }
            }
        }

        Assert.True(n > 0);
        return ((float)(sx / n), (float)(sy / n));
    }

    private static async Task<Guid> CreatePlateWithFamilyAsync(Services.Plates.PlateLibraryService library)
    {
        var created = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Astral", new PlateStarterContent(null));
        var document = library.OpenDocumentForEditing(created.PlateId);
        document.Components = Family.Select(f => ComponentDocuments.Of(f.Id)).ToList();
        document.Components[0].Opacity = 0.6f;
        document.Components[3].Scale = 1.25f;
        document.Components[3].Corners = CornerMask.TopLeft | CornerMask.BottomRight;
        document.Components[5].RotationDegrees = -6f;
        document.Components[6].Offset = new Vector2(4, 2);
        await library.SavePlateDocumentAsync(document);
        return created.PlateId;
    }

    private static void AssertNoPhysicalReference(string text)
    {
        Assert.DoesNotContain(".png", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AstralGold", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BlueGold", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CelestialDream", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AetherFrameAssets", text, StringComparison.Ordinal);
        Assert.DoesNotContain(BuiltInArtCatalog.ResourcePrefix, text, StringComparison.Ordinal);
        Assert.DoesNotContain("af.asset.", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\\\", text, StringComparison.Ordinal); // an escaped Windows path in JSON
        Assert.DoesNotContain("Plugin development", text, StringComparison.Ordinal);
    }

    private sealed class CatalogWithout(string excludedId) : IComponentCatalog
    {
        public ComponentDefinition? Find(string? id) => id == excludedId ? null : BuiltInComponentCatalog.Find(id);
    }
}
