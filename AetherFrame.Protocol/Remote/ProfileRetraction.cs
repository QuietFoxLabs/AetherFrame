using System;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// A persona's signed withdrawal of one of its own remote profiles from publication: every
/// revision of the profile (signing persona, profile id) is to be taken down. A retraction can name
/// no other persona's profile, whatever id it carries (docs/networking/ProtocolSpecification-v1.md,
/// "Profile identity and ownership"). The counterpart of <see cref="ProfileSnapshot"/>, so that a
/// client can unpublish with the same key-first mechanism it published with and no session or
/// account. What a server does after a retraction is stated in the specification's server
/// obligations. Validated on construction; immutable.
/// </summary>
public sealed class ProfileRetraction : RemoteDocument
{
    /// <summary>The payload schema version this build reads and writes.</summary>
    public const ushort SchemaVersion = 1;

    /// <summary>Builds a retraction.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public ProfileRetraction(ProfileId profileId, long issuedAtUnixSeconds)
    {
        if (profileId.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A retraction's profile id is never empty.");
        }

        ProtocolTimestamps.Check(issuedAtUnixSeconds, "issuedAt");
        ProfileId = profileId;
        IssuedAtUnixSeconds = issuedAtUnixSeconds;
    }

    /// <inheritdoc />
    public override DocumentType DocumentType => DocumentType.ProfileRetraction;

    /// <summary>The id of the signing persona's profile to withdraw.</summary>
    public override ProfileId ProfileId { get; }

    /// <summary>When the retraction was issued, in Unix seconds (0 to <see cref="ProtocolLimits.MaxUnixSeconds"/>).</summary>
    public long IssuedAtUnixSeconds { get; }

    /// <summary><see cref="IssuedAtUnixSeconds"/> as an instant.</summary>
    public DateTimeOffset IssuedAt => DateTimeOffset.FromUnixTimeSeconds(IssuedAtUnixSeconds);

    internal override byte[] EncodePayload()
    {
        var writer = new CanonicalWriter(2 + ProtocolConstants.OpaqueIdLength + 8);
        writer.WriteU16(SchemaVersion);
        Span<byte> id = stackalloc byte[ProtocolConstants.OpaqueIdLength];
        ProfileId.WriteBytes(id);
        writer.WriteFixed(id);
        writer.WriteU64((ulong)IssuedAtUnixSeconds);
        return writer.ToArray();
    }

    internal static ProfileRetraction Decode(ReadOnlySpan<byte> payload)
    {
        var reader = new CanonicalReader(payload);
        var schema = reader.ReadU16("schemaVersion");
        if (schema != SchemaVersion)
        {
            throw new ProtocolException(ProtocolError.UnsupportedVersion, $"Profile retraction schema {ProtocolText.Number(schema)} is not supported; this build reads schema {ProtocolText.Number(SchemaVersion)}.");
        }

        var profileId = ProfileId.FromBytes(reader.ReadFixed(ProtocolConstants.OpaqueIdLength, "profileId"));
        var issuedAt = ProtocolTimestamps.Read(ref reader, "issuedAt");
        reader.ExpectEnd("The profile retraction payload");
        return new ProfileRetraction(profileId, issuedAt);
    }
}
