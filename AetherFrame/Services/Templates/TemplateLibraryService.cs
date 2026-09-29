using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Plates;

namespace AetherFrame.Services.Templates;

/// <summary>
/// The local Template Library: Built-In Templates (never persisted — see
/// <see cref="BuiltInTemplateCatalog"/>) plus every Template the player saved. Deliberately
/// independent of Dalamud and game state, like <see cref="PlateLibraryService"/>, whose Plate
/// storage this depends on one-way for Save-as-Template and Use-Template — never the reverse.
///
/// <para><b>Sources of truth.</b> Which Templates exist is always the Template files on disk;
/// there is no separate ordering index (Templates have no manual reorder). A Template's embedded
/// document keeps <c>ProfileDocument</c>'s own schema/migration exactly as a Plate's does; this
/// envelope versions itself independently (see <c>TemplateDocuments</c>).</para>
///
/// <para><b>Threading.</b> Operations are serialized and started through the supplied dispatcher,
/// like <see cref="PlateLibraryService"/>: only an operation's synchronous prefix runs on the
/// dispatcher's thread (the framework thread in game). The load's read/parse phase runs on the
/// thread pool, and everything after an operation's first file step — including
/// <see cref="Generation"/> changing — runs on whichever thread completes that step (the thread
/// pool in game). Query members are safe from any thread.</para>
/// </summary>
internal sealed class TemplateLibraryService
{
    private readonly object gate = new();
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly PlateStoragePaths paths;
    private readonly IPlateFileStore store;
    private readonly PlateLibraryService plateLibrary;
    private readonly IAetherFrameLog log;
    private readonly Func<DateTime> utcNow;
    private readonly Func<Func<Task>, Task> dispatch;
    private readonly OwnedOperations operations;

    /// <summary>An unreadable Template's problem when its content is damaged.</summary>
    internal const string DamagedTemplateProblem = "This Template's file is damaged and couldn't be read. It has been left untouched.";

    /// <summary>An unreadable Template's problem when its file couldn't be opened at all: likely intact.</summary>
    internal const string UnavailableTemplateProblem =
        "This Template's file couldn't be opened; another program may be using it. It has been left untouched. Restart the game to try again.";

    /// <summary>A Ready Template's note when its file held bytes that aren't valid text, as for Plates
    /// (see <c>PlateLibraryService.InvalidTextPlateProblem</c>).</summary>
    internal const string InvalidTextTemplateProblem =
        "Part of this Template's file isn't valid text, so some of its characters may not show correctly. The file is unchanged; AetherFrame keeps a copy of it in its Recovery folder before saving over it.";

    private readonly Dictionary<Guid, TemplateRecord> templates = new();

    // Template files whose bytes on disk must be copied to Recovery before anything is written over
    // them (see PreserveBeforeOverwrite): one read from the store's backup copy whose damaged
    // on-disk bytes could not be copied at load, and one read with bytes that aren't valid text.
    // Nothing is written over either until its copy succeeds.
    private readonly HashSet<string> unpreservedFiles = new(StringComparer.OrdinalIgnoreCase);

    private bool isLoaded;
    private bool loadFailed;
    private int generation;
    private IReadOnlyList<TemplateSummary>? orderedSummaries;

    internal TemplateLibraryService(
        PlateStoragePaths paths,
        IPlateFileStore store,
        PlateLibraryService plateLibrary,
        IAetherFrameLog? log = null,
        Func<DateTime>? utcNow = null,
        Func<Func<Task>, Task>? dispatch = null,
        OwnedOperations? operations = null)
    {
        this.paths = paths;
        this.store = store;
        this.plateLibrary = plateLibrary;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.dispatch = dispatch ?? (work => work());
        this.operations = operations ?? new OwnedOperations();
    }

    /// <summary>Whether the player's saved Templates have loaded. Built-in Templates never wait on
    /// this: they're usable while loading and after a failed load.</summary>
    internal bool IsLoaded
    {
        get { lock (gate) return isLoaded; }
    }

    /// <summary>The player's saved Templates couldn't be loaded (see <see cref="InitializeAsync"/>);
    /// only built-in Templates are listed and usable until AetherFrame loads again.</summary>
    internal bool LoadFailed
    {
        get { lock (gate) return loadFailed; }
    }

    /// <summary>Changes whenever anything visible in the Library changes (cheap cache key for UI).</summary>
    internal int Generation
    {
        get { lock (gate) return generation; }
    }

    // ---------------------------------------------------------------- queries (any thread)

    /// <summary>Built-in Templates first (catalog order), then user Templates newest-created-first.
    /// Cached until the next change.</summary>
    internal IReadOnlyList<TemplateSummary> GetOrderedTemplates()
    {
        lock (gate)
        {
            return orderedSummaries ??= BuildOrderedSummariesLocked();
        }
    }

    /// <summary>Case-insensitive search over Template names.</summary>
    internal IReadOnlyList<TemplateSummary> Search(string? query)
    {
        var all = GetOrderedTemplates();
        return string.IsNullOrWhiteSpace(query)
            ? all
            : all.Where(t => PlateSearch.Matches(query, t.DisplayName)).ToList();
    }

    internal TemplateSummary? FindTemplate(Guid templateId) => GetOrderedTemplates().FirstOrDefault(t => t.TemplateId == templateId);

    /// <summary>
    /// The saved state of a Template for read-only display (Preview, cards), or null when it
    /// can't be shown. Callers must never mutate it. For a built-in id, a fresh document is
    /// regenerated every call (<paramref name="starterForBuiltIn"/> only affects it); for a user
    /// Template, the exact content last saved. Never mutates anything.
    /// </summary>
    internal ProfileDocument? GetSavedDocument(Guid templateId, PlateStarterContent? starterForBuiltIn = null)
    {
        if (BuiltInTemplateCatalog.IsBuiltIn(templateId))
        {
            return BuiltInTemplateCatalog.CreateDocument(templateId, utcNow(), starterForBuiltIn);
        }

        lock (gate)
        {
            return templates.TryGetValue(templateId, out var record) && record.Status == TemplateStatus.Ready
                ? record.Template!.Document
                : null;
        }
    }

    // ---------------------------------------------------------------- load

    /// <summary>
    /// Loads every saved Template. Safe to run on every startup. One unreadable file never
    /// prevents the rest from loading. Never rewrites a Template (unlike Plates, Templates have no
    /// legacy format to migrate on disk and no index to rebuild); the only thing it may write is a
    /// Recovery copy of a damaged file the store served from its backup (see
    /// <see cref="LoadTemplateAsync"/>).
    /// </summary>
    /// <remarks>Loading only reads, so <paramref name="cancellationToken"/> (like shutdown) stops it
    /// between files with <see cref="OperationCanceledException"/>, leaving Templates unloaded. Any
    /// other failure (the Templates folder itself unreadable, say) is rethrown for the caller to log,
    /// after marking the load failed: saved Templates then refuse every change, and built-in
    /// Templates stay usable, so Create Plate always works.</remarks>
    internal Task InitializeAsync(CancellationToken cancellationToken = default) => RunExclusiveAsync(() => InitializeCoreAsync(cancellationToken));

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            unpreservedFiles.Clear();
        }

        List<TemplateRecord> loaded;
        try
        {
            // Reading and parsing every file is the whole cost of a load, so it runs on the thread
            // pool rather than inside the dispatcher's tick (see the class remarks on threading).
            loaded = await Task.Run(() => LoadTemplatesAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OperationAbandonedException)
        {
            lock (gate)
            {
                loadFailed = true;
                Changed();
            }

            throw;
        }

        lock (gate)
        {
            templates.Clear();
            foreach (var record in loaded)
            {
                templates[record.Id] = record;
            }

            isLoaded = true;
            loadFailed = false;
            Changed();
        }

        log.Information($"AetherFrame Template Library loaded: {loaded.Count} Template(s).");
    }

    private async Task<List<TemplateRecord>> LoadTemplatesAsync(CancellationToken cancellationToken)
    {
        var loaded = new List<TemplateRecord>();

        foreach (var path in store.ListFiles(paths.TemplatesDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Stopping.ThrowIfCancellationRequested();

            if (!PlateStoragePaths.TryParseTemplateFileName(path, out var templateId))
            {
                log.Warning($"AetherFrame ignored a file in the Templates folder that isn't a Template: {Path.GetFileName(path)}");
                continue;
            }

            // A built-in Template is never a file (see BuiltInTemplateCatalog): a file using one of
            // their ids would be listed as a second, unremovable copy of the built-in, so it's
            // ignored like any other file that isn't a Template, and left untouched.
            if (BuiltInTemplateCatalog.IsBuiltIn(templateId))
            {
                log.Warning($"AetherFrame ignored a file in the Templates folder that uses a built-in Template's id: {Path.GetFileName(path)}");
                continue;
            }

            loaded.Add(await LoadTemplateAsync(path, templateId).ConfigureAwait(false));
        }

        return loaded;
    }

    /// <summary>
    /// One Template's record from its file. A file the store had to serve from its backup copy is
    /// logged and its damaged on-disk bytes are kept under Recovery (a copy only: loading never
    /// rewrites the file, and the next save of that Template replaces it as usual). A read that
    /// unloading abandoned or canceled is not a damaged file, so it propagates instead of being
    /// recorded as unreadable.
    /// </summary>
    private async Task<TemplateRecord> LoadTemplateAsync(string path, Guid templateId)
    {
        try
        {
            var result = await ReadTemplateFileAsync(path).ConfigureAwait(false);
            if (result.RecoveredFromBackup)
            {
                log.Warning($"AetherFrame Template file {Path.GetFileName(path)} was damaged and was recovered from its backup copy.");
                KeepRecoveryCopy(path);
            }

            if (result.Status == TemplateStatus.NewerVersion)
            {
                var (name, created, modified) = TemplateDocuments.ReadDisplayFields(result.Raw!);
                log.Warning($"AetherFrame found a Template saved by a newer version ({templateId}); it is listed but won't be opened or changed.");
                return new TemplateRecord(templateId, TemplateStatus.NewerVersion, null, null, name ?? "Template",
                    created ?? DateTime.MinValue, modified ?? DateTime.MinValue, result.Problem);
            }

            // The file name is the Template's identity: a document whose own id disagrees (e.g. a
            // hand-copied file) is treated as the Template its file says, mirroring Plates.
            var template = result.Template!;
            if (template.TemplateId != templateId)
            {
                log.Warning($"AetherFrame Template file {templateId} declared a different id ({template.TemplateId}); using the file's id.");
                template.TemplateId = templateId;
                result.Raw![nameof(PlateTemplate.TemplateId)] = templateId;
            }

            if (result.RepairedValues)
            {
                log.Warning($"AetherFrame repaired values in Template {templateId} that no build writes; its file is left untouched until it is next saved.");
            }

            // Read as it is, never refused for its encoding, as for Plates: the file is only kept in
            // Recovery before it is next written over (see PreserveBeforeOverwrite).
            string? problem = null;
            if (result.HasInvalidBytes && !result.RecoveredFromBackup)
            {
                log.Warning($"AetherFrame read Template file {Path.GetFileName(path)}, which holds bytes that aren't valid text; they read as U+FFFD, as before. The file is unchanged, and a copy of it is kept in Recovery before anything writes over it.");
                lock (gate)
                {
                    unpreservedFiles.Add(path);
                }

                problem = InvalidTextTemplateProblem;
            }

            var rawJson = VersionedJson.Serialize(result.Raw!);
            return new TemplateRecord(templateId, TemplateStatus.Ready, rawJson, template, template.Name, template.CreatedAtUtc, template.UpdatedAtUtc, problem);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OperationAbandonedException)
        {
            // As for Plates: a file that merely couldn't be opened is likely intact, so it isn't called damaged.
            var damaged = !PlateLibraryService.IsUnopenable(ex);
            log.Error(ex, damaged
                ? $"AetherFrame could not read Template {templateId}; it is listed as unreadable and its file is left untouched."
                : $"AetherFrame could not open Template {templateId}; it is listed as unreadable for this session and its file is left untouched.");
            return new TemplateRecord(templateId, TemplateStatus.Unreadable, null, null, "Unreadable Template", DateTime.MinValue, DateTime.MinValue,
                damaged ? DamagedTemplateProblem : UnavailableTemplateProblem);
        }
    }

    /// <summary>
    /// Reads and migrates both axes of one Template file: the envelope (see
    /// <c>PersistenceSchemas.Template</c>) and, only if that's usable, the embedded document (see
    /// <c>PersistenceSchemas.ProfileDocument</c>). Either being newer than this build knows makes
    /// the whole Template <see cref="TemplateStatus.NewerVersion"/> — never opened, file left
    /// exactly as read. Invalid content throws inside the reader (matching
    /// <see cref="VersionedJson.ReadAsync{T}"/>'s convention) so a store with backups retries from
    /// its backup copy; a newer-version result deliberately does not throw, so it's never retried
    /// from a stale backup. A result the store had to retry for reports
    /// <see cref="TemplateFileReadResult.RecoveredFromBackup"/>, exactly as
    /// <see cref="VersionedJson.ReadAsync{T}"/> does.
    /// </summary>
    private async Task<TemplateFileReadResult> ReadTemplateFileAsync(string path)
    {
        TemplateFileReadResult? result = null;
        var attempts = 0;

        await store.ReadTextAsync(path, stored =>
        {
            // A second invocation is the store retrying from its backup after the first copy failed.
            result = ParseTemplateText(stored.Text, recoveredFromBackup: ++attempts > 1) with { HasInvalidBytes = stored.HasInvalidBytes };
        }).ConfigureAwait(false);

        return result ?? throw new InvalidDataException("Template could not be read.");
    }

    /// <summary>
    /// One Template file's text as the loader reads it: damage throws (so a store with backups
    /// retries from its backup copy), and a newer envelope or embedded document comes back as
    /// NewerVersion, untouched. Also how a write proves its text loads again before writing it.
    /// The text's encoding is never damage (see <c>VersionedJson.ReadAsync</c>).
    /// </summary>
    private static TemplateFileReadResult ParseTemplateText(string text, bool recoveredFromBackup)
    {
        if (JsonNode.Parse(text) is not JsonObject raw)
        {
            throw new InvalidDataException("Template is not a JSON object.");
        }

        var envelopeMigration = PersistenceSchemas.Template.Migrate(raw);
        if (envelopeMigration.Outcome == SchemaMigrationOutcome.NewerVersion)
        {
            return new TemplateFileReadResult(raw, null, TemplateStatus.NewerVersion,
                "This Template was saved by a newer version of AetherFrame. Update AetherFrame to open it.", recoveredFromBackup, false);
        }

        if (!envelopeMigration.IsUsable)
        {
            throw new InvalidDataException(envelopeMigration.Error ?? "Template is unreadable.");
        }

        if (raw[nameof(PlateTemplate.Document)] is not JsonObject documentRaw)
        {
            throw new InvalidDataException("Template has no embedded document.");
        }

        var documentMigration = PersistenceSchemas.ProfileDocument.Migrate(documentRaw);
        if (documentMigration.Outcome == SchemaMigrationOutcome.NewerVersion)
        {
            return new TemplateFileReadResult(raw, null, TemplateStatus.NewerVersion,
                "This Template's content was saved by a newer version of AetherFrame. Update AetherFrame to open it.", recoveredFromBackup, false);
        }

        if (!documentMigration.IsUsable)
        {
            throw new InvalidDataException(documentMigration.Error ?? "Template's content is unreadable.");
        }

        var template = TemplateDocuments.Materialize(raw, out var repairedValues);
        return new TemplateFileReadResult(raw, template, TemplateStatus.Ready, null, recoveredFromBackup, repairedValues);
    }

    // ---------------------------------------------------------------- operations

    /// <summary>
    /// Saves an independent Template from a Plate's last SAVED state (unsaved editor changes are
    /// never included). Always a brand-new Template (new id) — never overwrites an existing one,
    /// even by matching name. Scrubs the same identity fields <see cref="PlateDocuments.CreateDuplicate"/>
    /// already does for a duplicated Plate, including <see cref="ProfileDocument.OwnerContentId"/>.
    /// </summary>
    internal Task<Guid> SaveAsTemplateAsync(Guid sourcePlateId, string? requestedName) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var (sourceJson, plateName) = plateLibrary.GetSavedJsonForExport(sourcePlateId, "saved as a Template");

            var now = utcNow();
            string name;
            lock (gate)
            {
                name = requestedName is not null
                    ? (TemplateNaming.TryNormalizeName(requestedName, out var normalized, out var error) ? normalized : throw new TemplateLibraryException(error!))
                    : TemplateNaming.MakeUniqueName(plateName, templates.Values.Select(t => t.Name));
            }

            var embeddedRaw = PlateDocuments.CreateDuplicate(ParseObject(sourceJson), Guid.NewGuid(), name, now);
            var embeddedDocument = PlateDocuments.Materialize(embeddedRaw);

            var templateId = Guid.NewGuid();
            var template = new PlateTemplate
            {
                Version = PlateTemplate.CurrentSchemaVersion,
                TemplateId = templateId,
                Name = name,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Origin = new TemplateOrigin { Kind = TemplateOriginKind.SavedFromPlate },
                Document = embeddedDocument,
            };

            var record = PrepareTemplateWrite(templateId, TemplateDocuments.ToJson(template));
            await WriteTemplateAsync(record).ConfigureAwait(false);

            lock (gate)
            {
                templates[templateId] = record;
                Changed();
            }

            log.Information($"AetherFrame saved Plate {sourcePlateId} as Template {templateId}.");
            return templateId;
        });

    /// <summary>Renames a Template: trimmed, non-empty, duplicates allowed. Throws for a built-in id.</summary>
    internal Task RenameTemplateAsync(Guid templateId, string? newName) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            if (BuiltInTemplateCatalog.IsBuiltIn(templateId))
            {
                throw new TemplateLibraryException("Built-in Templates can't be renamed.");
            }

            if (!TemplateNaming.TryNormalizeName(newName, out var name, out var error))
            {
                throw new TemplateLibraryException(error!);
            }

            var now = utcNow();
            JsonObject renamed;
            lock (gate)
            {
                var record = RequireReadyLocked(templateId, "renamed");
                renamed = ParseObject(record.RawJson!);
                TemplateDocuments.SetName(renamed, name, now);
            }

            var updated = PrepareTemplateWrite(templateId, renamed);
            await WriteTemplateAsync(updated).ConfigureAwait(false);

            lock (gate)
            {
                if (templates.ContainsKey(templateId))
                {
                    templates[templateId] = updated;
                }

                Changed();
            }
        });

    /// <summary>
    /// An independent copy of a user Template under a new identity: fresh Template Guid, the
    /// source's creative content unchanged (same managed asset references — never duplicated
    /// bytes), never a Plate. Never mutates the original. Throws for a built-in id.
    /// </summary>
    internal Task<Guid> DuplicateTemplateAsync(Guid sourceTemplateId) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            if (BuiltInTemplateCatalog.IsBuiltIn(sourceTemplateId))
            {
                throw new TemplateLibraryException("Built-in Templates can't be duplicated.");
            }

            var now = utcNow();
            var newId = Guid.NewGuid();
            JsonObject copy;
            lock (gate)
            {
                var source = RequireReadyLocked(sourceTemplateId, "duplicated");
                var name = TemplateNaming.MakeCopyName(source.Name, templates.Values.Select(t => t.Name));
                copy = TemplateDocuments.CreateDuplicate(ParseObject(source.RawJson!), newId, name, now);
            }

            var record = PrepareTemplateWrite(newId, copy);
            await WriteTemplateAsync(record).ConfigureAwait(false);

            lock (gate)
            {
                templates[newId] = record;
                Changed();
            }

            log.Information($"AetherFrame duplicated Template {sourceTemplateId} as {newId}.");
            return newId;
        });

    /// <summary>
    /// Deletes a user Template: moved to the Template trash (kept, never destroyed). Image assets
    /// are never touched — the same as deleting a Plate. Throws for a built-in id.
    /// </summary>
    internal Task DeleteTemplateAsync(Guid templateId) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            if (BuiltInTemplateCatalog.IsBuiltIn(templateId))
            {
                throw new TemplateLibraryException("Built-in Templates can't be deleted.");
            }

            lock (gate)
            {
                if (!templates.ContainsKey(templateId))
                {
                    throw new TemplateLibraryException("That Template no longer exists.");
                }
            }

            var now = utcNow();
            var path = paths.GetTemplatePath(templateId);
            if (store.FileExists(path))
            {
                store.MoveFile(path, paths.GetTrashTemplatePath(templateId, now));
            }

            lock (gate)
            {
                templates.Remove(templateId);
                Changed();
            }

            log.Information($"AetherFrame deleted Template {templateId} (moved to the Template trash).");
        });

    /// <summary>
    /// Use Template: creates a brand-new, independent Plate from a Template's saved content (a
    /// built-in's is regenerated fresh; a user Template's is exactly what was last saved — taken
    /// from the saved JSON like every other Library copy, so unknown data is kept by construction
    /// and the shared document <see cref="GetSavedDocument"/> hands out for display plays no part).
    /// The Template itself — built-in or user — is only ever read here, never mutated. See
    /// <see cref="PlateLibraryService.CreatePlateFromTemplateAsync"/> for the guarantees this
    /// relies on (fresh Plate Guid, no character binding copied, existing Active-Plate rules
    /// unchanged, shared asset ids with no bytes duplicated).
    /// </summary>
    internal Task<PlateCreationResult> InstantiateAsync(Guid templateId, CharacterContext? character, PlateStarterContent? starterForBuiltIn = null) =>
        RunExclusiveAsync(async () =>
        {
            JsonObject rawDocument;
            string name;

            // A built-in Template is generated, never read from disk, so it's usable however the
            // saved Templates' load went — Create Plate never depends on it.
            if (BuiltInTemplateCatalog.IsBuiltIn(templateId))
            {
                var definition = BuiltInTemplateCatalog.Find(templateId)!;
                var document = BuiltInTemplateCatalog.CreateDocument(templateId, utcNow(), starterForBuiltIn);
                rawDocument = PlateDocuments.ToJson(document);
                name = definition.Name;
            }
            else
            {
                RequireLoaded();
                lock (gate)
                {
                    var record = RequireReadyLocked(templateId, "used");
                    rawDocument = EmbeddedDocument(ParseObject(record.RawJson!));
                    name = record.Name;
                }
            }

            return await plateLibrary.CreatePlateFromTemplateAsync(rawDocument, name, character).ConfigureAwait(false);
        });

    /// <summary>
    /// Every asset referenced by any Template — built-in (currently none, but scanned for
    /// symmetry) and user, including trashed ones (restorable, so their images stay protected).
    /// Incomplete — and so unusable for cleanup — if any Template can't be read. This is only HALF
    /// of what's actually live: see <c>LiveAssetReferences.ComputeAsync</c> for the authoritative
    /// union with <see cref="PlateLibraryService.ScanAssetReferencesAsync"/>.
    /// </summary>
    internal Task<AssetReferenceScan> ScanAssetReferencesAsync() =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var referenced = new HashSet<Guid>();
            var problems = new List<string>();

            foreach (var definition in BuiltInTemplateCatalog.All)
            {
                AssetReferenceScanner.Collect(BuiltInTemplateCatalog.CreateDocument(definition.TemplateId, utcNow(), null), referenced);
            }

            List<TemplateRecord> snapshot;
            HashSet<string> unpreserved;
            lock (gate)
            {
                snapshot = templates.Values.ToList();
                unpreserved = new HashSet<string>(unpreservedFiles, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var record in snapshot)
            {
                if (record.Status == TemplateStatus.Ready)
                {
                    CollectTemplate(record.Template!, referenced);
                }
                else
                {
                    problems.Add($"Template {record.Id} is {record.Status}.");
                }
            }

            // What is in the Templates folder now, as for Plates (see PlateLibraryService's scan): a
            // Template put back from the trash by hand, a file whose name isn't a Template's, one
            // using a built-in Template's id (never loaded), or a loaded one whose file isn't what
            // memory holds and has no Recovery copy yet. Every GUID string in such a file counts;
            // one that can't be read leaves the scan incomplete.
            var loaded = snapshot.Select(r => r.Id).ToHashSet();
            foreach (var path in store.ListFiles(paths.TemplatesDirectory, "*.json"))
            {
                if (PlateStoragePaths.TryParseTemplateFileName(path, out var fileId) && loaded.Contains(fileId) && !unpreserved.Contains(path))
                {
                    continue;
                }

                try
                {
                    // As for Plates: a second run is the store's backup copy, which may be older.
                    var runs = 0;
                    await store.ReadTextAsync(path, text =>
                    {
                        if (++runs > 1)
                        {
                            throw new InvalidDataException("Only its backup copy could be read.");
                        }

                        using var json = JsonDocument.Parse(text.Text);
                        AssetReferenceScanner.CollectAllGuidStrings(json.RootElement, referenced);
                    }).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OperationAbandonedException)
                {
                    log.Error(ex, $"AetherFrame could not read {Path.GetFileName(path)} in the Templates folder while scanning for image references.");
                    problems.Add($"Template file {Path.GetFileName(path)} is unreadable ({ex.GetType().Name}).");
                }
            }

            foreach (var path in store.ListFiles(paths.TemplateTrashDirectory, "*.json"))
            {
                try
                {
                    var result = await ReadTemplateFileAsync(path).ConfigureAwait(false);
                    if (result.Status == TemplateStatus.Ready)
                    {
                        CollectTemplate(result.Template!, referenced);
                    }
                    else
                    {
                        problems.Add($"Trashed Template {Path.GetFileName(path)} was saved by a newer version.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OperationAbandonedException)
                {
                    // Only the failure's kind goes into the problem (it may reach the player); the
                    // exception itself, whose message may name the file's path, goes to the log.
                    log.Error(ex, $"AetherFrame could not read trashed Template {Path.GetFileName(path)} while scanning for image references.");
                    problems.Add($"Trashed Template {Path.GetFileName(path)} is unreadable ({ex.GetType().Name}).");
                }
            }

            return new AssetReferenceScan(problems.Count == 0, referenced, problems);
        });

    /// <summary>A Template's embedded document plus the envelope's own preserved unknown data,
    /// where a newer build may keep an image (a cover, say) this build can't know about.</summary>
    private static void CollectTemplate(PlateTemplate template, ISet<Guid> into)
    {
        AssetReferenceScanner.Collect(template.Document, into);
        AssetReferenceScanner.CollectUnknown(template.ExtensionData, into);
        AssetReferenceScanner.CollectUnknown(template.Origin?.ExtensionData, into);
    }

    // ---------------------------------------------------------------- internals

    private async Task RunExclusiveAsync(Func<Task> work) =>
        await RunExclusiveAsync<bool>(async () =>
        {
            await work().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

    /// <summary>One operation at a time, and shutdown-aware, exactly as <see cref="PlateLibraryService"/>'s.</summary>
    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> work)
    {
        try
        {
            await operationLock.WaitAsync(operations.Stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw Closing();
        }

        try
        {
            if (!operations.TryBegin(out var operation))
            {
                throw Closing();
            }

            using (operation)
            {
                T result = default!;
                await dispatch(async () =>
                {
                    operations.ThrowIfAbandoned();
                    result = await work().ConfigureAwait(false);
                }).ConfigureAwait(false);
                return result;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    private static TemplateLibraryException Closing() => new("AetherFrame is closing, so nothing was changed.");

    private void RequireLoaded()
    {
        lock (gate)
        {
            if (loadFailed)
            {
                throw new TemplateLibraryException("Your saved Templates couldn't be loaded, so they can't be used or changed right now. Built-in Templates still work.");
            }

            if (!isLoaded)
            {
                throw new TemplateLibraryException("Templates are still loading.");
            }
        }
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private TemplateRecord RequireReadyLocked(Guid templateId, string action)
    {
        if (!templates.TryGetValue(templateId, out var record))
        {
            throw new TemplateLibraryException("That Template no longer exists.");
        }

        return record.Status switch
        {
            TemplateStatus.Ready => record,
            TemplateStatus.NewerVersion => throw new TemplateLibraryException($"This Template was saved by a newer version of AetherFrame and can't be {action}."),
            _ when record.Problem == UnavailableTemplateProblem => throw new TemplateLibraryException($"This Template's file couldn't be opened, so it can't be {action}. Restart the game to try again."),
            _ => throw new TemplateLibraryException($"This Template's file is damaged and it can't be {action}."),
        };
    }

    /// <summary>Writes exactly the text <see cref="PrepareTemplateWrite"/> proved readable.</summary>
    private async Task WriteTemplateAsync(TemplateRecord record)
    {
        var path = paths.GetTemplatePath(record.Id);
        PreserveBeforeOverwrite(path);
        await store.WriteTextAsync(path, record.RawJson!).ConfigureAwait(false);
    }

    /// <summary>
    /// Before any write to <paramref name="path"/>: if its on-disk bytes still have no Recovery
    /// copy (a damaged file whose copy failed at load, see <see cref="KeepRecoveryCopy"/>, or a
    /// file read with bytes that aren't valid text), keeps one now, and refuses the write when that
    /// fails, so what may be the newest content, or the only faithful copy, is never replaced uncopied.
    /// </summary>
    private void PreserveBeforeOverwrite(string path)
    {
        lock (gate)
        {
            if (!unpreservedFiles.Contains(path))
            {
                return;
            }
        }

        try
        {
            if (store.FileExists(path))
            {
                var destination = paths.GetRecoveryPath(path, utcNow());
                store.CopyFile(path, destination);
                log.Warning($"AetherFrame kept a copy of the Template file {Path.GetFileName(path)} in its Recovery folder as {Path.GetFileName(destination)} before writing over it.");
            }
        }
        catch (Exception ex) when (ex is not OperationAbandonedException)
        {
            log.Error(ex, $"AetherFrame did not write the Template file {Path.GetFileName(path)}: a copy of it couldn't be kept in Recovery first.");
            throw new TemplateLibraryException(PlateLibraryService.UnpreservedDamagedFileMessage);
        }

        lock (gate)
        {
            unpreservedFiles.Remove(path);
        }
    }

    /// <summary>
    /// Copies the damaged on-disk bytes of <paramref name="path"/> under Recovery (never
    /// overwriting anything there) so the next write of that Template, which replaces them,
    /// destroys nothing the player might still want. A failure is logged and the load goes on,
    /// since the Template itself was read fine, but the path is remembered and never written over
    /// until a copy succeeds (see <see cref="PreserveBeforeOverwrite"/>); only abandonment by
    /// unloading propagates.
    /// </summary>
    private void KeepRecoveryCopy(string path)
    {
        try
        {
            if (!store.FileExists(path))
            {
                return;
            }

            var destination = paths.GetRecoveryPath(path, utcNow());
            store.CopyFile(path, destination);
            log.Warning($"AetherFrame kept a copy of the damaged Template file {Path.GetFileName(path)} in its Recovery folder as {Path.GetFileName(destination)}.");
        }
        catch (Exception ex) when (ex is not OperationAbandonedException)
        {
            log.Error(ex, $"AetherFrame couldn't keep a Recovery copy of the damaged Template file {Path.GetFileName(path)}; the Template still loaded from its backup copy, and its file won't be written over until a copy can be kept.");
            lock (gate)
            {
                unpreservedFiles.Add(path);
            }
        }
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private IReadOnlyList<TemplateSummary> BuildOrderedSummariesLocked()
    {
        var builtIns = BuiltInTemplateCatalog.All.Select(d =>
            new TemplateSummary(d.TemplateId, TemplateKind.BuiltIn, TemplateStatus.Ready, d.Name, DateTime.MinValue, DateTime.MinValue, null, false, d.SupportsPreview));

        var userTemplates = templates.Values
            .OrderByDescending(r => r.CreatedUtc)
            .ThenBy(r => r.Id)
            .Select(r => new TemplateSummary(r.Id, TemplateKind.UserSaved, r.Status, r.Name, r.CreatedUtc, r.ModifiedUtc, r.Problem,
                r.Template?.Document.HasUnsupportedElements ?? false, SupportsPreview: true));

        return builtIns.Concat(userTemplates).ToList();
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private void Changed()
    {
        generation++;
        orderedSummaries = null;
    }

    /// <summary>
    /// The record of a Template about to be written, holding the exact text the write puts on disk
    /// — proven first to load again as a Ready Template through the loader's own reader
    /// (<see cref="ParseTemplateText"/>), so a write that couldn't be read back is refused before
    /// anything is written, as for Plates.
    /// </summary>
    private TemplateRecord PrepareTemplateWrite(Guid templateId, JsonObject raw)
    {
        var json = VersionedJson.Serialize(raw);
        try
        {
            var parsed = ParseTemplateText(VersionedJson.RequireFaithfulReadBack(json, "Template"), recoveredFromBackup: false);
            if (parsed.Status != TemplateStatus.Ready)
            {
                throw new InvalidDataException("It would read as a newer version's Template.");
            }

            var template = parsed.Template!;
            return new TemplateRecord(templateId, TemplateStatus.Ready, json, template, template.Name, template.CreatedAtUtc, template.UpdatedAtUtc, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OperationAbandonedException)
        {
            log.Error(ex, $"AetherFrame did not write Template {templateId}: what it would have written couldn't be read back.");
            throw new TemplateLibraryException(PlateLibraryService.UnloadableWriteMessage);
        }
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Template JSON is not an object.");

    /// <summary>The embedded document of a saved (so already validated) Template envelope.</summary>
    private static JsonObject EmbeddedDocument(JsonObject envelope) =>
        envelope[nameof(PlateTemplate.Document)] as JsonObject ?? throw new JsonException("Template JSON has no embedded document.");

    /// <summary>
    /// One file as read. <see cref="RecoveredFromBackup"/>: the store served its backup copy
    /// because the on-disk one was unusable. <see cref="RepairedValues"/>: materializing repaired
    /// content no build writes (see <see cref="TemplateDocuments.Materialize(JsonObject, out bool)"/>).
    /// <see cref="HasInvalidBytes"/>: the copy read held bytes that aren't valid text (see
    /// <see cref="StoredText.HasInvalidBytes"/>).
    /// </summary>
    private readonly record struct TemplateFileReadResult(
        JsonObject? Raw,
        PlateTemplate? Template,
        TemplateStatus Status,
        string? Problem,
        bool RecoveredFromBackup,
        bool RepairedValues,
        bool HasInvalidBytes = false);

    /// <summary>
    /// One Template as loaded. Immutable apart from replacement on rename: the saved JSON is kept
    /// as a string (safe to read from any thread) and every change replaces the record.
    /// </summary>
    private sealed record TemplateRecord(
        Guid Id,
        TemplateStatus Status,
        string? RawJson,
        PlateTemplate? Template,
        string Name,
        DateTime CreatedUtc,
        DateTime ModifiedUtc,
        string? Problem);
}
