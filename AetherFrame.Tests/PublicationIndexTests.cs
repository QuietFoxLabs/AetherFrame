using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using AetherFrame.Personas;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Services.Network.Publishing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// NETWORK2's publication index (N2-6c; decisions P1 and P4, and N2-6's design, section 9): its
/// rules, and its bytes, exact and bounded, with every refusal. Nothing is repaired, and an index
/// a newer AetherFrame wrote is told apart from a damaged one.
/// </summary>
public sealed class PublicationIndexTests
{
    private const int Header = 24;
    private const int Entry = 89;
    private const int Checksum = 32;

    private readonly PersonaSlotId slot = PersonaSlotId.NewId();

    [Fact]
    public void AnIndex_IsExactlyItsLayout_AndReadsBackAsItself()
    {
        var plate = Guid.NewGuid();
        var pending = Pending(plate);
        var published = new PublicationEntry(Guid.NewGuid(), ProfileId.NewId(), RevisionId.NewId(), PublicationState.Published, 1_790_000_123, OutboxEntryName.NewName());
        var retracting = new PublicationEntry(plate, ProfileId.NewId(), RevisionId.NewId(), PublicationState.Retracting, 0, OutboxEntryName.None);
        var index = new PublicationIndex([pending, published, retracting]);

        var bytes = PublicationIndexCodec.Encode(slot, index);

        Assert.Equal(Header + (3 * Entry) + Checksum, bytes.Length);
        Assert.Equal("AFPI"u8.ToArray(), bytes[..4]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4)));
        Assert.Equal(Convert.FromHexString(slot.ToString()["slot_".Length..]), bytes[6..22]);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22)));

        var second = bytes.AsSpan(Header + Entry, Entry);
        Assert.Equal(published.PlateId.ToByteArray(bigEndian: true), second[..16].ToArray());
        Assert.Equal(published.ProfileId.ToArray(), second[16..32].ToArray());
        Assert.Equal(published.LatestRevision.ToArray(), second[32..48].ToArray());
        Assert.Equal((byte)PublicationState.Published, second[48]);
        Assert.Equal(1_790_000_123, BinaryPrimitives.ReadInt64BigEndian(second[49..]));
        Assert.Equal(published.PendingEntry.FileName[..32], Convert.ToHexStringLower(second[57..73]));
        Assert.True(second[73..89].IndexOfAnyExcept((byte)0) < 0, "the share code field is zero");
        Assert.True(bytes.AsSpan(Header + (2 * Entry) + 57, 16).IndexOfAnyExcept((byte)0) < 0, "an entry naming no outbox entry holds zeros there");
        Assert.Equal(SHA256.HashData(bytes.AsSpan(0, bytes.Length - Checksum)), bytes[^Checksum..]);

        var read = PublicationIndexCodec.Decode(bytes, slot);
        Assert.Equal(index.Entries, read.Entries);
        Assert.Equal(bytes, PublicationIndexCodec.Encode(slot, read));
    }

    [Fact]
    public void TheLargestIndex_Is22840Bytes_AndTheEmptyOne56()
    {
        Assert.Equal(22_840, PublicationIndexCodec.MaxBytes);
        Assert.Equal(56, PublicationIndexCodec.Encode(slot, PublicationIndex.Empty).Length);

        var full = new PublicationIndex(Enumerable.Range(0, PublicationIndex.MaxEntries).Select(_ => Pending(Guid.NewGuid())));
        var bytes = PublicationIndexCodec.Encode(slot, full);
        Assert.Equal(PublicationIndexCodec.MaxBytes, bytes.Length);
        Assert.Equal(PublicationIndex.MaxEntries, PublicationIndexCodec.Decode(bytes, slot).Entries.Count);
        Assert.Throws<ArgumentException>(() => full.With(Pending(Guid.NewGuid())));
    }

    [Fact]
    public void AnIndexANewerAetherFrameWrote_IsToldApart_BeforeItsChecksumIsRead()
    {
        foreach (var version in new ushort[] { 2, 0x8001, 0xFFFF })
        {
            var bytes = PublicationIndexCodec.Encode(slot, new PublicationIndex([Pending(Guid.NewGuid())]));
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), version);
            Assert.Equal(PublicationFileProblem.NewerVersion, Assert.Throws<PublicationFileException>(() => PublicationIndexCodec.Decode(bytes, slot)).Problem);
        }
    }

    [Fact]
    public void EveryDamage_IsRefused_AndNothingIsRepaired()
    {
        var plate = Guid.NewGuid();
        var good = PublicationIndexCodec.Encode(slot, new PublicationIndex([Pending(plate), Pending(Guid.NewGuid())]));

        var cases = new Dictionary<string, byte[]>
        {
            ["empty"] = [],
            ["five bytes"] = good[..5],
            ["another magic"] = With(good, b => b[3] = (byte)'R', reseal: false),
            ["version 0"] = With(good, b => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), 0), reseal: false),
            ["only a header"] = good[..Header],
            ["a flipped bit"] = With(good, b => b[Header + 20] ^= 1, reseal: false),
            ["a checksum of other bytes"] = With(good, b => b[^1] ^= 1, reseal: false),
            ["another slot"] = PublicationIndexCodec.Encode(PersonaSlotId.NewId(), new PublicationIndex([Pending(plate)])),
            ["a count past the entries"] = With(good, b => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), 3)),
            ["a count short of the entries"] = With(good, b => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), 1)),
            ["a count over 256"] = With(good, b => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), 257)),
            ["a byte after the entries"] = Reseal([.. good[..^Checksum], 0, .. new byte[Checksum]]),
            ["no Plate id"] = With(good, b => b.AsSpan(Header, 16).Clear()),
            ["no profile id"] = With(good, b => b.AsSpan(Header + 16, 16).Clear()),
            ["no revision id"] = With(good, b => b.AsSpan(Header + 32, 16).Clear()),
            ["state 0"] = With(good, b => b[Header + 48] = 0),
            ["state 4"] = With(good, b => b[Header + 48] = 4),
            ["a pending entry published before"] = With(good, b => BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(Header + 49), 5)),
            ["a pending entry with no outbox entry"] = With(good, b => b.AsSpan(Header + 57, 16).Clear()),
            ["a published entry never published"] = With(good, b => b[Header + 48] = (byte)PublicationState.Published),
            ["a retracting entry naming an outbox entry"] = With(good, b => b[Header + 48] = (byte)PublicationState.Retracting),
            ["a time before 1970"] = With(good, b => { b[Header + 48] = (byte)PublicationState.Retracting; BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(Header + 49), -1); }),
            ["a time past the protocol's"] = With(good, b => { b[Header + 48] = (byte)PublicationState.Published; BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(Header + 49), ProtocolLimits.MaxUnixSeconds + 1); }),
            ["a share code"] = With(good, b => b[Header + 73] = (byte)'A'),
            ["a profile twice"] = With(good, b => b.AsSpan(Header + 16, 16).CopyTo(b.AsSpan(Header + Entry + 16))),
            ["an outbox entry twice"] = With(good, b => b.AsSpan(Header + 57, 16).CopyTo(b.AsSpan(Header + Entry + 57))),
            ["two live entries for a Plate"] = With(good, b => b.AsSpan(Header, 16).CopyTo(b.AsSpan(Header + Entry))),
            ["past the largest index"] = new byte[PublicationIndexCodec.MaxBytes + 1],
        };

        foreach (var (name, bytes) in cases)
        {
            var refused = Assert.Throws<PublicationFileException>(() => PublicationIndexCodec.Decode(bytes, slot));
            Assert.True(refused.Problem == PublicationFileProblem.Damaged, name);
        }
    }

    [Fact]
    public void APlate_HasOneLiveEntryAtMost_AndAnyNumberRetracting()
    {
        var plate = Guid.NewGuid();
        var retracting = new[]
        {
            new PublicationEntry(plate, ProfileId.NewId(), RevisionId.NewId(), PublicationState.Retracting, 1_790_000_000, OutboxEntryName.None),
            new PublicationEntry(plate, ProfileId.NewId(), RevisionId.NewId(), PublicationState.Retracting, 0, OutboxEntryName.None),
        };
        var live = Pending(plate);
        var index = new PublicationIndex([.. retracting, live]);

        Assert.Same(live, index.LiveFor(plate));
        Assert.Null(index.LiveFor(Guid.NewGuid()));
        Assert.Equal(index.Entries, PublicationIndexCodec.Decode(PublicationIndexCodec.Encode(slot, index), slot).Entries);
        Assert.Throws<ArgumentException>(() => index.With(Pending(plate)));
    }

    [Fact]
    public void With_ReplacesTheEntryForItsProfile_OrAddsItLast()
    {
        var first = Pending(Guid.NewGuid());
        var second = Pending(Guid.NewGuid());
        var index = new PublicationIndex([first, second]);

        var updated = first with { LatestRevision = RevisionId.NewId(), PendingEntry = OutboxEntryName.NewName() };
        Assert.Equal(new[] { updated, second }, index.With(updated).Entries);

        var third = Pending(Guid.NewGuid());
        Assert.Equal(new[] { first, second, third }, index.With(third).Entries);
        Assert.Equal(new[] { first, second }, index.Entries);
    }

    [Fact]
    public void AnIndexThatBreaksARule_IsNeverMade()
    {
        var plate = Guid.NewGuid();
        var entry = Pending(plate);
        PublicationEntry[][] broken =
        [
            [entry, entry],
            [entry, Pending(plate)],
            [entry with { PlateId = Guid.Empty }],
            [entry with { ProfileId = default }],
            [entry with { LatestRevision = default }],
            [entry with { State = (PublicationState)9 }],
            [entry with { PendingEntry = OutboxEntryName.None }],
            [entry with { LastPublishedAt = 3 }],
            [entry with { State = PublicationState.Published }],
            [entry with { State = PublicationState.Retracting }],
            [entry with { State = PublicationState.Retracting, PendingEntry = OutboxEntryName.None, LastPublishedAt = -1 }],
        ];

        foreach (var entries in broken)
        {
            Assert.NotNull(PublicationIndex.Problem(entries));
            Assert.Throws<ArgumentException>(() => new PublicationIndex(entries));
        }

        Assert.Throws<ArgumentException>(() => PublicationIndexCodec.Encode(default, PublicationIndex.Empty));
    }

    [Fact]
    public void AnOutboxEntrysName_IsRandom_AndReadOnlyInTheFormItIsWritten()
    {
        var name = OutboxEntryName.NewName();
        Assert.False(name.IsNone);
        Assert.NotEqual(name, OutboxEntryName.NewName());
        Assert.Matches("^[0-9a-f]{32}[.]afpo$", name.FileName);
        Assert.True(OutboxEntryName.TryParseFileName(name.FileName, out var parsed));
        Assert.Equal(name, parsed);

        foreach (var wrong in new[]
        {
            name.FileName.ToUpperInvariant(),
            name.FileName[1..],
            "0" + name.FileName,
            name.FileName + ".tmp",
            name.FileName[..32] + ".afpi",
            new string('0', 32) + ".afpo",
            "g" + name.FileName[1..],
            string.Empty,
        })
        {
            Assert.False(OutboxEntryName.TryParseFileName(wrong, out _), wrong);
        }
    }

    private static PublicationEntry Pending(Guid plate) =>
        new(plate, ProfileId.NewId(), RevisionId.NewId(), PublicationState.Pending, 0, OutboxEntryName.NewName());

    private static byte[] With(byte[] good, Action<byte[]> change, bool reseal = true)
    {
        var bytes = (byte[])good.Clone();
        change(bytes);
        return reseal ? Reseal(bytes) : bytes;
    }

    /// <summary>The bytes with their checksum made right again, so the refusal is the rule's and not the checksum's.</summary>
    private static byte[] Reseal(byte[] bytes)
    {
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - Checksum), bytes.AsSpan(bytes.Length - Checksum));
        return bytes;
    }
}
