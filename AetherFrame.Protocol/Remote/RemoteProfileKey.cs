using System;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// What identifies a remote profile: the persona that owns it together with the profile id it chose
/// (docs/networking/ProtocolSpecification-v1.md, "Profile identity and ownership"). A profile id
/// alone identifies nothing: two personas may use the same id for unrelated profiles, and a signed
/// document only ever refers to a profile of the persona whose key signed it, so only the owner can
/// publish to a profile or retract it. Anything that stores or looks up profiles keys them by this
/// value, never by <see cref="Identity.ProfileId"/> alone.
/// </summary>
public readonly struct RemoteProfileKey : IEquatable<RemoteProfileKey>
{
    /// <summary>Builds the key of <paramref name="persona"/>'s profile <paramref name="profileId"/>.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/> when either part is empty.</exception>
    public RemoteProfileKey(PersonaId persona, ProfileId profileId)
    {
        if (persona.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A remote profile's owner is never empty.");
        }

        if (profileId.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A remote profile's id is never empty.");
        }

        Persona = persona;
        ProfileId = profileId;
    }

    /// <summary>The persona that owns the profile: the one whose key signed the document that named it.</summary>
    public PersonaId Persona { get; }

    /// <summary>The profile id the owner chose, meaningful only together with <see cref="Persona"/>.</summary>
    public ProfileId ProfileId { get; }

    /// <summary>True for the default value, which names no profile.</summary>
    public bool IsEmpty => Persona.IsEmpty && ProfileId.IsEmpty;

    /// <summary>The two text forms joined by a slash, for display and logs.</summary>
    public override string ToString() => Persona.ToString() + "/" + ProfileId.ToString();

    /// <inheritdoc />
    public bool Equals(RemoteProfileKey other) => Persona == other.Persona && ProfileId == other.ProfileId;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RemoteProfileKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Persona, ProfileId);

    /// <summary>Value equality.</summary>
    public static bool operator ==(RemoteProfileKey left, RemoteProfileKey right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(RemoteProfileKey left, RemoteProfileKey right) => !left.Equals(right);
}
