using AetherFrame.Protocol.Encoding;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// One entry of a served profile's image list (docs/networking/ProtocolSpecification-v1.md, section
/// 8.6; decision D6): what a viewer checks the served image's bytes against. It holds no asset id,
/// digest or byte length: the server serves its own re-encoded copy (decision I2), whose bytes
/// differ from the publisher's. Validated on construction; immutable.
/// </summary>
public sealed class ServedImage
{
    /// <summary>Builds an entry, refusing what a reader would refuse.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/> or <see cref="ProtocolError.LimitExceeded"/>.</exception>
    public ServedImage(ImageFormat format, int width, int height)
    {
        CheckFormat(format);
        CheckDimension(width, "width");
        CheckDimension(height, "height");
        CheckPixels(width, height);
        Format = format;
        Width = width;
        Height = height;
    }

    /// <summary>The image's format: PNG or JPEG (decision I1).</summary>
    public ImageFormat Format { get; }

    /// <summary>The image's width in pixels, 1 to 8,192.</summary>
    public int Width { get; }

    /// <summary>The image's height in pixels, 1 to 8,192.</summary>
    public int Height { get; }

    /// <summary>The image's pixels.</summary>
    public long Pixels => (long)Width * Height;

    internal void Write(CanonicalWriter writer)
    {
        writer.WriteU8((byte)Format);
        writer.WriteU32((uint)Width);
        writer.WriteU32((uint)Height);
    }

    /// <summary>Reads an entry, checking each field as soon as it is read, then the pixel product.</summary>
    internal static ServedImage Read(ref CanonicalReader reader)
    {
        var format = (ImageFormat)reader.ReadU8("image.format");
        CheckFormat(format);
        var width = ReadDimension(ref reader, "width");
        var height = ReadDimension(ref reader, "height");
        CheckPixels(width, height);
        return new ServedImage(format, width, height);
    }

    private static void CheckFormat(ImageFormat format)
    {
        if (format is not (ImageFormat.Png or ImageFormat.Jpeg))
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"A served image's format {ProtocolText.Number((byte)format)} is neither PNG (1) nor JPEG (2).");
        }
    }

    private static int ReadDimension(ref CanonicalReader reader, string what)
    {
        var value = reader.ReadU32("image." + what);
        if (value > ProtocolLimits.MaxImageDimension)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"A served image's {what} is {ProtocolText.Number(value)}; the limit is {ProtocolText.Number(ProtocolLimits.MaxImageDimension)}.");
        }

        CheckDimension((int)value, what);
        return (int)value;
    }

    private static void CheckDimension(int value, string what)
    {
        if (value < 1)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"A served image's {what} is at least 1.");
        }

        if (value > ProtocolLimits.MaxImageDimension)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"A served image's {what} is {ProtocolText.Number(value)}; the limit is {ProtocolText.Number(ProtocolLimits.MaxImageDimension)}.");
        }
    }

    private static void CheckPixels(int width, int height)
    {
        if ((long)width * height > ProtocolLimits.MaxImagePixels)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"A served image has {ProtocolText.Number((long)width * height)} pixels; the limit is {ProtocolText.Number(ProtocolLimits.MaxImagePixels)}.");
        }
    }
}
