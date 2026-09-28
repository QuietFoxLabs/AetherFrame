using System;
using System.Buffers.Binary;

namespace AetherFrame.Protocol.Tests;

/// <summary>Copies of a signed document with one thing changed.</summary>
internal static class DocumentMutations
{
    public static byte[] Mutate(byte[] document, int offset, byte value)
    {
        var copy = (byte[])document.Clone();
        copy[offset] = value;
        return copy;
    }

    public static byte[] Flip(byte[] document, int offset) => Mutate(document, offset, (byte)(document[offset] ^ 0x01));

    public static byte[] Substitute(byte[] document, int offset, byte[] bytes)
    {
        var copy = (byte[])document.Clone();
        bytes.CopyTo(copy, offset);
        return copy;
    }

    public static byte[] WithPayloadLength(byte[] document, uint length)
    {
        var copy = (byte[])document.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(Layout.PayloadLength), length);
        return copy;
    }

    public static byte[] Truncate(byte[] document, int length) => document.AsSpan(0, length).ToArray();

    public static byte[] Append(byte[] document, params byte[] extra)
    {
        var copy = new byte[document.Length + extra.Length];
        document.CopyTo(copy, 0);
        extra.CopyTo(copy, document.Length);
        return copy;
    }
}
