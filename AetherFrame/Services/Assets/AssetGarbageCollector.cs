using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AetherFrame.Domain.Assets;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Assets;

internal enum AssetLifecycleState
{
    /// <summary>Referenced by at least one Plate (including trashed Plates).</summary>
    Referenced,

    /// <summary>Referenced by nothing, but protected: too new, or imported this session.</summary>
    Protected,

    /// <summary>Referenced by nothing and eligible to move to the asset trash.</summary>
    Unreferenced,
}

internal sealed record AssetCleanupCandidate(Guid AssetId, string Path, AssetLifecycleState State);

/// <summary>What a cleanup pass would do, computed without changing anything.</summary>
internal sealed record AssetCleanupPlan(bool CanProceed, string? BlockedReason, IReadOnlyList<AssetCleanupCandidate> Assets)
{
    internal IEnumerable<AssetCleanupCandidate> Unreferenced => Assets.Where(a => a.State == AssetLifecycleState.Unreferenced);
}

/// <summary>Why an asset was moved to the trash and when, stored beside it.</summary>
internal sealed record AssetTrashRecord(int Version, Guid AssetId, string FileName, DateTime TrashedAtUtc);

/// <summary>
/// Trash-based cleanup of image assets no Plate references:
/// Referenced → Unreferenced → Trash → Purge after a grace period.
///
/// Built to never sacrifice a user's image: a plan can only proceed from a COMPLETE reference
/// scan (every Plate document readable, trashed Plates included); newly imported assets and
/// anything younger than <see cref="MinimumUnreferencedAge"/> are protected (an unsaved editor
/// session may be using them); trashing is a move that <see cref="Restore"/> undoes; and
/// permanent purge only runs when the caller explicitly passes <c>purgeEnabled: true</c>. Nothing
/// in AetherFrame runs any of this automatically yet.
/// </summary>
internal sealed class AssetGarbageCollector
{
    /// <summary>An unreferenced asset newer than this is never trashed.</summary>
    internal static readonly TimeSpan MinimumUnreferencedAge = TimeSpan.FromDays(7);

    /// <summary>How long a trashed asset stays restorable before it may be purged.</summary>
    internal static readonly TimeSpan TrashGracePeriod = TimeSpan.FromDays(30);

    private const string TrashRecordSuffix = ".trash.json";

    private readonly AssetStorageService assets;
    private readonly string trashDirectory;
    private readonly IAetherFrameLog log;
    private readonly Func<DateTime> utcNow;

    internal AssetGarbageCollector(AssetStorageService assets, string trashDirectory, IAetherFrameLog? log = null, Func<DateTime>? utcNow = null)
    {
        this.assets = assets;
        this.trashDirectory = trashDirectory;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Classifies every managed asset. <paramref name="additionallyInUse"/> covers references the
    /// saved Plates can't know about — e.g. every asset the open editor document or its undo
    /// history refers to.
    /// </summary>
    internal AssetCleanupPlan Plan(AssetReferenceScan scan, IEnumerable<Guid>? additionallyInUse = null)
    {
        if (!scan.IsComplete)
        {
            return new AssetCleanupPlan(false, "Some Plates couldn't be read, so it isn't safe to decide which images are unused: " + string.Join(" ", scan.Problems), []);
        }

        var inUse = scan.ReferencedAssetIds.ToHashSet();
        if (additionallyInUse is not null)
        {
            inUse.UnionWith(additionallyInUse);
        }

        var session = assets.ImportedThisSession;
        var now = utcNow();
        var result = new List<AssetCleanupCandidate>();

        foreach (var (assetId, path) in assets.ListAssets())
        {
            AssetLifecycleState state;
            if (inUse.Contains(assetId))
            {
                state = AssetLifecycleState.Referenced;
            }
            else if (session.Contains(assetId) || now - GetAgeReference(path) < MinimumUnreferencedAge)
            {
                state = AssetLifecycleState.Protected;
            }
            else
            {
                state = AssetLifecycleState.Unreferenced;
            }

            result.Add(new AssetCleanupCandidate(assetId, path, state));
        }

        return new AssetCleanupPlan(true, null, result);
    }

    /// <summary>
    /// Moves a plan's unreferenced assets to the asset trash. Returns how many moved. A move that
    /// fails leaves nothing behind: the record written for it is removed again, so a later purge
    /// can never treat a still-live asset as trashed. An id whose record names a file that is in
    /// the trash (a second file for the same id, e.g. "{id}.jpg" beside "{id}.png") is left in
    /// place, since one record can name only one trashed file and overwriting it would orphan
    /// the first; a stale record naming nothing is simply replaced.
    /// </summary>
    internal int MoveToTrash(AssetCleanupPlan plan)
    {
        if (!plan.CanProceed)
        {
            throw new InvalidOperationException(plan.BlockedReason ?? "Asset cleanup is blocked.");
        }

        var moved = 0;
        foreach (var candidate in plan.Unreferenced)
        {
            var recordPath = GetRecordPath(candidate.AssetId);
            var recordWritten = false;
            try
            {
                if (!File.Exists(candidate.Path))
                {
                    continue;
                }

                if (File.Exists(recordPath) && TryReadRecord(candidate.AssetId) is { } existing && File.Exists(Path.Combine(trashDirectory, existing.FileName)))
                {
                    log.Warning($"AetherFrame left unused asset {candidate.AssetId} in place: another file for that id is already in the trash.");
                    continue;
                }

                Directory.CreateDirectory(trashDirectory);
                var fileName = Path.GetFileName(candidate.Path);
                var record = new AssetTrashRecord(1, candidate.AssetId, fileName, utcNow());

                // The record first: a trashed file without one would never become purgeable.
                SystemFileStore.WriteAtomically(recordPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, JsonOptions.Default)));
                recordWritten = true;
                File.Move(candidate.Path, Path.Combine(trashDirectory, fileName), overwrite: false);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warning($"AetherFrame could not move unused asset {candidate.AssetId} to the trash ({ex.GetType().Name}).");
                if (recordWritten && File.Exists(candidate.Path))
                {
                    // The asset is still live, so the record would be a lie that purge acts on.
                    TryDeleteRecord(recordPath, candidate.AssetId);
                }
            }
        }

        return moved;
    }

    /// <summary>
    /// Moves a trashed asset back into managed storage. Returns false if it isn't in the trash.
    /// The restored file gets a fresh <see cref="MinimumUnreferencedAge"/> window (its last-write
    /// time is set to now): the player restored it to use it, and an unsaved edit referencing it
    /// must not see it trashed again by the next cleanup pass.
    /// </summary>
    internal bool Restore(Guid assetId)
    {
        var record = TryReadRecord(assetId);
        var trashedPath = record is null ? null : Path.Combine(trashDirectory, record.FileName);
        if (trashedPath is null || !File.Exists(trashedPath))
        {
            return false;
        }

        Directory.CreateDirectory(assets.AssetsDirectory);
        var restoredPath = Path.Combine(assets.AssetsDirectory, record!.FileName);
        File.Move(trashedPath, restoredPath, overwrite: false);

        try
        {
            File.SetLastWriteTimeUtc(restoredPath, utcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame restored asset {assetId} but could not refresh its age ({ex.GetType().Name}).");
        }

        TryDeleteRecord(GetRecordPath(assetId), assetId);
        return true;
    }

    /// <summary>
    /// Permanently deletes trashed assets older than <see cref="TrashGracePeriod"/> — but only when
    /// <paramref name="purgeEnabled"/> is explicitly true; otherwise it just reports what it would
    /// purge. Returns the affected asset ids. An asset's metadata goes only when its file was
    /// really in the trash and is gone from managed storage — a stale record for a live asset
    /// removes nothing but itself — and the record is deleted last, so a failed step leaves it
    /// for the next pass to retry.
    /// </summary>
    internal IReadOnlyList<Guid> PurgeExpired(bool purgeEnabled)
    {
        var expired = new List<Guid>();
        if (!Directory.Exists(trashDirectory))
        {
            return expired;
        }

        var now = utcNow();
        foreach (var recordPath in Directory.GetFiles(trashDirectory, "*" + TrashRecordSuffix))
        {
            var idText = Path.GetFileName(recordPath)[..^TrashRecordSuffix.Length];
            if (!Guid.TryParseExact(idText, "N", out var assetId) || TryReadRecord(assetId) is not { } record)
            {
                continue;
            }

            if (now - record.TrashedAtUtc < TrashGracePeriod)
            {
                continue;
            }

            expired.Add(assetId);
            if (!purgeEnabled)
            {
                continue;
            }

            try
            {
                var trashedPath = Path.Combine(trashDirectory, record.FileName);
                var deletedTrashedFile = false;
                if (File.Exists(trashedPath))
                {
                    File.Delete(trashedPath);
                    deletedTrashedFile = true;
                }

                if (deletedTrashedFile && assets.ResolveAssetPath(assetId) is null)
                {
                    assets.Metadata.Delete(assetId);
                }

                File.Delete(recordPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warning($"AetherFrame could not purge trashed asset {assetId} ({ex.GetType().Name}).");
            }
        }

        return expired;
    }

    private string GetRecordPath(Guid assetId) => Path.Combine(trashDirectory, assetId.ToString("N") + TrashRecordSuffix);

    private void TryDeleteRecord(string recordPath, Guid assetId)
    {
        try
        {
            File.Delete(recordPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not remove the trash record for asset {assetId} ({ex.GetType().Name}).");
        }
    }

    private AssetTrashRecord? TryReadRecord(Guid assetId)
    {
        var path = GetRecordPath(assetId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<AssetTrashRecord>(File.ReadAllText(path, Encoding.UTF8), JsonOptions.Default);

            // The record names the file to move or delete, so it must stay inside the trash folder.
            return record is not null && record.AssetId == assetId && record.FileName == Path.GetFileName(record.FileName)
                && Path.GetFileNameWithoutExtension(record.FileName) == assetId.ToString("N") ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Warning($"AetherFrame could not read the trash record for asset {assetId} ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>The later of creation and last write: a copied-in file keeps an old write time.</summary>
    private static DateTime GetAgeReference(string path)
    {
        var created = File.GetCreationTimeUtc(path);
        var written = File.GetLastWriteTimeUtc(path);
        return created > written ? created : written;
    }
}
