using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Plates;

/// <summary>What can be offered for a kept draft, judged against the Library (see <see cref="KeptChangesReview.Classify"/>).</summary>
internal enum KeptChangesChoice
{
    /// <summary>The draft is exactly the Plate's saved content (a save that landed as AetherFrame unloaded): retired silently.</summary>
    Identical,

    /// <summary>The Plate is saved exactly as the changes started from: they can be restored over it.</summary>
    Restore,

    /// <summary>
    /// The Plate was saved again since (here, in another game window, or by a save that landed while
    /// reporting failure), or edited by hand in a way that changed its revision or modified time:
    /// restoring over it would undo something, so only Restore as New Plate is offered, and the
    /// Plate is never written. A hand edit that changes neither isn't seen (see <see cref="KeptChangesReview.Classify"/>).
    /// </summary>
    SavedAgain,

    /// <summary>
    /// The Plate is a newer version's, or damaged: this version can't open it, so only Restore as
    /// New Plate is offered, and the Plate is never written.
    /// </summary>
    CannotOpen,

    /// <summary>The Plate was deleted or is missing: only Restore as New Plate, never under its old id.</summary>
    Deleted,

    /// <summary>The Plate couldn't be opened this session (locked): Restore as New Plate, or decide later.</summary>
    Unavailable,
}

/// <summary>
/// A kept draft that can be offered: its file, its envelope (with its document as a load repairs it),
/// the document's JSON exactly as kept, every field included, and what the Library allowed at load.
/// </summary>
internal sealed record KeptDraft(string Path, PlateDraft Draft, string DocumentJson, KeptChangesChoice Choice)
{
    internal Guid PlateId => Draft.PlateId;

    /// <summary>
    /// The same editing's older recovery points, newest first, each judged on its own: a draft kept at
    /// unload with the checkpoints taken before it, or a crashed editing's older checkpoints. Offered
    /// with this one, never as questions of their own. This one, the newest, is the editing's token:
    /// whatever acts on any of them claims this file first.
    /// </summary>
    internal IReadOnlyList<KeptDraft> Older { get; init; } = [];

    /// <summary>
    /// The editing's other checkpoint files (older ones, ones beyond what was read), removed once the
    /// editing is answered, so nothing of it is offered again.
    /// </summary>
    internal IReadOnlyList<string> OtherFiles { get; init; } = [];

    /// <summary>
    /// The editing's checkpoints that couldn't be read: moved to the Drafts trash with the answer
    /// rather than deleted, so what is left of them can be salvaged there while the trash keeps them.
    /// </summary>
    internal IReadOnlyList<string> DamagedFiles { get; init; } = [];

    /// <summary>A recovery checkpoint taken while editing, rather than a draft kept at unload.</summary>
    internal bool IsCheckpoint => Draft.Sequence is not null;

    /// <summary>The kept content, as the editor applies it.</summary>
    internal ProfileService.DocumentState State { get; } = ProfileService.DocumentState.Capture(Draft.Document);

    /// <summary>A fresh copy of the kept document's JSON, for a new Plate made from it.</summary>
    internal JsonObject DocumentRaw() => JsonNode.Parse(DocumentJson) as JsonObject ?? throw new JsonException("Kept changes hold no Plate.");
}

/// <summary>
/// What a load read of the kept unsaved changes and recovery checkpoints (see <see cref="KeptChangesReview.ReviewAsync"/>).
/// </summary>
/// <param name="Offers">The editings to offer, newest first.</param>
/// <param name="Complete">
/// Whether every kept draft and recovery checkpoint there was has been listed and read by this
/// version, so <paramref name="Offers"/> holds everything there is to answer. False when the Library
/// didn't load, a folder couldn't be listed, or anything was left unread: damaged, couldn't be
/// opened, saved by a newer version, beyond what one load reads, or an editing with no checkpoint
/// left intact. What recovery holds is then unknown: nothing may take the install for one with
/// nothing to recover (the welcome waits for a later load), and every such file is left as it is.
/// </param>
internal sealed record KeptChangesScan(IReadOnlyList<KeptDraft> Offers, bool Complete);

/// <summary>
/// The next load's side of unsaved changes AetherFrame kept when it unloaded: reading them, judging
/// each against the Library, and retiring those a save already covers. Free of Dalamud.
/// </summary>
internal static class KeptChangesReview
{
    /// <summary>What to offer (see <see cref="ReviewAsync"/>), for a caller that doesn't need to know whether everything was read.</summary>
    internal static async Task<IReadOnlyList<KeptDraft>> LoadAsync(DraftStore files, PlateLibraryService library, IAetherFrameLog log, RecoveryCheckpointStore? checkpoints = null) =>
        (await ReviewAsync(files, library, log, checkpoints).ConfigureAwait(false)).Offers;

    /// <summary>
    /// Reads the newest drafts (see <see cref="DraftStore.ReadNewestAsync"/>) and judges each against
    /// the Library, newest first. A draft identical to its Plate's saved content is retired (moved
    /// intact to the trash) and not returned; one that can't be read is left as it is and not
    /// returned; the rest are what to offer. With the Library not loaded, nothing is touched and
    /// nothing is offered: no draft can be judged against a Library that failed to load. Says whether
    /// everything was read (<see cref="KeptChangesScan.Complete"/>); a Drafts folder that can't be
    /// listed throws, as before, and nothing is read.
    /// </summary>
    internal static async Task<KeptChangesScan> ReviewAsync(DraftStore files, PlateLibraryService library, IAetherFrameLog log, RecoveryCheckpointStore? checkpoints = null)
    {
        if (!library.IsLoaded)
        {
            return new KeptChangesScan([], Complete: false);
        }

        var listing = await files.ReadNewestAsync().ConfigureAwait(false);
        var complete = listing.Unread == 0;
        var groups = new List<Group>();
        var byEdit = new Dictionary<(Guid Session, Guid Edit), Group>();
        if (checkpoints is not null)
        {
            complete &= ReadCheckpoints(checkpoints, groups, byEdit, log);
        }

        // A draft kept at unload is its editing's last word: the newest point of its checkpoints' group.
        foreach (var read in listing.Drafts)
        {
            if (read.Status != DraftReadStatus.Ready)
            {
                // Damaged, unopenable or a newer version's: left as it is, and what it holds is unknown.
                complete = false;
                continue;
            }

            var draft = read.Draft!;
            if (draft.SessionId is { } session && draft.EditId is { } edit && byEdit.TryGetValue((session, edit), out var group) && group.PlateId == draft.PlateId)
            {
                group.Points.Insert(0, (read.Path, draft, read.DocumentJson!));
            }
            else
            {
                groups.Add(new Group(draft.PlateId) { Points = { (read.Path, draft, read.DocumentJson!) } });
            }
        }

        var ordered = groups
            .Where(g => g.Points.Count > 0 && !g.Unavailable)
            .OrderByDescending(g => g.Points[0].Draft.WrittenAtUtc)
            .ToList();
        if (ordered.Count > DraftStore.MaxDraftsRead)
        {
            log.Warning($"AetherFrame found unsaved changes from {ordered.Count} editings and offers the newest {DraftStore.MaxDraftsRead}; the others stay kept for a later start.");
            ordered = ordered.Take(DraftStore.MaxDraftsRead).ToList();
            complete = false;
        }

        var offers = new List<KeptDraft>();
        foreach (var group in ordered)
        {
            var newest = group.Points[0];
            var choice = Classify(newest.Draft, library);
            var others = group.Files.Where(f => !string.Equals(f, newest.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (choice != KeptChangesChoice.Identical)
            {
                // An older point the Plate is saved as holds nothing to recover: it goes with the answer, unoffered.
                var older = group.Points.Skip(1)
                    .Select(p => new KeptDraft(p.Path, p.Draft, p.DocumentJson, Classify(p.Draft, library)))
                    .Where(p => p.Choice != KeptChangesChoice.Identical)
                    .ToList();
                offers.Add(new KeptDraft(newest.Path, newest.Draft, newest.DocumentJson, choice) { Older = older, OtherFiles = others, DamagedFiles = group.Damaged });
                continue;
            }

            // Its newest point is the Plate's saved content: a save already holds it, and every older point is behind that save.
            if (files.Claim(newest.Path) == DraftClaim.Claimed)
            {
                log.Information($"AetherFrame retired kept unsaved changes of Plate {newest.Draft.PlateId}: they are its saved version.");
                RemoveFiles(checkpoints, others, log);
                TrashDamaged(files, group.Damaged);
            }
        }

        if (offers.Count > 0)
        {
            log.Information($"AetherFrame kept unsaved changes for {offers.Count} Plate edit(s) when it last stopped; offering them back.");
        }

        return new KeptChangesScan(offers, complete);
    }

    /// <summary>
    /// Removes an answered editing's other checkpoint files (see <see cref="KeptDraft.OtherFiles"/>).
    /// One that can't be removed is logged; a later load finds it without its token and offers it on its own.
    /// </summary>
    internal static void RemoveFiles(RecoveryCheckpointStore? checkpoints, IReadOnlyList<string> paths, IAetherFrameLog log)
    {
        if (checkpoints is null)
        {
            return;
        }

        foreach (var path in paths)
        {
            try
            {
                checkpoints.Files.DeleteFile(path);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                log.Warning($"AetherFrame couldn't remove an answered recovery checkpoint ({ex.GetType().Name}).");
            }
        }
    }

    /// <summary>An answered editing's damaged checkpoints (see <see cref="KeptDraft.DamagedFiles"/>) go to the Drafts trash; one that can't stays, logged.</summary>
    internal static void TrashDamaged(DraftStore files, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            files.MoveToTrash(path);
        }
    }

    /// <summary>
    /// Ended runs' checkpoints, grouped by editing (a running client's are never listed). Each group
    /// reads its checkpoints newest first until <see cref="RecoveryCheckpointStore.KeptPerEdit"/> read
    /// intact: a damaged or interrupted newest one leaves the earlier ones on offer. Damaged ones are
    /// logged and go to the Drafts trash with an answer rather than being deleted; a newer version's are never
    /// offered or removed. One that can't be opened (held open, or gone because another game window is
    /// answering its editing) holds the whole editing back, untouched, for a later load. Returns
    /// whether every checkpoint was listed and read, or goes with an editing on offer: false when a
    /// folder couldn't be listed, a checkpoint couldn't be opened, names another Plate or is a newer
    /// version's, or an editing has no intact checkpoint left to offer.
    /// </summary>
    private static bool ReadCheckpoints(RecoveryCheckpointStore checkpoints, List<Group> groups, Dictionary<(Guid Session, Guid Edit), Group> byEdit, IAetherFrameLog log)
    {
        var (found, running, complete) = checkpoints.ListEndedSessions();
        if (running > 0)
        {
            log.Information($"AetherFrame left the recovery checkpoints of {running} other running game client(s) alone.");
        }

        foreach (var edit in found.GroupBy(f => (f.SessionId, f.EditId)))
        {
            var group = new Group(edit.First().PlateId);
            var newerVersion = false;
            foreach (var file in edit.OrderByDescending(f => f.Sequence))
            {
                if (file.PlateId != group.PlateId)
                {
                    log.Warning($"AetherFrame ignored a recovery checkpoint that names another Plate than its editing: {LogPrivacy.FileName(file.Path)}");
                    complete = false;
                    continue;
                }

                if (group.Points.Count >= RecoveryCheckpointStore.KeptPerEdit)
                {
                    group.Files.Add(file.Path);
                    continue;
                }

                try
                {
                    if (checkpoints.ReadForOffer(file) is { } read)
                    {
                        group.Points.Add((file.Path, read.Draft, read.DocumentJson));
                        group.Files.Add(file.Path);
                    }
                    else
                    {
                        newerVersion = true;
                        log.Warning($"AetherFrame found a recovery checkpoint saved by a newer version ({LogPrivacy.FileName(file.Path)}); it is left as it is.");
                    }
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    // Gone (another game window is answering it) or held open: this editing waits for a later load, untouched.
                    log.Warning($"AetherFrame couldn't open recovery checkpoint {LogPrivacy.FileName(file.Path)} ({ex.GetType().Name}); its editing is offered at a later start.");
                    group.Unavailable = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not Lifecycle.OperationAbandonedException)
                {
                    // Not deleted: it goes to the Drafts trash with an answer, where what is left of it can be salvaged.
                    log.Error(ex, $"AetherFrame could not read recovery checkpoint {LogPrivacy.FileName(file.Path)}; an earlier one is offered.");
                    group.Damaged.Add(file.Path);
                }
            }

            // A newer version's editing is never answered here, so none of its files are removed with an answer.
            if (newerVersion)
            {
                group.Points.Clear();
            }

            // Held back, a newer version's, or nothing intact to offer: what this editing holds is unknown here.
            if (group.Unavailable || group.Points.Count == 0)
            {
                complete = false;
            }

            groups.Add(group);
            byEdit[edit.Key] = group;
        }

        return complete;
    }

    private sealed class Group(Guid plateId)
    {
        internal Guid PlateId { get; } = plateId;

        /// <summary>Recovery points read intact, newest first.</summary>
        internal List<(string Path, PlateDraft Draft, string DocumentJson)> Points { get; init; } = new();

        /// <summary>Its checkpoint files that go with an answer: those read intact, and those beyond the newest intact ones (never a damaged one, or a draft kept at unload).</summary>
        internal List<string> Files { get; } = new();

        /// <summary>Its checkpoints that couldn't be read: moved to the Drafts trash with an answer.</summary>
        internal List<string> Damaged { get; } = new();

        /// <summary>A checkpoint couldn't be opened: nothing of this editing is offered or retired this load.</summary>
        internal bool Unavailable { get; set; }
    }

    /// <summary>
    /// What the Library allows for <paramref name="draft"/> now. Identical when its content is the
    /// Plate's saved content (compared structurally, as dirty state is); Restore when the Plate is
    /// Ready and its saved revision and modified time are the ones the changes started from;
    /// otherwise only a new Plate (see <see cref="KeptChangesChoice"/>). Only the revision and the
    /// modified time are compared, so a hand edit to the Plate's file that changes neither still
    /// offers Restore. Restoring never writes: the player's Save is what would replace that edit.
    /// </summary>
    internal static KeptChangesChoice Classify(PlateDraft draft, PlateLibraryService library)
    {
        if (library.FindPlate(draft.PlateId) is not { } plate)
        {
            return KeptChangesChoice.Deleted;
        }

        if (!plate.IsReady)
        {
            return NotReady(plate);
        }

        if (library.GetSavedDocument(draft.PlateId) is { } saved
            && ProfileService.DocumentState.Capture(saved).ContentEquals(ProfileService.DocumentState.Capture(draft.Document)))
        {
            return KeptChangesChoice.Identical;
        }

        return plate.Revision == draft.BaseRevision && plate.ModifiedUtc == draft.BaseUpdatedAtUtc
            ? KeptChangesChoice.Restore
            : KeptChangesChoice.SavedAgain;
    }

    /// <summary>
    /// <paramref name="offered"/> again, just before acting on it this session. Only Library
    /// operations change a loaded Plate now, and a rename writes no content, so a Plate offered for
    /// Restore stays restorable while it is Ready at the same revision; a save bumps the revision
    /// (only a new Plate then), and a Plate deleted since can only be restored as a new one.
    /// </summary>
    internal static KeptChangesChoice Recheck(KeptChangesChoice offered, PlateDraft draft, PlateLibraryService library)
    {
        if (library.FindPlate(draft.PlateId) is not { } plate)
        {
            return KeptChangesChoice.Deleted;
        }

        if (offered != KeptChangesChoice.Restore)
        {
            return offered;
        }

        if (!plate.IsReady)
        {
            return NotReady(plate);
        }

        return plate.Revision == draft.BaseRevision ? KeptChangesChoice.Restore : KeptChangesChoice.SavedAgain;
    }

    /// <summary>A Plate in the Library that isn't Ready: locked this session, or one this version can't open (newer, or damaged).</summary>
    private static KeptChangesChoice NotReady(PlateSummary plate) =>
        plate.Status != PlateStatus.NewerVersion && plate.Problem == PlateLibraryService.UnavailablePlateProblem
            ? KeptChangesChoice.Unavailable
            : KeptChangesChoice.CannotOpen;
}
