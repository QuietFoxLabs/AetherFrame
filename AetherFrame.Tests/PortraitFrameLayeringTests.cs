using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Portrait Frames and Overlays always paint over the portrait picture — whether the picture is the
/// Basic portrait (the role-tagged element they follow) or, with no such element, a plain image in
/// the portrait's place (the Celestial Sakura showcase acceptance failure: the frame painted under it).
/// </summary>
public class PortraitFrameLayeringTests
{
    public static IEnumerable<object[]> PortraitFrameIds() =>
        BuiltInComponentCatalog.OfKind(PlateComponentKind.PortraitFrame).Select(d => new object[] { d.Id });

    [Theory]
    [MemberData(nameof(PortraitFrameIds))]
    public void EveryPortraitFrame_PaintsDirectlyAboveThePortrait(string definitionId)
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(definitionId)];

        var plan = ComponentDocuments.Plan(document);
        var portrait = plan.FindIndex(s => s.Element?.Role == ProfileElementRole.BasicPortrait);
        var frame = plan.FindIndex(s => !s.IsElement);

        Assert.Equal(portrait + 1, frame);
        Assert.Equal(PlateLayer.PortraitFrame, plan[frame].Layer);
    }

    [Theory]
    [InlineData(0)] // the portrait at the bottom of the element stack
    [InlineData(99)] // ... and at the top
    public void PortraitFrameAndOverlay_StayRightAboveThePortrait_WhateverItsZ(int portraitZ)
    {
        var document = ComponentDocuments.WithAnchors();
        document.Elements.Single(e => e.Role == ProfileElementRole.BasicPortrait).ZIndex = portraitZ;
        document.Components =
        [
            ComponentDocuments.Of(BuiltInComponentCatalog.PortraitOverlayVignette),
            ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameCelestialSakura),
        ];

        var plan = ComponentDocuments.Plan(document);
        var portrait = plan.FindIndex(s => s.Element?.Role == ProfileElementRole.BasicPortrait);

        Assert.Equal(PlateLayer.PortraitFrame, plan[portrait + 1].Layer);
        Assert.Equal(PlateLayer.PortraitOverlay, plan[portrait + 2].Layer);
    }

    [Fact]
    public void WithoutAPortraitElement_ThePortraitBandPaintsOverAPlainImageInThePortraitsPlace()
    {
        // The showcase Plate as saved in game: the picture is an Advanced image (no portrait role).
        var document = PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "Showcase", ComponentDocuments.Now, new PlateStarterContent(FakeCharacter.Hero));
        var picture = new ImageProfileElement { AssetId = Guid.NewGuid(), Position = new Vector2(76.5f, 76.6f), Size = new Vector2(328.3f, 583.4f), ZIndex = 0 };
        document.Elements.Add(picture);
        document.Components =
        [
            ComponentDocuments.Of(BuiltInComponentCatalog.BackgroundCelestialSakura),
            ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameCelestialSakura),
            ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameCelestialSakura, layerOrder: 1),
            ComponentDocuments.Of(BuiltInComponentCatalog.PortraitOverlayFade),
            ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentCelestialSakura),
        ];

        var plan = ComponentDocuments.Plan(document);
        var image = plan.FindIndex(s => ReferenceEquals(s.Element, picture));
        var frame = plan.FindIndex(s => s.Layer == PlateLayer.PortraitFrame);
        var overlay = plan.FindIndex(s => s.Layer == PlateLayer.PortraitOverlay);

        Assert.True(frame > image, "the frame must paint over the picture");
        Assert.True(overlay > frame);
        Assert.True(plan.FindIndex(s => s.Layer == PlateLayer.Decorations) > overlay);
        Assert.Equal(PlateLayer.Decorations, plan[^1].Layer);
        Assert.Equal(PlateLayer.Background, plan[0].Layer);

        // The Plate Frame artwork paints over the portrait band, as over a portrait element's and on the
        // preview cards, and under every text.
        var plateFrame = plan.FindIndex(s => s.Layer == PlateLayer.PlateFrame);
        Assert.True(plateFrame > overlay && plateFrame < plan.FindIndex(s => s.Element is TextProfileElement));

        var layout = Domain.Basic.AdventurePlateClassicLayout.GetRect(ProfileElementRole.BasicPortrait, AdventurePlateOrientation.Normal, document)!.Value;
        Assert.Equal(layout, plan[frame].Placement.Rect); // Offset and Scale still move it onto the picture
    }

    [Fact]
    public void TheSemanticOrder_BackgroundPortraitFrameOverlayPlateFrameArtContentDecorations()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components =
        [
            ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameCelestialSakura),
            ComponentDocuments.Of(BuiltInComponentCatalog.DividerCelestialSakuraSlim),
            ComponentDocuments.Of(BuiltInComponentCatalog.PortraitOverlayFade),
            ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingCelestialSakura),
            ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameCelestialSakura),
            ComponentDocuments.Of(BuiltInComponentCatalog.BackgroundCelestialSakura),
        ];

        var layers = ComponentDocuments.Plan(document).Select(s => s.IsElement ? (s.Element!.Role == ProfileElementRole.BasicPortrait ? PlateLayer.Portrait : PlateLayer.Identity) : s.Layer).ToList();

        Assert.Equal(
            [
                PlateLayer.Background,
                PlateLayer.Portrait, PlateLayer.PortraitFrame, PlateLayer.PortraitOverlay,
                PlateLayer.PlateFrame, // artwork: under every text, so it never covers the name
                PlateLayer.NameBacking, PlateLayer.Identity, PlateLayer.Identity, PlateLayer.Identity, PlateLayer.Identity, PlateLayer.Identity,
                PlateLayer.Decorations,
            ],
            layers);
    }

    [Fact]
    public void LayerOrder_StillOrdersComponentsWithinThePortraitBand()
    {
        var document = ComponentDocuments.WithAnchors();
        var sakura = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameCelestialSakura);
        var line = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameLine, layerOrder: 1);
        document.Components = [sakura, line];

        Assert.Equal([sakura, line], ComponentDocuments.Plan(document).Where(s => !s.IsElement).Select(s => s.Component));

        Assert.True(PlateComponentEditor.MoveInLayer(document, line.Id, -1)); // the Advanced "Move down within its layer"
        Assert.Equal([line, sakura], ComponentDocuments.Plan(document).Where(s => !s.IsElement).Select(s => s.Component));
    }
}
