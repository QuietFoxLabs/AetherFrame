using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// The byte layouts of docs/networking/ProtocolSpecification-v1.md (sections 4, 5 and 7) written
/// out from the tables, with no call into the library: what a second implementation would build.
/// Where the vector tests compare the library with this, a drift in either shows.
/// </summary>
internal static class ReferenceProtocol
{
    private static readonly byte[] SignatureTag = System.Text.Encoding.ASCII.GetBytes("AetherFrame.Protocol.SignedDocument.v1");
    private static readonly byte[] PersonaTag = System.Text.Encoding.ASCII.GetBytes("AetherFrame.Protocol.PersonaId.v1");

    /// <summary>Section 5: u8(38) ‖ tag ‖ u16(1) ‖ u8(type) ‖ key[65] ‖ u32(len) ‖ payload.</summary>
    public static byte[] SigningInput(byte documentType, ReadOnlySpan<byte> publicKey65, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[1 + SignatureTag.Length + 2 + 1 + 65 + 4 + payload.Length];
        var offset = 0;
        buffer[offset++] = (byte)SignatureTag.Length;
        SignatureTag.CopyTo(buffer, offset);
        offset += SignatureTag.Length;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x01;
        buffer[offset++] = documentType;
        publicKey65.CopyTo(buffer.AsSpan(offset));
        offset += 65;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), (uint)payload.Length);
        offset += 4;
        payload.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    /// <summary>SHA-256 of the signing input: what ECDSA signs.</summary>
    public static byte[] Digest(byte documentType, ReadOnlySpan<byte> publicKey65, ReadOnlySpan<byte> payload) =>
        SHA256.HashData(SigningInput(documentType, publicKey65, payload));

    /// <summary>Section 4: psn_ + hex(SHA-256(u8(33) ‖ tag ‖ u8(0x01) ‖ key[65])).</summary>
    public static string PersonaId(ReadOnlySpan<byte> publicKey65)
    {
        var input = new byte[1 + PersonaTag.Length + 1 + 65];
        input[0] = (byte)PersonaTag.Length;
        PersonaTag.CopyTo(input, 1);
        input[1 + PersonaTag.Length] = 0x01;
        publicKey65.CopyTo(input.AsSpan(2 + PersonaTag.Length));
        return "psn_" + Convert.ToHexStringLower(SHA256.HashData(input));
    }

    /// <summary>Section 7: "AFPD" ‖ u16(1) ‖ u8(type) ‖ key[65] ‖ u32(len) ‖ payload ‖ signature[64].</summary>
    public static byte[] Document(byte documentType, ReadOnlySpan<byte> publicKey65, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature64)
    {
        var buffer = new byte[4 + 2 + 1 + 65 + 4 + payload.Length + 64];
        var offset = 0;
        "AFPD"u8.CopyTo(buffer.AsSpan(offset));
        offset += 4;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x01;
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
}
