using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AetherFrame.Protocol.Identity;

/// <summary>
/// A persona's stable public identity: <c>psn_</c> followed by the 64 lowercase hex digits of
/// SHA-256 over the persona identity input (docs/networking/ProtocolSpecification-v1.md, "Persona
/// identity"). Derived from the public key alone, so it carries nothing about the player, the
/// character or the machine, and any party holding the key can recompute it. Comparisons are not
/// constant time: identities are public values.
/// </summary>
public readonly struct PersonaId : IEquatable<PersonaId>
{
    /// <summary>The text form's prefix.</summary>
    public const string Prefix = "psn_";

    /// <summary>The length of the text form: the prefix and 64 hex digits.</summary>
    public const int TextLength = 4 + (ProtocolConstants.DigestLength * 2);

    private readonly ulong a;
    private readonly ulong b;
    private readonly ulong c;
    private readonly ulong d;

    private PersonaId(ReadOnlySpan<byte> digest)
    {
        a = BinaryPrimitives.ReadUInt64BigEndian(digest);
        b = BinaryPrimitives.ReadUInt64BigEndian(digest.Slice(8));
        c = BinaryPrimitives.ReadUInt64BigEndian(digest.Slice(16));
        d = BinaryPrimitives.ReadUInt64BigEndian(digest.Slice(24));
    }

    /// <summary>True for the default value, which no key derives.</summary>
    public bool IsEmpty => (a | b | c | d) == 0;

    /// <summary>Derives the identity of a validated 65-byte public key.</summary>
    internal static PersonaId Derive(ReadOnlySpan<byte> publicKey)
    {
        var tag = ProtocolConstants.PersonaIdDomainTag;
        Span<byte> input = stackalloc byte[1 + tag.Length + 1 + ProtocolConstants.PublicKeyLength];
        input[0] = (byte)tag.Length;
        tag.CopyTo(input.Slice(1));
        input[1 + tag.Length] = ProtocolConstants.PersonaKeyFormatUncompressedP256;
        publicKey.CopyTo(input.Slice(2 + tag.Length));

        Span<byte> digest = stackalloc byte[ProtocolConstants.DigestLength];
        SHA256.HashData(input, digest);
        return new PersonaId(digest);
    }

    /// <summary>Parses the text form strictly: the prefix, then exactly 64 lowercase hex digits, not all zero.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public static PersonaId Parse(string text)
    {
        if (!TryParse(text, out var id))
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "The persona identity is not 'psn_' followed by 64 lowercase hex digits.");
        }

        return id;
    }

    /// <summary>The strict parse of <see cref="Parse"/>, without the exception.</summary>
    public static bool TryParse(string? text, out PersonaId id)
    {
        id = default;
        if (text is null || text.Length != TextLength || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> digest = stackalloc byte[ProtocolConstants.DigestLength];
        if (!ProtocolHex.TryParseLower(text.AsSpan(Prefix.Length), digest))
        {
            return false;
        }

        var parsed = new PersonaId(digest);
        if (parsed.IsEmpty)
        {
            return false;
        }

        id = parsed;
        return true;
    }

    /// <summary>Writes the 32 digest bytes.</summary>
    public void WriteBytes(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, a);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8), b);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(16), c);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(24), d);
    }

    /// <summary>The 32 digest bytes.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[ProtocolConstants.DigestLength];
        WriteBytes(bytes);
        return bytes;
    }

    /// <summary>The text form.</summary>
    public override string ToString()
    {
        Span<byte> digest = stackalloc byte[ProtocolConstants.DigestLength];
        WriteBytes(digest);
        return Prefix + ProtocolHex.ToLower(digest);
    }

    /// <inheritdoc />
    public bool Equals(PersonaId other) => a == other.a && b == other.b && c == other.c && d == other.d;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PersonaId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(a, b, c, d);

    /// <summary>Value equality.</summary>
    public static bool operator ==(PersonaId left, PersonaId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(PersonaId left, PersonaId right) => !left.Equals(right);
}
