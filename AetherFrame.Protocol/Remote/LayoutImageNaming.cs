using System;
using System.Collections.Generic;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// How a layout names the images it draws on the wire. A schema 2 snapshot names each by its asset
/// id (docs/networking/ProtocolSpecification-v1.md, section 8.5); a served profile by its index in
/// the served image list (section 8.6, decision D6), since it carries no asset id. The layout model
/// holds an <see cref="AssetId"/> either way: for a served profile, the key
/// <see cref="ServedProfile.ImageKey"/> gives each index.
/// </summary>
internal abstract class LayoutImageNaming
{
    /// <summary>The value an index takes for "no image": only a background may name none.</summary>
    public const byte NoImageIndex = 255;

    /// <summary>A snapshot's naming: the 16 bytes of the asset id, all zero for none.</summary>
    public static LayoutImageNaming AssetIds { get; } = new ByAssetId();

    /// <summary>A served profile's own naming: a <c>u8</c> index, read into its key and written from it.</summary>
    public static LayoutImageNaming ServedIndexes { get; } = new ByIndex(null);

    /// <summary>A served profile's writing: each asset id of <paramref name="images"/> as its position there.</summary>
    public static LayoutImageNaming IndexesOf(IReadOnlyList<ImageReference> images) => new ByIndex(images);

    /// <summary>Writes <paramref name="image"/>, or "none" for the empty id.</summary>
    public abstract void Write(CanonicalWriter writer, AssetId image);

    /// <summary>Reads one image name; the empty id means "none", which only a background accepts.</summary>
    public abstract AssetId Read(ref CanonicalReader reader, string field);

    private sealed class ByAssetId : LayoutImageNaming
    {
        public override void Write(CanonicalWriter writer, AssetId image)
        {
            Span<byte> id = stackalloc byte[ProtocolConstants.OpaqueIdLength];
            image.WriteBytes(id);
            writer.WriteFixed(id);
        }

        public override AssetId Read(ref CanonicalReader reader, string field) =>
            AssetId.FromBytesOrEmpty(reader.ReadFixed(ProtocolConstants.OpaqueIdLength, field));
    }

    private sealed class ByIndex(IReadOnlyList<ImageReference>? images) : LayoutImageNaming
    {
        public override void Write(CanonicalWriter writer, AssetId image)
        {
            if (image.IsEmpty)
            {
                writer.WriteU8(NoImageIndex);
                return;
            }

            if (images is null)
            {
                for (var key = 0; key < ProtocolLimits.MaxImagesPerProfile; key++)
                {
                    if (ServedProfile.ImageKey(key) == image)
                    {
                        writer.WriteU8((byte)key);
                        return;
                    }
                }

                throw new ProtocolException(ProtocolError.InvalidValue, "A served layout names an image by something other than its key.");
            }

            for (var index = 0; index < images.Count; index++)
            {
                if (images[index].AssetId == image)
                {
                    writer.WriteU8((byte)index);
                    return;
                }
            }

            throw new ProtocolException(ProtocolError.InvalidValue, "A layout draws an image the snapshot does not carry.");
        }

        public override AssetId Read(ref CanonicalReader reader, string field)
        {
            var index = reader.ReadU8(field);
            if (index == NoImageIndex)
            {
                return default;
            }

            if (index >= ProtocolLimits.MaxImagesPerProfile)
            {
                throw new ProtocolException(ProtocolError.InvalidValue, $"The layout field '{field}' names image {ProtocolText.Number(index)}; a served profile has at most {ProtocolText.Number(ProtocolLimits.MaxImagesPerProfile)}.");
            }

            return ServedProfile.ImageKey(index);
        }
    }
}
