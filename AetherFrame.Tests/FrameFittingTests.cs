using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Protocol.Remote;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Frames that fit (the owner's request of October 2, 2026): every Art Style's Plate Frame and Portrait
/// Frame is cut to fit any box (<see cref="ArtFrameSlices"/>), its rails on the edges of what it frames,
/// the Plate's or the drawn picture's, its corners and ornaments undistorted and reaching past them.
/// </summary>
public class FrameFittingTests
{
    private const float Tolerance = 0.01f;

    public static IEnumerable<object[]> FrameIds() =>
        BuiltInArtCatalog.All.Where(a => a.Kind is PlateComponentKind.PlateFrame or PlateComponentKind.PortraitFrame).Select(a => new object[] { a.Id });

    public static IEnumerable<object[]> PlateFrameDefinitionIds() =>
        BuiltInComponentCatalog.OfKind(PlateComponentKind.PlateFrame).Where(d => d.Art is not null).Select(d => new object[] { d.Id });

    // ---- The cuts --------------------------------------------------------------------------------

    [Fact]
    public void EveryArtStylesFrames_AreCutToFit_AndNothingElseIs()
    {
        foreach (var style in ArtSets.Styles)
        {
            foreach (var id in style.Components)
            {
                var art = BuiltInComponentCatalog.Find(id)!.Art!;
                var frame = art.Kind is PlateComponentKind.PlateFrame or PlateComponentKind.PortraitFrame;
                Assert.Equal(frame, ComponentPaintPlan.IsFramed(art));
                Assert.True(art.Frame is null || art.Slices is null, art.Id);
            }
        }

        Assert.All(BuiltInArtCatalog.All.Where(a => a.Frame is not null), a => Assert.True(a.Kind is PlateComponentKind.PlateFrame or PlateComponentKind.PortraitFrame, a.Id));
        Assert.Equal(82, BuiltInArtCatalog.All.Count(ComponentPaintPlan.IsFramed));
    }

    /// <summary>The drawing is the frame's opaque bounds (alpha over 16); its rails' outer edges (the
    /// content) are where the drawing starts and ends across its plain fills; and the middle between
    /// the caps, which is never drawn, is clear: what tools/art/measure_frames.py measured and checked.</summary>
    [Theory]
    [MemberData(nameof(FrameIds))]
    public void AFramesDrawing_ItsRails_AndItsClearMiddle_AreWhatItsPixelsSay(string artId)
    {
        var art = BuiltInArtCatalog.Find(artId)!;
        var frame = art.Frame!;
        var image = Decode(art);

        var (left, top, right, bottom) = (image.Width, image.Height, 0, 0);
        var middle = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var alpha = Alpha(image, x, y);
                if (alpha > 16)
                {
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x + 1), Math.Max(bottom, y + 1));
                }

                if (x >= frame.Columns.CapLeft && x < frame.Columns.CapRight && y >= frame.Rows.CapLeft && y < frame.Rows.CapRight)
                {
                    middle = Math.Max(middle, alpha);
                }
            }
        }

        Assert.Equal(new ArtBounds(left, top, right, bottom), frame.Drawing);
        Assert.True(middle <= 8, $"{artId}: the middle reaches alpha {middle}");

        // The rails: the median start and end of the drawing across the other axis's fills.
        var columnFills = Enumerable.Range(frame.Columns.CapLeft, frame.Columns.CenterLeft - frame.Columns.CapLeft)
            .Concat(Enumerable.Range(frame.Columns.CenterRight, frame.Columns.CapRight - frame.Columns.CenterRight)).ToList();
        var rowFills = Enumerable.Range(frame.Rows.CapLeft, frame.Rows.CenterLeft - frame.Rows.CapLeft)
            .Concat(Enumerable.Range(frame.Rows.CenterRight, frame.Rows.CapRight - frame.Rows.CenterRight)).ToList();
        Assert.Equal(frame.Rows.ContentLeft, Median(columnFills.Select(x => Enumerable.Range(0, image.Height).First(y => Alpha(image, x, y) > 16))));
        Assert.Equal(frame.Rows.ContentRight, Median(columnFills.Select(x => Enumerable.Range(0, image.Height).Last(y => Alpha(image, x, y) > 16) + 1)));
        Assert.Equal(frame.Columns.ContentLeft, Median(rowFills.Select(y => Enumerable.Range(0, image.Width).First(x => Alpha(image, x, y) > 16))));
        Assert.Equal(frame.Columns.ContentRight, Median(rowFills.Select(y => Enumerable.Range(0, image.Width).Last(x => Alpha(image, x, y) > 16) + 1)));
    }

    /// <summary>
    /// A fill stretches, so every line of it must look like the next: no line differs from the one six
    /// pixels further in by more than 48 levels in more than 12 pixels (premultiplied). Embroidered
    /// Tapestry's Plate Frame (a woven rail) and Celestial Sakura's frames (ornamented all along, cut
    /// by hand where least busy) are the measured exceptions.
    /// </summary>
    [Theory]
    [MemberData(nameof(FrameIds))]
    public void AFramesFills_ArePlainRail(string artId)
    {
        if (artId is "af.asset.embroidered-tapestry.plate-frame.standard" || artId.StartsWith("af.asset.celestial-sakura.", StringComparison.Ordinal))
        {
            return;
        }

        var art = BuiltInArtCatalog.Find(artId)!;
        var image = Decode(art);
        foreach (var vertical in new[] { false, true })
        {
            foreach (var band in new[] { 1, 3 })
            {
                var (start, end) = art.Frame!.Band(vertical, band);
                for (var line = start; line + 6 < end; line++)
                {
                    var differing = DifferingPixels(image, line, line + 6, vertical);
                    Assert.True(differing <= 12, $"{artId}: {(vertical ? "row" : "column")} {line} differs in {differing} pixels");
                }
            }
        }
    }

    // ---- On the Plate ----------------------------------------------------------------------------

    /// <summary>On the Plate's own shape and on another, a Plate Frame's rails lie on every edge of the
    /// Plate (what reaches past them, past it), every cell of its border is drawn, edge to edge, its
    /// corners keep the artwork's proportions, and its clear middle isn't drawn.</summary>
    [Theory]
    [MemberData(nameof(PlateFrameDefinitionIds))]
    public void APlateFrame_MeetsEveryEdgeOfThePlate_ItsCornersUndistorted(string definitionId)
    {
        foreach (var (width, height) in new[] { (1280f, 720f), (1200f, 800f), (1600f, 900f) })
        {
            var document = ComponentDocuments.WithAnchors();
            document.CanvasWidth = width;
            document.CanvasHeight = height;
            document.Components = [ComponentDocuments.Of(definitionId)];
            var step = Assert.Single(ComponentDocuments.Plan(document), s => !s.IsElement);
            Assert.Equal(new ElementRect(Vector2.Zero, new Vector2(width, height)), step.Placement.Rect);

            var art = step.Definition!.Art!;
            var frame = art.Frame!;
            var cells = Primitives(document, step);
            AssertWholeBorder(frame, cells);
            AssertRailsOn(art, cells, new ElementRect(Vector2.Zero, new Vector2(width, height)));
            Assert.DoesNotContain(cells, c => Covers(c, new Vector2(width, height) / 2f));

            foreach (var corner in new[] { (0, 0), (0, 4), (4, 0), (4, 4) })
            {
                var cell = Assert.Single(cells, c => c.Piece == ArtPieces.Frame(corner.Item1, corner.Item2));
                var (x0, x1) = frame.Band(rows: false, corner.Item2);
                var (y0, y1) = frame.Band(rows: true, corner.Item1);
                Assert.Equal((float)(x1 - x0) / (y1 - y0), (cell.B.X - cell.A.X) / (cell.D.Y - cell.A.Y), 2);
            }

            // What reaches past the rails is the drawing's, and the Plate's visual bounds include it.
            var (min, max) = ComponentPaintPlan.GetVisualBounds(ComponentDocuments.Plan(document), step.Component!)!.Value;
            Assert.Equal(cells.Min(c => c.A.X), min.X, Tolerance);
            Assert.Equal(cells.Min(c => c.A.Y), min.Y, Tolerance);
            Assert.Equal(cells.Max(c => c.C.X), max.X, Tolerance);
            Assert.Equal(cells.Max(c => c.C.Y), max.Y, Tolerance);
        }
    }

    /// <summary>On the shape it was drawn for, a frame's plain fills stretch by at most a quarter (by
    /// under two and a half times for Celestial Sakura's few hand-cut pixels), and never shrink.</summary>
    [Theory]
    [MemberData(nameof(FrameIds))]
    public void OnItsOwnShape_AFrame_LooksAsDrawn(string artId)
    {
        var art = BuiltInArtCatalog.Find(artId)!;
        var frame = art.Frame!;
        var box = art.Kind == PlateComponentKind.PlateFrame ? new Vector2(1280f, 720f) : new Vector2(400f, 640f);
        var scale = frame.ScaleFor(box);
        var limit = artId.StartsWith("af.asset.celestial-sakura.", StringComparison.Ordinal) ? 2.3f : 1.25f;

        Span<float> edges = stackalloc float[ArtFrameSlices.Bands + 1];
        foreach (var (rows, length) in new[] { (false, box.X), (true, box.Y) })
        {
            frame.Edges(rows, length, scale, edges);
            var (start, end) = frame.Band(rows, 1);
            var stretch = (edges[2] - edges[1]) / ((end - start) * scale);
            Assert.InRange(stretch, 1f - Tolerance, limit);
        }
    }

    [Fact]
    public void Edges_LayTheRailsOnTheBox_KeepTheFixedBandsAtTheScale_AndReachPastTheBox()
    {
        // Rails at 10 and 700, the drawing from 0 to 720; fixed 100 + 100 + 100 inside the rails, fills 190 + 200.
        var axis = new ArtSlices(10, 110, 300, 400, 600, 700);
        var frame = new ArtFrameSlices(axis, axis, new ArtBounds(0, 0, 720, 720));
        Span<float> edges = stackalloc float[6];

        frame.Edges(rows: false, 690f, 1f, edges); // 390 px of fill, 390 to share: as drawn
        Assert.Equal(new[] { -10f, 100f, 290f, 390f, 590f, 710f }, edges.ToArray());

        frame.Edges(rows: false, 1080f, 1f, edges); // twice the fill
        Assert.Equal(new[] { -10f, 100f, 480f, 580f, 980f, 1100f }, edges.ToArray());

        frame.Edges(rows: false, 150f, 1f, edges); // narrower than the fixed bands: squeezed, no fill
        Assert.Equal(new[] { -5f, 50f, 50f, 100f, 100f, 160f }, edges.ToArray());

        Assert.Equal(1f, frame.ScaleFor(new Vector2(690f, 1380f)));
    }

    // ---- On the portrait ---------------------------------------------------------------------------

    /// <summary>In Fit mode a picture of another shape is drawn smaller, centered: the portrait's frame
    /// and overlay lie on it. In Fill (the default) and Stretch, and while its size is unknown, on the box.</summary>
    [Theory]
    [InlineData(ProfileImageFit.Fit, 1920f, 1080f, 40f, 247.5f, 400f, 225f)]
    [InlineData(ProfileImageFit.Fit, 400f, 1280f, 140f, 40f, 200f, 640f)]
    [InlineData(ProfileImageFit.Fill, 1920f, 1080f, 40f, 40f, 400f, 640f)]
    [InlineData(ProfileImageFit.Stretch, 1920f, 1080f, 40f, 40f, 400f, 640f)]
    public void APortraitFrame_LiesOnTheDrawnPicture(ProfileImageFit fit, float pixelsX, float pixelsY, float x, float y, float w, float h)
    {
        var document = ComponentDocuments.WithAnchors();
        var portrait = (ImageProfileElement)document.Elements.Single(e => e.Role == ProfileElementRole.BasicPortrait);
        portrait.DisplayMode = fit;
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameCelestialSakura);
        var overlay = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitOverlayVignette);
        document.Components = [frame, overlay];

        var plan = Plan(document, _ => new Vector2(pixelsX, pixelsY));

        var expected = new ElementRect(new Vector2(x, y), new Vector2(w, h));
        Assert.Equal(expected, Assert.Single(plan, s => ReferenceEquals(s.Component, frame)).Placement.Rect);
        Assert.Equal(expected, Assert.Single(plan, s => ReferenceEquals(s.Component, overlay)).Placement.Rect);
    }

    [Fact]
    public void APortraitFrame_WithoutThePicturesSize_LiesOnTheBox_AndTurnsWithIt()
    {
        var document = ComponentDocuments.WithAnchors();
        var portrait = (ImageProfileElement)document.Elements.Single(e => e.Role == ProfileElementRole.BasicPortrait);
        portrait.DisplayMode = ProfileImageFit.Fit;
        portrait.RotationDegrees = 15f;
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameCelestialSakura);
        document.Components = [frame];

        var unknown = Assert.Single(Plan(document, _ => null), s => ReferenceEquals(s.Component, frame)).Placement;
        Assert.Equal(new ElementRect(portrait.Position, portrait.Size), unknown.Rect);

        var known = Assert.Single(Plan(document, _ => new Vector2(1920f, 1080f)), s => ReferenceEquals(s.Component, frame)).Placement;
        Assert.Equal(15f, known.RotationDegrees);
        Assert.Equal(portrait.Position + (portrait.Size / 2f), known.Rect.Position + (known.Rect.Size / 2f));
    }

    /// <summary>The frame meets the picture's edges whatever its shape: on a square picture its rails lie
    /// on the picture's edges, every cell of its border drawn.</summary>
    [Fact]
    public void APortraitFrame_OnASquarePicture_MeetsItsEdges()
    {
        var document = ComponentDocuments.WithAnchors();
        var portrait = (ImageProfileElement)document.Elements.Single(e => e.Role == ProfileElementRole.BasicPortrait);
        portrait.DisplayMode = ProfileImageFit.Fit;
        var definitionId = "af.portrait-frame.frontier-silver";
        var frame = ComponentDocuments.Of(definitionId);
        document.Components = [frame];

        var step = Assert.Single(Plan(document, _ => new Vector2(1000f, 1000f)), s => ReferenceEquals(s.Component, frame));
        var cells = Primitives(document, step);

        var picture = new ElementRect(new Vector2(40f, 160f), new Vector2(400f, 400f));
        Assert.Equal(picture, step.Placement.Rect);
        AssertWholeBorder(step.Definition!.Art!.Frame!, cells);
        AssertRailsOn(step.Definition!.Art!, cells, picture);
    }

    [Fact]
    public void PictureFit_IsTheRenderersFit()
    {
        foreach (var (box, pixels) in new[] { (new Vector2(400, 640), new Vector2(1920, 1080)), (new Vector2(400, 640), new Vector2(300, 900)), (new Vector2(500, 500), new Vector2(500, 500)) })
        {
            var layout = ImageFitLayout.Compute(ProfileImageFit.Fit, box, pixels, ImageFitLayout.FullSource, false, false);
            var size = PictureFit.Size(box, pixels);
            Assert.Equal(layout.DrawMax.X - layout.DrawMin.X, size.X, 3);
            Assert.Equal(layout.DrawMax.Y - layout.DrawMin.Y, size.Y, 3);
            Assert.Equal((box - size) / 2f, layout.DrawMin);
        }
    }

    // ---- Shared, and drawn ----------------------------------------------------------------------------

    /// <summary>A shared Plate names each border cell by its own ident, which a viewer finds again; the
    /// middle, a cell of unframed art and a horizontal piece of a frame are not pieces.</summary>
    [Theory]
    [MemberData(nameof(FrameIds))]
    public void AFramesCells_AreSharedByTheirOwnIdents_AndFoundAgain(string artId)
    {
        var art = BuiltInArtCatalog.Find(artId)!;
        foreach (var piece in ArtPieces.FrameBorder)
        {
            var ident = art.PieceIdent(piece);
            Assert.Equal<(BuiltInArtAsset, ArtPiece)?>((art, piece), BuiltInArtCatalog.FindPiece(ident));
            _ = new LayoutArtQuad(ident, default, default, default, default, default); // a valid wire ident
        }

        Assert.Null(BuiltInArtCatalog.FindPiece(art.Id + ".frame-r2c2"));
        Assert.Null(BuiltInArtCatalog.FindPiece(art.Id + ".left-cap"));
        Assert.Null(BuiltInArtCatalog.FindPiece(BuiltInArtCatalog.CelestialSakuraNameplateArt.Id + ".frame-r0c0"));
    }

    [Fact]
    public void AFramesCell_DrawsItsBandsOfTheTexture()
    {
        var art = BuiltInArtCatalog.Find("af.asset.frontier-silver.plate-frame.standard")!;
        var frame = art.Frame!;

        var (u0, v0, u1, v1) = art.Window2D(ArtPieces.Frame(4, 1));
        Assert.Equal((float)frame.Columns.CapLeft / art.PixelWidth, u0, 5);
        Assert.Equal((float)frame.Columns.CenterLeft / art.PixelWidth, u1, 5);
        Assert.Equal((float)frame.Rows.CapRight / art.PixelHeight, v0, 5);
        Assert.Equal((float)frame.Drawing.Bottom / art.PixelHeight, v1, 5);

        var (cornerU0, cornerV0, _, _) = art.Window2D(ArtPieces.Frame(0, 0));
        Assert.Equal(((float)frame.Drawing.Left / art.PixelWidth, (float)frame.Drawing.Top / art.PixelHeight), (cornerU0, cornerV0));

        Assert.Equal((0f, 0f, 1f, 1f), art.Window2D(ArtPiece.Whole));
    }

    /// <summary>Every cell of one frame draws from the same level of the artwork, the one its corners need.</summary>
    [Theory]
    [MemberData(nameof(PlateFrameDefinitionIds))]
    public void EveryCellOfAFrame_DrawsFromTheSameLevel(string definitionId)
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(definitionId)];
        var step = Assert.Single(ComponentDocuments.Plan(document), s => !s.IsElement);
        var art = step.Definition!.Art!;

        var sizes = Primitives(document, step).Select(c => art.ScreenPixels(c.Piece, c.A, c.B, c.D)).ToList();
        Assert.All(sizes, s => Assert.Equal(sizes[0], s, sizes[0] * 0.01f));
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static List<PaintStep> Plan(ProfileDocument document, Func<ImageProfileElement, Vector2?> imageSize)
    {
        var steps = new List<PaintStep>();
        ComponentPaintPlan.Build(document, document.Elements.Where(e => e.Visible).OrderBy(e => e.ZIndex).ToList(), BuiltInComponentCatalog.Instance, steps, imageSize: imageSize);
        return steps;
    }

    private static List<ComponentPrimitive> Primitives(ProfileDocument document, PaintStep step)
    {
        var primitives = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, step.Component!, step.Definition!, step.Placement, primitives);
        return primitives;
    }

    /// <summary>Every border cell with an extent is drawn, once, and each row's and column's cells join
    /// with no gap: the frame is whole.</summary>
    private static void AssertWholeBorder(ArtFrameSlices frame, List<ComponentPrimitive> cells)
    {
        var expected = ArtPieces.FrameBorder.Where(p =>
        {
            var (row, column) = ArtPieces.FrameCell(p);
            var (x0, x1) = frame.Band(rows: false, column);
            var (y0, y1) = frame.Band(rows: true, row);
            return x1 > x0 && y1 > y0;
        }).ToList();
        Assert.Equal(expected, cells.Select(c => c.Piece).ToList());

        foreach (var row in new[] { 0, ArtFrameSlices.Bands - 1 })
        {
            var line = cells.Where(c => ArtPieces.FrameCell(c.Piece).Row == row).OrderBy(c => ArtPieces.FrameCell(c.Piece).Column).ToList();
            for (var i = 1; i < line.Count; i++)
            {
                Assert.Equal(line[i - 1].B.X, line[i].A.X, Tolerance);
            }
        }

        foreach (var column in new[] { 0, ArtFrameSlices.Bands - 1 })
        {
            var line = cells.Where(c => ArtPieces.FrameCell(c.Piece).Column == column).OrderBy(c => ArtPieces.FrameCell(c.Piece).Row).ToList();
            for (var i = 1; i < line.Count; i++)
            {
                Assert.Equal(line[i - 1].D.Y, line[i].A.Y, Tolerance);
            }
        }
    }

    /// <summary>The rails' outer edges (the texture's content edges) fall on <paramref name="box"/>'s
    /// edges: where a fill cell of each rail maps its content edge to the canvas.</summary>
    private static void AssertRailsOn(BuiltInArtAsset art, List<ComponentPrimitive> cells, ElementRect box)
    {
        var frame = art.Frame!;
        float At(ComponentPrimitive cell, bool vertical, int pixel)
        {
            var (u0, v0, u1, v1) = art.Window2D(cell.Piece);
            return vertical
                ? cell.A.Y + ((cell.D.Y - cell.A.Y) * (((float)pixel / art.PixelHeight) - v0) / (v1 - v0))
                : cell.A.X + ((cell.B.X - cell.A.X) * (((float)pixel / art.PixelWidth) - u0) / (u1 - u0));
        }

        ComponentPrimitive Cell(int row, int column) => cells.Single(c => c.Piece == ArtPieces.Frame(row, column));

        Assert.Equal(box.Position.Y, At(Cell(0, 1), vertical: true, frame.Rows.ContentLeft), 0.05f);
        Assert.Equal(box.Position.Y + box.Size.Y, At(Cell(4, 1), vertical: true, frame.Rows.ContentRight), 0.05f);
        Assert.Equal(box.Position.X, At(Cell(1, 0), vertical: false, frame.Columns.ContentLeft), 0.05f);
        Assert.Equal(box.Position.X + box.Size.X, At(Cell(1, 4), vertical: false, frame.Columns.ContentRight), 0.05f);
    }

    private static int Alpha(ArtLevel image, int x, int y) => image.Rgba[(((y * image.Width) + x) * 4) + 3];

    private static int Median(IEnumerable<int> values)
    {
        var ordered = values.OrderBy(v => v).ToList();
        return ordered[ordered.Count / 2];
    }

    private static bool Covers(ComponentPrimitive quad, Vector2 point) =>
        point.X > Math.Min(quad.A.X, quad.C.X) && point.X < Math.Max(quad.A.X, quad.C.X) && point.Y > Math.Min(quad.A.Y, quad.C.Y) && point.Y < Math.Max(quad.A.Y, quad.C.Y);

    /// <summary>How many pixels of two lines (columns, or rows when <paramref name="vertical"/>) differ by
    /// more than 48 levels in a premultiplied channel.</summary>
    private static int DifferingPixels(ArtLevel image, int a, int b, bool vertical)
    {
        var count = 0;
        var length = vertical ? image.Width : image.Height;
        for (var i = 0; i < length; i++)
        {
            var p = vertical ? ((a * image.Width) + i) * 4 : ((i * image.Width) + a) * 4;
            var q = vertical ? ((b * image.Width) + i) * 4 : ((i * image.Width) + b) * 4;
            int pa = image.Rgba[p + 3], qa = image.Rgba[q + 3];
            var worst = Math.Abs(pa - qa);
            for (var c = 0; c < 3; c++)
            {
                worst = Math.Max(worst, Math.Abs((image.Rgba[p + c] * pa / 255) - (image.Rgba[q + c] * qa / 255)));
            }

            if (worst > 48)
            {
                count++;
            }
        }

        return count;
    }

    private static ArtLevel Decode(BuiltInArtAsset art)
    {
        using var stream = typeof(FrameFittingTests).Assembly.GetManifestResourceStream(art.ResourceName);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream!.CopyTo(buffer);
        return BundledArtImage.DecodePng(buffer.ToArray());
    }
}
