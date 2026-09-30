using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
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
/// NETWORK2's snapshot builder (N2-6a): a saved Plate becomes a schema 2 layout that holds what the
/// local renderer draws, in its order, and nothing else, or the reasons it can't. Every value is
/// carried as the renderer resolves it, and nothing is ever clamped, trimmed or dropped to get past
/// a limit. Snapshots are signed here with ephemeral keys only.
/// </summary>
public sealed class PlateSnapshotBuilderTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
    private const long CreatedAt = 1_790_000_000;

    private readonly ProfileId profile = ProfileId.NewId();
    private readonly RevisionId revision = RevisionId.NewId();

    [Fact]
    public void AClassicPlate_BuildsASnapshot_ThatSurvivesSigningAndVerifying()
    {
        var plate = ClassicPlate();
        var resolved = PlateSnapshotBuilder.Resolve(plate);
        var snapshot = Build(resolved);

        using var signer = EcdsaPersonaSigner.CreateEphemeral();
        var verified = Assert.IsType<ProfileLayoutSnapshot>(SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(snapshot, signer)).Document);

        Assert.Equal(plate.Name, verified.Name);
        Assert.Equal(Fingerprint(snapshot.Background), Fingerprint(verified.Background));
        Assert.Equal(snapshot.Items.Select(Fingerprint), verified.Items.Select(Fingerprint));
        Assert.Equal(resolved.Steps.Count, verified.Items.Count);

        // What the Plate shows is what is shared: the character's name the starter filled in.
        Assert.Contains(verified.Items.OfType<LayoutText>(), text => text.Text == "Visible Hero");
    }

    [Fact]
    public void TheSnapshot_HoldsNoLocalIdentifier_AndNothingOfTheCharacterBeyondWhatIsDrawn()
    {
        var plate = ComponentDocuments.WithAnchors();
        plate.OwnerContentId = 0x1122334455667788UL;
        plate.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameDouble), ImageComponent()];
        var resolved = PlateSnapshotBuilder.Resolve(plate);
        var snapshot = Build(resolved);

        using var signer = EcdsaPersonaSigner.CreateEphemeral();
        var bytes = SignedDocumentCodec.Sign(snapshot, signer);

        var local = new List<Guid> { plate.ProfileId };
        local.AddRange(plate.Elements.Select(e => e.Id));
        local.AddRange(plate.Components!.Select(c => c.Id));
        local.AddRange(resolved.Images);
        foreach (var id in local)
        {
            Assert.False(Contains(bytes, id.ToByteArray()), $"local id {id}");
            Assert.False(Contains(bytes, id.ToByteArray(bigEndian: true)), $"local id {id}, big-endian");
        }

        Assert.False(Contains(bytes, BitConverter.GetBytes(plate.OwnerContentId)));
        Assert.False(Contains(bytes, BitConverter.GetBytes(plate.OwnerContentId).Reverse().ToArray()));
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

        var resolved = PlateSnapshotBuilder.Resolve(plate);

        var paintOrder = new List<ProfileElement>();
        var drawn = new List<ProfileElement>();
        var plan = new List<PaintStep>();
        ProfileVisualBounds.FillDrawnElements(plate, ProfileRenderOptions.Finished, paintOrder, drawn);
        ComponentPaintPlan.Build(plate, drawn, BuiltInComponentCatalog.Instance, plan);

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
            var resolved = PlateSnapshotBuilder.Resolve(plate);
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

                var expected = new[] { primitive.A, primitive.B, primitive.C, primitive.D }.Take(points.Length).Select(Point);
                Assert.Equal(expected, points);
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
            Position = new Vector2(10.125f, -20.5f),
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

        var item = Assert.IsType<LayoutText>(Assert.Single(Build(PlateSnapshotBuilder.Resolve(plate)).Items));
        Assert.Equal("* Hello +", item.Text);
        Assert.Equal(new LayoutPoint(1013, -2050), item.Position);
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
    public void TheFavoriteJobsDisplay_IsWhatIsShared_NotTheStoredText()
    {
        var plate = Blank();
        var jobs = new TextProfileElement { Role = ProfileElementRole.BasicJob, Text = "Paladin, Warrior, Dark Knight" };
        plate.Elements.Add(jobs);

        var resolved = PlateSnapshotBuilder.Resolve(plate, displayOverride: element => element.Role == ProfileElementRole.BasicJob ? "Paladin +2" : null);

        Assert.Equal("Paladin +2", Assert.IsType<LayoutText>(Assert.Single(Build(resolved).Items)).Text);
    }

    [Fact]
    public void HiddenElements_AndSectionHeadingsWithNothingUnderThem_AreNotShared()
    {
        var plate = ComponentDocuments.WithAnchors();
        plate.Elements.Single(e => e.Role == ProfileElementRole.BasicTitle).Visible = false;
        ((TextProfileElement)plate.Elements.Single(e => e.Role == ProfileElementRole.BasicWorld)).Text = string.Empty;

        var texts = Build(PlateSnapshotBuilder.Resolve(plate)).Items.OfType<LayoutText>().Select(t => t.Text).ToList();

        // The hidden title and the heading over an empty section are left out, as in the Profile
        // View; the emptied value itself is an element the renderer draws, as nothing.
        Assert.Equal(new[] { "Name", string.Empty, "Caption" }, texts);
        Assert.DoesNotContain("Title", texts);
        Assert.DoesNotContain("HOME WORLD", texts);
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
        var image = new ImageProfileElement { AssetId = Guid.NewGuid(), Position = new Vector2(0.125f, -0.125f), Size = new Vector2(0.375f, 100f), RotationDegrees = 725f };
        var turned = new ImageProfileElement { AssetId = image.AssetId, Position = Vector2.Zero, Size = Vector2.One, RotationDegrees = -360f, ZIndex = 1 };
        plate.Elements.Add(image);
        plate.Elements.Add(turned);

        var items = Build(PlateSnapshotBuilder.Resolve(plate)).Items.Cast<LayoutImage>().ToList();

        Assert.Equal(new LayoutPoint(13, -13), items[0].Position);
        Assert.Equal(38, items[0].Width);
        Assert.Equal(500, items[0].Rotation);
        Assert.Equal(0, items[1].Rotation);
    }

    [Fact]
    public void AnImageDrawnTwice_IsNamedOnce_UnderOneFreshAssetId()
    {
        var plate = Blank();
        var local = Guid.NewGuid();
        plate.Elements.Add(new ImageProfileElement { AssetId = local, ZIndex = 0 });
        plate.Elements.Add(new ImageProfileElement { AssetId = local, ZIndex = 1 });
        var resolved = PlateSnapshotBuilder.Resolve(plate);
        var prepared = Prepared(resolved);

        var snapshot = Build(resolved, prepared);

        var image = Assert.Single(snapshot.Images);
        Assert.Equal(prepared[local].AssetId, image.AssetId);
        Assert.All(snapshot.Items.Cast<LayoutImage>(), item => Assert.Equal(image.AssetId, item.AssetId));
    }

    [Fact]
    public void TheBackground_IsSharedInEveryMode_WithItsImagePrepared_AndItsPatternAsDrawn()
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
                ImageAssetId = mode == ProfileBackgroundMode.Image ? Guid.NewGuid() : null,
                ImageFit = ProfileImageFit.Fit,
                ImageFlipY = true,
            };

            var resolved = PlateSnapshotBuilder.Resolve(plate);
            var prepared = Prepared(resolved);
            var background = Build(resolved, prepared).Background;

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
            Assert.Equal(mode == ProfileBackgroundMode.Image ? prepared.Values.Single().AssetId : default, background.ImageAssetId);
        }
    }

    [Fact]
    public void AComponentImageThatWasntPrepared_IsLeftOut_AsTheRendererDrawsNothingThere()
    {
        var plate = ComponentDocuments.WithAnchors();
        plate.Components = [ImageComponent()];
        var resolved = PlateSnapshotBuilder.Resolve(plate);
        var portrait = plate.Elements.OfType<ImageProfileElement>().Single().AssetId;

        var snapshot = Build(resolved, Prepared(resolved, except: plate.Components[0].AssetId!.Value));

        Assert.DoesNotContain(snapshot.Items, item => item is LayoutImageQuad);
        Assert.Single(snapshot.Images);
        Assert.Contains(snapshot.Items, item => item is LayoutImage);
        Assert.NotEqual(Guid.Empty, portrait);
    }

    [Fact]
    public void ANameTheNameRuleRefuses_IsRefused_AskingForARename()
    {
        foreach (var name in new[] { string.Empty, "Line" + (char)0x2028 + "break", new string('a', ProtocolLimits.MaxNameScalars + 1) })
        {
            var plate = Blank();
            plate.Name = name;
            Assert.Equal(PlateSnapshotRefusal.Name, Assert.Single(Refusals(PlateSnapshotBuilder.Resolve(plate))).Refusal);
        }
    }

    [Fact]
    public void MoreItemsThanAPlateCanShare_AreRefused_NeverTrimmed()
    {
        var steps = Enumerable.Repeat<ResolvedStep>(new ResolvedShape(new ComponentPrimitive(ComponentPrimitiveKind.Triangle, Vector2.Zero, Vector2.One, new Vector2(0f, 1f), Vector2.Zero, Vector4.One), Guid.Empty, null), ProtocolLimits.MaxLayoutItems + 1).ToList();
        var resolved = new ResolvedPlate("Many", 1280f, 720f, null, steps, []);

        Assert.Equal(PlateSnapshotRefusal.TooManyItems, Assert.Single(Refusals(resolved)).Refusal);
        Assert.NotNull(Build(new ResolvedPlate("Many", 1280f, 720f, null, steps.Take(ProtocolLimits.MaxLayoutItems).ToList(), [])));
    }

    [Fact]
    public void MoreImagesOrImagePixelsThanAPlateCanShare_AreRefused_NeverDropped()
    {
        var plate = Blank();
        for (var index = 0; index <= ProtocolLimits.MaxImagesPerProfile; index++)
        {
            plate.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = index });
        }

        Assert.Contains(Refusals(PlateSnapshotBuilder.Resolve(plate)), p => p.Refusal == PlateSnapshotRefusal.TooManyImages);

        var pixels = Blank();
        pixels.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = 0 });
        pixels.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = 1 });
        var resolved = PlateSnapshotBuilder.Resolve(pixels);

        // Two images of 8,192 by 2,049 hold 33,570,816 pixels, 16,384 over the limit; by 2,048, exactly at it.
        Assert.Equal(PlateSnapshotRefusal.TooManyImagePixels, Assert.Single(Refusals(resolved, Prepared(resolved, width: 8192, height: 2049))).Refusal);
        Assert.NotNull(Build(resolved, Prepared(resolved, width: 8192, height: 2048)));
    }

    [Fact]
    public void MoreTextThanAPlateCanShare_OrATextNoSharedTextCanHold_IsRefused()
    {
        var plate = Blank();
        for (var index = 0; index < 17; index++)
        {
            plate.Elements.Add(new TextProfileElement { Text = new string('a', TextProfileElement.MaxTextLength), ZIndex = index });
        }

        Assert.Equal(PlateSnapshotRefusal.TooMuchText, Assert.Single(Refusals(PlateSnapshotBuilder.Resolve(plate))).Refusal);

        foreach (var unshareable in new[] { "a" + (char)0 + "b", "a" + (char)0xD800, (char)0xDC00 + "a" })
        {
            var holding = Blank();
            var text = new TextProfileElement { Text = unshareable };
            holding.Elements.Add(text);
            Assert.Equal(new PlateSnapshotProblem(PlateSnapshotRefusal.TextUnshareable, text), Assert.Single(Refusals(PlateSnapshotBuilder.Resolve(holding))));
        }

        var jobs = Blank();
        jobs.Elements.Add(new TextProfileElement { Role = ProfileElementRole.BasicJob, Text = "Paladin" });
        var tooLong = new string('a', ProtocolLimits.MaxLayoutItemTextScalars + 1);
        Assert.Equal(PlateSnapshotRefusal.TextTooLong, Assert.Single(Refusals(PlateSnapshotBuilder.Resolve(jobs, displayOverride: _ => tooLong))).Refusal);
    }

    [Fact]
    public void AGradientColourOutsideZeroToOne_IsRefused_SinceTheRendererBlendsBeforeItResolves()
    {
        var plate = Blank();
        plate.Background = new ProfileBackground { Mode = ProfileBackgroundMode.LinearGradient, PrimaryColor = new Vector4(1.2f, 0f, 0f, 1f) };
        Assert.Equal(PlateSnapshotRefusal.GradientColor, Assert.Single(Refusals(PlateSnapshotBuilder.Resolve(plate))).Refusal);

        // Outside a gradient, the colour is carried as drawn.
        plate.Background.Mode = ProfileBackgroundMode.SolidColor;
        Assert.Equal(new LayoutColor(255, 0, 0, 255), Build(PlateSnapshotBuilder.Resolve(plate)).Background.Primary);
    }

    [Fact]
    public void AValueNoFieldCanExpress_UnknownElements_AndMissingImages_AreRefused_NamingTheElement()
    {
        var plate = Blank();
        var far = new TextProfileElement { Text = "Far", Position = new Vector2(20_000_000f, 0f) };
        var strange = new StrangeElement { ZIndex = 1 };
        var missing = new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = 2 };
        plate.Elements.Add(far);
        plate.Elements.Add(strange);
        plate.Elements.Add(missing);
        plate.Background = new ProfileBackground { Mode = ProfileBackgroundMode.Image, ImageAssetId = null };
        plate.Name = string.Empty;

        var problems = Refusals(PlateSnapshotBuilder.Resolve(plate), new Dictionary<Guid, ImageReference>());

        Assert.Equal(
            new[]
            {
                new PlateSnapshotProblem(PlateSnapshotRefusal.Name, null),
                new PlateSnapshotProblem(PlateSnapshotRefusal.BackgroundImageMissing, null),
                new PlateSnapshotProblem(PlateSnapshotRefusal.ValueOutOfRange, far),
                new PlateSnapshotProblem(PlateSnapshotRefusal.UnknownElement, strange),
                new PlateSnapshotProblem(PlateSnapshotRefusal.ImageMissing, missing),
            },
            problems);
    }

    private ProfileLayoutSnapshot Build(ResolvedPlate resolved, IReadOnlyDictionary<Guid, ImageReference>? prepared = null)
    {
        var result = PlateSnapshotBuilder.Map(resolved, profile, revision, CreatedAt, prepared ?? Prepared(resolved));
        Assert.Empty(result.Problems);
        return result.Snapshot!;
    }

    private IReadOnlyList<PlateSnapshotProblem> Refusals(ResolvedPlate resolved, IReadOnlyDictionary<Guid, ImageReference>? prepared = null)
    {
        var result = PlateSnapshotBuilder.Map(resolved, profile, revision, CreatedAt, prepared ?? Prepared(resolved));
        Assert.Null(result.Snapshot);
        Assert.NotEmpty(result.Problems);
        return result.Problems;
    }

    /// <summary>A prepared copy for every managed image the Plate draws, as N2-6b declares one: a fresh asset id, a digest and a size.</summary>
    private static Dictionary<Guid, ImageReference> Prepared(ResolvedPlate resolved, Guid? except = null, int width = 64, int height = 32)
    {
        var prepared = new Dictionary<Guid, ImageReference>();
        foreach (var local in resolved.Images.Where(image => image != except))
        {
            prepared[local] = new ImageReference(AssetId.NewId(), System.Security.Cryptography.SHA256.HashData(local.ToByteArray()), ImageFormat.Png, 1_000, width, height);
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
                .Select(p => p.Name + "=" + Convert.ToString(p.GetValue(value), CultureInfo.InvariantCulture))) + "}";

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

    /// <summary>An element of a kind no build knows, as a newer build's would be.</summary>
    private sealed class StrangeElement : ProfileElement
    {
        internal override ProfileElement Clone() => new StrangeElement();
    }
}
