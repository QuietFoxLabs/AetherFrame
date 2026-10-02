using System;
using System.Collections.Generic;
using System.Numerics;

namespace AetherFrame.Domain.Components;

/// <summary>
/// A piece of artwork bundled inside the plugin assembly, drawn by a graphical built-in
/// <see cref="ComponentDefinition"/> (<see cref="ComponentShape.Art"/>). Compile-time data like the
/// definitions themselves: nothing about it is persisted, so a Plate never stores a path, a file
/// name or image bytes — only the definition id that selects it.
/// </summary>
/// <param name="Id">Stable, frozen-forever logical id ("af.asset.&lt;family&gt;.&lt;kind&gt;.&lt;name&gt;"),
/// independent of where or how the image is bundled. Never reused.</param>
/// <param name="Name">Display label only.</param>
/// <param name="Kind">The one Component kind this artwork is drawn for.</param>
/// <param name="ResourceName">Manifest resource name of the runtime PNG, as the plugin assembly embeds
/// it when it carries the artwork (not a filesystem path; see <see cref="AssetPath"/> for where a
/// downloaded copy comes from). Always an 8-bit RGBA (or, for opaque art, RGB) PNG (see <c>BundledArtImage</c>).</param>
/// <param name="PixelWidth">The runtime PNG's width, in pixels.</param>
/// <param name="PixelHeight">The runtime PNG's height, in pixels. The artwork is drawn at this aspect
/// ratio (fitted inside its placement box, never visibly stretched: see <c>ComponentPaintPlan</c>),
/// except where it is cut to fit: <see cref="Slices"/> and <see cref="Frame"/>, whose caps and
/// ornaments still keep it.</param>
/// <param name="Tintable">True when the artwork is white/greyscale and takes the Component's color;
/// false draws its own colors, with only the color's alpha applied.</param>
/// <param name="DefaultOpacity">Alpha of the definition's default color.</param>
/// <param name="CornerPlacement">For Corner Ornaments: how the one (top-left) drawing serves the
/// other three corners.</param>
/// <param name="SizeFactor">Size of the artwork's placement box relative to its kind's standard
/// procedural box (artwork needs more room than a line mark to read): a Corner Ornament's square,
/// anchored at the same corner; a Name Backing's or Divider's box, around the same center. Unused
/// (1) for the kinds whose art fills its whole anchor: Background, Plate Frame, Portrait Frame.</param>
public sealed record BuiltInArtAsset(
    string Id,
    string Name,
    PlateComponentKind Kind,
    string ResourceName,
    int PixelWidth,
    int PixelHeight,
    bool Tintable,
    float DefaultOpacity,
    CornerArtPlacement CornerPlacement,
    float SizeFactor)
{
    /// <summary>Width over height of the runtime artwork (1 for square art).</summary>
    public float AspectRatio => PixelHeight > 0 ? (float)PixelWidth / PixelHeight : 1f;

    /// <summary>
    /// The runtime PNG's path below the plugin project's Assets folder, with '/' separators
    /// ("Components/AllaganTech/AllaganTech_Background.png"): <see cref="ResourceName"/> without its
    /// prefix, since no folder or file name there holds a dot but the extension's. Where a hosted copy
    /// lives (<see cref="ArtFiles"/>); never persisted.
    /// </summary>
    public string AssetPath { get; } = AssetPathOf(ResourceName);

    /// <summary>How the artwork stretches to any width, or null when it is always drawn whole at its
    /// own aspect ratio. Name Backings, Dividers and Section Headers can be sliced (see <c>ComponentPaintPlan</c>).
    /// The cuts are part of what a shared Plate's piece ident (<see cref="PieceIdent"/>) means to another
    /// viewer, so they are frozen with the artwork's id: different cuts need a new id.</summary>
    public ArtSlices? Slices { get; init; }

    /// <summary>How a frame (a Plate Frame or a Portrait Frame) is cut so its drawing meets the edges
    /// of any box, or null when it is drawn whole at its own aspect ratio. Frozen with the artwork's
    /// id like <see cref="Slices"/>, for the same reason.</summary>
    public ArtFrameSlices? Frame { get; init; }

    private static string AssetPathOf(string resourceName)
    {
        var name = resourceName.StartsWith(BuiltInArtCatalog.ResourcePrefix, StringComparison.Ordinal)
            ? resourceName[BuiltInArtCatalog.ResourcePrefix.Length..]
            : resourceName;
        return name.EndsWith(".png", StringComparison.Ordinal) ? name[..^4].Replace('.', '/') + ".png" : name;
    }

    /// <summary>The horizontal span of <paramref name="piece"/> in texture coordinates (0 to 1);
    /// the whole width for <see cref="ArtPiece.Whole"/> and for any piece of unsliced artwork.</summary>
    public (float U0, float U1) Window(ArtPiece piece)
    {
        if (Slices is not { } slices || PixelWidth <= 0)
        {
            return (0f, 1f);
        }

        var (x0, x1) = piece switch
        {
            ArtPiece.LeftCap => (0, slices.CapLeft),
            ArtPiece.LeftFill => (slices.CapLeft, slices.CenterLeft),
            ArtPiece.Center => (slices.CenterLeft, slices.CenterRight),
            ArtPiece.RightFill => (slices.CenterRight, slices.CapRight),
            ArtPiece.RightCap => (slices.CapRight, PixelWidth),
            _ => (0, PixelWidth),
        };

        return ((float)x0 / PixelWidth, (float)x1 / PixelWidth);
    }

    /// <summary>The part of the texture <paramref name="piece"/> draws, in texture coordinates (0 to 1):
    /// a cell of the grid for a piece of a frame (<see cref="Frame"/>), else <see cref="Window"/>'s
    /// span over the whole height.</summary>
    public (float U0, float V0, float U1, float V1) Window2D(ArtPiece piece)
    {
        if (ArtPieces.IsFrame(piece))
        {
            if (Frame is not { } frame || PixelWidth <= 0 || PixelHeight <= 0)
            {
                return (0f, 0f, 1f, 1f);
            }

            var (row, column) = ArtPieces.FrameCell(piece);
            var (x0, x1) = frame.Band(rows: false, column);
            var (y0, y1) = frame.Band(rows: true, row);
            return ((float)x0 / PixelWidth, (float)y0 / PixelHeight, (float)x1 / PixelWidth, (float)y1 / PixelHeight);
        }

        var (u0, u1) = Window(piece);
        return (u0, 0f, u1, 1f);
    }

    /// <summary>
    /// The on-screen size, in pixels, of the whole artwork a quad A-B-C-D (D below A) draws
    /// <paramref name="piece"/> of: what picks the level it draws from. A whole artwork's longer side;
    /// a piece of sliced artwork scales its full-height strip by the artwork's long side over its
    /// height; a cell of a frame scales by a side that keeps the artwork's proportions (a cap or the
    /// center piece). So every piece of one placement draws from the same level, and a stretched
    /// fill never needs a larger one.
    /// </summary>
    public float ScreenPixels(ArtPiece piece, Vector2 a, Vector2 b, Vector2 d)
    {
        var longSide = Math.Max(PixelWidth, PixelHeight);
        if (ArtPieces.IsFrame(piece) && Frame is { } frame && PixelWidth > 0 && PixelHeight > 0)
        {
            var (row, column) = ArtPieces.FrameCell(piece);
            var (y0, y1) = frame.Band(rows: true, row);
            var (x0, x1) = frame.Band(rows: false, column);
            if (ArtFrameSlices.IsFixed(row) && y1 > y0)
            {
                return Vector2.Distance(a, d) * longSide / (y1 - y0);
            }

            if (ArtFrameSlices.IsFixed(column) && x1 > x0)
            {
                return Vector2.Distance(a, b) * longSide / (x1 - x0);
            }
        }

        return piece == ArtPiece.Whole || ArtPieces.IsFrame(piece) || PixelHeight <= 0
            ? MathF.Max(Vector2.Distance(a, b), Vector2.Distance(a, d))
            : Vector2.Distance(a, d) * longSide / PixelHeight;
    }

    /// <summary>The ident a shared Plate names <paramref name="piece"/> by: <see cref="Id"/> for the whole
    /// artwork, else the id and the piece's suffix ("[id].left-cap"). See <see cref="BuiltInArtCatalog.FindPiece"/>.</summary>
    public string PieceIdent(ArtPiece piece) => piece == ArtPiece.Whole ? Id : Id + "." + ArtPieces.Suffix(piece);

    /// <summary>The visual family the artwork was designed in, shown with it wherever Components are
    /// browsed ("Celestial Sakura"); null for a standalone piece. Display and grouping only.</summary>
    public string? Family { get; init; }

    /// <summary>Extra words the artwork answers to in a search (motifs, colors, its role), lower case.
    /// Display metadata only: nothing is ever resolved by them.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>True when <paramref name="query"/> (trimmed, case-insensitive) occurs in the name, the
    /// family or a keyword; an empty query matches everything.</summary>
    public bool MatchesSearch(string? query)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (Name.Contains(text, StringComparison.OrdinalIgnoreCase) || (Family?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return true;
        }

        foreach (var keyword in Keywords)
        {
            if (keyword.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Where a horizontal artwork (a nameplate, a divider) is cut so it fits any width: five pieces,
/// left to right — a left cap, a fill, a center piece, a fill, a right cap. The caps and the center
/// piece are always drawn at the artwork's own proportions; only the two fills stretch, equally, so
/// a short name gets a compact plaque and a long one a long plaque, and no ornament is ever
/// distorted. A fill must therefore look the same in every column (plain rails and a plain band).
/// Every value is an x position in the runtime PNG's pixels, in order:
/// 0 &lt;= <paramref name="ContentLeft"/> &lt;= <paramref name="CapLeft"/> &lt;= <paramref name="CenterLeft"/>
/// &lt;= <paramref name="CenterRight"/> &lt;= <paramref name="CapRight"/> &lt;= <paramref name="ContentRight"/>
/// &lt;= the artwork's width. Artwork without a center piece has <paramref name="CenterLeft"/> equal
/// to <paramref name="CenterRight"/>.
/// </summary>
/// <param name="ContentLeft">Where the text area starts: the anchor box (the name and title, padded)
/// spans <paramref name="ContentLeft"/> to <paramref name="ContentRight"/>. For a Divider or a
/// Section Header, about where its left cap ends: the caps reach past the line it decorates.</param>
/// <param name="CapLeft">Where the left cap ends and the left fill starts.</param>
/// <param name="CenterLeft">Where the left fill ends and the center piece starts.</param>
/// <param name="CenterRight">Where the center piece ends and the right fill starts.</param>
/// <param name="CapRight">Where the right fill ends and the right cap starts.</param>
/// <param name="ContentRight">Where the text area ends.</param>
public sealed record ArtSlices(int ContentLeft, int CapLeft, int CenterLeft, int CenterRight, int CapRight, int ContentRight)
{
    /// <summary>True when every value is in order inside an artwork <paramref name="pixelWidth"/> wide,
    /// and both fills are at least a pixel wide (each stretches).</summary>
    public bool IsValidFor(int pixelWidth) =>
        ContentLeft >= 0 && ContentLeft <= CapLeft && CapLeft < CenterLeft && CenterLeft <= CenterRight
        && CenterRight < CapRight && CapRight <= ContentRight && ContentRight <= pixelWidth;

    /// <summary>The pixels that never stretch: the caps and the center piece.</summary>
    public int FixedWidth(int pixelWidth) => CapLeft + (CenterRight - CenterLeft) + (pixelWidth - CapRight);

    /// <summary>The pixels outside the text area, on both sides together.</summary>
    public int OutsideContent(int pixelWidth) => ContentLeft + (pixelWidth - ContentRight);
}

/// <summary>The bounds of an artwork's drawing, in its pixels: left and top inclusive, right and bottom exclusive.</summary>
public readonly record struct ArtBounds(int Left, int Top, int Right, int Bottom);

/// <summary>
/// Where a frame (a Plate Frame or a Portrait Frame) is cut so it fits any box: an
/// <see cref="ArtSlices"/> for its columns (x positions), one for its rows (y positions), so a grid
/// of five bands each way, and the bounds of its <paramref name="Drawing"/>. On each axis
/// <c>ContentLeft</c> to <c>ContentRight</c> is the outer edge of its rails, laid on the edges of the
/// box it frames, so the frame runs along them; what reaches past its rails (corner ornaments, a
/// crest) reaches past the box, out to the drawing's bounds. The caps and the center piece (a
/// mid-edge ornament, when the frame has one) keep the artwork's proportions, and only the two fills
/// stretch, so corners and ornaments are never distorted. Only the cells on the border are drawn:
/// every frame is clear between its caps on both axes (tools/art/measure_frames.py checks it).
/// </summary>
public sealed record ArtFrameSlices(ArtSlices Columns, ArtSlices Rows, ArtBounds Drawing)
{
    /// <summary>The bands on each axis: a cap, a fill, the center piece, a fill and a cap.</summary>
    public const int Bands = 5;

    /// <summary>True when both axes are in order inside an artwork of this size, each with fills that
    /// stretch, and the drawing reaches at least to the rails.</summary>
    public bool IsValidFor(int pixelWidth, int pixelHeight) =>
        Columns.IsValidFor(pixelWidth) && Rows.IsValidFor(pixelHeight)
        && Drawing.Left >= 0 && Drawing.Left <= Columns.ContentLeft && Columns.ContentRight <= Drawing.Right && Drawing.Right <= pixelWidth
        && Drawing.Top >= 0 && Drawing.Top <= Rows.ContentLeft && Rows.ContentRight <= Drawing.Bottom && Drawing.Bottom <= pixelHeight;

    /// <summary>True for a band that keeps the artwork's proportions: a cap or the center piece.</summary>
    public static bool IsFixed(int band) => band is 0 or 2 or 4;

    /// <summary>True for a cell on the frame's border (a cap row or a cap column): the cells drawn.</summary>
    public static bool IsBorder(int row, int column) => row is 0 or Bands - 1 || column is 0 or Bands - 1;

    /// <summary>The pixel span of <paramref name="band"/> (0 to 4) across the columns, or down the rows:
    /// the outer bands reach out to the drawing's bounds.</summary>
    public (int Start, int End) Band(bool rows, int band)
    {
        var axis = rows ? Rows : Columns;
        var (start, end) = rows ? (Drawing.Top, Drawing.Bottom) : (Drawing.Left, Drawing.Right);
        return band switch
        {
            0 => (start, axis.CapLeft),
            1 => (axis.CapLeft, axis.CenterLeft),
            2 => (axis.CenterLeft, axis.CenterRight),
            3 => (axis.CenterRight, axis.CapRight),
            _ => (axis.CapRight, end),
        };
    }

    /// <summary>The scale the caps keep in a box <paramref name="size"/> (logical units per pixel): the
    /// largest at which the rails fit inside it on both axes.</summary>
    public float ScaleFor(Vector2 size) =>
        Math.Min(size.X / (Columns.ContentRight - Columns.ContentLeft), size.Y / (Rows.ContentRight - Rows.ContentLeft));

    /// <summary>
    /// Where each band starts and ends across a box <paramref name="length"/> long (the columns, or
    /// the rows): six positions, from the drawing's start (at or before 0, by what reaches past the
    /// rail) through the four cuts to the drawing's end (at or after <paramref name="length"/>). The
    /// rails' outer edges lie exactly at 0 and <paramref name="length"/>. The fixed bands are at
    /// <paramref name="scale"/> per pixel (squeezed to fit when they are longer than the box, with no
    /// fill), and the fills share the rest in proportion to their own lengths, so at the shape the
    /// frame was drawn for every band keeps its place.
    /// </summary>
    public void Edges(bool rows, float length, float scale, Span<float> edges)
    {
        var axis = rows ? Rows : Columns;
        var (start, end) = rows ? (Drawing.Top, Drawing.Bottom) : (Drawing.Left, Drawing.Right);
        var fixedPixels = (axis.CapLeft - axis.ContentLeft) + (axis.CenterRight - axis.CenterLeft) + (axis.ContentRight - axis.CapRight);
        var fillPixels = (axis.CenterLeft - axis.CapLeft) + (axis.CapRight - axis.CenterRight);
        var fixedLength = fixedPixels * scale;
        var squeeze = fixedLength > length && fixedLength > 0f ? length / fixedLength : 1f;
        var perFixed = scale * squeeze;
        var perFill = fillPixels > 0 ? Math.Max(0f, length - (fixedLength * squeeze)) / fillPixels : 0f;

        edges[0] = -(axis.ContentLeft - start) * perFixed;
        edges[1] = (axis.CapLeft - axis.ContentLeft) * perFixed;
        edges[2] = edges[1] + ((axis.CenterLeft - axis.CapLeft) * perFill);
        edges[3] = edges[2] + ((axis.CenterRight - axis.CenterLeft) * perFixed);
        edges[4] = length - ((axis.ContentRight - axis.CapRight) * perFixed);
        edges[5] = length + ((end - axis.ContentRight) * perFixed);
    }
}

/// <summary>A part of an artwork, as a primitive draws it. Not persisted.</summary>
public enum ArtPiece
{
    /// <summary>The whole artwork (every unsliced artwork).</summary>
    Whole,
    LeftCap,
    LeftFill,
    Center,
    RightFill,
    RightCap,
}

/// <summary>The suffixes a shared Plate's art idents give the pieces of sliced artwork.</summary>
public static class ArtPieces
{
    /// <summary>The pieces of sliced artwork, left to right.</summary>
    public static readonly IReadOnlyList<ArtPiece> Sliced = [ArtPiece.LeftCap, ArtPiece.LeftFill, ArtPiece.Center, ArtPiece.RightFill, ArtPiece.RightCap];

    /// <summary>The ident suffix of <paramref name="piece"/>: lower case letters, digits and '-' only.
    /// Frozen once shipped, like the art ids themselves (a viewer looks pieces up by them).</summary>
    public static string Suffix(ArtPiece piece)
    {
        if (IsFrame(piece))
        {
            var (row, column) = FrameCell(piece);
            return FrameSuffixes[row][column];
        }

        return piece switch
        {
            ArtPiece.LeftCap => "left-cap",
            ArtPiece.LeftFill => "left-fill",
            ArtPiece.Center => "center",
            ArtPiece.RightFill => "right-fill",
            ArtPiece.RightCap => "right-cap",
            _ => string.Empty,
        };
    }

    // A frame's cells are pieces too, numbered from FrameBase, row by row ("frame-r0c4": the top
    // right corner). Computed rather than named: nothing persists an ArtPiece.
    private const int FrameBase = 100;
    private const int FrameCells = ArtFrameSlices.Bands * ArtFrameSlices.Bands;

    private static readonly string[][] FrameSuffixes = BuildFrameSuffixes();

    /// <summary>The cells a frame draws, row by row: those on its border (see <see cref="ArtFrameSlices.IsBorder"/>).</summary>
    public static readonly IReadOnlyList<ArtPiece> FrameBorder = BuildFrameBorder();

    /// <summary>The piece for the cell at <paramref name="row"/> and <paramref name="column"/> (each 0 to 4) of a frame.</summary>
    public static ArtPiece Frame(int row, int column) =>
        row is >= 0 and < ArtFrameSlices.Bands && column is >= 0 and < ArtFrameSlices.Bands
            ? (ArtPiece)(FrameBase + (row * ArtFrameSlices.Bands) + column)
            : throw new ArgumentOutOfRangeException(row is >= 0 and < ArtFrameSlices.Bands ? nameof(column) : nameof(row));

    /// <summary>True for a cell of a frame (<see cref="Frame"/>).</summary>
    public static bool IsFrame(ArtPiece piece) => (int)piece is >= FrameBase and < FrameBase + FrameCells;

    /// <summary>The row and column of a frame's cell.</summary>
    public static (int Row, int Column) FrameCell(ArtPiece piece) =>
        IsFrame(piece)
            ? Math.DivRem((int)piece - FrameBase, ArtFrameSlices.Bands)
            : throw new ArgumentOutOfRangeException(nameof(piece));

    private static string[][] BuildFrameSuffixes()
    {
        var suffixes = new string[ArtFrameSlices.Bands][];
        for (var row = 0; row < ArtFrameSlices.Bands; row++)
        {
            suffixes[row] = new string[ArtFrameSlices.Bands];
            for (var column = 0; column < ArtFrameSlices.Bands; column++)
            {
                suffixes[row][column] = $"frame-r{row}c{column}";
            }
        }

        return suffixes;
    }

    private static ArtPiece[] BuildFrameBorder()
    {
        var border = new List<ArtPiece>();
        for (var row = 0; row < ArtFrameSlices.Bands; row++)
        {
            for (var column = 0; column < ArtFrameSlices.Bands; column++)
            {
                if (ArtFrameSlices.IsBorder(row, column))
                {
                    border.Add(Frame(row, column));
                }
            }
        }

        return [.. border];
    }
}

/// <summary>How a corner drawing, designed for the top-left corner, is placed in the other corners.</summary>
public enum CornerArtPlacement
{
    /// <summary>Mirrored horizontally and/or vertically (what the procedural corner marks do).</summary>
    Mirror,

    /// <summary>Rotated by 90/180/270 degrees, so asymmetric details keep their handedness.</summary>
    Rotate,
}

/// <summary>The artwork AetherFrame knows. Compile-time: each artwork is inside the plugin assembly
/// or, since art on demand, downloaded the first time a player uses it and checked against
/// <see cref="ArtFiles"/>. No user packs.</summary>
public static class BuiltInArtCatalog
{
    public const string CelestialDreamAstrolabePivot = "af.asset.celestial-dream.corner-ornament.astrolabe-pivot";

    public const string CelestialSakuraBackground = "af.asset.celestial-sakura.background.twilight";
    public const string CelestialSakuraPlateFrame = "af.asset.celestial-sakura.plate-frame.blossom";
    public const string CelestialSakuraPortraitFrame = "af.asset.celestial-sakura.portrait-frame.blossom";
    public const string CelestialSakuraNameplate = "af.asset.celestial-sakura.name-backing.nameplate";
    public const string CelestialSakuraOrnateDivider = "af.asset.celestial-sakura.divider.ornate";
    public const string CelestialSakuraSlimDivider = "af.asset.celestial-sakura.divider.slim";
    public const string CelestialSakuraCornerOrnament = "af.asset.celestial-sakura.corner-ornament.blossom";

    /// <summary>The family name every Celestial Sakura piece carries (<see cref="BuiltInArtAsset.Family"/>).</summary>
    public const string CelestialSakuraFamily = "Celestial Sakura";

    /// <summary>Prefix of every bundled manifest resource name (see the plugin project's Assets folder).</summary>
    public const string ResourcePrefix = "AetherFrame.Assets.";

    private const string CelestialSakuraResources = ResourcePrefix + "Components.CelestialSakura.";

    public static readonly BuiltInArtAsset AstrolabePivot = new(
        CelestialDreamAstrolabePivot,
        "Astrolabe Pivot",
        PlateComponentKind.CornerOrnament,
        ResourcePrefix + "Components.CelestialDream.CornerOrnaments.AstrolabePivot.png",
        PixelWidth: 512,
        PixelHeight: 512,
        Tintable: true,
        DefaultOpacity: 0.9f,
        CornerPlacement: CornerArtPlacement.Rotate,
        SizeFactor: 2f);

    // Celestial Sakura: full-color artwork (champagne gold filigree, blush cherry blossoms, pearls,
    // a crescent moon) bundled exactly as approved — never tinted, at its generated resolution. The
    // Plate-sized pieces are 16:9 like the Adventure Plate canvas, the Portrait Frame 5:8 like its
    // portrait; each is drawn at its own aspect ratio (see Assets/README.md for the measurements).

    /// <summary>The words every Celestial Sakura piece answers to, before its own role words. Declared
    /// before the pieces: static fields initialize in order.</summary>
    private static readonly string[] CelestialSakuraKeywords = ArtKeywords.CelestialSakura;

    /// <summary>A twilight sky with cherry branches and a crescent moon, covering the whole Plate (opaque).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraBackgroundArt = CelestialSakura(
        CelestialSakuraBackground, "Celestial Sakura", PlateComponentKind.Background, "CelestialSakura_Background.png", 1672, 941, 1f,
        "background", "sky", "twilight", "landscape");

    /// <summary>A gold filigree border with blossom corners and a crescent crest, its rails laid on the
    /// Plate's edges and its ornaments reaching past (cut where it is least busy: it is ornamented all
    /// along; see ArtFrameData).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraPlateFrameArt = CelestialSakura(
        CelestialSakuraPlateFrame, "Celestial Sakura", PlateComponentKind.PlateFrame, "CelestialSakura_PlateFrame.png", 1672, 941, 1f,
        "frame", "plate frame", "border") with
    {
        Frame = ArtFrameData.ByFolder["CelestialSakura"].PlateFrame,
    };

    /// <summary>A slim gold portrait border with blossoms at opposing corners, its rails laid on the
    /// picture's edges and its ornaments reaching past.</summary>
    public static readonly BuiltInArtAsset CelestialSakuraPortraitFrameArt = CelestialSakura(
        CelestialSakuraPortraitFrame, "Celestial Sakura", PlateComponentKind.PortraitFrame, "CelestialSakura_PortraitFrame.png", 992, 1586, 1f,
        "frame", "portrait", "portrait frame", "border") with
    {
        Frame = ArtFrameData.ByFolder["CelestialSakura"].PortraitFrame,
    };

    /// <summary>An ivory enamel plaque with blossom ends, behind the name. 1.5x the name backing's height
    /// (the plaque's rails and ornaments surround a text area about a third of its height), and as
    /// wide as the name: sliced between the blossom ends and the crescent crest, where the plaque is
    /// only its plain rails and band (see Assets/README.md for the measurements).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraNameplateArt = CelestialSakura(
        CelestialSakuraNameplate, "Celestial Sakura", PlateComponentKind.NameBacking, "CelestialSakura_Nameplate.png", 2172, 724, 1.5f,
        "nameplate", "name", "plaque", "banner") with
    {
        Slices = new ArtSlices(ContentLeft: 340, CapLeft: 512, CenterLeft: 760, CenterRight: 1400, CapRight: 1672, ContentRight: 1840),
    };

    /// <summary>The primary divider: curling gold with blossom clusters and a crescent-set gem. 3:1, in a
    /// band 3x the procedural Divider's height: its drawing fills two thirds of that band, which keeps
    /// the crescent clear of the name above and the first section heading below on the Classic layout.</summary>
    public static readonly BuiltInArtAsset CelestialSakuraOrnateDividerArt = CelestialSakura(
        CelestialSakuraOrnateDivider, "Celestial Sakura Ornate", PlateComponentKind.Divider, "CelestialSakura_Divider_Ornate.png", 2172, 724, 3f,
        "divider", "ornate", "line");

    /// <summary>The secondary divider: a fine tapering gold line with one star and one blossom, in a band
    /// 4x the procedural Divider's height (only its small star rises above the line, so it can be wider).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraSlimDividerArt = CelestialSakura(
        CelestialSakuraSlimDivider, "Celestial Sakura Slim", PlateComponentKind.Divider, "CelestialSakura_Divider_Slim.png", 2172, 724, 4f,
        "divider", "slim", "line", "star");

    /// <summary>An L-shaped blossom cluster with a crescent, drawn for the top-left corner and mirrored
    /// into the others (so its hanging crystals hang down in both top corners), 3x the procedural box.</summary>
    public static readonly BuiltInArtAsset CelestialSakuraCornerOrnamentArt = CelestialSakura(
        CelestialSakuraCornerOrnament, "Celestial Sakura", PlateComponentKind.CornerOrnament, "CelestialSakura_CornerOrnament.png", 1254, 1254, 3f,
        "corner", "ornament", "corner ornament");

    public static readonly IReadOnlyList<BuiltInArtAsset> All =
    [
        AstrolabePivot,
        CelestialSakuraBackgroundArt,
        CelestialSakuraPlateFrameArt,
        CelestialSakuraPortraitFrameArt,
        CelestialSakuraNameplateArt,
        CelestialSakuraOrnateDividerArt,
        CelestialSakuraSlimDividerArt,
        CelestialSakuraCornerOrnamentArt,
        .. ArtSets.Assets,
    ];

    private static readonly Dictionary<string, BuiltInArtAsset> ById = BuildIndex();

    /// <summary>The artwork with this exact id, or null (ids are case-sensitive).</summary>
    public static BuiltInArtAsset? Find(string? id) => id is not null && ById.TryGetValue(id, out var art) ? art : null;

    /// <summary>
    /// The artwork and piece a shared Plate's art ident names (<see cref="BuiltInArtAsset.PieceIdent"/>):
    /// an exact id is the whole artwork; an id followed by a piece's suffix is that piece of a sliced
    /// artwork, or that cell of a frame's border. Null for anything else, a piece of unsliced
    /// artwork included. Exact matches only, like <see cref="Find"/>.
    /// </summary>
    public static (BuiltInArtAsset Art, ArtPiece Piece)? FindPiece(string? ident)
    {
        if (Find(ident) is { } whole)
        {
            return (whole, ArtPiece.Whole);
        }

        if (ident is null)
        {
            return null;
        }

        foreach (var piece in ArtPieces.Sliced)
        {
            var suffix = "." + ArtPieces.Suffix(piece);
            if (ident.EndsWith(suffix, StringComparison.Ordinal) && Find(ident[..^suffix.Length]) is { Slices: { } slices } art && slices.IsValidFor(art.PixelWidth))
            {
                return (art, piece);
            }
        }

        foreach (var piece in ArtPieces.FrameBorder)
        {
            var suffix = "." + ArtPieces.Suffix(piece);
            if (ident.EndsWith(suffix, StringComparison.Ordinal) && Find(ident[..^suffix.Length]) is { Frame: { } frame } art && frame.IsValidFor(art.PixelWidth, art.PixelHeight))
            {
                return (art, piece);
            }
        }

        return null;
    }

    /// <summary>Every artwork of one family, in catalog order (display grouping; never used to resolve).</summary>
    public static IEnumerable<BuiltInArtAsset> OfFamily(string family)
    {
        foreach (var art in All)
        {
            if (string.Equals(art.Family, family, StringComparison.Ordinal))
            {
                yield return art;
            }
        }
    }

    private static BuiltInArtAsset CelestialSakura(string id, string name, PlateComponentKind kind, string file, int width, int height, float sizeFactor, params string[] roleKeywords) =>
        new(id, name, kind, CelestialSakuraResources + file, width, height, Tintable: false, DefaultOpacity: 1f, CornerArtPlacement.Mirror, sizeFactor)
        {
            Family = CelestialSakuraFamily,
            Keywords = [.. CelestialSakuraKeywords, .. roleKeywords],
        };

    private static Dictionary<string, BuiltInArtAsset> BuildIndex()
    {
        var index = new Dictionary<string, BuiltInArtAsset>(StringComparer.Ordinal);
        foreach (var art in All)
        {
            index.Add(art.Id, art); // throws on a duplicate id: caught by any test run
        }

        return index;
    }
}

/// <summary>Search words more than one catalog uses: a class of its own, so the catalogs share them
/// without depending on each other's static initialization.</summary>
internal static class ArtKeywords
{
    /// <summary>The words every Celestial Sakura piece answers to, before its own role words.</summary>
    internal static readonly string[] CelestialSakura =
        ["celestial sakura", "sakura", "cherry blossom", "blossom", "moon", "crescent", "rose", "pink", "gold", "pearl"];
}
