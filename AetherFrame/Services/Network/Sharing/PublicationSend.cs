using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using AetherFrame.Personas;
using AetherFrame.Protocol.Identity;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Network.Transport;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>What sending a character's waiting revision came to.</summary>
internal enum SendResult
{
    /// <summary>The server accepted it: the index records it as published, and the outbox entry is gone.</summary>
    Sent,

    /// <summary>Nothing waits to be sent.</summary>
    NothingWaiting,

    /// <summary>It was signed more than a day ago, so it was dropped unsent (N2): the next save signs anew.</summary>
    Stale,

    /// <summary>The server refused it, with <see cref="SendOutcome.Reason"/> when it gave one; it was dropped, and the Plate the server shows is unchanged.</summary>
    Refused,

    /// <summary>Another AetherFrame's check took the character over (C1).</summary>
    TakenOver,

    /// <summary>No answer, a busy or failing server, a rate limit, or a send the player stopped: it waits for the next try.</summary>
    TryLater,

    /// <summary>The character's key can't sign now.</summary>
    KeyUnavailable,

    /// <summary>The index or the entry can't be read, or isn't what this character signed: nothing was sent.</summary>
    Unreadable,

    /// <summary>The server accepted it, but the index couldn't be saved: it is sent again next time, and the server answers that exact resubmission as accepted.</summary>
    NotSaved,
}

/// <summary>
/// What sending came to: the server's reason code for a refusal, once sent the Plate that is now
/// public, and for a revision that waits, the status the server answered with, if it answered at
/// all (for the log: a busy server and no answer in time look alike to the player).
/// </summary>
internal sealed record SendOutcome(SendResult Result, string? Reason = null, Guid Plate = default, HttpStatusCode? Status = null);

/// <summary>
/// Sends a character's waiting revision (NETWORK2's N2-9c; decisions C3, C4 and N2): the one entry
/// its key's publication index names, under the binding's profile id, exactly as it was signed and
/// stored in the outbox, with its prepared images. It runs inside a persona-session operation,
/// under the persona files' lock, off the framework thread. The index is the record: a revision the
/// server accepts is recorded as published before its outbox entry is deleted, and one it refuses,
/// or one signed more than a day ago, is dropped the same way, so nothing the index doesn't name is
/// ever sent. Compiled only in the networking preview flavour.
/// </summary>
internal static class PublicationSend
{
    /// <summary>How old a waiting revision may be and still be sent (N2).</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// <summary>The server's reason codes for refusing a publish (ServerApi-v1.md, section 4); any other text is no reason.</summary>
    private static readonly string[] Reasons = ["not-bound", "wrong-profile", "not-a-layout", "clock-ahead", "revision-conflict", "image-refused", "document-refused"];

    internal static SendOutcome SendWaiting(PersonaManager manager, PublicationFiles files, SharingClient client, PersonaSlotId slot, PersonaPublicKey key, ProfileId binding, Func<DateTimeOffset> utcNow, CancellationToken cancellation)
    {
        PublicationIndex index;
        PublicationEntry? waiting = null;
        OutboxEntry entry;
        try
        {
            index = files.ReadIndex(slot) is { } bytes ? PublicationIndexCodec.Decode(bytes, slot) : PublicationIndex.Empty;
            foreach (var candidate in index.Entries)
            {
                if (candidate.ProfileId == binding && candidate.IsLive && !candidate.PendingEntry.IsNone)
                {
                    waiting = candidate;
                }
            }

            if (waiting is null)
            {
                return new SendOutcome(SendResult.NothingWaiting);
            }

            var stored = files.ReadEntry(slot, waiting.PendingEntry);
            if (stored is null)
            {
                return new SendOutcome(SendResult.Unreadable);
            }

            entry = OutboxEntryCodec.Decode(stored, key);
        }
        catch (Exception exception) when (exception is PublicationFileException or IOException or UnauthorizedAccessException)
        {
            return new SendOutcome(SendResult.Unreadable);
        }

        if (entry.Snapshot.ProfileId != binding || entry.Snapshot.RevisionId != waiting.LatestRevision)
        {
            return new SendOutcome(SendResult.Unreadable);
        }

        if (utcNow() - entry.Snapshot.CreatedAt > MaxAge)
        {
            return Drop(files, slot, index, waiting) ? new SendOutcome(SendResult.Stale) : new SendOutcome(SendResult.Unreadable);
        }

        SharingResponse response;
        try
        {
            response = client.PublishAsync(entry.Document.ToArray(), entry.Images, new LeasedSigner(manager, slot, key), cancellation).GetAwaiter().GetResult();
        }
        catch (SharingException exception)
        {
            return new SendOutcome(SendResult.TryLater, Status: exception.Status);
        }
        catch (OperationCanceledException)
        {
            // The player stopped the send, or the plugin is unloading: the revision waits.
            return new SendOutcome(SendResult.TryLater);
        }
        catch (LeasedSignerException)
        {
            return new SendOutcome(SendResult.KeyUnavailable);
        }

        switch (response.Status)
        {
            case HttpStatusCode.NoContent:
                var published = waiting with { State = PublicationState.Published, LastPublishedAt = utcNow().ToUnixTimeSeconds(), PendingEntry = OutboxEntryName.None };
                return Save(files, slot, index.With(published), waiting.PendingEntry) ? new SendOutcome(SendResult.Sent, Plate: waiting.PlateId) : new SendOutcome(SendResult.NotSaved, Plate: waiting.PlateId);
            case HttpStatusCode.Gone:
                return new SendOutcome(SendResult.TakenOver);
            case HttpStatusCode.UnprocessableEntity:
            case HttpStatusCode.BadRequest:
            case HttpStatusCode.Forbidden:
            case HttpStatusCode.NotFound:
            case HttpStatusCode.RequestEntityTooLarge:
                // A refusal of this revision: it would be refused again, so it is dropped.
                var reason = response.Status == HttpStatusCode.UnprocessableEntity ? ReasonOf(response.Body) : null;
                return Drop(files, slot, index, waiting) ? new SendOutcome(SendResult.Refused, reason) : new SendOutcome(SendResult.Unreadable);
            default:
                // Busy, limited, restarting (a 5xx from the proxy while the server restarts), a
                // request that timed out, or an answer this build doesn't know: it waits.
                return new SendOutcome(SendResult.TryLater, Status: response.Status);
        }
    }

    /// <summary>
    /// Drops every revision waiting in the outbox of a character's key, and the index's record of
    /// anything not yet published, when sharing is paused or turned off (C4, D1): nothing it signed
    /// is sent afterwards. True when the index was saved, or there was nothing to drop.
    /// </summary>
    internal static bool DropAll(PublicationFiles files, PersonaSlotId slot)
    {
        try
        {
            if (files.ReadIndex(slot) is not { } bytes)
            {
                return true;
            }

            var index = PublicationIndexCodec.Decode(bytes, slot);
            var kept = new System.Collections.Generic.List<PublicationEntry>();
            foreach (var entry in index.Entries)
            {
                if (entry.State == PublicationState.Published)
                {
                    kept.Add(entry with { PendingEntry = OutboxEntryName.None });
                }
            }

            var next = new PublicationIndex(kept);
            files.ReplaceIndex(slot, PublicationIndexCodec.Encode(slot, next));
            foreach (var entry in index.Entries)
            {
                if (!entry.PendingEntry.IsNone)
                {
                    files.TryDeleteEntry(slot, entry.PendingEntry);
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is PublicationFileException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Forgets everything a character's key published from here, when sharing is turned off or the
    /// character was taken over (C4, C1): the server holds none of it any more. True when the index
    /// was emptied, or there was none.
    /// </summary>
    internal static bool Clear(PublicationFiles files, PersonaSlotId slot)
    {
        try
        {
            if (files.ReadIndex(slot) is not { } bytes)
            {
                return true;
            }

            PublicationIndex? index = null;
            try
            {
                index = PublicationIndexCodec.Decode(bytes, slot);
            }
            catch (PublicationFileException)
            {
                // A damaged index is replaced all the same: nothing it named is sent again.
            }

            files.ReplaceIndex(slot, PublicationIndexCodec.Encode(slot, PublicationIndex.Empty));
            foreach (var entry in index?.Entries ?? Array.Empty<PublicationEntry>())
            {
                if (!entry.PendingEntry.IsNone)
                {
                    files.TryDeleteEntry(slot, entry.PendingEntry);
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is PublicationFileException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The refusal's reason code, when the body is exactly one of the server's.</summary>
    internal static string? ReasonOf(byte[] body)
    {
        if (body.Length is 0 or > 32)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(body);
        return Array.IndexOf(Reasons, text) >= 0 ? text : null;
    }

    /// <summary>Drops a revision the server won't take: a published entry keeps its record, a never-published one goes.</summary>
    private static bool Drop(PublicationFiles files, PersonaSlotId slot, PublicationIndex index, PublicationEntry waiting)
    {
        var entries = new System.Collections.Generic.List<PublicationEntry>();
        foreach (var entry in index.Entries)
        {
            if (entry != waiting)
            {
                entries.Add(entry);
            }
            else if (entry.State == PublicationState.Published)
            {
                entries.Add(entry with { PendingEntry = OutboxEntryName.None });
            }
        }

        return Save(files, slot, new PublicationIndex(entries), waiting.PendingEntry);
    }

    /// <summary>Saves <paramref name="next"/>, then deletes the outbox entry it no longer names.</summary>
    private static bool Save(PublicationFiles files, PersonaSlotId slot, PublicationIndex next, OutboxEntryName released)
    {
        try
        {
            files.ReplaceIndex(slot, PublicationIndexCodec.Encode(slot, next));
        }
        catch (Exception exception) when (exception is PublicationFileException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        files.TryDeleteEntry(slot, released);
        return true;
    }
}
