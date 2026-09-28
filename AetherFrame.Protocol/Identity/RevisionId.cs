using System;

namespace AetherFrame.Protocol.Identity;

/// <summary>
/// The opaque identity of one immutable published revision of a remote profile: 16 random bytes, <c>rev_</c> and 32 lowercase hex digits in text. A new revision gets a new id; a revision is never edited in place.
/// Never derived from anything about the player (see <see cref="Id128"/>).
/// </summary>
public readonly struct RevisionId : IEquatable<RevisionId>, IComparable<RevisionId>
{
    /// <summary>The text form's prefix.</summary>
    public const string Prefix = "rev_";

    private readonly ulong high;
    private readonly ulong low;

    private RevisionId(ulong high, ulong low)
    {
        this.high = high;
        this.low = low;
    }

    /// <summary>True for the default value, which is never a valid identifier.</summary>
    public bool IsEmpty => (high | low) == 0;

    /// <summary>A fresh random identifier.</summary>
    public static RevisionId NewId()
    {
        var (h, l) = Id128.NewRandom();
        return new RevisionId(h, l);
    }

    /// <summary>Reads the 16 wire bytes, refusing the all-zero value.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public static RevisionId FromBytes(ReadOnlySpan<byte> bytes)
    {
        var (h, l) = Id128.FromBytes(bytes, "revision id");
        return new RevisionId(h, l);
    }

    /// <summary>Parses the text form strictly.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public static RevisionId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new ProtocolException(ProtocolError.InvalidValue, "The revision id is not 'rev_' followed by 32 lowercase hex digits.");

    /// <summary>The strict parse of <see cref="Parse"/>, without the exception.</summary>
    public static bool TryParse(string? text, out RevisionId id)
    {
        var ok = Id128.TryParse(Prefix, text, out var h, out var l);
        id = ok ? new RevisionId(h, l) : default;
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
    public int CompareTo(RevisionId other) => Id128.Compare(high, low, other.high, other.low);

    /// <inheritdoc />
    public bool Equals(RevisionId other) => high == other.high && low == other.low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RevisionId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(high, low);

    /// <summary>Value equality.</summary>
    public static bool operator ==(RevisionId left, RevisionId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(RevisionId left, RevisionId right) => !left.Equals(right);
}
