using System.Collections.Generic;
using System.Text;

namespace AetherFrame.Tests;

/// <summary>
/// PNG files whose IHDR says exactly what a test needs: any bit depth and colour type, and sizes
/// that only a hostile file would declare. <see cref="TestImages.Png"/> stays the plain 8-bit RGBA
/// builder every other test uses.
/// </summary>
internal static class ImageTestSupport
{
    /// <summary>PNG colour type 0: greyscale.</summary>
    internal const byte Greyscale = 0;

    /// <summary>PNG colour type 2: truecolour (RGB).</summary>
    internal const byte Truecolour = 2;

    /// <summary>PNG colour type 4: greyscale with alpha.</summary>
    internal const byte GreyscaleAlpha = 4;

    /// <summary>PNG colour type 6: truecolour with alpha (RGBA).</summary>
    internal const byte TruecolourAlpha = 6;

    /// <summary>
    /// A structurally whole PNG (IHDR, one IDAT, IEND) with the given header fields. Width and
    /// height are written as the unsigned 32-bit values PNG uses, so a value past
    /// <see cref="int.MaxValue"/> is representable.
    /// </summary>
    internal static byte[] Png(uint width, uint height, byte bitDepth = 8, byte colourType = TruecolourAlpha)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new List<byte>();
        ihdr.AddRange(BigEndian(width));
        ihdr.AddRange(BigEndian(height));
        ihdr.AddRange([bitDepth, colourType, 0, 0, 0]);
        Chunk(bytes, "IHDR", ihdr.ToArray());
        Chunk(bytes, "IDAT", [0, 0, 0, 0]);
        Chunk(bytes, "IEND", []);
        return bytes.ToArray();
    }

    /// <summary>A 16-bit RGBA PNG: the layout the game's decoder uploads at 8 bytes per pixel.</summary>
    internal static byte[] SixteenBitRgbaPng(uint width, uint height) => Png(width, height, 16, TruecolourAlpha);

    private static void Chunk(List<byte> bytes, string type, byte[] data)
    {
        bytes.AddRange(BigEndian((uint)data.Length));
        bytes.AddRange(Encoding.ASCII.GetBytes(type));
        bytes.AddRange(data);
        bytes.AddRange([0, 0, 0, 0]);
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
