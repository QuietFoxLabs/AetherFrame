using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// One immutable published revision of a remote profile with its layout: ProfileSnapshot schema 2
/// (docs/networking/ProtocolSpecification-v1.md, section 8.5; decision D8). Besides the ids, the
/// creation time and the name, it carries the canvas size, the background, the paint list a
/// viewer draws in order, and the images that list and the background draw. Every image in the set
/// is drawn and every drawn image is in the set; only PNG and JPEG are carried (decision I1).
/// Shares nothing with the local Plate model. Validated on construction; immutable.
/// </summary>
public sealed class ProfileLayoutSnapshot : RemoteProfileDocument
{
    /// <summary>The payload schema version of a layout snapshot.</summary>
    public const ushort SchemaVersion = 2;

    private readonly LayoutItem[] items;
    private readonly ReadOnlyCollection<LayoutItem> itemsView;
    private readonly ImageReference[] images;
    private readonly ReadOnlyCollection<ImageReference> imagesView;

    /// <summary>Builds a layout snapshot, refusing anything a reader would refuse.</summary>
    /// <exception cref="ProtocolException">Any refusal of section 8.5, with its code.</exception>
    public ProfileLayoutSnapshot(
        ProfileId profileId,
        RevisionId revisionId,
        long createdAtUnixSeconds,
        string name,
        int canvasWidth,
        int canvasHeight,
        LayoutBackground background,
        IEnumerable<LayoutItem> items,
        IEnumerable<ImageReference> images)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(images);

        if (profileId.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A snapshot's profile id is never empty.");
        }

        if (revisionId.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A snapshot's revision id is never empty.");
        }

        ProtocolTimestamps.Check(createdAtUnixSeconds, "createdAt");
        ProtocolName.Encode(name, "name");
        LayoutFields.CheckRange(canvasWidth, ProtocolLimits.MinLayoutCanvasExtent, ProtocolLimits.MaxLayoutCanvasExtent, "canvasWidth");
        LayoutFields.CheckRange(canvasHeight, ProtocolLimits.MinLayoutCanvasExtent, ProtocolLimits.MaxLayoutCanvasExtent, "canvasHeight");

        var itemList = new List<LayoutItem>();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
            if (itemList.Count == ProtocolLimits.MaxLayoutItems)
            {
                throw new ProtocolException(ProtocolError.LimitExceeded, $"A layout lists more than {ProtocolText.Number(ProtocolLimits.MaxLayoutItems)} items.");
            }

            itemList.Add(item);
        }

        var sorted = new List<ImageReference>(ProtocolLimits.MaxImagesPerProfile);
        foreach (var image in images)
        {
            ArgumentNullException.ThrowIfNull(image, nameof(images));
            if (sorted.Count == ProtocolLimits.MaxImagesPerProfile)
            {
                throw new ProtocolException(ProtocolError.LimitExceeded, $"A snapshot references more than {ProtocolText.Number(ProtocolLimits.MaxImagesPerProfile)} images.");
            }

            if (image.Format is not (ImageFormat.Png or ImageFormat.Jpeg))
            {
                throw new ProtocolException(ProtocolError.InvalidValue, "A layout snapshot carries only PNG and JPEG images.");
            }

            sorted.Add(image);
        }

        sorted.Sort(static (a, b) => a.AssetId.CompareTo(b.AssetId));
        for (var index = 1; index < sorted.Count; index++)
        {
            if (sorted[index - 1].AssetId == sorted[index].AssetId)
            {
                throw new ProtocolException(ProtocolError.InvalidValue, "A snapshot references the same asset id twice.");
            }
        }

        var (totalBytes, totalText) = CheckWhole(background, itemList, sorted);

        ProfileId = profileId;
        RevisionId = revisionId;
        CreatedAtUnixSeconds = createdAtUnixSeconds;
        Name = name;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Background = background;
        this.items = itemList.ToArray();
        itemsView = Array.AsReadOnly(this.items);
        this.images = sorted.ToArray();
        imagesView = Array.AsReadOnly(this.images);
        TotalImageBytes = totalBytes;
        TotalTextScalars = totalText;
    }

    /// <summary>
    /// Whether <paramref name="name"/> meets the name rule a snapshot's name follows (section 8.1.1,
    /// decision D4), exactly as the constructor and every reader check it, so a publisher can refuse
    /// a Plate's name before building anything.
    /// </summary>
    public static bool IsValidName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            ProtocolName.Encode(name, "name");
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override DocumentType DocumentType => DocumentType.ProfileSnapshot;

    /// <summary>The id of the profile this revision belongs to; the profile itself is (signing persona, this id).</summary>
    public override ProfileId ProfileId { get; }

    /// <summary>This revision's own id.</summary>
    public RevisionId RevisionId { get; }

    /// <summary>When the revision was created, in Unix seconds.</summary>
    public long CreatedAtUnixSeconds { get; }

    /// <summary><see cref="CreatedAtUnixSeconds"/> as an instant.</summary>
    public DateTimeOffset CreatedAt => DateTimeOffset.FromUnixTimeSeconds(CreatedAtUnixSeconds);

    /// <summary>The Plate's display name, under the name rule (decision D4).</summary>
    public string Name { get; }

    /// <summary>The canvas width, in hundredths of a canvas unit.</summary>
    public int CanvasWidth { get; }

    /// <summary>The canvas height, in hundredths of a canvas unit.</summary>
    public int CanvasHeight { get; }

    /// <summary>The background, drawn before the paint list.</summary>
    public LayoutBackground Background { get; }

    /// <summary>The paint list, in the order a viewer draws it. A read-only view.</summary>
    public IReadOnlyList<LayoutItem> Items => itemsView;

    /// <summary>The images, in ascending asset id order. A read-only view.</summary>
    public IReadOnlyList<ImageReference> Images => imagesView;

    /// <summary>The bytes the images declare in total.</summary>
    public long TotalImageBytes { get; }

    /// <summary>The scalar values all the text items hold together.</summary>
    public int TotalTextScalars { get; }

    internal override byte[] EncodePayload()
    {
        var writer = new CanonicalWriter(4096);
        writer.WriteU16(SchemaVersion);
        Span<byte> id = stackalloc byte[ProtocolConstants.OpaqueIdLength];
        ProfileId.WriteBytes(id);
        writer.WriteFixed(id);
        RevisionId.WriteBytes(id);
        writer.WriteFixed(id);
        writer.WriteU64((ulong)CreatedAtUnixSeconds);
        writer.WriteName(Name, "name");
        writer.WriteI32(CanvasWidth);
        writer.WriteI32(CanvasHeight);
        Background.Write(writer, LayoutImageNaming.AssetIds);
        writer.WriteCount(items.Length);
        foreach (var item in items)
        {
            item.Write(writer, LayoutImageNaming.AssetIds);
        }

        writer.WriteCount(images.Length);
        foreach (var image in images)
        {
            image.Write(writer);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// Decodes a schema 2 payload, checking each field as soon as it is read, then trailing bytes,
    /// then the rules over the whole payload (docs/networking/ProtocolSpecification-v1.md, "Input
    /// with several faults").
    /// </summary>
    internal static ProfileLayoutSnapshot Decode(ReadOnlySpan<byte> payload)
    {
        var reader = new CanonicalReader(payload);
        var schema = reader.ReadU16("schemaVersion");
        if (schema != SchemaVersion)
        {
            throw new ProtocolException(ProtocolError.UnsupportedVersion, $"Profile snapshot schema {ProtocolText.Number(schema)} is not a layout snapshot.");
        }

        var profileId = ProfileId.FromBytes(reader.ReadFixed(ProtocolConstants.OpaqueIdLength, "profileId"));
        var revisionId = RevisionId.FromBytes(reader.ReadFixed(ProtocolConstants.OpaqueIdLength, "revisionId"));
        var createdAt = ProtocolTimestamps.Read(ref reader, "createdAt");
        var name = reader.ReadName("name");
        var canvasWidth = LayoutFields.ReadExtent(ref reader, "canvasWidth", ProtocolLimits.MinLayoutCanvasExtent, ProtocolLimits.MaxLayoutCanvasExtent);
        var canvasHeight = LayoutFields.ReadExtent(ref reader, "canvasHeight", ProtocolLimits.MinLayoutCanvasExtent, ProtocolLimits.MaxLayoutCanvasExtent);
        var background = LayoutBackground.Read(ref reader, LayoutImageNaming.AssetIds);

        var itemCount = reader.ReadCount(ProtocolLimits.MaxLayoutItems, "items");
        var items = new LayoutItem[itemCount];
        for (var index = 0; index < itemCount; index++)
        {
            items[index] = LayoutItem.Read(ref reader, LayoutImageNaming.AssetIds);
        }

        var imageCount = reader.ReadCount(ProtocolLimits.MaxImagesPerProfile, "images");
        var images = new ImageReference[imageCount];
        AssetId? previous = null;
        for (var index = 0; index < imageCount; index++)
        {
            images[index] = ImageReference.Read(ref reader, previous, webPAllowed: false);
            previous = images[index].AssetId;
        }

        reader.ExpectEnd("The layout snapshot payload");
        CheckWhole(background, items, images);
        return new ProfileLayoutSnapshot(profileId, revisionId, createdAt, name, canvasWidth, canvasHeight, background, items, images);
    }

    /// <summary>
    /// The rules over the whole payload, in the specification's order: the images' total bytes,
    /// their total pixels, the items' total text, then that every drawn image is in the set and
    /// every image in the set is drawn. <paramref name="images"/> is sorted by asset id.
    /// </summary>
    private static (long TotalBytes, int TotalText) CheckWhole(LayoutBackground background, IReadOnlyList<LayoutItem> items, IReadOnlyList<ImageReference> images)
    {
        long totalBytes = 0;
        foreach (var image in images)
        {
            totalBytes += image.ByteLength;
        }

        if (totalBytes > ProtocolLimits.MaxProfileImageBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"A snapshot's images declare {ProtocolText.Number(totalBytes)} bytes in total; the limit is {ProtocolText.Number(ProtocolLimits.MaxProfileImageBytes)}.");
        }

        long totalPixels = 0;
        foreach (var image in images)
        {
            totalPixels += (long)image.Width * image.Height;
        }

        if (totalPixels > ProtocolLimits.MaxLayoutImagePixels)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"A layout's images declare {ProtocolText.Number(totalPixels)} pixels in total; the limit is {ProtocolText.Number(ProtocolLimits.MaxLayoutImagePixels)}.");
        }

        var totalText = 0;
        foreach (var item in items)
        {
            totalText += item.TextScalars;
        }

        if (totalText > ProtocolLimits.MaxLayoutTextScalars)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"A layout's texts hold {ProtocolText.Number(totalText)} characters in all; the limit is {ProtocolText.Number(ProtocolLimits.MaxLayoutTextScalars)}.");
        }

        var drawn = new bool[images.Count];
        MarkDrawn(background.ImageAssetId, images, drawn);
        foreach (var item in items)
        {
            MarkDrawn(item.DrawnAsset, images, drawn);
        }

        if (Array.IndexOf(drawn, false) >= 0)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A layout snapshot carries an image that nothing draws.");
        }

        return (totalBytes, totalText);
    }

    private static void MarkDrawn(AssetId assetId, IReadOnlyList<ImageReference> images, bool[] drawn)
    {
        if (assetId.IsEmpty)
        {
            return;
        }

        for (var index = 0; index < images.Count; index++)
        {
            if (images[index].AssetId == assetId)
            {
                drawn[index] = true;
                return;
            }
        }

        throw new ProtocolException(ProtocolError.InvalidValue, "A layout draws an image the snapshot does not carry.");
    }
}
