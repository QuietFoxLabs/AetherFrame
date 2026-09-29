using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace AetherFrame.Domain.Rendering;

/// <summary>One RGBA level of bundled artwork: <see cref="Width"/> x <see cref="Height"/> texels, straight (non-premultiplied) alpha.</summary>
public sealed record ArtLevel(int Width, int Height, byte[] Rgba)
{
    /// <summary>The longer side, in texels: what level selection compares with the on-screen size.</summary>
    public int LongSide => Math.Max(Width, Height);
}

/// <summary>
/// Decodes AetherFrame's own bundled artwork PNGs and builds their downsampled levels. Pure logic
/// (no Dalamud), so the bundled files and the level math are unit tested.
///
/// <para><b>Why levels.</b> Dalamud textures have no mipmaps and ImGui samples them bilinearly, so
/// one large texture drawn far smaller than its size skips texels and breaks up fine lines (a
/// Corner Ornament is usually drawn at a fraction of its runtime size). Building a few halved
/// levels once, and drawing the smallest level at least as large as the on-screen size (see
/// <see cref="SelectLevel"/>), keeps thin lines continuous at every size from one bundled PNG.</para>
///
/// <para>The decoder only accepts what the bundled art is required to be — 8-bit RGBA,
/// non-interlaced, and a size whose every level down to <see cref="MinLevelSize"/> halves exactly
/// (see <see cref="IsSupportedSize"/>: square, 3:1, 16:9 or 5:8 art alike) — and rejects anything
/// else instead of guessing. It is never used for
/// user images (those go through Dalamud's decoders and <c>ImageSafety</c>).</para>
/// </summary>
public static class BundledArtImage
{
    /// <summary>Largest bundled art dimension accepted.</summary>
    public const int MaxSize = 2048;

    /// <summary>Smallest level built, on the shorter side (below this, a mark is a few pixels anyway).</summary>
    public const int MinLevelSize = 32;

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decodes an 8-bit RGBA, non-interlaced PNG of a supported size (<see cref="IsSupportedSize"/>).
    /// Throws <see cref="InvalidDataException"/> otherwise.</summary>
    public static ArtLevel DecodePng(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG");
        }

        var width = 0;
        var height = 0;
        var sawHeader = false;
        var sawEnd = false;
        using var compressed = new MemoryStream();
        var position = Signature.Length;

        while (!sawEnd)
        {
            if (png.Length - position < 12)
            {
                throw new InvalidDataException("truncated chunk");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(png[position..]);
            if (length < 0 || length > png.Length - position - 12)
            {
                throw new InvalidDataException("bad chunk length");
            }

            var type = png.Slice(position + 4, 4);
            var data = png.Slice(position + 8, length);
            position += 12 + length;

            if (type.SequenceEqual("IHDR"u8))
            {
                if (length != 13)
                {
                    throw new InvalidDataException("bad header");
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                var bitDepth = data[8];
                var colorType = data[9];
                if (bitDepth != 8 || colorType != 6 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    throw new InvalidDataException("bundled art must be 8-bit RGBA, non-interlaced");
                }

                if (!IsSupportedSize(width, height))
                {
                    throw new InvalidDataException("bundled art must have a size whose levels halve exactly");
                }

                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (!sawHeader)
                {
                    throw new InvalidDataException("image data before header");
                }

                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                sawEnd = true;
            }
        }

        if (!sawHeader)
        {
            throw new InvalidDataException("no header");
        }

        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var raw = new byte[(stride + 1) * height];
        compressed.Position = 0;
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            inflater.ReadExactly(raw);
            if (inflater.ReadByte() != -1)
            {
                throw new InvalidDataException("excess image data");
            }
        }

        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var source = raw.AsSpan((y * (stride + 1)) + 1, stride);
            var row = pixels.AsSpan(y * stride, stride);
            var previous = y == 0 ? Span<byte>.Empty : pixels.AsSpan((y - 1) * stride, stride);
            for (var i = 0; i < stride; i++)
            {
                int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                int up = y > 0 ? previous[i] : 0;
                int upLeft = y > 0 && i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                row[i] = (byte)(source[i] + filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException("bad row filter"),
                });
            }
        }

        return new ArtLevel(width, height, pixels);
    }

    /// <summary>
    /// True for the sizes bundled art may have, up to <see cref="MaxSize"/>: both sides divisible by
    /// every halving <see cref="BuildLevels"/> makes (until the shorter side reaches
    /// <see cref="MinLevelSize"/>), so every level is an exact 2x2 reduction of the one above — e.g.
    /// 512 x 512 and 1536 x 512 (multiples of 16), 1536 x 864, 800 x 1280, 1152 x 384 (of 8).
    /// </summary>
    public static bool IsSupportedSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxSize || height > MaxSize)
        {
            return false;
        }

        var shorter = Math.Min(width, height);
        var step = 1;
        while (shorter / (step * 2) >= MinLevelSize)
        {
            step *= 2;
        }

        return width % step == 0 && height % step == 0;
    }

    /// <summary>
    /// <paramref name="top"/> followed by successively halved levels, until the shorter side reaches <see cref="MinLevelSize"/>.
    /// Each texel averages its 2x2 source texels weighted by alpha (premultiplied), so soft glows keep
    /// their brightness and never pick up the color of fully transparent texels; a texel with no
    /// coverage at all keeps the average color of its fully transparent source texels — white for
    /// tintable art (so bilinear filtering never darkens a tint), the artwork's own nearby color for
    /// authored-color art (so its edges never pick up a foreign fringe).
    /// </summary>
    public static IReadOnlyList<ArtLevel> BuildLevels(ArtLevel top)
    {
        var levels = new List<ArtLevel> { top };
        var current = top;
        while (Math.Min(current.Width, current.Height) / 2 >= MinLevelSize && current.Width % 2 == 0 && current.Height % 2 == 0)
        {
            var width = current.Width / 2;
            var height = current.Height / 2;
            var source = current.Rgba;
            var sourceStride = current.Width * 4;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    int clearR = 0, clearG = 0, clearB = 0, clear = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var s = (((y * 2) + dy) * sourceStride) + (((x * 2) + dx) * 4);
                            var alpha = source[s + 3];
                            r += source[s] * alpha;
                            g += source[s + 1] * alpha;
                            b += source[s + 2] * alpha;
                            a += alpha;
                            if (alpha == 0)
                            {
                                clearR += source[s];
                                clearG += source[s + 1];
                                clearB += source[s + 2];
                                clear++;
                            }
                        }
                    }

                    var d = ((y * width) + x) * 4;
                    if ((a + 2) / 4 == 0)
                    {
                        // Coverage rounds to zero, so at least three of the four are fully transparent.
                        pixels[d] = (byte)((clearR + (clear / 2)) / clear);
                        pixels[d + 1] = (byte)((clearG + (clear / 2)) / clear);
                        pixels[d + 2] = (byte)((clearB + (clear / 2)) / clear);
                        pixels[d + 3] = 0;
                        continue;
                    }

                    pixels[d] = (byte)((r + (a / 2)) / a);
                    pixels[d + 1] = (byte)((g + (a / 2)) / a);
                    pixels[d + 2] = (byte)((b + (a / 2)) / a);
                    pixels[d + 3] = (byte)((a + 2) / 4);
                }
            }

            current = new ArtLevel(width, height, pixels);
            levels.Add(current);
        }

        return levels;
    }

    /// <summary>
    /// Index into <paramref name="levelSizes"/> (each level's <see cref="ArtLevel.LongSide"/>, largest
    /// first, as <see cref="BuildLevels"/> returns them) of the smallest level whose longer side is still
    /// at least <paramref name="screenPixels"/> (the drawn longer side), so the
    /// texture is never magnified unless even the largest level is too small, and never minified by
    /// more than 2x.
    /// </summary>
    public static int SelectLevel(ReadOnlySpan<int> levelSizes, float screenPixels)
    {
        var chosen = 0;
        for (var i = 1; i < levelSizes.Length; i++)
        {
            if (!(levelSizes[i] >= screenPixels))
            {
                break;
            }

            chosen = i;
        }

        return chosen;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
