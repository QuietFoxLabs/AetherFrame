using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Characters;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Lifecycle;

namespace AetherFrame.Services.Plates;

/// <summary>
/// The local Plate Library: every saved Plate, their manual order, and each character's
/// associated and Active Plates. Deliberately independent of Dalamud and game state (the
/// character is always passed in), so all of it runs in tests against plain files.
///
/// <para><b>Sources of truth.</b> Which Plates exist is always the Plate documents on disk; the
/// library index (<see cref="PlateLibraryState"/>) only orders them and is rebuilt from the
/// documents when missing or damaged. Character associations and Active state live in
/// <see cref="CharacterBinding"/>s, never in documents.</para>
///
/// <para><b>Write ordering.</b> Every multi-file operation writes the Plate document first,
/// then character bindings, then the index — so an interruption can only ever leave a Plate
/// that the next startup finds and re-lists, never an index or binding pointing at nothing that
/// matters. Deleting moves the document to the trash FIRST for the same reason: afterwards a
/// stale reference is harmless and ignored.</para>
///
/// <para><b>Threading.</b> Operations are serialized and STARTED through the supplied dispatcher
/// (the framework thread in game): an operation's synchronous prefix — its checks and the JSON
/// work before its first file step — runs there, and everything after the first incomplete await
/// (further file steps, the in-memory updates, and the <see cref="PlateRenamed"/>,
/// <see cref="PlateDeleted"/> and <see cref="PlateSaved"/> events) continues on whichever thread
/// completed that step: the thread pool in game, where the store writes asynchronously. Loading
/// moves its whole read phase to the thread pool deliberately. Nothing here may therefore assume
/// the dispatcher's thread after an await, and event subscribers must be thread-safe. Query
/// members are safe from any thread, including ImGui Draw: they only read immutable snapshots
/// under a lock.</para>
///
/// <para><b>Shutdown.</b> Every operation is an <see cref="OwnedOperations"/> operation, so
/// unloading waits for the one running and none starts afterwards.</para>
/// </summary>
internal sealed class PlateLibraryService
{
    private readonly object gate = new();
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly PlateStoragePaths paths;
    private readonly IPlateFileStore store;
    private readonly IAetherFrameLog log;
    private readonly Func<DateTime> utcNow;
    private readonly Func<Func<Task>, Task> dispatch;
    private readonly OwnedOperations operations;

    private readonly Dictionary<Guid, PlateRecord> plates = new();
    private readonly Dictionary<ulong, CharacterBinding> bindings = new();

    // Bindings whose file exists but holds damaged content. Never overwritten without first keeping
    // a recovery copy (see WriteBindingAsync).
    private readonly HashSet<ulong> unreadableBindings = new();

    // Bindings whose file couldn't be read at all this session (locked, missing between listing
    // and reading, access denied, …): the content may be perfectly intact, so it is never written
    // over — its character's Plate settings can't be changed until the next load reads it.
    private readonly HashSet<ulong> unavailableBindings = new();

    // Bindings saved by a newer AetherFrame: never interpreted and never written by this build.
    private readonly HashSet<ulong> newerVersionBindings = new();

    // Files read from the store's backup copy whose damaged on-disk bytes could not be copied to
    // Recovery at load (a full disk, say). Those bytes may be newer than the backup, so nothing is
    // written over them until the copy succeeds (see PreserveBeforeOverwrite).
    private readonly HashSet<string> unpreservedDamagedFiles = new(StringComparer.OrdinalIgnoreCase);

    private PlateLibraryState library = new();

    // False when library.json must not be written this session — written by a newer build, or not
    // readable at load: ordering then works in memory but is never written over that file.
    private bool libraryWritable = true;

    private bool isLoaded;
    private int generation;
    private IReadOnlyList<PlateSummary>? orderedSummaries;
    private (int Generation, string Query, IReadOnlyList<PlateSummary> Result)? lastSearch;

    internal PlateLibraryService(
        PlateStoragePaths paths,
        IPlateFileStore store,
        IAetherFrameLog? log = null,
        Func<DateTime>? utcNow = null,
        Func<Func<Task>, Task>? dispatch = null,
        OwnedOperations? operations = null)
    {
        this.paths = paths;
        this.store = store;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.dispatch = dispatch ?? (work => work());
        this.operations = operations ?? new OwnedOperations();
    }

    /// <summary>Raised after a Plate is renamed, on whichever thread completed the write (see the class remarks on threading).</summary>
    internal event Action<Guid, string>? PlateRenamed;

    /// <summary>Raised after a Plate is deleted, on whichever thread completed its last file step (see the class remarks on threading).</summary>
    internal event Action<Guid>? PlateDeleted;

    /// <summary>Raised after a Plate's document is saved, on whichever thread completed the write (see the class remarks on threading).</summary>
    internal event Action<Guid>? PlateSaved;

    internal bool IsLoaded
    {
        get { lock (gate) return isLoaded; }
    }

    /// <summary>Changes whenever anything visible in the Library changes (cheap cache key for UI).</summary>
    internal int Generation
    {
        get { lock (gate) return generation; }
    }

    internal PlateStoragePaths Paths => paths;

    // ---------------------------------------------------------------- queries (any thread)

    /// <summary>Every known Plate in manual order. Cached until the next change.</summary>
    internal IReadOnlyList<PlateSummary> GetOrderedPlates()
    {
        lock (gate)
        {
            return orderedSummaries ??= BuildOrderedSummariesLocked();
        }
    }

    /// <summary>
    /// Case-insensitive search over Plate names and associated character names. Called every
    /// frame while a query is typed, so the last result is kept and returned again (the same
    /// instance; summaries are immutable) until the query or the Library changes.
    /// </summary>
    internal IReadOnlyList<PlateSummary> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return GetOrderedPlates();
        }

        var needle = query.Trim();
        lock (gate)
        {
            if (lastSearch is { } cached && cached.Generation == generation && string.Equals(cached.Query, needle, StringComparison.Ordinal))
            {
                return cached.Result;
            }

            var all = orderedSummaries ??= BuildOrderedSummariesLocked();
            var result = all.Where(p => PlateSearch.Matches(needle, p.DisplayName, p.CharacterNames)).ToList();
            lastSearch = (generation, needle, result);
            return result;
        }
    }

    internal PlateSummary? FindPlate(Guid plateId) => GetOrderedPlates().FirstOrDefault(p => p.PlateId == plateId);

    /// <summary>
    /// The saved state of a Plate for read-only display (Plate Viewer, cards), or null when it
    /// can't be shown. Callers must never mutate it; editing always gets its own copy via
    /// <see cref="OpenDocumentForEditing"/>.
    /// </summary>
    internal ProfileDocument? GetSavedDocument(Guid plateId)
    {
        lock (gate)
        {
            return plates.TryGetValue(plateId, out var record) ? record.Preview : null;
        }
    }

    /// <summary>
    /// A brand-new, independent document of a Plate's saved state to edit. Never shares anything
    /// with the Library's own copy. Throws <see cref="PlateLibraryException"/> when the Plate
    /// can't be opened.
    /// </summary>
    internal ProfileDocument OpenDocumentForEditing(Guid plateId)
    {
        string rawJson;
        lock (gate)
        {
            var record = RequireReadyLocked(plateId, "opened");
            rawJson = record.RawJson!;
        }

        return PlateDocuments.Materialize(ParseObject(rawJson));
    }

    /// <summary>
    /// A Plate's saved JSON exactly as stored (every field, including ones this build doesn't
    /// know), for export (or, with <paramref name="action"/> overridden, for another use that
    /// wants the same "exactly as saved" guarantee — e.g. Save as Template). Throws
    /// <see cref="PlateLibraryException"/> when the Plate can't be read.
    /// </summary>
    internal (string Json, string Name) GetSavedJsonForExport(Guid plateId, string action = "exported")
    {
        lock (gate)
        {
            var record = RequireReadyLocked(plateId, action);
            return (record.RawJson!, record.Name);
        }
    }

    /// <summary>The character's Active Plate, only if that Plate currently exists.</summary>
    internal Guid? GetActivePlateId(ulong contentId)
    {
        lock (gate)
        {
            return bindings.TryGetValue(contentId, out var binding) && binding.ActivePlateId is { } id && plates.ContainsKey(id)
                ? id
                : null;
        }
    }

    /// <summary>A copy of the character's binding (including a stale Active id, untouched), or null.</summary>
    internal CharacterBinding? GetBinding(ulong contentId)
    {
        lock (gate)
        {
            return bindings.TryGetValue(contentId, out var binding) ? binding.Clone() : null;
        }
    }

    // ---------------------------------------------------------------- load & migration

    /// <summary>
    /// Loads everything and brings it up to date. Safe to run on every startup: it only writes
    /// when something actually needs writing, and never duplicates Plates or bindings. One
    /// unreadable file never prevents the rest from loading.
    /// </summary>
    /// <remarks>
    /// <paramref name="cancellationToken"/> (like shutdown) only stops a load that hasn't written
    /// anything yet: it's honored while files are being read, never once migration writes begin.
    /// A canceled load throws <see cref="OperationCanceledException"/> and leaves the Library
    /// unloaded. A migration write that fails does not: everything was already read, so the
    /// Library loads and the write is retried at the next startup (a failed binding write also
    /// keeps the index unwritten, so that startup migrates again rather than believing it done).
    /// </remarks>
    internal Task InitializeAsync(CancellationToken cancellationToken = default) => RunExclusiveAsync(() => InitializeCoreAsync(cancellationToken));

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        // The read phase — every document read, parsed, migrated and re-serialized — runs on the
        // thread pool: the dispatcher (the framework thread in game) only starts the operation and
        // is free again at the first of these awaits, however large the Library is. The store has
        // no thread affinity, and the writes below keep their ordering on whatever thread the
        // reads finished on.
        ThrowIfLoadCanceled(cancellationToken);
        lock (gate)
        {
            unpreservedDamagedFiles.Clear();
        }

        var loadedPlates = await Task.Run(LoadPlatesAsync).ConfigureAwait(false);
        ThrowIfLoadCanceled(cancellationToken);
        var (loadedBindings, badBindings, missingBindings, newerBindings, migratedBindings) = await Task.Run(LoadBindingsAsync).ConfigureAwait(false);
        ThrowIfLoadCanceled(cancellationToken);

        lock (gate)
        {
            plates.Clear();
            foreach (var record in loadedPlates)
            {
                plates[record.Id] = record;
            }

            bindings.Clear();
            foreach (var binding in loadedBindings)
            {
                bindings[binding.ContentId] = binding;
            }

            unreadableBindings.Clear();
            unreadableBindings.UnionWith(badBindings);
            unavailableBindings.Clear();
            unavailableBindings.UnionWith(missingBindings);
            newerVersionBindings.Clear();
            newerVersionBindings.UnionWith(newerBindings);
            Changed();
        }

        var bindingsToWrite = new HashSet<ulong>();
        var writeLibrary = false;

        if (!store.FileExists(paths.LibraryFile))
        {
            // First run of the Plate Library over single-profile data (or a lost index).
            log.Information("AetherFrame is building the Plate Library from existing Plates.");
            BackUpLegacyBindings();

            bindingsToWrite.UnionWith(migratedBindings);
            bindingsToWrite.UnionWith(AssociateLegacyOwners());

            lock (gate)
            {
                library = new PlateLibraryState { OrderedPlateIds = PlateOrdering.BuildInitialOrder(ExistingPlatesLocked()) };
                libraryWritable = true;
            }

            writeLibrary = true;
        }
        else
        {
            writeLibrary = await Task.Run(LoadLibraryIndexAsync).ConfigureAwait(false);
        }

        lock (gate)
        {
            writeLibrary |= PlateOrdering.Reconcile(library.OrderedPlateIds, ExistingPlatesLocked());

            var missing = library.OrderedPlateIds.Count(id => !plates.ContainsKey(id));
            if (missing > 0)
            {
                log.Warning($"AetherFrame's Plate order mentions {missing} Plate(s) that no longer exist; they are skipped.");
            }
        }

        // Bindings before the index: the index is written last so its existence marks a
        // completed migration (see class doc). Every document is already in memory, so a failed
        // write here never fails the load — it only decides what the next startup retries.
        foreach (var contentId in bindingsToWrite)
        {
            CharacterBinding? binding;
            lock (gate)
            {
                bindings.TryGetValue(contentId, out binding);
            }

            if (binding is null)
            {
                continue;
            }

            try
            {
                await WriteBindingAsync(binding).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsInterruption(ex))
            {
                // Writing the index now would mark the migration complete with this character's
                // legacy associations still only in memory, so the index stays unwritten this
                // session and the next startup migrates again (idempotently).
                log.Error(ex, "AetherFrame loaded the Plate Library but could not write a character binding file; the migration will be retried at the next startup.");
                lock (gate)
                {
                    libraryWritable = false;
                }
            }
        }

        if (writeLibrary)
        {
            try
            {
                await WriteLibraryAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsInterruption(ex))
            {
                // Purely derived from the Plates: the next startup rebuilds and writes it again.
                log.Error(ex, "AetherFrame loaded the Plate Library but could not save the Plate order; it will be retried at the next startup.");
            }
        }

        lock (gate)
        {
            isLoaded = true;
            Changed();
        }

        log.Information($"AetherFrame Plate Library loaded: {loadedPlates.Count} Plate(s), {loadedBindings.Count} character binding(s).");
    }

    private async Task<List<PlateRecord>> LoadPlatesAsync()
    {
        var records = new List<PlateRecord>();

        foreach (var path in store.ListFiles(paths.PlatesDirectory, "*.json"))
        {
            if (!PlateStoragePaths.TryParsePlateFileName(path, out var plateId))
            {
                log.Warning($"AetherFrame ignored a file in the Plates folder that isn't a Plate: {Path.GetFileName(path)}");
                continue;
            }

            records.Add(await LoadPlateAsync(path, plateId).ConfigureAwait(false));
        }

        return records;
    }

    private async Task<PlateRecord> LoadPlateAsync(string path, Guid plateId)
    {
        try
        {
            var result = await VersionedJson.ReadAsync(store, path, PersistenceSchemas.ProfileDocument, PlateDocuments.Deserialize).ConfigureAwait(false);
            if (result.RecoveredFromBackup)
            {
                KeepRecoveredFile(path);
            }

            if (result.IsNewerVersion)
            {
                var (name, created, modified) = PlateDocuments.ReadDisplayFields(result.Raw!);
                log.Warning($"AetherFrame found a Plate saved by a newer version ({plateId}); it is listed but won't be opened or changed.");
                return new PlateRecord(plateId, PlateStatus.NewerVersion, null, null, name ?? "Plate", created ?? DateTime.MinValue, modified ?? DateTime.MinValue, 0, result.Migration.Version,
                    "This Plate was saved by a newer version of AetherFrame. Update AetherFrame to open it.");
            }

            // The file name is the Plate's identity: a document whose own id disagrees (e.g. a
            // hand-copied file) is treated as the Plate its file says, so saving it can never
            // overwrite a different Plate.
            var raw = result.Raw!;
            var document = result.Value!;
            if (document.ProfileId != plateId)
            {
                log.Warning($"AetherFrame Plate file {plateId} declared a different id ({document.ProfileId}); using the file's id.");
                document.ProfileId = plateId;
                raw[nameof(ProfileDocument.ProfileId)] = plateId;
            }

            // The preview gets the same in-memory repairs an opened document gets (see
            // PlateDocuments.Materialize); the saved JSON is kept as read, so nothing is written
            // over the file because of them until the player saves.
            PlateDocuments.ApplyLegacyRepairs(document, out var repairedValues);
            repairedValues |= document.NormalizeElementIds();
            if (repairedValues)
            {
                log.Warning($"AetherFrame repaired values in Plate {plateId} in memory (a value that isn't a number, or a missing or repeated element id); the file is unchanged until you save.");
            }

            return new PlateRecord(plateId, PlateStatus.Ready, VersionedJson.Serialize(raw), document, document.Name, document.CreatedAtUtc, document.UpdatedAtUtc,
                document.Revision, result.Migration.Version, null);
        }
        catch (Exception ex) when (!IsInterruption(ex))
        {
            log.Error(ex, $"AetherFrame could not read Plate {plateId}; it is listed as unreadable and its file is left untouched.");
            return new PlateRecord(plateId, PlateStatus.Unreadable, null, null, "Unreadable Plate", DateTime.MinValue, DateTime.MinValue, 0, 0,
                "This Plate's file is damaged and couldn't be read. It has been left untouched.");
        }
    }

    /// <summary>
    /// Reads every binding file. Damaged content (see <see cref="IsContentDamage"/>) lands in
    /// Unreadable; any other failure — the file may be intact but locked, gone, or inaccessible —
    /// in Unavailable, which this session never writes over.
    /// </summary>
    private async Task<(List<CharacterBinding> Loaded, List<ulong> Unreadable, List<ulong> Unavailable, List<ulong> Newer, List<ulong> Migrated)> LoadBindingsAsync()
    {
        var loaded = new List<CharacterBinding>();
        var unreadable = new List<ulong>();
        var unavailable = new List<ulong>();
        var newer = new List<ulong>();
        var migrated = new List<ulong>();

        foreach (var path in store.ListFiles(paths.CharactersDirectory, "*.json"))
        {
            if (!PlateStoragePaths.TryParseBindingFileName(path, out var contentId))
            {
                continue;
            }

            try
            {
                var result = await VersionedJson.ReadAsync<CharacterBinding>(store, path, PersistenceSchemas.CharacterBinding).ConfigureAwait(false);
                if (result.RecoveredFromBackup)
                {
                    KeepRecoveredFile(path);
                }

                if (result.IsNewerVersion)
                {
                    log.Warning("AetherFrame found a character binding file saved by a newer version; it is left untouched.");
                    newer.Add(contentId);
                    continue;
                }

                var binding = result.Value!;
                binding.ContentId = contentId;
                binding.PlateIds = binding.PlateIds.Where(id => id != Guid.Empty).Distinct().ToList();
                loaded.Add(binding);

                if (result.WasMigrated)
                {
                    migrated.Add(contentId);
                }
            }
            catch (Exception ex) when (IsContentDamage(ex))
            {
                log.Error(ex, "AetherFrame could not read a character binding file; it is left untouched.");
                unreadable.Add(contentId);
            }
            catch (Exception ex) when (!IsInterruption(ex))
            {
                log.Error(ex, "AetherFrame could not open a character binding file; this character's Plate settings are left untouched and can't be changed until the game is restarted.");
                unavailable.Add(contentId);
            }
        }

        return (loaded, unreadable, unavailable, newer, migrated);
    }

    /// <returns>True when the index needs (re)writing.</returns>
    private async Task<bool> LoadLibraryIndexAsync()
    {
        try
        {
            var result = await VersionedJson.ReadAsync<PlateLibraryState>(store, paths.LibraryFile, PersistenceSchemas.PlateLibrary).ConfigureAwait(false);
            if (result.RecoveredFromBackup)
            {
                KeepRecoveredFile(paths.LibraryFile);
            }

            if (result.IsNewerVersion)
            {
                log.Warning("AetherFrame's Plate order was saved by a newer version; it is used read-only and never overwritten.");
                lock (gate)
                {
                    library = new PlateLibraryState { OrderedPlateIds = ReadNewerOrder(result.Raw!) ?? PlateOrdering.BuildInitialOrder(ExistingPlatesLocked()) };
                    libraryWritable = false;
                }

                return false;
            }

            lock (gate)
            {
                library = result.Value!;
                library.OrderedPlateIds ??= new List<Guid>();
                libraryWritable = true;
            }

            return result.WasMigrated;
        }
        catch (Exception ex) when (IsContentDamage(ex))
        {
            log.Error(ex, "AetherFrame's Plate order is damaged; rebuilding it from the saved Plates (the damaged file is kept in Recovery).");

            // Without a Recovery copy the damaged file is never written over: the rebuilt order
            // then serves this session in memory only, like a newer-version index.
            bool preserved;
            try
            {
                PreserveDamagedFile(paths.LibraryFile);
                preserved = true;
            }
            catch (Exception copyFailure) when (!IsInterruption(copyFailure))
            {
                log.Error(copyFailure, "AetherFrame could not keep a copy of the damaged Plate order in Recovery; the file is left untouched for this session.");
                preserved = false;
            }

            lock (gate)
            {
                library = new PlateLibraryState { OrderedPlateIds = PlateOrdering.BuildInitialOrder(ExistingPlatesLocked()) };
                libraryWritable = preserved;
            }

            return preserved;
        }
        catch (Exception ex) when (!IsInterruption(ex))
        {
            // The file may be intact (locked, access denied, …): the order is rebuilt for this
            // session only and the file is never written over.
            log.Error(ex, "AetherFrame could not open its Plate order; the saved Plates are listed newest-first for this session and the file is left untouched.");
            lock (gate)
            {
                library = new PlateLibraryState { OrderedPlateIds = PlateOrdering.BuildInitialOrder(ExistingPlatesLocked()) };
                libraryWritable = false;
            }

            return false;
        }
    }

    /// <summary>Keeps an untouched copy of every pre-Plate-Library binding (once).</summary>
    private void BackUpLegacyBindings()
    {
        foreach (var path in store.ListFiles(paths.CharactersDirectory, "*.json"))
        {
            var backup = Path.Combine(paths.MigrationBackupDirectory, "Characters", Path.GetFileName(path));
            try
            {
                if (!store.FileExists(backup))
                {
                    store.CopyFile(path, backup);
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "AetherFrame could not back up a character binding file before migrating; continuing (the original is not modified destructively).");
            }
        }
    }

    /// <summary>
    /// Single-profile documents recorded the character they were made for. Any such Plate not
    /// already associated with that character is associated (never made Active). A character
    /// whose binding file exists but couldn't be read is skipped rather than overwritten.
    /// </summary>
    /// <returns>Characters whose bindings changed.</returns>
    private List<ulong> AssociateLegacyOwners()
    {
        var changed = new List<ulong>();
        var now = utcNow();

        lock (gate)
        {
            foreach (var record in plates.Values.Where(r => r.Status == PlateStatus.Ready).OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id))
            {
                var owner = record.Preview!.OwnerContentId;
                if (owner == 0 || unreadableBindings.Contains(owner) || unavailableBindings.Contains(owner) || newerVersionBindings.Contains(owner))
                {
                    continue;
                }

                if (!bindings.TryGetValue(owner, out var binding))
                {
                    binding = new CharacterBinding { ContentId = owner, CreatedAtUtc = now, UpdatedAtUtc = now };
                    bindings[owner] = binding;
                }

                if (!binding.PlateIds.Contains(record.Id))
                {
                    binding.PlateIds.Add(record.Id);
                    binding.UpdatedAtUtc = now;
                    changed.Add(owner);
                }
            }

            if (changed.Count > 0)
            {
                Changed();
            }
        }

        return changed;
    }

    // ---------------------------------------------------------------- operations

    /// <summary>
    /// Creates and saves a new Plate first in the Library. With a character, the Plate is
    /// associated with it — and becomes its Active Plate only if the character had no Plates yet.
    /// Without one, the Plate stays unbound.
    /// <paramref name="starter"/> is the layout's starter content (see <see cref="PlateFactory"/>); null
    /// creates the bare document.
    /// </summary>
    internal Task<PlateCreationResult> CreatePlateAsync(
        PlateStartingLayout layout, CharacterContext? character, string? name = null, PlateStarterContent? starter = null) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var now = utcNow();
            var plateId = Guid.NewGuid();
            string plateName;
            lock (gate)
            {
                plateName = ResolveNewName(name, PlateFactory.DefaultNameFor(layout));
            }

            var document = PlateFactory.Create(layout, plateId, plateName, now, starter);
            var raw = PlateDocuments.ToJson(document);
            var result = await InsertNewPlateAsync(plateId, raw, character, now).ConfigureAwait(false);
            log.Information($"AetherFrame created Plate {plateId} ({layout}).");
            return result;
        });

    /// <summary>
    /// Creates a brand-new, independent Plate from a Template's saved document (see
    /// <c>TemplateLibraryService.InstantiateAsync</c>, the only caller): a fresh Plate Guid and
    /// storage entry, no character binding beyond the usual new-Plate rules below, and — because
    /// <paramref name="templateDocumentRaw"/> is only ever read here via
    /// <see cref="PlateDocuments.CreateDuplicate"/> — the Template itself is never mutated and its
    /// own Guid is never reused as the new Plate's.
    /// </summary>
    internal Task<PlateCreationResult> CreatePlateFromTemplateAsync(JsonObject templateDocumentRaw, string plateName, CharacterContext? character) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var now = utcNow();
            var plateId = Guid.NewGuid();
            string uniqueName;
            lock (gate)
            {
                uniqueName = PlateNaming.MakeUniqueName(plateName, plates.Values.Select(p => p.Name));
            }

            var raw = PlateDocuments.CreateDuplicate(templateDocumentRaw, plateId, uniqueName, now);
            var result = await InsertNewPlateAsync(plateId, raw, character, now).ConfigureAwait(false);
            log.Information($"AetherFrame created Plate {plateId} from a Template.");
            return result;
        });

    /// <summary>
    /// The shared tail of every "add a brand-new Plate" operation: writes the document, inserts it
    /// first in the Library, and applies the usual character-association rules — associated with
    /// <paramref name="character"/> if given, and Active only if this is that character's very
    /// first Plate (never otherwise). Callers must already hold no lock and must have resolved
    /// <paramref name="raw"/>'s final content; this only ever writes and indexes it. Once the
    /// document is written the Plate exists whatever happens next: a failed binding write (or a
    /// binding this session can't write) is reported as a <see cref="PlateLibraryException"/> that
    /// says so, after the index was still attempted; a failed index write is only logged.
    /// </summary>
    private async Task<PlateCreationResult> InsertNewPlateAsync(Guid plateId, JsonObject raw, CharacterContext? character, DateTime now)
    {
        var record = PreparePlateWrite(plateId, raw);
        await WritePlateAsync(record).ConfigureAwait(false);

        lock (gate)
        {
            plates[plateId] = record;
            PlateOrdering.InsertAtFront(library.OrderedPlateIds, plateId);
            Changed();
        }

        var becameActive = false;
        string? linkFailure = null;
        if (character is { } who)
        {
            if (PrepareBindingForWrite(who, now, out var refusal) is { } binding)
            {
                lock (gate)
                {
                    var hadPlates = binding.PlateIds.Any(id => id != plateId && plates.ContainsKey(id));
                    if (!binding.PlateIds.Contains(plateId))
                    {
                        binding.PlateIds.Add(plateId);
                    }

                    // First Plate for this character: Active automatically. Never otherwise.
                    if (!hadPlates && (binding.ActivePlateId is null || !plates.ContainsKey(binding.ActivePlateId.Value)))
                    {
                        binding.ActivePlateId = plateId;
                        becameActive = true;
                    }
                }

                try
                {
                    await CommitBindingAsync(binding).ConfigureAwait(false);
                }
                catch (Exception ex) when (!IsInterruption(ex))
                {
                    // The write didn't land, so memory still holds the old binding (nothing Active
                    // changed); the Plate itself is on disk and listed.
                    becameActive = false;
                    log.Error(ex, $"AetherFrame created Plate {plateId} but could not associate it with a character.");
                    linkFailure = "The Plate was created, but it couldn't be linked to your character.";
                }
            }
            else if (refusal == BindingRefusal.Unavailable)
            {
                // A newer-version binding is skipped quietly (the Plate is still created, as ever);
                // an unavailable one is worth telling the player about, because a restart fixes it.
                linkFailure = "The Plate was created, but it couldn't be linked to your character. " + DescribeRefusal(refusal);
            }
        }

        try
        {
            await WriteLibraryAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsInterruption(ex))
        {
            // The Plate is saved and listed; only its position isn't, and startup re-lists it.
            log.Error(ex, $"AetherFrame created Plate {plateId} but could not save the Library order.");
        }

        if (linkFailure is not null)
        {
            // The Plate exists, is listed first, and is intact; only its character link is missing.
            throw new PlateLibraryException(linkFailure);
        }

        return new PlateCreationResult(plateId, becameActive);
    }

    /// <summary>
    /// Saves an independent copy of a Plate's SAVED state (unsaved editor changes are not part of
    /// it) directly after the source, named "Name Copy", as a brand-new Plate (new id, created and
    /// modified now). Same asset references, no image bytes copied. The copy is associated with
    /// exactly the characters the source is associated with — never with whoever happens to be
    /// logged in — and is never Active; an unbound source gives an unbound copy.
    /// </summary>
    internal Task<Guid> DuplicatePlateAsync(Guid sourcePlateId) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var now = utcNow();
            var newId = Guid.NewGuid();
            JsonObject copy;
            lock (gate)
            {
                var source = RequireReadyLocked(sourcePlateId, "duplicated");
                var name = PlateNaming.MakeCopyName(source.Name, plates.Values.Select(p => p.Name));
                copy = PlateDocuments.CreateDuplicate(ParseObject(source.RawJson!), newId, name, now);
            }

            var record = PreparePlateWrite(newId, copy);
            await WritePlateAsync(record).ConfigureAwait(false);

            lock (gate)
            {
                plates[newId] = record;
                PlateOrdering.InsertAfter(library.OrderedPlateIds, sourcePlateId, newId);
                Changed();
            }

            // The source's associations (Active counts as one), copied — Active status never is.
            List<CharacterBinding> associated;
            lock (gate)
            {
                associated = bindings.Values
                    .Where(b => b.PlateIds.Contains(sourcePlateId) || b.ActivePlateId == sourcePlateId)
                    .Select(b =>
                    {
                        var copy = b.Clone();
                        copy.PlateIds.Add(newId);
                        copy.UpdatedAtUtc = now;
                        return copy;
                    })
                    .ToList();
            }

            var failedAssociations = 0;
            foreach (var binding in associated)
            {
                try
                {
                    await CommitBindingAsync(binding).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failedAssociations++;
                    log.Error(ex, $"AetherFrame duplicated Plate {sourcePlateId} but could not associate the copy with a character.");
                }
            }

            try
            {
                await WriteLibraryAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsInterruption(ex))
            {
                // The copy is saved and listed; only its position isn't, and startup re-lists it.
                // Reporting a failure here would invite a retry, and so a second copy.
                log.Error(ex, $"AetherFrame duplicated Plate {sourcePlateId} but could not save the Library order.");
            }

            log.Information($"AetherFrame duplicated Plate {sourcePlateId} as {newId}.");

            if (failedAssociations > 0)
            {
                // The copy exists and is intact; only its character links are incomplete.
                throw new PlateLibraryException("The copy was made, but it couldn't be linked to every character that uses the original.");
            }

            return newId;
        });

    /// <summary>
    /// Renames a Plate: trimmed, non-empty, duplicates allowed. Only the name (and modified time)
    /// change — identity, associations, Active state, and content stay exactly as they were.
    /// Throws <see cref="PlateLibraryException"/> for an invalid name.
    /// </summary>
    internal Task RenamePlateAsync(Guid plateId, string? newName) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            if (!PlateNaming.TryNormalizeName(newName, out var name, out var error))
            {
                throw new PlateLibraryException(error!);
            }

            var now = utcNow();
            JsonObject renamed;
            lock (gate)
            {
                var record = RequireReadyLocked(plateId, "renamed");
                renamed = ParseObject(record.RawJson!);
                PlateDocuments.SetName(renamed, name, now);
            }

            var updated = PreparePlateWrite(plateId, renamed);
            await WritePlateAsync(updated).ConfigureAwait(false);

            lock (gate)
            {
                if (plates.ContainsKey(plateId))
                {
                    plates[plateId] = updated;
                }

                Changed();
            }

            PlateRenamed?.Invoke(plateId, name);
        });

    /// <summary>
    /// Deletes a Plate: its document moves to the Plate trash (kept, never destroyed), then every
    /// character association is removed — clearing Active where it was this Plate, with no other
    /// Plate chosen in its place — then the index entry. Image assets are never touched.
    /// </summary>
    internal Task<PlateDeletionResult> DeletePlateAsync(Guid plateId) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            lock (gate)
            {
                if (!plates.ContainsKey(plateId))
                {
                    throw new PlateLibraryException("That Plate no longer exists.");
                }
            }

            var now = utcNow();
            var documentPath = paths.GetPlatePath(plateId);
            if (store.FileExists(documentPath))
            {
                // Step 1, and the only one that can abort the delete: nothing else has changed yet.
                store.MoveFile(documentPath, paths.GetTrashPlatePath(plateId, now));
            }

            var affected = new List<CharacterBinding>();
            var clearedActive = new List<ulong>();
            lock (gate)
            {
                plates.Remove(plateId);

                foreach (var existing in bindings.Values.ToList())
                {
                    if (!existing.PlateIds.Contains(plateId) && existing.ActivePlateId != plateId)
                    {
                        continue;
                    }

                    var binding = existing.Clone();
                    binding.PlateIds.RemoveAll(id => id == plateId);
                    if (binding.ActivePlateId == plateId)
                    {
                        // Left unset on purpose: another Plate is never chosen automatically.
                        binding.ActivePlateId = null;
                        clearedActive.Add(binding.ContentId);
                    }

                    binding.UpdatedAtUtc = now;
                    affected.Add(binding);

                    // Memory reflects the delete even if a write below fails.
                    bindings[binding.ContentId] = binding;
                }

                library.OrderedPlateIds.RemoveAll(id => id == plateId);
                Changed();
            }

            // The Plate is already gone from the Library; a failed binding/index write below only
            // leaves a stale reference, which is ignored everywhere.
            foreach (var binding in affected)
            {
                try
                {
                    await WriteBindingAsync(binding).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"AetherFrame deleted Plate {plateId} but could not update a character binding; the stale reference is ignored.");
                }
            }

            try
            {
                await WriteLibraryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"AetherFrame deleted Plate {plateId} but could not update the Plate order; the stale entry is ignored.");
            }

            log.Information($"AetherFrame deleted Plate {plateId} (moved to the Plate trash).");
            PlateDeleted?.Invoke(plateId);
            return new PlateDeletionResult(plateId, clearedActive);
        });

    /// <summary>
    /// Makes a Plate this character's Active Plate (associating it if needed). Only the given
    /// character's binding changes.
    /// </summary>
    internal Task SetActivePlateAsync(CharacterContext character, Guid plateId) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            lock (gate)
            {
                RequireReadyLocked(plateId, "made Active");
            }

            var binding = PrepareBindingForWrite(character, utcNow(), out var refusal)
                ?? throw new PlateLibraryException(DescribeRefusal(refusal));

            if (!binding.PlateIds.Contains(plateId))
            {
                binding.PlateIds.Add(plateId);
            }

            binding.ActivePlateId = plateId;
            await CommitBindingAsync(binding).ConfigureAwait(false);
            log.Information($"AetherFrame set Plate {plateId} Active for a character.");
        });

    /// <summary>Moves a Plate next to another in the manual order and saves the order.</summary>
    internal Task MovePlateAsync(Guid plateId, Guid targetPlateId, bool placeAfter) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            bool moved;
            lock (gate)
            {
                moved = PlateOrdering.Move(library.OrderedPlateIds, plateId, targetPlateId, placeAfter);
                if (moved)
                {
                    Changed();
                }
            }

            if (moved)
            {
                await WriteLibraryAsync().ConfigureAwait(false);
            }
        });

    /// <summary>
    /// Adds an imported Plate (see <c>PackageImporter</c>) as a brand-new Plate at the front of
    /// the Library. <paramref name="raw"/> must already carry <paramref name="plateId"/> — a fresh
    /// id — and every image it references must already be in managed storage: the document write
    /// is the commit point, so once the Plate is visible, all it needs exists (and a write that
    /// fails after its file landed moves that file to the trash, since the caller's rollback
    /// removes the images). Never overwrites (refuses an id that's in use or on disk), and never
    /// touches any character binding: an imported Plate belongs to no one and is Active for no
    /// one until the player chooses. <paramref name="continuesOwnedOperation"/>: the caller has
    /// already registered the operation this write completes (see <c>PlatePackageService</c>,
    /// whose import copies the images under its own lease first), so it is not refused once
    /// shutdown began — a running operation is waited for, not rolled back by its last step.
    /// </summary>
    internal Task ImportPlateAsync(Guid plateId, JsonObject raw, bool continuesOwnedOperation = false) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            lock (gate)
            {
                if (plateId == Guid.Empty || plates.ContainsKey(plateId) || store.FileExists(paths.GetPlatePath(plateId)))
                {
                    throw new PlateLibraryException("The imported Plate couldn't be given a new identity.");
                }
            }

            if (raw[nameof(ProfileDocument.ProfileId)] is not JsonValue idValue || !idValue.TryGetValue<string>(out var idText)
                || !Guid.TryParse(idText, out var documentId) || documentId != plateId)
            {
                throw new PlateLibraryException("The imported Plate couldn't be given a new identity.");
            }

            // Read back before writing, so nothing after the write (the commit point) can fail on content.
            var record = PreparePlateWrite(plateId, raw);
            try
            {
                await WritePlateAsync(record).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsInterruption(ex))
            {
                // A store can fail AFTER the file landed (Dalamud's commits its backup row after
                // the move). The importer treats any failure as "nothing committed" and removes
                // the images it added, so a Plate left behind would point at nothing: the file —
                // minted for this import and verified absent a moment ago, so never someone
                // else's — goes to the Plate trash, where nothing lists it.
                TrashHalfWrittenImport(plateId);
                throw;
            }

            lock (gate)
            {
                plates[plateId] = record;
                PlateOrdering.InsertAtFront(library.OrderedPlateIds, plateId);
                Changed();
            }

            try
            {
                await WriteLibraryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The Plate is saved and listed; only its position isn't, and startup re-lists it.
                log.Error(ex, "AetherFrame imported a Plate but could not save the Library order.");
            }

            log.Information($"AetherFrame imported a package as new Plate {plateId}.");
        }, continuesOwnedOperation);

    /// <summary>
    /// Moves an imported document whose write reported failure out of the Plates folder, if it
    /// landed there. Best effort, and never throws, so the write's own failure is what the
    /// importer sees: a document that can't be moved (the move fails, or unloading stops the
    /// operation before this file step) stays where it is, and the importer, which checks the
    /// Plates folder before removing anything, then keeps its images with it.
    /// </summary>
    private void TrashHalfWrittenImport(Guid plateId)
    {
        try
        {
            var path = paths.GetPlatePath(plateId);
            if (store.FileExists(path))
            {
                store.MoveFile(path, paths.GetTrashPlatePath(plateId, utcNow()));
                log.Warning($"AetherFrame moved the file of a failed import ({plateId}) to the Plate trash so it isn't listed without its images.");
            }
        }
        catch (Exception ex) when (IsInterruption(ex))
        {
            log.Warning($"AetherFrame is unloading, so the file of a failed import ({plateId}) stays in the Plates folder with its images; it is listed at the next startup.");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"AetherFrame could not move the file of a failed import ({plateId}) to the Plate trash; it stays in the Plates folder with its images and is listed at the next startup.");
        }
    }

    /// <summary>
    /// Writes an editor's document as the Plate's saved state. Refused when the Plate was
    /// deleted (a save must never bring a deleted Plate back) or can't be written by this build.
    /// The Library's current name always wins over the snapshot's, so a rename made while the
    /// Plate was open is never reverted by the next save.
    /// </summary>
    internal Task SavePlateDocumentAsync(ProfileDocument snapshot) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var plateId = snapshot.ProfileId;
            lock (gate)
            {
                if (!plates.TryGetValue(plateId, out var record))
                {
                    throw new PlateLibraryException("This Plate was deleted from My Plates, so it can't be saved.");
                }

                if (record.Status != PlateStatus.Ready)
                {
                    throw new PlateLibraryException("This Plate can't be saved by this version of AetherFrame.");
                }

                snapshot.Name = record.Name;
            }

            snapshot.Version = ProfileDocument.CurrentSchemaVersion;
            var saved = PreparePlateWrite(plateId, PlateDocuments.ToJson(snapshot));
            await WritePlateAsync(saved).ConfigureAwait(false);

            lock (gate)
            {
                if (plates.ContainsKey(plateId))
                {
                    plates[plateId] = saved;
                }

                Changed();
            }

            PlateSaved?.Invoke(plateId);
        });

    /// <summary>
    /// Every asset referenced by any saved Plate and any Plate in the trash (restorable, so its
    /// images stay protected). Incomplete — and so unusable for cleanup — if any document can't
    /// be read or was saved by a newer version.
    /// </summary>
    internal Task<AssetReferenceScan> ScanAssetReferencesAsync() =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var referenced = new HashSet<Guid>();
            var problems = new List<string>();

            List<PlateRecord> snapshot;
            lock (gate)
            {
                snapshot = plates.Values.ToList();
            }

            foreach (var record in snapshot)
            {
                if (record.Status == PlateStatus.Ready)
                {
                    AssetReferenceScanner.Collect(PlateDocuments.Materialize(ParseObject(record.RawJson!)), referenced);
                }
                else
                {
                    problems.Add($"Plate {record.Id} is {record.Status}.");
                }
            }

            foreach (var path in store.ListFiles(paths.PlateTrashDirectory, "*.json"))
            {
                try
                {
                    var result = await VersionedJson.ReadAsync(store, path, PersistenceSchemas.ProfileDocument, PlateDocuments.Deserialize).ConfigureAwait(false);
                    if (result.IsUsable)
                    {
                        AssetReferenceScanner.Collect(result.Value!, referenced);
                    }
                    else
                    {
                        problems.Add($"Trashed Plate {Path.GetFileName(path)} was saved by a newer version.");
                    }
                }
                catch (Exception ex) when (!IsInterruption(ex))
                {
                    // Problems can reach the player (a blocked cleanup quotes them), so the
                    // failure's text — which may name a local path — stays in the log.
                    log.Error(ex, $"AetherFrame could not read trashed Plate {Path.GetFileName(path)} while scanning image references.");
                    problems.Add($"Trashed Plate {Path.GetFileName(path)} is unreadable ({ex.GetType().Name}).");
                }
            }

            return new AssetReferenceScan(problems.Count == 0, referenced, problems);
        });

    // ---------------------------------------------------------------- internals

    private async Task RunExclusiveAsync(Func<Task> work, bool continuesOwnedOperation = false) =>
        await RunExclusiveAsync<bool>(async () =>
        {
            await work().ConfigureAwait(false);
            return true;
        }, continuesOwnedOperation).ConfigureAwait(false);

    /// <summary>
    /// Runs one operation at a time, as an <see cref="OwnedOperations"/> operation: once the plugin
    /// starts shutting down, an operation still waiting its turn gives up and none starts, while
    /// one already running is waited for (and, if unloading stops waiting, stops at its next file
    /// step — see <see cref="OwnedOperations"/>). With <paramref name="continuesOwnedOperation"/>
    /// the caller registered the operation already and this is its last step: it takes its turn
    /// even once shutdown began, and only abandonment stops it. The dispatcher only starts the
    /// work: it is not waited for there, so the work's continuations run wherever its awaits
    /// complete (see the class remarks on threading).
    /// </summary>
    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> work, bool continuesOwnedOperation = false)
    {
        if (continuesOwnedOperation)
        {
            // A running operation is waited for, so its turn comes: whoever holds the lock ends
            // (or is abandoned and stops at its next step), whatever shutdown did meanwhile.
            await operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await DispatchAsync(work).ConfigureAwait(false);
            }
            finally
            {
                operationLock.Release();
            }
        }

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
                return await DispatchAsync(work).ConfigureAwait(false);
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<T> DispatchAsync<T>(Func<Task<T>> work)
    {
        T result = default!;
        await dispatch(async () =>
        {
            // Dispatched before unloading gave up on it, but not started yet: it never starts.
            operations.ThrowIfAbandoned();
            result = await work().ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result;
    }

    private static PlateLibraryException Closing() => new("AetherFrame is closing, so nothing was changed.");

    /// <summary>What the player is told when a write is refused because its result wouldn't load again.</summary>
    internal const string UnloadableWriteMessage = "AetherFrame couldn't save this change: the result wouldn't load again, so nothing was written.";

    /// <summary>What the player is told when <see cref="PreserveBeforeOverwrite"/> refuses a write.</summary>
    internal const string UnpreservedDamagedFileMessage =
        "A damaged file couldn't be copied to AetherFrame's Recovery folder, so it wasn't written over. Free some disk space and try again.";

    /// <summary>
    /// Between reading and writing during load: stops (with nothing written) when the load was
    /// canceled or the plugin is shutting down. Past this point a load always finishes its writes.
    /// </summary>
    private void ThrowIfLoadCanceled(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        operations.Stopping.ThrowIfCancellationRequested();
    }

    private void RequireLoaded()
    {
        if (!IsLoaded)
        {
            throw new PlateLibraryException("My Plates is still loading.");
        }
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private PlateRecord RequireReadyLocked(Guid plateId, string action)
    {
        if (!plates.TryGetValue(plateId, out var record))
        {
            throw new PlateLibraryException("That Plate no longer exists.");
        }

        return record.Status switch
        {
            PlateStatus.Ready => record,
            PlateStatus.NewerVersion => throw new PlateLibraryException($"This Plate was saved by a newer version of AetherFrame and can't be {action}."),
            _ => throw new PlateLibraryException($"This Plate's file is damaged and it can't be {action}."),
        };
    }

    /// <summary>
    /// A working COPY of the binding to change for <paramref name="character"/> (new when there is
    /// none), with its descriptive metadata refreshed; <see cref="CommitBindingAsync"/> writes it
    /// and only then makes it live, so a failed write never leaves memory claiming otherwise. A
    /// binding whose file holds damaged content is replaced by a fresh one (WriteBindingAsync keeps
    /// a recovery copy of the damaged file first). Null, with <paramref name="refusal"/> saying why,
    /// for a binding this build must not write: one saved by a newer version, or one whose file
    /// couldn't be read this session and may well be intact.
    /// </summary>
    private CharacterBinding? PrepareBindingForWrite(CharacterContext character, DateTime now, out BindingRefusal refusal)
    {
        lock (gate)
        {
            if (newerVersionBindings.Contains(character.ContentId))
            {
                log.Warning("AetherFrame left a character's Plate settings unchanged: they were saved by a newer version.");
                refusal = BindingRefusal.NewerVersion;
                return null;
            }

            if (unavailableBindings.Contains(character.ContentId))
            {
                log.Warning("AetherFrame left a character's Plate settings unchanged: they couldn't be read when the Plate Library loaded.");
                refusal = BindingRefusal.Unavailable;
                return null;
            }

            refusal = BindingRefusal.None;
            var binding = bindings.TryGetValue(character.ContentId, out var existing)
                ? existing.Clone()
                : new CharacterBinding { ContentId = character.ContentId, CreatedAtUtc = now };

            if (!string.IsNullOrWhiteSpace(character.Name))
            {
                binding.LastKnownCharacterName = character.Name;
            }

            if (!string.IsNullOrWhiteSpace(character.HomeWorld))
            {
                binding.LastKnownHomeWorld = character.HomeWorld;
            }

            binding.Version = CharacterBinding.CurrentVersion;
            binding.UpdatedAtUtc = now;
            return binding;
        }
    }

    /// <summary>The player-facing reason <see cref="PrepareBindingForWrite"/> refused a binding.</summary>
    private static string DescribeRefusal(BindingRefusal refusal) => refusal switch
    {
        BindingRefusal.NewerVersion => "This character's Plate settings were saved by a newer version of AetherFrame and can't be changed.",
        BindingRefusal.Unavailable => "This character's Plate settings couldn't be read when AetherFrame started. Restart the game to try again.",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Not a refusal."),
    };

    /// <summary>Writes a binding prepared by <see cref="PrepareBindingForWrite"/>, then makes it live.</summary>
    private async Task CommitBindingAsync(CharacterBinding binding)
    {
        await WriteBindingAsync(binding).ConfigureAwait(false);
        lock (gate)
        {
            bindings[binding.ContentId] = binding;
            Changed();
        }
    }

    /// <summary>Writes exactly the text <see cref="PreparePlateWrite"/> proved readable.</summary>
    private async Task WritePlateAsync(PlateRecord record)
    {
        var path = paths.GetPlatePath(record.Id);
        PreserveBeforeOverwrite(path);
        await store.WriteTextAsync(path, record.RawJson!).ConfigureAwait(false);
    }

    private async Task WriteBindingAsync(CharacterBinding binding)
    {
        string json;
        bool replacesDamaged;
        lock (gate)
        {
            binding.Version = CharacterBinding.CurrentVersion;
            json = VersionedJson.Serialize(binding);
            replacesDamaged = unreadableBindings.Contains(binding.ContentId);
        }

        var path = paths.GetBindingPath(binding.ContentId);
        if (replacesDamaged)
        {
            // Throws (aborting the write) if the damaged file can't be preserved first.
            PreserveDamagedFile(path);
            lock (gate)
            {
                unreadableBindings.Remove(binding.ContentId);
            }
        }

        PreserveBeforeOverwrite(path);
        await store.WriteTextAsync(path, json).ConfigureAwait(false);
    }

    private async Task WriteLibraryAsync()
    {
        string json;
        lock (gate)
        {
            if (!libraryWritable)
            {
                return;
            }

            library.Version = PlateLibraryState.CurrentVersion;
            json = VersionedJson.Serialize(library);
        }

        PreserveBeforeOverwrite(paths.LibraryFile);
        await store.WriteTextAsync(paths.LibraryFile, json).ConfigureAwait(false);
    }

    /// <summary>Copies a damaged file into Recovery before anything is written over it.</summary>
    private void PreserveDamagedFile(string path)
    {
        if (!store.FileExists(path))
        {
            return;
        }

        var destination = paths.GetRecoveryPath(path, utcNow());
        store.CopyFile(path, destination);
        log.Warning($"AetherFrame kept a copy of damaged file {LogPrivacy.FileName(path)} in Recovery as {LogPrivacy.FileName(destination)}.");
    }

    /// <summary>
    /// A file the store could only read from its backup copy: the copy on disk is damaged, and the
    /// next write to that path would replace it. So the player learns about it once, and the
    /// on-disk bytes go to Recovery first (a copy only — the file itself is never rewritten here,
    /// and an existing Recovery copy is never overwritten). A failed copy doesn't stop the load —
    /// the record is used either way — but the path is remembered, and nothing is written over it
    /// until a later copy succeeds (see <see cref="PreserveBeforeOverwrite"/>).
    /// </summary>
    private void KeepRecoveredFile(string path)
    {
        var fileName = LogPrivacy.FileName(path);
        log.Warning($"AetherFrame found {fileName} damaged and read it from the backup copy instead; the damaged file is kept in Recovery.");

        try
        {
            var destination = paths.GetRecoveryPath(path, utcNow());
            if (store.FileExists(path) && !store.FileExists(destination))
            {
                store.CopyFile(path, destination);
            }
        }
        catch (Exception ex) when (!IsInterruption(ex))
        {
            log.Error(ex, $"AetherFrame could not keep a copy of damaged file {fileName} in Recovery; it won't be written over until a copy can be kept.");
            lock (gate)
            {
                unpreservedDamagedFiles.Add(path);
            }
        }
    }

    /// <summary>
    /// Before any write to <paramref name="path"/>: if its damaged on-disk bytes still have no
    /// Recovery copy (see <see cref="KeepRecoveredFile"/>), keeps one now, and refuses the write
    /// when that still fails, so the only copy of what may be the newest content is never replaced.
    /// </summary>
    private void PreserveBeforeOverwrite(string path)
    {
        lock (gate)
        {
            if (!unpreservedDamagedFiles.Contains(path))
            {
                return;
            }
        }

        try
        {
            PreserveDamagedFile(path);
        }
        catch (Exception ex) when (!IsInterruption(ex))
        {
            log.Error(ex, $"AetherFrame did not write {LogPrivacy.FileName(path)}: its damaged copy still couldn't be kept in Recovery.");
            throw new PlateLibraryException(UnpreservedDamagedFileMessage);
        }

        lock (gate)
        {
            unpreservedDamagedFiles.Remove(path);
        }
    }

    /// <summary>
    /// The store's read contract: a file whose CONTENT is damaged fails this way (the store's
    /// backup copy, if any, was already tried); any other failure means the file couldn't be
    /// opened at all and may be perfectly intact, so it must never be written over as damaged.
    /// </summary>
    private static bool IsContentDamage(Exception ex) => ex is InvalidDataException or JsonException;

    /// <summary>An operation stopped by cancellation or by unloading — never a fault in a file, so never handled as one.</summary>
    private static bool IsInterruption(Exception ex) => ex is OperationCanceledException or OperationAbandonedException;

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private string ResolveNewName(string? requested, string fallback)
    {
        if (requested is not null)
        {
            return PlateNaming.TryNormalizeName(requested, out var normalized, out var error)
                ? normalized
                : throw new PlateLibraryException(error!);
        }

        return PlateNaming.MakeUniqueName(fallback, plates.Values.Select(p => p.Name));
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private List<(Guid Id, DateTime CreatedUtc)> ExistingPlatesLocked() =>
        plates.Values.Select(p => (p.Id, p.CreatedUtc)).ToList();

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private IReadOnlyList<PlateSummary> BuildOrderedSummariesLocked()
    {
        var namesByPlate = new Dictionary<Guid, List<string>>();
        var activeFor = new Dictionary<Guid, List<ulong>>();
        foreach (var binding in bindings.Values)
        {
            if (binding.ActivePlateId is { } activeId)
            {
                if (!activeFor.TryGetValue(activeId, out var contentIds))
                {
                    activeFor[activeId] = contentIds = new List<ulong>();
                }

                contentIds.Add(binding.ContentId);
            }

            if (string.IsNullOrWhiteSpace(binding.LastKnownCharacterName))
            {
                continue;
            }

            foreach (var id in binding.PlateIds)
            {
                if (!namesByPlate.TryGetValue(id, out var names))
                {
                    namesByPlate[id] = names = new List<string>();
                }

                names.Add(binding.LastKnownCharacterName);
            }
        }

        var order = PlateOrdering.ResolveDisplayOrder(library.OrderedPlateIds, ExistingPlatesLocked());
        return order.Select(id =>
        {
            var record = plates[id];
            return new PlateSummary(record.Id, record.Status, record.Name, record.CreatedUtc, record.ModifiedUtc, record.Revision, record.Problem,
                namesByPlate.TryGetValue(id, out var names) ? names : [],
                activeFor.TryGetValue(id, out var active) ? active : [],
                record.Preview?.HasUnsupportedElements ?? false);
        }).ToList();
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private void Changed()
    {
        generation++;
        orderedSummaries = null;
    }

    /// <summary>
    /// The record of a Plate about to be written, holding the exact text the write puts on disk —
    /// proven first to load again as a Ready Plate through the reader startup uses (the same text
    /// check, schema check, deserialization and in-memory repairs). Text that couldn't be read
    /// back would make the Plate unreadable at the next startup, and in game the write replaces
    /// the storage's backup copy too, so it is refused here, before anything is written.
    /// </summary>
    private PlateRecord PreparePlateWrite(Guid plateId, JsonObject raw)
    {
        var json = VersionedJson.Serialize(raw);
        try
        {
            VersionedJson.RejectUndecodableText(json, PersistenceSchemas.ProfileDocument.Name);
            var parsed = VersionedJson.Parse(json, PersistenceSchemas.ProfileDocument, PlateDocuments.Deserialize);
            if (!parsed.IsUsable)
            {
                throw new InvalidDataException(parsed.IsNewerVersion ? "It would read as a newer version's Plate." : parsed.Migration.Error ?? "It is unreadable.");
            }

            var document = PlateDocuments.Materialize(parsed.Raw!);
            return new PlateRecord(plateId, PlateStatus.Ready, json, document, document.Name, document.CreatedAtUtc, document.UpdatedAtUtc, document.Revision,
                document.Version, null);
        }
        catch (Exception ex) when (!IsInterruption(ex))
        {
            log.Error(ex, $"AetherFrame did not write Plate {plateId}: what it would have written couldn't be read back.");
            throw new PlateLibraryException(UnloadableWriteMessage);
        }
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Plate JSON is not an object.");

    private static List<Guid>? ReadNewerOrder(JsonObject raw)
    {
        try
        {
            return raw[nameof(PlateLibraryState.OrderedPlateIds)]?.Deserialize<List<Guid>>(JsonOptions.Default);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Why a character's binding can't be written this session (see <see cref="PrepareBindingForWrite"/>).</summary>
    private enum BindingRefusal
    {
        None,
        NewerVersion,
        Unavailable,
    }

    /// <summary>
    /// One Plate as loaded. Immutable apart from <see cref="Preview"/>'s Name on rename: the saved
    /// JSON is kept as a string (safe to read from any thread, unlike a lazily-built JsonObject)
    /// and every change replaces the record.
    /// </summary>
    private sealed record PlateRecord(
        Guid Id,
        PlateStatus Status,
        string? RawJson,
        ProfileDocument? Preview,
        string Name,
        DateTime CreatedUtc,
        DateTime ModifiedUtc,
        int Revision,
        int SchemaVersion,
        string? Problem);
}
