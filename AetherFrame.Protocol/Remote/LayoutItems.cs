using System;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// One entry of a schema 2 layout's paint list (docs/networking/ProtocolSpecification-v1.md,
/// section 8.5): something a viewer draws, already resolved by the publisher. The set of kinds is
/// closed (the constructor is private protected); each is validated on construction and immutable.
/// </summary>
public abstract class LayoutItem
{
    private const byte AllFlips = (byte)(LayoutFlips.Horizontal | LayoutFlips.Vertical);

    private protected LayoutItem()
    {
    }

    /// <summary>What the item is.</summary>
    public abstract LayoutItemKind Kind { get; }

    /// <summary>The snapshot image the item draws, or the empty id when it draws none.</summary>
    internal virtual AssetId DrawnAsset => default;

    /// <summary>The scalar values of the item's text, or 0 when it has none.</summary>
    internal virtual int TextScalars => 0;

    internal void Write(CanonicalWriter writer)
    {
        writer.WriteU8((byte)Kind);
        WriteFields(writer);
    }

    private protected abstract void WriteFields(CanonicalWriter writer);

    /// <summary>Reads one item: its kind, then that kind's fields, each checked as soon as it is read.</summary>
    internal static LayoutItem Read(ref CanonicalReader reader)
    {
        var kind = (LayoutItemKind)reader.ReadU8("item.kind");
        return kind switch
        {
            LayoutItemKind.Text => LayoutText.ReadFields(ref reader),
            LayoutItemKind.Image => LayoutImage.ReadFields(ref reader),
            LayoutItemKind.Quad => new LayoutQuad(LayoutPoint.Read(ref reader, "quad.a"), LayoutPoint.Read(ref reader, "quad.b"), LayoutPoint.Read(ref reader, "quad.c"), LayoutPoint.Read(ref reader, "quad.d"), LayoutColor.Read(ref reader, "quad.color")),
            LayoutItemKind.Triangle => new LayoutTriangle(LayoutPoint.Read(ref reader, "triangle.a"), LayoutPoint.Read(ref reader, "triangle.b"), LayoutPoint.Read(ref reader, "triangle.c"), LayoutColor.Read(ref reader, "triangle.color")),
            LayoutItemKind.ImageQuad => ReadImageQuad(ref reader),
            LayoutItemKind.ArtQuad => ReadArtQuad(ref reader),
            _ => throw new ProtocolException(ProtocolError.InvalidValue, $"Layout item kind {ProtocolText.Number((byte)kind)} is not known."),
        };
    }

    private protected static void CheckFlips(LayoutFlips flips, string field) => LayoutFields.CheckFlags((byte)flips, field, AllFlips);

    private protected static LayoutFlips ReadFlips(ref CanonicalReader reader, string field) => (LayoutFlips)LayoutFields.ReadFlags(ref reader, field, AllFlips);

    private protected static AssetId CheckAsset(AssetId assetId, string field)
    {
        if (assetId.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"The layout field '{field}' names no image.");
        }

        return assetId;
    }

    private protected static void WriteAsset(CanonicalWriter writer, AssetId assetId)
    {
        Span<byte> id = stackalloc byte[ProtocolConstants.OpaqueIdLength];
        assetId.WriteBytes(id);
        writer.WriteFixed(id);
    }

    private static LayoutImageQuad ReadImageQuad(ref CanonicalReader reader)
    {
        var assetId = AssetId.FromBytes(reader.ReadFixed(ProtocolConstants.OpaqueIdLength, "imageQuad.assetId"));
        return new LayoutImageQuad(assetId, LayoutPoint.Read(ref reader, "imageQuad.a"), LayoutPoint.Read(ref reader, "imageQuad.b"), LayoutPoint.Read(ref reader, "imageQuad.c"), LayoutPoint.Read(ref reader, "imageQuad.d"), LayoutColor.Read(ref reader, "imageQuad.tint"));
    }

    private static LayoutArtQuad ReadArtQuad(ref CanonicalReader reader)
    {
        var art = LayoutFields.ReadIdent(ref reader, "artQuad.art");
        return new LayoutArtQuad(art, LayoutPoint.Read(ref reader, "artQuad.a"), LayoutPoint.Read(ref reader, "artQuad.b"), LayoutPoint.Read(ref reader, "artQuad.c"), LayoutPoint.Read(ref reader, "artQuad.d"), LayoutColor.Read(ref reader, "artQuad.tint"));
    }
}

/// <summary>A text: its box, its final display text, its font and its style.</summary>
public sealed class LayoutText : LayoutItem
{
    /// <summary>The smallest font size, in hundredths of a canvas unit (1 unit, the smallest a local Plate draws).</summary>
    public const int MinFontSize = 100;

    /// <summary>The largest font size, in hundredths of a canvas unit (1,024 units, as a local Plate allows).</summary>
    public const int MaxFontSize = 102_400;

    /// <summary>The largest letter spacing either way, in hundredths of a canvas unit (10,000 units).</summary>
    public const int MaxLetterSpacing = 1_000_000;

    /// <summary>The largest line spacing either way, in hundredths of the font size (a multiple of 10,000).</summary>
    public const int MaxLineSpacing = 1_000_000;

    /// <summary>The thickest outline, in hundredths of a canvas unit.</summary>
    public const int MaxOutlineThickness = 1_600;

    /// <summary>The largest shadow offset either way, in hundredths of a canvas unit.</summary>
    public const int MaxShadowOffset = 4_000;

    private readonly int textScalars;

    /// <summary>Builds a text item, refusing any value outside its range.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>, <see cref="ProtocolError.InvalidText"/>, <see cref="ProtocolError.InvalidLength"/> or <see cref="ProtocolError.LimitExceeded"/>.</exception>
    public LayoutText(
        LayoutPoint position,
        int width,
        int height,
        string text,
        string font,
        int fontSize,
        LayoutColor color,
        LayoutHorizontalAlign align,
        LayoutVerticalAlign verticalAlign,
        LayoutTextFlags flags,
        int letterSpacing,
        int lineSpacing,
        int autoFitMinimum,
        LayoutColor outlineColor,
        int outlineThickness,
        LayoutColor shadowColor,
        int shadowX,
        int shadowY,
        LayoutTextLayout layout)
    {
        ArgumentNullException.ThrowIfNull(text);
        position.Check("text.position");
        LayoutFields.CheckRange(width, 0, ProtocolLimits.MaxLayoutExtent, "text.width");
        LayoutFields.CheckRange(height, 0, ProtocolLimits.MaxLayoutExtent, "text.height");
        ProtocolText.Encode(text, "text.text", ProtocolLimits.MaxLayoutItemTextScalars);
        font = LayoutFields.CheckIdent(font, "text.font");
        LayoutFields.CheckRange(fontSize, MinFontSize, MaxFontSize, "text.fontSize");
        LayoutFields.CheckCode((byte)align, "text.align", (byte)LayoutHorizontalAlign.Right);
        LayoutFields.CheckCode((byte)verticalAlign, "text.verticalAlign", (byte)LayoutVerticalAlign.Bottom);
        LayoutFields.CheckRange(letterSpacing, -MaxLetterSpacing, MaxLetterSpacing, "text.letterSpacing");
        LayoutFields.CheckRange(lineSpacing, -MaxLineSpacing, MaxLineSpacing, "text.lineSpacing");
        LayoutFields.CheckRange(autoFitMinimum, MinFontSize, MaxFontSize, "text.autoFitMinimum");
        LayoutFields.CheckRange(outlineThickness, 0, MaxOutlineThickness, "text.outlineThickness");
        LayoutFields.CheckRange(shadowX, -MaxShadowOffset, MaxShadowOffset, "text.shadowX");
        LayoutFields.CheckRange(shadowY, -MaxShadowOffset, MaxShadowOffset, "text.shadowY");
        LayoutFields.CheckCode((byte)layout, "text.layout", (byte)LayoutTextLayout.Current);

        Position = position;
        Width = width;
        Height = height;
        Text = text;
        Font = font;
        FontSize = fontSize;
        Color = color;
        Align = align;
        VerticalAlign = verticalAlign;
        Flags = flags;
        LetterSpacing = letterSpacing;
        LineSpacing = lineSpacing;
        AutoFitMinimum = autoFitMinimum;
        OutlineColor = outlineColor;
        OutlineThickness = outlineThickness;
        ShadowColor = shadowColor;
        ShadowX = shadowX;
        ShadowY = shadowY;
        Layout = layout;
        textScalars = ProtocolText.ScalarCount(text);
    }

    /// <inheritdoc />
    public override LayoutItemKind Kind => LayoutItemKind.Text;

    /// <summary>The box's top left corner.</summary>
    public LayoutPoint Position { get; }

    /// <summary>The box's width, in hundredths of a canvas unit.</summary>
    public int Width { get; }

    /// <summary>The box's height, in hundredths of a canvas unit.</summary>
    public int Height { get; }

    /// <summary>What is drawn, exactly as the publisher resolved it (affixes included). Plain text (decision N7).</summary>
    public string Text { get; }

    /// <summary>The font family's identifier; a viewer that does not bundle it draws a placeholder.</summary>
    public string Font { get; }

    /// <summary>The font size, in hundredths of a canvas unit.</summary>
    public int FontSize { get; }

    /// <summary>The text's colour.</summary>
    public LayoutColor Color { get; }

    /// <summary>The horizontal alignment.</summary>
    public LayoutHorizontalAlign Align { get; }

    /// <summary>The vertical alignment.</summary>
    public LayoutVerticalAlign VerticalAlign { get; }

    /// <summary>The text's switches: wrapping, style, auto-fit, outline and shadow.</summary>
    public LayoutTextFlags Flags { get; }

    /// <summary>Extra space between letters, in hundredths of a canvas unit; negative draws letters closer.</summary>
    public int LetterSpacing { get; }

    /// <summary>The line spacing as a multiple of the font size, in hundredths (100 is single spacing).</summary>
    public int LineSpacing { get; }

    /// <summary>The smallest font size auto-fit shrinks to, in hundredths of a canvas unit; a viewer uses at most the font size.</summary>
    public int AutoFitMinimum { get; }

    /// <summary>The outline's colour, its opacity folded into the alpha.</summary>
    public LayoutColor OutlineColor { get; }

    /// <summary>The outline's thickness, in hundredths of a canvas unit.</summary>
    public int OutlineThickness { get; }

    /// <summary>The shadow's colour, its opacity folded into the alpha.</summary>
    public LayoutColor ShadowColor { get; }

    /// <summary>The shadow's horizontal offset, in hundredths of a canvas unit.</summary>
    public int ShadowX { get; }

    /// <summary>The shadow's vertical offset, in hundredths of a canvas unit.</summary>
    public int ShadowY { get; }

    /// <summary>Which text layout rules the text renders with.</summary>
    public LayoutTextLayout Layout { get; }

    internal override int TextScalars => textScalars;

    private protected override void WriteFields(CanonicalWriter writer)
    {
        Position.Write(writer);
        writer.WriteI32(Width);
        writer.WriteI32(Height);
        writer.WriteText(Text, "text.text", ProtocolLimits.MaxLayoutItemTextScalars);
        LayoutFields.WriteIdent(writer, Font);
        writer.WriteI32(FontSize);
        Color.Write(writer);
        writer.WriteU8((byte)Align);
        writer.WriteU8((byte)VerticalAlign);
        writer.WriteU8((byte)Flags);
        writer.WriteI32(LetterSpacing);
        writer.WriteI32(LineSpacing);
        writer.WriteI32(AutoFitMinimum);
        OutlineColor.Write(writer);
        writer.WriteU16((ushort)OutlineThickness);
        ShadowColor.Write(writer);
        writer.WriteI32(ShadowX);
        writer.WriteI32(ShadowY);
        writer.WriteU8((byte)Layout);
    }

    internal static LayoutText ReadFields(ref CanonicalReader reader)
    {
        var position = LayoutPoint.Read(ref reader, "text.position");
        var width = LayoutFields.ReadExtent(ref reader, "text.width", 0, ProtocolLimits.MaxLayoutExtent);
        var height = LayoutFields.ReadExtent(ref reader, "text.height", 0, ProtocolLimits.MaxLayoutExtent);
        var text = reader.ReadText("text.text", ProtocolLimits.MaxLayoutItemTextScalars);
        var font = LayoutFields.ReadIdent(ref reader, "text.font");
        var fontSize = LayoutFields.ReadI32In(ref reader, "text.fontSize", MinFontSize, MaxFontSize);
        var color = LayoutColor.Read(ref reader, "text.color");
        var align = (LayoutHorizontalAlign)LayoutFields.ReadCode(ref reader, "text.align", (byte)LayoutHorizontalAlign.Right);
        var verticalAlign = (LayoutVerticalAlign)LayoutFields.ReadCode(ref reader, "text.verticalAlign", (byte)LayoutVerticalAlign.Bottom);
        var flags = (LayoutTextFlags)reader.ReadU8("text.flags");
        var letterSpacing = LayoutFields.ReadI32In(ref reader, "text.letterSpacing", -MaxLetterSpacing, MaxLetterSpacing);
        var lineSpacing = LayoutFields.ReadI32In(ref reader, "text.lineSpacing", -MaxLineSpacing, MaxLineSpacing);
        var autoFitMinimum = LayoutFields.ReadI32In(ref reader, "text.autoFitMinimum", MinFontSize, MaxFontSize);
        var outlineColor = LayoutColor.Read(ref reader, "text.outlineColor");
        var outlineThickness = LayoutFields.ReadU16In(ref reader, "text.outlineThickness", 0, MaxOutlineThickness);
        var shadowColor = LayoutColor.Read(ref reader, "text.shadowColor");
        var shadowX = LayoutFields.ReadI32In(ref reader, "text.shadowX", -MaxShadowOffset, MaxShadowOffset);
        var shadowY = LayoutFields.ReadI32In(ref reader, "text.shadowY", -MaxShadowOffset, MaxShadowOffset);
        var layout = (LayoutTextLayout)LayoutFields.ReadCode(ref reader, "text.layout", (byte)LayoutTextLayout.Current);
        return new LayoutText(position, width, height, text, font, fontSize, color, align, verticalAlign, flags, letterSpacing, lineSpacing, autoFitMinimum, outlineColor, outlineThickness, shadowColor, shadowX, shadowY, layout);
    }
}

/// <summary>An image element: one of the snapshot's images in a box, fitted, flipped, rotated about its centre and faded.</summary>
public sealed class LayoutImage : LayoutItem
{
    /// <summary>Builds an image item, refusing any value outside its range.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public LayoutImage(AssetId assetId, LayoutPoint position, int width, int height, int rotation, LayoutImageFit fit, LayoutFlips flips, byte opacity)
    {
        CheckAsset(assetId, "image.assetId");
        position.Check("image.position");
        LayoutFields.CheckRange(width, 0, ProtocolLimits.MaxLayoutExtent, "image.width");
        LayoutFields.CheckRange(height, 0, ProtocolLimits.MaxLayoutExtent, "image.height");
        LayoutFields.CheckAngle(rotation, "image.rotation");
        LayoutFields.CheckCode((byte)fit, "image.fit", (byte)LayoutImageFit.Fill);
        CheckFlips(flips, "image.flips");

        AssetId = assetId;
        Position = position;
        Width = width;
        Height = height;
        Rotation = rotation;
        Fit = fit;
        Flips = flips;
        Opacity = opacity;
    }

    /// <inheritdoc />
    public override LayoutItemKind Kind => LayoutItemKind.Image;

    /// <summary>The snapshot image the element shows.</summary>
    public AssetId AssetId { get; }

    /// <summary>The box's top left corner, before rotation.</summary>
    public LayoutPoint Position { get; }

    /// <summary>The box's width, in hundredths of a canvas unit.</summary>
    public int Width { get; }

    /// <summary>The box's height, in hundredths of a canvas unit.</summary>
    public int Height { get; }

    /// <summary>The rotation about the box's centre, in hundredths of a degree.</summary>
    public int Rotation { get; }

    /// <summary>How the image fills its box.</summary>
    public LayoutImageFit Fit { get; }

    /// <summary>How the image is mirrored.</summary>
    public LayoutFlips Flips { get; }

    /// <summary>The image's opacity.</summary>
    public byte Opacity { get; }

    internal override AssetId DrawnAsset => AssetId;

    private protected override void WriteFields(CanonicalWriter writer)
    {
        WriteAsset(writer, AssetId);
        Position.Write(writer);
        writer.WriteI32(Width);
        writer.WriteI32(Height);
        writer.WriteI32(Rotation);
        writer.WriteU8((byte)Fit);
        writer.WriteU8((byte)Flips);
        writer.WriteU8(Opacity);
    }

    internal static LayoutImage ReadFields(ref CanonicalReader reader)
    {
        var assetId = AssetId.FromBytes(reader.ReadFixed(ProtocolConstants.OpaqueIdLength, "image.assetId"));
        var position = LayoutPoint.Read(ref reader, "image.position");
        var width = LayoutFields.ReadExtent(ref reader, "image.width", 0, ProtocolLimits.MaxLayoutExtent);
        var height = LayoutFields.ReadExtent(ref reader, "image.height", 0, ProtocolLimits.MaxLayoutExtent);
        var rotation = LayoutFields.ReadAngle(ref reader, "image.rotation");
        var fit = (LayoutImageFit)LayoutFields.ReadCode(ref reader, "image.fit", (byte)LayoutImageFit.Fill);
        var flips = ReadFlips(ref reader, "image.flips");
        var opacity = reader.ReadU8("image.opacity");
        return new LayoutImage(assetId, position, width, height, rotation, fit, flips, opacity);
    }
}

/// <summary>A filled quadrilateral of one colour, its corners clockwise from the top left.</summary>
public sealed class LayoutQuad : LayoutItem
{
    /// <summary>Builds a quad, refusing a corner outside the coordinate range.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public LayoutQuad(LayoutPoint a, LayoutPoint b, LayoutPoint c, LayoutPoint d, LayoutColor color)
    {
        a.Check("quad.a");
        b.Check("quad.b");
        c.Check("quad.c");
        d.Check("quad.d");
        A = a;
        B = b;
        C = c;
        D = d;
        Color = color;
    }

    /// <inheritdoc />
    public override LayoutItemKind Kind => LayoutItemKind.Quad;

    /// <summary>The top left corner.</summary>
    public LayoutPoint A { get; }

    /// <summary>The top right corner.</summary>
    public LayoutPoint B { get; }

    /// <summary>The bottom right corner.</summary>
    public LayoutPoint C { get; }

    /// <summary>The bottom left corner.</summary>
    public LayoutPoint D { get; }

    /// <summary>The fill colour.</summary>
    public LayoutColor Color { get; }

    private protected override void WriteFields(CanonicalWriter writer)
    {
        A.Write(writer);
        B.Write(writer);
        C.Write(writer);
        D.Write(writer);
        Color.Write(writer);
    }
}

/// <summary>A filled triangle of one colour.</summary>
public sealed class LayoutTriangle : LayoutItem
{
    /// <summary>Builds a triangle, refusing a corner outside the coordinate range.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public LayoutTriangle(LayoutPoint a, LayoutPoint b, LayoutPoint c, LayoutColor color)
    {
        a.Check("triangle.a");
        b.Check("triangle.b");
        c.Check("triangle.c");
        A = a;
        B = b;
        C = c;
        Color = color;
    }

    /// <inheritdoc />
    public override LayoutItemKind Kind => LayoutItemKind.Triangle;

    /// <summary>The first corner.</summary>
    public LayoutPoint A { get; }

    /// <summary>The second corner.</summary>
    public LayoutPoint B { get; }

    /// <summary>The third corner.</summary>
    public LayoutPoint C { get; }

    /// <summary>The fill colour.</summary>
    public LayoutColor Color { get; }

    private protected override void WriteFields(CanonicalWriter writer)
    {
        A.Write(writer);
        B.Write(writer);
        C.Write(writer);
        Color.Write(writer);
    }
}

/// <summary>One of the snapshot's images drawn into four corners, tinted (a Component's image).</summary>
public sealed class LayoutImageQuad : LayoutItem
{
    /// <summary>Builds an image quad, refusing an empty image or a corner outside the coordinate range.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public LayoutImageQuad(AssetId assetId, LayoutPoint a, LayoutPoint b, LayoutPoint c, LayoutPoint d, LayoutColor tint)
    {
        CheckAsset(assetId, "imageQuad.assetId");
        a.Check("imageQuad.a");
        b.Check("imageQuad.b");
        c.Check("imageQuad.c");
        d.Check("imageQuad.d");
        AssetId = assetId;
        A = a;
        B = b;
        C = c;
        D = d;
        Tint = tint;
    }

    /// <inheritdoc />
    public override LayoutItemKind Kind => LayoutItemKind.ImageQuad;

    /// <summary>The snapshot image drawn.</summary>
    public AssetId AssetId { get; }

    /// <summary>Where the image's top left corner goes.</summary>
    public LayoutPoint A { get; }

    /// <summary>Where the image's top right corner goes.</summary>
    public LayoutPoint B { get; }

    /// <summary>Where the image's bottom right corner goes.</summary>
    public LayoutPoint C { get; }

    /// <summary>Where the image's bottom left corner goes.</summary>
    public LayoutPoint D { get; }

    /// <summary>The colour the image is multiplied by, its alpha the opacity.</summary>
    public LayoutColor Tint { get; }

    internal override AssetId DrawnAsset => AssetId;

    private protected override void WriteFields(CanonicalWriter writer)
    {
        WriteAsset(writer, AssetId);
        A.Write(writer);
        B.Write(writer);
        C.Write(writer);
        D.Write(writer);
        Tint.Write(writer);
    }
}

/// <summary>A piece of artwork the viewer bundles, named by its frozen id, drawn into four corners, tinted.</summary>
public sealed class LayoutArtQuad : LayoutItem
{
    /// <summary>Builds an art quad, refusing a malformed id or a corner outside the coordinate range.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>, <see cref="ProtocolError.InvalidLength"/> or <see cref="ProtocolError.LimitExceeded"/>.</exception>
    public LayoutArtQuad(string art, LayoutPoint a, LayoutPoint b, LayoutPoint c, LayoutPoint d, LayoutColor tint)
    {
        art = LayoutFields.CheckIdent(art, "artQuad.art");
        a.Check("artQuad.a");
        b.Check("artQuad.b");
        c.Check("artQuad.c");
        d.Check("artQuad.d");
        Art = art;
        A = a;
        B = b;
        C = c;
        D = d;
        Tint = tint;
    }

    /// <inheritdoc />
    public override LayoutItemKind Kind => LayoutItemKind.ArtQuad;

    /// <summary>The artwork's identifier; a viewer that does not bundle it draws a placeholder, never fetches it.</summary>
    public string Art { get; }

    /// <summary>Where the artwork's top left corner goes.</summary>
    public LayoutPoint A { get; }

    /// <summary>Where the artwork's top right corner goes.</summary>
    public LayoutPoint B { get; }

    /// <summary>Where the artwork's bottom right corner goes.</summary>
    public LayoutPoint C { get; }

    /// <summary>Where the artwork's bottom left corner goes.</summary>
    public LayoutPoint D { get; }

    /// <summary>The colour the artwork is multiplied by, its alpha the opacity.</summary>
    public LayoutColor Tint { get; }

    private protected override void WriteFields(CanonicalWriter writer)
    {
        LayoutFields.WriteIdent(writer, Art);
        A.Write(writer);
        B.Write(writer);
        C.Write(writer);
        D.Write(writer);
        Tint.Write(writer);
    }
}
