using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AetherFrame.Protocol.Identity;

/// <summary>
/// What the opaque identifiers (<see cref="ProfileId"/>, <see cref="RevisionId"/>, <see cref="AssetId"/>)
/// share: 16 random bytes, held as two unsigned 64-bit halves in big-endian order so that comparing
/// the halves is comparing the bytes; a text form of a prefix and 32 lowercase hex digits; and an
/// all-zero value that means "unset" and is refused everywhere (docs/networking/ProtocolSpecification-v1.md,
/// "Opaque identifiers").
/// </summary>
internal static class Id128
{
    public const int TextLength = 4 + (ProtocolConstants.OpaqueIdLength * 2);

    public static (ulong High, ulong Low) NewRandom()
    {
        Span<byte> bytes = stackalloc byte[ProtocolConstants.OpaqueIdLength];
        do
        {
            RandomNumberGenerator.Fill(bytes);
        }
        while (IsZero(bytes));

        return Read(bytes);
    }

    public static (ulong High, ulong Low) Read(ReadOnlySpan<byte> bytes) =>
        (BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8)));

    public static void Write(ulong high, ulong low, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, high);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8), low);
    }

    public static string Format(string prefix, ulong high, ulong low)
    {
        Span<byte> bytes = stackalloc byte[ProtocolConstants.OpaqueIdLength];
        Write(high, low, bytes);
        return prefix + ProtocolHex.ToLower(bytes);
    }

    /// <summary>Reads 16 wire bytes, refusing the all-zero value.</summary>
    public static (ulong High, ulong Low) FromBytes(ReadOnlySpan<byte> bytes, string what)
    {
        if (bytes.Length != ProtocolConstants.OpaqueIdLength)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"A {what} is {ProtocolConstants.OpaqueIdLength} bytes.");
        }

        if (IsZero(bytes))
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"A {what} is never all zero.");
        }

        return Read(bytes);
    }

    /// <summary>Parses the text form strictly: the prefix, then exactly 32 lowercase hex digits, not all zero.</summary>
    public static bool TryParse(string prefix, string? text, out ulong high, out ulong low)
    {
        high = 0;
        low = 0;
        if (text is null || text.Length != TextLength || !text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[ProtocolConstants.OpaqueIdLength];
        if (!ProtocolHex.TryParseLower(text.AsSpan(prefix.Length), bytes) || IsZero(bytes))
        {
            return false;
        }

        (high, low) = Read(bytes);
        return true;
    }

    public static int Compare(ulong highA, ulong lowA, ulong highB, ulong lowB)
    {
        var byHigh = highA.CompareTo(highB);
        return byHigh != 0 ? byHigh : lowA.CompareTo(lowB);
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes) => bytes.IndexOfAnyExcept((byte)0) < 0;
}
