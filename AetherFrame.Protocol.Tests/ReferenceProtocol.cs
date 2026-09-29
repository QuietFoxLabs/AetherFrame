using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// The byte layouts of docs/networking/ProtocolSpecification-v1.md (sections 4, 5 and 7) written
/// out from the tables, with no call into the library: what a second implementation would build.
/// Where the vector tests compare the library with this, a drift in either shows.
/// <para>
/// Until the freeze every document is a draft (decision N3): version 0x8001 and the tag ending in
/// "-draft". The final version 1 marker (version 1, the tag without the suffix) is kept here too,
/// only so that the tests can build a final document and prove that a draft reader refuses it.
/// </para>
/// </summary>
internal static class ReferenceProtocol
{
    /// <summary>The marker every document carries during the draft period: version 0x8001 and the "-draft" tag.</summary>
    public static readonly Marker Draft = new(0x8001, "AetherFrame.Protocol.SignedDocument.v1-draft");

    /// <summary>The marker a frozen version 1 document will carry: version 1 and the tag without the suffix.</summary>
    public static readonly Marker Final = new(0x0001, "AetherFrame.Protocol.SignedDocument.v1");

    private static readonly byte[] PersonaTag = System.Text.Encoding.ASCII.GetBytes("AetherFrame.Protocol.PersonaId.v1");

    /// <summary>Section 5: u8(len(tag)) ‖ tag ‖ u16(version) ‖ u8(type) ‖ key[65] ‖ u32(len) ‖ payload, with the draft marker.</summary>
    public static byte[] SigningInput(byte documentType, ReadOnlySpan<byte> publicKey65, ReadOnlySpan<byte> payload) =>
        SigningInput(Draft, documentType, publicKey65, payload);

    /// <summary>Section 5 with the given marker.</summary>
    public static byte[] SigningInput(Marker marker, byte documentType, ReadOnlySpan<byte> publicKey65, ReadOnlySpan<byte> payload)
    {
        var tag = marker.Tag;
        var buffer = new byte[1 + tag.Length + 2 + 1 + 65 + 4 + payload.Length];
        var offset = 0;
        buffer[offset++] = (byte)tag.Length;
        tag.CopyTo(buffer, offset);
        offset += tag.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), marker.Version);
        offset += 2;
        buffer[offset++] = documentType;
        publicKey65.CopyTo(buffer.AsSpan(offset));
        offset += 65;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), (uint)payload.Length);
        offset += 4;
        payload.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    /// <summary>SHA-256 of the draft signing input: what ECDSA signs.</summary>
    public static byte[] Digest(byte documentType, ReadOnlySpan<byte> publicKey65, ReadOnlySpan<byte> payload) =>
        SHA256.HashData(SigningInput(documentType, publicKey65, payload));

    /// <summary>Section 4: psn_ + hex(SHA-256(u8(33) ‖ tag ‖ u8(0x01) ‖ key[65])). The same for drafts and final documents.</summary>
    public static string PersonaId(ReadOnlySpan<byte> publicKey65)
    {
        var input = new byte[1 + PersonaTag.Length + 1 + 65];
        input[0] = (byte)PersonaTag.Length;
        PersonaTag.CopyTo(input, 1);
        input[1 + PersonaTag.Length] = 0x01;
        publicKey65.CopyTo(input.AsSpan(2 + PersonaTag.Length));
        return "psn_" + Convert.ToHexStringLower(SHA256.HashData(input));
    }

    /// <summary>Section 7: "AFPD" ‖ u16(version) ‖ u8(type) ‖ key[65] ‖ u32(len) ‖ payload ‖ signature[64], with the draft marker.</summary>
    public static byte[] Document(byte documentType, ReadOnlySpan<byte> publicKey65, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature64) =>
        Document(Draft, documentType, publicKey65, payload, signature64);

    /// <summary>Section 7 with the given marker's version.</summary>
    public static byte[] Document(Marker marker, byte documentType, ReadOnlySpan<byte> publicKey65, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature64)
    {
        var buffer = new byte[4 + 2 + 1 + 65 + 4 + payload.Length + 64];
        var offset = 0;
        "AFPD"u8.CopyTo(buffer.AsSpan(offset));
        offset += 4;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), marker.Version);
        offset += 2;
        buffer[offset++] = documentType;
        publicKey65.CopyTo(buffer.AsSpan(offset));
        offset += 65;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), (uint)payload.Length);
        offset += 4;
        payload.CopyTo(buffer.AsSpan(offset));
        offset += payload.Length;
        signature64.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    /// <summary>A protocol version and its signature domain tag.</summary>
    internal sealed class Marker
    {
        public Marker(ushort version, string tag)
        {
            Version = version;
            Tag = System.Text.Encoding.ASCII.GetBytes(tag);
        }

        public ushort Version { get; }

        public byte[] Tag { get; }
    }
}
