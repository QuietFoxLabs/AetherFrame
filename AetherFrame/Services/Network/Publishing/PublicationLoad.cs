using System;
using System.Collections.Generic;
using System.IO;
using AetherFrame.Personas;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>Whether a revision waits in the outbox for an entry of the index.</summary>
internal enum OutboxState
{
    /// <summary>The entry names no outbox entry: nothing waits to be sent for it.</summary>
    Nothing,

    /// <summary>The outbox entry the index names is stored, and verified as the persona's: it waits to be sent.</summary>
    Waiting,

    /// <summary>
    /// The index names an outbox entry that is missing, unreadable or refused. The revision reads as
    /// "not stored; share again", never as pending, and is never sent or repaired.
    /// </summary>
    NotStored,
}

/// <summary>One entry of the index, and whether its revision waits in the outbox.</summary>
internal sealed record LoadedPublication(PublicationEntry Entry, OutboxState Outbox);

/// <summary>What loading a persona's publications found.</summary>
internal enum PublicationLoadResult
{
    /// <summary>The index was read (or there was none, which is an empty one), and every entry it names was checked.</summary>
    Loaded,

    /// <summary>The index exists but can't be read, or is damaged: publishing and unpublishing are off for the persona, and nothing was changed or deleted.</summary>
    IndexUnreadable,

    /// <summary>The index was written by a newer AetherFrame: as for <see cref="IndexUnreadable"/>, but the player is told to update.</summary>
    IndexNewerVersion,
}

/// <summary>A persona's publications as loaded: the index, each entry's outbox state, and what was cleared from its outbox.</summary>
internal sealed class LoadedPublications
{
    internal LoadedPublications(PublicationLoadResult result, PublicationIndex? index, IReadOnlyList<LoadedPublication> entries, int removed, string? failure)
    {
        Result = result;
        Index = index;
        Entries = entries;
        Removed = removed;
        Failure = failure;
    }

    /// <summary>Whether the index could be used.</summary>
    internal PublicationLoadResult Result { get; }

    /// <summary>The index, for <see cref="PublicationLoadResult.Loaded"/>.</summary>
    internal PublicationIndex? Index { get; }

    /// <summary>Each entry of the index, in its order, with its outbox state.</summary>
    internal IReadOnlyList<LoadedPublication> Entries { get; }

    /// <summary>How many files the index doesn't name were deleted from the outbox: entries a failed commit left, and temporary files.</summary>
    internal int Removed { get; }

    /// <summary>What failed, for the log, when the index couldn't be used: exception types and HResults only.</summary>
    internal string? Failure { get; }
}

/// <summary>
/// Loading a persona's publications (N2-6's design, sections 2 and 9): the index decides.
/// <list type="bullet">
/// <item>An index that can't be read, is damaged or is newer is refused. Nothing is deleted or
/// written, and it is never taken for an empty one: publishing and unpublishing stay off for the
/// persona until it is readable again. No index at all is a persona that has published nothing
/// from here.</item>
/// <item>Each outbox entry the index names is read and checked in full: its document verifies as
/// the persona's and is the revision the index names, and each image is exactly what the document
/// declares and holds nothing a prepared copy doesn't. An entry that fails reads as not stored,
/// and is never sent, repaired or deleted here: sharing again supersedes it.</item>
/// <item>Only once an index was read, every entry file it doesn't name, and every temporary file
/// an interrupted write left, is deleted: the saved index was written without them, so none is
/// ever sent. Without an index nothing is deleted, and files that aren't the outbox's are never
/// touched.</item>
/// </list>
/// No index file is ever deleted here, even one whose slot no persona holds any more: restoring
/// that persona's key as an orphan (L12) reunites them. A load runs as one persona-session
/// operation, under the persona files' lock, and never beside a commit: between a commit's entry
/// and its index, the new entry is one the saved index doesn't name yet.
/// </summary>
internal static class PublicationLoad
{
    /// <summary>Loads <paramref name="slot"/>'s publications from <paramref name="files"/>, checking its outbox against <paramref name="key"/>, the persona's public key.</summary>
    internal static LoadedPublications Load(PublicationFiles files, PersonaSlotId slot, PersonaPublicKey key)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(key);

        PublicationIndex index;
        bool saved;
        try
        {
            var bytes = files.ReadIndex(slot);
            saved = bytes is not null;
            index = bytes is null ? PublicationIndex.Empty : PublicationIndexCodec.Decode(bytes, slot);
        }
        catch (PublicationFileException e)
        {
            var result = e.Problem == PublicationFileProblem.NewerVersion ? PublicationLoadResult.IndexNewerVersion : PublicationLoadResult.IndexUnreadable;
            return new LoadedPublications(result, null, Array.Empty<LoadedPublication>(), 0, PublishOutcome.Describe(e));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LoadedPublications(PublicationLoadResult.IndexUnreadable, null, Array.Empty<LoadedPublication>(), 0, PublishOutcome.Describe(e));
        }

        var entries = new List<LoadedPublication>(index.Entries.Count);
        var named = new HashSet<OutboxEntryName>();
        foreach (var entry in index.Entries)
        {
            if (entry.PendingEntry.IsNone)
            {
                entries.Add(new LoadedPublication(entry, OutboxState.Nothing));
                continue;
            }

            named.Add(entry.PendingEntry);
            entries.Add(new LoadedPublication(entry, IsStored(files, slot, key, entry) ? OutboxState.Waiting : OutboxState.NotStored));
        }

        return new LoadedPublications(PublicationLoadResult.Loaded, index, entries, saved ? RemoveUnnamed(files, slot, named) : 0, null);
    }

    /// <summary>Whether the outbox entry <paramref name="entry"/> names is stored whole, as the persona's, and is the revision the index names.</summary>
    private static bool IsStored(PublicationFiles files, PersonaSlotId slot, PersonaPublicKey key, PublicationEntry entry)
    {
        try
        {
            if (files.ReadEntry(slot, entry.PendingEntry) is not { } bytes)
            {
                return false;
            }

            var stored = OutboxEntryCodec.Decode(bytes, key);
            return stored.Snapshot.ProfileId == entry.ProfileId && stored.Snapshot.RevisionId == entry.LatestRevision;
        }
        catch (Exception e) when (e is PublicationFileException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes every entry file the index doesn't name, and every temporary file, from the outbox; how many went. A listing that fails deletes nothing.</summary>
    private static int RemoveUnnamed(PublicationFiles files, PersonaSlotId slot, HashSet<OutboxEntryName> named)
    {
        OutboxListing listing;
        try
        {
            listing = files.ListOutbox(slot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var removed = 0;
        foreach (var name in listing.Entries)
        {
            if (!named.Contains(name) && files.TryDeleteEntry(slot, name))
            {
                removed++;
            }
        }

        foreach (var temporary in listing.Temporaries)
        {
            if (files.TryDeleteTemporary(slot, temporary))
            {
                removed++;
            }
        }

        return removed;
    }
}
