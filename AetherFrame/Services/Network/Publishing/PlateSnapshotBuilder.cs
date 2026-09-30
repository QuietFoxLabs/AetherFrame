using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Fonts;
using AetherFrame.UI.Rendering;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>One step of what the local renderer draws for a saved Plate, in paint order.</summary>
internal abstract record ResolvedStep;

/// <summary>A text element, with the text the renderer draws for it (its affixes, or the Favorite Jobs display).</summary>
internal sealed record ResolvedText(TextProfileElement Element, string Text) : ResolvedStep;

/// <summary>An image element.</summary>
internal sealed record ResolvedImage(ImageProfileElement Element) : ResolvedStep;

/// <summary>
/// One primitive of a component placement: a filled quad or triangle, the component's image
/// (<paramref name="Image"/>), or its bundled artwork (<paramref name="Art"/>, the artwork's id).
/// </summary>
internal sealed record ResolvedShape(ComponentPrimitive Primitive, Guid Image, string? Art) : ResolvedStep;

/// <summary>An element of a kind this build doesn't know, which the renderer draws as a placeholder.</summary>
internal sealed record ResolvedUnknown(ProfileElement Element) : ResolvedStep;

/// <summary>
/// A saved Plate as the local renderer draws it: its name, canvas, background, the drawn steps in
/// paint order, and the managed images those draw, each once, in the order first drawn.
/// </summary>
internal sealed class ResolvedPlate
{
    internal ResolvedPlate(string name, float canvasWidth, float canvasHeight, ProfileBackground? background, IReadOnlyList<ResolvedStep> steps, IReadOnlyList<Guid> images)
    {
        Name = name;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Background = background;
        Steps = steps;
        Images = images;
    }

    internal string Name { get; }

    internal float CanvasWidth { get; }

    internal float CanvasHeight { get; }

    internal ProfileBackground? Background { get; }

    internal IReadOnlyList<ResolvedStep> Steps { get; }

    /// <summary>The managed images the Plate draws (the background's, its image elements' and its image components'), each once.</summary>
    internal IReadOnlyList<Guid> Images { get; }
}

/// <summary>Why a saved Plate can't be shared as it is. Each is fixed by changing the Plate: nothing is ever clamped, trimmed or dropped to get past one.</summary>
internal enum PlateSnapshotRefusal
{
    /// <summary>The Plate's name breaks the shared-name rule (decision D4).</summary>
    Name,

    /// <summary>More items than a shared Plate can hold.</summary>
    TooManyItems,

    /// <summary>More images than a shared Plate can hold.</summary>
    TooManyImages,

    /// <summary>More image pixels in all than a shared Plate can hold.</summary>
    TooManyImagePixels,

    /// <summary>More text in all than a shared Plate can hold.</summary>
    TooMuchText,

    /// <summary>One text is longer than a shared text can be.</summary>
    TextTooLong,

    /// <summary>A text holds U+0000 or a broken surrogate, which no shared text can.</summary>
    TextUnshareable,

    /// <summary>A background gradient has a colour component outside 0 to 1, which the renderer blends before it resolves.</summary>
    GradientColor,

    /// <summary>A value is outside what a shared Plate can express.</summary>
    ValueOutOfRange,

    /// <summary>An element of a kind this build doesn't know.</summary>
    UnknownElement,

    /// <summary>An image element's image is missing.</summary>
    ImageMissing,

    /// <summary>The background's image is missing.</summary>
    BackgroundImageMissing,
}

/// <summary>One reason a Plate can't be shared, and the element it is about, when there is one.</summary>
internal readonly record struct PlateSnapshotProblem(PlateSnapshotRefusal Refusal, ProfileElement? Element);

/// <summary>A layout snapshot built from a saved Plate, or the reasons it can't be built. Exactly one of the two.</summary>
internal sealed class PlateSnapshotResult
{
    private PlateSnapshotResult(ProfileLayoutSnapshot? snapshot, IReadOnlyList<PlateSnapshotProblem> problems)
    {
        Snapshot = snapshot;
        Problems = problems;
    }

    /// <summary>The snapshot, unsigned; null when <see cref="Problems"/> holds anything.</summary>
    internal ProfileLayoutSnapshot? Snapshot { get; }

    /// <summary>Every reason the Plate can't be shared as it is; empty when <see cref="Snapshot"/> is built.</summary>
    internal IReadOnlyList<PlateSnapshotProblem> Problems { get; }

    internal static PlateSnapshotResult Built(ProfileLayoutSnapshot snapshot) => new(snapshot, Array.Empty<PlateSnapshotProblem>());

    internal static PlateSnapshotResult Refused(IReadOnlyList<PlateSnapshotProblem> problems) => new(null, problems);
}

/// <summary>
/// Turns a saved Plate into a schema 2 layout snapshot (NETWORK2's N2-6a;
/// docs/networking/ProtocolSpecification-v1.md, section 8.5): what the local renderer draws, in
/// its order, and nothing else. In two steps, since measuring text needs the game's fonts:
/// <list type="number">
/// <item><see cref="Resolve"/>, on the framework thread: the elements <c>ProfileRenderer</c> draws,
/// through the same <c>ProfileVisualBounds.FillDrawnElements</c>, <c>ComponentPaintPlan.Build</c>
/// and <c>ComponentGeometry.Build</c>, with the renderer's own text measurement and Favorite Jobs
/// display passed in;</item>
/// <item><see cref="Map"/>, on any thread: the resolved Plate and its prepared images become the
/// snapshot's layout, or the reasons they can't (<see cref="PlateSnapshotRefusal"/>).</item>
/// </list>
/// Every value is carried as the renderer resolves it; a value no field can express, or a Plate
/// over a whole-snapshot limit, is refused, never clamped, trimmed or dropped. Compiled only in the
/// networking preview flavour, and free of Dalamud and ImGui.
/// </summary>
internal static class PlateSnapshotBuilder
{
    /// <summary>
    /// What the local renderer draws for <paramref name="plate"/>, in its order, with the finished
    /// rendering's options: hidden elements and empty section headings are left out, and components
    /// the plan can't resolve draw nothing. <paramref name="measureText"/> is the renderer's
    /// measurement of a text's natural width, which a Name Backing follows, and
    /// <paramref name="displayOverride"/> gives the text drawn instead of an element's own (the
    /// Favorite Jobs display); both are the renderer's own in the plugin, and either may be null.
    /// </summary>
    internal static ResolvedPlate Resolve(
        ProfileDocument plate, Func<TextProfileElement, float?>? measureText = null, Func<TextProfileElement, string?>? displayOverride = null)
    {
        ArgumentNullException.ThrowIfNull(plate);

        var paintOrder = new List<ProfileElement>();
        var drawn = new List<ProfileElement>();
        var plan = new List<PaintStep>();
        ProfileVisualBounds.FillDrawnElements(plate, ProfileRenderOptions.Finished, paintOrder, drawn);
        ComponentPaintPlan.Build(plate, drawn, BuiltInComponentCatalog.Instance, plan, measureText);

        var steps = new List<ResolvedStep>(plan.Count);
        var images = new List<Guid>();
        var background = plate.Background;
        if (background is { Mode: ProfileBackgroundMode.Image, ImageAssetId: { } backgroundImage })
        {
            AddOnce(images, backgroundImage);
        }

        var primitives = new List<ComponentPrimitive>();
        foreach (var step in plan)
        {
            switch (step.Element)
            {
                case TextProfileElement text:
                    steps.Add(new ResolvedText(text, displayOverride?.Invoke(text) ?? text.GetDisplayText()));
                    continue;
                case ImageProfileElement image:
                    steps.Add(new ResolvedImage(image));
                    AddOnce(images, image.AssetId);
                    continue;
                case { } other:
                    steps.Add(new ResolvedUnknown(other));
                    continue;
            }

            if (step.Component is not { } component || step.Definition is not { } definition)
            {
                continue;
            }

            primitives.Clear();
            ComponentGeometry.Build(plate, component, definition, step.Placement, primitives);
            foreach (var primitive in primitives)
            {
                switch (primitive.Kind)
                {
                    case ComponentPrimitiveKind.Image:
                        // As the renderer: a component's image quad draws nothing without its image.
                        if (component.AssetId is { } asset && asset != Guid.Empty)
                        {
                            steps.Add(new ResolvedShape(primitive, asset, null));
                            AddOnce(images, asset);
                        }

                        break;

                    case ComponentPrimitiveKind.Art:
                        if (definition.Art is { } art)
                        {
                            steps.Add(new ResolvedShape(primitive, Guid.Empty, art.Id));
                        }

                        break;

                    default:
                        steps.Add(new ResolvedShape(primitive, Guid.Empty, null));
                        break;
                }
            }
        }

        return new ResolvedPlate(plate.Name, plate.CanvasWidth, plate.CanvasHeight, background, steps, images);
    }

    /// <summary>
    /// The layout snapshot of <paramref name="plate"/>, with <paramref name="prepared"/> declaring
    /// each managed image's prepared copy (its fresh asset id, decision N1, and its digest and size,
    /// decision D5), or every reason it can't be built. A component image that wasn't prepared is
    /// left out, as the renderer draws nothing for it; an image element or a background image that
    /// wasn't is refused, as the renderer draws a placeholder there.
    /// </summary>
    internal static PlateSnapshotResult Map(
        ResolvedPlate plate, ProfileId profileId, RevisionId revisionId, long createdAtUnixSeconds, IReadOnlyDictionary<Guid, ImageReference> prepared)
    {
        ArgumentNullException.ThrowIfNull(plate);
        ArgumentNullException.ThrowIfNull(prepared);

        var mapper = new Mapper(prepared);
        if (!ProfileLayoutSnapshot.IsValidName(plate.Name))
        {
            mapper.Refuse(PlateSnapshotRefusal.Name);
        }

        var canvasWidth = mapper.Hundredths(plate.CanvasWidth, ProtocolLimits.MinLayoutCanvasExtent, ProtocolLimits.MaxLayoutCanvasExtent);
        var canvasHeight = mapper.Hundredths(plate.CanvasHeight, ProtocolLimits.MinLayoutCanvasExtent, ProtocolLimits.MaxLayoutCanvasExtent);
        var background = mapper.Background(plate.Background);

        var items = new List<LayoutItem>(plate.Steps.Count);
        foreach (var step in plate.Steps)
        {
            if (mapper.Item(step) is { } item)
            {
                items.Add(item);
            }
        }

        mapper.CheckWhole(items.Count);
        if (mapper.Problems.Count > 0)
        {
            return PlateSnapshotResult.Refused(mapper.Problems);
        }

        try
        {
            return PlateSnapshotResult.Built(new ProfileLayoutSnapshot(profileId, revisionId, createdAtUnixSeconds, plate.Name, canvasWidth, canvasHeight, background!, items, mapper.UsedImages));
        }
        catch (ProtocolException)
        {
            // Every rule is checked above with its own words; this is the protocol's own check of
            // the same rules, and anything it still refuses is a value no field can express.
            return PlateSnapshotResult.Refused([new PlateSnapshotProblem(PlateSnapshotRefusal.ValueOutOfRange, null)]);
        }
    }

    /// <summary>
    /// A colour channel as ImGui turns it into a byte when the renderer draws it (clamped to 0 to 1,
    /// times 255, plus a half, truncated), so a component outside 0 to 1 is carried as drawn.
    /// </summary>
    internal static byte Channel(float value) => (byte)(int)((value < 0f ? 0f : value > 1f ? 1f : value) * 255f + 0.5f);

    private static void AddOnce(List<Guid> images, Guid image)
    {
        if (image != Guid.Empty && !images.Contains(image))
        {
            images.Add(image);
        }
    }

    /// <summary>One mapping: the problems found so far, and the prepared images the items name, each once.</summary>
    private sealed class Mapper
    {
        private readonly IReadOnlyDictionary<Guid, ImageReference> prepared;
        private readonly List<PlateSnapshotProblem> problems = new();
        private readonly List<ImageReference> used = new();
        private readonly HashSet<Guid> usedIds = new();
        private ProfileElement? element;
        private int textScalars;

        internal Mapper(IReadOnlyDictionary<Guid, ImageReference> prepared)
        {
            this.prepared = prepared;
        }

        internal List<PlateSnapshotProblem> Problems => problems;

        internal IReadOnlyList<ImageReference> UsedImages => used;

        internal void Refuse(PlateSnapshotRefusal refusal)
        {
            var problem = new PlateSnapshotProblem(refusal, element);
            if (!problems.Contains(problem))
            {
                problems.Add(problem);
            }
        }

        /// <summary>The whole-snapshot limits of section 8.5, in its order, each with its own words.</summary>
        internal void CheckWhole(int itemCount)
        {
            element = null;
            if (itemCount > ProtocolLimits.MaxLayoutItems)
            {
                Refuse(PlateSnapshotRefusal.TooManyItems);
            }

            if (used.Count > ProtocolLimits.MaxImagesPerProfile)
            {
                Refuse(PlateSnapshotRefusal.TooManyImages);
            }

            long pixels = 0;
            foreach (var image in used)
            {
                pixels += (long)image.Width * image.Height;
            }

            if (pixels > ProtocolLimits.MaxLayoutImagePixels)
            {
                Refuse(PlateSnapshotRefusal.TooManyImagePixels);
            }

            if (textScalars > ProtocolLimits.MaxLayoutTextScalars)
            {
                Refuse(PlateSnapshotRefusal.TooMuchText);
            }
        }

        internal LayoutBackground? Background(ProfileBackground? background)
        {
            element = null;
            if (background is null)
            {
                return LayoutBackground.None;
            }

            var mode = background.Mode switch
            {
                ProfileBackgroundMode.None => LayoutBackgroundMode.None,
                ProfileBackgroundMode.SolidColor => LayoutBackgroundMode.SolidColor,
                ProfileBackgroundMode.LinearGradient => LayoutBackgroundMode.LinearGradient,
                ProfileBackgroundMode.TexturedFill => LayoutBackgroundMode.TexturedFill,
                ProfileBackgroundMode.Image => LayoutBackgroundMode.Image,
                _ => Unexpressible(LayoutBackgroundMode.None),
            };

            // The renderer blends a gradient's endpoints before it resolves their colours, so a
            // component outside 0 to 1 draws a gradient no pair of colours in range can.
            if (mode == LayoutBackgroundMode.LinearGradient && (OutOfUnit(background.PrimaryColor) || OutOfUnit(background.SecondaryColor)))
            {
                Refuse(PlateSnapshotRefusal.GradientColor);
            }

            var image = default(AssetId);
            if (mode == LayoutBackgroundMode.Image)
            {
                if (background.ImageAssetId is { } local && Use(local) is { } asset)
                {
                    image = asset;
                }
                else
                {
                    Refuse(PlateSnapshotRefusal.BackgroundImageMissing);
                }
            }

            var texture = background.Texture switch
            {
                ProfileBackgroundTexture.None => LayoutTexture.None,
                ProfileBackgroundTexture.FineNoise => LayoutTexture.FineNoise,
                ProfileBackgroundTexture.Dots => LayoutTexture.Dots,
                ProfileBackgroundTexture.Grid => LayoutTexture.Grid,
                ProfileBackgroundTexture.DiagonalLines => LayoutTexture.DiagonalLines,
                ProfileBackgroundTexture.Crosshatch => LayoutTexture.Crosshatch,
                ProfileBackgroundTexture.SubtlePaper => LayoutTexture.SubtlePaper,
                ProfileBackgroundTexture.Checkerboard => LayoutTexture.Checkerboard,
                ProfileBackgroundTexture.Stripes => LayoutTexture.Stripes,
                ProfileBackgroundTexture.Waves => LayoutTexture.Waves,
                ProfileBackgroundTexture.Herringbone => LayoutTexture.Herringbone,
                ProfileBackgroundTexture.Honeycomb => LayoutTexture.Honeycomb,
                ProfileBackgroundTexture.Scales => LayoutTexture.Scales,
                ProfileBackgroundTexture.Speckle => LayoutTexture.Speckle,
                ProfileBackgroundTexture.Diamonds => LayoutTexture.Diamonds,
                ProfileBackgroundTexture.Chevron => LayoutTexture.Chevron,
                ProfileBackgroundTexture.Sparkle => LayoutTexture.Sparkle,
                ProfileBackgroundTexture.Linen => LayoutTexture.Linen,
                ProfileBackgroundTexture.Ripples => LayoutTexture.Ripples,
                ProfileBackgroundTexture.Quatrefoil => LayoutTexture.Quatrefoil,
                ProfileBackgroundTexture.Brick => LayoutTexture.Brick,

                // The renderer has no tile for a pattern it doesn't know, and draws none.
                _ => LayoutTexture.None,
            };

            // The renderer draws the pattern at its scale clamped to the pattern's range.
            var scale = Math.Clamp(background.TextureScale, ProfileBackground.MinTextureScale, ProfileBackground.MaxTextureScale);
            var fields = (
                Primary: Color(background.PrimaryColor),
                Secondary: Color(background.SecondaryColor),
                GradientAngle: Angle(background.GradientAngle),
                Opacity: Unit(background.Opacity),
                Intensity: Unit(background.TextureIntensity),
                Scale: Hundredths(scale, LayoutBackground.MinTextureScale, LayoutBackground.MaxTextureScale),
                Rotation: Angle(background.TextureRotation),
                Fit: Fit(background.ImageFit));
            if (problems.Count > 0)
            {
                return null;
            }

            return new LayoutBackground(
                mode, fields.Primary, fields.Secondary, fields.GradientAngle, fields.Opacity, texture, fields.Intensity,
                fields.Scale, fields.Rotation, image, fields.Fit, Flips(background.ImageFlipX, background.ImageFlipY));
        }

        internal LayoutItem? Item(ResolvedStep step)
        {
            element = step switch
            {
                ResolvedText text => text.Element,
                ResolvedImage image => image.Element,
                ResolvedUnknown unknown => unknown.Element,
                _ => null,
            };

            // An item with a problem is never built: its constructor would only refuse the same
            // value again, and the snapshot is refused anyway.
            var before = problems.Count;
            var item = step switch
            {
                ResolvedText text => Text(text, before),
                ResolvedImage image => Image(image.Element, before),
                ResolvedShape shape => Shape(shape, before),
                _ => Unknown(),
            };

            return problems.Count == before ? item : null;
        }

        private LayoutItem? Unknown()
        {
            Refuse(PlateSnapshotRefusal.UnknownElement);
            return null;
        }

        private LayoutItem? Text(ResolvedText resolved, int before)
        {
            var text = resolved.Element;
            var scalars = Scalars(resolved.Text);
            if (scalars < 0)
            {
                Refuse(PlateSnapshotRefusal.TextUnshareable);
                return null;
            }

            if (scalars > ProtocolLimits.MaxLayoutItemTextScalars)
            {
                Refuse(PlateSnapshotRefusal.TextTooLong);
                return null;
            }

            textScalars += scalars;
            var flags = LayoutTextFlags.None;
            flags |= text.Wrap ? LayoutTextFlags.Wrap : LayoutTextFlags.None;
            flags |= text.Bold ? LayoutTextFlags.Bold : LayoutTextFlags.None;
            flags |= text.Italic ? LayoutTextFlags.Italic : LayoutTextFlags.None;
            flags |= text.Underline ? LayoutTextFlags.Underline : LayoutTextFlags.None;
            flags |= text.Strikethrough ? LayoutTextFlags.Strikethrough : LayoutTextFlags.None;
            flags |= text.AutoFitText ? LayoutTextFlags.AutoFit : LayoutTextFlags.None;
            flags |= text.OutlineEnabled ? LayoutTextFlags.Outline : LayoutTextFlags.None;
            flags |= text.ShadowEnabled ? LayoutTextFlags.Shadow : LayoutTextFlags.None;

            var position = Point(text.Position);
            var width = Hundredths(text.Size.X, 0, ProtocolLimits.MaxLayoutExtent);
            var height = Hundredths(text.Size.Y, 0, ProtocolLimits.MaxLayoutExtent);
            var fontSize = Hundredths(text.FontSize, LayoutText.MinFontSize, LayoutText.MaxFontSize);
            var letterSpacing = Hundredths(text.LetterSpacing, -LayoutText.MaxLetterSpacing, LayoutText.MaxLetterSpacing);
            var lineSpacing = Hundredths(text.LineSpacing, -LayoutText.MaxLineSpacing, LayoutText.MaxLineSpacing);

            // The renderer shrinks an auto-fit text to no less than one canvas unit.
            var autoFitMinimum = Hundredths(Math.Max(1f, text.AutoFitMinimumSize), LayoutText.MinFontSize, LayoutText.MaxFontSize);

            // The renderer draws an outline at most MaxOutlineThickness thick, and a thickness above 0
            // is never carried as 0.
            var outlineThickness = OutlineThickness(Math.Min(text.OutlineThickness, TextProfileElement.MaxOutlineThickness));
            var shadowX = Hundredths(text.ShadowOffsetX, -LayoutText.MaxShadowOffset, LayoutText.MaxShadowOffset);
            var shadowY = Hundredths(text.ShadowOffsetY, -LayoutText.MaxShadowOffset, LayoutText.MaxShadowOffset);

            // The renderer draws each effect in its colour's red, green and blue at the text's alpha
            // times the effect's opacity, ignoring the colour's own alpha (ProfileTextRenderer's
            // DrawPassGroup); a viewer multiplies the carried alpha by the text's, so it is the opacity.
            var outlineColor = Color(text.OutlineColor with { W = Math.Clamp(text.OutlineOpacity, 0f, 1f) });
            var shadowColor = Color(text.ShadowColor with { W = Math.Clamp(text.ShadowOpacity, 0f, 1f) });
            var align = text.Alignment switch
            {
                TextAlignment.Left => LayoutHorizontalAlign.Left,
                TextAlignment.Center => LayoutHorizontalAlign.Center,
                TextAlignment.Right => LayoutHorizontalAlign.Right,
                _ => Unexpressible(LayoutHorizontalAlign.Left),
            };
            var verticalAlign = text.VerticalAlignment switch
            {
                TextVerticalAlignment.Top => LayoutVerticalAlign.Top,
                TextVerticalAlignment.Middle => LayoutVerticalAlign.Middle,
                TextVerticalAlignment.Bottom => LayoutVerticalAlign.Bottom,
                _ => Unexpressible(LayoutVerticalAlign.Top),
            };

            var color = Color(text.Color);
            if (problems.Count != before)
            {
                return null;
            }

            return new LayoutText(
                position, width, height, resolved.Text, ProfileFontCatalog.Resolve(text.FontFamily).Id, fontSize, color, align, verticalAlign, flags,
                letterSpacing, lineSpacing, autoFitMinimum, outlineColor, outlineThickness, shadowColor, shadowX, shadowY,
                text.UsesLegacyLayout ? LayoutTextLayout.Legacy : LayoutTextLayout.Current);
        }

        private LayoutItem? Image(ImageProfileElement image, int before)
        {
            if (Use(image.AssetId) is not { } asset)
            {
                Refuse(PlateSnapshotRefusal.ImageMissing);
                return null;
            }

            var position = Point(image.Position);
            var width = Hundredths(image.Size.X, 0, ProtocolLimits.MaxLayoutExtent);
            var height = Hundredths(image.Size.Y, 0, ProtocolLimits.MaxLayoutExtent);
            var rotation = Angle(image.RotationDegrees);
            var fit = Fit(image.DisplayMode);
            var opacity = Unit(image.Opacity);
            return problems.Count != before ? null : new LayoutImage(asset, position, width, height, rotation, fit, Flips(image.FlipX, image.FlipY), opacity);
        }

        private LayoutItem? Shape(ResolvedShape shape, int before)
        {
            var primitive = shape.Primitive;
            var color = Color(primitive.Color);
            var (a, b, c, d) = (Point(primitive.A), Point(primitive.B), Point(primitive.C), Point(primitive.D));
            if (problems.Count != before)
            {
                return null;
            }

            switch (primitive.Kind)
            {
                case ComponentPrimitiveKind.Quad:
                    return new LayoutQuad(a, b, c, d, color);

                case ComponentPrimitiveKind.Triangle:
                    return new LayoutTriangle(a, b, c, color);

                case ComponentPrimitiveKind.Image:
                    // A component's image that wasn't prepared draws nothing, as in the renderer.
                    return Use(shape.Image) is { } asset ? new LayoutImageQuad(asset, a, b, c, d, color) : null;

                case ComponentPrimitiveKind.Art when shape.Art is { } art:
                    return new LayoutArtQuad(art, a, b, c, d, color);

                default:
                    return null;
            }
        }

        /// <summary>The prepared copy of a managed image, named in the snapshot once however often it is drawn; null when it wasn't prepared.</summary>
        private AssetId? Use(Guid local)
        {
            if (local == Guid.Empty || !prepared.TryGetValue(local, out var reference))
            {
                return null;
            }

            if (usedIds.Add(local))
            {
                used.Add(reference);
            }

            return reference.AssetId;
        }

        private LayoutPoint Point(Vector2 point) =>
            new(Hundredths(point.X, -ProtocolLimits.MaxLayoutCoordinate, ProtocolLimits.MaxLayoutCoordinate), Hundredths(point.Y, -ProtocolLimits.MaxLayoutCoordinate, ProtocolLimits.MaxLayoutCoordinate));

        /// <summary>An angle in hundredths of a degree, normalized into one turn first.</summary>
        private int Angle(float degrees) => float.IsFinite(degrees)
            ? Hundredths(degrees % 360f, -ProtocolLimits.MaxLayoutAngle, ProtocolLimits.MaxLayoutAngle)
            : Unexpressible(0);

        private int OutlineThickness(float thickness)
        {
            var hundredths = Hundredths(thickness, int.MinValue, LayoutText.MaxOutlineThickness);
            return thickness > 0f && hundredths <= 0 ? 1 : Math.Max(0, hundredths);
        }

        /// <summary>A canvas value in hundredths, rounded to the nearest (halves away from zero), or a problem when outside [<paramref name="min"/>, <paramref name="max"/>].</summary>
        internal int Hundredths(float value, int min, int max)
        {
            if (!float.IsFinite(value))
            {
                return Unexpressible(min);
            }

            var hundredths = Math.Round((double)value * 100d, MidpointRounding.AwayFromZero);
            return hundredths < min || hundredths > max ? Unexpressible(min) : (int)hundredths;
        }

        private LayoutColor Color(Vector4 color) =>
            float.IsFinite(color.X) && float.IsFinite(color.Y) && float.IsFinite(color.Z) && float.IsFinite(color.W)
                ? new LayoutColor(Channel(color.X), Channel(color.Y), Channel(color.Z), Channel(color.W))
                : Unexpressible(default(LayoutColor));

        /// <summary>A 0 to 1 opacity as the renderer turns it into a byte.</summary>
        private byte Unit(float value) => float.IsFinite(value) ? Channel(Math.Clamp(value, 0f, 1f)) : Unexpressible((byte)0);

        private LayoutImageFit Fit(ProfileImageFit fit) => fit switch
        {
            ProfileImageFit.Stretch => LayoutImageFit.Stretch,
            ProfileImageFit.Fit => LayoutImageFit.Fit,
            ProfileImageFit.Fill => LayoutImageFit.Fill,
            _ => Unexpressible(LayoutImageFit.Stretch),
        };

        private static LayoutFlips Flips(bool horizontal, bool vertical) =>
            (horizontal ? LayoutFlips.Horizontal : LayoutFlips.None) | (vertical ? LayoutFlips.Vertical : LayoutFlips.None);

        private T Unexpressible<T>(T placeholder)
        {
            Refuse(PlateSnapshotRefusal.ValueOutOfRange);
            return placeholder;
        }

        private static bool OutOfUnit(Vector4 color) =>
            color.X is < 0f or > 1f || color.Y is < 0f or > 1f || color.Z is < 0f or > 1f;

        /// <summary>The Unicode scalar values in <paramref name="text"/>, or -1 when it holds U+0000 or a broken surrogate.</summary>
        private static int Scalars(string text)
        {
            var count = 0;
            for (var index = 0; index < text.Length; count++)
            {
                if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done || rune.Value == 0)
                {
                    return -1;
                }

                index += consumed;
            }

            return count;
        }
    }
}
