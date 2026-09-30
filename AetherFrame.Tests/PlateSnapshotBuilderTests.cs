using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Signing;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// NETWORK2's snapshot builder (N2-6a): a saved Plate becomes a candidate snapshot holding what the
/// local renderer visibly draws, in its order, and nothing else, or the reasons it can't. Every
/// value is carried as the renderer resolves it; nothing is clamped, trimmed or dropped to get past
/// a limit; each image is cropped to the windows drawn of it. Snapshots are signed here with
/// ephemeral keys only.
/// </summary>
public sealed class PlateSnapshotBuilderTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
    private const long CreatedAt = 1_790_000_000;

    private readonly ProfileId profile = ProfileId.NewId();
    private readonly RevisionId revision = RevisionId.NewId();

    [Fact]
    public void AClassicPlate_BuildsACandidate_ThatSignsAndVerifiesAsItself()
    {
        var plate = ClassicPlate();
        var resolved = Resolve(plate);
        var candidate = Candidate(resolved);
        var snapshot = candidate.ToSnapshot(profile, revision, CreatedAt);

        using var signer = EcdsaPersonaSigner.CreateEphemeral();
        var verified = Assert.IsType<ProfileLayoutSnapshot>(SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(snapshot, signer)).Document);

        Assert.Equal(plate.Name, verified.Name);
        Assert.Equal(Fingerprint(snapshot.Background), Fingerprint(verified.Background));
        Assert.Equal(snapshot.Items.Select(Fingerprint), verified.Items.Select(Fingerprint));
        Assert.Equal(candidate.Items.Count, candidate.Roles.Count);
        Assert.Equal(plate.ProfileId, candidate.PlateId);

        // What the Plate shows is what is shared, flagged by role: the name the starter filled in.
        var nameIndex = candidate.Items.ToList().FindIndex(item => item is LayoutText { Text: "Visible Hero" });
        Assert.Equal(ProfileElementRole.BasicName, candidate.Roles[nameIndex]);
    }

    [Fact]
    public void NothingTheRendererDoesntDraw_ReachesTheSignedSnapshot()
    {
        var plate = ComponentDocuments.WithAnchors();
        var hidden = new TextProfileElement { Text = "CanaryHiddenText", Visible = false, ZIndex = 10 };
        var outside = new TextProfileElement { Text = "CanaryOutsideText", Position = new Vector2(-100_000f, -100_000f), ZIndex = 11 };
        var transparent = new ImageProfileElement { AssetId = Guid.NewGuid(), Opacity = 0f, ZIndex = 12 };
        plate.Elements.Add(hidden);
        plate.Elements.Add(outside);
        plate.Elements.Add(transparent);

        // The heading over an emptied section isn't drawn: plant its text.
        var heading = (TextProfileElement)plate.Elements.Single(e => e.Role == ProfileElementRole.BasicWorldHeading);
        heading.Text = "CanaryHeadingText";
        ((TextProfileElement)plate.Elements.Single(e => e.Role == ProfileElementRole.BasicWorld)).Text = string.Empty;

        var stale = Guid.NewGuid();
        plate.Background = new ProfileBackground { Mode = ProfileBackgroundMode.SolidColor, ImageAssetId = stale, ExtensionData = Canary("background") };
        plate.OwnerContentId = 0x1122334455667788UL;
        plate.Revision = 0x5A6B7C8D;
        plate.CreatedAtUtc = new DateTime(2031, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        plate.UpdatedAtUtc = new DateTime(2032, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        plate.ExtensionData = Canary("document");
        plate.BasicIdentity = new BasicIdentityHeader { RegionWidth = 1234.5f, ExtensionData = Canary("identity") };
        plate.BasicPlate = new BasicPlateSettings { FavoriteJobId = 0x6E6F7071, Level = 0x10203040, ExtensionData = Canary("basic") };
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameDouble);
        frame.ExtensionData = Canary("component");
        plate.Components = [frame];
        for (var index = 0; index < plate.Elements.Count; index++)
        {
            plate.Elements[index].Name = "CanaryName" + index.ToString(CultureInfo.InvariantCulture);
            plate.Elements[index].ExtensionData = Canary("element" + index.ToString(CultureInfo.InvariantCulture));
        }

        var resolved = Resolve(plate);
        var candidate = Candidate(resolved);
        using var signer = EcdsaPersonaSigner.CreateEphemeral();
        var bytes = SignedDocumentCodec.Sign(candidate.ToSnapshot(profile, revision, CreatedAt), signer);

        // Neither a background image kept from another mode nor an image drawn at opacity 0 is prepared.
        Assert.DoesNotContain(resolved.Requirements, r => r.Image == stale || r.Image == transparent.AssetId);
        Assert.Contains(candidate.LeftOut, left => left.Element == transparent && left.Reason == LeftOutReason.Transparent);
        Assert.Contains(candidate.LeftOut, left => left.Element == outside && left.Reason == LeftOutReason.OutsideView);

        var texts = new List<string> { "CanaryHiddenText", "CanaryOutsideText", "CanaryHeadingText" };
        texts.AddRange(plate.Elements.Select(e => e.Name));
        texts.AddRange(new[] { "background", "document", "identity", "basic", "component" }.Concat(Enumerable.Range(0, plate.Elements.Count).Select(i => "element" + i.ToString(CultureInfo.InvariantCulture))).Select(CanaryValue));
        foreach (var text in texts)
        {
            Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(text)), text);
        }

        var ids = new List<Guid> { plate.ProfileId, stale, frame.Id };
        ids.AddRange(plate.Elements.Select(e => e.Id));
        ids.AddRange(plate.Elements.OfType<ImageProfileElement>().Select(e => e.AssetId));
        foreach (var id in ids)
        {
            Assert.False(Contains(bytes, id.ToByteArray()), $"local id {id}");
            Assert.False(Contains(bytes, id.ToByteArray(bigEndian: true)), $"local id {id}, big-endian");
        }

        foreach (var number in new[]
        {
            BitConverter.GetBytes(plate.OwnerContentId),
            BitConverter.GetBytes(plate.Revision),
            BitConverter.GetBytes(new DateTimeOffset(plate.CreatedAtUtc).ToUnixTimeSeconds()),
            BitConverter.GetBytes(new DateTimeOffset(plate.UpdatedAtUtc).ToUnixTimeSeconds()),
            BitConverter.GetBytes(plate.CreatedAtUtc.Ticks),
            BitConverter.GetBytes(plate.UpdatedAtUtc.Ticks),
            BitConverter.GetBytes(0x6E6F7071u),
            BitConverter.GetBytes(0x10203040),
        })
        {
            Assert.False(Contains(bytes, number));
            Assert.False(Contains(bytes, number.Reverse().ToArray()));
        }
    }

    [Fact]
    public void TheSteps_FollowTheRenderersOwnPlan_ElementForElement_AndPrimitiveForPrimitive()
    {
        var plate = ComponentDocuments.WithAnchors();
        plate.Components = [.. ComponentDocuments.OneOfEach(), ImageComponent()];
        foreach (var component in plate.Components.Where(c => BuiltInComponentCatalog.Find(c.DefinitionId)!.RequiresAsset))
        {
            component.AssetId ??= Guid.NewGuid();
        }

        var measurements = new Measurements();
        var resolved = Resolve(plate, measurements);

        var paintOrder = new List<ProfileElement>();
        var drawn = new List<ProfileElement>();
        var plan = new List<PaintStep>();
        ProfileVisualBounds.FillDrawnElements(plate, ProfileRenderOptions.Finished, paintOrder, drawn);
        ComponentPaintPlan.Build(plate, drawn, BuiltInComponentCatalog.Instance, plan, e => measurements.TryMeasureNaturalWidth(e, out var width) ? width : null);

        var expected = new List<object>();
        var primitives = new List<ComponentPrimitive>();
        foreach (var step in plan)
        {
            if (step.Element is { } element)
            {
                expected.Add(element);
                continue;
            }

            primitives.Clear();
            ComponentGeometry.Build(plate, step.Component!, step.Definition!, step.Placement, primitives);
            expected.AddRange(primitives.Cast<object>());
        }

        var actual = resolved.Steps.Select(step => step switch
        {
            ResolvedText text => (object)text.Element,
            ResolvedImage image => image.Element,
            ResolvedShape shape => shape.Primitive,
            _ => throw new InvalidOperationException(),
        });
        Assert.Equal(expected, actual);
        Assert.Contains(resolved.Steps, step => step is ResolvedShape { Primitive.Kind: ComponentPrimitiveKind.Image });
    }

    [Fact]
    public void EveryBuiltInComponent_MapsItsPrimitives_PointForPoint_InTheRenderersColours()
    {
        foreach (var definition in BuiltInComponentCatalog.All)
        {
            var plate = ComponentDocuments.WithAnchors();
            var component = ComponentDocuments.Of(definition.Id);
            if (definition.RequiresAsset)
            {
                component.AssetId = Guid.NewGuid();
            }

            component.RotationDegrees = 12.5f;
            plate.Components = [component];
            var resolved = Resolve(plate);
            var snapshot = Build(resolved);

            Assert.Equal(resolved.Steps.Count, snapshot.Items.Count);
            for (var index = 0; index < resolved.Steps.Count; index++)
            {
                if (resolved.Steps[index] is not ResolvedShape shape)
                {
                    continue;
                }

                var primitive = shape.Primitive;
                var item = snapshot.Items[index];
                var (points, color) = item switch
                {
                    LayoutQuad quad => (new[] { quad.A, quad.B, quad.C, quad.D }, quad.Color),
                    LayoutTriangle triangle => (new[] { triangle.A, triangle.B, triangle.C }, triangle.Color),
                    LayoutImageQuad image => (new[] { image.A, image.B, image.C, image.D }, image.Tint),
                    LayoutArtQuad art => (new[] { art.A, art.B, art.C, art.D }, art.Tint),
                    _ => throw new InvalidOperationException(definition.Id),
                };

                Assert.Equal(new[] { primitive.A, primitive.B, primitive.C, primitive.D }.Take(points.Length).Select(Point), points);
                Assert.Equal(Bytes(primitive.Color), color);
                if (item is LayoutArtQuad artQuad)
                {
                    Assert.Equal(definition.Art!.Id, artQuad.Art);
                }
            }
        }
    }

    [Fact]
    public void AText_IsSharedWithItsDisplayText_AndEveryField_AsTheRendererResolvesIt()
    {
        var plate = Blank();
        var text = new TextProfileElement
        {
            Text = "Hello",
            Prefix = "*",
            Suffix = "+",
            Position = new Vector2(10.125f, 20.5f),
            Size = new Vector2(300f, 40.25f),
            FontFamily = "a-font-this-build-lacks",
            FontSize = 18f,
            Color = new Vector4(1f, 0.5f, 0f, 0.8f),
            Alignment = TextAlignment.Right,
            VerticalAlignment = TextVerticalAlignment.Bottom,
            Wrap = false,
            Bold = true,
            Italic = true,
            Underline = true,
            Strikethrough = true,
            AutoFitText = true,
            AutoFitMinimumSize = 0.5f,
            LetterSpacing = -1.25f,
            LineSpacing = 1.25f,
            OutlineEnabled = true,
            OutlineColor = new Vector4(0f, 0f, 1f, 0.2f),
            OutlineOpacity = 0.5f,
            OutlineThickness = 0.004f,
            ShadowEnabled = true,
            ShadowColor = new Vector4(0.25f, 0.25f, 0.25f, 0f),
            ShadowOpacity = 1f,
            ShadowOffsetX = -2.5f,
            ShadowOffsetY = 4f,
            LayoutVersion = TextProfileElement.LegacyLayoutVersion,
        };
        plate.Elements.Add(text);

        var item = Assert.IsType<LayoutText>(Assert.Single(Build(Resolve(plate)).Items));
        Assert.Equal("* Hello +", item.Text);
        Assert.Equal(new LayoutPoint(1013, 2050), item.Position);
        Assert.Equal((30_000, 4025), (item.Width, item.Height));

        // The renderer draws a family it lacks in Dalamud's default font.
        Assert.Equal(ProfileFontFamilies.DalamudDefault, item.Font);
        Assert.Equal(1800, item.FontSize);
        Assert.Equal(new LayoutColor(255, 128, 0, 204), item.Color);
        Assert.Equal(LayoutHorizontalAlign.Right, item.Align);
        Assert.Equal(LayoutVerticalAlign.Bottom, item.VerticalAlign);
        Assert.Equal(
            LayoutTextFlags.Bold | LayoutTextFlags.Italic | LayoutTextFlags.Underline | LayoutTextFlags.Strikethrough
                | LayoutTextFlags.AutoFit | LayoutTextFlags.Outline | LayoutTextFlags.Shadow,
            item.Flags);
        Assert.Equal(-125, item.LetterSpacing);
        Assert.Equal(125, item.LineSpacing);

        // The renderer shrinks an auto-fit text to no less than one canvas unit.
        Assert.Equal(100, item.AutoFitMinimum);

        // An effect is drawn in its colour's red, green and blue at the text's alpha times its
        // opacity, the colour's own alpha ignored; a viewer multiplies by the text's alpha.
        Assert.Equal(new LayoutColor(0, 0, 255, 128), item.OutlineColor);
        Assert.Equal(new LayoutColor(64, 64, 64, 255), item.ShadowColor);

        // An outline above 0 is never rounded away.
        Assert.Equal(1, item.OutlineThickness);
        Assert.Equal((-250, 400), (item.ShadowX, item.ShadowY));
        Assert.Equal(LayoutTextLayout.Legacy, item.Layout);
    }

    [Fact]
    public void TheBasicName_IsSharedAtTheSizeBasicNameFitDrawsIt_RoundedDown_WithAutoFitOff()
    {
        foreach (var (natural, expectedSize, expectedWrap) in new[] { (95f, 3873, false), (150f, 3600, true), (50f, 4000, false) })
        {
            var plate = Blank();
            var name = new TextProfileElement { Role = ProfileElementRole.BasicName, Text = "Name", FontSize = 40f, Size = new Vector2(100f, 50f), AutoFitText = true, Wrap = false };
            plate.Elements.Add(name);

            var item = Assert.IsType<LayoutText>(Assert.Single(Build(Resolve(plate, new Measurements { Width = _ => natural })).Items));

            // 40 x 92 / 95 is 38.7368...: rounded down, never up. Too wide even at 90% wraps at 90%.
            Assert.Equal(expectedSize, item.FontSize);
            Assert.Equal(expectedWrap, item.Flags.HasFlag(LayoutTextFlags.Wrap));
            Assert.False(item.Flags.HasFlag(LayoutTextFlags.AutoFit));
        }
    }

    [Fact]
    public void AFontNotYetBuilt_FailsTheResolve_RatherThanGuessing()
    {
        // A Name Backing follows the measured name when the name doesn't wrap.
        var plate = ComponentDocuments.WithAnchors();
        ((TextProfileElement)plate.Elements.Single(e => e.Role == ProfileElementRole.BasicName)).Wrap = false;
        plate.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingBar)];
        Assert.False(PlateSnapshotBuilder.TryResolve(plate, new Measurements { Width = _ => null }, out _));
        Assert.True(PlateSnapshotBuilder.TryResolve(plate, new Measurements(), out _));

        var jobs = Blank();
        jobs.Elements.Add(new TextProfileElement { Role = ProfileElementRole.BasicJob, Text = "Paladin" });
        Assert.False(PlateSnapshotBuilder.TryResolve(jobs, new Measurements { Display = _ => (false, null) }, out _));
    }

    [Fact]
    public void TheFavoriteJobsDisplay_IsWhatIsShared_NotTheStoredText()
    {
        var plate = Blank();
        plate.Elements.Add(new TextProfileElement { Role = ProfileElementRole.BasicJob, Text = "Paladin, Warrior, Dark Knight" });

        var resolved = Resolve(plate, new Measurements { Display = e => (true, e.Role == ProfileElementRole.BasicJob ? "Paladin +2" : null) });

        Assert.Equal("Paladin +2", Assert.IsType<LayoutText>(Assert.Single(Build(resolved).Items)).Text);
    }

    [Fact]
    public void WhatDrawsNothing_IsLeftOut_WithItsReason()
    {
        var plate = Blank();
        var empty = new TextProfileElement { Text = string.Empty, ZIndex = 0 };
        var clear = new TextProfileElement { Text = "Clear", Color = new Vector4(1f, 1f, 1f, -0.5f), ZIndex = 1 };
        var faded = new ImageProfileElement { AssetId = Guid.NewGuid(), Opacity = 0f, ZIndex = 2 };
        var away = new ImageProfileElement { AssetId = Guid.NewGuid(), Position = new Vector2(5_000f, 5_000f), Size = new Vector2(10f, 10f), ZIndex = 3 };
        var edge = new TextProfileElement { Text = "Edge", Position = new Vector2(-50f, 10f), Size = new Vector2(52f, 20f), ZIndex = 4 };
        var shadowed = new TextProfileElement { Text = "Shadow", Position = new Vector2(-60f, 10f), Size = new Vector2(55f, 20f), ShadowEnabled = true, ShadowOffsetX = 8f, ZIndex = 5 };
        plate.Elements.AddRange([empty, clear, faded, away, edge, shadowed]);

        var candidate = Candidate(Resolve(plate));

        Assert.Equal(
            new[]
            {
                new LeftOutItem(empty, null, LeftOutReason.Empty),
                new LeftOutItem(clear, null, LeftOutReason.Transparent),
                new LeftOutItem(faded, null, LeftOutReason.Transparent),
                new LeftOutItem(away, null, LeftOutReason.OutsideView),
            },
            candidate.LeftOut);

        // A text crossing the canvas's edge, or whose shadow reaches onto it, is drawn, and shared.
        Assert.Equal(new[] { "Edge", "Shadow" }, candidate.Items.Cast<LayoutText>().Select(t => t.Text));
        Assert.Empty(candidate.Images);
    }

    [Fact]
    public void HiddenElements_AndSectionHeadingsWithNothingUnderThem_AreNotShared()
    {
        var plate = ComponentDocuments.WithAnchors();
        plate.Elements.Single(e => e.Role == ProfileElementRole.BasicTitle).Visible = false;
        ((TextProfileElement)plate.Elements.Single(e => e.Role == ProfileElementRole.BasicWorld)).Text = string.Empty;

        var candidate = Candidate(Resolve(plate));
        var texts = candidate.Items.OfType<LayoutText>().Select(t => t.Text).ToList();

        // The hidden title and the heading over an empty section aren't drawn in finished rendering;
        // the emptied value draws nothing, and is left out with that reason.
        Assert.Equal(new[] { "Name", "Caption" }, texts);
        Assert.Contains(candidate.LeftOut, left => left.Reason == LeftOutReason.Empty && ((TextProfileElement)left.Element!).Role == ProfileElementRole.BasicWorld);
    }

    [Fact]
    public void Colours_AreTheBytesImGuiMakes()
    {
        Assert.Equal(0, PlateSnapshotBuilder.Channel(-3f));
        Assert.Equal(255, PlateSnapshotBuilder.Channel(7f));
        Assert.Equal(128, PlateSnapshotBuilder.Channel(0.5f));
        Assert.Equal(64, PlateSnapshotBuilder.Channel(0.25f));
        Assert.Equal(254, PlateSnapshotBuilder.Channel(0.998f));
        Assert.Equal(255, PlateSnapshotBuilder.Channel(0.999f));
        for (var value = 0; value <= 255; value++)
        {
            Assert.Equal(value, PlateSnapshotBuilder.Channel(value / 255f));
        }
    }

    [Fact]
    public void Values_RoundToTheNearestHundredth_HalvesAwayFromZero_AndAnglesIntoOneTurn()
    {
        var plate = Blank();
        var local = Guid.NewGuid();
        plate.Elements.Add(new ImageProfileElement { AssetId = local, Position = new Vector2(0.125f, 0.375f), Size = new Vector2(0.375f, 100f), RotationDegrees = 725f });
        plate.Elements.Add(new ImageProfileElement { AssetId = local, Position = new Vector2(10f, 10f), Size = Vector2.One, RotationDegrees = -360f, ZIndex = 1 });

        var items = Build(Resolve(plate)).Items.Cast<LayoutImage>().ToList();

        Assert.Equal(new LayoutPoint(13, 38), items[0].Position);
        Assert.Equal(38, items[0].Width);
        Assert.Equal(500, items[0].Rotation);
        Assert.Equal(0, items[1].Rotation);
    }

    [Fact]
    public void EachImage_IsPreparedOnce_CroppedToTheUnionOfTheWindowsDrawnOfIt()
    {
        var plate = Blank();
        var photo = Guid.NewGuid();
        var whole = Guid.NewGuid();
        plate.Elements.Add(new ImageProfileElement { AssetId = photo, DisplayMode = ProfileImageFit.Fill, Size = new Vector2(100f, 100f), ZIndex = 0 });
        plate.Elements.Add(new ImageProfileElement { AssetId = photo, DisplayMode = ProfileImageFit.Fill, Size = new Vector2(200f, 100f), ZIndex = 1 });
        plate.Elements.Add(new ImageProfileElement { AssetId = whole, DisplayMode = ProfileImageFit.Fit, Size = new Vector2(100f, 100f), ZIndex = 2 });
        var measurements = new Measurements();
        measurements.Sizes[photo] = (1000, 400);
        measurements.Sizes[whole] = (300, 200);

        var resolved = Resolve(plate, measurements);

        // A square Fill of a 1,000 by 400 image draws its middle 400 by 400, and a 2:1 Fill its
        // middle 800 by 400 (each widened outward to whole pixels); the copy holds their union, and
        // a Fit draws the whole of its image.
        var square = PlateSnapshotBuilder.WindowOf(ProfileImageFit.Fill, new Vector2(100f, 100f), 1000, 400);
        var wide = PlateSnapshotBuilder.WindowOf(ProfileImageFit.Fill, new Vector2(200f, 100f), 1000, 400);
        Assert.InRange(square.X, 299, 300);
        Assert.InRange(square.Width, 400, 402);
        Assert.InRange(wide.X, 99, 100);
        Assert.InRange(wide.Width, 800, 802);
        var union = new PixelWindow(Math.Min(square.X, wide.X), 0, Math.Max(square.X + square.Width, wide.X + wide.Width) - Math.Min(square.X, wide.X), 400);
        Assert.Equal(
            new[] { new ImageRequirement(photo, union, 1000, 400), new ImageRequirement(whole, PixelWindow.Whole(300, 200), 300, 200) },
            resolved.Requirements);
        Assert.All(resolved.Steps.Cast<ResolvedImage>(), step => Assert.Contains(step.Image, resolved.Requirements));

        var snapshot = Build(resolved);
        Assert.Equal(2, snapshot.Images.Count);
        Assert.Equal(
            snapshot.Items.Cast<LayoutImage>().Take(2).Select(i => i.AssetId).Distinct().Single(),
            snapshot.Images.Single(i => i.Width == union.Width).AssetId);

        // The background's Fill window is taken against the canvas.
        var background = Blank();
        background.Background = new ProfileBackground { Mode = ProfileBackgroundMode.Image, ImageAssetId = photo, ImageFit = ProfileImageFit.Fill };
        var canvasAspect = background.CanvasWidth / background.CanvasHeight;
        var expected = PlateSnapshotBuilder.WindowOf(ProfileImageFit.Fill, new Vector2(background.CanvasWidth, background.CanvasHeight), 1000, 400);
        Assert.Equal(new ImageRequirement(photo, expected, 1000, 400), Assert.Single(Resolve(background, measurements).Requirements));
        Assert.True(canvasAspect > 0f && expected.Width < 1000);
    }

    [Fact]
    public void TheBackground_IsSharedInEveryMode_AsItDraws()
    {
        foreach (var mode in Enum.GetValues<ProfileBackgroundMode>())
        {
            var plate = Blank();
            plate.Background = new ProfileBackground
            {
                Mode = mode,
                PrimaryColor = new Vector4(0.1f, 0.2f, 0.3f, 1f),
                SecondaryColor = new Vector4(0.9f, 0.8f, 0.7f, 0.5f),
                GradientAngle = 450f,
                Opacity = 0.75f,
                Texture = ProfileBackgroundTexture.Honeycomb,
                TextureIntensity = 0.35f,
                TextureScale = 1_000f,
                TextureRotation = -30f,
                ImageAssetId = Guid.NewGuid(),
                ImageFit = ProfileImageFit.Fit,
                ImageFlipY = true,
            };

            var resolved = Resolve(plate);
            var prepared = Prepared(resolved);
            var background = Build(resolved, prepared).Background;

            if (mode == ProfileBackgroundMode.None)
            {
                // Mode None draws nothing at all, so nothing of it travels.
                Assert.Equal(Fingerprint(LayoutBackground.None), Fingerprint(background));
                continue;
            }

            Assert.Equal((byte)mode, (byte)background.Mode);
            Assert.Equal(new LayoutColor(26, 51, 77, 255), background.Primary);
            Assert.Equal(new LayoutColor(230, 204, 179, 128), background.Secondary);
            Assert.Equal(9_000, background.GradientAngle);
            Assert.Equal(191, background.Opacity);
            Assert.Equal(LayoutTexture.Honeycomb, background.Texture);

            // The renderer draws the pattern at its scale clamped to the pattern's range.
            Assert.Equal(LayoutBackground.MaxTextureScale, background.TextureScale);
            Assert.Equal(-3_000, background.TextureRotation);
            Assert.Equal(LayoutImageFit.Fit, background.ImageFit);
            Assert.Equal(LayoutFlips.Vertical, background.ImageFlips);

            // Only Image mode shares its image; any other mode never names the one it kept.
            Assert.Equal(mode == ProfileBackgroundMode.Image ? prepared.Values.Single().Reference.AssetId : default, background.ImageAssetId);
            Assert.Equal(mode == ProfileBackgroundMode.Image ? 1 : 0, resolved.Requirements.Count);
        }
    }

    [Fact]
    public void ABackgroundThatDrawsNothing_IsNone_AndImageModeWithoutAnImage_DrawsOnlyItsPattern()
    {
        var plate = Blank();
        plate.Background = new ProfileBackground { Mode = ProfileBackgroundMode.SolidColor, Opacity = 0f, PrimaryColor = new Vector4(0.5f, 0.5f, 0.5f, 1f) };
        Assert.Equal(Fingerprint(LayoutBackground.None), Fingerprint(Build(Resolve(plate)).Background));

        plate.Background = new ProfileBackground { Mode = ProfileBackgroundMode.Image, ImageAssetId = null, PrimaryColor = new Vector4(0.5f, 0.5f, 0.5f, 1f), Texture = ProfileBackgroundTexture.Dots };
        var background = Build(Resolve(plate)).Background;
        Assert.Equal(LayoutBackgroundMode.SolidColor, background.Mode);
        Assert.Equal(0, background.Primary.A);
        Assert.Equal(LayoutTexture.Dots, background.Texture);
        Assert.Equal(default, background.ImageAssetId);
    }

    [Fact]
    public void AComponentImageWithoutItsFile_IsLeftOut_AsTheRendererDrawsNothingThere()
    {
        var plate = ComponentDocuments.WithAnchors();
        var overlay = ImageComponent();
        plate.Components = [overlay];
        var measurements = new Measurements();
        measurements.Missing.Add(overlay.AssetId!.Value);

        var candidate = Candidate(Resolve(plate, measurements));

        Assert.DoesNotContain(candidate.Items, item => item is LayoutImageQuad);
        Assert.Contains(new LeftOutItem(null, overlay, LeftOutReason.ImageMissing), candidate.LeftOut);
        Assert.Single(candidate.Images);
    }

    [Fact]
    public void ANameTheNameRuleRefuses_IsRefused_AskingForARename()
    {
        foreach (var name in new[] { string.Empty, "Line" + (char)0x2028 + "break", new string('a', ProtocolLimits.MaxNameScalars + 1) })
        {
            var plate = Blank();
            plate.Name = name;
            Assert.Equal(PlateSnapshotRefusal.Name, Assert.Single(Refusals(Resolve(plate))).Refusal);
        }
    }

    [Fact]
    public void MoreItemsThanAPlateCanShare_AreRefused_NeverTrimmed()
    {
        var shape = new ResolvedShape(new ComponentPrimitive(ComponentPrimitiveKind.Triangle, Vector2.Zero, Vector2.One, new Vector2(0f, 1f), Vector2.Zero, Vector4.One), null, null);
        var steps = Enumerable.Repeat<ResolvedStep>(shape, ProtocolLimits.MaxLayoutItems + 1).ToList();

        Assert.Equal(PlateSnapshotRefusal.TooManyItems, Assert.Single(Refusals(Synthetic(steps))).Refusal);
        Assert.NotNull(Candidate(Synthetic(steps.Take(ProtocolLimits.MaxLayoutItems).ToList())));
    }

    [Fact]
    public void MoreImagesImageBytesOrImagePixelsThanAPlateCanShare_AreRefused_NeverDropped()
    {
        var plate = Blank();
        for (var index = 0; index <= ProtocolLimits.MaxImagesPerProfile; index++)
        {
            plate.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = index });
        }

        Assert.Contains(Refusals(Resolve(plate)), p => p.Refusal == PlateSnapshotRefusal.TooManyImages);

        var two = Blank();
        two.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = 0 });
        two.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = 1 });
        var resolved = Resolve(two);

        // Two images of 8,192 by 2,049 hold 33,570,816 pixels, 16,384 over the limit; by 2,048, exactly at it.
        Assert.Equal(PlateSnapshotRefusal.TooManyImagePixels, Assert.Single(Refusals(resolved, Prepared(resolved, width: 8192, height: 2049))).Refusal);
        Assert.NotNull(Candidate(resolved, Prepared(resolved, width: 8192, height: 2048)));

        // Five images of 8 MiB are 41,943,040 bytes, exactly the limit; six are over it.
        foreach (var (count, allowed) in new[] { (5, true), (6, false) })
        {
            var many = Blank();
            for (var index = 0; index < count; index++)
            {
                many.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = index });
            }

            var manyResolved = Resolve(many);
            var full = Prepared(manyResolved, byteLength: ProtocolLimits.MaxImageBytes);
            if (allowed)
            {
                Assert.NotNull(Candidate(manyResolved, full));
            }
            else
            {
                Assert.Equal(PlateSnapshotRefusal.TooManyImageBytes, Assert.Single(Refusals(manyResolved, full)).Refusal);
            }
        }
    }

    [Fact]
    public void MoreTextThanAPlateCanShare_OrATextNoSharedTextCanHold_IsRefused()
    {
        var plate = Blank();
        for (var index = 0; index < 17; index++)
        {
            plate.Elements.Add(new TextProfileElement { Text = new string('a', TextProfileElement.MaxTextLength), ZIndex = index });
        }

        Assert.Equal(PlateSnapshotRefusal.TooMuchText, Assert.Single(Refusals(Resolve(plate))).Refusal);

        foreach (var unshareable in new[] { "a" + (char)0 + "b", "a" + (char)0xD800, (char)0xDC00 + "a" })
        {
            var holding = Blank();
            var text = new TextProfileElement { Text = unshareable };
            holding.Elements.Add(text);
            Assert.Equal(new PlateSnapshotProblem(PlateSnapshotRefusal.TextUnshareable, text), Assert.Single(Refusals(Resolve(holding))));
        }

        var jobs = Blank();
        jobs.Elements.Add(new TextProfileElement { Role = ProfileElementRole.BasicJob, Text = "Paladin" });
        var tooLong = new string('a', ProtocolLimits.MaxLayoutItemTextScalars + 1);
        Assert.Equal(PlateSnapshotRefusal.TextTooLong, Assert.Single(Refusals(Resolve(jobs, new Measurements { Display = _ => (true, tooLong) }))).Refusal);
    }

    [Fact]
    public void AGradientColourOutsideZeroToOne_IsRefused_SinceTheRendererBlendsBeforeItResolves()
    {
        var plate = Blank();
        plate.Background = new ProfileBackground { Mode = ProfileBackgroundMode.LinearGradient, PrimaryColor = new Vector4(1.2f, 0f, 0f, 1f) };
        Assert.Equal(PlateSnapshotRefusal.GradientColor, Assert.Single(Refusals(Resolve(plate))).Refusal);

        // Outside a gradient, the colour is carried as drawn.
        plate.Background.Mode = ProfileBackgroundMode.SolidColor;
        Assert.Equal(new LayoutColor(255, 0, 0, 255), Build(Resolve(plate)).Background.Primary);
    }

    [Fact]
    public void WhatANewerAetherFrameMade_IsRefused_AsWhatThisBuildShowsIsntWhatItsMakerSaw()
    {
        var element = Blank();
        element.UnrecognizedElements = [JsonSerializer.SerializeToElement(new { elementType = "hologram" })];
        Assert.Equal(new PlateSnapshotProblem(PlateSnapshotRefusal.MadeByNewerVersion, null), Assert.Single(Refusals(Resolve(element))));

        var component = Blank();
        component.UnrecognizedComponents = [JsonSerializer.SerializeToElement(new { kind = 99 })];
        Assert.Equal(new PlateSnapshotProblem(PlateSnapshotRefusal.MadeByNewerVersion, null), Assert.Single(Refusals(Resolve(component))));
    }

    [Fact]
    public void AValueNoFieldCanExpress_UnknownElements_AndMissingImages_AreRefused_NamingTheElement()
    {
        var plate = Blank();
        var far = new TextProfileElement { Text = "Far", Position = new Vector2(20_000_000f, 0f), Size = new Vector2(100f, 20f) };
        plate.Components = [new PlateComponent { Kind = PlateComponentKind.PlateFrame, DefinitionId = BuiltInComponentCatalog.PlateFrameLine, Offset = Vector2.Zero }];
        var strange = new StrangeElement { ZIndex = 1 };
        var missing = new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = 2 };
        var missingAndClear = new ImageProfileElement { AssetId = Guid.Empty, Opacity = 0f, ZIndex = 3 };
        plate.Elements.AddRange([far, strange, missing, missingAndClear]);
        plate.Background = new ProfileBackground { Mode = ProfileBackgroundMode.Image, ImageAssetId = Guid.NewGuid() };
        plate.Name = string.Empty;
        var measurements = new Measurements();
        measurements.Missing.Add(missing.AssetId);
        measurements.Missing.Add(plate.Background.ImageAssetId!.Value);

        var problems = Refusals(Resolve(plate, measurements));

        // A missing image is refused even at opacity 0: finished rendering draws its placeholder.
        Assert.Equal(
            new[]
            {
                new PlateSnapshotProblem(PlateSnapshotRefusal.BackgroundImageMissing, null),
                new PlateSnapshotProblem(PlateSnapshotRefusal.UnknownElement, strange),
                new PlateSnapshotProblem(PlateSnapshotRefusal.ImageMissing, missing),
                new PlateSnapshotProblem(PlateSnapshotRefusal.ImageMissing, missingAndClear),
                new PlateSnapshotProblem(PlateSnapshotRefusal.Name, null),
            },
            problems.Where(p => p.Refusal != PlateSnapshotRefusal.ValueOutOfRange));

        // The text 20,000,000 units away is outside the view, and left out rather than refused.
        Assert.DoesNotContain(problems, p => p.Element == far);
    }

    [Fact]
    public void ACandidateBecomesASnapshot_ByAddingOnlyTheIdsAndTheTime()
    {
        var candidate = Candidate(Resolve(ClassicPlate()));
        var first = candidate.ToSnapshot(profile, revision, CreatedAt);
        var second = candidate.ToSnapshot(ProfileId.NewId(), RevisionId.NewId(), CreatedAt + 60);

        Assert.Equal(first.Items.Select(Fingerprint), second.Items.Select(Fingerprint));
        Assert.Equal(Fingerprint(first.Background), Fingerprint(second.Background));
        Assert.Equal(first.Images.Select(Fingerprint), second.Images.Select(Fingerprint));
        Assert.Equal((profile, revision, CreatedAt), (first.ProfileId, first.RevisionId, first.CreatedAtUnixSeconds));
    }

    [Fact]
    public void PaintVisibility_IsTheRenderersRule()
    {
        Assert.False(PaintVisibility.TextDraws(null, 1f));
        Assert.False(PaintVisibility.TextDraws(string.Empty, 1f));
        Assert.False(PaintVisibility.TextDraws("a", 0f));
        Assert.True(PaintVisibility.TextDraws("a", 0.01f));
        Assert.Equal(0f, PaintVisibility.TextAlpha(new TextProfileElement { Color = new Vector4(1f, 1f, 1f, -2f) }));
        Assert.Equal(1f, PaintVisibility.TextAlpha(new TextProfileElement { Color = new Vector4(1f, 1f, 1f, 3f) }));
        Assert.False(PaintVisibility.ImageDraws(new ImageProfileElement { Opacity = 0f }));
        Assert.True(PaintVisibility.ImageDraws(new ImageProfileElement { Opacity = 0.01f }));
        Assert.Equal(0f, PaintVisibility.BackgroundOpacity(null));
        Assert.Equal(0f, PaintVisibility.BackgroundOpacity(new ProfileBackground { Mode = ProfileBackgroundMode.None, Opacity = 1f }));
        Assert.Equal(1f, PaintVisibility.BackgroundOpacity(new ProfileBackground { Mode = ProfileBackgroundMode.SolidColor, Opacity = 2f }));
        Assert.Null(PaintVisibility.ComponentImage(new PlateComponent { AssetId = Guid.Empty }));
        Assert.Null(PaintVisibility.ComponentImage(new PlateComponent { AssetId = null }));
    }

    private static ResolvedPlate Resolve(ProfileDocument plate, Measurements? measurements = null)
    {
        Assert.True(PlateSnapshotBuilder.TryResolve(plate, measurements ?? new Measurements(), out var resolved));
        return resolved;
    }

    private static ResolvedPlate Synthetic(IReadOnlyList<ResolvedStep> steps) =>
        new(Guid.NewGuid(), "Many", 1280f, 720f, ResolvedBackground.Nothing, steps, [], [], []);

    private static SnapshotCandidate Candidate(ResolvedPlate resolved, IReadOnlyDictionary<ImageRequirement, PreparedImage>? prepared = null)
    {
        var result = PlateSnapshotBuilder.Map(resolved, prepared ?? Prepared(resolved));
        Assert.Empty(result.Problems);
        return result.Candidate!;
    }

    private ProfileLayoutSnapshot Build(ResolvedPlate resolved, IReadOnlyDictionary<ImageRequirement, PreparedImage>? prepared = null) =>
        Candidate(resolved, prepared).ToSnapshot(profile, revision, CreatedAt);

    private static IReadOnlyList<PlateSnapshotProblem> Refusals(ResolvedPlate resolved, IReadOnlyDictionary<ImageRequirement, PreparedImage>? prepared = null)
    {
        var result = PlateSnapshotBuilder.Map(resolved, prepared ?? Prepared(resolved));
        Assert.Null(result.Candidate);
        Assert.NotEmpty(result.Problems);
        return result.Problems;
    }

    /// <summary>A prepared copy of every required window, as N2-6b makes one: a fresh asset id, a digest, a size and some bytes.</summary>
    private static Dictionary<ImageRequirement, PreparedImage> Prepared(ResolvedPlate resolved, int? width = null, int? height = null, long byteLength = 1_000)
    {
        var prepared = new Dictionary<ImageRequirement, PreparedImage>();
        foreach (var requirement in resolved.Requirements)
        {
            var bytes = SHA256.HashData(requirement.Image.ToByteArray());
            var reference = new ImageReference(AssetId.NewId(), SHA256.HashData(bytes), ImageFormat.Png, byteLength, width ?? requirement.Window.Width, height ?? requirement.Window.Height);
            prepared[requirement] = new PreparedImage(reference, bytes);
        }

        return prepared;
    }

    private static ProfileDocument Blank() => PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Shared", Now);

    private static ProfileDocument ClassicPlate() =>
        PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "Adventure Plate", Now, new PlateStarterContent(new BasicCharacterInfo("Visible Hero", "Phoenix", "Light", 19, "Paladin", 100, "ABC")));

    private static PlateComponent ImageComponent()
    {
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitOverlayImage);
        component.AssetId = Guid.NewGuid();
        return component;
    }

    private static Dictionary<string, JsonElement> Canary(string where) => new() { ["canary" + where] = JsonSerializer.SerializeToElement(CanaryValue(where)) };

    private static string CanaryValue(string where) => "CanaryValue" + where;

    private static LayoutPoint Point(Vector2 point) =>
        new((int)Math.Round((double)point.X * 100d, MidpointRounding.AwayFromZero), (int)Math.Round((double)point.Y * 100d, MidpointRounding.AwayFromZero));

    private static LayoutColor Bytes(Vector4 color) =>
        new(PlateSnapshotBuilder.Channel(color.X), PlateSnapshotBuilder.Channel(color.Y), PlateSnapshotBuilder.Channel(color.Z), PlateSnapshotBuilder.Channel(color.W));

    /// <summary>Every public property of a layout object, by name, so two can be compared field for field.</summary>
    private static string Fingerprint(object value) =>
        value.GetType().Name + "{" + string.Join(
            ";",
            value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => p.Name + "=" + FieldText(p.GetValue(value)))) + "}";

    private static string FieldText(object? value) => value switch
    {
        ReadOnlyMemory<byte> memory => Convert.ToHexString(memory.Span),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index + needle.Length <= haystack.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Measurements with fixed answers, standing in for the renderer's.</summary>
    private sealed class Measurements : IPlateMeasurements
    {
        /// <summary>A text's natural width, or null for a font not yet built.</summary>
        internal Func<TextProfileElement, float?> Width { get; init; } = element => element.GetDisplayText().Length * element.FontSize * 0.5f;

        /// <summary>Whether the display can be decided, and the text drawn instead of the element's own.</summary>
        internal Func<TextProfileElement, (bool Ready, string? Display)> Display { get; init; } = _ => (true, null);

        internal Dictionary<Guid, (int Width, int Height)> Sizes { get; } = new();

        internal HashSet<Guid> Missing { get; } = new();

        public bool TryMeasureNaturalWidth(TextProfileElement element, out float width)
        {
            var measured = Width(element);
            width = measured ?? 0f;
            return measured is not null;
        }

        public bool TryGetDisplayOverride(ProfileDocument plate, TextProfileElement element, out string? display)
        {
            var (ready, shown) = Display(element);
            display = shown;
            return ready;
        }

        public bool TryGetImageSize(Guid image, out int width, out int height)
        {
            (width, height) = Missing.Contains(image) ? (0, 0) : Sizes.TryGetValue(image, out var size) ? size : (64, 32);
            return !Missing.Contains(image);
        }
    }

    /// <summary>An element of a kind no build knows, as a newer build's would be.</summary>
    private sealed class StrangeElement : ProfileElement
    {
        internal override ProfileElement Clone() => new StrangeElement();
    }
}
