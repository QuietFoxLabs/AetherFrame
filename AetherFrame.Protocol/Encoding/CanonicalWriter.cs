using System;
using System.Buffers;
using System.Buffers.Binary;

namespace AetherFrame.Protocol.Encoding;

/// <summary>
/// Writes the canonical encoding (docs/networking/ProtocolSpecification-v1.md, "Encoding"):
/// big-endian fixed-width integers, byte strings and text with a four-byte length, counts as four
/// bytes. There is exactly one encoding of every value, so the same content always produces the
/// same bytes: nothing here depends on the culture, the platform, or the order in which a model
/// was built.
/// </summary>
internal sealed class CanonicalWriter
{
    private readonly ArrayBufferWriter<byte> buffer;

    public CanonicalWriter(int initialCapacity = 256)
    {
        buffer = new ArrayBufferWriter<byte>(initialCapacity);
    }

    /// <summary>The bytes written so far.</summary>
    public int Length => buffer.WrittenCount;

    public void WriteU8(byte value)
    {
        buffer.GetSpan(1)[0] = value;
        buffer.Advance(1);
    }

    public void WriteU16(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.GetSpan(2), value);
        buffer.Advance(2);
    }

    public void WriteU32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.GetSpan(4), value);
        buffer.Advance(4);
    }

    public void WriteU64(ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(buffer.GetSpan(8), value);
        buffer.Advance(8);
    }

    /// <summary>Writes bytes whose length the reader knows from the schema (a key, an identifier, a digest).</summary>
    public void WriteFixed(ReadOnlySpan<byte> bytes)
    {
        buffer.Write(bytes);
    }

    /// <summary>Writes a four-byte length followed by the bytes.</summary>
    public void WriteLengthPrefixed(ReadOnlySpan<byte> bytes)
    {
        WriteU32(checked((uint)bytes.Length));
        buffer.Write(bytes);
    }

    /// <summary>Writes a text field: a four-byte UTF-8 byte length followed by the UTF-8 bytes (see <see cref="ProtocolText"/>).</summary>
    public void WriteText(string text, string field)
    {
        WriteLengthPrefixed(ProtocolText.Encode(text, field));
    }

    /// <summary>Writes the number of items that follow.</summary>
    public void WriteCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        WriteU32((uint)count);
    }

    public byte[] ToArray() => buffer.WrittenSpan.ToArray();
}
