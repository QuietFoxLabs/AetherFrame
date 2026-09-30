using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Network.Publishing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// NETWORK2's image preparation (N2-6b; decision D5 and its N2-6 note, decision I1): each required
/// window of a managed image is decoded, cropped by the read-back's pitch, cleared of the colour its
/// alpha hides, encoded again, checked on what the encoder actually wrote, and declared under a fresh
/// asset id, or refused with the reason the builder honours. The texture pipeline is a fake here:
/// it decodes real PNGs, and encodes PNGs and JPEG containers as Dalamud's WIC encoder writes them.
/// </summary>
public sealed class ImagePreparationTests
{
    [Fact]
    public void Clearing_KeepsWhatAlphaLetsShow_AndNothingElse()
    {
        for (var alpha = 0; alpha <= 255; alpha++)
        {
            for (var channel = 0; channel <= 255; channel++)
            {
                var cleared = ImagePreparer.Clear((byte)channel, (byte)alpha);
                if (alpha == 0)
                {
                    Assert.Equal(0, cleared);
                    continue;
                }

                // Drawn over anything, it looks as before; opaque colour is unchanged; clearing twice changes nothing.
                Assert.Equal(Premultiplied(channel, alpha), Premultiplied(cleared, alpha));
                Assert.Equal(alpha == 255 ? channel : cleared, cleared);
                Assert.Equal(cleared, ImagePreparer.Clear(cleared, (byte)alpha));
            }
        }

        // Detail under alpha 1 collapses to the one colour the pixel can show.
        Assert.Equal(new byte[] { 0, 255 }, Enumerable.Range(0, 256).Select(c => ImagePreparer.Clear((byte)c, 1)).Distinct().Order().ToArray());
    }

    [Fact]
    public void TheCrop_ReadsRowsByThePitch_InEveryFormatThePipelineReadsBack()
    {
        // A 5 by 3 image whose rows are padded to 32 bytes with 0xEE, which must never be copied.
        var source = Pixels(5, 3, (x, y) => ((byte)(10 * x), (byte)(20 * y), (byte)(x + y), (byte)(x == 4 ? 128 : 255)));
        var window = new PixelWindow(1, 1, 4, 2);
        var expected = Crop(source, 5, window);

        Assert.True(ImagePreparer.TryCrop(Readback(source, 5, 3, ImagePreparer.Rgba, pitch: 32), window, out var rgba, out var opaque));
        Assert.Equal(expected, rgba);
        Assert.False(opaque);
        Assert.DoesNotContain((byte)0xEE, rgba);

        Assert.True(ImagePreparer.TryCrop(Readback(source, 5, 3, ImagePreparer.Bgra, pitch: 24), window, out var fromBgra, out _));
        Assert.Equal(expected, fromBgra);

        // An opaque window is opaque; BGRX reads as opaque whatever its fourth byte holds.
        Assert.True(ImagePreparer.TryCrop(Readback(source, 5, 3, ImagePreparer.Rgba, pitch: 20), new PixelWindow(0, 0, 4, 3), out _, out var inner));
        Assert.True(inner);
        Assert.True(ImagePreparer.TryCrop(Readback(source, 5, 3, ImagePreparer.Bgrx, pitch: 20), window, out var fromBgrx, out var bgrxOpaque));
        Assert.True(bgrxOpaque);
        Assert.Equal(Crop(source.Select((value, index) => index % 4 == 3 ? (byte)255 : value).ToArray(), 5, window), fromBgrx);

        // A 16-bit read-back narrows each channel as an 8-bit target receives it, round(c / 257):
        // anything within 128 of v * 257 is v.
        var wide = new byte[5 * 3 * 8];
        for (var index = 0; index < source.Length; index++)
        {
            var near = Math.Clamp((source[index] * 257) + (((index % 3) - 1) * 128), 0, 65535);
            BinaryPrimitives.WriteUInt16LittleEndian(wide.AsSpan(index * 2), (ushort)near);
        }

        Assert.True(ImagePreparer.TryCrop(new DecodedImage(5, 3, 40, ImagePreparer.Rgba64, wide), window, out var fromWide, out _));
        Assert.Equal(expected, fromWide);

        // Anything else is refused: sRGB and float formats, a short buffer, a short pitch, a window outside.
        foreach (var format in new[] { 29, 91, 2, 10, 71 })
        {
            Assert.False(ImagePreparer.TryCrop(Readback(source, 5, 3, format, pitch: 20), window, out _, out _));
        }

        Assert.False(ImagePreparer.TryCrop(new DecodedImage(5, 3, 20, ImagePreparer.Rgba, new byte[59]), window, out _, out _));
        Assert.False(ImagePreparer.TryCrop(new DecodedImage(5, 3, 16, ImagePreparer.Rgba, new byte[60]), window, out _, out _));
        Assert.False(ImagePreparer.TryCrop(Readback(source, 5, 3, ImagePreparer.Rgba, pitch: 20), new PixelWindow(2, 1, 4, 2), out _, out _));
        Assert.False(ImagePreparer.TryCrop(Readback(source, 5, 3, ImagePreparer.Rgba, pitch: 20), new PixelWindow(-1, 0, 2, 2), out _, out _));
    }

    [Fact]
    public void JpegIsChosen_OnlyForAnOpaqueWindowOfAJpeg()
    {
        var jpeg = Jpeg(8, 8);
        Assert.Equal(ImageFormat.Jpeg, ImagePreparer.Choose(jpeg, opaque: true));
        Assert.Equal(ImageFormat.Png, ImagePreparer.Choose(jpeg, opaque: false));
        Assert.Equal(ImageFormat.Png, ImagePreparer.Choose(Png(2, 2, new byte[16]), opaque: true));
        Assert.Equal(ImageFormat.Png, ImagePreparer.Choose([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50], opaque: true));
    }

    [Fact]
    public void ACopyHoldsExactlyTheMeasuredInventory_AndDalamudsExifBlockIsRemovedWhole()
    {
        var png = Png(2, 2, new byte[16]);
        Assert.Same(png, PreparedContainer.Clean(png, ImageFormat.Png));

        // The constant colour and resolution chunks an encoder may add go, whole, once each and only
        // before the image data; any other chunk refuses the copy.
        foreach (var constant in new[] { "pHYs", "sRGB", "gAMA", "cHRM" })
        {
            Assert.Equal(png, PreparedContainer.Clean(PngWith(Chunk(constant, new byte[4])), ImageFormat.Png));
            Assert.Null(PreparedContainer.Clean(PngFrom(Chunk("IHDR", Header(2, 2)), Chunk(constant, [1]), Chunk(constant, [1]), Chunk("IDAT", Deflate(2, 2, new byte[16])), Chunk("IEND", [])), ImageFormat.Png));
            Assert.Null(PreparedContainer.Clean(PngFrom(Chunk("IHDR", Header(2, 2)), Chunk("IDAT", Deflate(2, 2, new byte[16])), Chunk(constant, [1]), Chunk("IEND", [])), ImageFormat.Png));
        }

        Assert.Equal(png, PreparedContainer.Clean(PngFrom(Chunk("IHDR", Header(2, 2)), Chunk("pHYs", new byte[9]), Chunk("sRGB", [0]), Chunk("gAMA", new byte[4]), Chunk("cHRM", new byte[32]), Chunk("IDAT", Deflate(2, 2, new byte[16])), Chunk("IEND", [])), ImageFormat.Png));
        foreach (var extra in new[] { "tEXt", "zTXt", "iTXt", "iCCP", "eXIf", "tIME", "cICP", "bKGD", "sBIT", "PLTE" })
        {
            Assert.Null(PreparedContainer.Clean(PngWith(Chunk(extra, new byte[4])), ImageFormat.Png));
        }

        Assert.Null(PreparedContainer.Clean(PngFrom(Chunk("IHDR", Header(2, 2)), Chunk("IEND", [])), ImageFormat.Png));
        Assert.Null(PreparedContainer.Clean(PngFrom(Chunk("IDAT", [1]), Chunk("IHDR", Header(2, 2)), Chunk("IEND", [])), ImageFormat.Png));
        Assert.Null(PreparedContainer.Clean(PngFrom(Chunk("IHDR", Header(2, 2)), Chunk("IDAT", [1]), Chunk("IEND", [1])), ImageFormat.Png));
        Assert.Null(PreparedContainer.Clean([.. png, 0], ImageFormat.Png));
        Assert.Null(PreparedContainer.Clean(png[..^1], ImageFormat.Png));
        Assert.Null(PreparedContainer.Clean(png, ImageFormat.Jpeg));

        // A JPEG as the encoder writes it: APP1 goes, byte for byte, and nothing else changes.
        var written = Jpeg(8, 8);
        var cleaned = PreparedContainer.Clean(written, ImageFormat.Jpeg);
        Assert.Equal(Remove(written, Exif()), cleaned);
        Assert.False(Contains(cleaned!, "Exif"u8.ToArray()));
        var bare = JpegFrom(App0(), Dqt(), Sof0(8, 8), Dht(), Sos(), Entropy(), Eoi());
        Assert.Equal(bare, PreparedContainer.Clean(bare, ImageFormat.Jpeg));
        Assert.Equal(bare, cleaned);

        // Restart markers and stuffed bytes within the image data, and a DRI, are the encoder's own.
        var restarts = JpegFrom(App0(), Dqt(), Segment(0xDD, [0, 4]), Sof0(8, 8), Dht(), Sos(), [0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56], Eoi());
        Assert.Equal(restarts, PreparedContainer.Clean(restarts, ImageFormat.Jpeg));

        // Anything else refuses the copy.
        foreach (var refused in new[]
        {
            JpegFrom(App0(), Exif(), Exif(), Dqt(), Sof0(8, 8), Dht(), Sos(), Entropy(), Eoi()),
            JpegFrom(App0(), Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/"u8, 0]), Dqt(), Sof0(8, 8), Dht(), Sos(), Entropy(), Eoi()),
            JpegFrom(App0(), Segment(0xE2, [.. "ICC_PROFILE"u8, 0]), Dqt(), Sof0(8, 8), Dht(), Sos(), Entropy(), Eoi()),
            JpegFrom(App0(), Segment(0xFE, "made by"u8.ToArray()), Dqt(), Sof0(8, 8), Dht(), Sos(), Entropy(), Eoi()),
            JpegFrom(App0(), Dqt(), Segment(0xC2, Sof0(8, 8)[4..]), Dht(), Sos(), Entropy(), Eoi()),
            JpegFrom(Exif(), App0(), Dqt(), Sof0(8, 8), Dht(), Sos(), Entropy(), Eoi()),
            JpegFrom(Segment(0xE0, [.. "JFXX"u8, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]), Dqt(), Sof0(8, 8), Dht(), Sos(), Entropy(), Eoi()),
            JpegFrom(Segment(0xE0, [.. "JFIF"u8, 0, 1, 1, 0, 0, 1, 0, 1, 1, 1, 9, 9, 9]), Dqt(), Sof0(8, 8), Dht(), Sos(), Entropy(), Eoi()),
            JpegFrom(Segment(0xE0, [.. "JFIF"u8, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0]), Dqt(), Sof0(8, 8), Dht(), Sos(), Entropy(), Eoi()),
            JpegFrom(App0(), [0xFF, .. Dqt()], Sof0(8, 8), Dht(), Sos(), Entropy(), Eoi()),
            [.. bare, 0x00],
            bare[..^1],
        })
        {
            Assert.Null(PreparedContainer.Clean(refused, ImageFormat.Jpeg));
        }
    }

    [Fact]
    public async Task AWindow_IsPrepared_AsItsOwnCopy_UnderAFreshId()
    {
        var pixels = Pixels(6, 4, (x, y) => ((byte)(40 * x), (byte)(60 * y), 7, (byte)(x < 3 ? 255 : 9 * y)));
        var file = Png(6, 4, pixels);
        var codec = new FakeCodec();
        var images = new FakeImages { [Photo] = file };
        var window = new PixelWindow(2, 1, 3, 2);
        var requirement = new ImageRequirement(Photo, window, 6, 4);

        var first = await ImagePreparer.PrepareAsync(requirement, images, codec, CancellationToken.None);
        var second = await ImagePreparer.PrepareAsync(requirement, images, codec, CancellationToken.None);

        // What was encoded is the window, cleared; what is declared is exactly the bytes kept.
        var (rgba, width, height, format) = codec.Encoded[0];
        Assert.Equal(Crop(pixels, 6, window), rgba);
        Assert.Equal((3, 2, ImageFormat.Png), (width, height, format));
        var copy = Assert.IsType<PreparedImage>(first.Copy);
        Assert.Equal((ImageFormat.Png, 3, 2, (long)copy.Bytes.Length), (copy.Reference.Format, copy.Reference.Width, copy.Reference.Height, copy.Reference.ByteLength));
        Assert.Equal(SHA256.HashData(copy.Bytes.Span), copy.Reference.Sha256.ToArray());
        Assert.Equal(new SniffedImage(ImageFormat.Png, 3, 2), ImageSniffer.Sniff(copy.Bytes.Span));

        // Each preparation declares a fresh id (decision N1), never one derived from the local image.
        Assert.NotEqual(copy.Reference.AssetId, second.Copy!.Reference.AssetId);
        Assert.False(Contains(copy.Bytes.ToArray(), Photo.ToByteArray()));
    }

    [Fact]
    public async Task AnOpaqueWindowOfAJpeg_IsAJpegCopy_WithoutDalamudsExifBlock_AndAnyAlphaMakesItAPng()
    {
        var jpeg = Jpeg(4, 4);
        var codec = new FakeCodec();
        codec.Decodes(jpeg, Readback(Pixels(4, 4, (x, y) => ((byte)x, (byte)y, 1, 255)), 4, 4, ImagePreparer.Bgrx, pitch: 16));
        var images = new FakeImages { [Photo] = jpeg };

        var prepared = await ImagePreparer.PrepareAsync(new ImageRequirement(Photo, PixelWindow.Whole(4, 4), 4, 4), images, codec, CancellationToken.None);
        var copy = Assert.IsType<PreparedImage>(prepared.Copy);
        Assert.Equal(ImageFormat.Jpeg, copy.Reference.Format);
        Assert.False(Contains(copy.Bytes.ToArray(), "Exif"u8.ToArray()));
        Assert.Equal(new SniffedImage(ImageFormat.Jpeg, 4, 4), ImageSniffer.Sniff(copy.Bytes.Span));

        codec.Decodes(jpeg, Readback(Pixels(4, 4, (x, y) => ((byte)x, (byte)y, 1, (byte)(x == 3 ? 254 : 255))), 4, 4, ImagePreparer.Bgra, pitch: 16));
        var translucent = await ImagePreparer.PrepareAsync(new ImageRequirement(Photo, PixelWindow.Whole(4, 4), 4, 4), images, codec, CancellationToken.None);
        Assert.Equal(ImageFormat.Png, translucent.Copy!.Reference.Format);
    }

    [Fact]
    public async Task EveryOtherOutcome_IsAReasonTheBuilderHonours()
    {
        var file = Png(4, 2, new byte[32]);
        var whole = new ImageRequirement(Photo, PixelWindow.Whole(4, 2), 4, 2);

        async Task<ImageUnavailableReason> Reason(ImageRequirement requirement, FakeImages images, FakeCodec codec)
        {
            var preparation = await ImagePreparer.PrepareAsync(requirement, images, codec, CancellationToken.None);
            Assert.Null(preparation.Copy);
            return preparation.Reason;
        }

        // No file, or one that doesn't decode: missing, as the renderer finds it.
        Assert.Equal(ImageUnavailableReason.Missing, await Reason(whole, new FakeImages(), new FakeCodec()));
        Assert.Equal(ImageUnavailableReason.Missing, await Reason(whole, new FakeImages { [Photo] = [1, 2, 3] }, new FakeCodec()));

        // Decoded at another size than the window was computed against.
        Assert.Equal(ImageUnavailableReason.SizeChanged, await Reason(whole with { SourceWidth = 5 }, new FakeImages { [Photo] = file }, new FakeCodec()));

        // Over I1's limits, before the file is even read.
        var untouched = new FakeImages { [Photo] = file };
        Assert.Equal(ImageUnavailableReason.TooLarge, await Reason(new ImageRequirement(Photo, new PixelWindow(0, 0, 8193, 1), 8193, 1), untouched, new FakeCodec()));
        Assert.Equal(ImageUnavailableReason.TooLarge, await Reason(new ImageRequirement(Photo, new PixelWindow(0, 0, 5000, 4001), 5000, 4001), untouched, new FakeCodec()));
        Assert.Equal(0, untouched.Reads);

        // A copy over 8 MiB as encoded.
        var huge = new FakeCodec { Encoder = (_, width, height, _) => PngFrom(Chunk("IHDR", Header(width, height)), Chunk("IDAT", new byte[ProtocolLimits.MaxImageBytes]), Chunk("IEND", [])) };
        Assert.Equal(ImageUnavailableReason.TooLarge, await Reason(whole, new FakeImages { [Photo] = file }, huge));

        // Anything else: a read-back in a format it doesn't know, an encoder that fails, writes
        // anything beyond the inventory, or writes another size than the window.
        var srgb = new FakeCodec();
        srgb.Decodes(file, new DecodedImage(4, 2, 16, 29, new byte[32]));
        Assert.Equal(ImageUnavailableReason.Unshareable, await Reason(whole, new FakeImages { [Photo] = file }, srgb));
        Assert.Equal(ImageUnavailableReason.Unshareable, await Reason(whole, new FakeImages { [Photo] = file }, new FakeCodec { Encoder = (_, _, _, _) => null }));
        Assert.Equal(ImageUnavailableReason.Unshareable, await Reason(whole, new FakeImages { [Photo] = file }, new FakeCodec { Encoder = (rgba, width, height, _) => PngWith(Chunk("tEXt", KeyedText()), width, height, rgba) }));
        Assert.Equal(ImageUnavailableReason.Unshareable, await Reason(whole, new FakeImages { [Photo] = file }, new FakeCodec { Encoder = (rgba, width, height, _) => Png(width + 1, height, [.. rgba, .. new byte[height * 4]]) }));
    }

    [Fact]
    public async Task EveryRequirement_HasItsPreparation()
    {
        var other = Guid.NewGuid();
        var images = new FakeImages { [Photo] = Png(4, 2, new byte[32]) };
        var requirements = new[]
        {
            new ImageRequirement(Photo, PixelWindow.Whole(4, 2), 4, 2),
            new ImageRequirement(Photo, new PixelWindow(1, 0, 2, 2), 4, 2),
            new ImageRequirement(other, PixelWindow.Whole(3, 3), 3, 3),
        };

        var preparations = await ImagePreparer.PrepareAllAsync(requirements, images, new FakeCodec(), CancellationToken.None);

        Assert.Equal(requirements, preparations.Keys);
        Assert.NotNull(preparations[requirements[0]].Copy);
        Assert.Equal(2, preparations[requirements[1]].Copy!.Reference.Width);
        Assert.Equal(ImageUnavailableReason.Missing, preparations[requirements[2]].Reason);
    }

    [Fact]
    public async Task TheSessionsKnownAnswerCheck_PassesOnlyForThePipelineItWasMeasuredAgainst()
    {
        Assert.True(await ImagePreparer.SelfTestAsync(new FakeCodec(), CancellationToken.None));

        // The known image is a real PNG, with colour hidden under alpha 0.
        var (width, height, pixels) = ImagePreparer.KnownImage();
        Assert.Equal(new SniffedImage(ImageFormat.Png, width, height), ImageSniffer.Sniff(ImagePreparer.KnownPng()));
        Assert.Equal(pixels, DecodePng(ImagePreparer.KnownPng())!.Pixels);
        Assert.Contains(Enumerable.Range(0, width * height), index => pixels[(index * 4) + 3] == 0 && pixels[index * 4] != 0);

        // A decoder that premultiplies, a JPEG path that swaps channels, an encoder that adds a
        // chunk or a segment, one that fails: publishing is off for the session.
        Assert.False(await ImagePreparer.SelfTestAsync(new FakeCodec { Premultiplies = true }, CancellationToken.None));
        Assert.False(await ImagePreparer.SelfTestAsync(new FakeCodec { SwapsJpegChannels = true }, CancellationToken.None));
        Assert.False(await ImagePreparer.SelfTestAsync(new FakeCodec { Encoder = (rgba, w, h, format) => format == ImageFormat.Png ? PngWith(Chunk("tEXt", KeyedText()), w, h, rgba) : Jpeg(w, h) }, CancellationToken.None));
        Assert.False(await ImagePreparer.SelfTestAsync(new FakeCodec { Encoder = (rgba, w, h, format) => format == ImageFormat.Png ? Png(w, h, rgba) : JpegFrom(App0(), Segment(0xFE, [1]), Dqt(), Sof0(h, w), Dht(), Sos(), Entropy(), Eoi()) }, CancellationToken.None));
        Assert.False(await ImagePreparer.SelfTestAsync(new FakeCodec { Encoder = (_, _, _, _) => null }, CancellationToken.None));
    }

    private static readonly Guid Photo = Guid.NewGuid();

    private static int Premultiplied(int channel, int alpha) => ((2 * channel * alpha) + 255) / 510;

    /// <summary>A tEXt chunk's data: the keyword "k", its zero separator, and the text "v".</summary>
    private static byte[] KeyedText() => [(byte)'k', 0, (byte)'v'];

    /// <summary>Straight RGBA pixels, row by row, from a function of their position.</summary>
    private static byte[] Pixels(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                (bytes[((y * width) + x) * 4], bytes[(((y * width) + x) * 4) + 1], bytes[(((y * width) + x) * 4) + 2], bytes[(((y * width) + x) * 4) + 3]) = (r, g, b, a);
            }
        }

        return bytes;
    }

    /// <summary>The window of straight RGBA pixels, cleared as preparation clears it.</summary>
    private static byte[] Crop(byte[] rgba, int width, PixelWindow window)
    {
        var output = new List<byte>();
        for (var y = window.Y; y < window.Y + window.Height; y++)
        {
            for (var x = window.X; x < window.X + window.Width; x++)
            {
                var at = ((y * width) + x) * 4;
                var alpha = rgba[at + 3];
                output.AddRange([ImagePreparer.Clear(rgba[at], alpha), ImagePreparer.Clear(rgba[at + 1], alpha), ImagePreparer.Clear(rgba[at + 2], alpha), alpha]);
            }
        }

        return output.ToArray();
    }

    /// <summary>Straight RGBA pixels as a read-back in <paramref name="format"/>, each row padded to <paramref name="pitch"/> with 0xEE.</summary>
    private static DecodedImage Readback(byte[] rgba, int width, int height, int format, int pitch)
    {
        var bytes = Enumerable.Repeat((byte)0xEE, pitch * height).ToArray();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var from = ((y * width) + x) * 4;
                var to = (y * pitch) + (x * 4);
                if (format is ImagePreparer.Bgra or ImagePreparer.Bgrx)
                {
                    (bytes[to], bytes[to + 1], bytes[to + 2], bytes[to + 3]) = (rgba[from + 2], rgba[from + 1], rgba[from], format == ImagePreparer.Bgrx ? (byte)0x5A : rgba[from + 3]);
                }
                else
                {
                    rgba.AsSpan(from, 4).CopyTo(bytes.AsSpan(to));
                }
            }
        }

        return new DecodedImage(width, height, pitch, format, bytes);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index + needle.Length <= haystack.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Remove(byte[] bytes, byte[] part)
    {
        for (var index = 0; index + part.Length <= bytes.Length; index++)
        {
            if (bytes.AsSpan(index, part.Length).SequenceEqual(part))
            {
                return [.. bytes[..index], .. bytes[(index + part.Length)..]];
            }
        }

        throw new InvalidOperationException("The part isn't in the bytes.");
    }

    // PNG: 8-bit RGBA, not interlaced, rows unfiltered, deflated; CRCs correct.
    private static byte[] Png(int width, int height, byte[] rgba) =>
        PngFrom(Chunk("IHDR", Header(width, height)), Chunk("IDAT", Deflate(width, height, rgba)), Chunk("IEND", []));

    private static byte[] PngWith(byte[] extra) => PngWith(extra, 2, 2, new byte[16]);

    private static byte[] PngWith(byte[] extra, int width, int height, byte[] rgba) =>
        PngFrom(Chunk("IHDR", Header(width, height)), extra, Chunk("IDAT", Deflate(width, height, rgba)), Chunk("IEND", []));

    private static byte[] PngFrom(params byte[][] chunks) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. chunks.SelectMany(c => c)];

    private static byte[] Header(int width, int height)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)height);
        (data[8], data[9]) = (8, 6);
        return data;
    }

    private static byte[] Deflate(int width, int height, byte[] rgba)
    {
        var rows = new byte[height * (1 + (width * 4))];
        for (var y = 0; y < height; y++)
        {
            rgba.AsSpan(y * width * 4, width * 4).CopyTo(rows.AsSpan((y * (1 + (width * 4))) + 1));
        }

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(rows);
        }

        return output.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        var crc = 0xFFFF_FFFFu;
        foreach (var value in chunk.AsSpan(4, 4 + data.Length))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), ~crc);
        return chunk;
    }

    /// <summary>An unfiltered 8-bit RGBA PNG's pixels, straight, as a tightly packed RGBA read-back; null for anything else.</summary>
    private static DecodedImage? DecodePng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || bytes[0] != 0x89)
        {
            return null;
        }

        var (width, height) = (0, 0);
        using var data = new MemoryStream();
        for (var offset = 8; offset + 12 <= bytes.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            var type = bytes.Slice(offset + 4, 4);
            var body = bytes.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                (width, height) = ((int)BinaryPrimitives.ReadUInt32BigEndian(body), (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                data.Write(body);
            }

            offset += 12 + length;
        }

        data.Position = 0;
        using var zlib = new ZLibStream(data, CompressionMode.Decompress);
        var rows = new byte[height * (1 + (width * 4))];
        zlib.ReadExactly(rows);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            rows.AsSpan((y * (1 + (width * 4))) + 1, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
        }

        return new DecodedImage(width, height, width * 4, ImagePreparer.Rgba, pixels);
    }

    // JPEG containers, as Dalamud's WIC encoder writes them: the entropy-coded data is never decoded.
    private static byte[] Jpeg(int width, int height) => JpegFrom(App0(), Exif(), Dqt(), Sof0(height, width), Dht(), Sos(), Entropy(), Eoi());

    private static byte[] JpegFrom(params byte[][] parts) => [0xFF, 0xD8, .. parts.SelectMany(p => p)];

    private static byte[] Segment(byte code, byte[] body)
    {
        var segment = new byte[4 + body.Length];
        (segment[0], segment[1]) = (0xFF, code);
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(2 + body.Length));
        body.CopyTo(segment, 4);
        return segment;
    }

    private static byte[] App0() => Segment(0xE0, [.. "JFIF"u8, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);

    /// <summary>The EXIF block Dalamud's encoder adds: little-endian TIFF with one ColorSpace tag.</summary>
    private static byte[] Exif() => Segment(0xE1, [.. "Exif"u8, 0, 0, 0x49, 0x49, 0x2A, 0, 8, 0, 0, 0, 1, 0, 0x01, 0xA0, 3, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    private static byte[] Dqt() => Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);

    private static byte[] Dht() => Segment(0xC4, [0x00, 1, .. new byte[15], 0x00]);

    private static byte[] Sof0(int height, int width)
    {
        var body = new byte[6 + 9];
        body[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3), (ushort)width);
        body[5] = 3;
        for (var component = 0; component < 3; component++)
        {
            (body[6 + (3 * component)], body[7 + (3 * component)], body[8 + (3 * component)]) = ((byte)(component + 1), 0x11, 0);
        }

        return Segment(0xC0, body);
    }

    private static byte[] Sos() => Segment(0xDA, [3, 1, 0, 2, 0x11, 3, 0x11, 0, 63, 0]);

    private static byte[] Entropy() => [0x12, 0x34, 0x56];

    private static byte[] Eoi() => [0xFF, 0xD9];

    /// <summary>The managed images' files, by id; counts its reads.</summary>
    private sealed class FakeImages : Dictionary<Guid, byte[]>, IManagedImages
    {
        internal int Reads { get; private set; }

        public byte[]? TryRead(Guid image)
        {
            Reads++;
            return TryGetValue(image, out var file) ? file : null;
        }
    }

    /// <summary>
    /// A texture pipeline standing in for Dalamud's: it decodes what it is told to, or any
    /// unfiltered 8-bit RGBA PNG (which is all it writes), and encodes a PNG or a JPEG container
    /// as Dalamud's WIC encoder writes it, EXIF block included; it can misbehave on purpose.
    /// </summary>
    private sealed class FakeCodec : IImageCodec
    {
        private readonly Dictionary<string, DecodedImage> decodes = new();
        private readonly Dictionary<string, (int Width, int Height, byte[] Rgba)> jpegs = new();

        internal List<(byte[] Rgba, int Width, int Height, ImageFormat Format)> Encoded { get; } = new();

        internal Func<byte[], int, int, ImageFormat, byte[]?> Encoder { get; init; } = (rgba, width, height, format) => format == ImageFormat.Jpeg ? Jpeg(width, height) : Png(width, height, rgba);

        /// <summary>Whether it hands back premultiplied colour, as a pipeline measured otherwise might.</summary>
        internal bool Premultiplies { get; init; }

        /// <summary>Whether its JPEG path swaps red and blue, as a pipeline measured otherwise might.</summary>
        internal bool SwapsJpegChannels { get; init; }

        internal void Decodes(byte[] file, DecodedImage image) => decodes[Convert.ToHexString(file)] = image;

        public Task<DecodedImage?> DecodeAsync(ReadOnlyMemory<byte> file, CancellationToken cancellation)
        {
            var key = Convert.ToHexString(file.Span);
            if (jpegs.TryGetValue(key, out var jpeg))
            {
                return Task.FromResult<DecodedImage?>(new DecodedImage(jpeg.Width, jpeg.Height, jpeg.Width * 4, ImagePreparer.Rgba, jpeg.Rgba));
            }

            if (!decodes.TryGetValue(key, out var image))
            {
                image = DecodePng(file.Span);
            }

            if (image is not null && Premultiplies)
            {
                var pixels = image.Pixels.ToArray();
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    for (var channel = 0; channel < 3; channel++)
                    {
                        pixels[index + channel] = (byte)Premultiplied(pixels[index + channel], pixels[index + 3]);
                    }
                }

                image = image with { Pixels = pixels };
            }

            return Task.FromResult(image);
        }

        public Task<byte[]?> EncodeAsync(ReadOnlyMemory<byte> rgba, int width, int height, ImageFormat format, CancellationToken cancellation)
        {
            var pixels = rgba.ToArray();
            Encoded.Add((pixels, width, height, format));
            var output = Encoder(pixels, width, height, format);
            if (output is not null && format == ImageFormat.Jpeg && PreparedContainer.Clean(output, format) is { } cleaned)
            {
                // What the copy decodes back to: its pixels, as a lossless stand-in for JPEG's.
                var back = pixels.ToArray();
                if (SwapsJpegChannels)
                {
                    for (var index = 0; index < back.Length; index += 4)
                    {
                        (back[index], back[index + 2]) = (back[index + 2], back[index]);
                    }
                }

                jpegs[Convert.ToHexString(cleaned)] = (width, height, back);
            }

            return Task.FromResult(output);
        }
    }
}
