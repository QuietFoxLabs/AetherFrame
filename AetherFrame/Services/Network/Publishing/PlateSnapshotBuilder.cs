using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Fonts;
using AetherFrame.UI.Rendering;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>
/// Turns a saved Plate into a <see cref="SnapshotCandidate"/>: the schema 2 layout of what the local
/// renderer visibly draws, in its order, and nothing else (NETWORK2's N2-6a;
/// docs/networking/ProtocolSpecification-v1.md, section 8.5). In two steps:
/// <list type="number">
/// <item><see cref="TryResolve"/>, where the renderer measures (the framework thread, inside its
/// draw callback): the elements <c>ProfileRenderer</c> draws, through the same
/// <c>ProfileVisualBounds</c>, <c>ComponentPaintPlan</c>, <c>ComponentGeometry</c>,
/// <see cref="PaintVisibility"/> and <see cref="BasicNameFit"/>, with the renderer's own measurements.
/// Anything the renderer can't yet measure fails the resolve rather than being guessed.</item>
/// <item><see cref="Map"/>, on any thread, once each drawn window of each image is prepared: the
/// candidate, or every reason the Plate can't be shared as it is.</item>
/// </list>
/// Every value is carried as the renderer resolves it; nothing is clamped, trimmed or dropped to
/// get past a limit. Compiled only in the networking preview flavour, and free of Dalamud and ImGui.
/// </summary>
internal static class PlateSnapshotBuilder
{
    /// <summary>
    /// What the renderer draws for <paramref name="plate"/> (the saved Plate), or false when a font it
    /// needs isn't built yet: the caller tries again a few frames later, then gives up.
    /// </summary>
    internal static bool TryResolve(ProfileDocument plate, IPlateMeasurements measurements, out ResolvedPlate resolved)
    {
        ArgumentNullException.ThrowIfNull(plate);
        ArgumentNullException.ThrowIfNull(measurements);
        resolved = null!;
        var problems = new List<PlateSnapshotProblem>();

        // Something a newer AetherFrame made shows nothing here, so what this Plate shows isn't
        // what its maker saw.
        if (plate.UnrecognizedElements is { Count: > 0 } || plate.UnrecognizedComponents is { Count: > 0 })
        {
            problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.MadeByNewerVersion, null));
        }

        var notReady = false;
        float? Measure(TextProfileElement element)
        {
            if (measurements.TryMeasureNaturalWidth(element, out var width))
            {
                return width;
            }

            notReady = true;
            return null;
        }

        var paintOrder = new List<ProfileElement>();
        var drawn = new List<ProfileElement>();
        var plan = new List<PaintStep>();
        ProfileVisualBounds.FillDrawnElements(plate, ProfileRenderOptions.Finished, paintOrder, drawn);
        ComponentPaintPlan.Build(plate, drawn, BuiltInComponentCatalog.Instance, plan, Measure);
        var view = ProfileVisualBounds.Compute(plate, plan);

        var resolver = new Resolver(plate, measurements, view, problems);
        var background = resolver.Background(plate.Background);
        foreach (var step in plan)
        {
            if (step.Element is { } element)
            {
                resolver.Element(element, ref notReady);
            }
            else if (step.Component is { } component && step.Definition is { } definition)
            {
                resolver.Component(component, definition, step.Placement);
            }
        }

        if (notReady)
        {
            return false;
        }

        var (steps, finalBackground, requirements) = resolver.Finish(background);
        resolved = new ResolvedPlate(plate.ProfileId, plate.Name, plate.CanvasWidth, plate.CanvasHeight, finalBackground, steps, resolver.LeftOut, requirements, problems);
        return true;
    }

    /// <summary>
    /// The candidate for <paramref name="plate"/>, given each required window's prepared copy
    /// (<paramref name="prepared"/>: its fresh asset id, decision N1, its declaration and bytes,
    /// decision D5), or every reason it can't be shared as it is. A component's image window that
    /// wasn't prepared is left out, as the renderer draws nothing without its image; an image
    /// element's or the background's is refused, as the renderer draws a placeholder there.
    /// </summary>
    internal static SnapshotCandidateResult Map(ResolvedPlate plate, IReadOnlyDictionary<ImageRequirement, PreparedImage> prepared)
    {
        ArgumentNullException.ThrowIfNull(plate);
        ArgumentNullException.ThrowIfNull(prepared);

        var mapper = new Mapper(prepared, plate.Problems);
        if (!ProfileLayoutSnapshot.IsValidName(plate.Name))
        {
            mapper.Refuse(PlateSnapshotRefusal.Name, null);
        }

        var canvasWidth = mapper.Hundredths(plate.CanvasWidth, ProtocolLimits.MinLayoutCanvasExtent, ProtocolLimits.MaxLayoutCanvasExtent, null);
        var canvasHeight = mapper.Hundredths(plate.CanvasHeight, ProtocolLimits.MinLayoutCanvasExtent, ProtocolLimits.MaxLayoutCanvasExtent, null);
        var background = mapper.Background(plate.Background);

        var items = new List<LayoutItem>(plate.Steps.Count);
        var roles = new List<ProfileElementRole?>(plate.Steps.Count);
        foreach (var step in plate.Steps)
        {
            if (mapper.Item(step) is { } item)
            {
                items.Add(item);
                roles.Add(step switch
                {
                    ResolvedText text => text.Element.Role,
                    ResolvedImage image => image.Element.Role,
                    _ => null,
                });
            }
        }

        mapper.CheckWhole(items.Count);
        if (mapper.Problems.Count > 0 || background is null)
        {
            return SnapshotCandidateResult.Refused(mapper.Problems);
        }

        var candidate = new SnapshotCandidate(plate.PlateId, plate.Name, canvasWidth, canvasHeight, background, items, roles, mapper.UsedImages, mapper.UsedBytes, plate.LeftOut);
        try
        {
            // The protocol's own check of every rule, with ids and a time of its own: a candidate
            // that passes it can fail at commit only on the ids and the time the commit draws.
            candidate.ToSnapshot(ProfileId.NewId(), RevisionId.NewId(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch (ProtocolException)
        {
            return SnapshotCandidateResult.Refused([new PlateSnapshotProblem(PlateSnapshotRefusal.ValueOutOfRange, null)]);
        }

        return SnapshotCandidateResult.Built(candidate);
    }

    /// <summary>
    /// A colour channel as ImGui turns it into a byte when the renderer draws it (clamped to 0 to 1,
    /// times 255, plus a half, truncated), so a component outside 0 to 1 is carried as drawn.
    /// </summary>
    internal static byte Channel(float value) => (byte)(int)((value < 0f ? 0f : value > 1f ? 1f : value) * 255f + 0.5f);

    /// <summary>
    /// The pixels of an image of <paramref name="width"/> by <paramref name="height"/> that
    /// <paramref name="fit"/> samples into a <paramref name="box"/>, widened outward to whole pixels:
    /// the whole image for Stretch and Fit, the centred window for Fill (<c>ImageFitLayout</c>).
    /// </summary>
    internal static PixelWindow WindowOf(ProfileImageFit fit, Vector2 box, int width, int height)
    {
        var layout = ImageFitLayout.Compute(fit, box, new Vector2(width, height), ImageFitLayout.FullSource, flipX: false, flipY: false);
        var minX = Math.Clamp((int)MathF.Floor(Math.Min(layout.UvMin.X, layout.UvMax.X) * width), 0, width);
        var minY = Math.Clamp((int)MathF.Floor(Math.Min(layout.UvMin.Y, layout.UvMax.Y) * height), 0, height);
        var maxX = Math.Clamp((int)MathF.Ceiling(Math.Max(layout.UvMin.X, layout.UvMax.X) * width), 0, width);
        var maxY = Math.Clamp((int)MathF.Ceiling(Math.Max(layout.UvMin.Y, layout.UvMax.Y) * height), 0, height);
        return maxX > minX && maxY > minY ? new PixelWindow(minX, minY, maxX - minX, maxY - minY) : PixelWindow.Whole(width, height);
    }

    /// <summary>One resolve: the visible steps, what was left out, and the image windows they draw, each once.</summary>
    private sealed class Resolver
    {
        private readonly ProfileDocument plate;
        private readonly IPlateMeasurements measurements;
        private readonly CanvasBounds view;
        private readonly List<PlateSnapshotProblem> problems;
        private readonly List<ComponentPrimitive> primitives = new();

        internal Resolver(ProfileDocument plate, IPlateMeasurements measurements, CanvasBounds view, List<PlateSnapshotProblem> problems)
        {
            this.plate = plate;
            this.measurements = measurements;
            this.view = view;
            this.problems = problems;
        }

        private readonly List<ResolvedStep> steps = new();
        private readonly Dictionary<Guid, (PixelWindow Union, int Width, int Height)> windows = new();
        private readonly List<Guid> imageOrder = new();

        internal List<LeftOutItem> LeftOut { get; } = new();

        /// <summary>
        /// The steps, the background and the image requirements, each image required once and
        /// cropped to the union of the windows its items draw.
        /// </summary>
        internal (IReadOnlyList<ResolvedStep> Steps, ResolvedBackground Background, IReadOnlyList<ImageRequirement> Requirements) Finish(ResolvedBackground background)
        {
            var requirements = new Dictionary<Guid, ImageRequirement>();
            foreach (var image in imageOrder)
            {
                var (union, width, height) = windows[image];
                requirements[image] = new ImageRequirement(image, union, width, height);
            }

            ImageRequirement? Final(ImageRequirement? drawn) => drawn is { } requirement ? requirements[requirement.Image] : null;

            var final = new List<ResolvedStep>(steps.Count);
            foreach (var step in steps)
            {
                final.Add(step switch
                {
                    ResolvedImage image => image with { Image = requirements[image.Image.Image] },
                    ResolvedShape shape => shape with { Image = Final(shape.Image) },
                    _ => step,
                });
            }

            var requirementList = new List<ImageRequirement>(imageOrder.Count);
            foreach (var image in imageOrder)
            {
                requirementList.Add(requirements[image]);
            }

            return (final, background with { Image = Final(background.Image) }, requirementList);
        }

        internal ResolvedBackground Background(ProfileBackground? background)
        {
            // Mode None, or an opacity of 0, draws nothing at all, pattern included.
            if (background is null || PaintVisibility.BackgroundOpacity(background) <= 0f)
            {
                return ResolvedBackground.Nothing;
            }

            if (background.Mode != ProfileBackgroundMode.Image)
            {
                return new ResolvedBackground(background, null, NoBase: false);
            }

            // Image mode with no image draws no base, and still its pattern.
            if (background.ImageAssetId is not { } image || image == Guid.Empty)
            {
                return new ResolvedBackground(background, null, NoBase: true);
            }

            if (!measurements.TryGetImageSize(image, out var width, out var height))
            {
                problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.BackgroundImageMissing, null));
                return new ResolvedBackground(background, null, NoBase: false);
            }

            var canvas = new Vector2(Math.Max(0f, plate.CanvasWidth), Math.Max(0f, plate.CanvasHeight));
            return new ResolvedBackground(background, Require(image, WindowOf(background.ImageFit, canvas, width, height), width, height), NoBase: false);
        }

        internal void Element(ProfileElement element, ref bool notReady)
        {
            switch (element)
            {
                case TextProfileElement text:
                    Text(text, ref notReady);
                    break;

                case ImageProfileElement image:
                    Image(image);
                    break;

                default:
                    problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.UnknownElement, element));
                    break;
            }
        }

        internal void Component(PlateComponent component, ComponentDefinition definition, ComponentPlacement placement)
        {
            primitives.Clear();
            ComponentGeometry.Build(plate, component, definition, placement, primitives);
            foreach (var primitive in primitives)
            {
                var points = primitive.Kind == ComponentPrimitiveKind.Triangle ? new[] { primitive.A, primitive.B, primitive.C } : new[] { primitive.A, primitive.B, primitive.C, primitive.D };
                if (!Meets(Bounds(points)))
                {
                    var outside = new LeftOutItem(null, component, LeftOutReason.OutsideView);
                    if (!LeftOut.Contains(outside))
                    {
                        LeftOut.Add(outside);
                    }

                    continue;
                }

                switch (primitive.Kind)
                {
                    case ComponentPrimitiveKind.Image:
                        // As the renderer: a component's image quad draws nothing without its image,
                        // and samples the whole of it.
                        if (PaintVisibility.ComponentImage(component) is { } image && measurements.TryGetImageSize(image, out var width, out var height))
                        {
                            steps.Add(new ResolvedShape(primitive, Require(image, PixelWindow.Whole(width, height), width, height), null));
                        }
                        else
                        {
                            var missing = new LeftOutItem(null, component, LeftOutReason.ImageMissing);
                            if (!LeftOut.Contains(missing))
                            {
                                LeftOut.Add(missing);
                            }
                        }

                        break;

                    case ComponentPrimitiveKind.Art:
                        if (definition.Art is { } art)
                        {
                            steps.Add(new ResolvedShape(primitive, null, art.Id));
                        }

                        break;

                    default:
                        steps.Add(new ResolvedShape(primitive, null, null));
                        break;
                }
            }
        }

        private void Text(TextProfileElement text, ref bool notReady)
        {
            if (!measurements.TryGetDisplayOverride(plate, text, out var shown))
            {
                notReady = true;
                return;
            }

            var display = shown ?? text.GetDisplayText();
            if (!PaintVisibility.TextDraws(display, PaintVisibility.TextAlpha(text)))
            {
                LeftOut.Add(new LeftOutItem(text, null, string.IsNullOrEmpty(display) ? LeftOutReason.Empty : LeftOutReason.Transparent));
                return;
            }

            if (!Meets(TextFootprint(text)))
            {
                LeftOut.Add(new LeftOutItem(text, null, LeftOutReason.OutsideView));
                return;
            }

            // The Basic name is drawn at BasicNameFit's size, not the generic auto fit: that size,
            // and whether it wraps, are what is shared, with auto fit off.
            if (BasicNameFit.Applies(text))
            {
                if (!measurements.TryMeasureNaturalWidth(text, out var natural))
                {
                    notReady = true;
                    return;
                }

                var (size, wrap) = BasicNameFit.ForBox(text, natural);
                steps.Add(new ResolvedText(text, display, size, wrap, AutoFit: false, SizeBaked: true));
                return;
            }

            steps.Add(new ResolvedText(text, display, text.FontSize, text.Wrap, text.AutoFitText, SizeBaked: false));
        }

        private void Image(ImageProfileElement image)
        {
            if (!Meets(Bounds(RotationGeometry.GetRotatedCorners(image.Position, image.Size, image.RotationDegrees))))
            {
                LeftOut.Add(new LeftOutItem(image, null, LeftOutReason.OutsideView));
                return;
            }

            // A missing image is refused before its opacity is looked at: finished rendering draws
            // its placeholder at any opacity.
            if (image.AssetId == Guid.Empty || !measurements.TryGetImageSize(image.AssetId, out var width, out var height))
            {
                problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.ImageMissing, image));
                return;
            }

            if (!PaintVisibility.ImageDraws(image))
            {
                LeftOut.Add(new LeftOutItem(image, null, LeftOutReason.Transparent));
                return;
            }

            steps.Add(new ResolvedImage(image, Require(image.AssetId, WindowOf(image.DisplayMode, image.Size, width, height), width, height)));
        }

        /// <summary>Records that <paramref name="window"/> of an image is drawn; its copy is cropped to the union of every such window.</summary>
        private ImageRequirement Require(Guid image, PixelWindow window, int width, int height)
        {
            if (windows.TryGetValue(image, out var known))
            {
                var minX = Math.Min(known.Union.X, window.X);
                var minY = Math.Min(known.Union.Y, window.Y);
                var maxX = Math.Max(known.Union.X + known.Union.Width, window.X + window.Width);
                var maxY = Math.Max(known.Union.Y + known.Union.Height, window.Y + window.Height);
                windows[image] = (new PixelWindow(minX, minY, maxX - minX, maxY - minY), width, height);
            }
            else
            {
                windows[image] = (window, width, height);
                imageOrder.Add(image);
            }

            return new ImageRequirement(image, window, width, height);
        }

        /// <summary>Whether a footprint meets the Profile View's bounds (the canvas and every component's painted bounds) with some area.</summary>
        private bool Meets((Vector2 Min, Vector2 Max) footprint) =>
            float.IsFinite(footprint.Min.X) && float.IsFinite(footprint.Min.Y) && float.IsFinite(footprint.Max.X) && float.IsFinite(footprint.Max.Y)
                ? footprint.Max.X > view.Min.X && footprint.Min.X < view.Max.X && footprint.Max.Y > view.Min.Y && footprint.Min.Y < view.Max.Y
                : true;

        /// <summary>A text's box, grown by what its drawn outline and shadow extend past it (as the renderer's clip is).</summary>
        private static (Vector2 Min, Vector2 Max) TextFootprint(TextProfileElement text)
        {
            var outline = text.OutlineEnabled && text.OutlineThickness > 0f ? Math.Max(1f, Math.Min(text.OutlineThickness, TextProfileElement.MaxOutlineThickness)) : 0f;
            var shadow = text.ShadowEnabled ? Math.Max(Math.Abs(text.ShadowOffsetX), Math.Abs(text.ShadowOffsetY)) : 0f;
            var margin = new Vector2(outline + shadow);
            return (text.Position - margin, text.Position + text.Size + margin);
        }

        private static (Vector2 Min, Vector2 Max) Bounds(IReadOnlyList<Vector2> points)
        {
            var min = points[0];
            var max = points[0];
            for (var index = 1; index < points.Count; index++)
            {
                min = Vector2.Min(min, points[index]);
                max = Vector2.Max(max, points[index]);
            }

            return (min, max);
        }
    }

    /// <summary>One mapping: the problems found so far, and the prepared images the items name, each once.</summary>
    private sealed class Mapper
    {
        private readonly IReadOnlyDictionary<ImageRequirement, PreparedImage> prepared;
        private readonly List<PlateSnapshotProblem> problems;
        private readonly List<ImageReference> used = new();
        private readonly List<ReadOnlyMemory<byte>> usedBytes = new();
        private readonly HashSet<ImageRequirement> usedRequirements = new();
        private int textScalars;
        private int refusals;

        internal Mapper(IReadOnlyDictionary<ImageRequirement, PreparedImage> prepared, IReadOnlyList<PlateSnapshotProblem> earlier)
        {
            this.prepared = prepared;
            problems = new List<PlateSnapshotProblem>(earlier);
        }

        internal List<PlateSnapshotProblem> Problems => problems;

        internal IReadOnlyList<ImageReference> UsedImages => used;

        internal IReadOnlyList<ReadOnlyMemory<byte>> UsedBytes => usedBytes;

        internal void Refuse(PlateSnapshotRefusal refusal, ProfileElement? element)
        {
            // Counted before the problem is deduplicated, so a value refused again (another shape
            // out of range, say) still stops its own item from being built.
            refusals++;
            var problem = new PlateSnapshotProblem(refusal, element);
            if (!problems.Contains(problem))
            {
                problems.Add(problem);
            }
        }

        /// <summary>The whole-snapshot limits of section 8.5, in its order, each with its own words.</summary>
        internal void CheckWhole(int itemCount)
        {
            if (itemCount > ProtocolLimits.MaxLayoutItems)
            {
                Refuse(PlateSnapshotRefusal.TooManyItems, null);
            }

            if (used.Count > ProtocolLimits.MaxImagesPerProfile)
            {
                Refuse(PlateSnapshotRefusal.TooManyImages, null);
            }

            long bytes = 0;
            long pixels = 0;
            foreach (var image in used)
            {
                bytes += image.ByteLength;
                pixels += (long)image.Width * image.Height;
            }

            if (bytes > ProtocolLimits.MaxProfileImageBytes)
            {
                Refuse(PlateSnapshotRefusal.TooManyImageBytes, null);
            }

            if (pixels > ProtocolLimits.MaxLayoutImagePixels)
            {
                Refuse(PlateSnapshotRefusal.TooManyImagePixels, null);
            }

            if (textScalars > ProtocolLimits.MaxLayoutTextScalars)
            {
                Refuse(PlateSnapshotRefusal.TooMuchText, null);
            }
        }

        internal LayoutBackground? Background(ResolvedBackground resolved)
        {
            if (resolved.Background is not { } background)
            {
                return LayoutBackground.None;
            }

            // A background with a problem is never built, whatever that problem was.
            var before = refusals;
            var mode = background.Mode switch
            {
                ProfileBackgroundMode.SolidColor => LayoutBackgroundMode.SolidColor,
                ProfileBackgroundMode.LinearGradient => LayoutBackgroundMode.LinearGradient,
                ProfileBackgroundMode.TexturedFill => LayoutBackgroundMode.TexturedFill,
                ProfileBackgroundMode.Image => LayoutBackgroundMode.Image,
                _ => Unexpressible(LayoutBackgroundMode.None, null),
            };

            // The renderer blends a gradient's endpoints before it resolves their colours, so a
            // component outside 0 to 1 draws a gradient no pair of colours in range can.
            if (mode == LayoutBackgroundMode.LinearGradient && (OutOfUnit(background.PrimaryColor) || OutOfUnit(background.SecondaryColor)))
            {
                Refuse(PlateSnapshotRefusal.GradientColor, null);
            }

            var primary = background.PrimaryColor;
            var image = default(AssetId);
            if (resolved.NoBase)
            {
                // Image mode with no image draws no base: a solid fill of nothing, under its pattern.
                mode = LayoutBackgroundMode.SolidColor;
                primary = primary with { W = 0f };
            }
            else if (mode == LayoutBackgroundMode.Image)
            {
                if (resolved.Image is { } requirement && Use(requirement) is { } asset)
                {
                    image = asset;
                }
                else
                {
                    Refuse(PlateSnapshotRefusal.BackgroundImageMissing, null);
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
                Primary: Color(primary, null),
                Secondary: Color(background.SecondaryColor, null),
                GradientAngle: Angle(background.GradientAngle, null),
                Opacity: Unit(background.Opacity, null),
                Intensity: Unit(background.TextureIntensity, null),
                Scale: Hundredths(scale, LayoutBackground.MinTextureScale, LayoutBackground.MaxTextureScale, null),
                Rotation: Angle(background.TextureRotation, null),
                Fit: Fit(background.ImageFit, null));
            if (refusals != before)
            {
                return null;
            }

            return new LayoutBackground(
                mode, fields.Primary, fields.Secondary, fields.GradientAngle, fields.Opacity, texture, fields.Intensity,
                fields.Scale, fields.Rotation, image, fields.Fit, Flips(background.ImageFlipX, background.ImageFlipY));
        }

        internal LayoutItem? Item(ResolvedStep step)
        {
            // An item with a problem is never built: its constructor would only refuse the same
            // value again, and the snapshot is refused anyway.
            var before = refusals;
            var item = step switch
            {
                ResolvedText text => Text(text),
                ResolvedImage image => Image(image),
                ResolvedShape shape => Shape(shape),
                _ => null,
            };

            return refusals == before ? item : null;
        }

        private LayoutItem? Text(ResolvedText resolved)
        {
            var text = resolved.Element;
            var scalars = Scalars(resolved.Text);
            if (scalars < 0)
            {
                Refuse(PlateSnapshotRefusal.TextUnshareable, text);
                return null;
            }

            if (scalars > ProtocolLimits.MaxLayoutItemTextScalars)
            {
                Refuse(PlateSnapshotRefusal.TextTooLong, text);
                return null;
            }

            textScalars += scalars;
            var flags = LayoutTextFlags.None;
            flags |= resolved.Wrap ? LayoutTextFlags.Wrap : LayoutTextFlags.None;
            flags |= text.Bold ? LayoutTextFlags.Bold : LayoutTextFlags.None;
            flags |= text.Italic ? LayoutTextFlags.Italic : LayoutTextFlags.None;
            flags |= text.Underline ? LayoutTextFlags.Underline : LayoutTextFlags.None;
            flags |= text.Strikethrough ? LayoutTextFlags.Strikethrough : LayoutTextFlags.None;
            flags |= resolved.AutoFit ? LayoutTextFlags.AutoFit : LayoutTextFlags.None;
            flags |= text.OutlineEnabled ? LayoutTextFlags.Outline : LayoutTextFlags.None;
            flags |= text.ShadowEnabled ? LayoutTextFlags.Shadow : LayoutTextFlags.None;

            var before = refusals;
            var position = Point(text.Position, text);
            var width = Hundredths(text.Size.X, 0, ProtocolLimits.MaxLayoutExtent, text);
            var height = Hundredths(text.Size.Y, 0, ProtocolLimits.MaxLayoutExtent, text);

            // A size BasicNameFit resolved is rounded down, so the shared name never draws larger.
            var fontSize = resolved.SizeBaked
                ? Floor(resolved.FontSize, LayoutText.MinFontSize, LayoutText.MaxFontSize, text)
                : Hundredths(resolved.FontSize, LayoutText.MinFontSize, LayoutText.MaxFontSize, text);
            var letterSpacing = Hundredths(text.LetterSpacing, -LayoutText.MaxLetterSpacing, LayoutText.MaxLetterSpacing, text);
            var lineSpacing = Hundredths(text.LineSpacing, -LayoutText.MaxLineSpacing, LayoutText.MaxLineSpacing, text);

            // The renderer shrinks an auto-fit text to no less than one canvas unit.
            var autoFitMinimum = Hundredths(Math.Max(1f, text.AutoFitMinimumSize), LayoutText.MinFontSize, LayoutText.MaxFontSize, text);

            // The renderer draws an outline at most MaxOutlineThickness thick, and a thickness above 0
            // is never carried as 0.
            var outlineThickness = OutlineThickness(Math.Min(text.OutlineThickness, TextProfileElement.MaxOutlineThickness), text);
            var shadowX = Hundredths(text.ShadowOffsetX, -LayoutText.MaxShadowOffset, LayoutText.MaxShadowOffset, text);
            var shadowY = Hundredths(text.ShadowOffsetY, -LayoutText.MaxShadowOffset, LayoutText.MaxShadowOffset, text);

            // The renderer draws each effect in its colour's red, green and blue at the text's alpha
            // times the effect's opacity, ignoring the colour's own alpha (ProfileTextRenderer's
            // DrawPassGroup); a viewer multiplies the carried alpha by the text's, so it is the opacity.
            var outlineColor = Color(text.OutlineColor with { W = Math.Clamp(text.OutlineOpacity, 0f, 1f) }, text);
            var shadowColor = Color(text.ShadowColor with { W = Math.Clamp(text.ShadowOpacity, 0f, 1f) }, text);
            var align = text.Alignment switch
            {
                TextAlignment.Left => LayoutHorizontalAlign.Left,
                TextAlignment.Center => LayoutHorizontalAlign.Center,
                TextAlignment.Right => LayoutHorizontalAlign.Right,
                _ => Unexpressible(LayoutHorizontalAlign.Left, text),
            };
            var verticalAlign = text.VerticalAlignment switch
            {
                TextVerticalAlignment.Top => LayoutVerticalAlign.Top,
                TextVerticalAlignment.Middle => LayoutVerticalAlign.Middle,
                TextVerticalAlignment.Bottom => LayoutVerticalAlign.Bottom,
                _ => Unexpressible(LayoutVerticalAlign.Top, text),
            };

            var color = Color(text.Color, text);
            if (refusals != before)
            {
                return null;
            }

            return new LayoutText(
                position, width, height, resolved.Text, ProfileFontCatalog.Resolve(text.FontFamily).Id, fontSize, color, align, verticalAlign, flags,
                letterSpacing, lineSpacing, autoFitMinimum, outlineColor, outlineThickness, shadowColor, shadowX, shadowY,
                text.UsesLegacyLayout ? LayoutTextLayout.Legacy : LayoutTextLayout.Current);
        }

        private LayoutItem? Image(ResolvedImage resolved)
        {
            var image = resolved.Element;
            if (Use(resolved.Image) is not { } asset)
            {
                Refuse(PlateSnapshotRefusal.ImageMissing, image);
                return null;
            }

            var before = refusals;
            var position = Point(image.Position, image);
            var width = Hundredths(image.Size.X, 0, ProtocolLimits.MaxLayoutExtent, image);
            var height = Hundredths(image.Size.Y, 0, ProtocolLimits.MaxLayoutExtent, image);
            var rotation = Angle(image.RotationDegrees, image);
            var fit = Fit(image.DisplayMode, image);
            var opacity = Unit(image.Opacity, image);
            return refusals != before ? null : new LayoutImage(asset, position, width, height, rotation, fit, Flips(image.FlipX, image.FlipY), opacity);
        }

        private LayoutItem? Shape(ResolvedShape shape)
        {
            var primitive = shape.Primitive;
            var before = refusals;
            var color = Color(primitive.Color, null);
            var (a, b, c, d) = (Point(primitive.A, null), Point(primitive.B, null), Point(primitive.C, null), Point(primitive.D, null));
            if (refusals != before)
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
                    return shape.Image is { } requirement && Use(requirement) is { } asset ? new LayoutImageQuad(asset, a, b, c, d, color) : null;

                case ComponentPrimitiveKind.Art when shape.Art is { } art:
                    return new LayoutArtQuad(art, a, b, c, d, color);

                default:
                    return null;
            }
        }

        /// <summary>The prepared copy of a drawn window, named in the snapshot once however often it is drawn; null when it wasn't prepared.</summary>
        private AssetId? Use(ImageRequirement requirement)
        {
            if (!prepared.TryGetValue(requirement, out var copy))
            {
                return null;
            }

            if (usedRequirements.Add(requirement))
            {
                used.Add(copy.Reference);
                usedBytes.Add(copy.Bytes);
            }

            return copy.Reference.AssetId;
        }

        private LayoutPoint Point(Vector2 point, ProfileElement? element) =>
            new(Hundredths(point.X, -ProtocolLimits.MaxLayoutCoordinate, ProtocolLimits.MaxLayoutCoordinate, element), Hundredths(point.Y, -ProtocolLimits.MaxLayoutCoordinate, ProtocolLimits.MaxLayoutCoordinate, element));

        /// <summary>An angle in hundredths of a degree, normalized into one turn first.</summary>
        private int Angle(float degrees, ProfileElement? element) => float.IsFinite(degrees)
            ? Hundredths(degrees % 360f, -ProtocolLimits.MaxLayoutAngle, ProtocolLimits.MaxLayoutAngle, element)
            : Unexpressible(0, element);

        private int OutlineThickness(float thickness, ProfileElement element)
        {
            var hundredths = Hundredths(thickness, int.MinValue, LayoutText.MaxOutlineThickness, element);
            return thickness > 0f && hundredths <= 0 ? 1 : Math.Max(0, hundredths);
        }

        /// <summary>A canvas value in hundredths, rounded to the nearest (halves away from zero), or a problem when outside [<paramref name="min"/>, <paramref name="max"/>].</summary>
        internal int Hundredths(float value, int min, int max, ProfileElement? element)
        {
            if (!float.IsFinite(value))
            {
                return Unexpressible(min, element);
            }

            var hundredths = Math.Round((double)value * 100d, MidpointRounding.AwayFromZero);
            return hundredths < min || hundredths > max ? Unexpressible(min, element) : (int)hundredths;
        }

        private int Floor(float value, int min, int max, ProfileElement element)
        {
            if (!float.IsFinite(value))
            {
                return Unexpressible(min, element);
            }

            var hundredths = Math.Floor((double)value * 100d);
            return hundredths < min || hundredths > max ? Unexpressible(min, element) : (int)hundredths;
        }

        private LayoutColor Color(Vector4 color, ProfileElement? element) =>
            float.IsFinite(color.X) && float.IsFinite(color.Y) && float.IsFinite(color.Z) && float.IsFinite(color.W)
                ? new LayoutColor(Channel(color.X), Channel(color.Y), Channel(color.Z), Channel(color.W))
                : Unexpressible(default(LayoutColor), element);

        /// <summary>A 0 to 1 opacity as the renderer turns it into a byte.</summary>
        private byte Unit(float value, ProfileElement? element) => float.IsFinite(value) ? Channel(Math.Clamp(value, 0f, 1f)) : Unexpressible((byte)0, element);

        private LayoutImageFit Fit(ProfileImageFit fit, ProfileElement? element) => fit switch
        {
            ProfileImageFit.Stretch => LayoutImageFit.Stretch,
            ProfileImageFit.Fit => LayoutImageFit.Fit,
            ProfileImageFit.Fill => LayoutImageFit.Fill,
            _ => Unexpressible(LayoutImageFit.Stretch, element),
        };

        private static LayoutFlips Flips(bool horizontal, bool vertical) =>
            (horizontal ? LayoutFlips.Horizontal : LayoutFlips.None) | (vertical ? LayoutFlips.Vertical : LayoutFlips.None);

        private T Unexpressible<T>(T placeholder, ProfileElement? element)
        {
            Refuse(PlateSnapshotRefusal.ValueOutOfRange, element);
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
