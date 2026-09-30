using System;
using AetherFrame.Protocol.Encoding;

namespace AetherFrame.Protocol.Remote;

/// <summary>A colour of a schema 2 layout: red, green, blue and straight alpha, each 0 to 255.</summary>
public readonly record struct LayoutColor(byte R, byte G, byte B, byte A)
{
    internal void Write(CanonicalWriter writer)
    {
        writer.WriteU8(R);
        writer.WriteU8(G);
        writer.WriteU8(B);
        writer.WriteU8(A);
    }

    internal static LayoutColor Read(ref CanonicalReader reader, string field)
    {
        var bytes = reader.ReadFixed(4, field);
        return new LayoutColor(bytes[0], bytes[1], bytes[2], bytes[3]);
    }
}

/// <summary>
/// A point of a schema 2 layout, in hundredths of a canvas unit, each coordinate within
/// <see cref="ProtocolLimits.MaxLayoutCoordinate"/> either way. Checked where a layout item is built.
/// </summary>
public readonly record struct LayoutPoint(int X, int Y)
{
    internal void Write(CanonicalWriter writer)
    {
        writer.WriteI32(X);
        writer.WriteI32(Y);
    }

    internal static LayoutPoint Read(ref CanonicalReader reader, string field) =>
        new(LayoutFields.ReadCoordinate(ref reader, field + ".x"), LayoutFields.ReadCoordinate(ref reader, field + ".y"));

    internal void Check(string field)
    {
        LayoutFields.CheckCoordinate(X, field + ".x");
        LayoutFields.CheckCoordinate(Y, field + ".y");
    }
}

/// <summary>What a layout item is. A closed set; any other value is refused.</summary>
public enum LayoutItemKind : byte
{
    /// <summary>A text, drawn with its own font and style.</summary>
    Text = 1,

    /// <summary>An image element: one of the snapshot's images in a box, fitted, flipped and rotated.</summary>
    Image = 2,

    /// <summary>A filled quadrilateral of one colour.</summary>
    Quad = 3,

    /// <summary>A filled triangle of one colour.</summary>
    Triangle = 4,

    /// <summary>One of the snapshot's images drawn into four corners, tinted.</summary>
    ImageQuad = 5,

    /// <summary>A piece of artwork the viewer bundles, named by its id, drawn into four corners, tinted.</summary>
    ArtQuad = 6,
}

/// <summary>What the background draws under its pattern.</summary>
public enum LayoutBackgroundMode : byte
{
    /// <summary>Nothing at all: no base and no pattern.</summary>
    None = 0,
    /// <summary>The primary colour.</summary>
    SolidColor = 1,
    /// <summary>A gradient from the primary to the secondary colour at the gradient angle.</summary>
    LinearGradient = 2,
    /// <summary>The primary colour, meant to carry a pattern.</summary>
    TexturedFill = 3,
    /// <summary>The background image.</summary>
    Image = 4,
}

/// <summary>The background's pattern (the specification's Appendix A).</summary>
public enum LayoutTexture : byte
{
    /// <summary>No pattern.</summary>
    None = 0,
    /// <summary>Fine noise.</summary>
    FineNoise = 1,
    /// <summary>Dots.</summary>
    Dots = 2,
    /// <summary>A grid.</summary>
    Grid = 3,
    /// <summary>Diagonal lines.</summary>
    DiagonalLines = 4,
    /// <summary>Crosshatching.</summary>
    Crosshatch = 5,
    /// <summary>A subtle paper grain.</summary>
    SubtlePaper = 6,
    /// <summary>A checkerboard.</summary>
    Checkerboard = 7,
    /// <summary>Stripes.</summary>
    Stripes = 8,
    /// <summary>Waves.</summary>
    Waves = 9,
    /// <summary>Herringbone.</summary>
    Herringbone = 10,
    /// <summary>A honeycomb.</summary>
    Honeycomb = 11,
    /// <summary>Scales.</summary>
    Scales = 12,
    /// <summary>Speckles.</summary>
    Speckle = 13,
    /// <summary>Diamonds.</summary>
    Diamonds = 14,
    /// <summary>Chevrons.</summary>
    Chevron = 15,
    /// <summary>Sparkles.</summary>
    Sparkle = 16,
    /// <summary>A linen weave.</summary>
    Linen = 17,
    /// <summary>Ripples.</summary>
    Ripples = 18,
    /// <summary>Quatrefoils.</summary>
    Quatrefoil = 19,
    /// <summary>Brickwork.</summary>
    Brick = 20,
}

/// <summary>How an image fills its box.</summary>
public enum LayoutImageFit : byte
{
    /// <summary>Stretched to the box, whatever its proportions.</summary>
    Stretch = 0,
    /// <summary>Scaled to fit inside the box, keeping its proportions.</summary>
    Fit = 1,
    /// <summary>Scaled to cover the box, keeping its proportions and cropping the rest.</summary>
    Fill = 2,
}

/// <summary>Which ways an image is mirrored.</summary>
[Flags]
public enum LayoutFlips : byte
{
    /// <summary>Not mirrored.</summary>
    None = 0,
    /// <summary>Mirrored left to right.</summary>
    Horizontal = 1,
    /// <summary>Mirrored top to bottom.</summary>
    Vertical = 2,
}

/// <summary>A text's horizontal alignment.</summary>
public enum LayoutHorizontalAlign : byte
{
    /// <summary>Lines start at the box's left edge.</summary>
    Left = 0,
    /// <summary>Lines are centred in the box.</summary>
    Center = 1,
    /// <summary>Lines end at the box's right edge.</summary>
    Right = 2,
}

/// <summary>A text's vertical alignment.</summary>
public enum LayoutVerticalAlign : byte
{
    /// <summary>The text starts at the box's top.</summary>
    Top = 0,
    /// <summary>The text is centred vertically in the box.</summary>
    Middle = 1,
    /// <summary>The text ends at the box's bottom.</summary>
    Bottom = 2,
}

/// <summary>A text's switches.</summary>
[Flags]
public enum LayoutTextFlags : byte
{
    /// <summary>No switches.</summary>
    None = 0,
    /// <summary>Lines wrap at the box's width.</summary>
    Wrap = 1,
    /// <summary>Bold.</summary>
    Bold = 2,
    /// <summary>Italic.</summary>
    Italic = 4,
    /// <summary>Underlined.</summary>
    Underline = 8,
    /// <summary>Struck through.</summary>
    Strikethrough = 16,
    /// <summary>The font shrinks, down to the auto-fit minimum, until the text fits the box.</summary>
    AutoFit = 32,
    /// <summary>Drawn with the outline.</summary>
    Outline = 64,
    /// <summary>Drawn with the shadow.</summary>
    Shadow = 128,
}

/// <summary>Which text layout rules a text renders with.</summary>
public enum LayoutTextLayout : byte
{
    /// <summary>The text layout rules of Plates saved before the current ones.</summary>
    Legacy = 0,
    /// <summary>The current text layout rules.</summary>
    Current = 1,
}

/// <summary>
/// The range checks of schema 2's fields (docs/networking/ProtocolSpecification-v1.md, section
/// 8.5), shared by the readers, which check each field as it is read, and the constructors, which
/// refuse to build what a reader would refuse. A value outside its range is
/// <see cref="ProtocolError.InvalidValue"/>.
/// </summary>
internal static class LayoutFields
{
    public static int ReadCoordinate(ref CanonicalReader reader, string field) => CheckCoordinate(reader.ReadI32(field), field);

    public static int CheckCoordinate(int value, string field) =>
        CheckRange(value, -ProtocolLimits.MaxLayoutCoordinate, ProtocolLimits.MaxLayoutCoordinate, field);

    public static int ReadExtent(ref CanonicalReader reader, string field, int min, int max) => CheckRange(reader.ReadI32(field), min, max, field);

    public static int ReadAngle(ref CanonicalReader reader, string field) => CheckAngle(reader.ReadI32(field), field);

    public static int CheckAngle(int value, string field) =>
        CheckRange(value, -ProtocolLimits.MaxLayoutAngle, ProtocolLimits.MaxLayoutAngle, field);

    public static int ReadU16In(ref CanonicalReader reader, string field, int min, int max) => CheckRange(reader.ReadU16(field), min, max, field);

    public static int ReadI32In(ref CanonicalReader reader, string field, int min, int max) => CheckRange(reader.ReadI32(field), min, max, field);

    public static int CheckRange(int value, int min, int max, string field)
    {
        if (value < min || value > max)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"The layout field '{field}' is {ProtocolText.Number(value)}; it lies from {ProtocolText.Number(min)} to {ProtocolText.Number(max)}.");
        }

        return value;
    }

    /// <summary>Reads a one-byte enumeration code no greater than <paramref name="max"/>.</summary>
    public static byte ReadCode(ref CanonicalReader reader, string field, byte max) => CheckCode(reader.ReadU8(field), field, max);

    public static byte CheckCode(byte value, string field, byte max)
    {
        if (value > max)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"The layout field '{field}' has the unknown code {ProtocolText.Number(value)}.");
        }

        return value;
    }

    /// <summary>Reads a flag byte whose bits outside <paramref name="mask"/> are all zero.</summary>
    public static byte ReadFlags(ref CanonicalReader reader, string field, byte mask) => CheckFlags(reader.ReadU8(field), field, mask);

    public static byte CheckFlags(byte value, string field, byte mask)
    {
        if ((value & ~mask) != 0)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"The layout field '{field}' sets a flag no version 1 reader knows.");
        }

        return value;
    }

    /// <summary>
    /// Reads an identifier: a one-byte length, then 1 to <see cref="ProtocolLimits.MaxLayoutIdentBytes"/>
    /// ASCII bytes of a-z, 0-9, '.' and '-', the first a letter. A zero length is
    /// <see cref="ProtocolError.InvalidLength"/>, a longer one <see cref="ProtocolError.LimitExceeded"/>
    /// (before the bytes are looked at), a wrong character <see cref="ProtocolError.InvalidValue"/>.
    /// </summary>
    public static string ReadIdent(ref CanonicalReader reader, string field)
    {
        var length = reader.ReadU8(field);
        CheckIdentLength(length, field);
        return CheckIdentBytes(reader.ReadFixed(length, field), field);
    }

    public static void WriteIdent(CanonicalWriter writer, string ident)
    {
        writer.WriteU8((byte)ident.Length);
        foreach (var c in ident)
        {
            writer.WriteU8((byte)c);
        }
    }

    public static string CheckIdent(string value, string field)
    {
        ArgumentNullException.ThrowIfNull(value, field);
        CheckIdentLength(value.Length, field);
        foreach (var c in value)
        {
            if (c > 0x7F)
            {
                throw BadIdent(field);
            }
        }

        var bytes = new byte[value.Length];
        for (var index = 0; index < value.Length; index++)
        {
            bytes[index] = (byte)value[index];
        }

        return CheckIdentBytes(bytes, field);
    }

    private static void CheckIdentLength(int length, string field)
    {
        if (length == 0)
        {
            throw new ProtocolException(ProtocolError.InvalidLength, $"The layout identifier '{field}' is empty.");
        }

        if (length > ProtocolLimits.MaxLayoutIdentBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The layout identifier '{field}' is {ProtocolText.Number(length)} bytes; the limit is {ProtocolText.Number(ProtocolLimits.MaxLayoutIdentBytes)}.");
        }
    }

    private static string CheckIdentBytes(ReadOnlySpan<byte> bytes, string field)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            var b = bytes[index];
            var letter = b is >= (byte)'a' and <= (byte)'z';
            var allowed = letter || (index > 0 && (b is >= (byte)'0' and <= (byte)'9' || b == (byte)'.' || b == (byte)'-'));
            if (!allowed)
            {
                throw BadIdent(field);
            }
        }

        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private static ProtocolException BadIdent(string field) =>
        new(ProtocolError.InvalidValue, $"The layout identifier '{field}' holds a character other than a-z, 0-9, '.' and '-', or does not start with a letter.");
}
