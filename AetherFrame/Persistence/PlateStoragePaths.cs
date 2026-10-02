using System;
using System.Globalization;
using System.IO;

namespace AetherFrame.Persistence;

/// <summary>
/// Where everything lives under the plugin's config directory. Plate documents stay exactly
/// where single-profile documents always were ("Profiles/{guid}.json"), and character bindings
/// in "Characters/{contentId}.json", so migration never moves user data.
/// </summary>
internal sealed class PlateStoragePaths
{
    internal PlateStoragePaths(string rootDirectory)
    {
        Root = rootDirectory;
        PlatesDirectory = Path.Combine(rootDirectory, "Profiles");
        CharactersDirectory = Path.Combine(rootDirectory, "Characters");
        LibraryDirectory = Path.Combine(rootDirectory, "Library");
        LibraryFile = Path.Combine(LibraryDirectory, "library.json");
        PlateTrashDirectory = Path.Combine(rootDirectory, "Trash", "Plates");
        RecoveryDirectory = Path.Combine(rootDirectory, "Recovery");
        MigrationBackupDirectory = Path.Combine(rootDirectory, "Backups", "pre-plate-library");
        AssetsDirectory = Path.Combine(rootDirectory, "assets");
        AssetStagingDirectory = Path.Combine(rootDirectory, "asset-staging");
        AssetMetadataDirectory = Path.Combine(rootDirectory, "asset-metadata");
        AssetTrashDirectory = Path.Combine(rootDirectory, "asset-trash");
        ThumbnailsDirectory = Path.Combine(rootDirectory, "thumbnails");
        PackageStagingDirectory = Path.Combine(rootDirectory, "package-staging");
        TemplatesDirectory = Path.Combine(rootDirectory, "Templates");
        TemplateTrashDirectory = Path.Combine(rootDirectory, "Trash", "Templates");
        TemplateThumbnailsDirectory = Path.Combine(rootDirectory, "thumbnails", "templates");
        DraftsDirectory = Path.Combine(rootDirectory, "Drafts");
        DraftTrashDirectory = Path.Combine(rootDirectory, "Trash", "Drafts");
    }

    internal string Root { get; }

    /// <summary>Plate documents, one "{guid}.json" per Plate.</summary>
    internal string PlatesDirectory { get; }

    internal string CharactersDirectory { get; }

    internal string LibraryDirectory { get; }

    internal string LibraryFile { get; }

    /// <summary>Deleted Plates, moved here intact (never destroyed).</summary>
    internal string PlateTrashDirectory { get; }

    /// <summary>Copies of damaged files, taken before anything is written over them.</summary>
    internal string RecoveryDirectory { get; }

    /// <summary>Untouched copies of pre-Plate-Library character bindings.</summary>
    internal string MigrationBackupDirectory { get; }

    /// <summary>Managed image assets ("{guid:N}.{ext}"), unchanged from before.</summary>
    internal string AssetsDirectory { get; }

    internal string AssetStagingDirectory { get; }

    internal string AssetMetadataDirectory { get; }

    internal string AssetTrashDirectory { get; }

    internal string ThumbnailsDirectory { get; }

    /// <summary>
    /// Private scratch space for validating .aetherframe files: one folder per import, deleted when
    /// the import finishes or is cancelled. Never user data; leftovers are swept at startup.
    /// </summary>
    internal string PackageStagingDirectory { get; }

    /// <summary>Template envelopes, one "{guid}.json" per Template. Never mixed with Plate
    /// identity — a physically separate directory tree.</summary>
    internal string TemplatesDirectory { get; }

    /// <summary>Deleted user Templates, moved here intact (never destroyed). Built-in Templates
    /// are never persisted, so they never appear here.</summary>
    internal string TemplateTrashDirectory { get; }

    /// <summary>Separate from <see cref="ThumbnailsDirectory"/> so a Template id and a Plate id
    /// (drawn from the same Guid space) can never collide on the same cache file name.</summary>
    internal string TemplateThumbnailsDirectory { get; }

    /// <summary>
    /// An editor's unsaved changes, kept when AetherFrame unloaded (see <c>DraftStore</c>): one
    /// write-once file per unload, never inside a Plate. Kept apart from the Plates folder, whose every
    /// Guid-named file is loaded as a Plate, and from Recovery, which holds only damaged files' copies.
    /// </summary>
    internal string DraftsDirectory { get; }

    /// <summary>Kept changes that were answered or retired, moved here intact (never destroyed).</summary>
    internal string DraftTrashDirectory { get; }

    internal string GetPlatePath(Guid plateId) => Path.Combine(PlatesDirectory, $"{plateId}.json");

    internal string GetBindingPath(ulong contentId) => Path.Combine(CharactersDirectory, $"{contentId.ToString(CultureInfo.InvariantCulture)}.json");

    internal string GetTrashPlatePath(Guid plateId, DateTime deletedUtc) =>
        Path.Combine(PlateTrashDirectory, $"{plateId}.deleted-{Stamp(deletedUtc)}.json");

    /// <summary>A recovery copy of <paramref name="originalPath"/>: same name, stamped.</summary>
    internal string GetRecoveryPath(string originalPath, DateTime nowUtc) =>
        Path.Combine(RecoveryDirectory, $"{Path.GetFileNameWithoutExtension(originalPath)}.damaged-{Stamp(nowUtc)}{Path.GetExtension(originalPath)}");

    /// <summary>
    /// The Plate a file in the Plates folder is named for — only when the name is the exact
    /// spelling <see cref="GetPlatePath"/> writes (hex with dashes; letter case is ignored, as
    /// Windows file names ignore it). Any other spelling of a Guid (no dashes, braces, padding)
    /// is not a Plate file: it would claim an id every write targets under the canonical name.
    /// </summary>
    internal static bool TryParsePlateFileName(string path, out Guid plateId) =>
        TryParseCanonicalGuid(Path.GetFileNameWithoutExtension(path), out plateId);

    /// <summary>The character a binding file is named for — only when the name is the exact
    /// spelling <see cref="GetBindingPath"/> writes (decimal digits, no leading zeros).</summary>
    internal static bool TryParseBindingFileName(string path, out ulong contentId)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return ulong.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out contentId)
            && contentId != 0
            && contentId.ToString(CultureInfo.InvariantCulture) == name;
    }

    internal string GetTemplatePath(Guid templateId) => Path.Combine(TemplatesDirectory, $"{templateId}.json");

    internal string GetTrashTemplatePath(Guid templateId, DateTime deletedUtc) =>
        Path.Combine(TemplateTrashDirectory, $"{templateId}.deleted-{Stamp(deletedUtc)}.json");

    /// <summary>The Template a file in the Templates folder is named for; see <see cref="TryParsePlateFileName"/>.</summary>
    internal static bool TryParseTemplateFileName(string path, out Guid templateId) =>
        TryParseCanonicalGuid(Path.GetFileNameWithoutExtension(path), out templateId);

    /// <summary>
    /// Where one unload keeps a Plate's unsaved changes: "{plateId}.unsaved-{stamp}-{draftId}.json",
    /// the draft's own id in 32 hex digits. Unique to each write, so a draft is never written over: not
    /// by a later unload, not by another game client sharing the folder, and never answered from a
    /// backup copy Dalamud keeps of an earlier file under the same path.
    /// </summary>
    internal string GetDraftPath(Guid plateId, DateTime writtenUtc, Guid draftId) =>
        Path.Combine(DraftsDirectory, $"{plateId}.unsaved-{Stamp(writtenUtc)}-{draftId:N}.json");

    /// <summary>
    /// Where a draft goes once answered or retired: the trash, under the same (unique) name; or, for
    /// a <paramref name="number"/> from 2, that name numbered ("{name}.2.json"), for a draft whose name
    /// the trash has already (one the player copied back out of it).
    /// </summary>
    internal string GetDraftTrashPath(string draftPath, int number = 1) =>
        Path.Combine(DraftTrashDirectory, number == 1
            ? Path.GetFileName(draftPath)
            : $"{Path.GetFileNameWithoutExtension(draftPath)}.{number.ToString(CultureInfo.InvariantCulture)}{Path.GetExtension(draftPath)}");

    /// <summary>
    /// The Plate, time and draft a file in the Drafts folder is named for, only when the name is the
    /// exact spelling <see cref="GetDraftPath"/> writes (letter case ignored, as Windows ignores it).
    /// Anything else, a temporary file left by an interrupted write included, is not a draft.
    /// </summary>
    internal static bool TryParseDraftFileName(string path, out Guid plateId, out DateTime writtenUtc, out Guid draftId)
    {
        plateId = Guid.Empty;
        writtenUtc = default;
        draftId = Guid.Empty;

        const string marker = ".unsaved-";
        const int guidLength = 36;
        const int stampLength = 19;
        const int idLength = 32;
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length != guidLength + marker.Length + stampLength + 1 + idLength
            || string.Compare(name, guidLength, marker, 0, marker.Length, StringComparison.OrdinalIgnoreCase) != 0
            || name[guidLength + marker.Length + stampLength] != '-')
        {
            return false;
        }

        var stamp = name.Substring(guidLength + marker.Length, stampLength);
        var id = name[^idLength..];
        if (!TryParseCanonicalGuid(name[..guidLength], out plateId)
            || !DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out writtenUtc)
            || !Guid.TryParseExact(id, "N", out draftId)
            || draftId == Guid.Empty
            || !string.Equals(draftId.ToString("N"), id, StringComparison.OrdinalIgnoreCase))
        {
            plateId = Guid.Empty;
            writtenUtc = default;
            draftId = Guid.Empty;
            return false;
        }

        return true;
    }

    private const string StampFormat = "yyyyMMdd-HHmmss-fff";

    /// <summary>A file name's time stamp, in the Gregorian calendar whatever the player's culture
    /// (a Thai or Japanese calendar would otherwise put another year in the name).</summary>
    private static string Stamp(DateTime utc) => utc.ToString(StampFormat, CultureInfo.InvariantCulture);

    // "D" is the dashed spelling; the comparison rejects what Guid parsing still tolerates around it (whitespace).
    private static bool TryParseCanonicalGuid(string name, out Guid id) =>
        Guid.TryParseExact(name, "D", out id)
        && id != Guid.Empty
        && string.Equals(id.ToString("D"), name, StringComparison.OrdinalIgnoreCase);
}
