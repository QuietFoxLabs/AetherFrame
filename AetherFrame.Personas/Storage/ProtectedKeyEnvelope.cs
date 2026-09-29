using System;
using System.Buffers.Binary;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Personas.Storage;

/// <summary>
/// The at-rest form of one protected key, version 1: a header naming the slot, the protector and the
/// public key, then the protector's blob. The header is the protector's context, so a blob moved
/// into another envelope (another slot, another public key, another protector) does not open.
/// Big-endian throughout, and exact: no trailing bytes. There is no checksum: damage shows as an
/// envelope that does not decode, a blob the protector refuses, or a scalar that does not belong to
/// the recorded public key, and every one of those is "unavailable" to the store.
/// <code>
/// magic "AFPK" (4) | version u16 = 1 (2) | slot (16) | protector id: length u8, ASCII | public key (65) | blob: length u32, bytes
/// </code>
/// </summary>
internal static class ProtectedKeyEnvelope
{
    /// <summary>The one version this build writes and reads.</summary>
    internal const ushort Version = 1;

    /// <summary>The longest protector id, in bytes.</summary>
    internal const int MaxProtectorIdLength = 64;

    /// <summary>The longest blob, in bytes: far above any platform's protected form of 32 bytes.</summary>
    internal const int MaxBlobLength = 4096;

    /// <summary>The bytes before the protector id's characters: magic, version, slot, id length.</summary>
    private const int FixedHeaderLength = 4 + 2 + PersonaSlotId.ByteLength + 1;

    private static ReadOnlySpan<byte> Magic => "AFPK"u8;

    /// <summary>What an envelope decodes to. <see cref="Context"/> is the header, byte for byte. (A plain struct: a record would pull System.Text into the assembly.)</summary>
    internal readonly struct Decoded
    {
        internal Decoded(PersonaSlotId slot, string protectorId, PersonaPublicKey publicKey, byte[] context, byte[] blob)
        {
            Slot = slot;
            ProtectorId = protectorId;
            PublicKey = publicKey;
            Context = context;
            Blob = blob;
        }

        internal PersonaSlotId Slot { get; }

        internal string ProtectorId { get; }

        internal PersonaPublicKey PublicKey { get; }

        internal byte[] Context { get; }

        internal byte[] Blob { get; }
    }

    /// <summary>True for a protector id an envelope can carry: 1 to 64 lowercase letters, digits, dots and hyphens.</summary>
    internal static bool IsValidProtectorId(string? id)
    {
        if (id is null || id.Length is 0 or > MaxProtectorIdLength)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (c > 0x7F || !IsIdByte((byte)c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The header for a key, which is also the protector's context. <paramref name="protectorId"/> must be valid.</summary>
    internal static byte[] EncodeHeader(PersonaSlotId slot, string protectorId, PersonaPublicKey publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (slot.IsEmpty || !IsValidProtectorId(protectorId))
        {
            throw new ArgumentException("An envelope needs a non-empty slot and a valid protector id.");
        }

        var header = new byte[FixedHeaderLength + protectorId.Length + ProtocolConstants.PublicKeyLength];
        var span = header.AsSpan();
        Magic.CopyTo(span);
        span = span[Magic.Length..];
        BinaryPrimitives.WriteUInt16BigEndian(span, Version);
        span = span[2..];
        slot.WriteBytes(span);
        span = span[PersonaSlotId.ByteLength..];
        span[0] = (byte)protectorId.Length;
        span = span[1..];
        for (var index = 0; index < protectorId.Length; index++)
        {
            span[index] = (byte)protectorId[index];
        }

        span = span[protectorId.Length..];
        publicKey.Bytes.CopyTo(span);
        return header;
    }

    /// <summary>The envelope: <paramref name="header"/> (from <see cref="EncodeHeader"/>) followed by the length-prefixed <paramref name="blob"/>.</summary>
    internal static byte[] Encode(ReadOnlySpan<byte> header, ReadOnlySpan<byte> blob)
    {
        if (blob.Length is 0 or > MaxBlobLength)
        {
            throw new ArgumentException("A blob is 1 to 4096 bytes.", nameof(blob));
        }

        var envelope = new byte[header.Length + 4 + blob.Length];
        header.CopyTo(envelope);
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(header.Length), (uint)blob.Length);
        blob.CopyTo(envelope.AsSpan(header.Length + 4));
        return envelope;
    }

    /// <summary>
    /// Decodes an envelope of this version exactly: false for anything else, including any trailing
    /// byte, a zero slot, an invalid protector id, a public key that is not a point on P-256, or a
    /// blob of length 0 or over <see cref="MaxBlobLength"/>. Never throws.
    /// </summary>
    internal static bool TryDecode(ReadOnlySpan<byte> envelope, out Decoded decoded)
    {
        decoded = default;
        const int shortestHeader = FixedHeaderLength + 1 + ProtocolConstants.PublicKeyLength;
        if (envelope.Length < shortestHeader + 4 + 1 || !envelope[..Magic.Length].SequenceEqual(Magic))
        {
            return false;
        }

        var offset = Magic.Length;
        if (BinaryPrimitives.ReadUInt16BigEndian(envelope[offset..]) != Version)
        {
            return false;
        }

        offset += 2;
        if (!PersonaSlotId.TryFromBytes(envelope.Slice(offset, PersonaSlotId.ByteLength), out var slot))
        {
            return false;
        }

        offset += PersonaSlotId.ByteLength;
        int idLength = envelope[offset];
        offset += 1;
        if (idLength is 0 or > MaxProtectorIdLength || envelope.Length < offset + idLength + ProtocolConstants.PublicKeyLength + 4 + 1)
        {
            return false;
        }

        var idChars = new char[idLength];
        for (var index = 0; index < idLength; index++)
        {
            var b = envelope[offset + index];
            if (!IsIdByte(b))
            {
                return false;
            }

            idChars[index] = (char)b;
        }

        offset += idLength;
        PersonaPublicKey publicKey;
        try
        {
            publicKey = PersonaPublicKey.FromBytes(envelope.Slice(offset, ProtocolConstants.PublicKeyLength));
        }
        catch (ProtocolException)
        {
            return false;
        }

        offset += ProtocolConstants.PublicKeyLength;
        var context = envelope[..offset].ToArray();
        var blobLength = BinaryPrimitives.ReadUInt32BigEndian(envelope[offset..]);
        offset += 4;
        if (blobLength is 0 or > MaxBlobLength || (uint)(envelope.Length - offset) != blobLength)
        {
            return false;
        }

        decoded = new Decoded(slot, new string(idChars), publicKey, context, envelope[offset..].ToArray());
        return true;
    }

    private static bool IsIdByte(byte b) =>
        (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') || b == '.' || b == '-';
}
