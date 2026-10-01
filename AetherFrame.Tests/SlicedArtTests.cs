using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Protocol.Remote;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Sliced artwork (<see cref="ArtSlices"/>): a Name Backing as wide as the name, with its caps and
/// center piece never distorted. The Celestial Sakura nameplate is the first sliced piece.
/// </summary>
public class SlicedArtTests
{
    private static readonly BuiltInArtAsset Nameplate = BuiltInArtCatalog.CelestialSakuraNameplateArt;

    // The nameplate's measured cuts (see Assets/README.md).
    private const int ContentLeft = 340;
    private const int CapLeft = 512;
    private const int CenterLeft = 760;
    private const int CenterRight = 1400;
    private const int CapRight = 1672;
    private const int ContentRight = 1840;

    // A centered name box on the 1280 x 720 canvas; its padded box is 72 tall, so the plaque is 108.
    private const float NameTop = 60f;
    private const float NameHeight = 60f;
    private const float PlaqueHeight = (NameHeight + (2f * ComponentPaintPlan.NameBackingPadY)) * 1.5f;
    private const float PerPixel = PlaqueHeight / 724f;

    [Fact]
    public void TheNameplate_IsSlicedAtItsMeasuredCuts()
    {
        Assert.Equal(new ArtSlices(ContentLeft, CapLeft, CenterLeft, CenterRight, CapRight, ContentRight), Nameplate.Slices);
        Assert.True(ComponentPaintPlan.IsSliced(Nameplate));
        Assert.Equal(1652, Nameplate.Slices!.FixedWidth(Nameplate.PixelWidth));
        Assert.Equal(672, Nameplate.Slices.OutsideContent(Nameplate.PixelWidth));
    }

    [Fact]
    public void OnlyNameBackingsAndDividers_AreSliced_AndEverySlicingFitsItsArtwork()
    {
        foreach (var art in BuiltInArtCatalog.All.Where(a => a.Slices is not null))
        {
            Assert.True(art.Kind is PlateComponentKind.NameBacking or PlateComponentKind.Divider, art.Id);
            Assert.True(art.Slices!.IsValidFor(art.PixelWidth), art.Id);
        }
    }

    [Theory]
    [InlineData(-1, 512, 760, 1400, 1672, 1840)] // before the artwork
    [InlineData(340, 512, 512, 1400, 1672, 1840)] // an empty left fill
    [InlineData(340, 512, 760, 1400, 1400, 1840)] // an empty right fill
    [InlineData(600, 512, 760, 1400, 1672, 1840)] // content starting inside the fill
    [InlineData(340, 512, 1400, 760, 1672, 1840)] // out of order
    [InlineData(340, 512, 760, 1400, 1672, 2173)] // past the artwork
    public void ASlicingOutOfOrder_IsNotValid_AndDrawsWhole(int contentLeft, int capLeft, int centerLeft, int centerRight, int capRight, int contentRight)
    {
        var art = Nameplate with { Slices = new ArtSlices(contentLeft, capLeft, centerLeft, centerRight, capRight, contentRight) };

        Assert.False(art.Slices!.IsValidFor(art.PixelWidth));
        Assert.False(ComponentPaintPlan.IsSliced(art));
        var primitive = Assert.Single(Primitives(art, new Vector2(600f, PlaqueHeight)));
        Assert.Equal(ArtPiece.Whole, primitive.Piece);
    }

    [Fact]
    public void ThePieces_TileTheArtwork_LeftToRight()
    {
        Assert.Equal((0f, 1f), Nameplate.Window(ArtPiece.Whole));
        var windows = ArtPieces.Sliced.Select(Nameplate.Window).ToList();
        Assert.Equal(0f, windows[0].U0);
        Assert.Equal(1f, windows[^1].U1);
        for (var i = 0; i < windows.Count; i++)
        {
            Assert.True(windows[i].U1 > windows[i].U0);
            if (i > 0)
            {
                Assert.Equal(windows[i - 1].U1, windows[i].U0);
            }
        }

        Assert.Equal((float)CenterLeft / 2172, Nameplate.Window(ArtPiece.Center).U0);
        Assert.Equal((0f, 1f), BuiltInArtCatalog.AstrolabePivot.Window(ArtPiece.LeftCap)); // unsliced: always whole
    }

    [Fact]
    public void EveryPieceIdent_IsALayoutIdent_FoundAgain_AndNoArtworksId()
    {
        foreach (var art in BuiltInArtCatalog.All)
        {
            Assert.Equal((art, ArtPiece.Whole), BuiltInArtCatalog.FindPiece(art.Id));
            if (art.Slices is null)
            {
                continue;
            }

            foreach (var piece in ArtPieces.Sliced)
            {
                var ident = art.PieceIdent(piece);
                _ = new LayoutArtQuad(ident, default, default, default, default, default); // throws on an ident the protocol refuses
                Assert.Null(BuiltInArtCatalog.Find(ident));
                Assert.Equal((art, piece), BuiltInArtCatalog.FindPiece(ident));
            }
        }

        Assert.Equal(Nameplate.Id + ".left-cap", Nameplate.PieceIdent(ArtPiece.LeftCap));
        Assert.Equal(Nameplate.Id, Nameplate.PieceIdent(ArtPiece.Whole));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".left-cap")]
    [InlineData("af.asset.celestial-dream.corner-ornament.astrolabe-pivot.left-cap")] // a piece of unsliced art
    [InlineData("af.asset.celestial-sakura.name-backing.nameplate.middle")]
    [InlineData("af.asset.celestial-sakura.name-backing.nameplate.left-cap.left-cap")]
    [InlineData("af.asset.celestial-sakura.name-backing.nameplate.Left-Cap")]
    [InlineData("af.asset.celestial-sakura.name-backing.nameplateleft-cap")]
    public void FindPiece_FindsNothingElse(string? ident)
    {
        Assert.Null(BuiltInArtCatalog.FindPiece(ident));
    }

    [Theory]
    [InlineData(40f)] // "Al": the plaque's fixed pieces are wider than the name
    [InlineData(300f)]
    [InlineData(600f)]
    public void TheNameplate_IsAsWideAsTheName_AtOneHeight_CenteredOnIt(float nameWidth)
    {
        var rect = NameplateRect(nameWidth);

        Assert.Equal(PlaqueHeight, rect.Size.Y, 3);
        var anchorWidth = nameWidth + (2f * TextProfileElement.LayoutPadding) + 2f + (2f * ComponentPaintPlan.NameBackingPadX);
        var expected = Math.Max(anchorWidth + (672 * PerPixel), 1652 * PerPixel);
        Assert.Equal(expected, rect.Size.X, 2);
        AssertClose(new Vector2(640f, NameTop + (NameHeight / 2f)), rect.Position + (rect.Size / 2f));
    }

    [Fact]
    public void ALongerName_GetsALongerPlaque_AndAShortOne_TheShortestWhole()
    {
        var shortest = NameplateRect(1f).Size.X;
        Assert.Equal(1652 * PerPixel, shortest, 2);
        Assert.Equal(shortest, NameplateRect(40f).Size.X, 2);
        Assert.True(NameplateRect(300f).Size.X > shortest);
        Assert.True(NameplateRect(600f).Size.X > NameplateRect(300f).Size.X);
    }

    [Fact]
    public void TheNameplate_NeverStretchesPastThePlate()
    {
        // A left-aligned name filling a box that ends 40 px from the right edge.
        var document = OneName(new Vector2(480f, NameTop), new Vector2(760f, NameHeight), TextAlignment.Left);
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingCelestialSakura)];

        foreach (var measure in new Func<TextProfileElement, float?>?[] { _ => 740f, null })
        {
            var rect = Single(document, measure).Placement.Rect;
            Assert.True(rect.Position.X >= -0.01f && rect.Position.X + rect.Size.X <= 1280.01f, $"{rect}");
            Assert.Equal(PlaqueHeight, rect.Size.Y, 3);
        }
    }

    [Fact]
    public void Scale_GrowsTheWholePlaque_AroundItsCenter()
    {
        var document = OneName(new Vector2(280f, NameTop), new Vector2(720f, NameHeight), TextAlignment.Center);
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingCelestialSakura);
        document.Components = [component];
        var normal = Single(document, _ => 300f).Placement.Rect;

        component.Scale = 2f;
        var doubled = Single(document, _ => 300f).Placement.Rect;

        Assert.Equal(normal.Size * 2f, doubled.Size);
        AssertClose(normal.Position + (normal.Size / 2f), doubled.Position + (doubled.Size / 2f));
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(900f)]
    public void ThePieces_SpanThePlaque_CapsAndCenterKeepTheirShape_FillsShareTheRest(float width)
    {
        var size = new Vector2(width, PlaqueHeight);
        var primitives = Primitives(Nameplate, size);

        Assert.Equal(ArtPieces.Sliced, primitives.Select(p => p.Piece));
        Assert.All(primitives, p => Assert.Equal(ComponentPrimitiveKind.Art, p.Kind));
        Assert.Equal(0f, primitives[0].A.X);
        Assert.Equal(width, primitives[^1].B.X, 3);
        for (var i = 1; i < primitives.Count; i++)
        {
            Assert.Equal(primitives[i - 1].B, primitives[i].A); // one shared edge, no gap or overlap
            Assert.Equal(primitives[i - 1].C, primitives[i].D);
        }

        Assert.All(primitives, p => Assert.Equal((0f, PlaqueHeight), (p.A.Y, p.D.Y)));
        Assert.Equal(CapLeft * PerPixel, Width(primitives[0]), 2);
        Assert.Equal((CenterRight - CenterLeft) * PerPixel, Width(primitives[2]), 2);
        Assert.Equal((2172 - CapRight) * PerPixel, Width(primitives[4]), 2);
        Assert.Equal(Width(primitives[1]), Width(primitives[3]), 2);
    }

    [Fact]
    public void AtItsShortest_ThePlaqueIsItsCapsAndCenter_WithNoFill()
    {
        var primitives = Primitives(Nameplate, new Vector2(1652 * PerPixel, PlaqueHeight));

        Assert.Equal([ArtPiece.LeftCap, ArtPiece.Center, ArtPiece.RightCap], primitives.Select(p => p.Piece));
        Assert.Equal(primitives[0].B, primitives[1].A);
        Assert.Equal(primitives[1].B, primitives[2].A);
    }

    [Fact]
    public void ABoxNarrowerThanTheFixedPieces_SqueezesThem_IntoTheBox()
    {
        var width = 1652 * PerPixel / 2f;
        var primitives = Primitives(Nameplate, new Vector2(width, PlaqueHeight));

        Assert.Equal([ArtPiece.LeftCap, ArtPiece.Center, ArtPiece.RightCap], primitives.Select(p => p.Piece));
        Assert.Equal(0f, primitives[0].A.X);
        Assert.Equal(width, primitives[^1].B.X, 3);
        Assert.Equal(CapLeft * PerPixel / 2f, Width(primitives[0]), 3);
    }

    private static ElementRect NameplateRect(float nameWidth)
    {
        var document = OneName(new Vector2(280f, NameTop), new Vector2(720f, NameHeight), TextAlignment.Center);
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingCelestialSakura)];
        return Single(document, _ => nameWidth).Placement.Rect;
    }

    private static ProfileDocument OneName(Vector2 position, Vector2 size, TextAlignment alignment)
    {
        var document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Sliced", ComponentDocuments.Now);
        document.Elements.Add(new TextProfileElement { Role = ProfileElementRole.BasicName, Text = "Name", Position = position, Size = size, Alignment = alignment, Wrap = false });
        return document;
    }

    private static PaintStep Single(ProfileDocument document, Func<TextProfileElement, float?>? measure)
    {
        var steps = new List<PaintStep>();
        ComponentPaintPlan.Build(document, document.Elements.Where(e => e.Visible).ToList(), BuiltInComponentCatalog.Instance, steps, measure);
        return Assert.Single(steps, s => !s.IsElement);
    }

    private static List<ComponentPrimitive> Primitives(BuiltInArtAsset art, Vector2 size)
    {
        var document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Sliced", ComponentDocuments.Now);
        var definition = ComponentDefinition.ForArt("test.sliced", "A sliced test piece.", art, ComponentColorSource.White);
        var component = new PlateComponent { Kind = definition.Kind, DefinitionId = definition.Id };
        var output = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, component, definition, new ComponentPlacement(new ElementRect(Vector2.Zero, size), 0f, false, false), output);
        return output;
    }

    private static float Width(ComponentPrimitive primitive) => primitive.B.X - primitive.A.X;

    private static void AssertClose(Vector2 expected, Vector2 actual) => Assert.True(Vector2.Distance(expected, actual) < 0.01f, $"{expected} vs {actual}");
}
