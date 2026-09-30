using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Personas.Storage;

/// <summary>
/// The persona registry's bytes (decision P3 in docs/networking/DecisionRegister.md), big-endian
/// throughout, holding no private material:
/// <code>
/// magic "AFPR" | version u16 = 1 | count u32 (0..256) | records | activeSlot[16] (all zero for none) | sha256[32] of everything before it
/// record = slot[16] (non-zero) | publicKey[65] | flags u8 (bit 0: K4 acknowledged) | labelLength u16 (UTF-16 code units, 1..64) | label (UTF-16BE code units)
/// </code>
/// The checksum guards against corruption only: whoever can rewrite the registry runs as the user
/// and can already read every key (K2). A label travels as UTF-16 code units, so no transcoding
/// can change it. Decoding refuses anything but exactly this layout: every length is checked
/// against what remains before it is read, and nothing is repaired. The magic and the version come
/// before the checksum, and every later version keeps them where they are, so a registry a newer
/// AetherFrame wrote is told apart from a damaged one, within this version's size limit. The one
/// exception is damage to the two version bytes that leaves a value above 1, which reads as a newer
/// version; nothing is overwritten either way.
/// </summary>
internal static class PersonaRegistryCodec
{
    /// <summary>The one version this build writes and reads.</summary>
    internal const ushort Version = 1;

    private const int HeaderLength = 4 + 2 + 4;
    private const int TrailerLength = PersonaSlotId.ByteLength + ChecksumLength;
    private const int ChecksumLength = 32;
    private const int RecordFixedLength = PersonaSlotId.ByteLength + ProtocolConstants.PublicKeyLength + 1 + 2;
    private const int MaxRecordLength = RecordFixedLength + (PersonaLabel.MaxLength * 2);
    private const byte AcknowledgedFlag = 0x01;

    /// <summary>The largest registry: 256 records with the longest labels, 54,330 bytes.</summary>
    internal const int MaxBytes = HeaderLength + (PersonaManager.MaxPersonas * MaxRecordLength) + TrailerLength;

    private static ReadOnlySpan<byte> Magic => "AFPR"u8;

    /// <summary>The registry holding <paramref name="records"/> in order, with <paramref name="active"/> selected (empty for none).</summary>
    internal static byte[] Encode(IReadOnlyList<PersonaRecord> records, PersonaSlotId active)
    {
        if (records.Count > PersonaManager.MaxPersonas)
        {
            throw new PersonaException(PersonaError.RegistryFull, $"The registry holds at most {PersonaManager.MaxPersonas} personas.");
        }

        // Index loops throughout: a foreach over the interface would refer to the non-generic
        // enumerator, which the assembly's type allowlist refuses.
        var length = HeaderLength + TrailerLength;
        for (var index = 0; index < records.Count; index++)
        {
            length += RecordFixedLength + (records[index].Label.Length * 2);
        }

        var bytes = new byte[length];
        var span = bytes.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt32BigEndian(span[6..], (uint)records.Count);
        var offset = HeaderLength;
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            record.Slot.WriteBytes(span.Slice(offset, PersonaSlotId.ByteLength));
            offset += PersonaSlotId.ByteLength;
            record.PublicKey.Bytes.CopyTo(span[offset..]);
            offset += ProtocolConstants.PublicKeyLength;
            span[offset++] = record.Acknowledged ? AcknowledgedFlag : (byte)0;
            BinaryPrimitives.WriteUInt16BigEndian(span[offset..], (ushort)record.Label.Length);
            offset += 2;
            foreach (var c in record.Label)
            {
                BinaryPrimitives.WriteUInt16BigEndian(span[offset..], c);
                offset += 2;
            }
        }

        if (!active.IsEmpty)
        {
            active.WriteBytes(span.Slice(offset, PersonaSlotId.ByteLength));
        }

        offset += PersonaSlotId.ByteLength;
        SHA256.HashData(span[..offset], span[offset..]);
        return bytes;
    }

    /// <summary>
    /// Decodes a registry, or explains why it is not one this build reads. A registry that decodes
    /// holds unique non-empty slots, valid and unique public keys, labels that are their own
    /// normalization, only known flags, and an active slot that names one of its records or none.
    /// </summary>
    internal static bool TryDecode(ReadOnlySpan<byte> bytes, out IReadOnlyList<PersonaRecord> records, out PersonaSlotId active, out string reason) =>
        TryDecode(bytes, out records, out active, out reason, out _);

    /// <summary>
    /// As <see cref="TryDecode(ReadOnlySpan{byte}, out IReadOnlyList{PersonaRecord}, out PersonaSlotId, out string)"/>,
    /// and says whether a refused registry names a later version. The magic and the version are
    /// checked before the checksum, so a registry a newer AetherFrame wrote, whatever its later
    /// layout, is told apart from a damaged one within this version's size limit.
    /// </summary>
    internal static bool TryDecode(ReadOnlySpan<byte> bytes, out IReadOnlyList<PersonaRecord> records, out PersonaSlotId active, out string reason, out bool newerVersion)
    {
        records = Array.Empty<PersonaRecord>();
        active = default;
        newerVersion = false;
        if (bytes.Length > MaxBytes)
        {
            reason = $"it is larger than the {MaxBytes}-byte limit";
            return false;
        }

        // The magic and the version first, which need only six bytes, so a later version is told
        // apart however short it is; then this version's own minimum.
        if (bytes.Length < 4 + 2)
        {
            reason = "it is shorter than a registry's header";
            return false;
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            reason = "it does not start with the registry magic";
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        if (version != Version)
        {
            newerVersion = version > Version;
            reason = newerVersion ? $"it is registry version {version}, which a newer AetherFrame wrote" : "it is a registry version this build does not read";
            return false;
        }

        if (bytes.Length < HeaderLength + TrailerLength)
        {
            reason = "it is shorter than an empty registry";
            return false;
        }

        Span<byte> checksum = stackalloc byte[ChecksumLength];
        SHA256.HashData(bytes[..^ChecksumLength], checksum);
        if (!checksum.SequenceEqual(bytes[^ChecksumLength..]))
        {
            reason = "its checksum does not match";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes[6..]);
        if (count > PersonaManager.MaxPersonas)
        {
            reason = $"it declares more than {PersonaManager.MaxPersonas} personas";
            return false;
        }

        var body = bytes[..^TrailerLength];
        var offset = HeaderLength;
        var decoded = new List<PersonaRecord>((int)count);
        var slots = new HashSet<PersonaSlotId>();
        var identities = new HashSet<PersonaId>();
        for (var index = 0; index < count; index++)
        {
            if (body.Length - offset < RecordFixedLength)
            {
                reason = "a record is cut short";
                return false;
            }

            if (!PersonaSlotId.TryFromBytes(body.Slice(offset, PersonaSlotId.ByteLength), out var slot) || slot.IsEmpty || !slots.Add(slot))
            {
                reason = "a record's slot is empty or repeated";
                return false;
            }

            offset += PersonaSlotId.ByteLength;
            PersonaPublicKey publicKey;
            try
            {
                publicKey = PersonaPublicKey.FromBytes(body.Slice(offset, ProtocolConstants.PublicKeyLength));
            }
            catch (ProtocolException)
            {
                reason = "a record's public key is not a P-256 point";
                return false;
            }

            if (!identities.Add(publicKey.Id))
            {
                reason = "two records hold the same identity";
                return false;
            }

            offset += ProtocolConstants.PublicKeyLength;
            var flags = body[offset++];
            if ((flags & ~AcknowledgedFlag) != 0)
            {
                reason = "a record sets a flag this build does not know";
                return false;
            }

            var labelLength = BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
            offset += 2;
            if (labelLength is 0 or > PersonaLabel.MaxLength)
            {
                reason = "a record's label length is out of range";
                return false;
            }

            if (body.Length - offset < labelLength * 2)
            {
                reason = "a record's label is cut short";
                return false;
            }

            var characters = new char[labelLength];
            for (var c = 0; c < labelLength; c++)
            {
                characters[c] = (char)BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset + (c * 2), 2));
            }

            offset += labelLength * 2;
            var label = new string(characters);
            if (!PersonaLabel.TryNormalize(label, out var normalized) || !string.Equals(normalized, label, StringComparison.Ordinal))
            {
                reason = "a record's label is not one the label rule keeps as it is";
                return false;
            }

            decoded.Add(new PersonaRecord(slot, publicKey, label, (flags & AcknowledgedFlag) != 0));
        }

        if (offset != body.Length)
        {
            reason = "bytes follow the last record";
            return false;
        }

        PersonaSlotId selected = default;
        var activeBytes = bytes.Slice(bytes.Length - TrailerLength, PersonaSlotId.ByteLength);
        if (activeBytes.IndexOfAnyExcept((byte)0) >= 0)
        {
            if (!PersonaSlotId.TryFromBytes(activeBytes, out selected) || !slots.Contains(selected))
            {
                reason = "the active slot names no record";
                return false;
            }
        }

        // Only a registry that decoded whole is handed out; a refusal leaves both empty.
        records = decoded;
        active = selected;
        reason = "";
        return true;
    }
}
