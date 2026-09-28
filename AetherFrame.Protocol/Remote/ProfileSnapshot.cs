using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// One immutable published revision of a remote profile, as far as NETWORK0 defines it: the
/// profile and revision ids, when it was created, the Plate's display name, and the source images
/// it references. Deliberately narrower than the local Plate (no layout, no elements, no
/// Components, no character, no local ids or paths); the layout schema is a later payload schema
/// version. Images are a set, held sorted by asset id so that the same set always encodes the same
/// way. Validated on construction; immutable.
/// </summary>
public sealed class ProfileSnapshot : RemoteDocument
{
    /// <summary>The payload schema version this build reads and writes.</summary>
    public const ushort SchemaVersion = 1;

    private readonly ImageReference[] images;
    private readonly ReadOnlyCollection<ImageReference> imagesView;

    /// <summary>Builds a snapshot, refusing any value outside the limits and any repeated asset id.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>, <see cref="ProtocolError.InvalidText"/> or <see cref="ProtocolError.LimitExceeded"/>.</exception>
    public ProfileSnapshot(ProfileId profileId, RevisionId revisionId, long createdAtUnixSeconds, string name, IEnumerable<ImageReference> images)
    {
        ArgumentNullException.ThrowIfNull(name);
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
        ProtocolText.Encode(name, "name");

        var sorted = new List<ImageReference>(ProtocolLimits.MaxImagesPerProfile);
        foreach (var image in images)
        {
            ArgumentNullException.ThrowIfNull(image, nameof(images));
            if (sorted.Count == ProtocolLimits.MaxImagesPerProfile)
            {
                throw new ProtocolException(ProtocolError.LimitExceeded, $"A snapshot references more than {ProtocolText.Number(ProtocolLimits.MaxImagesPerProfile)} images.");
            }

            sorted.Add(image);
        }

        sorted.Sort(static (a, b) => a.AssetId.CompareTo(b.AssetId));
        long totalBytes = 0;
        for (var index = 0; index < sorted.Count; index++)
        {
            if (index > 0 && sorted[index - 1].AssetId == sorted[index].AssetId)
            {
                throw new ProtocolException(ProtocolError.InvalidValue, "A snapshot references the same asset id twice.");
            }

            totalBytes = checked(totalBytes + sorted[index].ByteLength);
        }

        if (totalBytes > ProtocolLimits.MaxProfileImageBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"A snapshot's images declare {ProtocolText.Number(totalBytes)} bytes in total; the limit is {ProtocolText.Number(ProtocolLimits.MaxProfileImageBytes)}.");
        }

        ProfileId = profileId;
        RevisionId = revisionId;
        CreatedAtUnixSeconds = createdAtUnixSeconds;
        Name = name;
        this.images = sorted.ToArray();
        imagesView = Array.AsReadOnly(this.images);
        TotalImageBytes = totalBytes;
    }

    /// <inheritdoc />
    public override DocumentType DocumentType => DocumentType.ProfileSnapshot;

    /// <summary>The id of the profile this revision belongs to; the profile itself is (signing persona, this id).</summary>
    public override ProfileId ProfileId { get; }

    /// <summary>This revision's own id.</summary>
    public RevisionId RevisionId { get; }

    /// <summary>When the revision was created, in Unix seconds (0 to <see cref="ProtocolLimits.MaxUnixSeconds"/>).</summary>
    public long CreatedAtUnixSeconds { get; }

    /// <summary><see cref="CreatedAtUnixSeconds"/> as an instant.</summary>
    public DateTimeOffset CreatedAt => DateTimeOffset.FromUnixTimeSeconds(CreatedAtUnixSeconds);

    /// <summary>The Plate's display name, exactly as authored (possibly empty).</summary>
    public string Name { get; }

    /// <summary>The referenced images, in ascending asset id order. A read-only view: no cast reaches the array behind it.</summary>
    public IReadOnlyList<ImageReference> Images => imagesView;

    /// <summary>The bytes the images declare in total.</summary>
    public long TotalImageBytes { get; }

    internal override byte[] EncodePayload()
    {
        var writer = new CanonicalWriter();
        writer.WriteU16(SchemaVersion);
        Span<byte> id = stackalloc byte[ProtocolConstants.OpaqueIdLength];
        ProfileId.WriteBytes(id);
        writer.WriteFixed(id);
        RevisionId.WriteBytes(id);
        writer.WriteFixed(id);
        writer.WriteU64((ulong)CreatedAtUnixSeconds);
        writer.WriteText(Name, "name");
        writer.WriteCount(images.Length);
        foreach (var image in images)
        {
            image.Write(writer);
        }

        return writer.ToArray();
    }

    internal static ProfileSnapshot Decode(ReadOnlySpan<byte> payload)
    {
        var reader = new CanonicalReader(payload);
        var schema = reader.ReadU16("schemaVersion");
        if (schema != SchemaVersion)
        {
            throw new ProtocolException(ProtocolError.UnsupportedVersion, $"Profile snapshot schema {ProtocolText.Number(schema)} is not supported; this build reads schema {ProtocolText.Number(SchemaVersion)}.");
        }

        var profileId = ProfileId.FromBytes(reader.ReadFixed(ProtocolConstants.OpaqueIdLength, "profileId"));
        var revisionId = RevisionId.FromBytes(reader.ReadFixed(ProtocolConstants.OpaqueIdLength, "revisionId"));
        var createdAt = ProtocolTimestamps.Read(ref reader, "createdAt");
        var name = reader.ReadText("name");
        var count = reader.ReadCount(ProtocolLimits.MaxImagesPerProfile, "images");
        var images = new ImageReference[count];
        AssetId? previous = null;
        for (var index = 0; index < count; index++)
        {
            images[index] = ImageReference.Read(ref reader, previous);
            previous = images[index].AssetId;
        }

        reader.ExpectEnd("The profile snapshot payload");
        return new ProfileSnapshot(profileId, revisionId, createdAt, name, images);
    }
}
