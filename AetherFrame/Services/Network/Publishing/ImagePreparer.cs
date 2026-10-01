using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Assets;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>
/// An image as the texture pipeline decoded it and read it back whole: its size, the distance in
/// bytes between the starts of its rows (<paramref name="Pitch"/>, which may exceed the width's
/// bytes), its DXGI format, the bytes, and whether its colour is premultiplied by its alpha
/// (<paramref name="Premultiplied"/>), which a DXGI format doesn't say.
/// </summary>
internal sealed record DecodedImage(int Width, int Height, int Pitch, int DxgiFormat, byte[] Pixels, bool Premultiplied = false)
{
    /// <summary>A file that decoded, whose pixels couldn't be read back: the texture pipeline failed, not the file, so it isn't missing.</summary>
    internal static readonly DecodedImage ReadBackFailed = new(0, 0, 0, 0, []);
}

/// <summary>
/// What preparing needs from the texture pipeline (the plugin's is Dalamud's, through WIC): decoding
/// a managed image's file and reading it back whole, and encoding tightly packed RGBA pixels with
/// preparation's settings. A failure is a null, never an exception.
/// </summary>
internal interface IImageCodec
{
    /// <summary>
    /// The file's image, decoded and read back whole; null when the file can't be decoded (as the
    /// renderer finds it), and <see cref="DecodedImage.ReadBackFailed"/> when it decoded but its
    /// pixels couldn't be read back.
    /// </summary>
    Task<DecodedImage?> DecodeAsync(ReadOnlyMemory<byte> file, CancellationToken cancellation);

    /// <summary>The pixels (<paramref name="width"/> by <paramref name="height"/>, RGBA, rows packed), encoded in <paramref name="format"/>; null when that fails.</summary>
    Task<byte[]?> EncodeAsync(ReadOnlyMemory<byte> rgba, int width, int height, ImageFormat format, CancellationToken cancellation);
}

/// <summary>A codec that can say, for the log, how its last failure ended: an exception's kind and HRESULT, never its text.</summary>
internal interface IImageCodecFailures
{
    /// <summary>The last failure, or null when there was none.</summary>
    string? LastFailure { get; }
}

/// <summary>The managed images' files, read whole.</summary>
internal interface IManagedImages
{
    /// <summary>The bytes of <paramref name="image"/>'s managed file; null when it is missing or can't be read.</summary>
    byte[]? TryRead(Guid image);
}

/// <summary>
/// Prepares the copies a Plate's candidate needs (NETWORK2's N2-6b; decision D5 and its N2-6 note;
/// decision I1): each required window of a managed image, decoded, cropped, with the colour its
/// alpha hides cleared, encoded again, and checked on what the encoder actually wrote, then declared
/// under a fresh asset id (decision N1). Every outcome is an <see cref="ImagePreparation"/>, which
/// the builder's <c>Map</c> honours. Free of Dalamud: the texture pipeline is behind
/// <see cref="IImageCodec"/>.
/// </summary>
internal static class ImagePreparer
{
    /// <summary>DXGI_FORMAT_R8G8B8A8_UNORM.</summary>
    internal const int Rgba = 28;

    /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM.</summary>
    internal const int Bgra = 87;

    /// <summary>DXGI_FORMAT_B8G8R8X8_UNORM: no alpha, drawn opaque.</summary>
    internal const int Bgrx = 88;

    /// <summary>DXGI_FORMAT_R16G16B16A16_UNORM: a 16-bit PNG, which the texture pipeline keeps at 16 bits a channel.</summary>
    internal const int Rgba64 = 11;

    /// <summary>Every requirement's preparation, one image at a time, so only one is ever decoded at once.</summary>
    internal static async Task<IReadOnlyDictionary<ImageRequirement, ImagePreparation>> PrepareAllAsync(
        IReadOnlyList<ImageRequirement> requirements, IManagedImages images, IImageCodec codec, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var preparations = new Dictionary<ImageRequirement, ImagePreparation>(requirements.Count);
        foreach (var requirement in requirements)
        {
            preparations[requirement] = await PrepareAsync(requirement, images, codec, cancellation).ConfigureAwait(false);
        }

        return preparations;
    }

    /// <summary>
    /// One required window's copy, or why there is none:
    /// <list type="bullet">
    /// <item>over I1's limits (8,192 pixels a side, 20,000,000 in all, 8 MiB encoded): too large;</item>
    /// <item>no file, or a file that doesn't decode: missing, as the renderer finds it;</item>
    /// <item>decoded at another size than the window was computed against (an animation's first
    /// frame can be smaller than its canvas): the size changed;</item>
    /// <item>anything else that fails: unshareable.</item>
    /// </list>
    /// </summary>
    internal static async Task<ImagePreparation> PrepareAsync(ImageRequirement requirement, IManagedImages images, IImageCodec codec, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(codec);
        var window = requirement.Window;
        if (window.Width > ProtocolLimits.MaxImageDimension || window.Height > ProtocolLimits.MaxImageDimension
            || (long)window.Width * window.Height > ProtocolLimits.MaxImagePixels)
        {
            return ImagePreparation.Unavailable(ImageUnavailableReason.TooLarge);
        }

        if (images.TryRead(requirement.Image) is not { } file)
        {
            return ImagePreparation.Unavailable(ImageUnavailableReason.Missing);
        }

        if (await codec.DecodeAsync(file, cancellation).ConfigureAwait(false) is not { } decoded)
        {
            return ImagePreparation.Unavailable(ImageUnavailableReason.Missing);
        }

        // The renderer draws a file that decodes; one the pipeline then failed to read back is
        // refused, never left out as if it were missing.
        if (ReferenceEquals(decoded, DecodedImage.ReadBackFailed))
        {
            return ImagePreparation.Unavailable(ImageUnavailableReason.Unshareable);
        }

        if (decoded.Width != requirement.SourceWidth || decoded.Height != requirement.SourceHeight)
        {
            return ImagePreparation.Unavailable(ImageUnavailableReason.SizeChanged);
        }

        if (!TryCrop(decoded, window, out var rgba, out var opaque))
        {
            return ImagePreparation.Unavailable(ImageUnavailableReason.Unshareable);
        }

        return await EncodeAsync(rgba, window.Width, window.Height, Choose(file, opaque), codec, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// JPEG for an opaque window of a JPEG, PNG otherwise (N2-6's design): a JPEG source is already
    /// lossy, so encoding it again as JPEG keeps its copy small, while anything with alpha, or drawn
    /// losslessly, stays lossless.
    /// </summary>
    internal static ImageFormat Choose(ReadOnlySpan<byte> file, bool opaque) =>
        opaque && ImageSafety.Sniff(file) == DetectedImageFormat.Jpeg ? ImageFormat.Jpeg : ImageFormat.Png;

    /// <summary>
    /// The pixels of <paramref name="window"/>, tightly packed as 8-bit RGBA, with the colour alpha
    /// hides cleared (<see cref="Clear"/>), and whether every one of them is opaque. Each is what the
    /// renderer draws from the texture: BGRX as opaque, and a 16-bit channel c as round(c/257), as
    /// an 8-bit target receives it. Rows are read by the read-back's pitch, so no padding byte is
    /// ever copied. Premultiplied colour (<see cref="DecodedImage.Premultiplied"/>) is made straight
    /// again (<see cref="Straight"/>), which gives exactly what <see cref="Clear"/> gives for the
    /// file's own colour when the pipeline rounded as <see cref="Clear"/> does; the known-answer
    /// check proves that each session. A premultiplied 16-bit window with a translucent pixel is
    /// refused: no pipeline was measured doing that. False for a read-back in any other format
    /// (sRGB, float, block-compressed), a buffer too short for its size, or a window outside it.
    /// </summary>
    internal static bool TryCrop(DecodedImage image, PixelWindow window, out byte[] rgba, out bool opaque)
    {
        ArgumentNullException.ThrowIfNull(image);
        rgba = [];
        opaque = false;
        var size = image.DxgiFormat switch
        {
            Rgba or Bgra or Bgrx => 4,
            Rgba64 => 8,
            _ => 0,
        };
        if (size == 0 || image.Width <= 0 || image.Height <= 0
            || (long)image.Pitch < (long)image.Width * size
            || image.Pixels.LongLength < ((long)(image.Height - 1) * image.Pitch) + ((long)image.Width * size)
            || window.X < 0 || window.Y < 0 || window.Width <= 0 || window.Height <= 0
            || (long)window.X + window.Width > image.Width || (long)window.Y + window.Height > image.Height)
        {
            return false;
        }

        var output = new byte[window.Width * window.Height * 4];
        var allOpaque = true;
        var at = 0;
        for (var y = window.Y; y < window.Y + window.Height; y++)
        {
            var row = image.Pixels.AsSpan((y * image.Pitch) + (window.X * size), window.Width * size);
            for (var x = 0; x < row.Length; x += size)
            {
                var (red, green, blue, alpha) = image.DxgiFormat switch
                {
                    Rgba => (row[x], row[x + 1], row[x + 2], row[x + 3]),
                    Bgra => (row[x + 2], row[x + 1], row[x], row[x + 3]),
                    Bgrx => (row[x + 2], row[x + 1], row[x], (byte)255),
                    _ => (Narrow(row[x..]), Narrow(row[(x + 2)..]), Narrow(row[(x + 4)..]), Narrow(row[(x + 6)..])),
                };
                if (!image.Premultiplied)
                {
                    output[at] = Clear(red, alpha);
                    output[at + 1] = Clear(green, alpha);
                    output[at + 2] = Clear(blue, alpha);
                }
                else if (size == 8 && alpha is > 0 and < 255)
                {
                    return false;
                }
                else
                {
                    output[at] = Straight(red, alpha);
                    output[at + 1] = Straight(green, alpha);
                    output[at + 2] = Straight(blue, alpha);
                }

                output[at + 3] = alpha;
                allOpaque &= alpha == 255;
                at += 4;
            }
        }

        rgba = output;
        opaque = allOpaque;
        return true;
    }

    /// <summary>A little-endian 16-bit channel as an 8-bit one: round(c/257), which never ties.</summary>
    private static byte Narrow(ReadOnlySpan<byte> channel) => (byte)((BinaryPrimitives.ReadUInt16LittleEndian(channel) + 128) / 257);

    /// <summary>
    /// A straight-alpha channel with what its alpha hides removed (D5's N2-6 note, (2)):
    /// round(round(c*a/255)*255/a), and 0 where a is 0. Drawn over anything, it looks as before
    /// (its premultiplied value is the same), and opaque colour is unchanged.
    /// </summary>
    internal static byte Clear(byte channel, byte alpha)
    {
        if (alpha == 0)
        {
            return 0;
        }

        var premultiplied = ((2 * channel * alpha) + 255) / 510;
        return (byte)(((2 * premultiplied * 255) + alpha) / (2 * alpha));
    }

    /// <summary>
    /// A premultiplied channel made straight: round(p*255/a), at most 255, and 0 where a is 0. For
    /// p = round(c*a/255) it is <see cref="Clear"/>(c, a).
    /// </summary>
    internal static byte Straight(byte premultiplied, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, ((2 * premultiplied * 255) + alpha) / (2 * alpha));

    /// <summary>
    /// The per-session known-answer check (D5's N2-6 note, (3)). A small PNG with every kind of
    /// alpha, prepared, must decode back to exactly the pixels preparation computed, with nothing
    /// but IHDR, IDAT and IEND left; an opaque colour encoded as JPEG must hold exactly the allowed
    /// segments and decode back to that colour, channel for channel, within JPEG's loss. Either
    /// failing turns publishing off for the session, as K3's probe does for keys: the texture
    /// pipeline isn't the one preparation was written for. Its caller treats an exception the same.
    /// </summary>
    internal static async Task<bool> SelfTestAsync(IImageCodec codec, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var (width, height, pixels) = KnownImage();
        var whole = PixelWindow.Whole(width, height);
        var png = await PrepareAsync(new ImageRequirement(Guid.Empty, whole, width, height), new OneImage(KnownPng()), codec, cancellation).ConfigureAwait(false);
        if (png.Copy is not { } copy || copy.Reference.Format != ImageFormat.Png
            || await codec.DecodeAsync(copy.Bytes, cancellation).ConfigureAwait(false) is not { } back
            || !TryCrop(back, whole, out var roundTrip, out _))
        {
            return false;
        }

        if (!roundTrip.AsSpan().SequenceEqual(Cleared(pixels)))
        {
            return false;
        }

        // One colour whose channels are far apart, so a swap or a conversion shows past JPEG's loss.
        var opaque = new byte[8 * 8 * 4];
        for (var index = 0; index < opaque.Length; index += 4)
        {
            (opaque[index], opaque[index + 1], opaque[index + 2], opaque[index + 3]) = (KnownJpegColour.R, KnownJpegColour.G, KnownJpegColour.B, 255);
        }

        var jpeg = await EncodeAsync(opaque, 8, 8, ImageFormat.Jpeg, codec, cancellation).ConfigureAwait(false);
        if (jpeg.Copy is not { Reference.Format: ImageFormat.Jpeg } jpegCopy
            || await codec.DecodeAsync(jpegCopy.Bytes, cancellation).ConfigureAwait(false) is not { } jpegBack
            || !TryCrop(jpegBack, PixelWindow.Whole(8, 8), out var jpegPixels, out var jpegOpaque) || !jpegOpaque)
        {
            return false;
        }

        for (var index = 0; index < jpegPixels.Length; index += 4)
        {
            if (Math.Abs(jpegPixels[index] - KnownJpegColour.R) > JpegTolerance
                || Math.Abs(jpegPixels[index + 1] - KnownJpegColour.G) > JpegTolerance
                || Math.Abs(jpegPixels[index + 2] - KnownJpegColour.B) > JpegTolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The known JPEG's one colour.</summary>
    internal static readonly (byte R, byte G, byte B) KnownJpegColour = (210, 120, 40);

    /// <summary>How far a channel of the known JPEG may come back from its colour: a flat colour at quality 0.92 loses a few levels at most.</summary>
    internal const int JpegTolerance = 8;

    /// <summary>
    /// Encodes, checks and declares one copy: what the encoder wrote must hold exactly the allowed
    /// inventory (<see cref="PreparedContainer"/>), fit I1's 8 MiB, and pass section 8.2.1 as every
    /// consumer will apply it, at exactly its window's size.
    /// </summary>
    private static async Task<ImagePreparation> EncodeAsync(byte[] rgba, int width, int height, ImageFormat format, IImageCodec codec, CancellationToken cancellation)
    {
        if (await codec.EncodeAsync(rgba, width, height, format, cancellation).ConfigureAwait(false) is not { } encoded
            || PreparedContainer.Clean(encoded, format) is not { } copy)
        {
            return ImagePreparation.Unavailable(ImageUnavailableReason.Unshareable);
        }

        if (copy.Length > ProtocolLimits.MaxImageBytes)
        {
            return ImagePreparation.Unavailable(ImageUnavailableReason.TooLarge);
        }

        try
        {
            if (ImageSniffer.Sniff(copy) != new SniffedImage(format, width, height))
            {
                return ImagePreparation.Unavailable(ImageUnavailableReason.Unshareable);
            }
        }
        catch (ProtocolException)
        {
            return ImagePreparation.Unavailable(ImageUnavailableReason.Unshareable);
        }

        var reference = new ImageReference(AssetId.NewId(), SHA256.HashData(copy), format, copy.Length, width, height);
        return ImagePreparation.Prepared(new PreparedImage(reference, copy));
    }

    /// <summary>The known image: 4 by 2, straight RGBA, with colour under alpha 0, 1, 2, 64, 128, 200, 254 and 255.</summary>
    internal static (int Width, int Height, byte[] Pixels) KnownImage() =>
        (4, 2,
        [
            200, 10, 60, 0, 17, 250, 3, 1, 255, 255, 255, 2, 90, 180, 45, 64,
            1, 2, 3, 128, 250, 128, 7, 200, 33, 66, 99, 254, 12, 34, 56, 255,
        ]);

    /// <summary>The known image as a PNG: 8-bit RGBA, not interlaced, rows unfiltered, deflated.</summary>
    internal static byte[] KnownPng()
    {
        var (width, height, pixels) = KnownImage();
        var rows = new byte[height * (1 + (width * 4))];
        for (var y = 0; y < height; y++)
        {
            pixels.AsSpan(y * width * 4, width * 4).CopyTo(rows.AsSpan((y * (1 + (width * 4))) + 1));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(rows);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        (header[8], header[9]) = (8, 6);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(field, (uint)data.Length);
        stream.Write(field);
        stream.Write(type);
        stream.Write(data);
        var crc = Crc32(Crc32(0xFFFF_FFFFu, type), data) ^ 0xFFFF_FFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(field, crc);
        stream.Write(field);
    }

    /// <summary>PNG's CRC-32 (ISO 3309), continued over <paramref name="bytes"/>.</summary>
    private static uint Crc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
            }
        }

        return crc;
    }

    /// <summary>
    /// Where the known-answer check went wrong, step by step, for the log once it has failed: each
    /// step's formats, sizes, PNG chunks or JPEG segments, and the known image's own pixels, so one
    /// failure in game says what the texture pipeline did. It holds nothing of a player's. A
    /// codec failure is named by <see cref="IImageCodecFailures"/>: an exception's kind, never its
    /// text.
    /// </summary>
    internal static async Task<string> DiagnoseAsync(IImageCodec codec, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var (width, height, pixels) = KnownImage();
        var expected = Cleared(pixels);
        var notes = new List<string>();

        var decoded = await codec.DecodeAsync(KnownPng(), cancellation).ConfigureAwait(false);
        var known = "known PNG decoded: " + Describe(decoded, codec);
        if (decoded is not null && !ReferenceEquals(decoded, DecodedImage.ReadBackFailed))
        {
            known += ", first bytes " + Convert.ToHexString(decoded.Pixels.AsSpan(0, Math.Min(decoded.Pixels.Length, 32)));
            known += TryCrop(decoded, PixelWindow.Whole(width, height), out var cropped, out _)
                ? ", cleared " + Convert.ToHexString(cropped) + (cropped.AsSpan().SequenceEqual(expected) ? " as expected" : ", expected " + Convert.ToHexString(expected))
                : ", its format refused";
        }

        notes.Add(known);
        notes.Add(await DescribeEncodingAsync(expected, width, height, ImageFormat.Png, codec, cancellation).ConfigureAwait(false));

        var opaque = new byte[8 * 8 * 4];
        for (var index = 0; index < opaque.Length; index += 4)
        {
            (opaque[index], opaque[index + 1], opaque[index + 2], opaque[index + 3]) = (KnownJpegColour.R, KnownJpegColour.G, KnownJpegColour.B, 255);
        }

        notes.Add(await DescribeEncodingAsync(opaque, 8, 8, ImageFormat.Jpeg, codec, cancellation).ConfigureAwait(false));
        return string.Join("; ", notes);
    }

    /// <summary>The known image's pixels as preparation clears them: what its copy must decode back to.</summary>
    private static byte[] Cleared(byte[] pixels)
    {
        var cleared = new byte[pixels.Length];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            (cleared[index], cleared[index + 1], cleared[index + 2], cleared[index + 3]) =
                (Clear(pixels[index], alpha), Clear(pixels[index + 1], alpha), Clear(pixels[index + 2], alpha), alpha);
        }

        return cleared;
    }

    /// <summary>One encoding of the diagnosis: what the encoder wrote, whether its container and its sniffed header pass, and what it decodes back to.</summary>
    private static async Task<string> DescribeEncodingAsync(byte[] rgba, int width, int height, ImageFormat format, IImageCodec codec, CancellationToken cancellation)
    {
        var name = format == ImageFormat.Png ? "PNG" : "JPEG";
        if (await codec.EncodeAsync(rgba, width, height, format, cancellation).ConfigureAwait(false) is not { } encoded)
        {
            return name + " encoded: failed" + Failure(codec);
        }

        var text = name + " encoded: " + encoded.Length + " bytes, " + (format == ImageFormat.Png ? PngChunks(encoded) : JpegSegments(encoded));
        if (PreparedContainer.Clean(encoded, format) is not { } copy)
        {
            return text + ", its container refused";
        }

        try
        {
            var sniffed = ImageSniffer.Sniff(copy);
            text += ", sniffed " + sniffed.Format + " " + sniffed.Width + "x" + sniffed.Height;
        }
        catch (ProtocolException e)
        {
            return text + ", refused by the image rule (" + e.Error + ")";
        }

        var back = await codec.DecodeAsync(copy, cancellation).ConfigureAwait(false);
        text += ", decoded back: " + Describe(back, codec);
        if (back is not null && !ReferenceEquals(back, DecodedImage.ReadBackFailed) && TryCrop(back, PixelWindow.Whole(width, height), out var again, out _))
        {
            text += ", " + Convert.ToHexString(again.AsSpan(0, Math.Min(again.Length, 32)));
        }

        return text;
    }

    private static string Describe(DecodedImage? image, IImageCodec codec) =>
        image is null ? "no" + Failure(codec)
        : ReferenceEquals(image, DecodedImage.ReadBackFailed) ? "read-back failed" + Failure(codec)
        : image.Width + "x" + image.Height + ", pitch " + image.Pitch + ", DXGI format " + image.DxgiFormat + (image.Premultiplied ? ", premultiplied" : "");

    private static string Failure(IImageCodec codec) => codec is IImageCodecFailures { LastFailure: { } failure } ? " (" + failure + ")" : "";

    /// <summary>A PNG's chunks in order, with their lengths: the first 24.</summary>
    private static string PngChunks(byte[] png)
    {
        var chunks = new List<string>();
        var at = 8;
        while (at + 8 <= png.Length && chunks.Count < 24)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            var type = new char[4];
            for (var index = 0; index < 4; index++)
            {
                var value = png[at + 4 + index];
                type[index] = value is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') ? (char)value : '?';
            }

            chunks.Add(new string(type) + "(" + length + ")");
            if (length > (uint)(png.Length - at))
            {
                break;
            }

            at += 12 + (int)length;
        }

        return "chunks " + string.Join(" ", chunks);
    }

    /// <summary>A JPEG's markers up to its scan, and its last two bytes.</summary>
    private static string JpegSegments(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return "no SOI";
        }

        var markers = new List<string> { "D8" };
        var at = 2;
        while (at + 4 <= jpeg.Length && markers.Count < 24)
        {
            if (jpeg[at] != 0xFF)
            {
                markers.Add("?");
                break;
            }

            var marker = jpeg[at + 1];
            markers.Add(marker.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            if (marker == 0xDA)
            {
                break;
            }

            at += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(at + 2));
        }

        return "markers " + string.Join(" ", markers) + ", ends " + Convert.ToHexString(jpeg.AsSpan(jpeg.Length - 2));
    }

    /// <summary>The known image, as the one managed image the check reads.</summary>
    private sealed class OneImage : IManagedImages
    {
        private readonly byte[] file;

        internal OneImage(byte[] file) => this.file = file;

        public byte[]? TryRead(Guid image) => file;
    }
}
