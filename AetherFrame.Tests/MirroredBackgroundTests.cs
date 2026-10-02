using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A Mirrored Plate mirrors its background artwork (the owner's request of October 2, 2026): most Art
/// Styles' backgrounds are drawn calm behind the details and busy behind the portrait, so the details
/// sit over the same side of the art in both orientations. The Plate Frame and Portrait Frame stay as drawn.
/// </summary>
public class MirroredBackgroundTests
{
    private const float Tolerance = 0.01f;

    [Theory]
    [InlineData(AdventurePlateOrientation.Normal, false)]
    [InlineData(AdventurePlateOrientation.Mirrored, true)]
    public void TheBackground_IsMirrored_OnlyOnAMirroredPlate(AdventurePlateOrientation orientation, bool mirrored)
    {
        var document = Classic(orientation);

        var background = BackgroundStep(document);

        Assert.Equal(mirrored, background.Placement.MirrorX);
        Assert.False(background.Placement.MirrorY);
        Assert.Equal(new ElementRect(Vector2.Zero, new Vector2(1280, 720)), background.Placement.Rect);
    }

    [Fact]
    public void APlateWithoutBasicSettings_NeverMirrorsItsBackground()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.BackgroundCelestialSakura)];
        Assert.Null(document.BasicPlate);

        Assert.False(BackgroundStep(document).Placement.MirrorX);
    }

    /// <summary>The picture, and its Offset and Rotation with it: the Mirrored background is the
    /// Normal one reflected across the Plate's vertical center line, point for point.</summary>
    [Fact]
    public void TheMirroredBackground_IsTheNormalOnesReflection_OffsetAndRotationIncluded()
    {
        var normal = Classic(AdventurePlateOrientation.Normal);
        var mirrored = Classic(AdventurePlateOrientation.Mirrored);
        foreach (var document in new[] { normal, mirrored })
        {
            var background = PlateComponentEditor.FindSlot(document, PlateComponentKind.Background)!;
            background.Offset = new Vector2(30f, 12f);
            background.RotationDegrees = 5f;
            background.Scale = 1.1f;
        }

        var normalArt = ArtQuad(normal);
        var mirroredArt = ArtQuad(mirrored);

        // Each texture corner lands at its reflection, so the art's left edge is at the Plate's right.
        foreach (var (a, b) in new[] { (normalArt.A, mirroredArt.A), (normalArt.B, mirroredArt.B), (normalArt.C, mirroredArt.C), (normalArt.D, mirroredArt.D) })
        {
            Assert.Equal(1280f - a.X, b.X, Tolerance);
            Assert.Equal(a.Y, b.Y, Tolerance);
        }

        Assert.Equal(-5f, BackgroundStep(mirrored).Placement.RotationDegrees);
        Assert.True(mirroredArt.A.X > mirroredArt.B.X);
    }

    /// <summary>Every Plate Frame and Portrait Frame is drawn symmetric, so only the background turns;
    /// Corner Ornaments mirror per corner, as in Normal.</summary>
    [Fact]
    public async Task OnAMirroredPlate_EveryOtherPiece_IsPlacedAsBefore()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Basic.ApplyTheme(ProfileThemePresets.Find("af.style.frontier-silver")!);
        var normal = ComponentDocuments.Plan(harness.Document).Where(s => !s.IsElement && s.Component!.Kind != PlateComponentKind.Background).ToList();

        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        var mirrored = ComponentDocuments.Plan(harness.Document).Where(s => !s.IsElement && s.Component!.Kind != PlateComponentKind.Background).ToList();

        Assert.Equal(normal.Count, mirrored.Count);
        for (var i = 0; i < normal.Count; i++)
        {
            Assert.Same(normal[i].Component, mirrored[i].Component);
            Assert.Equal((normal[i].Placement.MirrorX, normal[i].Placement.MirrorY), (mirrored[i].Placement.MirrorX, mirrored[i].Placement.MirrorY));
            Assert.Equal(normal[i].Placement.RotationDegrees, mirrored[i].Placement.RotationDegrees);
        }

        Assert.Contains(mirrored, s => s.Component!.Kind == PlateComponentKind.PlateFrame && !s.Placement.MirrorX);
        Assert.Contains(mirrored, s => s.Component!.Kind == PlateComponentKind.PortraitFrame && !s.Placement.MirrorX);
    }

    /// <summary>
    /// Why it reads: in either orientation, every Details text sits over the part of its style's
    /// background that <see cref="ArtSetsTests.EveryStylesText_ContrastsWithTheArtBehindIt"/> measures
    /// (37.5% to 97% across, 25% to 95% down). Unmirrored, a Mirrored Plate's details would sit over
    /// the art's left side, drawn busy for the portrait.
    /// </summary>
    [Theory]
    [InlineData(AdventurePlateOrientation.Normal)]
    [InlineData(AdventurePlateOrientation.Mirrored)]
    public async Task EveryStylesDetails_SitOverTheMeasuredCalmSide(AdventurePlateOrientation orientation)
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Basic.SetOrientation(orientation);
        var details = harness.Document.Elements
            .Where(e => e is TextProfileElement && BasicSections.SectionOf(e.Role) is { } section && section != BasicSection.Identity)
            .ToList();
        Assert.NotEmpty(details);

        foreach (var style in ArtSets.Styles)
        {
            harness.Basic.ApplyTheme(style);
            var art = ArtQuad(harness.Document);
            foreach (var element in details)
            {
                foreach (var corner in new[] { element.Position, element.Position + element.Size })
                {
                    // The axis-aligned art quad's texture coordinates at this point (its A-B-C-D are
                    // the texture's corners, mirrored or not).
                    var u = (corner.X - art.A.X) / (art.B.X - art.A.X);
                    var v = (corner.Y - art.A.Y) / (art.D.Y - art.A.Y);
                    Assert.True(u >= 0.375f - Tolerance && u <= 0.97f + Tolerance, $"{style.Name}: {element.Role} at u {u}");
                    Assert.True(v >= 0.25f - Tolerance && v <= 0.95f + Tolerance, $"{style.Name}: {element.Role} at v {v}");
                }
            }
        }
    }

    private static ProfileDocument Classic(AdventurePlateOrientation orientation)
    {
        var document = BasicDocuments.Classic();
        document.BasicPlate!.Orientation = orientation;
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.BackgroundCelestialSakura), ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameCelestialSakura)];
        return document;
    }

    private static PaintStep BackgroundStep(ProfileDocument document) =>
        Assert.Single(ComponentDocuments.Plan(document), s => s.Component is { Kind: PlateComponentKind.Background });

    private static ComponentPrimitive ArtQuad(ProfileDocument document)
    {
        var step = BackgroundStep(document);
        var primitives = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, step.Component!, step.Definition!, step.Placement, primitives);
        return Assert.Single(primitives, p => p.Kind == ComponentPrimitiveKind.Art);
    }
}
