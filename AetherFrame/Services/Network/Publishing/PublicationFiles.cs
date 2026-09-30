using System;
using System.Collections.Generic;
using System.IO;
using AetherFrame.Personas;
using AetherFrame.Services.Network.Personas;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>What a persona's outbox folder holds, by kind: entries by name, the temporary files an interrupted write left, and anything else.</summary>
internal sealed record OutboxListing(IReadOnlyList<OutboxEntryName> Entries, IReadOnlyList<string> Temporaries, int Others)
{
    internal static readonly OutboxListing Empty = new(Array.Empty<OutboxEntryName>(), Array.Empty<string>(), 0);
}

/// <summary>
/// The publication files of this installation's personas (decisions P1 and P4, and N2-6's design,
/// sections 2 and 9), in the persona folder, beside the registry and never in <c>keys\</c>:
/// <list type="bullet">
/// <item><c>slot_...afpub</c>: a persona's publication index, named by its slot, never by its
/// identity;</item>
/// <item><c>outbox\slot_...\</c>: its outbox, each entry a file of its own under a fresh random
/// name (<see cref="OutboxEntryName"/>).</item>
/// </list>
/// Compiled only in the networking preview flavour, like everything under Services/Network.
/// <para>
/// Every write goes to a temporary file beside its target (the target's name and <c>.tmp</c>),
/// which is flushed to disk, read back and compared, and only then moved into place, written
/// through (<see cref="WrittenThroughMove"/>): an index replaces the old one, and an entry is moved
/// to a name that must not exist yet. So a file holds its old bytes or its new ones, never a mix,
/// and an interrupted write leaves only a temporary file, which is never read. Reads are bounded:
/// one byte past the largest file of its kind is refused, never read whole. Nothing here decides
/// what the bytes mean; the codecs and the commit do.
/// </para>
/// <para>
/// Every use runs under the persona files' lock (P3), as one persona-session operation, one at a
/// time: a load run while a commit is between its entry and its index would take the new entry
/// for one the index doesn't name, and delete it.
/// </para>
/// </summary>
internal sealed class PublicationFiles
{
    /// <summary>A publication index's file extension.</summary>
    internal const string IndexExtension = ".afpub";

    /// <summary>The outboxes' folder, in the persona folder.</summary>
    internal const string OutboxFolder = "outbox";

    /// <summary>A temporary file's suffix, after its target's name.</summary>
    internal const string TemporarySuffix = ".tmp";

    private readonly string directory;

    /// <summary>The files in <paramref name="personasDirectory"/>, the persona folder, which is created on the first write.</summary>
    internal PublicationFiles(string personasDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personasDirectory);
        directory = Path.GetFullPath(personasDirectory);
    }

    /// <summary>The persona folder.</summary>
    internal string Directory => directory;

    /// <summary>Where <paramref name="slot"/>'s index is kept.</summary>
    internal string IndexPath(PersonaSlotId slot) => Path.Combine(directory, SlotName(slot) + IndexExtension);

    /// <summary>Where <paramref name="slot"/>'s outbox is kept.</summary>
    internal string OutboxPath(PersonaSlotId slot) => Path.Combine(directory, OutboxFolder, SlotName(slot));

    /// <summary>
    /// The index's bytes, or null when there is none: the file or its folder doesn't exist, a persona
    /// that has published nothing from here. Anything else that stops the read throws, including a
    /// file larger than the largest index: that is never read as "no index".
    /// </summary>
    internal byte[]? ReadIndex(PersonaSlotId slot) => ReadBounded(IndexPath(slot), PublicationIndexCodec.MaxBytes, "publication index");

    /// <summary>Puts <paramref name="bytes"/> in place of <paramref name="slot"/>'s index, durably and whole: <see cref="StageIndex"/>, then <see cref="MoveStagedIndex"/>.</summary>
    /// <exception cref="IOException">The save failed, as either step says.</exception>
    internal void ReplaceIndex(PersonaSlotId slot, ReadOnlySpan<byte> bytes)
    {
        StageIndex(slot, bytes);
        MoveStagedIndex(slot);
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to the index's temporary file, flushed to disk and read back:
    /// the first step of a save. The index itself isn't touched.
    /// </summary>
    /// <exception cref="IOException">The temporary file couldn't be written; it is deleted, and the index is certainly as it was.</exception>
    internal void StageIndex(PersonaSlotId slot, ReadOnlySpan<byte> bytes)
    {
        var temporary = IndexPath(slot) + TemporarySuffix;
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            WriteChecked(temporary, bytes, "publication index");
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Moves the staged temporary file over the index, written through: the second step of a save, and a commit's commit point.</summary>
    /// <exception cref="IOException">
    /// The move failed. Normally the old index is left as it was; a failure reported after the move
    /// took effect leaves the new one (P3's indeterminate save), which a later load reads.
    /// </exception>
    internal void MoveStagedIndex(PersonaSlotId slot)
    {
        var final = IndexPath(slot);
        var temporary = final + TemporarySuffix;
        try
        {
            WrittenThroughMove.Replace(temporary, final);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Writes a new outbox entry for <paramref name="slot"/>, durably and whole, under <paramref name="name"/>, which must not exist yet.</summary>
    /// <exception cref="IOException">The write failed; no entry of that name was made, or one already existed and was left as it was.</exception>
    internal void WriteEntry(PersonaSlotId slot, OutboxEntryName name, ReadOnlySpan<byte> bytes)
    {
        if (name.IsNone)
        {
            throw new ArgumentException("An entry is written under a name.", nameof(name));
        }

        var outbox = OutboxPath(slot);
        var final = Path.Combine(outbox, name.FileName);
        var temporary = final + TemporarySuffix;
        System.IO.Directory.CreateDirectory(outbox);
        try
        {
            WriteChecked(temporary, bytes, "outbox entry");
            WrittenThroughMove.MoveNew(temporary, final);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>An outbox entry's bytes, or null when it doesn't exist. Anything else that stops the read throws, including a file larger than the largest entry.</summary>
    internal byte[]? ReadEntry(PersonaSlotId slot, OutboxEntryName name) =>
        name.IsNone ? null : ReadBounded(Path.Combine(OutboxPath(slot), name.FileName), OutboxEntryCodec.MaxBytes, "outbox entry");

    /// <summary>An outbox entry's size in bytes, or 0 when it doesn't exist.</summary>
    internal long EntrySize(PersonaSlotId slot, OutboxEntryName name)
    {
        if (name.IsNone)
        {
            return 0;
        }

        var file = new FileInfo(Path.Combine(OutboxPath(slot), name.FileName));
        return file.Exists ? file.Length : 0;
    }

    /// <summary>Deletes an outbox entry; true when it is gone, or was never there. Never throws for the file itself.</summary>
    internal bool TryDeleteEntry(PersonaSlotId slot, OutboxEntryName name) =>
        !name.IsNone && TryDelete(Path.Combine(OutboxPath(slot), name.FileName));

    /// <summary>
    /// Deletes a temporary file an interrupted write of an entry left in <paramref name="slot"/>'s
    /// outbox, named as this build names one (<see cref="IsTemporary"/>); true when it is gone.
    /// </summary>
    internal bool TryDeleteTemporary(PersonaSlotId slot, string fileName) =>
        IsTemporary(fileName) && TryDelete(Path.Combine(OutboxPath(slot), fileName));

    /// <summary>
    /// What <paramref name="slot"/>'s outbox folder holds; empty when it doesn't exist. A folder that
    /// can't be listed throws, and is never taken for an empty one.
    /// </summary>
    internal OutboxListing ListOutbox(PersonaSlotId slot)
    {
        var outbox = OutboxPath(slot);
        if (!System.IO.Directory.Exists(outbox))
        {
            return OutboxListing.Empty;
        }

        var entries = new List<OutboxEntryName>();
        var temporaries = new List<string>();
        var others = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(outbox))
        {
            var fileName = Path.GetFileName(path);
            if (OutboxEntryName.TryParseFileName(fileName, out var name))
            {
                entries.Add(name);
            }
            else if (IsTemporary(fileName))
            {
                temporaries.Add(fileName);
            }
            else
            {
                others++;
            }
        }

        return new OutboxListing(entries, temporaries, others);
    }

    /// <summary>Whether <paramref name="fileName"/> is an entry's temporary file, exactly as this build names one: an entry's name and <see cref="TemporarySuffix"/>.</summary>
    internal static bool IsTemporary(string? fileName) =>
        fileName is not null && fileName.EndsWith(TemporarySuffix, StringComparison.Ordinal)
        && OutboxEntryName.TryParseFileName(fileName[..^TemporarySuffix.Length], out _);

    /// <summary>The slot's text form: <c>slot_</c> and 32 hex digits, never the persona's identity.</summary>
    private static string SlotName(PersonaSlotId slot)
    {
        if (slot.IsEmpty)
        {
            throw new ArgumentException("Publication files belong to a persona's slot.", nameof(slot));
        }

        return slot.ToString();
    }

    /// <summary>
    /// The file's bytes, or null when it or its folder doesn't exist. It reads at most one byte past
    /// <paramref name="maxBytes"/>, and throws when that byte is there.
    /// </summary>
    private static byte[]? ReadBounded(string path, long maxBytes, string what)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        using (stream)
        {
            var length = stream.Length;
            if (length > maxBytes)
            {
                throw new IOException($"The {what} is larger than any this build reads.");
            }

            var buffer = new byte[length];
            stream.ReadExactly(buffer);
            if (stream.ReadByte() != -1)
            {
                throw new IOException($"The {what} grew while it was read.");
            }

            return buffer;
        }
    }

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="temporary"/>, replacing one an interrupted write left, flushes it to disk, and reads it back.</summary>
    private static void WriteChecked(string temporary, ReadOnlySpan<byte> bytes, string what)
    {
        using var stream = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
        var check = new byte[bytes.Length];
        stream.ReadExactly(check);
        if (stream.Length != bytes.Length || !check.AsSpan().SequenceEqual(bytes))
        {
            throw new IOException($"The {what}'s temporary file did not read back as written.");
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
