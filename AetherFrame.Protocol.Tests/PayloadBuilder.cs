using System;
using System.Collections.Generic;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Protocol.Tests;

/// <summary>Hand-built payloads that the models themselves would refuse to build, signed so that only the payload decoder can refuse them.</summary>
internal static class PayloadBuilder
{
    public static byte[] Signed(DocumentType type, IPersonaSigner signer, byte[] payload) =>
        SignedDocumentCodec.Assemble(type, signer.PublicKey, payload, signer.Sign(SigningInput.Create(type, signer.PublicKey, payload)));

    /// <summary>A snapshot payload from raw parts; each part defaults to the sample's value.</summary>
    public static byte[] Snapshot(
        ushort schema = ProfileSnapshot.SchemaVersion,
        byte[]? profileId = null,
        byte[]? revisionId = null,
        ulong createdAt = Samples.CreatedAt,
        byte[]? nameBytes = null,
        uint? imageCount = null,
        IReadOnlyList<byte[]>? images = null,
        byte[]? trailing = null)
    {
        images ??= [Image(Samples.Asset1, 0x11), Image(Samples.Asset2, 0x22, format: 2, byteLength: 5678, width: 1920, height: 1080)];
        var writer = new CanonicalWriter();
        writer.WriteU16(schema);
        writer.WriteFixed(profileId ?? Samples.Profile.ToArray());
        writer.WriteFixed(revisionId ?? Samples.Revision.ToArray());
        writer.WriteU64(createdAt);
        writer.WriteLengthPrefixed(nameBytes ?? ProtocolConstants.StrictUtf8.GetBytes(Samples.Name));
        writer.WriteU32(imageCount ?? (uint)images.Count);
        foreach (var image in images)
        {
            writer.WriteFixed(image);
        }

        if (trailing is not null)
        {
            writer.WriteFixed(trailing);
        }

        return writer.ToArray();
    }

    public static byte[] Image(AssetId assetId, byte digestFill = 0x11, byte format = 1, ulong byteLength = 1234, uint width = 640, uint height = 480, byte[]? digest = null)
    {
        var writer = new CanonicalWriter();
        writer.WriteFixed(assetId.ToArray());
        writer.WriteFixed(digest ?? Samples.Digest(digestFill));
        writer.WriteU8(format);
        writer.WriteU64(byteLength);
        writer.WriteU32(width);
        writer.WriteU32(height);
        return writer.ToArray();
    }

    public static byte[] Retraction(ushort schema = ProfileRetraction.SchemaVersion, byte[]? profileId = null, ulong issuedAt = Samples.IssuedAt, byte[]? trailing = null)
    {
        var writer = new CanonicalWriter();
        writer.WriteU16(schema);
        writer.WriteFixed(profileId ?? Samples.Profile.ToArray());
        writer.WriteU64(issuedAt);
        if (trailing is not null)
        {
            writer.WriteFixed(trailing);
        }

        return writer.ToArray();
    }

    /// <summary>The largest snapshot the limits allow: a 64 four-byte-scalar name (256 bytes) and eight 5 MiB images.</summary>
    public static ProfileSnapshot MaximalSnapshot()
    {
        var images = new List<ImageReference>();
        for (var index = 1; index <= ProtocolLimits.MaxImagesPerProfile; index++)
        {
            var id = AssetId.Parse("ast_" + index.ToString("x32", System.Globalization.CultureInfo.InvariantCulture));
            images.Add(new ImageReference(id, Samples.Digest((byte)index), ImageFormat.Png, ProtocolLimits.MaxProfileImageBytes / ProtocolLimits.MaxImagesPerProfile, 5000, 4000));
        }

        var name = string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", ProtocolLimits.MaxNameScalars));
        return new ProfileSnapshot(Samples.Profile, Samples.RevisionMaximal, ProtocolLimits.MaxUnixSeconds, name, images);
    }
}
