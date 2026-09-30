using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Server.Images;

/// <summary>What re-encoding one image came to.</summary>
internal sealed record ImageProcessing(byte[]? Bytes, bool IsBusy)
{
    /// <summary>The worker's queue is full, or no worker came: try again later.</summary>
    public static readonly ImageProcessing Busy = new(null, true);

    /// <summary>The worker refused the image, or failed on it.</summary>
    public static readonly ImageProcessing Refused = new(null, false);

    public static ImageProcessing Recoded(byte[] bytes) => new(bytes, false);
}

/// <summary>
/// Decodes an image and encodes it again (decision I2). The server's is the image worker's client
/// (<see cref="ImageWorkerClient"/>) when a worker socket is configured; otherwise
/// <see cref="NoImageProcessor"/> refuses every image, so a Plate with images isn't published rather
/// than served unprocessed.
/// </summary>
internal interface IImageProcessor
{
    /// <summary>
    /// Re-encodes <paramref name="bytes"/>, which have already matched their declaration (section
    /// 8.2.1). The answer is untrusted: <see cref="ProcessedImages.Check"/> checks it before anything
    /// is stored.
    /// </summary>
    Task<ImageProcessing> ProcessAsync(ImageReference declared, ReadOnlyMemory<byte> bytes, CancellationToken cancellation);
}

/// <summary>Refuses every image: the server's processor when no image worker socket is configured.</summary>
internal sealed class NoImageProcessor : IImageProcessor
{
    public Task<ImageProcessing> ProcessAsync(ImageReference declared, ReadOnlyMemory<byte> bytes, CancellationToken cancellation) => Task.FromResult(ImageProcessing.Refused);
}

/// <summary>
/// The server's own check of the worker's output (decision I2: it is untrusted). The bytes must pass
/// section 8.2.1, match the declared format and size, and hold exactly what the worker's encoder
/// writes, and nothing else:
/// <list type="bullet">
/// <item>a PNG: <c>IHDR</c> (8-bit, RGBA, not interlaced), one or more <c>IDAT</c>, <c>IEND</c>;</item>
/// <item>a JPEG: SOI, a JFIF <c>APP0</c>, SOF0 with 3 components, one DHT, one DQT, one SOS, EOI.</item>
/// </list>
/// </summary>
internal static class ProcessedImages
{
    public static bool Check(ReadOnlySpan<byte> output, ImageReference declared)
    {
        if (output.Length is 0 or > (int)ProtocolLimits.MaxImageBytes)
        {
            return false;
        }

        try
        {
            var sniffed = ImageSniffer.Sniff(output);
            if (sniffed.Format != declared.Format || sniffed.Width != declared.Width || sniffed.Height != declared.Height)
            {
                return false;
            }
        }
        catch (ProtocolException)
        {
            return false;
        }

        return declared.Format == ImageFormat.Png ? IsWorkerPng(output) : IsWorkerJpeg(output);
    }

    /// <summary>The PNG chunk inventory. The bytes have passed section 8.2.1, so the walk stays inside them.</summary>
    internal static bool IsWorkerPng(ReadOnlySpan<byte> png)
    {
        var position = 8;
        var types = new List<string>();
        while (position + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            var type = System.Text.Encoding.ASCII.GetString(png.Slice(position + 4, 4));
            if (type == "IHDR" && (png[position + 16] != 8 || png[position + 17] != 6 || png[position + 20] != 0))
            {
                return false;
            }

            types.Add(type);
            position += 12 + length;
        }

        if (types.Count < 3 || types[0] != "IHDR" || types[^1] != "IEND")
        {
            return false;
        }

        for (var index = 1; index < types.Count - 1; index++)
        {
            if (types[index] != "IDAT")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The JPEG segment inventory, walked as section 8.2.1 walks it.</summary>
    internal static bool IsWorkerJpeg(ReadOnlySpan<byte> jpeg)
    {
        var markers = new List<byte> { 0xD8 };
        var position = 2;
        while (position + 1 < jpeg.Length)
        {
            if (jpeg[position] != 0xFF)
            {
                return false;
            }

            var code = jpeg[position + 1];
            markers.Add(code);
            if (code == 0xD9)
            {
                break;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(position + 2)..]);
            var body = jpeg.Slice(position + 4, length - 2);
            if (code == 0xE0 && !body.StartsWith("JFIF\0"u8))
            {
                return false;
            }

            if (code == 0xC0 && body[5] != 3)
            {
                return false;
            }

            position += 2 + length;
            if (code == 0xDA)
            {
                while (position + 1 < jpeg.Length && !(jpeg[position] == 0xFF && jpeg[position + 1] != 0x00 && jpeg[position + 1] is < 0xD0 or > 0xD7))
                {
                    position++;
                }
            }
        }

        return markers.SequenceEqual(new byte[] { 0xD8, 0xE0, 0xC0, 0xC4, 0xDB, 0xDA, 0xD9 });
    }
}
