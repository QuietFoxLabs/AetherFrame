using System;
using System.Buffers.Binary;

namespace AetherFrame.Protocol.Encoding;

/// <summary>
/// Reads the canonical encoding written by <see cref="CanonicalWriter"/>, treating the input as
/// hostile: every value is checked against what remains before it is read, every declared length
/// or count is checked against its limit before anything is allocated or copied, and the caller
/// must call <see cref="ExpectEnd"/> so trailing bytes are refused. It allocates nothing itself and
/// hands out slices of its input, so nothing is ever sized by a declared length that has not
/// passed its limit; all it can throw is <see cref="ProtocolException"/>.
/// </summary>
internal ref struct CanonicalReader
{
    private readonly ReadOnlySpan<byte> input;
    private int position;

    public CanonicalReader(ReadOnlySpan<byte> input)
    {
        this.input = input;
        position = 0;
    }

    /// <summary>The bytes not yet read.</summary>
    public readonly int Remaining => input.Length - position;

    /// <summary>The bytes read so far.</summary>
    public readonly int Position => position;

    public byte ReadU8(string field) => Take(1, field)[0];

    public ushort ReadU16(string field) => BinaryPrimitives.ReadUInt16BigEndian(Take(2, field));

    public uint ReadU32(string field) => BinaryPrimitives.ReadUInt32BigEndian(Take(4, field));

    public ulong ReadU64(string field) => BinaryPrimitives.ReadUInt64BigEndian(Take(8, field));

    /// <summary>Reads a signed four-byte integer (two's complement, big-endian): the one signed type, used only by schema 2's layout.</summary>
    public int ReadI32(string field) => BinaryPrimitives.ReadInt32BigEndian(Take(4, field));

    /// <summary>Reads bytes whose length the schema fixes.</summary>
    public ReadOnlySpan<byte> ReadFixed(int length, string field) => Take(length, field);

    /// <summary>
    /// Reads a four-byte length and the bytes it announces. A length over <paramref name="maxLength"/>
    /// is refused as over the limit before the input's own length is consulted, so a hostile length
    /// can never size an allocation or hide behind a truncation error.
    /// </summary>
    public ReadOnlySpan<byte> ReadLengthPrefixed(int maxLength, string field)
    {
        var length = ReadU32(field);
        if (length > (uint)maxLength)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"'{field}' declares {ProtocolText.Number(length)} bytes; the limit is {ProtocolText.Number(maxLength)}.");
        }

        return Take((int)length, field);
    }

    /// <summary>Reads a text field (see <see cref="ProtocolText"/>).</summary>
    public string ReadText(string field) => ProtocolText.Decode(ReadLengthPrefixed(ProtocolLimits.MaxTextBytes, field), field);

    /// <summary>Reads a text field whose own limit is <paramref name="maxScalars"/> scalars, and so at most four times that in bytes.</summary>
    public string ReadText(string field, int maxScalars) => ProtocolText.Decode(ReadLengthPrefixed(maxScalars * 4, field), field, maxScalars);

    /// <summary>
    /// Reads a name field (see <see cref="ProtocolName"/>): its byte length is checked against the
    /// name's own limit before the input's length is consulted, like every length.
    /// </summary>
    public string ReadName(string field) => ProtocolName.Decode(ReadLengthPrefixed(ProtocolLimits.MaxNameBytes, field), field);

    /// <summary>
    /// Reads the number of items that follow, refusing a count over <paramref name="maxCount"/>
    /// before any item is read, so a hostile count can never size an allocation.
    /// </summary>
    public int ReadCount(int maxCount, string field)
    {
        var count = ReadU32(field);
        if (count > (uint)maxCount)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"'{field}' declares {ProtocolText.Number(count)} items; the limit is {ProtocolText.Number(maxCount)}.");
        }

        return (int)count;
    }

    /// <summary>Refuses input that continues after the last value of <paramref name="what"/>.</summary>
    public readonly void ExpectEnd(string what)
    {
        if (Remaining != 0)
        {
            throw new ProtocolException(ProtocolError.TrailingBytes, $"{what} is followed by {ProtocolText.Number(Remaining)} unexpected bytes.");
        }
    }

    private ReadOnlySpan<byte> Take(int length, string field)
    {
        if (length > Remaining)
        {
            throw new ProtocolException(ProtocolError.Truncated, $"The input ends inside '{field}': {ProtocolText.Number(length)} bytes were expected, {ProtocolText.Number(Remaining)} remain.");
        }

        var slice = input.Slice(position, length);
        position += length;
        return slice;
    }
}
