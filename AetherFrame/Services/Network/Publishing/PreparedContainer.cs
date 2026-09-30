using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>
/// The exact inventory a prepared copy may hold, checked on what the encoder actually wrote (decision
/// D5's N2-6 note, (3)), before the copy is hashed or declared:
/// <list type="bullet">
/// <item>a PNG is its signature, IHDR, one or more IDAT, and IEND, with nothing after it;</item>
/// <item>a JPEG is start of image, APP0 (JFIF) first, then only DQT, SOF0, DHT, DRI and scans (SOS
/// and their data), and end of image last. The one APP1 (EXIF) block Dalamud's encoder adds to every
/// JPEG is removed, whole.</item>
/// </list>
/// Anything else refuses the copy: text, colour profiles, gamma, physical size, times, other
/// metadata, comments, other frame types, fill bytes, bytes after the end. Free of Dalamud.
/// </summary>
internal static class PreparedContainer
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The copy as it may be shared (a JPEG without its APP1 block), or null when it holds anything else.</summary>
    internal static byte[]? Clean(byte[] encoded, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        return format switch
        {
            ImageFormat.Png => IsBarePng(encoded) ? encoded : null,
            ImageFormat.Jpeg => BareJpeg(encoded),
            _ => null,
        };
    }

    private static bool IsBarePng(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.StartsWith(PngSignature))
        {
            return false;
        }

        var offset = PngSignature.Length;
        var header = false;
        var data = false;
        while (bytes.Length - offset >= 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > (uint)(bytes.Length - offset - 12))
            {
                return false;
            }

            var type = bytes.Slice(offset + 4, 4);
            if (!header)
            {
                if (!type.SequenceEqual("IHDR"u8))
                {
                    return false;
                }

                header = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                data = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return data && length == 0 && offset + 12 == bytes.Length;
            }
            else
            {
                return false;
            }

            offset += 12 + (int)length;
        }

        return false;
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
                // JFIF's APP0 comes first, as the encoder writes it.
                if (code != 0xE0 || !body.StartsWith("JFIF\0"u8))
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
            else if (code is 0xDB or 0xC0 or 0xC4 or 0xDD)
            {
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

        var total = 0;
        foreach (var (_, length) in kept)
        {
            total += length;
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
