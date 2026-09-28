using System;

namespace AetherFrame.Protocol.Identity;

/// <summary>
/// The opaque identity of a remote profile: 16 random bytes, <c>prf_</c> and 32 lowercase hex
/// digits in text. Chosen by the publishing client, unrelated to the local Plate's own id, and
/// never derived from anything about the player (see <see cref="Id128"/>).
/// </summary>
public readonly struct ProfileId : IEquatable<ProfileId>, IComparable<ProfileId>
{
    /// <summary>The text form's prefix.</summary>
    public const string Prefix = "prf_";

    private readonly ulong high;
    private readonly ulong low;

    private ProfileId(ulong high, ulong low)
    {
        this.high = high;
        this.low = low;
    }

    /// <summary>True for the default value, which is never a valid identifier.</summary>
    public bool IsEmpty => (high | low) == 0;

    /// <summary>A fresh random identifier.</summary>
    public static ProfileId NewId()
    {
        var (h, l) = Id128.NewRandom();
        return new ProfileId(h, l);
    }

    /// <summary>Reads the 16 wire bytes, refusing the all-zero value.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public static ProfileId FromBytes(ReadOnlySpan<byte> bytes)
    {
        var (h, l) = Id128.FromBytes(bytes, "profile id");
        return new ProfileId(h, l);
    }

    /// <summary>Parses the text form strictly.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public static ProfileId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new ProtocolException(ProtocolError.InvalidValue, "The profile id is not 'prf_' followed by 32 lowercase hex digits.");

    /// <summary>The strict parse of <see cref="Parse"/>, without the exception.</summary>
    public static bool TryParse(string? text, out ProfileId id)
    {
        var ok = Id128.TryParse(Prefix, text, out var h, out var l);
        id = ok ? new ProfileId(h, l) : default;
        return ok;
    }

    /// <summary>Writes the 16 wire bytes.</summary>
    public void WriteBytes(Span<byte> destination) => Id128.Write(high, low, destination);

    /// <summary>The 16 wire bytes.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[ProtocolConstants.OpaqueIdLength];
        WriteBytes(bytes);
        return bytes;
    }

    /// <summary>The text form.</summary>
    public override string ToString() => Id128.Format(Prefix, high, low);

    /// <inheritdoc />
    public int CompareTo(ProfileId other) => Id128.Compare(high, low, other.high, other.low);

    /// <inheritdoc />
    public bool Equals(ProfileId other) => high == other.high && low == other.low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ProfileId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(high, low);

    /// <summary>Value equality.</summary>
    public static bool operator ==(ProfileId left, ProfileId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(ProfileId left, ProfileId right) => !left.Equals(right);
}
