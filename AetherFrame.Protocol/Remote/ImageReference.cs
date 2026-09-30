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
        CheckDigest(digest);
        CheckFormat(format);
        if (byteLength < 1)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "An image's byte length is at least 1.");
        }

        CheckByteLength((ulong)byteLength);
        CheckDimension(width, "width");
        CheckDimension(height, "height");
        CheckPixels(width, height);

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

    /// <summary>
    /// Reads one reference, checking each field as soon as it is read and the set-order rule as soon
    /// as the key is read, so a payload with several faults is refused for the first one in schema
    /// order (docs/networking/ProtocolSpecification-v1.md, "Errors"). <paramref name="previous"/> is
    /// the asset id of the reference before this one in the set, or null for the first.
    /// </summary>
    internal static ImageReference Read(ref CanonicalReader reader, AssetId? previous) => Read(ref reader, previous, webPAllowed: true);

    /// <summary>
    /// As <see cref="Read(ref CanonicalReader, AssetId?)"/>, and when <paramref name="webPAllowed"/> is
    /// false (schema 2, decision I1) a WebP format is refused as soon as the format is read.
    /// </summary>
    internal static ImageReference Read(ref CanonicalReader reader, AssetId? previous, bool webPAllowed)
    {
        var assetId = AssetId.FromBytes(reader.ReadFixed(ProtocolConstants.OpaqueIdLength, "image.assetId"));
        if (previous is { } last && last.CompareTo(assetId) >= 0)
        {
            throw new ProtocolException(ProtocolError.NotCanonical, "A snapshot's images must be in strictly ascending asset id order.");
        }

        var digest = reader.ReadFixed(ProtocolConstants.DigestLength, "image.sha256").ToArray();
        CheckDigest(digest);
        var format = (ImageFormat)reader.ReadU8("image.format");
        CheckFormat(format);
        if (!webPAllowed && format == ImageFormat.WebP)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A layout snapshot carries only PNG and JPEG images.");
        }

        var byteLength = reader.ReadU64("image.byteLength");
        if (byteLength == 0)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "An image's byte length is at least 1.");
        }

        CheckByteLength(byteLength);
        var width = ReadDimension(ref reader, "width");
        var height = ReadDimension(ref reader, "height");
        CheckPixels(width, height);
        return new ImageReference(assetId, digest, format, (long)byteLength, width, height);
    }

    private static int ReadDimension(ref CanonicalReader reader, string what)
    {
        var value = reader.ReadU32("image." + what);
        if (value == 0)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"An image's {what} is at least 1.");
        }

        if (value > ProtocolLimits.MaxImageDimension)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"An image declares a {what} of {ProtocolText.Number(value)}; the limit is {ProtocolText.Number(ProtocolLimits.MaxImageDimension)}.");
        }

        return (int)value;
    }

    private static void CheckDigest(ReadOnlySpan<byte> digest)
    {
        if (digest.IndexOfAnyExcept((byte)0) < 0)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "An image digest is never all zero.");
        }
    }

    private static void CheckFormat(ImageFormat format)
    {
        if (!ImageFormats.IsKnown(format))
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"Image format {ProtocolText.Number((byte)format)} is not known.");
        }
    }

    private static void CheckByteLength(ulong byteLength)
    {
        if (byteLength > (ulong)ProtocolLimits.MaxImageBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"An image declares {byteLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes; the limit is {ProtocolText.Number(ProtocolLimits.MaxImageBytes)}.");
        }
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

    private static void CheckPixels(int width, int height)
    {
        if ((long)width * height > ProtocolLimits.MaxImagePixels)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"An image declares {ProtocolText.Number((long)width * height)} pixels; the limit is {ProtocolText.Number(ProtocolLimits.MaxImagePixels)}.");
        }
    }
}
