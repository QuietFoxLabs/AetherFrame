using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AetherFrame.ImageJobs;

/// <summary>A job for the image worker: the declared format and size, and the bytes to re-encode.</summary>
internal sealed record ImageJob(byte Format, int Width, int Height, byte[] Bytes);

/// <summary>
/// The server's and the image worker's one exchange (decision I2), over the Unix socket the server
/// owns: the server sends one job, the worker answers once and exits. Both sides read strictly and
/// within bounds; neither trusts the other's lengths.
/// <code>
/// job:   "AFIJ" | u8 version = 1 | u8 format (1 PNG, 2 JPEG) | u32 width | u32 height | u32 length | bytes
/// reply: "AFIR" | u8 version = 1 | u8 outcome (0 re-encoded, 1 refused) | u32 length | bytes (none when refused)
/// </code>
/// This file is compiled into both the server and the worker.
/// </summary>
internal static class ImageJobWire
{
    /// <summary>The most bytes either side sends or reads as an image: the protocol's image limit.</summary>
    public const int MaxImageBytes = 8 * 1024 * 1024;

    private const byte Version = 1;

    private static ReadOnlySpan<byte> JobMagic => "AFIJ"u8;

    private static ReadOnlySpan<byte> ReplyMagic => "AFIR"u8;

    public static async Task WriteJobAsync(Stream stream, ImageJob job, CancellationToken cancellation)
    {
        var header = new byte[4 + 1 + 1 + 4 + 4 + 4];
        JobMagic.CopyTo(header);
        header[4] = Version;
        header[5] = job.Format;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6), (uint)job.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(10), (uint)job.Height);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(14), (uint)job.Bytes.Length);
        await stream.WriteAsync(header, cancellation);
        await stream.WriteAsync(job.Bytes, cancellation);
        await stream.FlushAsync(cancellation);
    }

    /// <summary>Reads a job, or null for anything but one well-formed job within the bounds.</summary>
    public static async Task<ImageJob?> ReadJobAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[18];
        if (!await ReadExactlyAsync(stream, header, cancellation) || !header.AsSpan(0, 4).SequenceEqual(JobMagic) || header[4] != Version || header[5] is not (1 or 2))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6));
        var height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(10));
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14));
        if (width is 0 or > 8192 || height is 0 or > 8192 || length is 0 or > MaxImageBytes)
        {
            return null;
        }

        var bytes = new byte[length];
        return await ReadExactlyAsync(stream, bytes, cancellation) ? new ImageJob(header[5], (int)width, (int)height, bytes) : null;
    }

    public static async Task WriteReplyAsync(Stream stream, byte[]? output, CancellationToken cancellation)
    {
        var header = new byte[4 + 1 + 1 + 4];
        ReplyMagic.CopyTo(header);
        header[4] = Version;
        header[5] = output is null ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6), (uint)(output?.Length ?? 0));
        await stream.WriteAsync(header, cancellation);
        if (output is not null)
        {
            await stream.WriteAsync(output, cancellation);
        }

        await stream.FlushAsync(cancellation);
    }

    /// <summary>
    /// Reads a reply: the re-encoded bytes, or null for a refusal or for anything malformed, too
    /// long, or cut short. A worker's reply is untrusted.
    /// </summary>
    public static async Task<byte[]?> ReadReplyAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[10];
        if (!await ReadExactlyAsync(stream, header, cancellation) || !header.AsSpan(0, 4).SequenceEqual(ReplyMagic) || header[4] != Version || header[5] != 0)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6));
        if (length is 0 or > MaxImageBytes)
        {
            return null;
        }

        var bytes = new byte[length];
        return await ReadExactlyAsync(stream, bytes, cancellation) ? bytes : null;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellation)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellation);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
