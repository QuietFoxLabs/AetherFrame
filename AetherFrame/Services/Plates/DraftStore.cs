using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Lifecycle;

namespace AetherFrame.Services.Plates;

/// <summary>What became of a draft's file when it was claimed (see <see cref="DraftStore.Claim"/>).</summary>
internal enum DraftClaim
{
    /// <summary>Moved to the trash by this call: this game window acts on it, and no other can.</summary>
    Claimed,

    /// <summary>Gone already: another game window sharing the folder took it.</summary>
    AlreadyHandled,

    /// <summary>Still where it was (the move failed): nothing may act on it.</summary>
    Failed,
}

/// <summary>How a draft file read (see <see cref="DraftStore.ReadNewestAsync"/>).</summary>
internal enum DraftReadStatus
{
    Ready,

    /// <summary>Written by a newer AetherFrame: left as it is, never offered.</summary>
    NewerVersion,

    /// <summary>Its content is damaged: left as it is, never offered.</summary>
    Damaged,

    /// <summary>It couldn't be opened (locked, gone): left as it is, never offered.</summary>
    Unavailable,
}

/// <summary>One draft file as read: for a Ready one, its envelope and its document's migrated JSON.</summary>
internal sealed record DraftRead(string Path, DraftReadStatus Status, PlateDraft? Draft, string? DocumentJson);

/// <summary>The drafts read at a load, newest first, and how many more were left unread.</summary>
internal sealed record DraftListing(IReadOnlyList<DraftRead> Drafts, int Unread);

/// <summary>
/// Where AetherFrame keeps an editor's unsaved changes when it unloads, and how it reads and claims
/// them at the next load (<see cref="PlateStoragePaths.DraftsDirectory"/>): beside the Library,
/// never inside a Plate, and never in the plugin configuration. Deliberately independent of Dalamud,
/// so all of it runs in tests against plain files.
///
/// <para><b>Write-once.</b> Every draft gets a new file named for its Plate, the time and its own
/// random id (see <see cref="PlateStoragePaths.GetDraftPath"/>), so nothing is ever written over:
/// not an earlier draft, not one another game client wrote, and never a name whose backup copy in
/// Dalamud's storage could hold anything but what was written under it. A draft answered or retired
/// moves intact to <see cref="PlateStoragePaths.DraftTrashDirectory"/>; nothing here deletes one.</para>
///
/// <para><b>Claiming.</b> No lock spans game clients (the reliability report's D8), so whatever
/// acts on a draft moves it to the trash first (<see cref="Claim"/>): only one move can succeed,
/// and a draft another window moved first is reported as handled there.</para>
///
/// <para>Every method is safe from any thread; none keeps state between calls.</para>
/// </summary>
internal sealed class DraftStore
{
    /// <summary>The most drafts one load reads and offers; any others wait, untouched, for a later load.</summary>
    internal const int MaxDraftsRead = 20;

    private readonly PlateStoragePaths paths;
    private readonly IPlateFileStore store;
    private readonly IAetherFrameLog log;
    private readonly Func<DateTime> utcNow;
    private readonly Func<Guid> newId;

    internal DraftStore(PlateStoragePaths paths, IPlateFileStore store, IAetherFrameLog? log = null, Func<DateTime>? utcNow = null, Func<Guid>? newId = null)
    {
        this.paths = paths;
        this.store = store;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.newId = newId ?? Guid.NewGuid;
    }

    internal PlateStoragePaths Paths => paths;

    /// <summary>The draft to keep for a copy of the open document (see <see cref="DraftDocuments.Create"/>), stamped now with a new id.</summary>
    internal PlateDraft Create(ProfileService.OpenDocumentCopy copy, DraftEditor editor, string build) =>
        DraftDocuments.Create(copy.Document, copy.BaseRevision, copy.BaseUpdatedAtUtc, editor, build, newId(), utcNow());

    /// <summary>
    /// Writes <paramref name="draft"/> to a file of its own and returns its path, or null, with
    /// nothing written and the reason logged, when its text fails the proof every Plate write passes
    /// (see <see cref="Prove"/>). Never writes over a file: a name that is somehow taken gets a new
    /// id. A failure of the store itself is thrown, for the caller to report.
    /// </summary>
    internal async Task<string?> WriteAsync(PlateDraft draft)
    {
        var path = paths.GetDraftPath(draft.PlateId, draft.WrittenAtUtc, draft.DraftId);
        for (var attempt = 0; store.FileExists(path); attempt++)
        {
            if (attempt == 3)
            {
                throw new IOException($"AetherFrame found no free name for unsaved changes of Plate {draft.PlateId}.");
            }

            draft.DraftId = newId();
            path = paths.GetDraftPath(draft.PlateId, draft.WrittenAtUtc, draft.DraftId);
        }

        if (Prove(draft) is not { } json)
        {
            return null;
        }

        await store.WriteTextAsync(path, json).ConfigureAwait(false);
        log.Information($"AetherFrame kept the unsaved changes of Plate {draft.PlateId} as {LogPrivacy.FileName(path)}.");
        return path;
    }

    /// <summary>
    /// The exact text a draft write puts on disk, proven first to read back as the same Ready draft
    /// through the reader every load uses: serialized, put through a file's byte-level round trip
    /// (<see cref="VersionedJson.RequireFaithfulReadBack"/>), and parsed both layers deep. Text that
    /// wouldn't load is refused (null), logged, and never written.
    /// </summary>
    private string? Prove(PlateDraft draft)
    {
        try
        {
            var json = VersionedJson.Serialize(DraftDocuments.ToJson(draft));
            var parsed = DraftDocuments.Parse(VersionedJson.RequireFaithfulReadBack(json, "Unsaved changes"));
            if (parsed.Status != DraftTextStatus.Ready || parsed.Draft!.DraftId != draft.DraftId)
            {
                throw new InvalidDataException("It would read back as something else.");
            }

            return json;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            log.Error(ex, $"AetherFrame did not keep the unsaved changes of Plate {draft.PlateId}: what it would have written couldn't be read back.");
            return null;
        }
    }

    /// <summary>
    /// Lists the drafts waiting in the Drafts folder and reads the newest <see cref="MaxDraftsRead"/>,
    /// newest first; the rest stay as they are for a later load, and the log says how many. Files
    /// whose names aren't a draft's (an interrupted write's temporary file, say) are ignored. A draft
    /// that is damaged, can't be opened or was written by a newer version is reported as such, logged
    /// and left untouched. Reads only: nothing here moves or writes anything.
    /// </summary>
    internal async Task<DraftListing> ReadNewestAsync()
    {
        var named = new List<(string Path, DateTime WrittenUtc, Guid PlateId, Guid DraftId)>();
        foreach (var path in store.ListFiles(paths.DraftsDirectory, "*.json"))
        {
            if (PlateStoragePaths.TryParseDraftFileName(path, out var plateId, out var writtenUtc, out var draftId))
            {
                named.Add((path, writtenUtc, plateId, draftId));
            }
            else
            {
                log.Warning($"AetherFrame ignored a file in the Drafts folder that isn't kept changes: {LogPrivacy.FileName(path)}");
            }
        }

        var newest = named
            .OrderByDescending(d => d.WrittenUtc)
            .ThenByDescending(d => Path.GetFileName(d.Path), StringComparer.OrdinalIgnoreCase)
            .Take(MaxDraftsRead)
            .ToList();
        var unread = named.Count - newest.Count;
        if (unread > 0)
        {
            log.Warning($"AetherFrame found {named.Count} kept unsaved changes and read the newest {newest.Count}; the other {unread} stay in the Drafts folder for a later start.");
        }

        var read = new List<DraftRead>(newest.Count);
        foreach (var (path, _, plateId, draftId) in newest)
        {
            read.Add(await ReadAsync(path, plateId, draftId).ConfigureAwait(false));
        }

        return new DraftListing(read, unread);
    }

    private async Task<DraftRead> ReadAsync(string path, Guid plateId, Guid draftId)
    {
        var name = LogPrivacy.FileName(path);
        try
        {
            DraftText? text = null;
            await store.ReadTextAsync(path, stored => text = DraftDocuments.Parse(stored.Text)).ConfigureAwait(false);
            if (text is null)
            {
                throw new InvalidDataException("Unsaved changes could not be read.");
            }

            if (text.Status == DraftTextStatus.NewerVersion)
            {
                log.Warning($"AetherFrame found kept changes saved by a newer version ({name}); they are left as they are.");
                return new DraftRead(path, DraftReadStatus.NewerVersion, null, null);
            }

            // The file name is what was written for it: a file whose content names another Plate or
            // draft was copied or edited by hand, and is not offered for either.
            var draft = text.Draft!;
            if (draft.PlateId != plateId || draft.DraftId != draftId)
            {
                throw new InvalidDataException("The kept changes don't match their file's name.");
            }

            return new DraftRead(path, DraftReadStatus.Ready, draft, text.DocumentJson);
        }
        catch (Exception ex) when (!IsInterruption(ex))
        {
            var unavailable = PlateLibraryService.IsUnopenable(ex);
            log.Error(ex, unavailable
                ? $"AetherFrame could not open kept changes {name}; they are left as they are."
                : $"AetherFrame could not read kept changes {name}; they are damaged and left as they are.");
            return new DraftRead(path, unavailable ? DraftReadStatus.Unavailable : DraftReadStatus.Damaged, null, null);
        }
    }

    /// <summary>
    /// Takes a draft for this game window before anything acts on it: moves it, intact and never over
    /// another file, to the trash. Discarding it is exactly this. A draft no longer there was taken by
    /// another game window. A move that fails leaves it where it was, logged, and nothing may act on it.
    /// </summary>
    internal DraftClaim Claim(string draftPath)
    {
        try
        {
            if (!store.FileExists(draftPath))
            {
                return DraftClaim.AlreadyHandled;
            }

            store.MoveFile(draftPath, paths.GetDraftTrashPath(draftPath));
            return DraftClaim.Claimed;
        }
        catch (Exception ex) when (!IsInterruption(ex))
        {
            if (!store.FileExists(draftPath))
            {
                return DraftClaim.AlreadyHandled;
            }

            log.Error(ex, $"AetherFrame could not move kept changes {LogPrivacy.FileName(draftPath)} to its Trash folder; they are left where they were.");
            return DraftClaim.Failed;
        }
    }

    /// <summary>
    /// Puts a claimed draft back where it was, when what it was claimed for failed before it changed
    /// anything, so it is offered again; never over a file. False, logged, when it stays in the trash.
    /// </summary>
    internal bool Unclaim(string draftPath)
    {
        try
        {
            store.MoveFile(paths.GetDraftTrashPath(draftPath), draftPath);
            return true;
        }
        catch (Exception ex) when (!IsInterruption(ex))
        {
            log.Error(ex, $"AetherFrame could not put kept changes {LogPrivacy.FileName(draftPath)} back; they stay in its Trash folder.");
            return false;
        }
    }

    private static bool IsInterruption(Exception ex) => ex is OperationCanceledException or OperationAbandonedException;
}
