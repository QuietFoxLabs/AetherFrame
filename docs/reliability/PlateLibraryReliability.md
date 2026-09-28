# Plate Library reliability and data preservation

This report covers a review of the local Plate Library, its persistence, backup, import and export, and the fixes that followed. It was written after v0.1.6, on top of `master` at `6384db6` (the NETWORK0 merge).

**Scope.** It covers local files only: Plates, Templates, character bindings, the Plate order (`library.json`), images and their metadata, Recovery and backup copies, and `.aetherframe` packages. The NETWORK0 protocol, networking, the plugin's distribution and the saved file formats are out of scope and unchanged.

**Summary.** v0.1.6 had already made the Library robust. Writes are atomic. Damaged files are left untouched and copied to Recovery. Newer-version files are never written. Import validates everything on a staging copy before committing. Nothing deletes images. This review found no way for an ordinary save, load or import to corrupt or lose a Plate. It did find narrower gaps, and those are fixed here:

- **Medium.** A damaged file recovered from Dalamud's backup could be overwritten with no Recovery copy kept, if that copy had failed at load.
- **Low-medium.** A file with invalid bytes could load as intact, bypassing both the backup and Recovery. The next save could then destroy the intact backup.
- **Low.** Several smaller defects, listed below.

Every fix keeps the saved file formats, schema versions and package format as they were.

## 1. Method

- **Four parallel investigations**, each read-only, with probe tests in scratch copies:
  - A: saving, loading, crash safety and atomic writes.
  - B: import and export validation, malformed files and unsafe paths.
  - C: asset integrity, missing images, file ownership and cleanup safety.
  - D: migrations, backward compatibility, recovery and test coverage.
- **Reconciliation.** Every claimed weakness was checked against the code before being fixed.
- **Tests that prove each fix.** Every fix has a regression test. Each test was shown to fail with the fix removed, except where noted in section 3.
- **Adversarial review.** An independent review of the finished diff followed.
- **Baseline at `6384db6`**, built and run on Linux with .NET 10.0.401 and Dalamud's reference assemblies:
  - AetherFrame.Tests: 2739 passed.
  - AetherFrame.Protocol.Tests: 153 passed.
  - AetherFrame.ReleaseTools.Tests: 439 passed.
  - The full solution, the Dalamud plugin included, builds with 0 warnings.

## 2. Existing behavior, verified

What was already safe, and stays as it was.

### Atomic writes

- **Plate files and everything else the Library writes in game** (Templates, character bindings, `library.json`) go through Dalamud's `IReliableFileStorage`. It writes a temporary file, replaces the target with `MoveFileEx`, and keeps a backup row.
- **Everything outside that storage** is written the same atomic way: the stuck-temp fallback, asset metadata, thumbnails and export files all write a temporary sibling, flush it and rename it (`SystemFileStore.WriteAtomically`, `PackageExporter`).
- **No code in the plugin truncates or writes in place** over user content. There is no `File.WriteAllText`, `FileMode.Create` or `Truncate` anywhere.
- **A failed write never damages the original.** It leaves the old file intact and shows the player a plain error. The editor stays dirty.

### Serialization and concurrency

- **Serialization happens before the store is touched.** Every document is serialized to a complete string first. A value that isn't a number is refused before any file step.
- **Operations are serialized.** Each Library holds a `SemaphoreSlim(1,1)` for the whole operation. `ProfileService` refuses a second save while one is in flight.
- **A deleted Plate can't come back.** A save of a deleted Plate is refused, and delete moves the file to `Trash/Plates` first.

### Damaged, locked and newer files

- **Reads prefer the file on disk.** The file is read first, and the backup is used only when the content is rejected (`ReliableReads`). A file that is locked or missing is "unavailable": it is never written over and never silently replaced by the backup.
- **A damaged binding or `library.json`** is either copied to Recovery before being overwritten, or left read-only for the session.
- **A newer version's files are never written.** This covers Plates, Templates, bindings, the index, asset metadata and configuration. They are listed read-only, deleting one moves it intact, and none of them is ever read from an older backup.
- **Unknown data survives.** Unknown fields, elements and Components are carried through every save (`[JsonExtensionData]`, `UnrecognizedElements`, `UnrecognizedComponents`).

### Migrations

- **Migrations run in memory.** Only the pre-Library binding migration writes, and only once `Backups/pre-plate-library` holds the originals. It is idempotent.
- **A failed migration write** keeps `library.json` unwritten, so the next startup migrates again.

### Packages

`PackagePolicy`, `ZipPreflight`, `PackagePaths`, `PackageJson`, `PackageManifest`, `PackageProfileValidator` and `ImageSafety` together cover:

- **Archive integrity:** truncated archives, disagreeing end records and central directories, duplicate and case-colliding entries.
- **Unsafe names:** path traversal, absolute paths, drive letters, alternate data streams, reserved names and any name outside printable ASCII.
- **Size and structure limits:** ZIP bombs, measured by bytes actually read; entry, size, depth, value-count and property-name limits.
- **Versions:** newer and invalid format and schema versions.
- **Duplicates:** duplicate ids and properties.
- **Images:** missing and undeclared images; a mismatch between an image's extension, media type and magic bytes; decompression-bomb dimensions.

What stays safe around them:

- **No filesystem path is ever built from a package-supplied name.**
- **Everything is validated on a staging copy** before any persistent change.
- **Import never overwrites anything.** It makes a new Plate with fresh Plate and image ids and binds it to no character. If the commit fails, it rolls back only the images it created.
- **Export writes a temporary file**, reads it back with the real importer, then moves it into place.

### Images

- **Files and ids.** Images are stored under fresh GUID names. They are copied into staging with `CreateNew` and moved without overwrite, so an existing asset is never replaced.
- **Deleting and duplicating Plates or Templates never touches images.**
- **Nothing deletes images.** Asset cleanup (`AssetGarbageCollector`) has no caller. Startup only sweeps exact temporary-file names inside AetherFrame's own staging folders.
- **A missing or corrupt image** draws a placeholder and is logged once. Exporting it gives a clear "missing" error.

## 3. Confirmed weaknesses and fixes

### Summary

| # | Severity | Area | Weakness | Commit |
|---|---|---|---|---|
| 1 | Medium | Persistence | A damaged file served from the backup, whose Recovery copy failed at load, was written over by the next save or rename with nothing kept | `237673f` |
| 2 | Low-Medium | Persistence | Invalid UTF-8 in a file that still parses loaded as intact, bypassing the backup and Recovery | `be5d5b0` |
| 3 | Low | Persistence | Recovery and migration copies were not flushed to disk before the original was replaced | `dc92e41` |
| 4 | Low (defense in depth) | Persistence | Nothing verified that a Plate or Template write would load again | `706de0b` |
| 5 | Low | Persistence | Duplicate reported failure after the copy was saved, when only the order write failed, inviting a second copy | `706de0b` |
| 6 | Low-Medium (wording) | Export | An over-limit Plate's export failed with the import message "The Plate in this file is damaged." | `b8efb4a` |
| 7 | Low | Import | An image counted as "used" when its id appeared anywhere, a caption included, leaving images referenced by nothing | `b8efb4a` |
| 8 | Low | Import | Invalid UTF-8 in a string value was refused through the "unexpected exception" path, with a stack trace in the log | `b8efb4a` |
| 9 | Low | Import | Retrying an import whose Plate had landed but failed to finish could import it twice | `b8efb4a` |
| 10 | Low (latent) | Assets | The asset cleanup's reference scan missed Template envelope data and files in the Plates folder that weren't loaded | `5ddd120` |
| 11 | Low | Assets | Add Image could store a file under an extension that doesn't match its content | `5ddd120` |
| 12 | Low (wording) | Diagnostics | A locked Plate or Template was described as "damaged" | `62e87a7` |

### Details

1. **A damaged file written over with no Recovery copy.**
   - **Before:** `KeepRecoveredFile` in the Plate Library, and `KeepRecoveryCopy` in the Template Library, tried the Recovery copy once, at load, and only logged a failure. The next Save, Rename, Set Active, Move or Template rename then replaced the damaged bytes.
   - **Why it matters:** the damaged on-disk file is the newest content whenever Dalamud's backup is stale, and that is by design after a stuck-temp direct write on a full disk. It contradicted the 0.1.6 changelog.
   - **Now:** such a path is remembered for the session. Every write to it first retries the copy (`PreserveBeforeOverwrite`), and refuses with "A damaged file couldn't be copied to AetherFrame's Recovery folder, so it wasn't written over. Free some disk space and try again." while the copy still fails.
   - **Tests:** `DamagedFilePreservationTests`, covering Plate save and rename, a binding, `library.json` and a Template. Five of the six fail without the fix; the sixth pins the unchanged case where the copy succeeds at load.

2. **Invalid UTF-8 read as intact.**
   - **Before:** files are decoded leniently, so a corrupted byte became U+FFFD and the Plate loaded Ready. The intact backup was never consulted, and the next save wrote the replacement character over the file and its backup row.
   - **Now:** both readers (`VersionedJson.ReadAsync`, used for Plates, bindings and the index, and the Template reader) reject text holding a literal U+FFFD as damage (`VersionedJson.RejectUndecodableText`). AetherFrame never writes that character literally: the serializer escapes everything outside ASCII. The usual damage path applies from there.
   - **Why the check lives in the reader:** the stores still decode as before. The reader runs once per copy, so the stores' "a second run means the backup" contract is unchanged.
   - **Tests:** `InvalidTextEncodingTests`. Four fail without the fix. The UTF-8 with BOM, UTF-16 with BOM and non-ASCII cases pass either way; they pin that valid text is unaffected.

3. **Unflushed copies.**
   - **Before:** `SystemFileStore.CopyFile` used `File.Copy`, which gives no durability guarantee, while the write that follows it over the original is flushed.
   - **Now:** it copies through streams, flushes the copy to disk, removes a partial copy on failure, and still never overwrites.
   - **Tests:** `SystemFileStoreCopyTests` pins the exact bytes, the no-overwrite rule and that a failed copy creates nothing. A flush can't be observed deterministically, so no test fails without this change.

4. **Writes that might not load again.**
   - **Now:** each Plate and Template write serializes once, runs that exact text through the startup reader, and writes it only if it would load as Ready (`PreparePlateWrite`, `PrepareTemplateWrite`). Otherwise the write is refused with "AetherFrame couldn't save this change: the result wouldn't load again, so nothing was written." The Library's record is built from the same text.
   - **Scope:** no input reachable from the editor is known to produce such text. The check guards the Library's own entry points.
   - **Tests:** `WriteReadBackTests`. The import and create-from-document tests fail without the fix: an imported or instantiated document carrying a newer schema version was written, and was listed as a newer version's Plate after a restart.

5. **Duplicate reporting a failure that wasn't one.**
   - **Now:** a failed `library.json` write after the copy is saved is logged, as in Create, Import and Delete, instead of being thrown. The copy is listed, and startup re-lists it.
   - **Test:** `WriteReadBackTests.DuplicateWhoseOrderWriteFails_…` fails without the fix.

6. **Export wording.**
   - **Before:** a Plate saved by 0.1.5 with a typed font size over 1024, or a hand-edited Plate with a repeated element id, opens fine but is refused by the export self-check.
   - **Now:** the player sees "This Plate holds a value a Plate file can't carry (font size out of range). Change it in the editor and save, then export again." The detail is the validator's fixed text, omitted if it ever looks like a path. Any other self-check refusal reads "The Plate couldn't be exported."
   - **Tests:** `PackageReliabilityTests`.

7. **Images riding along.**
   - **Now:** a package image counts as used only where `AssetReferenceScanner` finds it. That is the scan export and cleanup already use, and it still counts any id in data from newer builds.
   - **Compatibility:** every historical fixture package, and every package the exporter produces, still imports.

8. **Invalid UTF-8 in package strings.**
   - **Now:** `PackageJson.Screen` checks string values as well as property names, so the profile or manifest is refused as damaged. `PackageManifest.Parse` no longer throws.

9. **Import retry.**
   - **Now:** `StagedPackage` becomes non-importable once its Plate is on disk: after a success, or after an unfinished import whose Plate stayed.

10. **The dormant cleanup's reference scan.** This is not active: nothing calls the cleanup. The scan now also counts:
    - every GUID in a Template envelope's and its Origin's preserved data;
    - every GUID in any file in the Plates folder that isn't a loaded Plate, such as a Plate put back from the trash by hand while running, or a `"<id> - Copy.json"`.

    A file that can't be read leaves the scan incomplete, which blocks cleanup. The change can only protect more images, never fewer.

11. **Add Image extension.**
    - **Before:** the stored file was named after the first inspection of the source, not after the copy that was validated.
    - **Now:** it takes the extension of the stored copy's content, as package imports already did.

12. **Locked-file wording.**
    - **Now:** an unreadable Plate or Template whose file couldn't be opened, as opposed to one whose content is damaged, says "couldn't be opened; another program may be using it … Restart the game to try again". The log line says "could not open".
    - **Tests:** `UnopenableFileDiagnosticsTests`.

## 4. Every behavior change

Everything a player or an existing file can notice:

| Change | Who sees it |
|---|---|
| A write to a file whose damaged bytes couldn't be kept in Recovery is refused, with a message, until a copy succeeds | Only after a damaged file was read from its backup and the Recovery copy failed at load: in practice, a full disk |
| A Plate, Template, binding or `library.json` holding a literal U+FFFD loads from its backup, with the damaged file kept in Recovery; without a backup it is listed unreadable and left untouched | Only for damaged files, or a hand edit that typed that exact character literally. Files AetherFrame wrote never contain it |
| A write whose text wouldn't load as Ready is refused, with a message | No known editor path; only direct internal misuse |
| Duplicate succeeds even when the order file couldn't be written; the failure is logged | Only when writing `library.json` fails |
| Exporting an over-limit Plate names the value instead of saying the file is damaged | Plates saved by 0.1.5's unbounded sliders, or hand edits |
| A package whose image is referenced only from text is refused ("The file contains an image the Plate doesn't use.") | Crafted packages; AetherFrame never exports one |
| A package with invalid UTF-8 in a text value is refused as damaged instead of "isn't a valid AetherFrame Plate file" | Corrupted or crafted packages |
| A checked package can't be imported twice from the same Import window | After a success, or after an unfinished import |
| Add Image names the stored file after the stored content | Only if the source changed format while being added |
| A locked Plate or Template is described as "couldn't be opened", not "damaged" | Files held by another program at load |
| Recovery and migration backup copies are flushed to disk | Not visible |
| The dormant cleanup's scan protects more images | Not visible: cleanup doesn't run |

Unchanged:

- **Formats:** the saved file formats and schema versions (Plate 2, binding 2, index 1, Template 1, asset metadata 1), and the `.aetherframe` format and its limits.
- **Features:** the editor and UI layout, and the configuration.
- **Other components:** the NETWORK0 protocol, the distribution files and `plugin-repository`.
- **Images:** no image is ever deleted, and no orphan cleanup runs.

## 5. Compatibility with existing saved Plates

- **Formats.** No persisted format changed between v0.1.4, v0.1.5, v0.1.6 and this branch. `git diff v0.1.4 v0.1.6` touches no schema constant, and `git diff v0.1.6 6384db6 -- AetherFrame/` is empty. The v0.1.0 to v0.1.4 fixture sets, written by those builds' own code, therefore represent 0.1.5 and 0.1.6 data too.
- **New pinned guarantees** (`CompatibilityPreservationTests`):
  - Every JSON record of every fixture installation round-trips byte for byte, through the raw writer (rename, duplicate) and through its kind's typed writer. This covers Plates, bindings, the index, asset metadata, and trashed Plates and Templates.
  - Renaming every historical Plate changes only its name and modified time.
  - A binding rewrite changes only its modified time.
  - A newer version's Template, binding, index or Plate is never written by rename, duplicate, delete or move.
  - A migration that fails partway finishes at the next startup, keeps the originals in `Backups/pre-plate-library`, and a third startup writes nothing.
- **Existing suites** covering historical fixtures, legacy formats and packages from every tag all still pass.
- **Only one existing file can load differently:** a hand-edited file containing a literal U+FFFD (section 4). No AetherFrame build writes one.
- **No migration was added.** No format change was needed.

## 6. Tests

| | Before (`6384db6`) | After |
|---|---|---|
| AetherFrame.Tests | 2739 passed | 2796 passed (57 new cases) |
| AetherFrame.Protocol.Tests | 153 passed | 153 passed |
| AetherFrame.ReleaseTools.Tests | 439 passed | 439 passed |
| Solution build (Release, Dalamud plugin included) | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `validate-package` on the built package | OK | OK |

- **New test files:** `DamagedFilePreservationTests`, `InvalidTextEncodingTests`, `SystemFileStoreCopyTests`, `WriteReadBackTests`, `PackageReliabilityTests`, `AssetReferenceCoverageTests`, `CompatibilityPreservationTests` and `UnopenableFileDiagnosticsTests`.
- **Isolation.** All of them use isolated temporary directories and never touch a real Dalamud configuration.
- **Negative checks.** Each fix's tests were run against the code without that fix and failed, except the flushed copy and the compatibility pins, which describe behavior that was already correct.
- **Where they ran.** Locally on Linux only. Windows runs in CI (`build.yml`, `windows-2022` and `ubuntu-24.04`).

## 7. Deferred findings

None of these puts existing data at risk today. Each needs a product decision, touches Dalamud-side code that can't be tested here, or is only relevant once a dormant feature is switched on.

| # | Finding | Why deferred |
|---|---|---|
| D1 | A configuration file damaged beyond Dalamud's own backup is replaced by the defaults and saved with no copy kept. That loses a newer build's settings held in its extension data. | The file holds settings, not Plates, and the fix lives in `Plugin.cs`, which can't be tested here. Suggested fix: copy `ConfigFile` to `Recovery` before the first save, and skip the save if the copy fails. |
| D2 | Elements and Components this build doesn't recognize move to the end of their lists on a model save. After a downgrade, a newer build may stack them differently. | Needs the original positions carried in memory, and only matters on a downgrade within the same schema version. |
| D3 | There is no v0.1.5 or v0.1.6 fixture set, and no fixture generator in the repository. | The formats are identical to v0.1.4 (section 5), so a new set adds little until a format changes. Suggestion: add an opt-in generator test (the protocol tests' `AETHERFRAME_PROTOCOL_REGENERATE_VECTORS` pattern) and a Releasing step, before the next format change. |
| D4 | Prerequisites before asset cleanup is ever switched on. See the list below the table. | Cleanup has no caller. Each of these must be settled when it is activated. |
| D5 | A Plate with repeated element ids from a hand edit can't be exported until it is saved with the ids repaired. | Exporting the repaired ids would change export output. That is a product decision. |
| D6 | When a Plate is served from a stale backup, only the log says so. | UI and product decision (a notice in My Plates). |
| D7 | No autosave, draft or version history: unsaved edits are lost on a crash, and each save replaces both the file and its backup. | Product decision. |
| D8 | Two game clients sharing one configuration folder can overwrite each other's changes. | Needs a cross-process locking design. |
| D9 | `WriteAtomically`'s cleanup can mask the original error if the delete itself fails. Hidden `.{name}.{guid}.tmp` leftovers from the stuck-temp fallback are swept only for thumbnails. | Cosmetic; no data risk. |
| D10 | A crash mid-export leaves `.{guid}.aetherframe.tmp` in the player's chosen folder. The suggested export name doesn't avoid reserved Windows names (a Plate named "Con"). | Outside AetherFrame's folder, so it can't be swept safely. The Windows case is untested here. |
| D11 | Imported unknown elements are checked only for number sizes, so a future build that recognises them must validate them on load. | Forward compatibility by design. Recorded for the build that adds new element types. |
| D12 | `ImageSafety`: the truncation check for JPEGs of 1 MiB or less is weak, and compressed PNG ancillary chunks are not bounded. | The decoder's behavior is unverified. Dimensions and bytes are already bounded. |
| D13 | After an unfinished import, the Import window still shows the green "Ready to import" verdict above its error. The button is disabled. | Cosmetic UI. |
| D14 | A version number out of `int` range is read as damage, not as a newer version, so a binding with one is replaced after a Recovery copy. | No build writes one. |
| D15 | `Vector2`, `Vector4` and `ElementRect` values carry no extension data, so unknown sub-fields inside them are dropped on a model save. | Forward compatibility only. It would need a model change. |
| D16 | No bounded retry on transient sharing violations (antivirus, indexers). | A failed save keeps the old file and the editor dirty, which is acceptable. A retry policy is optional polish. |

The D4 prerequisites, before asset cleanup is ever switched on:

- **A folder that can't be listed must block cleanup.** A missing or inaccessible `Profiles` folder currently gives an empty scan that calls itself complete.
- **`Recovery/` and `Backups/` must be scanned.**
- **The editor's `AssetsInUse` must be a required input to `Plan`.**
- **Uppercase-named asset files need handling.** They can be trashed but not restored.
- **Stray files must not win.** `ResolveAssetPath` picks the first `{id}.*` file alphabetically, so a stray `{id}.bak` wins. It can also throw from Draw when the folder can't be listed.

No specific change was stopped for being incompatible or destructive: none of the fixes needed a format change or a migration.

## 8. Manual acceptance in FFXIV

These need the game. Use Windows, Dalamud API 15, this branch's build installed as a dev plugin, and a copy of a real 0.1.6 data folder. Keep `/xllog` open. "Unloaded" means disabled in `/xlplugins`.

1. **Load real data.** Hash the data folder, enable the plugin, and open My Plates: every Plate is listed, and no file's hash changed.
2. **Everyday operations.** Create, rename, duplicate, delete, Set Active, reorder, save from both editors, Save as Template, and Use Template. Everything behaves as in 0.1.6, and everything reloads after a plugin reload.
3. **Failed Recovery copy.**
   1. Unload the plugin.
   2. Truncate a Plate file that Dalamud has saved before.
   3. Create an empty *file* named `Recovery` in the data folder, where the Recovery folder would go.
   4. Load: the Plate shows its backup content, and the log has an Error saying the damaged file won't be written over until a copy can be kept.
   5. Save that Plate: the editor shows "A damaged file couldn't be copied to AetherFrame's Recovery folder…" and stays dirty, and the file is still truncated.
   6. Delete the `Recovery` file and save again: it succeeds, and `Recovery/<guid>.damaged-*.json` holds the truncated bytes.
4. **Invalid bytes.** With the plugin unloaded, use a hex editor to change one letter of a saved Plate's name to `FF`, then load. The card shows the intact name, the log says it was read from the backup copy, and a Recovery copy holds the edited bytes.
5. **Locked file.** With the plugin unloaded, hold a Plate file open with no sharing, for example in PowerShell: `$f=[IO.File]::Open("<path>",'Open','Read','None')`. Load: the card's hint says the file couldn't be opened and asks for a restart, not that it is damaged. Close the handle and reload: the Plate is Ready.
6. **Duplicate while the order can't be written.** Make `Library/library.json` read-only, then Duplicate a Plate. The copy appears with no error message, and the log has an Error about the Library order. Undo the read-only flag and reload: both Plates are listed.
7. **Export an over-limit Plate.** Export a Plate saved by 0.1.5 with a typed font size over 1024, or hand-edit a text's `FontSize` to 2000. The message names "font size out of range" and asks to change it and save, and no file is written.
8. **Import.** Round-trip export and import a Plate with images. Also import a package another tester exported with 0.1.5 or 0.1.6: both import as before, and nothing existing is replaced.
9. **Add Image.** Add a PNG, a JPEG and a WebP. Each stored file in `assets/` has the extension matching its content, and each displays.

Anything failing in steps 1 to 4 or 8 blocks a release.

## 9. What could not be checked in the cloud

- **The game itself.** Everything in section 8 needs FFXIV: Dalamud's `IReliableFileStorage` in game, ImGui, and real Windows file locking and antivirus behavior.
- **Windows builds and tests** run only in GitHub CI.
- **Durability.** Flush-to-disk behavior under power loss can't be observed by a test.
