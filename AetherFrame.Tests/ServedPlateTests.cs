using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Fonts;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Network.Sharing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The viewer's side of a shared Plate (N2-10; the specification's sections 8.5 and 8.6): a Plate
/// shared through the snapshot builder, served, read back and turned into what the renderer draws,
/// gives the renderer the publisher's own values, to the hundredth the layout carries.
/// </summary>
public sealed class ServedPlateTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AText_IsDrawnWithThePublishersOwnValues()
    {
        var plate = Blank();
        var original = new TextProfileElement
        {
            Text = "Hello there",
            Prefix = "*",
            Position = new Vector2(40.25f, 60.5f),
            Size = new Vector2(300.75f, 80.25f),
            FontFamily = ProfileFontFamilies.AetherFrameSerif,
            FontSize = 23.45f,
            Color = new Vector4(0.2f, 0.4f, 0.6f, 0.8f),
            Alignment = TextAlignment.Center,
            VerticalAlignment = TextVerticalAlignment.Bottom,
            Wrap = true,
            Bold = true,
            Italic = true,
            Underline = true,
            Strikethrough = true,
            AutoFitText = true,
            AutoFitMinimumSize = 9.5f,
            LetterSpacing = 1.5f,
            LineSpacing = 1.25f,
            OutlineEnabled = true,
            OutlineColor = new Vector4(1f, 0f, 0.5f, 0.3f),
            OutlineOpacity = 0.5f,
            OutlineThickness = 2.5f,
            ShadowEnabled = true,
            ShadowColor = new Vector4(0f, 0.25f, 1f, 1f),
            ShadowOpacity = 0.6f,
            ShadowOffsetX = -3.25f,
            ShadowOffsetY = 4.75f,
        };
        plate.Elements.Add(original);

        var served = Serve(plate);
        var text = Assert.Single(served.Steps.OfType<ServedText>());
        Assert.True(text.FontKnown);
        Assert.Equal("* Hello there", text.Text);

        var drawn = text.Element;
        Assert.Equal(original.Position, drawn.Position);
        Assert.Equal(original.Size, drawn.Size);
        Assert.Equal(original.FontFamily, drawn.FontFamily);
        Assert.Equal(23.45f, drawn.FontSize, 3);
        Assert.Equal(Bytes(original.Color), Bytes(drawn.Color));
        Assert.Equal((original.Alignment, original.VerticalAlignment), (drawn.Alignment, drawn.VerticalAlignment));
        Assert.Equal((true, true, true, true, true, true), (drawn.Wrap, drawn.Bold, drawn.Italic, drawn.Underline, drawn.Strikethrough, drawn.AutoFitText));
        Assert.Equal(9.5f, drawn.AutoFitMinimumSize, 3);
        Assert.Equal(1.5f, drawn.LetterSpacing, 3);
        Assert.Equal(1.25f, drawn.LineSpacing, 3);
        Assert.True(drawn.OutlineEnabled);
        Assert.Equal(2.5f, drawn.OutlineThickness, 3);

        // The renderer draws an effect in its colour's red, green and blue at its opacity, so the
        // colour's own alpha never mattered, and the viewer's opacity is the publisher's.
        Assert.Equal(Bytes(original.OutlineColor with { W = 1f }), Bytes(drawn.OutlineColor));
        Assert.Equal(PlateSnapshotBuilder.Channel(0.5f), PlateSnapshotBuilder.Channel(drawn.OutlineOpacity));
        Assert.True(drawn.ShadowEnabled);
        Assert.Equal(Bytes(original.ShadowColor), Bytes(drawn.ShadowColor));
        Assert.Equal(PlateSnapshotBuilder.Channel(0.6f), PlateSnapshotBuilder.Channel(drawn.ShadowOpacity));
        Assert.Equal((-3.25f, 4.75f), (drawn.ShadowOffsetX, drawn.ShadowOffsetY));
        Assert.Equal(TextProfileElement.CurrentLayoutVersion, drawn.LayoutVersion);

        // Its display text is carried whole: no affixes, no role, nothing for the renderer to add.
        Assert.Equal((string.Empty, string.Empty, ProfileElementRole.None), (drawn.Prefix, drawn.Suffix, drawn.Role));
        Assert.Same(drawn, Assert.Single(served.FontWarmup.Elements));
    }

    [Fact]
    public void ALegacyText_StaysLegacy()
    {
        var plate = Blank();
        plate.Elements.Add(new TextProfileElement { Text = "Old", FontFamily = ProfileFontFamilies.DalamudDefault, LayoutVersion = TextProfileElement.LegacyLayoutVersion });

        var drawn = Assert.Single(Serve(plate).Steps.OfType<ServedText>()).Element;
        Assert.Equal(TextProfileElement.LegacyLayoutVersion, drawn.LayoutVersion);
        Assert.Equal(ProfileFontFamilies.DalamudDefault, drawn.FontFamily);
    }

    [Fact]
    public void AnImage_AndTheBackground_AreDrawnWithThePublishersOwnValues()
    {
        var plate = Blank();
        var backdrop = Guid.NewGuid();
        plate.Background = new ProfileBackground
        {
            Mode = ProfileBackgroundMode.Image,
            ImageAssetId = backdrop,
            ImageFit = ProfileImageFit.Fit,
            ImageFlipX = true,
            Opacity = 0.9f,
            Texture = ProfileBackgroundTexture.Honeycomb,
            TextureIntensity = 0.4f,
            TextureScale = 30.5f,
            TextureRotation = 15.25f,
            SecondaryColor = new Vector4(0.1f, 0.2f, 0.3f, 0.7f),
        };
        var original = new ImageProfileElement
        {
            AssetId = Guid.NewGuid(),
            Position = new Vector2(100.5f, 120.25f),
            Size = new Vector2(200f, 150.75f),
            RotationDegrees = 12.5f,
            DisplayMode = ProfileImageFit.Fill,
            FlipY = true,
            Opacity = 0.75f,
        };
        plate.Elements.Add(original);

        var served = Serve(plate);
        var image = Assert.Single(served.Steps.OfType<ServedImageStep>());
        Assert.InRange(image.Index, 0, 1);
        Assert.Equal(ServedPlate.ImageKey(image.Index), image.Element.AssetId);
        Assert.Equal(image.Index, ServedPlate.IndexOf(image.Element.AssetId));
        Assert.Equal((original.Position, original.Size, original.RotationDegrees), (image.Element.Position, image.Element.Size, image.Element.RotationDegrees));
        Assert.Equal((ProfileImageFit.Fill, false, true), (image.Element.DisplayMode, image.Element.FlipX, image.Element.FlipY));
        Assert.Equal(PlateSnapshotBuilder.Channel(0.75f), PlateSnapshotBuilder.Channel(image.Element.Opacity));

        var background = Assert.IsType<ProfileBackground>(served.Background);
        Assert.Equal(1 - image.Index, served.BackgroundIndex);
        Assert.Equal(ServedPlate.ImageKey(served.BackgroundIndex), background.ImageAssetId);
        Assert.Equal((ProfileBackgroundMode.Image, ProfileImageFit.Fit, true, false), (background.Mode, background.ImageFit, background.ImageFlipX, background.ImageFlipY));
        Assert.Equal((ProfileBackgroundTexture.Honeycomb, 30.5f, 15.25f), (background.Texture, background.TextureScale, background.TextureRotation));
        Assert.Equal(PlateSnapshotBuilder.Channel(0.9f), PlateSnapshotBuilder.Channel(background.Opacity));
        Assert.Equal(PlateSnapshotBuilder.Channel(0.4f), PlateSnapshotBuilder.Channel(background.TextureIntensity));
        Assert.Equal(Bytes(plate.Background.SecondaryColor), Bytes(background.SecondaryColor));
    }

    [Fact]
    public void AClassicPlate_DrawsEveryShapeAtItsPointsAndColour()
    {
        var plate = PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "Adventure Plate", Now, new PlateStarterContent(new BasicCharacterInfo("Visible Hero", "Phoenix", "Light", 19, "Paladin", 100, "ABC")));
        plate.Components =
        [
            ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameDouble),
            ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameCelestialSakura),
            ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingCelestialSakura),
        ];
        var (candidate, served) = ServeWithCandidate(plate);

        Assert.Equal(candidate.Items.Count, served.Steps.Count);
        for (var index = 0; index < candidate.Items.Count; index++)
        {
            switch (candidate.Items[index])
            {
                case LayoutQuad quad:
                    var drawnQuad = Assert.IsType<ServedShape>(served.Steps[index]);
                    Assert.Equal(ServedShapeKind.Quad, drawnQuad.Kind);
                    Assert.Equal(new[] { Units(quad.A), Units(quad.B), Units(quad.C), Units(quad.D) }, new[] { drawnQuad.A, drawnQuad.B, drawnQuad.C, drawnQuad.D });
                    Assert.Equal(quad.Color, Bytes(drawnQuad.Color));
                    break;

                case LayoutTriangle triangle:
                    var drawnTriangle = Assert.IsType<ServedShape>(served.Steps[index]);
                    Assert.Equal(ServedShapeKind.Triangle, drawnTriangle.Kind);
                    Assert.Equal(new[] { Units(triangle.A), Units(triangle.B), Units(triangle.C) }, new[] { drawnTriangle.A, drawnTriangle.B, drawnTriangle.C });
                    Assert.Equal(triangle.Color, Bytes(drawnTriangle.Color));
                    break;

                case LayoutArtQuad art:
                    var drawnArt = Assert.IsType<ServedShape>(served.Steps[index]);
                    Assert.Equal(ServedShapeKind.Art, drawnArt.Kind);
                    var found = BuiltInArtCatalog.FindPiece(art.Art);
                    Assert.NotNull(found);
                    Assert.Same(found.Value.Art, drawnArt.Art);
                    Assert.Equal(found.Value.Piece, drawnArt.Piece);
                    Assert.Equal(art.Tint, Bytes(drawnArt.Color));
                    break;

                case LayoutText text:
                    Assert.Equal(text.Text, Assert.IsType<ServedText>(served.Steps[index]).Text);
                    break;
            }
        }

        Assert.Contains(served.Steps, step => step is ServedShape { Kind: ServedShapeKind.Quad });
        Assert.Contains(served.Steps, step => step is ServedShape { Kind: ServedShapeKind.Art, Art: not null });
        Assert.Contains(served.Steps, step => step is ServedShape { Kind: ServedShapeKind.Art, Piece: ArtPiece.Center });
        Assert.Contains(served.Steps, step => step is ServedText);
        Assert.Empty(served.Notes);
    }

    [Fact]
    public void AViewer_FitsThePublishersOwnVisualBounds_OverflowIncluded()
    {
        // The owner's request of October 1, 2026: another player's Plate shows as My Plates' View
        // shows one, artwork past the Plate's edges included.
        var plate = PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "Adventure Plate", Now, new PlateStarterContent(new BasicCharacterInfo("Visible Hero", "Phoenix", "Light", 19, "Paladin", 100, "ABC")));
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameCelestialSakura);
        frame.Scale = 1.3f;
        plate.Components = [frame];
        var expected = AetherFrame.UI.Rendering.ProfileVisualBounds.Compute(plate);
        Assert.True(expected.Min.X < 0f || expected.Min.Y < 0f, "The frame should overflow the canvas.");

        var served = Serve(plate);
        Assert.Equal(expected.Min.X, served.VisualMin.X, 1);
        Assert.Equal(expected.Min.Y, served.VisualMin.Y, 1);
        Assert.Equal(expected.Max.X, served.VisualMax.X, 1);
        Assert.Equal(expected.Max.Y, served.VisualMax.Y, 1);

        // A procedural frame's shapes sit inside its placement box, which the publisher's bounds
        // unite: the viewer's fit holds every shape and the canvas, within the publisher's bounds.
        var drawn = PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "Adventure Plate", Now, new PlateStarterContent(new BasicCharacterInfo("Visible Hero", "Phoenix", "Light", 19, "Paladin", 100, "ABC")));
        var procedural = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameDouble);
        procedural.Scale = 1.3f;
        drawn.Components = [procedural];
        var publisher = AetherFrame.UI.Rendering.ProfileVisualBounds.Compute(drawn);
        var servedDrawn = Serve(drawn);
        Assert.True(servedDrawn.VisualMin.X >= publisher.Min.X - 0.01f && servedDrawn.VisualMin.Y >= publisher.Min.Y - 0.01f);
        Assert.True(servedDrawn.VisualMax.X <= publisher.Max.X + 0.01f && servedDrawn.VisualMax.Y <= publisher.Max.Y + 0.01f);
        Assert.True(servedDrawn.VisualMin.X < 0f && servedDrawn.VisualMax.X > servedDrawn.Canvas.X, "The frame's shapes overflow the canvas, and the fit holds them.");
        foreach (var shape in servedDrawn.Steps.OfType<ServedShape>())
        {
            foreach (var point in new[] { shape.A, shape.B, shape.C, shape.D })
            {
                Assert.InRange(point.X, servedDrawn.VisualMin.X, servedDrawn.VisualMax.X);
                Assert.InRange(point.Y, servedDrawn.VisualMin.Y, servedDrawn.VisualMax.Y);
            }
        }

        // Without overflow, it is the canvas.
        var blank = Serve(Blank());
        Assert.Equal((Vector2.Zero, blank.Canvas), (blank.VisualMin, blank.VisualMax));
    }

    [Fact]
    public void AFontOrArtworkThisBuildLacks_IsNamedInANote_AndNeverLookedFor()
    {
        var marker = RevisionMarker.NewMarker();
        var items = new LayoutItem[]
        {
            new LayoutText(new LayoutPoint(0, 0), 1000, 1000, "Hi", "some.future-font", 1600, new LayoutColor(255, 255, 255, 255), LayoutHorizontalAlign.Left, LayoutVerticalAlign.Top, LayoutTextFlags.None, 0, 100, 800, default, 0, default, 0, 0, LayoutTextLayout.Current),
            new LayoutArtQuad("some.future-art", new LayoutPoint(0, 0), new LayoutPoint(100, 0), new LayoutPoint(100, 100), new LayoutPoint(0, 100), new LayoutColor(255, 255, 255, 255)),
        };
        var snapshot = new ProfileLayoutSnapshot(ProfileId.NewId(), RevisionId.NewId(), 1_790_000_000, "Future", 10_000, 10_000, LayoutBackground.None, items, []);
        var served = ServedPlate.From(ServedProfile.Read(ServedProfile.Build(snapshot, marker)));

        var text = Assert.IsType<ServedText>(served.Steps[0]);
        Assert.False(text.FontKnown);
        Assert.Empty(served.FontWarmup.Elements);
        Assert.Null(Assert.IsType<ServedShape>(served.Steps[1]).Art);
        Assert.Equal(2, served.Notes.Count);
        Assert.Contains("some.future-font", served.Notes[0], StringComparison.Ordinal);
        Assert.Contains("some.future-art", served.Notes[1], StringComparison.Ordinal);
        Assert.Null(served.Background);
        Assert.Equal(-1, served.BackgroundIndex);
    }

    [Fact]
    public void OnlyTheBundledFonts_AreKnown()
    {
        foreach (var family in ProfileFontCatalog.All)
        {
            Assert.True(ServedPlate.IsBundledFont(family.Id));
        }

        Assert.False(ServedPlate.IsBundledFont(ProfileFontFamilies.AetherFrameSans.ToUpperInvariant()));
        Assert.False(ServedPlate.IsBundledFont("../fonts/evil"));
        Assert.Equal(-1, ServedPlate.IndexOf(Guid.Empty));
        Assert.Equal(-1, ServedPlate.IndexOf(Guid.NewGuid()));
    }

    private static ServedPlate Serve(ProfileDocument plate) => ServeWithCandidate(plate).Served;

    private static (SnapshotCandidate Candidate, ServedPlate Served) ServeWithCandidate(ProfileDocument plate)
    {
        Assert.True(PlateSnapshotBuilder.TryResolve(plate, new Measurements(), out var resolved));
        var prepared = new Dictionary<ImageRequirement, ImagePreparation>();
        foreach (var requirement in resolved.Requirements)
        {
            var bytes = new byte[64];
            requirement.Image.ToByteArray().CopyTo(bytes, 0);
            prepared[requirement] = ImagePreparation.Prepared(new PreparedImage(new ImageReference(AssetId.NewId(), SHA256.HashData(bytes), ImageFormat.Png, bytes.Length, requirement.Window.Width, requirement.Window.Height), bytes));
        }

        var result = PlateSnapshotBuilder.Map(resolved, prepared);
        Assert.Empty(result.Problems);
        var candidate = result.Candidate!;
        var bytesServed = ServedProfile.Build(candidate.ToSnapshot(ProfileId.NewId(), RevisionId.NewId(), 1_790_000_000), RevisionMarker.NewMarker());
        return (candidate, ServedPlate.From(ServedProfile.Read(bytesServed)));
    }

    private static ProfileDocument Blank() => PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Shared", Now);

    private static LayoutColor Bytes(Vector4 color) =>
        new(PlateSnapshotBuilder.Channel(color.X), PlateSnapshotBuilder.Channel(color.Y), PlateSnapshotBuilder.Channel(color.Z), PlateSnapshotBuilder.Channel(color.W));

    private static Vector2 Units(LayoutPoint point) => new((float)(point.X / 100d), (float)(point.Y / 100d));

    /// <summary>Measurements with fixed answers, standing in for the renderer's.</summary>
    private sealed class Measurements : IPlateMeasurements
    {
        public bool TryMeasureNaturalWidth(TextProfileElement element, out float width)
        {
            width = element.GetDisplayText().Length * element.FontSize * 0.5f;
            return true;
        }

        public bool TryGetDisplayOverride(ProfileDocument plate, TextProfileElement element, out string? display)
        {
            display = null;
            return true;
        }

        public bool TryGetImageSize(Guid image, out int width, out int height)
        {
            (width, height) = (64, 32);
            return true;
        }
    }
}
