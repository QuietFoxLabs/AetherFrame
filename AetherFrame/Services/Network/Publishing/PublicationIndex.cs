using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>Where one of a persona's profiles stands on this installation (docs/networking/DecisionRegister.md, P1, N2 and D1).</summary>
internal enum PublicationState : byte
{
    /// <summary>Signed and kept in the outbox; the server has acknowledged no revision of it yet.</summary>
    Pending = 1,

    /// <summary>The server has acknowledged a revision of it; a newer one may wait in the outbox.</summary>
    Published = 2,

    /// <summary>Unpublished here, and the retraction isn't acknowledged yet. Its profile id is never used again (D1).</summary>
    Retracting = 3,
}

/// <summary>
/// An outbox entry's file name: 16 random bytes, as 32 lowercase hex digits and <c>.afpo</c>. It is
/// drawn afresh for every entry, so it says nothing about the Plate, the profile or the persona.
/// All zero means none.
/// </summary>
internal readonly struct OutboxEntryName : IEquatable<OutboxEntryName>
{
    /// <summary>The name's length in bytes.</summary>
    internal const int ByteLength = 16;

    /// <summary>An outbox entry's file extension.</summary>
    internal const string Extension = ".afpo";

    private readonly ulong high;
    private readonly ulong low;

    private OutboxEntryName(ulong high, ulong low)
    {
        this.high = high;
        this.low = low;
    }

    /// <summary>No entry.</summary>
    internal static OutboxEntryName None => default;

    /// <summary>Whether this names no entry.</summary>
    internal bool IsNone => (high | low) == 0;

    /// <summary>The file name: 32 lowercase hex digits, then <see cref="Extension"/>.</summary>
    internal string FileName
    {
        get
        {
            Span<byte> bytes = stackalloc byte[ByteLength];
            WriteBytes(bytes);
            return Convert.ToHexStringLower(bytes) + Extension;
        }
    }

    /// <summary>A fresh name from the operating system's random number generator; never none.</summary>
    internal static OutboxEntryName NewName()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        OutboxEntryName name;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            name = FromBytes(bytes);
        }
        while (name.IsNone);

        return name;
    }

    /// <summary>The name held in 16 bytes; all zero reads as none.</summary>
    internal static OutboxEntryName FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
        {
            throw new ArgumentException("An outbox entry's name is 16 bytes.", nameof(bytes));
        }

        return new OutboxEntryName(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

    /// <summary>
    /// The name a file in an outbox directory carries, when its name is exactly one this build
    /// writes: 32 lowercase hex digits, not all zero, then <see cref="Extension"/>.
    /// </summary>
    internal static bool TryParseFileName(string fileName, out OutboxEntryName name)
    {
        name = None;
        if (fileName is null || fileName.Length != (ByteLength * 2) + Extension.Length || !fileName.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[ByteLength];
        for (var index = 0; index < ByteLength; index++)
        {
            var upper = HexValue(fileName[index * 2]);
            var lower = HexValue(fileName[(index * 2) + 1]);
            if (upper < 0 || lower < 0)
            {
                return false;
            }

            bytes[index] = (byte)((upper << 4) | lower);
        }

        name = FromBytes(bytes);
        return !name.IsNone;
    }

    /// <summary>Writes the name's 16 bytes.</summary>
    internal void WriteBytes(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, high);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], low);
    }

    public bool Equals(OutboxEntryName other) => high == other.high && low == other.low;

    public override bool Equals(object? obj) => obj is OutboxEntryName other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(high, low);

    public static bool operator ==(OutboxEntryName left, OutboxEntryName right) => left.Equals(right);

    public static bool operator !=(OutboxEntryName left, OutboxEntryName right) => !left.Equals(right);

    /// <summary>Only lowercase hex digits: the name is written in no other form.</summary>
    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };
}

/// <summary>
/// One profile a persona published, or is publishing, as its publication index records it (P1):
/// the local Plate it was made from, its profile id, the latest revision signed for it, where it
/// stands, when the server last acknowledged a revision of it (Unix seconds; 0 for never), and the
/// outbox entry holding a signed revision not yet acknowledged (<see cref="OutboxEntryName.None"/>
/// for none). The share code the server returns is added by N2-9.
/// </summary>
internal sealed record PublicationEntry(
    Guid PlateId,
    ProfileId ProfileId,
    RevisionId LatestRevision,
    PublicationState State,
    long LastPublishedAt,
    OutboxEntryName PendingEntry)
{
    /// <summary>Whether the entry may still be updated: pending or published, never retracting (D1).</summary>
    internal bool IsLive => State is PublicationState.Pending or PublicationState.Published;
}

/// <summary>
/// A persona's publication index (decisions P1 and P4): what it published, or is publishing, from
/// this installation. Immutable; a change makes a new index, which is saved before it is used
/// (persist, then apply, as P3 does for the registry). Every index holds at most
/// <see cref="MaxEntries"/> entries, and follows the rules <see cref="Problem"/> checks, so an
/// index that exists is one this build would save and read back.
/// </summary>
internal sealed class PublicationIndex
{
    /// <summary>The most entries an index holds.</summary>
    internal const int MaxEntries = 256;

    private readonly PublicationEntry[] entries;

    /// <summary>An index of <paramref name="entries"/>, in order.</summary>
    /// <exception cref="ArgumentException">They break a rule of <see cref="Problem"/>.</exception>
    internal PublicationIndex(IEnumerable<PublicationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = new List<PublicationEntry>(entries);
        if (Problem(list) is { } problem)
        {
            throw new ArgumentException(problem, nameof(entries));
        }

        this.entries = list.ToArray();
    }

    /// <summary>An index with nothing in it: a persona that has published nothing from here.</summary>
    internal static PublicationIndex Empty { get; } = new(Array.Empty<PublicationEntry>());

    /// <summary>The entries, in the order they were added.</summary>
    internal IReadOnlyList<PublicationEntry> Entries => entries;

    /// <summary>The Plate's live entry (pending or published), or null: a Plate has at most one per persona.</summary>
    internal PublicationEntry? LiveFor(Guid plateId)
    {
        foreach (var entry in entries)
        {
            if (entry.PlateId == plateId && entry.IsLive)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The index with <paramref name="entry"/> in place of the entry for its profile, or added after the others.</summary>
    /// <exception cref="ArgumentException">The result would break a rule of <see cref="Problem"/>.</exception>
    internal PublicationIndex With(PublicationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var list = new List<PublicationEntry>(entries.Length + 1);
        var replaced = false;
        foreach (var existing in entries)
        {
            if (existing.ProfileId == entry.ProfileId)
            {
                list.Add(entry);
                replaced = true;
            }
            else
            {
                list.Add(existing);
            }
        }

        if (!replaced)
        {
            list.Add(entry);
        }

        return new PublicationIndex(list);
    }

    /// <summary>
    /// Why <paramref name="entries"/> can't be an index, or null when they can:
    /// <list type="bullet">
    /// <item>at most <see cref="MaxEntries"/>;</item>
    /// <item>each with a Plate id, a known state, and a last publish time from 0 to the protocol's
    /// latest; a pending entry names its outbox entry and has never been published; a published
    /// entry has been; a retracting entry names no outbox entry, since unpublishing drops the
    /// profile's waiting revision (D1), and the outbox holds nothing else for it;</item>
    /// <item>no profile id twice, no outbox entry named twice, and at most one live entry for a
    /// Plate. A Plate may also have any number of retracting ones: unpublishing and publishing
    /// again, before the retraction is acknowledged, gives it a new profile.</item>
    /// </list>
    /// </summary>
    internal static string? Problem(IReadOnlyList<PublicationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaxEntries)
        {
            return $"An index holds at most {MaxEntries} entries.";
        }

        var profiles = new HashSet<ProfileId>();
        var pending = new HashSet<OutboxEntryName>();
        var livePlates = new HashSet<Guid>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null)
            {
                return "An index holds no null entry.";
            }

            if (EntryProblem(entry) is { } problem)
            {
                return problem;
            }

            if (!profiles.Add(entry.ProfileId))
            {
                return "An index names a profile once.";
            }

            if (!entry.PendingEntry.IsNone && !pending.Add(entry.PendingEntry))
            {
                return "An index names an outbox entry once.";
            }

            if (entry.IsLive && !livePlates.Add(entry.PlateId))
            {
                return "A Plate has at most one live entry for a persona.";
            }
        }

        return null;
    }

    private static string? EntryProblem(PublicationEntry entry)
    {
        if (entry.PlateId == Guid.Empty)
        {
            return "An entry names its Plate.";
        }

        if (entry.ProfileId.IsEmpty || entry.LatestRevision.IsEmpty)
        {
            return "An entry names its profile and its latest revision.";
        }

        if (entry.LastPublishedAt < 0 || entry.LastPublishedAt > ProtocolLimits.MaxUnixSeconds)
        {
            return "An entry's last publish time is outside what the protocol can say.";
        }

        return entry.State switch
        {
            PublicationState.Pending when entry.PendingEntry.IsNone => "A pending entry names its outbox entry.",
            PublicationState.Pending when entry.LastPublishedAt != 0 => "A pending entry has never been published.",
            PublicationState.Pending => null,
            PublicationState.Published when entry.LastPublishedAt == 0 => "A published entry has been published.",
            PublicationState.Published => null,
            PublicationState.Retracting when !entry.PendingEntry.IsNone => "A retracting entry names no outbox entry: unpublishing drops its waiting revision (D1).",
            PublicationState.Retracting => null,
            _ => "An entry's state is pending, published or retracting.",
        };
    }
}
