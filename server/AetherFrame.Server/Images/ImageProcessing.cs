using System;
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
/// (N2-7c); until the worker exists, <see cref="NoImageProcessor"/> refuses every image, so a Plate
/// with images isn't published rather than served unprocessed.
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

/// <summary>Refuses every image: the server's processor until the image worker (N2-7c) is configured.</summary>
internal sealed class NoImageProcessor : IImageProcessor
{
    public Task<ImageProcessing> ProcessAsync(ImageReference declared, ReadOnlyMemory<byte> bytes, CancellationToken cancellation) => Task.FromResult(ImageProcessing.Refused);
}

/// <summary>The server's own check of a processor's output (decision I2: the worker's output is untrusted).</summary>
internal static class ProcessedImages
{
    /// <summary>
    /// Whether <paramref name="output"/> is an image section 8.2.1 accepts, of the declared format
    /// and dimensions, within the byte limit. N2-7c adds the exact inventory of chunks or segments
    /// the worker's encoder writes.
    /// </summary>
    public static bool Check(ReadOnlySpan<byte> output, ImageReference declared)
    {
        if (output.Length is 0 or > (int)ProtocolLimits.MaxImageBytes)
        {
            return false;
        }

        try
        {
            var sniffed = ImageSniffer.Sniff(output);
            return sniffed.Format == declared.Format && sniffed.Width == declared.Width && sniffed.Height == declared.Height;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }
}
