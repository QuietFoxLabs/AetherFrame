using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using AetherFrame.Personas;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>
/// What the consent screen showed, and the player approved: the candidate, and the persona it named
/// (decision L10, and N2-6's design, section 1). The commit signs exactly this candidate, as exactly
/// this persona, and reads nothing from the Plate again.
/// </summary>
internal sealed class PublishConsent
{
    internal PublishConsent(SnapshotCandidate candidate, PersonaSlotId shownSlot, PersonaPublicKey shownKey)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(shownKey);
        if (shownSlot.IsEmpty)
        {
            throw new ArgumentException("The consent screen names a persona's slot.", nameof(shownSlot));
        }

        Candidate = candidate;
        ShownSlot = shownSlot;
        ShownKey = shownKey;
    }

    /// <summary>What is shared: everything but the profile id, the revision id and the time.</summary>
    internal SnapshotCandidate Candidate { get; }

    /// <summary>The slot of the persona the consent screen named.</summary>
    internal PersonaSlotId ShownSlot { get; }

    /// <summary>The public key of the persona the consent screen named.</summary>
    internal PersonaPublicKey ShownKey { get; }
}

/// <summary>What a commit came to.</summary>
internal enum PublishResult
{
    /// <summary>Signed, checked and kept in the outbox, and the saved index names it. Nothing is sent yet.</summary>
    Stored,

    /// <summary>The persona's publication index exists but can't be read, or is damaged; it is left as it was, and publishing is off for the persona.</summary>
    IndexUnreadable,

    /// <summary>The persona's publication index was written by a newer AetherFrame; it is left as it was.</summary>
    IndexNewerVersion,

    /// <summary>The persona's index holds as many profiles as it can (256), and this Plate has none of them.</summary>
    IndexFull,

    /// <summary>The persona the consent screen named isn't the active one any more, or was switched away from while signing (L10). Nothing is signed; the player retries.</summary>
    ActivePersonaChanged,

    /// <summary>The persona's key can't be opened now.</summary>
    KeyUnavailable,

    /// <summary>The player hasn't acknowledged what losing this persona's key means (K4).</summary>
    NotAcknowledged,

    /// <summary>Signing failed; nothing was stored.</summary>
    SigningFailed,

    /// <summary>The signed bytes don't verify as the persona shown, or don't say what the consent screen showed; nothing was stored.</summary>
    NotAsShown,

    /// <summary>The persona's outbox would hold more than 128 MiB of signed revisions waiting to be sent.</summary>
    OutboxFull,

    /// <summary>
    /// The outbox entry or the index couldn't be saved. The index normally still names what it
    /// named before; when <see cref="PublishOutcome.Indeterminate"/> is set, the save failed after
    /// the new index may have reached the disk, and the next load shows which.
    /// </summary>
    NotSaved,
}

/// <summary>What a commit came to, and for a stored revision, the index to apply in memory now that it is saved.</summary>
internal sealed class PublishOutcome
{
    private PublishOutcome(PublishResult result, PublicationIndex? index, ProfileId profile, RevisionId revision, string? failure, bool indeterminate)
    {
        Result = result;
        Index = index;
        Profile = profile;
        Revision = revision;
        Failure = failure;
        Indeterminate = indeterminate;
    }

    /// <summary>What happened.</summary>
    internal PublishResult Result { get; }

    /// <summary>The saved index, for <see cref="PublishResult.Stored"/>: the caller applies it in memory only now (persist, then apply).</summary>
    internal PublicationIndex? Index { get; }

    /// <summary>The profile the revision belongs to, for <see cref="PublishResult.Stored"/>.</summary>
    internal ProfileId Profile { get; }

    /// <summary>The revision signed and stored, for <see cref="PublishResult.Stored"/>.</summary>
    internal RevisionId Revision { get; }

    /// <summary>What failed, for the log: each exception type in the chain with its HResult, never its text, which can hold a path.</summary>
    internal string? Failure { get; }

    /// <summary>For <see cref="PublishResult.NotSaved"/>: whether the new index may have reached the disk (P3's indeterminate save).</summary>
    internal bool Indeterminate { get; }

    internal static PublishOutcome Stored(PublicationIndex index, ProfileId profile, RevisionId revision) => new(PublishResult.Stored, index, profile, revision, null, false);

    internal static PublishOutcome Refused(PublishResult result, Exception? failure = null, bool indeterminate = false) =>
        new(result, null, default, default, failure is null ? null : Describe(failure), indeterminate);

    /// <summary>Each exception type in the chain with its HResult, and any persona or protocol error; never an exception's text.</summary>
    internal static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current switch
            {
                PersonaException persona => $"{nameof(PersonaException)}({persona.Error}) 0x{current.HResult:X8}",
                ProtocolException protocol => $"{nameof(ProtocolException)}({protocol.Error}) 0x{current.HResult:X8}",
                PublicationFileException file => $"{nameof(PublicationFileException)}({file.Problem}) 0x{current.HResult:X8}",
                _ => $"{current.GetType().Name} 0x{current.HResult:X8}",
            });
        }

        return string.Join(" <- ", parts);
    }
}

/// <summary>
/// The commit: one signed revision of a Plate's snapshot into the persona's outbox, with its
/// publication index as the commit point (N2-6's design, section 2). It runs as one persona-session
/// operation, off the framework thread, and in this order:
/// <list type="number">
/// <item>read the persona's index;</item>
/// <item>reuse the Plate's live (pending or published) profile id, or draw a new one; a retracting
/// one is never reused (D1);</item>
/// <item>draw a revision id for this attempt, never reused even after a failure (N2), and take the
/// time from the UTC clock;</item>
/// <item>open a signer for the persona shown, and only while it is the active one (L10);</item>
/// <item>refuse unless that persona has acknowledged what losing its key means (K4);</item>
/// <item>sign;</item>
/// <item>dispose the signer, before any file is touched;</item>
/// <item>verify the signed bytes with the protocol library: the key must be the one shown, and the
/// snapshot they decode to must be the one signed, field for field;</item>
/// <item>write the outbox entry, checked as a load checks it, under a new random name;</item>
/// <item>save the index naming it, checked by decoding it again; the caller then applies it in
/// memory;</item>
/// <item>only then delete the entry it supersedes.</item>
/// </list>
/// No failure deletes an entry the index names. An entry the saved index doesn't name is never sent,
/// and a later load deletes it. Every refusal that can be decided before signing is.
/// </summary>
internal static class PublicationCommit
{
    /// <summary>The most a persona's outbox holds, in the entries its index names: 128 MiB.</summary>
    internal const long MaxOutboxBytes = 128L * 1024 * 1024;

    /// <summary>Commits <paramref name="consent"/>'s candidate for <paramref name="personas"/>' persona it names, in <paramref name="files"/>, at <paramref name="utcNow"/>'s time.</summary>
    internal static PublishOutcome Commit(PersonaManager personas, PublicationFiles files, PublishConsent consent, Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(personas);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(utcNow);
        var slot = consent.ShownSlot;
        var key = consent.ShownKey;
        var candidate = consent.Candidate;

        // 1. The persona's index, as saved.
        PublicationIndex index;
        try
        {
            index = files.ReadIndex(slot) is { } current ? PublicationIndexCodec.Decode(current, slot) : PublicationIndex.Empty;
        }
        catch (PublicationFileException e)
        {
            return PublishOutcome.Refused(e.Problem == PublicationFileProblem.NewerVersion ? PublishResult.IndexNewerVersion : PublishResult.IndexUnreadable, e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return PublishOutcome.Refused(PublishResult.IndexUnreadable, e);
        }

        // 2. The Plate's live profile, or a new one.
        var live = index.LiveFor(candidate.PlateId);
        if (live is null && index.Entries.Count >= PublicationIndex.MaxEntries)
        {
            return PublishOutcome.Refused(PublishResult.IndexFull);
        }

        var profile = live?.ProfileId ?? ProfileId.NewId();

        // 3. This attempt's revision, and the time.
        var revision = RevisionId.NewId();
        ProfileLayoutSnapshot snapshot;
        try
        {
            snapshot = candidate.ToSnapshot(profile, revision, utcNow().ToUnixTimeSeconds());
        }
        catch (ProtocolException e)
        {
            // The candidate was checked whole when it was built: only the ids or the clock are left.
            return PublishOutcome.Refused(PublishResult.SigningFailed, e);
        }

        var images = ImagesInOrder(candidate, snapshot);
        if (images is null)
        {
            return PublishOutcome.Refused(PublishResult.NotAsShown);
        }

        // 4 to 7. One lease, one signature, disposed before any file is touched.
        PersonaSignerAvailability availability;
        PersonaSignerLease? opened;
        try
        {
            availability = personas.TryOpenSigner(slot, key, out opened);
        }
        catch (PersonaException e)
        {
            // The store opened a key that isn't the persona's.
            return PublishOutcome.Refused(PublishResult.KeyUnavailable, e);
        }

        if (availability != PersonaSignerAvailability.Available || opened is null)
        {
            return PublishOutcome.Refused(availability == PersonaSignerAvailability.KeyUnavailable ? PublishResult.KeyUnavailable : PublishResult.ActivePersonaChanged);
        }

        var lease = opened;
        byte[] signed;
        try
        {
            if (!lease.Persona.Acknowledged)
            {
                return PublishOutcome.Refused(PublishResult.NotAcknowledged);
            }

            signed = SignedDocumentCodec.Sign(snapshot, lease.Signer);
        }
        catch (PersonaException e) when (e.Error == PersonaError.LeaseRevoked)
        {
            return PublishOutcome.Refused(PublishResult.ActivePersonaChanged, e);
        }
        catch (Exception e) when (e is PersonaException or ProtocolException or CryptographicException)
        {
            return PublishOutcome.Refused(PublishResult.SigningFailed, e);
        }
        finally
        {
            lease.Dispose();
        }

        // 8. The bytes say what was shown, as the persona shown.
        try
        {
            var verified = SignedDocumentCodec.Verify(signed);
            if (!verified.PublicKey.Equals(key) || verified.Document is not ProfileLayoutSnapshot decoded || !SnapshotComparer.Same(decoded, snapshot))
            {
                return PublishOutcome.Refused(PublishResult.NotAsShown);
            }
        }
        catch (ProtocolException e)
        {
            return PublishOutcome.Refused(PublishResult.NotAsShown, e);
        }

        byte[] entry;
        try
        {
            entry = OutboxEntryCodec.Encode(signed, images, key);
        }
        catch (PublicationFileException e)
        {
            return PublishOutcome.Refused(PublishResult.NotAsShown, e);
        }

        // The outbox's bound, over what the index will name once this is stored.
        var superseded = live?.PendingEntry ?? OutboxEntryName.None;
        try
        {
            long total = entry.Length;
            foreach (var existing in index.Entries)
            {
                if (!existing.PendingEntry.IsNone && existing.PendingEntry != superseded)
                {
                    total += files.EntrySize(slot, existing.PendingEntry);
                }
            }

            if (total > MaxOutboxBytes)
            {
                return PublishOutcome.Refused(PublishResult.OutboxFull);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return PublishOutcome.Refused(PublishResult.NotSaved, e);
        }

        // 9. The entry, under a name of its own.
        var name = OutboxEntryName.NewName();
        try
        {
            files.WriteEntry(slot, name, entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return PublishOutcome.Refused(PublishResult.NotSaved, e);
        }

        // 10. The index naming it: the commit point.
        var next = index.With(new PublicationEntry(candidate.PlateId, profile, revision, live?.State ?? PublicationState.Pending, live?.LastPublishedAt ?? 0, name));
        byte[] saved;
        try
        {
            saved = PublicationIndexCodec.Encode(slot, next);
        }
        catch (PublicationFileException e)
        {
            return PublishOutcome.Refused(PublishResult.NotSaved, e);
        }

        try
        {
            files.ReplaceIndex(slot, saved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The new entry stays: the index on disk may name it. A load deletes it if it doesn't.
            return PublishOutcome.Refused(PublishResult.NotSaved, e, indeterminate: true);
        }

        // 11. Only now the revision this one supersedes, which was never acknowledged; a failure
        // leaves a file the index no longer names, which a later load deletes.
        if (!superseded.IsNone)
        {
            files.TryDeleteEntry(slot, superseded);
        }

        return PublishOutcome.Stored(next, profile, revision);
    }

    /// <summary>The candidate's prepared copies in the snapshot's order of its images (by asset id), or null when one is missing.</summary>
    private static IReadOnlyList<ReadOnlyMemory<byte>>? ImagesInOrder(SnapshotCandidate candidate, ProfileLayoutSnapshot snapshot)
    {
        if (candidate.Images.Count != candidate.ImageBytes.Count || candidate.Images.Count != snapshot.Images.Count)
        {
            return null;
        }

        var ordered = new ReadOnlyMemory<byte>[snapshot.Images.Count];
        for (var position = 0; position < ordered.Length; position++)
        {
            var found = false;
            for (var index = 0; index < candidate.Images.Count; index++)
            {
                if (candidate.Images[index].AssetId == snapshot.Images[position].AssetId)
                {
                    ordered[position] = candidate.ImageBytes[index];
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return null;
            }
        }

        return ordered;
    }
}
