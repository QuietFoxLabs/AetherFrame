using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using AetherFrame.Protocol.Encoding;

namespace AetherFrame.Protocol.Remote;

/// <summary>What an image's bytes are, by the rules of the specification's section 8.2.1.</summary>
/// <param name="Format">PNG or JPEG.</param>
/// <param name="Width">The width in pixels, 1 to 8,192.</param>
/// <param name="Height">The height in pixels, 1 to 8,192.</param>
public readonly record struct SniffedImage(ImageFormat Format, int Width, int Height);

/// <summary>
/// The one rule every consumer of a shared image applies to its bytes before decoding anything:
/// the publisher to the copies it prepared, the server to what it receives and to its own
/// re-encodes (decision I2), and the viewer to what it is served (decision I1).
/// docs/networking/ProtocolSpecification-v1.md, section 8.2.1, is normative. It reads the
/// container's structure only, in a walk bounded by the bytes given, and never decodes pixels:
/// <list type="bullet">
/// <item>a non-animated, non-interlaced 8-bit PNG, truecolour with or without alpha;</item>
/// <item>a JPEG whose one frame is baseline, extended or progressive Huffman (SOF0 to SOF2),
/// 8-bit, with 1 or 3 components, ending at its end-of-image marker.</item>
/// </list>
/// </summary>
public static class ImageSniffer
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Reads <paramref name="bytes"/> by section 8.2.1, refusing anything it doesn't allow.</summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.InvalidLength"/> for no bytes, <see cref="ProtocolError.LimitExceeded"/>
    /// for more than 8 MiB or a size over section 8.2's limits, <see cref="ProtocolError.Truncated"/>
    /// for a structure that runs past the end, <see cref="ProtocolError.TrailingBytes"/> for bytes
    /// after the end, and <see cref="ProtocolError.InvalidValue"/> for anything else it refuses.
    /// </exception>
    public static SniffedImage Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidLength, "An image is never empty.");
        }

        if (bytes.Length > ProtocolLimits.MaxImageBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"An image is at most {ProtocolText.Number(ProtocolLimits.MaxImageBytes)} bytes.");
        }

        if (bytes.StartsWith(PngSignature))
        {
            return SniffPng(bytes);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return SniffJpeg(bytes);
        }

        throw new ProtocolException(ProtocolError.InvalidValue, "An image is a PNG or a JPEG, and these bytes are neither.");
    }

    /// <summary>
    /// Checks that <paramref name="bytes"/> are exactly the image <paramref name="declared"/>
    /// describes, in section 13, rule 7's order: the byte length and SHA-256 first, then section
    /// 8.2.1's rules, then the declared format, width and height.
    /// </summary>
    /// <exception cref="ProtocolException">A refusal of <see cref="Sniff"/>, or <see cref="ProtocolError.InvalidValue"/> when anything differs from the declaration.</exception>
    public static SniffedImage CheckDeclared(ReadOnlySpan<byte> bytes, ImageReference declared)
    {
        ArgumentNullException.ThrowIfNull(declared);
        Span<byte> digest = stackalloc byte[ProtocolConstants.DigestLength];
        SHA256.HashData(bytes, digest);
        if (bytes.Length != declared.ByteLength || !CryptographicOperations.FixedTimeEquals(digest, declared.Sha256))
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "An image's bytes are not the image its declaration describes.");
        }

        var sniffed = Sniff(bytes);
        if (sniffed.Format != declared.Format || sniffed.Width != declared.Width || sniffed.Height != declared.Height)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "An image's bytes are not the image its declaration describes.");
        }

        return sniffed;
    }

    private static SniffedImage SniffPng(ReadOnlySpan<byte> bytes)
    {
        var offset = PngSignature.Length;
        var first = true;
        var width = 0;
        var height = 0;
        var sawData = false;
        var dataEnded = false;
        while (true)
        {
            if (bytes.Length - offset < 12)
            {
                throw new ProtocolException(ProtocolError.Truncated, "A PNG chunk runs past the end of the image.");
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            var type = bytes.Slice(offset + 4, 4);
            foreach (var letter in type)
            {
                if (letter is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')))
                {
                    throw new ProtocolException(ProtocolError.InvalidValue, "A PNG chunk's type is four ASCII letters.");
                }
            }

            if (length > int.MaxValue || length > (uint)(bytes.Length - offset - 12))
            {
                throw new ProtocolException(ProtocolError.Truncated, "A PNG chunk runs past the end of the image.");
            }

            var data = bytes.Slice(offset + 8, (int)length);
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13)
                {
                    throw new ProtocolException(ProtocolError.InvalidValue, "A PNG starts with a 13-byte IHDR chunk.");
                }

                (width, height) = CheckSize(BinaryPrimitives.ReadUInt32BigEndian(data), BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                var (depth, colourType, compression, filter, interlace) = (data[8], data[9], data[10], data[11], data[12]);
                if (depth != 8 || colourType is not (2 or 6) || compression != 0 || filter != 0 || interlace != 0)
                {
                    throw new ProtocolException(ProtocolError.InvalidValue, "A PNG is 8-bit truecolour, with or without alpha, and not interlaced.");
                }

                first = false;
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                throw new ProtocolException(ProtocolError.InvalidValue, "A PNG has one IHDR chunk.");
            }
            else if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8))
            {
                throw new ProtocolException(ProtocolError.InvalidValue, "A shared PNG is never animated.");
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (dataEnded)
                {
                    throw new ProtocolException(ProtocolError.InvalidValue, "A PNG's IDAT chunks are consecutive.");
                }

                sawData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !sawData)
                {
                    throw new ProtocolException(ProtocolError.InvalidValue, "A PNG ends with an empty IEND chunk, after its image data.");
                }

                if (offset + 12 != bytes.Length)
                {
                    throw new ProtocolException(ProtocolError.TrailingBytes, "Nothing follows a PNG's IEND chunk.");
                }

                return new SniffedImage(ImageFormat.Png, width, height);
            }
            else if (sawData)
            {
                dataEnded = true;
            }

            offset += 12 + (int)length;
        }
    }

    private static SniffedImage SniffJpeg(ReadOnlySpan<byte> bytes)
    {
        var offset = 2;
        var width = 0;
        var height = 0;
        var framed = false;
        var scanned = false;
        while (true)
        {
            var marker = NextMarker(bytes, ref offset);
            switch (marker)
            {
                case 0xD9:
                    // End of image: after a frame and a scan, and last.
                    if (!framed || !scanned)
                    {
                        throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG ends after its frame and its image data.");
                    }

                    if (offset != bytes.Length)
                    {
                        throw new ProtocolException(ProtocolError.TrailingBytes, "Nothing follows a JPEG's end-of-image marker.");
                    }

                    return new SniffedImage(ImageFormat.Jpeg, width, height);

                case 0xD8:
                case >= 0xD0 and <= 0xD7:
                case 0x01:
                    throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG has one start-of-image marker, restart markers only within image data, and no TEM marker.");
            }

            var segment = Segment(bytes, ref offset);
            switch (marker)
            {
                case 0xC0:
                case 0xC1:
                case 0xC2:
                    if (framed)
                    {
                        throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG has one frame.");
                    }

                    (width, height) = Frame(segment);
                    framed = true;
                    break;

                case 0xC3:
                case >= 0xC5 and <= 0xCF:
                case 0xDC:
                case 0xDE:
                case 0xDF:
                case >= 0xF0 and <= 0xFD:
                    throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG frame is baseline, extended or progressive Huffman (SOF0 to SOF2), with no DNL, DAC, hierarchical or reserved marker.");

                case 0xDA:
                    if (!framed)
                    {
                        throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG's image data follows its frame.");
                    }

                    SkipScan(bytes, ref offset);
                    scanned = true;
                    break;
            }
        }
    }

    /// <summary>The next marker's code, after any fill bytes; <paramref name="offset"/> moves past it.</summary>
    private static byte NextMarker(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (offset >= bytes.Length || bytes[offset] != 0xFF)
        {
            throw offset >= bytes.Length
                ? new ProtocolException(ProtocolError.Truncated, "A JPEG ends before its end-of-image marker.")
                : new ProtocolException(ProtocolError.InvalidValue, "A JPEG marker starts with 0xFF.");
        }

        while (offset < bytes.Length && bytes[offset] == 0xFF)
        {
            offset++;
        }

        if (offset >= bytes.Length)
        {
            throw new ProtocolException(ProtocolError.Truncated, "A JPEG ends before its end-of-image marker.");
        }

        var code = bytes[offset++];
        if (code == 0x00)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG marker's code is never 0x00 outside image data.");
        }

        return code;
    }

    /// <summary>A marker segment's body; <paramref name="offset"/> moves past it.</summary>
    private static ReadOnlySpan<byte> Segment(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (bytes.Length - offset < 2)
        {
            throw new ProtocolException(ProtocolError.Truncated, "A JPEG segment runs past the end of the image.");
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
        if (length < 2)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG segment's length counts its own two bytes.");
        }

        if (length > bytes.Length - offset)
        {
            throw new ProtocolException(ProtocolError.Truncated, "A JPEG segment runs past the end of the image.");
        }

        var body = bytes.Slice(offset + 2, length - 2);
        offset += length;
        return body;
    }

    private static (int Width, int Height) Frame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG frame header is too short.");
        }

        var (precision, height, width, components) = (frame[0], BinaryPrimitives.ReadUInt16BigEndian(frame[1..]), BinaryPrimitives.ReadUInt16BigEndian(frame[3..]), frame[5]);
        if (precision != 8 || components is not (1 or 3) || frame.Length != 6 + (3 * components))
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG frame is 8-bit, with 1 or 3 components.");
        }

        // A height of 0 would leave it to a DNL marker, which is refused.
        return CheckSize(width, height);
    }

    /// <summary>Moves <paramref name="offset"/> past a scan's entropy-coded data, to the marker that ends it.</summary>
    private static void SkipScan(ReadOnlySpan<byte> bytes, ref int offset)
    {
        while (offset < bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            // 0xFF 0x00 is a stuffed byte, and a restart marker stays within the data; any other
            // marker (after fill bytes) ends the scan.
            var next = offset + 1;
            while (next < bytes.Length && bytes[next] == 0xFF)
            {
                next++;
            }

            if (next >= bytes.Length)
            {
                break;
            }

            if (bytes[next] == 0x00 || bytes[next] is >= 0xD0 and <= 0xD7)
            {
                offset = next + 1;
                continue;
            }

            offset = next - 1;
            return;
        }

        throw new ProtocolException(ProtocolError.Truncated, "A JPEG ends before its end-of-image marker.");
    }

    private static (int Width, int Height) CheckSize(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "An image is at least one pixel each way.");
        }

        if (width > ProtocolLimits.MaxImageDimension || height > ProtocolLimits.MaxImageDimension || (long)width * height > ProtocolLimits.MaxImagePixels)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"An image is at most {ProtocolText.Number(ProtocolLimits.MaxImageDimension)} pixels each way, and {ProtocolText.Number(ProtocolLimits.MaxImagePixels)} in all.");
        }

        return ((int)width, (int)height);
    }
}
