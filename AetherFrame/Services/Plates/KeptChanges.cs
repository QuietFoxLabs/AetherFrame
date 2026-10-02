using System;
using System.Collections.Generic;
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
    /// reporting failure), edited by hand, or is now a newer version's or damaged: restoring over it
    /// would undo something, so only Restore as New Plate is offered, and the Plate is never written.
    /// </summary>
    SavedAgain,

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

    /// <summary>The kept content, as the editor applies it.</summary>
    internal ProfileService.DocumentState State { get; } = ProfileService.DocumentState.Capture(Draft.Document);

    /// <summary>A fresh copy of the kept document's JSON, for a new Plate made from it.</summary>
    internal JsonObject DocumentRaw() => JsonNode.Parse(DocumentJson) as JsonObject ?? throw new JsonException("Kept changes hold no Plate.");
}

/// <summary>
/// The next load's side of unsaved changes AetherFrame kept when it unloaded: reading them, judging
/// each against the Library, and retiring those a save already covers. Free of Dalamud.
/// </summary>
internal static class KeptChangesReview
{
    /// <summary>
    /// Reads the newest drafts (see <see cref="DraftStore.ReadNewestAsync"/>) and judges each against
    /// the Library, newest first. A draft identical to its Plate's saved content is retired (moved
    /// intact to the trash) and not returned; one that can't be read is left as it is and not
    /// returned; the rest are what to offer. With the Library not loaded, nothing is touched and
    /// nothing is offered: no draft can be judged against a Library that failed to load.
    /// </summary>
    internal static async Task<IReadOnlyList<KeptDraft>> LoadAsync(DraftStore files, PlateLibraryService library, IAetherFrameLog log)
    {
        if (!library.IsLoaded)
        {
            return [];
        }

        var listing = await files.ReadNewestAsync().ConfigureAwait(false);
        var offers = new List<KeptDraft>();
        foreach (var read in listing.Drafts)
        {
            if (read.Status != DraftReadStatus.Ready)
            {
                continue;
            }

            var choice = Classify(read.Draft!, library);
            if (choice != KeptChangesChoice.Identical)
            {
                offers.Add(new KeptDraft(read.Path, read.Draft!, read.DocumentJson!, choice));
                continue;
            }

            if (files.Claim(read.Path) == DraftClaim.Claimed)
            {
                log.Information($"AetherFrame retired kept unsaved changes of Plate {read.Draft!.PlateId}: they are its saved version.");
            }
        }

        if (offers.Count > 0)
        {
            log.Information($"AetherFrame kept unsaved changes for {offers.Count} Plate edit(s) when it last unloaded; offering them back.");
        }

        return offers;
    }

    /// <summary>
    /// What the Library allows for <paramref name="draft"/> now. Identical when its content is the
    /// Plate's saved content (compared structurally, as dirty state is); Restore when the Plate is
    /// Ready and its saved revision and modified time are the ones the changes started from;
    /// otherwise only a new Plate (see <see cref="KeptChangesChoice"/>).
    /// </summary>
    internal static KeptChangesChoice Classify(PlateDraft draft, PlateLibraryService library)
    {
        if (library.FindPlate(draft.PlateId) is not { } plate)
        {
            return KeptChangesChoice.Deleted;
        }

        switch (plate.Status)
        {
            case PlateStatus.Ready:
                if (library.GetSavedDocument(draft.PlateId) is { } saved
                    && ProfileService.DocumentState.Capture(saved).ContentEquals(ProfileService.DocumentState.Capture(draft.Document)))
                {
                    return KeptChangesChoice.Identical;
                }

                return plate.Revision == draft.BaseRevision && plate.ModifiedUtc == draft.BaseUpdatedAtUtc
                    ? KeptChangesChoice.Restore
                    : KeptChangesChoice.SavedAgain;

            case PlateStatus.NewerVersion:
                return KeptChangesChoice.SavedAgain;

            default:
                return plate.Problem == PlateLibraryService.UnavailablePlateProblem ? KeptChangesChoice.Unavailable : KeptChangesChoice.SavedAgain;
        }
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

        return offered == KeptChangesChoice.Restore && (!plate.IsReady || plate.Revision != draft.BaseRevision)
            ? KeptChangesChoice.SavedAgain
            : offered;
    }
}
