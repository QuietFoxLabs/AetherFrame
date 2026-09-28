using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;

namespace AetherFrame.Services.Packages;

/// <summary>
/// Commits a validated <see cref="StagedPackage"/> as a new, independent Plate — the only way
/// package content enters the installation.
///
/// <para><b>Transaction.</b> Images first, each copied into managed storage under the brand-new
/// id the package was already re-pointed at; then the Plate document, whose write is the commit
/// point; only then does the Plate appear in the Library. Anything failing before the commit point
/// rolls back exactly the image files this import created (their ids were minted here, so no
/// pre-existing file can be among them) and leaves the Library as it was. Images already in storage
/// are never overwritten, reused, or deleted. The one exception to the rollback: a document write
/// that reported failure after its file landed, and whose file then couldn't be moved away (a
/// failed move, or unloading stopping the operation first). That document is a whole Plate the
/// next load lists, so its images stay with it (see <see cref="DocumentRemains"/>): a failure can
/// leave an import finished later, never a Plate pointing at images that are gone.</para>
///
/// <para><b>Identity.</b> Every import is a new Plate: a fresh Plate id (never the package's),
/// created and modified "now", revision 0, no legacy character owner. It's associated with no
/// character and is nobody's Active Plate — the player can Set Active afterwards like any other
/// Plate. The authored name is kept as it is; duplicate names are allowed in the Library.</para>
/// </summary>
internal static class PackageImporter
{
    /// <param name="continuesOwnedOperation">The caller registered this import as an owned
    /// operation already (see <c>PlatePackageService.ImportAsync</c>), so its Plate write is that
    /// operation's last step rather than a new one shutdown could refuse.</param>
    internal static async Task<PackageImportResult> ImportAsync(
        StagedPackage package, PlateLibraryService library, AssetStorageService assets, IAetherFrameLog? log = null, DateTime? nowUtc = null,
        bool continuesOwnedOperation = false)
    {
        log ??= NullAetherFrameLog.Instance;
        if (!package.CanImport || package.Summary is not { } summary)
        {
            return PackageImportResult.Failed(PackageErrorCode.CommitFailed, "This file can't be imported.");
        }

        var plateId = Guid.NewGuid();
        var created = new List<(Guid AssetId, string Path)>();
        try
        {
            foreach (var asset in package.Assets)
            {
                var path = assets.AddValidatedPackageImage(asset.StagedPath, asset.LocalAssetId, asset.Declaration.Sha256, Path.GetFileName(asset.Declaration.Path));
                created.Add((asset.LocalAssetId, path));
            }

            var raw = WithNewIdentity(package.PreparedProfile!, plateId, nowUtc ?? DateTime.UtcNow);
            await library.ImportPlateAsync(plateId, raw, continuesOwnedOperation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (DocumentRemains(library, plateId))
            {
                // Whole and listed at the next load: importing this package again would add it twice.
                package.MarkCommitted();
                log.Error(ex, $"AetherFrame could not finish importing {package.DescribeForLog()}, but its Plate file stayed in the Plates folder; its {created.Count} new image(s) are kept with it, and it is listed the next time AetherFrame loads.");
                return PackageImportResult.Failed(PackageErrorCode.CommitFailed, UnfinishedMessage, ex.GetType().Name);
            }

            log.Error(ex, $"AetherFrame could not import {package.DescribeForLog()}; rolling back {created.Count} new image(s).");
            RollBack(created, assets, log);
            var message = ex is PlateLibraryException refused ? refused.Message : "The Plate couldn't be imported. Nothing was changed.";
            return PackageImportResult.Failed(PackageErrorCode.CommitFailed, message, ex.GetType().Name);
        }

        package.MarkCommitted();
        return new PackageImportResult(true, plateId, summary.PlateName, null);
    }

    /// <summary>
    /// A copy of the prepared JSON with this import's new identity. Only identity and local
    /// history change; all creative content (known or not) is carried over exactly.
    /// </summary>
    internal static JsonObject WithNewIdentity(JsonObject prepared, Guid plateId, DateTime nowUtc)
    {
        var raw = (JsonObject)prepared.DeepClone();
        raw[nameof(ProfileDocument.ProfileId)] = plateId.ToString();
        raw[nameof(ProfileDocument.OwnerContentId)] = 0;
        raw[nameof(ProfileDocument.Revision)] = 0;
        raw[nameof(ProfileDocument.CreatedAtUtc)] = nowUtc;
        raw[nameof(ProfileDocument.UpdatedAtUtc)] = nowUtc;
        return raw;
    }

    /// <summary>
    /// Whether the import's Plate document is in the Plates folder although the import failed:
    /// its write reported failure after the file landed, and the Library couldn't move it away.
    /// Every write of it is an atomic replacement, so such a file is the whole document. Checked
    /// on the disk itself, not through the Library's store: the answer decides whether the images
    /// may go, even for an operation unloading has stopped.
    /// </summary>
    private static bool DocumentRemains(PlateLibraryService library, Guid plateId) =>
        File.Exists(library.Paths.GetPlatePath(plateId));

    /// <summary>What the player is told when the import failed after its Plate was written.</summary>
    internal const string UnfinishedMessage = "The import didn't finish, but the Plate was saved. It appears in My Plates the next time AetherFrame starts.";

    private static void RollBack(List<(Guid AssetId, string Path)> created, AssetStorageService assets, IAetherFrameLog log)
    {
        foreach (var (assetId, path) in created)
        {
            try
            {
                assets.RemoveImportedAsset(assetId, path);
            }
            catch (Exception ex)
            {
                // An unreferenced image is harmless (asset cleanup finds it); never escalate.
                log.Warning($"AetherFrame could not roll back an imported image ({ex.GetType().Name}).");
            }
        }
    }
}
