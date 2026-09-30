using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>
/// The exact inventory a prepared copy may hold, checked on what the encoder actually wrote (decision
/// D5's N2-6 note, (3)), before the copy is hashed or declared:
/// <list type="bullet">
/// <item>a PNG is its signature, IHDR, one or more IDAT, and IEND, with nothing after it. The
/// constant colour and resolution chunks an encoder may add before the image data (pHYs, sRGB,
/// gAMA and cHRM, each at most once) are removed, whole;</item>
/// <item>a JPEG is start of image, JFIF's APP0 first (its identifier, version, units and density,
/// with no thumbnail), then only DQT, SOF0, DHT, DRI and scans (SOS and their data), and end of
/// image last. The one APP1 (EXIF) block Dalamud's encoder adds to every JPEG is removed,
/// whole.</item>
/// </list>
/// Anything else refuses the copy: text, colour profiles, times, other metadata, comments, other
/// frame types, fill bytes, bytes after the end. Removing only ever drops whole chunks or segments,
/// so nothing of them can remain. Free of Dalamud.
/// </summary>
internal static class PreparedContainer
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // A JFIF APP0 segment with no thumbnail: its length (16) counts the length itself, "JFIF" and
    // its zero, the version, the units, the two densities, and a 0 by 0 thumbnail.
    private const int JfifLength = 16;

    /// <summary>The copy as it may be shared (without the chunks or the segment removed), or null when it holds anything else.</summary>
    internal static byte[]? Clean(byte[] encoded, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        return format switch
        {
            ImageFormat.Png => BarePng(encoded),
            ImageFormat.Jpeg => BareJpeg(encoded),
            _ => null,
        };
    }

    private static byte[]? BarePng(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith(PngSignature))
        {
            return null;
        }

        var kept = new List<(int Start, int Length)> { (0, PngSignature.Length) };
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var offset = PngSignature.Length;
        var header = false;
        var data = false;
        while (bytes.Length - offset >= 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            if (length > (uint)(bytes.Length - offset - 12))
            {
                return null;
            }

            var chunk = 12 + (int)length;
            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            switch (type)
            {
                case "IHDR" when !header:
                    header = true;
                    kept.Add((offset, chunk));
                    break;

                case "pHYs" or "sRGB" or "gAMA" or "cHRM" when header && !data && removed.Add(type):
                    // A constant the encoder writes: removed, whole.
                    break;

                case "IDAT" when header:
                    data = true;
                    kept.Add((offset, chunk));
                    break;

                case "IEND" when data && length == 0 && offset + chunk == bytes.Length:
                    kept.Add((offset, chunk));
                    return Join(bytes, kept);

                default:
                    return null;
            }

            offset += chunk;
        }

        return null;
    }

    private static byte[]? BareJpeg(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return null;
        }

        // The byte ranges kept, in order: all of the JPEG but its APP1 block.
        var kept = new List<(int Start, int Length)> { (0, 2) };
        var offset = 2;
        var first = true;
        var exif = false;
        while (true)
        {
            if (bytes.Length - offset < 2 || bytes[offset] != 0xFF)
            {
                return null;
            }

            var code = bytes[offset + 1];
            if (code == 0xD9)
            {
                if (first || offset + 2 != bytes.Length)
                {
                    return null;
                }

                kept.Add((offset, 2));
                break;
            }

            if (bytes.Length - offset < 4)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2));
            if (length < 2 || length > bytes.Length - offset - 2)
            {
                return null;
            }

            var body = bytes.AsSpan(offset + 4, length - 2);
            var segment = 2 + length;
            if (first)
            {
                // JFIF's APP0 comes first, as the encoder writes it, with no thumbnail.
                if (code != 0xE0 || length != JfifLength || !body.StartsWith("JFIF\0"u8) || body[12] != 0 || body[13] != 0)
                {
                    return null;
                }

                first = false;
                kept.Add((offset, segment));
            }
            else if (code == 0xE1 && !exif && body.StartsWith("Exif\0\0"u8))
            {
                // Dalamud's EXIF block, which only says the colour space: removed, whole.
                exif = true;
            }
            else if (code is 0xDB or 0xC0 or 0xC4 || (code == 0xDD && length == 4))
            {
                // DRI holds only its restart interval: a segment of exactly 4 bytes.
                kept.Add((offset, segment));
            }
            else if (code == 0xDA)
            {
                var end = ScanEnd(bytes, offset + segment);
                if (end < 0)
                {
                    return null;
                }

                kept.Add((offset, end - offset));
                offset = end;
                continue;
            }
            else
            {
                return null;
            }

            offset += segment;
        }

        return Join(bytes, kept);
    }

    /// <summary>The kept byte ranges, in order; the same array when every byte is kept.</summary>
    private static byte[] Join(byte[] bytes, List<(int Start, int Length)> kept)
    {
        var total = 0;
        foreach (var (_, length) in kept)
        {
            total += length;
        }

        if (total == bytes.Length)
        {
            return bytes;
        }

        var cleaned = new byte[total];
        var at = 0;
        foreach (var (start, length) in kept)
        {
            bytes.AsSpan(start, length).CopyTo(cleaned.AsSpan(at));
            at += length;
        }

        return cleaned;
    }

    /// <summary>Where a scan's entropy-coded data ends: at the next marker other than a stuffed byte or a restart marker; -1 when none does.</summary>
    private static int ScanEnd(byte[] bytes, int offset)
    {
        while (offset < bytes.Length - 1)
        {
            if (bytes[offset] == 0xFF && bytes[offset + 1] != 0x00 && bytes[offset + 1] is not (>= 0xD0 and <= 0xD7))
            {
                return offset;
            }

            offset += bytes[offset] == 0xFF ? 2 : 1;
        }

        return -1;
    }
}
