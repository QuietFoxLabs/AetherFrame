using System;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;
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
/// <item><see cref="Map"/>, on any thread, once each required copy of each image is prepared: the
/// candidate, or every reason the Plate can't be shared as it is.</item>
/// </list>
/// Every value is carried as the renderer resolves it; nothing is clamped, trimmed or dropped to
/// get past a limit. Compiled only in the networking preview flavour, and free of Dalamud and ImGui.
/// </summary>
internal static class PlateSnapshotBuilder
{
    /// <summary>
    /// What the renderer draws for <paramref name="plate"/>, or false when a font it needs isn't
    /// built yet: the caller tries again a few frames later, then gives up.
    /// <paramref name="plate"/> is a private copy of the saved Plate (N2-6c deserializes the saved
    /// JSON for it, never handing over the document an editor holds), and nothing changes it until
    /// <see cref="Map"/> has returned: the steps name its elements, and Map reads their values.
    /// </summary>
    internal static bool TryResolve(ProfileDocument plate, IPlateMeasurements measurements, out ResolvedPlate resolved)
    {
        ArgumentNullException.ThrowIfNull(plate);
        ArgumentNullException.ThrowIfNull(measurements);
        resolved = null!;
        var problems = new List<PlateSnapshotProblem>();

        // Something a newer AetherFrame made shows nothing here, or shows otherwise, so what this
        // Plate shows isn't what its maker saw: an element or component this build can't read, a
        // Components value it can't read at all, or a visible component of a kind or definition it
        // doesn't know. A setting it doesn't recognize on what is drawn (the package check's
        // "settings from a newer version") is refused where it is met.
        if (plate.UnrecognizedElements is { Count: > 0 } || plate.UnrecognizedComponents is { Count: > 0 } || plate.MalformedComponentsValue is not null)
        {
            problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.MadeByNewerVersion, null));
        }

        RefuseUnknownComponents(plate, problems);

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

        // A theme or orientation a newer build made changes how components draw here: the colour
        // of one without its own, and where one without an anchor element sits.
        if (plate.BasicPlate is { } basic
            && ((!string.IsNullOrEmpty(basic.ThemeId) && ProfileThemePresets.Find(basic.ThemeId) is null) || !Enum.IsDefined(basic.Orientation))
            && plan.Exists(step => step.Component is not null))
        {
            problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.MadeByNewerVersion, null));
        }

        // What the Profile View shows is what its window is sized to: these bounds, from the plan
        // without measured text, as every surface that fits a Plate computes them.
        var view = ProfileVisualBounds.Compute(plate, ProfileRenderOptions.Finished);

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
    /// The candidate for <paramref name="plate"/>, given what preparing each required copy
    /// produced (<paramref name="preparations"/>, one for every requirement: its prepared copy, its
    /// declaration and bytes, decision D5; or why there is none), or every reason the Plate can't be
    /// shared as it is. The Plate <see cref="TryResolve"/> was given must not have changed since.
    /// Every copy the candidate uses is declared under an asset id drawn for this candidate alone
    /// (N1), since neither the bytes nor the digest carry it: candidates built from the same
    /// preparations never share one, and a candidate is signed at most once
    /// (<see cref="SnapshotCandidate.TryClaimForSigning"/>), so no two revisions ever do.
    /// <list type="bullet">
    /// <item>An image whose managed file is missing or undecodable is left out when a component
    /// draws it, as the renderer draws nothing there, and refused when an element or the
    /// background draws it, as the renderer draws a placeholder there.</item>
    /// <item>An image preparation refused, or a copy that isn't its window's (its size, byte
    /// length or digest), refuses the Plate, whichever item draws it: nothing is dropped
    /// silently.</item>
    /// </list>
    /// The candidate holds its own copy of every image's bytes, and those bytes are the ones checked.
    /// </summary>
    /// <exception cref="ArgumentException">A requirement of <paramref name="plate"/> has no preparation.</exception>
    internal static SnapshotCandidateResult Map(ResolvedPlate plate, IReadOnlyDictionary<ImageRequirement, ImagePreparation> preparations)
    {
        ArgumentNullException.ThrowIfNull(plate);
        ArgumentNullException.ThrowIfNull(preparations);
        foreach (var requirement in plate.Requirements)
        {
            if (!preparations.ContainsKey(requirement))
            {
                throw new ArgumentException("Every required copy of an image has a preparation.", nameof(preparations));
            }
        }

        var mapper = new Mapper(preparations, plate.Problems);
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

        var leftOut = new List<LeftOutItem>(plate.LeftOut);
        foreach (var left in mapper.LeftOut)
        {
            if (!leftOut.Contains(left))
            {
                leftOut.Add(left);
            }
        }

        var candidate = new SnapshotCandidate(plate.PlateId, plate.Name, canvasWidth, canvasHeight, background, items, roles, mapper.UsedImages, mapper.UsedBytes, leftOut);
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

    /// <summary>Refuses each visible component of a kind or definition this build doesn't know (a newer build's), which draws nothing here.</summary>
    private static void RefuseUnknownComponents(ProfileDocument plate, List<PlateSnapshotProblem> problems)
    {
        if (plate.Components is not { } components)
        {
            return;
        }

        foreach (var component in components)
        {
            if (component is { Visible: true }
                && ComponentPaintPlan.Resolve(component, BuiltInComponentCatalog.Instance, out _) is ComponentStatus.UnknownKind or ComponentStatus.MissingDefinition or ComponentStatus.KindMismatch)
            {
                problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.MadeByNewerVersion, null, component));
            }
        }
    }

    /// <summary>
    /// A colour channel as ImGui turns it into a byte when the renderer draws it (clamped to 0 to 1,
    /// times 255, plus a half, truncated), so a component outside 0 to 1 is carried as drawn.
    /// </summary>
    internal static byte Channel(float value) => (byte)(int)((value < 0f ? 0f : value > 1f ? 1f : value) * 255f + 0.5f);

    /// <summary>Whether anything drawn at <paramref name="alpha"/> shows: ImGui turns an alpha under half a 255th into 0, which draws nothing.</summary>
    private static bool Shows(float alpha) => Channel(alpha) > 0;

    /// <summary>
    /// The pixels of an image of <paramref name="width"/> by <paramref name="height"/> that
    /// <paramref name="fit"/> samples into a <paramref name="box"/>: the whole image for Stretch and
    /// Fit; for Fill, the centred window (<c>ImageFitLayout</c>), widened to whole pixels equally on
    /// both sides. It stays centred exactly, so Fill on a copy of it samples the same pixels.
    /// </summary>
    internal static PixelWindow WindowOf(ProfileImageFit fit, Vector2 box, int width, int height)
    {
        var layout = ImageFitLayout.Compute(fit, box, new Vector2(width, height), ImageFitLayout.FullSource, flipX: false, flipY: false);
        var (x, windowWidth) = Centred(Math.Abs(layout.UvMax.X - layout.UvMin.X) * width, width);
        var (y, windowHeight) = Centred(Math.Abs(layout.UvMax.Y - layout.UvMin.Y) * height, height);
        return new PixelWindow(x, y, windowWidth, windowHeight);
    }

    /// <summary>
    /// The fewest whole pixels of a row <paramref name="size"/> long, centred exactly in it, that
    /// hold a centred run of <paramref name="span"/>: equal margins need a length of the row's
    /// parity, and a run under a pixel still takes one or two. A thousandth of a pixel of float
    /// error is forgiven.
    /// </summary>
    private static (int Start, int Length) Centred(float span, int size)
    {
        if (!float.IsFinite(span) || span >= size)
        {
            return (0, size);
        }

        var length = Math.Max(1, (int)MathF.Ceiling(span - 0.001f));
        if ((size - length) % 2 != 0)
        {
            length++;
        }

        return ((size - length) / 2, length);
    }

    /// <summary>One resolve: the visible steps, what was left out, and the image windows they draw.</summary>
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

        // Every distinct window drawn of every image, in the order first drawn.
        private readonly List<ImageRequirement> windows = new();

        internal List<LeftOutItem> LeftOut { get; } = new();

        /// <summary>
        /// The steps, the background and the prepared copies they draw from. An image needs a copy
        /// of each window drawn of it that no other window drawn of it holds, in the order first
        /// drawn, and a window inside another is drawn from that one's copy: every window is centred
        /// in its image, and one inside another spans the same whole width or height, so Fill on
        /// the larger copy gives the smaller window back. Nothing outside the windows items draw is
        /// in any copy, as a union of windows across each other's axis would be (the corners
        /// neither draws).
        /// </summary>
        internal (IReadOnlyList<ResolvedStep> Steps, ResolvedBackground Background, IReadOnlyList<ImageRequirement> Requirements) Finish(ResolvedBackground background)
        {
            var copies = new List<ImageRequirement>();
            foreach (var window in windows)
            {
                if (!windows.Exists(other => other != window && Holds(other, window)))
                {
                    copies.Add(window);
                }
            }

            ImageRequirement CopyFor(ImageRequirement drawn) => copies.Find(copy => Holds(copy, drawn));

            var final = new List<ResolvedStep>(steps.Count);
            foreach (var step in steps)
            {
                final.Add(step switch
                {
                    ResolvedImage image => image with { Image = CopyFor(image.Image) },
                    ResolvedShape { Image: { } drawn } shape => shape with { Image = CopyFor(drawn) },
                    _ => step,
                });
            }

            return (final, background.Image is { } backdrop ? background with { Image = CopyFor(backdrop) } : background, copies);
        }

        internal ResolvedBackground Background(ProfileBackground? background)
        {
            // Mode None, or an opacity of 0, draws nothing at all, pattern included.
            var opacity = PaintVisibility.BackgroundOpacity(background);
            if (background is null || opacity <= 0f)
            {
                return ResolvedBackground.Nothing;
            }

            // A mode, pattern or image fit a newer build made draws otherwise here (the fit is
            // carried in every mode, as the package check reads it).
            if (!Enum.IsDefined(background.Mode) || !Enum.IsDefined(background.Texture) || !Enum.IsDefined(background.ImageFit))
            {
                problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.MadeByNewerVersion, null, Background: true));
                return ResolvedBackground.Nothing;
            }

            // An image the renderer can't find (an empty id among them) draws its placeholder,
            // whatever the opacity: refused, with every other reason the background has.
            var (image, width, height) = (default(Guid?), 0, 0);
            if (background.Mode == ProfileBackgroundMode.Image && background.ImageAssetId is { } id)
            {
                if (id == Guid.Empty || !measurements.TryGetImageSize(id, out width, out height))
                {
                    problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.BackgroundImageMissing, null, Background: true));
                    return new ResolvedBackground(background, null, NoBase: false);
                }

                image = id;
            }

            // Everything else draws at the background's opacity: at an alpha ImGui turns into 0,
            // nothing of it shows.
            if (!Shows(opacity))
            {
                return ResolvedBackground.Nothing;
            }

            if (background.Mode != ProfileBackgroundMode.Image)
            {
                return new ResolvedBackground(background, null, NoBase: false);
            }

            // Image mode with no image draws no base, and still its pattern.
            if (image is not { } drawn)
            {
                return new ResolvedBackground(background, null, NoBase: true);
            }

            var canvas = new Vector2(Math.Max(0f, plate.CanvasWidth), Math.Max(0f, plate.CanvasHeight));
            return new ResolvedBackground(background, Require(drawn, WindowOf(background.ImageFit, canvas, width, height), width, height), NoBase: false);
        }

        internal void Element(ProfileElement element, ref bool notReady)
        {
            // A role a newer build made may fill the element with what this build doesn't draw.
            if (!Enum.IsDefined(element.Role))
            {
                problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.MadeByNewerVersion, element));
                return;
            }

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
                // A primitive at an alpha ImGui turns into 0 draws nothing, and needs no image.
                if (!Shows(primitive.Color.W))
                {
                    continue;
                }

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
                            steps.Add(new ResolvedShape(primitive, Require(image, PixelWindow.Whole(width, height), width, height), null, component));
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
                        // A piece of sliced artwork is named by its own ident, so the art quad's
                        // corners still carry the whole of what it names (section 8.5).
                        if (definition.Art is { } art)
                        {
                            steps.Add(new ResolvedShape(primitive, null, art.PieceIdent(primitive.Piece), component));
                        }

                        break;

                    default:
                        steps.Add(new ResolvedShape(primitive, null, null, component));
                        break;
                }
            }
        }

        private void Text(TextProfileElement text, ref bool notReady)
        {
            // An alignment or text layout a newer build made draws this text otherwise here.
            if (!Enum.IsDefined(text.Alignment) || !Enum.IsDefined(text.VerticalAlignment) || text.LayoutVersion > TextProfileElement.CurrentLayoutVersion)
            {
                problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.MadeByNewerVersion, text));
                return;
            }

            if (!measurements.TryGetDisplayOverride(plate, text, out var shown))
            {
                notReady = true;
                return;
            }

            // A text draws at its alpha, and its outline and shadow at no more: at an alpha ImGui
            // turns into 0, nothing of it shows.
            var display = shown ?? text.GetDisplayText();
            var alpha = PaintVisibility.TextAlpha(text);
            if (!PaintVisibility.TextDraws(display, alpha) || !Shows(alpha))
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
            // and whether it wraps, are what is shared, with auto fit off. The renderer draws a
            // text at no less than one canvas unit.
            if (BasicNameFit.Applies(text))
            {
                if (!measurements.TryMeasureNaturalWidth(text, out var natural))
                {
                    notReady = true;
                    return;
                }

                var (size, wrap) = BasicNameFit.ForBox(text, natural);
                steps.Add(new ResolvedText(text, display, Math.Max(1f, size), wrap, AutoFit: false, SizeBaked: true));
                return;
            }

            steps.Add(new ResolvedText(text, display, Math.Max(1f, text.FontSize), text.Wrap, text.AutoFitText, SizeBaked: false));
        }

        private void Image(ImageProfileElement image)
        {
            // A fit a newer build made draws this image otherwise here.
            if (!Enum.IsDefined(image.DisplayMode))
            {
                problems.Add(new PlateSnapshotProblem(PlateSnapshotRefusal.MadeByNewerVersion, image));
                return;
            }

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

            // At an alpha ImGui turns into 0, the image draws nothing.
            if (!PaintVisibility.ImageDraws(image) || !Shows(PaintVisibility.ImageOpacity(image)))
            {
                LeftOut.Add(new LeftOutItem(image, null, LeftOutReason.Transparent));
                return;
            }

            steps.Add(new ResolvedImage(image, Require(image.AssetId, WindowOf(image.DisplayMode, image.Size, width, height), width, height)));
        }

        /// <summary>Records that <paramref name="window"/> of an image is drawn; <see cref="Finish"/> names the copy it is drawn from.</summary>
        private ImageRequirement Require(Guid image, PixelWindow window, int width, int height)
        {
            var drawn = new ImageRequirement(image, window, width, height);
            if (!windows.Contains(drawn))
            {
                windows.Add(drawn);
            }

            return drawn;
        }

        /// <summary>Whether <paramref name="outer"/> is a window of the same image, measured at the same size, holding the whole of <paramref name="inner"/>.</summary>
        private static bool Holds(ImageRequirement outer, ImageRequirement inner) =>
            outer.Image == inner.Image && outer.SourceWidth == inner.SourceWidth && outer.SourceHeight == inner.SourceHeight
                && outer.Window.X <= inner.Window.X && outer.Window.Y <= inner.Window.Y
                && outer.Window.X + outer.Window.Width >= inner.Window.X + inner.Window.Width
                && outer.Window.Y + outer.Window.Height >= inner.Window.Y + inner.Window.Height;

        /// <summary>Whether a footprint meets the Profile View's bounds (the canvas and every component's painted bounds) with some area.</summary>
        private bool Meets((Vector2 Min, Vector2 Max) footprint) =>
            float.IsFinite(footprint.Min.X) && float.IsFinite(footprint.Min.Y) && float.IsFinite(footprint.Max.X) && float.IsFinite(footprint.Max.Y)
                ? footprint.Max.X > view.Min.X && footprint.Min.X < view.Max.X && footprint.Max.Y > view.Min.Y && footprint.Min.Y < view.Max.Y
                : true;

        /// <summary>A text's box moved by its Height, grown by what its drawn outline and shadow extend past it (as the renderer's clip is).</summary>
        private static (Vector2 Min, Vector2 Max) TextFootprint(TextProfileElement text)
        {
            var outline = text.OutlineEnabled && text.OutlineThickness > 0f ? Math.Max(1f, Math.Min(text.OutlineThickness, TextProfileElement.MaxOutlineThickness)) : 0f;
            var shadow = text.ShadowEnabled ? Math.Max(Math.Abs(text.ShadowOffsetX), Math.Abs(text.ShadowOffsetY)) : 0f;
            var margin = new Vector2(outline + shadow);
            var position = text.Position + new Vector2(0f, text.DrawnVerticalOffset);
            return (position - margin, position + text.Size + margin);
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
        private readonly IReadOnlyDictionary<ImageRequirement, ImagePreparation> preparations;
        private readonly List<PlateSnapshotProblem> problems;
        private readonly List<ImageReference> used = new();
        private readonly List<ReadOnlyMemory<byte>> usedBytes = new();

        // Each required copy once checked: its asset id, or null when it isn't its window's.
        private readonly Dictionary<ImageRequirement, AssetId?> checkedCopies = new();
        private int textScalars;
        private int refusals;

        // What the values being mapped belong to when no element is named: the component whose
        // shape they draw, or the background.
        private PlateComponent? currentComponent;
        private bool inBackground;

        internal Mapper(IReadOnlyDictionary<ImageRequirement, ImagePreparation> preparations, IReadOnlyList<PlateSnapshotProblem> earlier)
        {
            this.preparations = preparations;
            problems = new List<PlateSnapshotProblem>(earlier);
        }

        internal List<PlateSnapshotProblem> Problems => problems;

        /// <summary>Components whose image turned out missing when it was prepared: they draw nothing, as in the renderer.</summary>
        internal List<LeftOutItem> LeftOut { get; } = new();

        internal IReadOnlyList<ImageReference> UsedImages => used;

        internal IReadOnlyList<ReadOnlyMemory<byte>> UsedBytes => usedBytes;

        internal void Refuse(PlateSnapshotRefusal refusal, ProfileElement? element)
        {
            // Counted before the problem is deduplicated, so a value refused again (another shape
            // out of range, say) still stops its own item from being built.
            refusals++;
            var problem = element is null ? new PlateSnapshotProblem(refusal, null, currentComponent, inBackground) : new PlateSnapshotProblem(refusal, element);
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
            inBackground = true;
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
                if (resolved.Image is not { } requirement)
                {
                    Refuse(PlateSnapshotRefusal.BackgroundImageMissing, null);
                }
                else if (Use(requirement, null) is { } asset)
                {
                    image = asset;
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
                _ => Unexpressible(LayoutTexture.None, null),
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
            inBackground = false;
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
            currentComponent = (step as ResolvedShape)?.Component;
            var item = step switch
            {
                ResolvedText text => Text(text),
                ResolvedImage image => Image(image),
                ResolvedShape shape => Shape(shape),
                _ => null,
            };

            currentComponent = null;
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

            // The renderer draws bold and italic only in a font with those faces, and a family it
            // lacks in Dalamud's default font, which has neither.
            var font = ProfileFontCatalog.Resolve(text.FontFamily);
            var flags = LayoutTextFlags.None;
            flags |= resolved.Wrap ? LayoutTextFlags.Wrap : LayoutTextFlags.None;
            flags |= text.Bold && font.SupportsBold ? LayoutTextFlags.Bold : LayoutTextFlags.None;
            flags |= text.Italic && font.SupportsItalic ? LayoutTextFlags.Italic : LayoutTextFlags.None;
            flags |= text.Underline ? LayoutTextFlags.Underline : LayoutTextFlags.None;
            flags |= text.Strikethrough ? LayoutTextFlags.Strikethrough : LayoutTextFlags.None;
            flags |= resolved.AutoFit ? LayoutTextFlags.AutoFit : LayoutTextFlags.None;
            flags |= text.OutlineEnabled ? LayoutTextFlags.Outline : LayoutTextFlags.None;
            flags |= text.ShadowEnabled ? LayoutTextFlags.Shadow : LayoutTextFlags.None;

            var before = refusals;
            // Height moves the drawn text and its clip, never the box a Name Backing follows (whose
            // steps are already planned), so a viewer draws it as the text's box moved by Height.
            var position = Point(text.Position + new Vector2(0f, text.DrawnVerticalOffset), text);
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
                position, width, height, resolved.Text, font.Id, fontSize, color, align, verticalAlign, flags,
                letterSpacing, lineSpacing, autoFitMinimum, outlineColor, outlineThickness, shadowColor, shadowX, shadowY,
                text.UsesLegacyLayout ? LayoutTextLayout.Legacy : LayoutTextLayout.Current);
        }

        private LayoutItem? Image(ResolvedImage resolved)
        {
            var image = resolved.Element;
            if (Use(resolved.Image, image) is not { } asset)
            {
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
                    return shape.Image is { } requirement && Use(requirement, null) is { } asset ? new LayoutImageQuad(asset, a, b, c, d, color) : null;

                case ComponentPrimitiveKind.Art when shape.Art is { } art:
                    return new LayoutArtQuad(art, a, b, c, d, color);

                default:
                    return null;
            }
        }

        /// <summary>
        /// The prepared copy a step draws from, named in the snapshot once however often it is
        /// drawn. Its bytes are copied first, and the copy is what is checked and kept, so nothing
        /// the caller does to its own buffer later changes either. Null when there is none, after
        /// recording why: an image whose file is missing is left out when a component draws it
        /// (the renderer draws nothing there) and refused when an element or the background does
        /// (it draws a placeholder); any other reason, or a copy that isn't its window's, refuses
        /// the Plate.
        /// </summary>
        private AssetId? Use(ImageRequirement requirement, ProfileElement? element)
        {
            var preparation = preparations[requirement];
            if (preparation.Copy is { } copy)
            {
                if (!checkedCopies.TryGetValue(requirement, out var asset))
                {
                    var bytes = copy.Bytes.ToArray();
                    asset = null;
                    if (IsCopyOf(copy.Reference, bytes, requirement))
                    {
                        // Declared under an asset id of this candidate's own (N1): a preparation
                        // may be used again, a candidate's ids never are.
                        var declared = copy.Reference;
                        var own = new ImageReference(AssetId.NewId(), declared.Sha256, declared.Format, declared.ByteLength, declared.Width, declared.Height);
                        asset = own.AssetId;
                        used.Add(own);
                        usedBytes.Add(bytes);
                    }

                    checkedCopies[requirement] = asset;
                }

                if (asset is null)
                {
                    Refuse(PlateSnapshotRefusal.ImageUnshareable, element);
                }

                return asset;
            }

            switch (preparation.Reason)
            {
                case ImageUnavailableReason.Missing when currentComponent is { } component:
                    var missing = new LeftOutItem(null, component, LeftOutReason.ImageMissing);
                    if (!LeftOut.Contains(missing))
                    {
                        LeftOut.Add(missing);
                    }

                    break;

                case ImageUnavailableReason.Missing:
                    Refuse(inBackground ? PlateSnapshotRefusal.BackgroundImageMissing : PlateSnapshotRefusal.ImageMissing, element);
                    break;

                case ImageUnavailableReason.TooLarge:
                    Refuse(PlateSnapshotRefusal.ImageTooLarge, element);
                    break;

                case ImageUnavailableReason.SizeChanged:
                    Refuse(PlateSnapshotRefusal.ImageSizeChanged, element);
                    break;

                default:
                    Refuse(PlateSnapshotRefusal.ImageUnshareable, element);
                    break;
            }

            return null;
        }

        /// <summary>Whether <paramref name="bytes"/> are a copy of its window: that window's size, and the byte length and digest its declaration gives.</summary>
        private static bool IsCopyOf(ImageReference reference, byte[] bytes, ImageRequirement requirement)
        {
            if (reference.Width != requirement.Window.Width || reference.Height != requirement.Window.Height || bytes.Length != reference.ByteLength)
            {
                return false;
            }

            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(bytes, digest);
            return CryptographicOperations.FixedTimeEquals(digest, reference.Sha256);
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
