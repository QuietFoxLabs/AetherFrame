using System;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// What a remote profile states about one of its source images: the remote asset id, the SHA-256
/// of the source bytes, the format, the byte length and the pixel size, each within
/// <see cref="ProtocolLimits"/>. It is a declaration the client signs, which a server checks
/// against the bytes it actually receives; it carries no file name, no local id and no path.
/// Immutable.
/// </summary>
public sealed class ImageReference
{
    private readonly byte[] sha256;

    /// <summary>Builds a reference, refusing any value outside the limits.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/> or <see cref="ProtocolError.LimitExceeded"/>.</exception>
    public ImageReference(AssetId assetId, ReadOnlySpan<byte> sha256, ImageFormat format, long byteLength, int width, int height)
    {
        if (assetId.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "An image's asset id is never empty.");
        }

        if (sha256.Length != ProtocolConstants.DigestLength)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"An image digest is {ProtocolText.Number(ProtocolConstants.DigestLength)} bytes; this one is {ProtocolText.Number(sha256.Length)}.");
        }

        // Copied before it is checked, so the digest this holds is the one that passed.
        var digest = sha256.ToArray();
        if (digest.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "An image digest is never all zero.");
        }

        if (!ImageFormats.IsKnown(format))
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"Image format {ProtocolText.Number((byte)format)} is not known.");
        }

        if (byteLength < 1)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "An image's byte length is at least 1.");
        }

        if (byteLength > ProtocolLimits.MaxImageBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"An image declares {ProtocolText.Number(byteLength)} bytes; the limit is {ProtocolText.Number(ProtocolLimits.MaxImageBytes)}.");
        }

        CheckDimension(width, "width");
        CheckDimension(height, "height");
        if ((long)width * height > ProtocolLimits.MaxImagePixels)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"An image declares {ProtocolText.Number((long)width * height)} pixels; the limit is {ProtocolText.Number(ProtocolLimits.MaxImagePixels)}.");
        }

        AssetId = assetId;
        this.sha256 = digest;
        Format = format;
        ByteLength = byteLength;
        Width = width;
        Height = height;
    }

    /// <summary>The remote asset id.</summary>
    public AssetId AssetId { get; }

    /// <summary>SHA-256 of the source image bytes.</summary>
    public ReadOnlySpan<byte> Sha256 => sha256;

    /// <summary>The sniffed container format.</summary>
    public ImageFormat Format { get; }

    /// <summary>The source image's size in bytes, 1 to <see cref="ProtocolLimits.MaxImageBytes"/>.</summary>
    public long ByteLength { get; }

    /// <summary>The source image's width in pixels, 1 to <see cref="ProtocolLimits.MaxImageDimension"/>.</summary>
    public int Width { get; }

    /// <summary>The source image's height in pixels, 1 to <see cref="ProtocolLimits.MaxImageDimension"/>.</summary>
    public int Height { get; }

    /// <summary>A copy of the digest.</summary>
    public byte[] Sha256ToArray() => (byte[])sha256.Clone();

    internal void Write(CanonicalWriter writer)
    {
        Span<byte> id = stackalloc byte[ProtocolConstants.OpaqueIdLength];
        AssetId.WriteBytes(id);
        writer.WriteFixed(id);
        writer.WriteFixed(sha256);
        writer.WriteU8((byte)Format);
        writer.WriteU64((ulong)ByteLength);
        writer.WriteU32((uint)Width);
        writer.WriteU32((uint)Height);
    }

    internal static ImageReference Read(ref CanonicalReader reader)
    {
        var assetId = AssetId.FromBytes(reader.ReadFixed(ProtocolConstants.OpaqueIdLength, "image.assetId"));
        var digest = reader.ReadFixed(ProtocolConstants.DigestLength, "image.sha256");
        var format = (ImageFormat)reader.ReadU8("image.format");
        var byteLength = reader.ReadU64("image.byteLength");
        var width = reader.ReadU32("image.width");
        var height = reader.ReadU32("image.height");

        // The constructor re-checks everything; these two conversions must not wrap first.
        if (byteLength > long.MaxValue)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, "An image declares more bytes than the limit.");
        }

        if (width > int.MaxValue || height > int.MaxValue)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, "An image declares a dimension over the limit.");
        }

        return new ImageReference(assetId, digest, format, (long)byteLength, (int)width, (int)height);
    }

    private static void CheckDimension(int value, string what)
    {
        if (value < 1)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"An image's {what} is at least 1.");
        }

        if (value > ProtocolLimits.MaxImageDimension)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"An image declares a {what} of {ProtocolText.Number(value)}; the limit is {ProtocolText.Number(ProtocolLimits.MaxImageDimension)}.");
        }
    }
}
