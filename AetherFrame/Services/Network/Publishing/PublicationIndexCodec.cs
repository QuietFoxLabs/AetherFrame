using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using AetherFrame.Personas;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>Why a publication file was refused.</summary>
internal enum PublicationFileProblem
{
    /// <summary>It can't be read, or isn't exactly a file this build writes: damaged, or not one of ours.</summary>
    Damaged,

    /// <summary>It names a later version, which a newer AetherFrame wrote (or damage that left a value above 1 there).</summary>
    NewerVersion,
}

/// <summary>A publication index or outbox entry refused. Its message names no path, id or content.</summary>
internal sealed class PublicationFileException : Exception
{
    internal PublicationFileException(PublicationFileProblem problem, string message)
        : base(message)
    {
        Problem = problem;
    }

    internal PublicationFileException(PublicationFileProblem problem, string message, Exception inner)
        : base(message, inner)
    {
        Problem = problem;
    }

    /// <summary>Which rule refused it.</summary>
    internal PublicationFileProblem Problem { get; }
}

/// <summary>
/// A publication index's bytes (decisions P1 and P4, and N2-6's design, section 9), big-endian
/// throughout:
/// <code>
/// magic "AFPI" | version u16 = 1 | slot[16] | count u16 (0..256) | entries | sha256[32] of everything before it
/// entry = plateId[16] | profileId[16] | latestRevision[16] | state u8 | lastPublishedAt i64 | pendingEntry[16] | reserved[16]
/// </code>
/// The slot is the persona's, so an index moved to another persona's name is refused. The Plate id
/// is the local Plate's, in RFC 4122 order; the profile and revision ids are the protocol's; the
/// state is <see cref="PublicationState"/>'s value; the last publish time is 0 until the server
/// acknowledges a revision; the pending entry is an outbox entry's name, all zero for none. The
/// last 16 bytes of an entry are reserved and all zero: they were laid out for a share code, which
/// R5 retired before one was ever stored, so this build refuses anything else in them. The largest
/// index, 256 entries, is 22,840 bytes, inside the design's bound of 36,000.
/// <para>
/// The checksum guards against corruption only, as the registry's does (P3). Decoding refuses
/// anything but exactly this layout and <see cref="PublicationIndex.Problem"/>'s rules, and never
/// repairs: the size first, then the magic and the version, so an index a newer AetherFrame wrote
/// is told apart from a damaged one, as the registry's are (P3); then the length, the checksum,
/// the slot, the count and every entry. Every index is encoded and decoded again before it is
/// saved, as the registry is.
/// </para>
/// </summary>
internal static class PublicationIndexCodec
{
    /// <summary>The one version this build writes and reads.</summary>
    internal const ushort Version = 1;

    private const int SlotLength = PersonaSlotId.ByteLength;
    private const int IdLength = ProtocolConstants.OpaqueIdLength;
    private const int ShareCodeLength = 16;
    private const int HeaderLength = 4 + 2 + SlotLength + 2;
    private const int EntryLength = 16 + IdLength + IdLength + 1 + 8 + OutboxEntryName.ByteLength + ShareCodeLength;
    private const int ChecksumLength = 32;

    /// <summary>The largest index: 256 entries, 22,840 bytes.</summary>
    internal const int MaxBytes = HeaderLength + (PublicationIndex.MaxEntries * EntryLength) + ChecksumLength;

    private static ReadOnlySpan<byte> Magic => "AFPI"u8;

    /// <summary>The bytes of <paramref name="index"/> as <paramref name="slot"/>'s, checked by decoding them again.</summary>
    internal static byte[] Encode(PersonaSlotId slot, PublicationIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (slot.IsEmpty)
        {
            throw new ArgumentException("An index belongs to a persona's slot.", nameof(slot));
        }

        var entries = index.Entries;
        var bytes = new byte[HeaderLength + (entries.Count * EntryLength) + ChecksumLength];
        var span = bytes.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], Version);
        WriteSlot(slot, span.Slice(6, SlotLength));
        BinaryPrimitives.WriteUInt16BigEndian(span[(6 + SlotLength)..], (ushort)entries.Count);
        var offset = HeaderLength;
        for (var position = 0; position < entries.Count; position++)
        {
            var entry = entries[position];
            if (!entry.PlateId.TryWriteBytes(span.Slice(offset, 16), bigEndian: true, out _))
            {
                throw new InvalidOperationException("A Plate id is 16 bytes.");
            }

            offset += 16;
            entry.ProfileId.WriteBytes(span.Slice(offset, IdLength));
            offset += IdLength;
            entry.LatestRevision.WriteBytes(span.Slice(offset, IdLength));
            offset += IdLength;
            span[offset++] = (byte)entry.State;
            BinaryPrimitives.WriteInt64BigEndian(span[offset..], entry.LastPublishedAt);
            offset += 8;
            entry.PendingEntry.WriteBytes(span.Slice(offset, OutboxEntryName.ByteLength));
            offset += OutboxEntryName.ByteLength;

            // The reserved field (laid out for a share code, which R5 retired) stays zero.
            offset += ShareCodeLength;
        }

        SHA256.HashData(span[..offset], span[offset..]);

        // The reader decides what an index is: bytes it would refuse are never saved.
        Decode(bytes, slot);
        return bytes;
    }

    /// <summary>The index <paramref name="bytes"/> hold for <paramref name="slot"/>, refusing anything else.</summary>
    /// <exception cref="PublicationFileException">
    /// <see cref="PublicationFileProblem.NewerVersion"/> for a later version within this version's
    /// size limit, and <see cref="PublicationFileProblem.Damaged"/> for everything else refused.
    /// </exception>
    internal static PublicationIndex Decode(ReadOnlySpan<byte> bytes, PersonaSlotId slot)
    {
        if (bytes.Length > MaxBytes)
        {
            throw Damaged("The index is larger than any index this build reads.");
        }

        if (bytes.Length < 6 || !bytes[..4].SequenceEqual(Magic))
        {
            throw Damaged("The file isn't a publication index.");
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        if (version > Version)
        {
            throw new PublicationFileException(PublicationFileProblem.NewerVersion, "The index was written by a newer AetherFrame.");
        }

        if (version != Version)
        {
            throw Damaged("The index names a version that never existed.");
        }

        if (bytes.Length < HeaderLength + ChecksumLength)
        {
            throw Damaged("The index is shorter than its header and checksum.");
        }

        var body = bytes[..^ChecksumLength];
        Span<byte> digest = stackalloc byte[ChecksumLength];
        SHA256.HashData(body, digest);
        if (!digest.SequenceEqual(bytes[^ChecksumLength..]))
        {
            throw Damaged("The index's checksum doesn't match: it is damaged.");
        }

        Span<byte> expected = stackalloc byte[SlotLength];
        WriteSlot(slot, expected);
        if (slot.IsEmpty || !bytes.Slice(6, SlotLength).SequenceEqual(expected))
        {
            throw Damaged("The index belongs to another persona's slot.");
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes[(6 + SlotLength)..]);
        if (count > PublicationIndex.MaxEntries || body.Length != HeaderLength + (count * EntryLength))
        {
            throw Damaged("The index's length doesn't match its count of entries.");
        }

        var entries = new List<PublicationEntry>(count);
        var offset = HeaderLength;
        for (var position = 0; position < count; position++)
        {
            entries.Add(ReadEntry(bytes.Slice(offset, EntryLength)));
            offset += EntryLength;
        }

        if (PublicationIndex.Problem(entries) is { } problem)
        {
            throw Damaged(problem);
        }

        return new PublicationIndex(entries);
    }

    /// <summary>The slot's 16 bytes, from its text form (<c>slot_</c> and 32 lowercase hex digits), the persona library's public form of it.</summary>
    internal static void WriteSlot(PersonaSlotId slot, Span<byte> destination)
    {
        var text = slot.ToString();
        if (Convert.FromHexString(text.AsSpan(PersonaSlotId.Prefix.Length), destination, out _, out var written) != System.Buffers.OperationStatus.Done || written != SlotLength)
        {
            throw new InvalidOperationException("A slot's text form is its prefix and 32 hex digits.");
        }
    }

    private static PublicationEntry ReadEntry(ReadOnlySpan<byte> entry)
    {
        var plate = new Guid(entry[..16], bigEndian: true);
        ProfileId profile;
        RevisionId revision;
        try
        {
            profile = ProfileId.FromBytes(entry.Slice(16, IdLength));
            revision = RevisionId.FromBytes(entry.Slice(16 + IdLength, IdLength));
        }
        catch (ProtocolException e)
        {
            throw new PublicationFileException(PublicationFileProblem.Damaged, "An entry's profile or revision id is all zero.", e);
        }

        var offset = 16 + IdLength + IdLength;
        var state = entry[offset++];
        if (state is not ((byte)PublicationState.Pending or (byte)PublicationState.Published or (byte)PublicationState.Retracting))
        {
            throw Damaged("An entry's state is unknown.");
        }

        var lastPublishedAt = BinaryPrimitives.ReadInt64BigEndian(entry[offset..]);
        offset += 8;
        var pending = OutboxEntryName.FromBytes(entry.Slice(offset, OutboxEntryName.ByteLength));
        offset += OutboxEntryName.ByteLength;
        if (entry.Slice(offset, ShareCodeLength).IndexOfAnyExcept((byte)0) >= 0)
        {
            throw Damaged("An entry holds data in its reserved field, which this build doesn't read.");
        }

        return new PublicationEntry(plate, profile, revision, (PublicationState)state, lastPublishedAt, pending);
    }

    private static PublicationFileException Damaged(string message) => new(PublicationFileProblem.Damaged, message);
}
