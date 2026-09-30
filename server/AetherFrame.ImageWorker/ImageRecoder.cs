using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;

namespace AetherFrame.ImageWorker;

/// <summary>The two formats a shared image can be (decision I1), as the job's format byte names them.</summary>
public enum JobFormat : byte
{
    Png = 1,
    Jpeg = 2,
}

/// <summary>
/// Decodes an image and encodes it again (decision I2), assuming the decoder can fail badly: only
/// the PNG and JPEG codecs are configured, the image is identified first and must match what the
/// server declared, one frame is read with no metadata, buffers come from the GC heap (so the
/// process's heap limit bounds them), and the encoding is fixed: 8-bit RGBA non-interlaced PNG, or
/// baseline 4:2:0 JPEG, with no metadata block, no rotation and no colour profile applied.
/// </summary>
public static class ImageRecoder
{
    /// <summary>The JPEG quality the worker encodes at.</summary>
    public const int JpegQuality = 90;

    private static readonly Configuration Codecs = new(new PngConfigurationModule(), new JpegConfigurationModule())
    {
        MemoryAllocator = new SimpleGcMemoryAllocator(),
    };

    private static readonly DecoderOptions Options = new()
    {
        Configuration = Codecs,
        MaxFrames = 1,
        SkipMetadata = true,
    };

    /// <summary>
    /// The re-encoded copy of <paramref name="input"/>, or null when it isn't an image of
    /// <paramref name="format"/> at <paramref name="width"/> by <paramref name="height"/>, or can't
    /// be decoded.
    /// </summary>
    public static byte[]? Recode(JobFormat format, int width, int height, byte[] input)
    {
        try
        {
            var info = Image.Identify(Options, input);
            var expected = format == JobFormat.Png ? PngFormat.Instance : (IImageFormat)JpegFormat.Instance;
            if (info.Metadata.DecodedImageFormat != expected || info.Width != width || info.Height != height || info.FrameMetadataCollection.Count > 1)
            {
                return null;
            }

            using var image = Image.Load<Rgba32>(Options, input);
            if (image.Width != width || image.Height != height)
            {
                return null;
            }

            using var output = new MemoryStream();
            if (format == JobFormat.Png)
            {
                image.Save(output, new PngEncoder
                {
                    BitDepth = PngBitDepth.Bit8,
                    ColorType = PngColorType.RgbWithAlpha,
                    InterlaceMethod = PngInterlaceMode.None,
                    SkipMetadata = true,
                    ChunkFilter = PngChunkFilter.ExcludeAll,
                });
            }
            else
            {
                image.Save(output, new JpegEncoder
                {
                    Quality = JpegQuality,
                    ColorType = JpegEncodingColor.YCbCrRatio420,
                    Interleaved = true,
                    SkipMetadata = true,
                });
            }

            return output.ToArray();
        }
        catch (Exception e) when (e is ImageFormatException or UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            return null;
        }
    }
}
