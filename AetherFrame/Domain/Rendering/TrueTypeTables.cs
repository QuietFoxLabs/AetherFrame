using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AetherFrame.Domain.Rendering;

/// <summary>
/// A face's vertical design metrics, in font units: its em (head.unitsPerEm) and the line box
/// ImGui sizes it by (hhea's ascender to descender; stb_truetype's ScaleForPixelHeight makes that
/// box the font size tall).
/// </summary>
internal readonly record struct FontVerticalMetrics(int UnitsPerEm, int Ascent, int Descent)
{
    /// <summary>The line box's height in font units.</summary>
    internal int LineBox => Ascent - Descent;

    /// <summary>How many pixels one em of the face is when ImGui builds it at <paramref name="sizePx"/>.</summary>
    internal float EmPixels(float sizePx) => LineBox > 0 ? sizePx * UnitsPerEm / LineBox : sizePx;

    /// <summary>The size to build the face at so one em of it is <paramref name="emPixels"/> pixels.</summary>
    internal float SizeForEm(float emPixels) => UnitsPerEm > 0 ? emPixels * LineBox / UnitsPerEm : emPixels;
}

/// <summary>
/// Just enough of a TrueType file to size a fallback face against another and to know what it
/// draws (issue #121): the vertical metrics ImGui builds with, and the codepoints its Unicode
/// "cmap" maps (formats 4 and 12). For AetherFrame's own embedded fonts; anything malformed
/// reads as nothing rather than throwing.
/// </summary>
internal static class TrueTypeTables
{
    /// <summary>The face's <see cref="FontVerticalMetrics"/>, or false when it has no readable head and hhea tables.</summary>
    internal static bool TryReadVerticalMetrics(ReadOnlySpan<byte> font, out FontVerticalMetrics metrics)
    {
        metrics = default;
        try
        {
            if (!TryFindTable(font, "head", out var head) || !TryFindTable(font, "hhea", out var hhea))
            {
                return false;
            }

            var unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(font.Slice(head + 18, 2));
            var ascent = BinaryPrimitives.ReadInt16BigEndian(font.Slice(hhea + 4, 2));
            var descent = BinaryPrimitives.ReadInt16BigEndian(font.Slice(hhea + 6, 2));
            if (unitsPerEm == 0 || ascent <= descent)
            {
                return false;
            }

            metrics = new FontVerticalMetrics(unitsPerEm, ascent, descent);
            return true;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// The codepoints from <paramref name="first"/> to <paramref name="last"/> (inclusive) the face
    /// maps to a glyph, ascending; empty when it has no Unicode cmap this reads.
    /// </summary>
    internal static int[] MappedCodepoints(ReadOnlySpan<byte> font, int first, int last)
    {
        var found = new SortedSet<int>();
        try
        {
            if (!TryFindTable(font, "cmap", out var cmap) || !TryFindUnicodeSubtable(font, cmap, out var sub))
            {
                return [];
            }

            if (BinaryPrimitives.ReadUInt16BigEndian(font.Slice(sub, 2)) == 12)
            {
                ReadFormat12(font, sub, first, last, found);
            }
            else
            {
                ReadFormat4(font, sub, first, last, found);
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException)
        {
            return [];
        }

        var codepoints = new int[found.Count];
        found.CopyTo(codepoints);
        return codepoints;
    }

    private static bool TryFindTable(ReadOnlySpan<byte> font, string tag, out int offset)
    {
        offset = 0;
        var count = BinaryPrimitives.ReadUInt16BigEndian(font.Slice(4, 2));
        for (var i = 0; i < count; i++)
        {
            var record = font.Slice(12 + (16 * i), 16);
            if (record[0] == tag[0] && record[1] == tag[1] && record[2] == tag[2] && record[3] == tag[3])
            {
                offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(record.Slice(8, 4)));
                return offset < font.Length;
            }
        }

        return false;
    }

    // A Windows Unicode subtable (full repertoire before BMP), else a Unicode-platform one; format
    // 12 before format 4, as stb_truetype prefers.
    private static bool TryFindUnicodeSubtable(ReadOnlySpan<byte> font, int cmap, out int subtable)
    {
        subtable = 0;
        var best = 0;
        var count = BinaryPrimitives.ReadUInt16BigEndian(font.Slice(cmap + 2, 2));
        for (var i = 0; i < count; i++)
        {
            var record = font.Slice(cmap + 4 + (8 * i), 8);
            var platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(2, 2));
            if (!((platform == 3 && encoding is 1 or 10) || platform == 0))
            {
                continue;
            }

            var at = cmap + checked((int)BinaryPrimitives.ReadUInt32BigEndian(record.Slice(4, 4)));
            var rank = BinaryPrimitives.ReadUInt16BigEndian(font.Slice(at, 2)) switch
            {
                12 => 2,
                4 => 1,
                _ => 0,
            };
            if (rank > best)
            {
                best = rank;
                subtable = at;
            }
        }

        return best > 0;
    }

    private static void ReadFormat4(ReadOnlySpan<byte> font, int sub, int first, int last, SortedSet<int> into)
    {
        var segments = BinaryPrimitives.ReadUInt16BigEndian(font.Slice(sub + 6, 2)) / 2;
        var ends = sub + 14;
        var starts = ends + (2 * segments) + 2;
        var deltas = starts + (2 * segments);
        var rangeOffsets = deltas + (2 * segments);
        for (var s = 0; s < segments; s++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(font.Slice(ends + (2 * s), 2));
            int start = BinaryPrimitives.ReadUInt16BigEndian(font.Slice(starts + (2 * s), 2));
            int delta = BinaryPrimitives.ReadInt16BigEndian(font.Slice(deltas + (2 * s), 2));
            int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(font.Slice(rangeOffsets + (2 * s), 2));
            for (var c = Math.Max(start, first); c <= Math.Min(Math.Min(end, last), 0xFFFE); c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    glyph = BinaryPrimitives.ReadUInt16BigEndian(font.Slice(rangeOffsets + (2 * s) + rangeOffset + (2 * (c - start)), 2));
                    glyph = glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
                }

                if (glyph != 0)
                {
                    into.Add(c);
                }
            }
        }
    }

    private static void ReadFormat12(ReadOnlySpan<byte> font, int sub, int first, int last, SortedSet<int> into)
    {
        var groups = BinaryPrimitives.ReadUInt32BigEndian(font.Slice(sub + 12, 4));
        for (var i = 0L; i < groups; i++)
        {
            var group = font.Slice(checked(sub + 16 + (int)(12 * i)), 12);
            var start = BinaryPrimitives.ReadUInt32BigEndian(group);
            var end = BinaryPrimitives.ReadUInt32BigEndian(group.Slice(4, 4));
            var glyph = BinaryPrimitives.ReadUInt32BigEndian(group.Slice(8, 4));
            for (var c = Math.Max(start, (uint)first); c <= Math.Min(end, (uint)last); c++)
            {
                if (glyph + (c - start) != 0)
                {
                    into.Add((int)c);
                }
            }
        }
    }
}
