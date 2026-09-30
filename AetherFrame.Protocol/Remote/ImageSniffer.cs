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
/// container's structure only, in a walk bounded by the bytes given, never decodes pixels, and
/// checks each rule as soon as what it is about has been read (section 9.1):
/// <list type="bullet">
/// <item>a non-animated, non-interlaced 8-bit PNG, truecolour with or without alpha, whose only
/// critical chunks are IHDR, PLTE, IDAT and IEND, with no compressed ancillary chunk;</item>
/// <item>a JPEG whose one frame is baseline, extended or progressive Huffman (SOF0 to SOF2),
/// 8-bit, with 1 or 3 components, in at most 64 scans, holding only the markers such a JPEG
/// needs, and ending at its end-of-image marker.</item>
/// </list>
/// A consumer decodes exactly the bytes it checked, from its own copy of them.
/// </summary>
public static class ImageSniffer
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Reads <paramref name="bytes"/> by section 8.2.1, refusing anything it doesn't allow.</summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.InvalidLength"/> for no bytes, <see cref="ProtocolError.LimitExceeded"/>
    /// for more than 8 MiB, a size over section 8.2's limits, a PNG chunk length over 2^31 - 1 or a
    /// 65th JPEG scan, <see cref="ProtocolError.Truncated"/> for a structure that runs past the end,
    /// <see cref="ProtocolError.TrailingBytes"/> for bytes after the end, and
    /// <see cref="ProtocolError.InvalidValue"/> for anything else it refuses; the first fault in
    /// reading order.
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
    /// describes, in section 13, rule 7's order: the byte length, then the SHA-256 (compared in
    /// constant time), then section 8.2.1's rules, then the declared format, width and height.
    /// </summary>
    /// <exception cref="ProtocolException">A refusal of <see cref="Sniff"/>, or <see cref="ProtocolError.InvalidValue"/> when anything differs from the declaration.</exception>
    public static SniffedImage CheckDeclared(ReadOnlySpan<byte> bytes, ImageReference declared)
    {
        ArgumentNullException.ThrowIfNull(declared);
        if (bytes.Length != declared.ByteLength)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "An image's bytes are not the image its declaration describes.");
        }

        Span<byte> digest = stackalloc byte[ProtocolConstants.DigestLength];
        SHA256.HashData(bytes, digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, declared.Sha256))
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
        var palette = false;
        var sawData = false;
        var dataEnded = false;
        while (true)
        {
            // The length, and its limit, before anything it announces.
            if (bytes.Length - offset < 4)
            {
                throw PngTruncated();
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            offset += 4;
            if (length > int.MaxValue)
            {
                throw new ProtocolException(ProtocolError.LimitExceeded, "A PNG chunk's length is at most 2,147,483,647.");
            }

            // The type, and every rule it decides, before the chunk's data.
            if (bytes.Length - offset < 4)
            {
                throw PngTruncated();
            }

            var type = bytes.Slice(offset, 4);
            offset += 4;
            foreach (var letter in type)
            {
                if (!IsAsciiLetter(letter))
                {
                    throw new ProtocolException(ProtocolError.InvalidValue, "A PNG chunk's type is four ASCII letters.");
                }
            }

            var end = type.SequenceEqual("IEND"u8);
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13)
                {
                    throw new ProtocolException(ProtocolError.InvalidValue, "A PNG starts with a 13-byte IHDR chunk.");
                }
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                throw new ProtocolException(ProtocolError.InvalidValue, "A PNG has one IHDR chunk.");
            }
            else if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8))
            {
                throw new ProtocolException(ProtocolError.InvalidValue, "A shared PNG is never animated.");
            }
            else if (type.SequenceEqual("iCCP"u8) || type.SequenceEqual("zTXt"u8) || type.SequenceEqual("iTXt"u8))
            {
                throw new ProtocolException(ProtocolError.InvalidValue, "A shared PNG has no iCCP, zTXt or iTXt chunk, whose compressed contents no limit here bounds.");
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (palette || sawData || length is 0 or > 768 || length % 3 != 0)
                {
                    throw new ProtocolException(ProtocolError.InvalidValue, "A PNG has at most one PLTE chunk, of 1 to 256 entries, before its image data.");
                }

                palette = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (dataEnded)
                {
                    throw new ProtocolException(ProtocolError.InvalidValue, "A PNG's IDAT chunks are consecutive.");
                }

                sawData = true;
            }
            else if (end)
            {
                if (length != 0 || !sawData)
                {
                    throw new ProtocolException(ProtocolError.InvalidValue, "A PNG ends with an empty IEND chunk, after its image data.");
                }
            }
            else if (type[0] <= (byte)'Z')
            {
                // An upper-case first letter marks a critical chunk, which a decoder must understand.
                throw new ProtocolException(ProtocolError.InvalidValue, "A PNG's only critical chunks are IHDR, PLTE, IDAT and IEND.");
            }
            else if (sawData)
            {
                dataEnded = true;
            }

            // The data, then its fields in order, then the CRC, which this rule doesn't check.
            if (length > (uint)(bytes.Length - offset))
            {
                throw PngTruncated();
            }

            var data = bytes.Slice(offset, (int)length);
            offset += (int)length;
            if (first)
            {
                width = Dimension(BinaryPrimitives.ReadUInt32BigEndian(data));
                height = Dimension(BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                CheckPixels(width, height);
                if (data[8] != 8 || data[9] is not (2 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    throw new ProtocolException(ProtocolError.InvalidValue, "A PNG is 8-bit truecolour, with or without alpha, and not interlaced.");
                }

                first = false;
            }

            if (bytes.Length - offset < 4)
            {
                throw PngTruncated();
            }

            offset += 4;
            if (end)
            {
                if (offset != bytes.Length)
                {
                    throw new ProtocolException(ProtocolError.TrailingBytes, "Nothing follows a PNG's IEND chunk.");
                }

                return new SniffedImage(ImageFormat.Png, width, height);
            }
        }
    }

    private static SniffedImage SniffJpeg(ReadOnlySpan<byte> bytes)
    {
        var offset = 2;
        var width = 0;
        var height = 0;
        var framed = false;
        var scans = 0;
        while (true)
        {
            // The marker's code, and every rule it decides, before its segment.
            var marker = NextMarker(bytes, ref offset);
            switch (marker)
            {
                case 0xD9:
                    if (!framed || scans == 0)
                    {
                        throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG ends after its frame and its image data.");
                    }

                    if (offset != bytes.Length)
                    {
                        throw new ProtocolException(ProtocolError.TrailingBytes, "Nothing follows a JPEG's end-of-image marker.");
                    }

                    return new SniffedImage(ImageFormat.Jpeg, width, height);

                case >= 0xC0 and <= 0xC2:
                    if (framed)
                    {
                        throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG has one frame.");
                    }

                    break;

                case 0xDA:
                    if (!framed)
                    {
                        throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG's image data follows its frame.");
                    }

                    if (scans == ProtocolLimits.MaxJpegScans)
                    {
                        throw new ProtocolException(ProtocolError.LimitExceeded, $"A JPEG has at most {ProtocolText.Number(ProtocolLimits.MaxJpegScans)} scans.");
                    }

                    break;

                case 0xC4:
                case 0xDB:
                case 0xDD:
                case >= 0xE0 and <= 0xEF:
                case 0xFE:
                    break;

                default:
                    throw new ProtocolException(
                        ProtocolError.InvalidValue,
                        "A JPEG holds only the markers a baseline, extended or progressive Huffman JPEG needs (SOF0 to SOF2, DHT, DQT, DRI, SOS, APP0 to APP15, COM, one start and one end of image), and restart markers only within image data.");
            }

            var segment = Segment(bytes, ref offset);
            if (marker is >= 0xC0 and <= 0xC2)
            {
                (width, height) = Frame(segment);
                framed = true;
            }
            else if (marker == 0xDA)
            {
                scans++;
                SkipScan(bytes, ref offset);
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

    /// <summary>A marker segment's body, after its length's rule; <paramref name="offset"/> moves past it.</summary>
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

    /// <summary>A frame header's fields, each checked as it is read.</summary>
    private static (int Width, int Height) Frame(ReadOnlySpan<byte> frame)
    {
        if (FrameField(frame, 0, 1) != 8)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG frame is 8-bit.");
        }

        // A height of 0 would leave it to a DNL marker, which is refused.
        var height = Dimension(FrameField(frame, 1, 2));
        var width = Dimension(FrameField(frame, 3, 2));
        CheckPixels(width, height);
        var components = FrameField(frame, 5, 1);
        if (components is not (1 or 3))
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG frame has 1 or 3 components.");
        }

        if (frame.Length != 6 + (3 * components))
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG frame header holds 3 bytes per component, and nothing more.");
        }

        return (width, height);
    }

    /// <summary>A big-endian field of a frame header, which must hold it.</summary>
    private static uint FrameField(ReadOnlySpan<byte> frame, int at, int size)
    {
        if (frame.Length < at + size)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A JPEG frame header is too short.");
        }

        return size == 1 ? frame[at] : BinaryPrimitives.ReadUInt16BigEndian(frame[at..]);
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

    /// <summary>A width or height, checked as it is read: 0 is refused, and so is more than section 8.2 allows.</summary>
    private static int Dimension(uint value)
    {
        if (value == 0)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "An image is at least one pixel each way.");
        }

        if (value > ProtocolLimits.MaxImageDimension)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"An image is at most {ProtocolText.Number(ProtocolLimits.MaxImageDimension)} pixels each way.");
        }

        return (int)value;
    }

    /// <summary>The pixel product, once both dimensions are read.</summary>
    private static void CheckPixels(int width, int height)
    {
        if ((long)width * height > ProtocolLimits.MaxImagePixels)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"An image is at most {ProtocolText.Number(ProtocolLimits.MaxImagePixels)} pixels in all.");
        }
    }

    private static bool IsAsciiLetter(byte value) => value is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z');

    private static ProtocolException PngTruncated() => new(ProtocolError.Truncated, "A PNG chunk runs past the end of the image.");
}
