using System;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Server.Images;

/// <summary>
/// Decodes an image and encodes it again (decision I2). The server's is the image worker's client
/// (N2-7c); until the worker exists, <see cref="NoImageProcessor"/> refuses every image, so a Plate
/// with images isn't published rather than served unprocessed.
/// </summary>
internal interface IImageProcessor
{
    /// <summary>
    /// The re-encoded copy of <paramref name="bytes"/>, which have already matched their declaration
    /// (section 8.2.1), or null when the processor refuses them. Its answer is untrusted:
    /// <see cref="ProcessedImages.Check"/> checks it before anything is stored.
    /// </summary>
    Task<byte[]?> ProcessAsync(ImageReference declared, ReadOnlyMemory<byte> bytes, CancellationToken cancellation);
}

/// <summary>Refuses every image: the server's processor until the image worker (N2-7c) is configured.</summary>
internal sealed class NoImageProcessor : IImageProcessor
{
    public Task<byte[]?> ProcessAsync(ImageReference declared, ReadOnlyMemory<byte> bytes, CancellationToken cancellation) => Task.FromResult<byte[]?>(null);
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
