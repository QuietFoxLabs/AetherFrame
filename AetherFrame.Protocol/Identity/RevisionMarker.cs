using System;

namespace AetherFrame.Protocol.Identity;

/// <summary>
/// A served revision's marker (docs/networking/ProtocolSpecification-v1.md, section 8.6; decision
/// D6): 16 random bytes the server draws from a cryptographically secure generator for each revision
/// it serves, <c>mrk_</c> and 32 lowercase hex digits in text. A viewer names it when it fetches the
/// revision's images, so a refresh never mixes two revisions. It is never the server's sequence, a
/// time or a document's digest, and it names nothing outside the one server.
/// </summary>
public readonly struct RevisionMarker : IEquatable<RevisionMarker>
{
    /// <summary>The text form's prefix.</summary>
    public const string Prefix = "mrk_";

    private readonly ulong high;
    private readonly ulong low;

    private RevisionMarker(ulong high, ulong low)
    {
        this.high = high;
        this.low = low;
    }

    /// <summary>True for the default value, which is never a valid marker.</summary>
    public bool IsEmpty => (high | low) == 0;

    /// <summary>A fresh random marker.</summary>
    public static RevisionMarker NewMarker()
    {
        var (h, l) = Id128.NewRandom();
        return new RevisionMarker(h, l);
    }

    /// <summary>Reads the 16 wire bytes, refusing the all-zero value.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public static RevisionMarker FromBytes(ReadOnlySpan<byte> bytes)
    {
        var (h, l) = Id128.FromBytes(bytes, "revision marker");
        return new RevisionMarker(h, l);
    }

    /// <summary>Parses the text form strictly.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public static RevisionMarker Parse(string text) =>
        TryParse(text, out var marker) ? marker : throw new ProtocolException(ProtocolError.InvalidValue, "The revision marker is not 'mrk_' followed by 32 lowercase hex digits.");

    /// <summary>The strict parse of <see cref="Parse"/>, without the exception.</summary>
    public static bool TryParse(string? text, out RevisionMarker marker)
    {
        var ok = Id128.TryParse(Prefix, text, out var h, out var l);
        marker = ok ? new RevisionMarker(h, l) : default;
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
    public bool Equals(RevisionMarker other) => high == other.high && low == other.low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RevisionMarker other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(high, low);

    /// <summary>Value equality.</summary>
    public static bool operator ==(RevisionMarker left, RevisionMarker right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(RevisionMarker left, RevisionMarker right) => !left.Equals(right);
}
