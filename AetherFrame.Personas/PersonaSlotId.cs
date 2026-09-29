using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AetherFrame.Personas;

/// <summary>
/// The local handle of one persona record on this installation: 16 random bytes, written as
/// <c>slot_</c> and 32 lowercase hex digits. It is minted here, never derived from the persona's
/// key or identity, never sent anywhere, and never equal on two installations that hold the same
/// persona. It exists so that logs, file names and error text can refer to a persona without
/// naming its public identity, which would let two mentions be correlated. The text form is the
/// in-memory model's; how a slot would be written to disk is not decided by this type.
/// </summary>
public readonly struct PersonaSlotId : IEquatable<PersonaSlotId>
{
    /// <summary>The text form's prefix.</summary>
    public const string Prefix = "slot_";

    /// <summary>The number of random bytes.</summary>
    public const int ByteLength = 16;

    /// <summary>The length of the text form: the prefix and 32 hex digits.</summary>
    public const int TextLength = 5 + (ByteLength * 2);

    private readonly ulong high;
    private readonly ulong low;

    private PersonaSlotId(ulong high, ulong low)
    {
        this.high = high;
        this.low = low;
    }

    /// <summary>True for the default value, which never names a persona.</summary>
    public bool IsEmpty => (high | low) == 0;

    /// <summary>A fresh random handle.</summary>
    public static PersonaSlotId NewId()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        do
        {
            RandomNumberGenerator.Fill(bytes);
        }
        while (bytes.IndexOfAnyExcept((byte)0) < 0);

        return new PersonaSlotId(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8)));
    }

    /// <summary>Parses the text form strictly: the prefix, then exactly 32 lowercase hex digits, not all zero.</summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidIdentifier"/>.</exception>
    public static PersonaSlotId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new PersonaException(PersonaError.InvalidIdentifier, "The persona slot is not the slot prefix followed by 32 lowercase hex digits.");

    /// <summary>The strict parse of <see cref="Parse"/>, without the exception.</summary>
    public static bool TryParse(string? text, out PersonaSlotId id)
    {
        id = default;
        if (text is null || text.Length != TextLength || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[ByteLength];
        var digits = text.AsSpan(Prefix.Length);
        for (var index = 0; index < ByteLength; index++)
        {
            var upper = Digit(digits[index * 2]);
            var lower = Digit(digits[(index * 2) + 1]);
            if (upper < 0 || lower < 0)
            {
                return false;
            }

            bytes[index] = (byte)((upper << 4) | lower);
        }

        var parsed = new PersonaSlotId(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8)));
        if (parsed.IsEmpty)
        {
            return false;
        }

        id = parsed;
        return true;
    }

    /// <summary>The text form.</summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, high);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(8), low);
        return Prefix + Convert.ToHexStringLower(bytes);
    }

    /// <inheritdoc />
    public bool Equals(PersonaSlotId other) => high == other.high && low == other.low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PersonaSlotId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(high, low);

    /// <summary>Value equality.</summary>
    public static bool operator ==(PersonaSlotId left, PersonaSlotId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(PersonaSlotId left, PersonaSlotId right) => !left.Equals(right);

    private static int Digit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };
}
