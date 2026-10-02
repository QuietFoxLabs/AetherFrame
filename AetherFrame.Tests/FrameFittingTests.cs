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
/// Frame is cut to fit any box (<see cref="ArtFrameSlices"/>), its drawing's edges on the edges of what
/// it frames, the Plate's or the drawn picture's, its corners and ornaments undistorted.
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
        Assert.Equal(80, BuiltInArtCatalog.All.Count(ComponentPaintPlan.IsFramed));
    }

    /// <summary>The drawing's edges are its opaque bounds (alpha over 16), and the middle between the
    /// caps, which is never drawn, is clear: what tools/art/measure_frames.py measured and checked.</summary>
    [Theory]
    [MemberData(nameof(FrameIds))]
    public void AFramesDrawing_IsItsOpaqueBounds_AndItsMiddleIsClear(string artId)
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
                var alpha = image.Rgba[(((y * image.Width) + x) * 4) + 3];
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

        Assert.Equal((frame.Columns.ContentLeft, frame.Columns.ContentRight), (left, right));
        Assert.Equal((frame.Rows.ContentLeft, frame.Rows.ContentRight), (top, bottom));
        Assert.True(middle <= 8, $"{artId}: the middle reaches alpha {middle}");
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
        foreach (var (axis, vertical) in new[] { (art.Frame!.Columns, false), (art.Frame.Rows, true) })
        {
            foreach (var band in new[] { 1, 3 })
            {
                var (start, end) = ArtFrameSlices.Band(axis, band);
                for (var line = start; line + 6 < end; line++)
                {
                    var differing = DifferingPixels(image, line, line + 6, vertical);
                    Assert.True(differing <= 12, $"{artId}: {(vertical ? "row" : "column")} {line} differs in {differing} pixels");
                }
            }
        }
    }

    // ---- On the Plate ----------------------------------------------------------------------------

    /// <summary>On the Plate's own shape and on another, a Plate Frame's drawing meets every edge of the
    /// Plate, its corners keep the artwork's proportions, and its clear middle isn't drawn.</summary>
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

            var cells = Primitives(document, step);
            Assert.All(cells, c => Assert.True(ArtPieces.IsFrame(c.Piece) && ArtFrameSlices.IsBorder(ArtPieces.FrameCell(c.Piece).Row, ArtPieces.FrameCell(c.Piece).Column)));
            Assert.Equal(0f, cells.Min(c => c.A.X), Tolerance);
            Assert.Equal(0f, cells.Min(c => c.A.Y), Tolerance);
            Assert.Equal(width, cells.Max(c => c.C.X), Tolerance);
            Assert.Equal(height, cells.Max(c => c.C.Y), Tolerance);
            Assert.DoesNotContain(cells, c => Covers(c, new Vector2(width, height) / 2f));

            var frame = step.Definition!.Art!.Frame!;
            foreach (var corner in new[] { (0, 0), (0, 4), (4, 0), (4, 4) })
            {
                var cell = Assert.Single(cells, c => c.Piece == ArtPieces.Frame(corner.Item1, corner.Item2));
                var (x0, x1) = ArtFrameSlices.Band(frame.Columns, corner.Item2);
                var (y0, y1) = ArtFrameSlices.Band(frame.Rows, corner.Item1);
                Assert.Equal((float)(x1 - x0) / (y1 - y0), (cell.B.X - cell.A.X) / (cell.D.Y - cell.A.Y), 2);
            }
        }
    }

    /// <summary>On the shape it was drawn for, a frame looks as drawn: its fills stretch by at most 15%
    /// (by under a third for Celestial Sakura's few hand-cut pixels).</summary>
    [Theory]
    [MemberData(nameof(FrameIds))]
    public void OnItsOwnShape_AFrame_LooksAsDrawn(string artId)
    {
        var art = BuiltInArtCatalog.Find(artId)!;
        var frame = art.Frame!;
        var box = art.Kind == PlateComponentKind.PlateFrame ? new Vector2(1280f, 720f) : new Vector2(400f, 640f);
        var scale = Math.Min(box.X / (frame.Columns.ContentRight - frame.Columns.ContentLeft), box.Y / (frame.Rows.ContentRight - frame.Rows.ContentLeft));
        var limit = artId.StartsWith("af.asset.celestial-sakura.", StringComparison.Ordinal) ? 1.35f : 1.15f;

        Span<float> edges = stackalloc float[ArtFrameSlices.Bands + 1];
        foreach (var (axis, length) in new[] { (frame.Columns, box.X), (frame.Rows, box.Y) })
        {
            ArtFrameSlices.Edges(axis, length, scale, edges);
            var (start, end) = ArtFrameSlices.Band(axis, 1);
            var stretch = (edges[2] - edges[1]) / ((end - start) * scale);
            Assert.InRange(stretch, 1f - Tolerance, limit);
        }
    }

    [Fact]
    public void Edges_KeepTheFixedBandsAtTheScale_AndEndExactlyAtTheBox()
    {
        var axis = new ArtSlices(10, 110, 300, 400, 600, 700); // fixed 100 + 100 + 100, fills 190 + 200
        Span<float> edges = stackalloc float[6];

        ArtFrameSlices.Edges(axis, 690f, 1f, edges); // 390 px of fill, 390 to share: as drawn
        Assert.Equal(new[] { 0f, 100f, 290f, 390f, 590f, 690f }, edges.ToArray());

        ArtFrameSlices.Edges(axis, 1080f, 1f, edges); // twice the fill
        Assert.Equal(new[] { 0f, 100f, 480f, 580f, 980f, 1080f }, edges.ToArray());

        ArtFrameSlices.Edges(axis, 150f, 1f, edges); // narrower than the fixed bands: squeezed, no fill
        Assert.Equal(new[] { 0f, 50f, 50f, 100f, 100f, 150f }, edges.ToArray());
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

    /// <summary>The frame meets the picture's edges whatever its shape: a frame on a square picture is
    /// square, corners undistorted, its drawing exactly over the picture.</summary>
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

        Assert.Equal(new ElementRect(new Vector2(40f, 160f), new Vector2(400f, 400f)), step.Placement.Rect);
        Assert.Equal(40f, cells.Min(c => c.A.X), Tolerance);
        Assert.Equal(160f, cells.Min(c => c.A.Y), Tolerance);
        Assert.Equal(440f, cells.Max(c => c.C.X), Tolerance);
        Assert.Equal(560f, cells.Max(c => c.C.Y), Tolerance);
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
        Assert.Equal((float)frame.Rows.ContentRight / art.PixelHeight, v1, 5);

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
