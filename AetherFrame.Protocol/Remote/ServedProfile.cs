using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// What a viewer receives (docs/networking/ProtocolSpecification-v1.md, section 8.6; decision D6): a
/// Plate's name, canvas, background and paint list, which the server builds from a verified schema 2
/// snapshot it stores, with the images named by index. It is not a signed document: it holds no
/// signature, persona id, profile id, revision id, asset id or time, and nothing in it is signed by
/// the publisher. What it holds is attested by the server alone. Only <see cref="Read"/> makes one;
/// it is never a <see cref="Documents.VerifiedDocument"/>, and a document is never read as one.
/// Immutable.
/// </summary>
/// <remarks>
/// The layout model names images by <see cref="AssetId"/>. In a served profile each is a key that
/// stands for its index, never the publisher's asset id, which a served profile doesn't carry:
/// <see cref="ImageIndexOf"/> turns one back into its index in <see cref="Images"/>. A served
/// profile's items and background never go into a snapshot.
/// </remarks>
public sealed class ServedProfile
{
    /// <summary>The only served profile version this build reads or writes.</summary>
    public const ushort FormatVersion = 1;

    private readonly LayoutItem[] items;
    private readonly ServedImage[] images;

    private ServedProfile(RevisionMarker marker, string name, int canvasWidth, int canvasHeight, LayoutBackground background, LayoutItem[] items, ServedImage[] images, int totalTextScalars)
    {
        Marker = marker;
        Name = name;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Background = background;
        this.items = items;
        this.images = images;
        Items = Array.AsReadOnly(items);
        Images = Array.AsReadOnly(images);
        TotalTextScalars = totalTextScalars;
    }

    /// <summary>The marker of the revision served, which a viewer names when it fetches the images.</summary>
    public RevisionMarker Marker { get; }

    /// <summary>The Plate's display name, under the name rule (section 8.1.1).</summary>
    public string Name { get; }

    /// <summary>The canvas width, in hundredths of a canvas unit.</summary>
    public int CanvasWidth { get; }

    /// <summary>The canvas height, in hundredths of a canvas unit.</summary>
    public int CanvasHeight { get; }

    /// <summary>The background, drawn before the paint list. Its image, if any, is named by a key (<see cref="ImageIndexOf"/>).</summary>
    public LayoutBackground Background { get; }

    /// <summary>The paint list, in drawing order. Images are named by keys (<see cref="ImageIndexOf"/>). A read-only view.</summary>
    public ReadOnlyCollection<LayoutItem> Items { get; }

    /// <summary>The images, by index. A read-only view.</summary>
    public ReadOnlyCollection<ServedImage> Images { get; }

    /// <summary>The scalar values all the text items hold together.</summary>
    public int TotalTextScalars { get; }

    /// <summary>
    /// The key the layout model uses for the image at <paramref name="index"/> (0 to 7): fifteen zero
    /// bytes, then the index plus one. It is an index in a model's clothing, never an asset id: a
    /// served profile's items and background never go into a snapshot, which would sign these keys
    /// as asset ids. A viewer turns a key back into its index with <see cref="ImageIndexOf"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An index outside 0 to 7.</exception>
    internal static AssetId ImageKey(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ProtocolLimits.MaxImagesPerProfile);
        Span<byte> bytes = stackalloc byte[ProtocolConstants.OpaqueIdLength];
        bytes[^1] = (byte)(index + 1);
        return AssetId.FromBytes(bytes);
    }

    /// <summary>The index in <see cref="Images"/> that <paramref name="key"/> names, or -1 when it names none of them.</summary>
    public int ImageIndexOf(AssetId key)
    {
        for (var index = 0; index < images.Length; index++)
        {
            if (ImageKey(index) == key)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Builds the served profile of <paramref name="snapshot"/> under <paramref name="marker"/>
    /// (section 8.6): the name, canvas, background and items as the snapshot holds them, with each
    /// image named by its position in the snapshot's image set (ascending asset id), and each image
    /// entry reduced to its format and size. For a server, from a snapshot it has verified. The
    /// result is read back before it is returned, so a server never serves what a viewer refuses.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/> for an empty marker.</exception>
    public static byte[] Build(ProfileLayoutSnapshot snapshot, RevisionMarker marker)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (marker.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A served profile's marker is never empty.");
        }

        var images = new ServedImage[snapshot.Images.Count];
        for (var index = 0; index < images.Length; index++)
        {
            images[index] = new ServedImage(snapshot.Images[index].Format, snapshot.Images[index].Width, snapshot.Images[index].Height);
        }

        var bytes = Write(marker, snapshot.Name, snapshot.CanvasWidth, snapshot.CanvasHeight, snapshot.Background, snapshot.Items, images, LayoutImageNaming.IndexesOf(snapshot.Images));
        Read(bytes);
        return bytes;
    }

    /// <summary>This served profile's bytes: exactly what <see cref="Read"/> read, since each model has one encoding.</summary>
    internal byte[] Encode() => Write(Marker, Name, CanvasWidth, CanvasHeight, Background, items, images, LayoutImageNaming.ServedIndexes);

    /// <summary>
    /// Reads a served profile from hostile bytes (section 8.6): the size, then each field as soon as
    /// it is read, then trailing bytes, then the rules over the whole body. The input is copied once
    /// before anything is read from it.
    /// </summary>
    /// <exception cref="ProtocolException">The first rule the input breaks, in the order of section 9.1.</exception>
    public static ServedProfile Read(ReadOnlySpan<byte> input)
    {
        if (input.Length > ProtocolLimits.MaxServedProfileBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The served profile is {ProtocolText.Number(input.Length)} bytes; the limit is {ProtocolText.Number(ProtocolLimits.MaxServedProfileBytes)}.");
        }

        var bytes = input.ToArray();
        var reader = new CanonicalReader(bytes);
        if (!reader.ReadFixed(ProtocolConstants.ServedProfileMagic.Length, "magic").SequenceEqual(ProtocolConstants.ServedProfileMagic))
        {
            throw new ProtocolException(ProtocolError.InvalidFraming, "The input does not start with the served profile magic.");
        }

        var version = reader.ReadU16("version");
        if (version != FormatVersion)
        {
            throw new ProtocolException(ProtocolError.UnsupportedVersion, $"Served profile version {ProtocolText.Number(version)} is not supported; this build reads only version {ProtocolText.Number(FormatVersion)}.");
        }

        var marker = RevisionMarker.FromBytes(reader.ReadFixed(ProtocolConstants.OpaqueIdLength, "marker"));
        var name = reader.ReadName("name");
        var canvasWidth = LayoutFields.ReadExtent(ref reader, "canvasWidth", ProtocolLimits.MinLayoutCanvasExtent, ProtocolLimits.MaxLayoutCanvasExtent);
        var canvasHeight = LayoutFields.ReadExtent(ref reader, "canvasHeight", ProtocolLimits.MinLayoutCanvasExtent, ProtocolLimits.MaxLayoutCanvasExtent);
        var background = LayoutBackground.Read(ref reader, LayoutImageNaming.ServedIndexes);

        var itemCount = reader.ReadCount(ProtocolLimits.MaxLayoutItems, "items");
        var items = new LayoutItem[itemCount];
        for (var index = 0; index < itemCount; index++)
        {
            items[index] = LayoutItem.Read(ref reader, LayoutImageNaming.ServedIndexes);
        }

        var imageCount = reader.ReadCount(ProtocolLimits.MaxImagesPerProfile, "images");
        var images = new ServedImage[imageCount];
        for (var index = 0; index < imageCount; index++)
        {
            images[index] = ServedImage.Read(ref reader);
        }

        reader.ExpectEnd("The served profile");
        var totalText = CheckWhole(background, items, images);
        return new ServedProfile(marker, name, canvasWidth, canvasHeight, background, items, images, totalText);
    }

    private static byte[] Write(RevisionMarker marker, string name, int canvasWidth, int canvasHeight, LayoutBackground background, IReadOnlyList<LayoutItem> items, IReadOnlyList<ServedImage> images, LayoutImageNaming naming)
    {
        var writer = new CanonicalWriter(4096);
        writer.WriteFixed(ProtocolConstants.ServedProfileMagic);
        writer.WriteU16(FormatVersion);
        Span<byte> id = stackalloc byte[ProtocolConstants.OpaqueIdLength];
        marker.WriteBytes(id);
        writer.WriteFixed(id);
        writer.WriteName(name, "name");
        writer.WriteI32(canvasWidth);
        writer.WriteI32(canvasHeight);
        background.Write(writer, naming);
        writer.WriteCount(items.Count);
        foreach (var item in items)
        {
            item.Write(writer, naming);
        }

        writer.WriteCount(images.Count);
        foreach (var image in images)
        {
            image.Write(writer);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// The rules over the whole body, in the specification's order: the images' total pixels, the
    /// items' total text, then that every image the layout names is listed and every listed image
    /// is named.
    /// </summary>
    private static int CheckWhole(LayoutBackground background, LayoutItem[] items, ServedImage[] images)
    {
        var totalPixels = images.Sum(image => image.Pixels);
        if (totalPixels > ProtocolLimits.MaxLayoutImagePixels)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"A served profile's images hold {ProtocolText.Number(totalPixels)} pixels in total; the limit is {ProtocolText.Number(ProtocolLimits.MaxLayoutImagePixels)}.");
        }

        var totalText = 0;
        foreach (var item in items)
        {
            totalText += item.TextScalars;
        }

        if (totalText > ProtocolLimits.MaxLayoutTextScalars)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"A served profile's texts hold {ProtocolText.Number(totalText)} characters in all; the limit is {ProtocolText.Number(ProtocolLimits.MaxLayoutTextScalars)}.");
        }

        var named = new bool[images.Length];
        MarkNamed(background.ImageAssetId, named);
        foreach (var item in items)
        {
            MarkNamed(item.DrawnAsset, named);
        }

        if (Array.IndexOf(named, false) >= 0)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A served profile lists an image that nothing draws.");
        }

        return totalText;
    }

    private static void MarkNamed(AssetId key, bool[] named)
    {
        if (key.IsEmpty)
        {
            return;
        }

        for (var index = 0; index < named.Length; index++)
        {
            if (ImageKey(index) == key)
            {
                named[index] = true;
                return;
            }
        }

        throw new ProtocolException(ProtocolError.InvalidValue, "A served profile draws an image it does not list.");
    }
}
