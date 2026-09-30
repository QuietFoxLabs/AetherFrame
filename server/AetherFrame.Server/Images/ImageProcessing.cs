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
/// section 8.2.1, match the declared format and size, and be exactly what the worker's encoder
/// (ImageSharp 3.1.12, pinned by the worker's lock file) writes, and nothing else:
/// <list type="bullet">
/// <item>a PNG: <c>IHDR</c> (8-bit RGBA, not interlaced), then only <c>IDAT</c>, then <c>IEND</c>;</item>
/// <item>a JPEG: SOI, then the encoder's fixed JFIF header, a baseline frame of the declared size with
/// its fixed 4:2:0 components, its fixed Huffman and quantization tables and its fixed scan header,
/// byte for byte, then entropy-coded data with no marker in it, then EOI. So a compromised worker
/// can't hand viewers tables, thumbnails or segments of its own.</item>
/// </list>
/// Every read is bounded by the bytes, whatever section 8.2.1 has already checked.
/// </summary>
internal static class ProcessedImages
{
    // What ImageSharp 3.1.12's JpegEncoder writes at quality 90, 4:2:0, interleaved, without metadata:
    // the same bytes at every size, apart from the frame's height and width.
    private static readonly byte[] JfifHeader = Convert.FromHexString("FFE000104A46494600010101006000600000");
    private static readonly byte[] HuffmanTables = Convert.FromHexString(
        "FFC401A20000010501010101010100000000000000000102030405060708090A0B100002010303020403050504040000" +
        "017D01020300041105122131410613516107227114328191A1082342B1C11552D1F02433627282090A161718191A2526" +
        "2728292A3435363738393A434445464748494A535455565758595A636465666768696A737475767778797A8384858687" +
        "88898A92939495969798999AA2A3A4A5A6A7A8A9AAB2B3B4B5B6B7B8B9BAC2C3C4C5C6C7C8C9CAD2D3D4D5D6D7D8D9DA" +
        "E1E2E3E4E5E6E7E8E9EAF1F2F3F4F5F6F7F8F9FA0100030101010101010101010000000000000102030405060708090A" +
        "0B1100020102040403040705040400010277000102031104052131061241510761711322328108144291A1B1C1092333" +
        "52F0156272D10A162434E125F11718191A262728292A35363738393A434445464748494A535455565758595A63646566" +
        "6768696A737475767778797A82838485868788898A92939495969798999AA2A3A4A5A6A7A8A9AAB2B3B4B5B6B7B8B9BA" +
        "C2C3C4C5C6C7C8C9CAD2D3D4D5D6D7D8D9DAE2E3E4E5E6E7E8E9EAF2F3F4F5F6F7F8F9FA");
    private static readonly byte[] QuantizationTables = Convert.FromHexString(
        "FFDB0084000302020302020303030304030304050805050404050A070706080C0A0C0C0B0A0B0B0D0E12100D0E110E0B" +
        "0B1016101113141515150C0F1718161418121415140103040405040509050509140D0B0D141414141414141414141414" +
        "1414141414141414141414141414141414141414141414141414141414141414141414141414");
    private static readonly byte[] ScanHeader = Convert.FromHexString("FFDA000C03010002110311003F00");
    private static readonly byte[] FrameStart = Convert.FromHexString("FFC0001108");
    private static readonly byte[] FrameComponents = Convert.FromHexString("03012200021101031101");

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

        return declared.Format == ImageFormat.Png ? IsWorkerPng(output) : IsWorkerJpeg(output, declared.Width, declared.Height);
    }

    /// <summary>The PNG chunk inventory: IHDR (8-bit RGBA, not interlaced), one or more IDAT, IEND at the very end.</summary>
    internal static bool IsWorkerPng(ReadOnlySpan<byte> png)
    {
        var position = 8;
        var index = 0;
        var sawData = false;
        while (true)
        {
            if (png.Length - position < 12)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            if (length > (uint)(png.Length - position - 12))
            {
                return false;
            }

            var type = png.Slice(position + 4, 4);
            var data = png.Slice(position + 8, (int)length);
            if (index == 0)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13 || data[8] != 8 || data[9] != 6 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    return false;
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                sawData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return sawData && length == 0 && position + 12 == png.Length;
            }
            else
            {
                return false;
            }

            position += 12 + (int)length;
            index++;
        }
    }

    /// <summary>The JPEG, segment by segment against the encoder's fixed bytes.</summary>
    internal static bool IsWorkerJpeg(ReadOnlySpan<byte> jpeg, int width, int height)
    {
        var rest = jpeg;
        if (!Take(ref rest, [0xFF, 0xD8]) || !Take(ref rest, JfifHeader) || !Take(ref rest, FrameStart))
        {
            return false;
        }

        if (rest.Length < 4 || BinaryPrimitives.ReadUInt16BigEndian(rest) != height || BinaryPrimitives.ReadUInt16BigEndian(rest[2..]) != width)
        {
            return false;
        }

        rest = rest[4..];
        if (!Take(ref rest, FrameComponents) || !Take(ref rest, HuffmanTables) || !Take(ref rest, QuantizationTables) || !Take(ref rest, ScanHeader))
        {
            return false;
        }

        // Entropy-coded data, then EOI as the last two bytes. The encoder writes no restart
        // intervals, so every FF in the data is a stuffed FF 00.
        if (rest.Length < 2 || rest[^2] != 0xFF || rest[^1] != 0xD9)
        {
            return false;
        }

        var data = rest[..^2];
        for (var index = 0; index < data.Length; index++)
        {
            if (data[index] == 0xFF)
            {
                if (index + 1 >= data.Length || data[index + 1] != 0x00)
                {
                    return false;
                }

                index++;
            }
        }

        return true;
    }

    private static bool Take(ref ReadOnlySpan<byte> rest, ReadOnlySpan<byte> expected)
    {
        if (!rest.StartsWith(expected))
        {
            return false;
        }

        rest = rest[expected.Length..];
        return true;
    }
}
