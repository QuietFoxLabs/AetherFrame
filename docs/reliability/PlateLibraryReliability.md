# Plate Library reliability and data preservation

This report covers a review of the local Plate Library, its persistence, backup, import and export, and the fixes that followed. It was written after v0.1.6, on top of `master` at `6384db6` (the NETWORK0 merge).

**Revised after an independent audit.** An independent adversarial review of this branch at `c724cd6` confirmed that one of its fixes, the treatment of text that isn't valid (fix 2, `be5d5b0`), lost data in several ways, and that the Recovery copy's central behaviour was not tested under a copy that fails partway. Both are corrected, with regression tests. Section 10 describes the audit's findings and the corrections; sections 3 to 8 describe the branch as it now stands.

**Scope.** It covers local files only: Plates, Templates, character bindings, the Plate order (`library.json`), images and their metadata, Recovery and backup copies, and `.aetherframe` packages. The NETWORK0 protocol, networking, the plugin's distribution and the saved file formats are out of scope and unchanged.

**Summary.** v0.1.6 had already made the Library robust. Each write replaces its file atomically, although a write and Dalamud's backup row of it are not one transaction (section 2). Damaged files are left untouched and copied to Recovery. Newer-version files are never written. Import validates everything on a staging copy before committing. Nothing deletes images. This review found no way for an ordinary save, load or import to corrupt or lose a Plate. It did find narrower gaps, and those are fixed here:

- **Medium.** A damaged file recovered from Dalamud's backup could be overwritten with no Recovery copy kept, if that copy had failed at load.
- **Low-medium.** A file with bytes that aren't valid text loaded with U+FFFD in their place, and the next save replaced the file, and the backup row, with no copy of the original bytes kept. The original bytes are now kept in Recovery before anything writes over the file. The file still loads from itself, as in 0.1.6: its encoding never decides which copy is read (section 10).
- **Low.** Several smaller defects, listed below.

Every fix keeps the saved file formats, schema versions and package format as they were.

## 1. Method

- **Four parallel investigations**, each read-only, with probe tests in scratch copies:
  - A: saving, loading, crash safety and atomic writes.
  - B: import and export validation, malformed files and unsafe paths.
  - C: asset integrity, missing images, file ownership and cleanup safety.
  - D: migrations, backward compatibility, recovery and test coverage.
- **Reconciliation.** Every claimed weakness was checked against the code before being fixed.
- **Tests that prove each fix.** Every fix has a regression test. The tests that fail with their fix removed, and those that only pin behaviour that was already correct, are named per fix in section 3 and in section 6.
- **Adversarial review.** An independent review of the finished diff followed. It found no high- or medium-severity defect. Its five low-severity findings (a Recovery copy losing the damaged file's modified time, two wording issues, the Import window's verdict and the refusal message's advice) are fixed in `d575611`. A second, independent audit of the result followed (section 10).
- **Baseline at `6384db6`**, built and run on Linux with .NET 10.0.401 and Dalamud's reference assemblies:
  - AetherFrame.Tests: 2739 passed.
  - AetherFrame.Protocol.Tests: 153 passed.
  - AetherFrame.ReleaseTools.Tests: 439 passed.
  - The full solution, the Dalamud plugin included, builds with 0 warnings.

## 2. Existing behavior, verified

What was already safe, and stays as it was.

### Atomic writes

- **Plate files and everything else the Library writes in game** (Templates, character bindings, `library.json`) go through Dalamud's `IReliableFileStorage`. It writes a temporary file, replaces the target with `MoveFileEx`, and keeps a backup row.
- **The file and its backup row are not one transaction.** Dalamud moves the new file into place inside its database transaction (`WriteAllBytesSafe` runs inside `RunInTransaction`, checked against the Dalamud build used here). If the commit then fails, the write throws although the file on disk was already replaced, and the backup row still holds the previous content. So a write reported as failed may have landed. Where that matters, AetherFrame already assumes it may have: a failed import moves its half-written file to the trash, and the stuck-temp fallback writes directly and leaves the backup row stale (see "Reads prefer the file on disk" below).
- **Everything outside that storage** is written the same atomic way: the stuck-temp fallback, asset metadata, thumbnails and export files all write a temporary sibling, flush it and rename it (`SystemFileStore.WriteAtomically`, `PackageExporter`).
- **No code in the plugin truncates or writes in place** over user content. There is no `File.WriteAllText`, `FileMode.Create` or `Truncate` anywhere.
- **A failed write never leaves a torn file.** The target holds either the old content or the new, never a mix. It holds the new content in the case above, where Dalamud's commit failed after the move. Either way the player sees a plain error and the editor stays dirty.

### Serialization and concurrency

- **Serialization happens before the store is touched.** Every document is serialized to a complete string first. A value that isn't a number is refused before any file step.
- **Operations are serialized.** Each Library holds a `SemaphoreSlim(1,1)` for the whole operation. `ProfileService` refuses a second save while one is in flight.
- **A deleted Plate can't come back.** A save of a deleted Plate is refused, and delete moves the file to `Trash/Plates` first.

### Damaged, locked and newer files

- **Reads prefer the file on disk.** The file is read first, and the backup is used only when the content is unusable: not valid JSON, or not a usable object of its kind (`ReliableReads`). A file that is locked or missing is "unavailable": it is never written over and never silently replaced by the backup. The file's encoding is never a reason to use the backup (section 5).
- **The backup can be older than the file.** It holds the last write that went through Dalamud's storage. After a stuck-temp direct write, or a commit that failed after the move, it is older than the file. That is why a damaged file's own bytes are kept in Recovery before anything replaces them, and why nothing prefers the backup to a file that is usable.
- **A damaged binding or `library.json`** is either copied to Recovery before being overwritten, or left read-only for the session.
- **A newer version's files are never written.** This covers Plates, Templates, bindings, the index, asset metadata and configuration. They are listed read-only, deleting one moves it intact, and none of them is ever read from an older backup.
- **Unknown data survives.** Unknown fields, elements and Components are carried through every save (`[JsonExtensionData]`, `UnrecognizedElements`, `UnrecognizedComponents`).

### Migrations

- **Migrations run in memory.** Only the pre-Library binding migration writes. It first copies the original bindings to `Backups/pre-plate-library`; a copy that fails is logged and the migration still writes (the migrated binding keeps the original's property names and adds to them, so an older build still reads it). It is idempotent: a third start writes nothing at all.
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
| 2 | Low-Medium | Persistence | A file with bytes that aren't valid text loaded with U+FFFD, and the next save replaced it and its backup row with no copy of the original bytes | `be5d5b0`, corrected by `deb7a7e` |
| 3 | Low | Persistence | Recovery and migration copies were not flushed to disk before the original was replaced, and a copy that failed partway could leave a partial file | `dc92e41`, `17d9c4e` |
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
   - **Now:** such a path is remembered for the session. Every write to it first retries the copy (`PreserveBeforeOverwrite`), and refuses with "AetherFrame couldn't keep a copy of the file this would replace in its Recovery folder, so nothing was written over it. Check that the drive isn't full and the AetherFrame folder isn't read-only, then try again." while the copy still fails.
   - **Tests:** `DamagedFilePreservationTests`, covering Plate save and rename, a binding, `library.json` and a Template, with the copy blocked before it starts. Five of its first six cases fail without the fix; the sixth pins the unchanged case where the copy succeeds at load. Its seventh, `RefusedMove_LeavesTheOrderAsItWas_AndTheSameMoveLaterSavesIt`, fails without `ac8d6e5`, which puts the order back when a Move can't be saved. `RecoveryCopyFaultTests` covers a copy that fails after writing part of its output (fix 3).

2. **Text that isn't valid, written over with no copy kept.**
   - **Before (0.1.6):** a file is decoded leniently, so a byte sequence that isn't valid in the file's encoding becomes U+FFFD. The file loads Ready, and the next save writes the replacement character over the file and its backup row, with no copy of the original bytes anywhere.
   - **First fix, withdrawn (`be5d5b0`):** the readers refused, as damage, any decoded text holding U+FFFD. The independent audit showed this was wrong in several ways (section 10): it can't tell a correctly encoded U+FFFD from an invalid byte, and it ran before the newer-version check, so valid files loaded older backups or became unreadable, a newer version's file lost its protection, and a binding or the Plate order could be rebuilt.
   - **Now (`deb7a7e`):** validity is checked on the bytes, never on the decoded text. `StoredTextDecoder` decodes a file exactly as `File.ReadAllText` does (the text is identical to 0.1.6's for every input; section 5 lists the encodings), then decodes the same bytes again strictly to say whether any sequence was invalid. Dalamud's backup row is read as bytes and decoded the same way. The encoding never decides which copy is read: only unusable content falls back to the backup, and a newer version is recognised before anything else. A usable file with invalid bytes loads from itself, with U+FFFD in their place as before, and is left untouched. Its path is remembered, and its exact original bytes are copied to Recovery right before the first write that would replace them (`KeepInvalidTextFile`, `PreserveBeforeOverwrite`). That write is refused, with the message in fix 1, while the copy fails. A Plate or Template read this way shows a note when its card is hovered: "Part of this Plate's file isn't valid text, so some of its characters may not show correctly. The file is unchanged; AetherFrame keeps a copy of it in its Recovery folder before saving over it."
   - **Why not the backup:** the backup can be older than the file (section 2), and a file that parses is not damaged in any way the Library can check. A hand edit saved in a legacy code page, for example, is newer than the backup. The original bytes, kept before the first overwrite, lose nothing; the backup would silently lose the edit.
   - **Tests:** `InvalidTextEncodingTests`, `EncodingRegressionTests`, `BindingAndIndexEncodingTests`, `VersionedDocumentEncodingTests` and `StoredTextDecoderTests`. Section 6 gives how many fail on `c724cd6`.

3. **Unflushed or partial copies.**
   - **Before:** `SystemFileStore.CopyFile` used `File.Copy`, which gives no durability guarantee, while the write that follows it over the original is flushed. `dc92e41` copied through streams and flushed, but still wrote straight to the copy's final name: a crash partway, or a failure whose cleanup couldn't delete the partial file, could leave a truncated file under the name the Plate loader takes as proof that a copy was kept.
   - **Now (`17d9c4e`):** the copy is written to a temporary sibling (`.{name}.{id}.tmp`), flushed to disk, given the source's modified time, and only then renamed to its final name, never over an existing file. A copy that fails partway leaves nothing under the copy's name, and its temporary file, only ever the one that call created, is removed. Like `File.Copy`, it lets other programs keep the source open.
   - **Why the modified time matters:** for a damaged file, it is the evidence of whether the damaged bytes are newer than the backup.
   - **Tests:** `SystemFileStoreCopyTests` pins the exact bytes, the modified time, the no-overwrite rule and that a failed copy creates nothing. `RecoveryCopyFaultTests` injects a failure after part of the copy has been written, as a full disk does, through an internal constructor that supplies the copy's stream. It checks that the original stays untouched, the write is refused, nothing partial is left, a retry succeeds once the fault is gone, and the kept copy holds exactly the original bytes. It covers the store itself and each Library path: a damaged Plate read from its backup, a Plate, binding, Plate order and Template read with invalid bytes, and a reload after a failed copy. Replacing the copy with plain `File.Copy`, or with `dc92e41`'s, makes most of these tests fail (section 6). A flush can't be observed for durability, only that it was requested before the copy got its name.

4. **Writes that might not load again.**
   - **Now:** each Plate and Template write serializes once, proves that exact text makes the UTF-8 round trip a file makes unchanged (`VersionedJson.RequireFaithfulReadBack`: only text holding a lone surrogate or starting with U+FEFF could fail it, and the serializer produces neither), runs it through the startup reader, and writes it only if it would load as Ready (`PreparePlateWrite`, `PrepareTemplateWrite`). Otherwise the write is refused with "AetherFrame couldn't save this change: the result wouldn't load again, so nothing was written." The Library's record is built from the same text.
   - **Scope:** no input reachable from the editor is known to produce such text. The check guards the Library's own entry points. Since `dbad379`, the serialization itself is inside the check, so a Template of a Plate nested to the reader's limit (reachable through a newer build's data) is refused with this message instead of the serializer's own text.
   - **Tests:** `WriteReadBackTests` for Plates. The import and create-from-document tests fail without the fix: an imported or instantiated document carrying a newer schema version was written, and was listed as a newer version's Plate after a restart. `TemplateWriteReadBackTests` covers Templates. The byte-level round trip itself can't be triggered through any realistic input, since the serializer's output is always ASCII; `StoredTextDecoderTests` covers it at unit level.

5. **Duplicate reporting a failure that wasn't one.**
   - **Now:** a failed `library.json` write after the copy is saved is logged, as in Create, Import and Delete, instead of being thrown. The copy is listed, and startup re-lists it.
   - **Test:** `WriteReadBackTests.DuplicateWhoseOrderWriteFails_…` fails without the fix.

6. **Export wording.**
   - **Before:** a Plate saved by 0.1.5 with a typed font size over 1024, or a hand-edited Plate with a repeated element id, opens fine but is refused by the export self-check.
   - **Now:** the player sees "This Plate holds a value a Plate file can't carry (font size out of range). Change it in the editor and save, then export again." The detail is the validator's fixed text, omitted if it ever looks like a path. Since `d04d556`, only a typed value the editor shows gets that advice: a refusal of the raw structure, or of data the editor keeps without showing it, says "This Plate holds data a Plate file can't carry, so it can't be exported." and keeps its detail for the log. Any other self-check refusal keeps the check's own message, as before, such as "too many elements".
   - **Tests:** `PackageReliabilityTests`.

7. **Images riding along.**
   - **Now:** a package image counts as used only where `AssetReferenceScanner` finds it. That is the scan export and cleanup already use, and it still counts any id in data from newer builds. Since `d04d556`, the check runs again on the document actually imported, after its images are re-pointed, so an id the scan finds only in a form the import doesn't re-point is refused too.
   - **Compatibility:** every historical fixture package, and every package the exporter produces, still imports.

8. **Invalid UTF-8 in package strings.**
   - **Now:** `PackageJson.Screen` checks string values as well as property names, so the profile or manifest is refused as damaged. `PackageManifest.Parse` no longer throws.

9. **Import retry.**
   - **Now:** `StagedPackage` becomes non-importable once its Plate is on disk: after a success, or after an unfinished import whose Plate stayed. After a success the Import window closes, as before. After an unfinished import (the Plate file landed, the import reported a failure, and the file couldn't be moved away) it stays open and shows "Already imported" instead of "Ready to import", keeps the preview and disables the button.
   - **Tests:** `PackageReliabilityTests.ImportWhosePlateWasWrittenButUnfinished_CantBeImportedAgain` and `SuccessfulImport_CantBeRepeatedFromTheSameCheckedFile`. The unfinished case needs fault injection; it can't be produced in game.

10. **The dormant cleanup's reference scan.** This is not active: nothing calls the cleanup. The scan now also counts:
    - every GUID in a Template envelope's and its Origin's preserved data;
    - every GUID in any file in the Plates folder that isn't a loaded Plate, such as a Plate put back from the trash by hand while running, or a `"<id> - Copy.json"`;
    - since `a1c8d19`, the same for the Templates folder, and for a loaded file whose bytes on disk aren't what memory holds and have no Recovery copy yet.

    A file that can't be read, or that only its backup can answer for, leaves the scan incomplete, which blocks cleanup. The change can only protect more images, never fewer.

11. **Add Image extension.**
    - **Before:** the stored file was named after the first inspection of the source, not after the copy that was validated.
    - **Now:** it takes the extension of the stored copy's content, as package imports already did.

12. **Locked-file wording.**
    - **Now:** an unreadable Plate or Template whose file couldn't be opened, as opposed to one whose content is damaged, says "couldn't be opened; another program may be using it … Restart the game to try again". The log line says "could not open".
   - **How the two are told apart:** only an I/O or access failure counts as "couldn't be opened". Any content failure keeps "damaged".
    - **Tests:** `UnopenableFileDiagnosticsTests`. The locked Plate and Template cases fail without the fix. `TemplateWhoseContentFailsToMaterialize_IsCalledDamaged_NotUnopenable` pins that a content failure keeps "damaged", which it already did.

## 4. Every behavior change

Everything a player or an existing file can notice:

| Change | Who sees it |
|---|---|
| A write to a file whose damaged bytes couldn't be kept in Recovery is refused, with a message, until a copy succeeds | Only after a damaged file was read from its backup and the Recovery copy failed at load: in practice, a full disk |
| A Plate, Template, binding or `library.json` with bytes that aren't valid text loads as before, from the file itself, but its original bytes go to Recovery before the first write over it, and that write is refused, with a message, while the copy fails | Only files damaged in place or hand-edited in another encoding. AetherFrame writes only ASCII, so its own files never have invalid bytes |
| A Plate or Template read with invalid bytes shows a note when its card is hovered | The same files |
| A write whose text wouldn't load as Ready is refused, with a message | No known editor path; only direct internal misuse |
| Duplicate succeeds even when the order file couldn't be written; the failure is logged | Only when writing `library.json` fails |
| Exporting an over-limit Plate names the value instead of saying the file is damaged | Plates saved by 0.1.5's unbounded sliders, or hand edits |
| Exporting a Plate refused for data the editor doesn't show (an element that isn't an object, a number in a newer build's data) says "This Plate holds data a Plate file can't carry, so it can't be exported." with no advice to change it in the editor | Hand edits |
| A package whose image is used only in a form the import doesn't re-point, next to an exact spelling of its id in a text field, is refused ("The file contains an image the Plate doesn't use.") | Crafted packages; AetherFrame never exports one |
| A Move whose order write fails or is refused puts the previous order back, so the same move can simply be made again | Only when writing `library.json` fails or is refused |
| Set Active, or creating a Plate for a character, whose damaged binding can't be copied to Recovery shows the Recovery message instead of "See the Dalamud log" | Only a damaged binding with no backup, on a full disk |
| Save as Template of a Plate nested to the reader's limit is refused with "the result wouldn't load again, so nothing was written", not the serializer's own text | Only Plates holding a newer build's deeply nested data |
| Recovery and trash file names are stamped in the Gregorian calendar | Players whose Windows culture uses another calendar (Thai, Saudi); before, those names held another year |
| A package whose image is referenced only from text is refused ("The file contains an image the Plate doesn't use.") | Crafted packages; AetherFrame never exports one |
| A package with invalid UTF-8 in a text value is refused as damaged instead of "isn't a valid AetherFrame Plate file" | Corrupted or crafted packages |
| A checked package can't be imported twice from the same Import window, which then says "Already imported" | Only after an unfinished import: the Plate file landed, the import reported a failure and the file couldn't be moved away. After a success the window closes, as before |
| Add Image names the stored file after the stored content | Only if the source changed format while being added |
| A locked Plate or Template is described as "couldn't be opened", not "damaged" | Files held by another program at load |
| Recovery and migration backup copies are flushed to disk and given their name only when complete; like before, they keep the original's modified time | Not visible, apart from a hidden `.{name}.{id}.tmp` file in `Recovery` if the game crashes mid-copy |
| The dormant cleanup's scan protects more images: it also reads unloaded files in the Templates folder, and a loaded file whose bytes on disk aren't yet kept in Recovery; a file only its backup can answer for makes the scan incomplete | Not visible: cleanup doesn't run |

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
- **No existing file loads differently.** Every file decodes to exactly the text 0.1.6 read, from the same copy. The only differences for a file with bytes that aren't valid text are the hover note and the Recovery copy kept before its first overwrite. `StoredTextDecoderTests` compares the decoder with `File.ReadAllText` over a seeded corpus of valid and invalid inputs in every encoding.
- **No migration was added.** No format change was needed.

### Supported encodings

What AetherFrame reads, unchanged from 0.1.6:

- **UTF-8**, with or without a byte order mark. This is all AetherFrame, and Dalamud's storage, ever write: UTF-8 with no byte order mark, and in practice ASCII, since the serializer escapes everything else.
- **UTF-16**, little- or big-endian, and **UTF-32**, little- or big-endian, when the file starts with its byte order mark. Such a file can only come from a hand edit. The next save rewrites it as UTF-8.
- **Anything else** is read as UTF-8, as it always was. A UTF-16 file without a byte order mark holds NUL characters and isn't valid JSON, so it is damage, handled as such.

Releases before 0.1.6 read files in game through Dalamud's own text read, which decodes UTF-8 and keeps a byte order mark as text. 0.1.6 moved to `File.ReadAllText`, and this branch keeps exactly that decoding.

A sequence that isn't valid in the file's encoding is detected on the bytes: an invalid, overlong or truncated UTF-8 sequence, an encoded surrogate, a lone UTF-16 surrogate, an odd trailing byte, or a UTF-32 value outside Unicode. A correctly encoded U+FFFD is ordinary text. Dalamud's backup rows are always valid UTF-8, since they hold what `Encoding.UTF8` encoded.

## 6. Tests

| | Before (`6384db6`) | At the audit (`c724cd6`) | After the corrections |
|---|---|---|---|
| AetherFrame.Tests | 2739 passed | 2798 passed | **3023 passed** |
| AetherFrame.Protocol.Tests | 153 passed | 153 passed | 153 passed |
| AetherFrame.ReleaseTools.Tests | 439 passed | 439 passed | 439 passed |
| Solution build (Release, Dalamud plugin included) | 0 warnings, 0 errors | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `validate-package` on the built package | OK | OK | OK |

- **Test files added by the first pass:** `DamagedFilePreservationTests`, `InvalidTextEncodingTests` (rewritten by the correction, see below), `SystemFileStoreCopyTests`, `WriteReadBackTests`, `PackageReliabilityTests`, `AssetReferenceCoverageTests`, `CompatibilityPreservationTests` and `UnopenableFileDiagnosticsTests`.
- **Test files added by the correction:** `EncodingRegressionTests`, `BindingAndIndexEncodingTests`, `VersionedDocumentEncodingTests`, `RecoveryCopyFaultTests`, `StoredTextDecoderTests` and `TemplateWriteReadBackTests`. Existing files gained cases for the review's other findings.
- **Against the audited code.** The three files that use only APIs `c724cd6` already had were run there. `EncodingRegressionTests` fails 25 of 27 cases, `BindingAndIndexEncodingTests` 19 of 19, and `VersionedDocumentEncodingTests` 20 of 56. The cases that pass there pin what must not change: real damage still reads the backup; a recovered Plate saves and renames with its damaged bytes kept once; and every historical fixture, re-encoded with each byte order mark, loads and saves exactly as its ASCII original (36 cases). `InvalidTextEncodingTests` uses the new note constants and the decoder, so it can't be built there; its old version asserted the withdrawn behaviour.
- **Against mutations.** `RecoveryCopyFaultTests` injects a disk that fills up partway through a copy. Replacing the copy with plain `File.Copy` fails 14 of its 21 cases, and so does the previous stream copy of `dc92e41`, whose partial file a crash would have left under the copy's own name. A rename allowed to overwrite fails its mid-copy case. `StoredTextDecoderTests` compares the decoder with `File.ReadAllText` on 2000 seeded files and at every buffer boundary; relaxing any strict decoder, or keeping a byte order mark in the strict check, fails between 9 and 24 of its 80 cases.
- **Negative checks, precisely.** For each fix, the tests named in section 3 were run with that fix removed. The ones that failed are named there. Those that pass either way pin behaviour that was already correct: `DamagedFilePreservationTests.DamagedPlateWhoseCopySucceedsAtLoad_SavesAsBefore`, `WriteReadBackTests.EveryWrite_PutsOnDiskTheTextTheLibraryKeeps`, `UnopenableFileDiagnosticsTests.TemplateWhoseContentFailsToMaterialize_IsCalledDamaged_NotUnopenable`, `SystemFileStoreCopyTests`, `CompatibilityPreservationTests` and the byte order mark cases above.
- **Isolation.** All of them use isolated temporary directories and never touch a real Dalamud configuration.
- **Where they ran.** Locally on Linux, and in CI on `windows-2022` and `ubuntu-24.04` (`build.yml`) for every pushed commit.
- **An independent reproduction** of the audit's six scenarios, written separately from these tests and not committed, fails all 7 of its cases on `c724cd6` and passes them all on the corrected branch.

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
| D13 | A version number out of `int` range is read as damage, not as a newer version, so a binding with one is replaced after a Recovery copy. | No build writes one. |
| D14 | `Vector2`, `Vector4` and `ElementRect` values carry no extension data, so unknown sub-fields inside them are dropped on a model save. | Forward compatibility only. It would need a model change. |
| D15 | No bounded retry on transient sharing violations (antivirus, indexers). | A failed save keeps the old file and the editor dirty, which is acceptable. A retry policy is optional polish. |
| D16 | The pre-Library binding migration still writes when copying the originals to `Backups/pre-plate-library` fails (section 2). | Refusing needs a per-binding "not backed up" guard on every binding write, a design change. The migrated binding keeps the original's property names and adds to them, so nothing an older build reads is lost; the risk is only losing the exact original bytes, on a failure that hits the backup copy but not the write. |
| D17 | A write Dalamud reports as failed may have replaced the file already (section 2). The Library then keeps the previous record in memory while the file holds the new text, until the next load. | Adopting the landed text after a failure touches every write path and how each failure is presented. The player saw an error, the editor stays dirty, and saving again converges. |
| D18 | A file with bytes that aren't valid text loads from itself, so when the damage was in the file and Dalamud's backup row still holds the intact text, the next save replaces that row too. | Deliberate (section 3, fix 2): the backup can be older than the file, and preferring it loses newer content and edits. The file's exact original bytes are kept in Recovery before that save, the damaged characters show, and the Plate's card says so. Keeping the backup row's text in Recovery as well would need the store to read a backup the reader didn't ask for. |

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
   5. Save that Plate: the editor shows "AetherFrame couldn't keep a copy of the file this would replace in its Recovery folder…" and stays dirty, and the file is still truncated.
   6. Delete the `Recovery` file and save again: it succeeds, and `Recovery/<guid>.damaged-*.json` holds the truncated bytes.
4. **Invalid bytes.**
   1. With the plugin unloaded, hash a saved Plate's file, then use a hex editor to change one letter of its name to `FF`.
   2. Load. The card shows the name with a replacement character in place of that letter, not the backup's name, and hovering the card shows the note about text that isn't valid. The log has a Warning. The file's hash still matches the edited file, and `Recovery` has nothing for it.
   3. Create the `Recovery` file as in step 3, then save the Plate: it is refused with the Recovery message, and the file is unchanged.
   4. Delete the `Recovery` file and save again: it succeeds, `Recovery/<guid>.damaged-*.json` holds exactly the edited bytes, and after a plugin reload the Plate has no note.
5. **Valid unusual text.** With the plugin unloaded, hand-edit a Plate's name to hold a real `�` character (U+FFFD, saved as UTF-8), and save a second Plate's file as UTF-16 with a byte order mark (Notepad's "UTF-16 LE"). Load: both are Ready with the exact names, no note, and nothing in `Recovery`.
6. **Newer version.** With the plugin unloaded, set a Plate's `"Version"` to 99 and add a `�` to its name. Load: it is listed as saved by a newer version, never shows the backup's content, and its file's hash is unchanged after trying to rename or set it Active.
7. **Character with invalid text.** With the plugin unloaded, change one letter of `LastKnownCharacterName` in `Characters/<id>.json` to `FF`. Load: every Plate still shows as associated with that character, and the Active Plate is unchanged. Set another Plate Active: the associations stay, and `Recovery` holds the edited binding.
8. **Locked file.** With the plugin unloaded, hold a Plate file open with no sharing, for example in PowerShell: `$f=[IO.File]::Open("<path>",'Open','Read','None')`. Load: the card's hint says the file couldn't be opened and asks for a restart, not that it is damaged. Close the handle and reload: the Plate is Ready.
9. **Import again.** Import a package: the window closes and the new Plate is listed. Choose the same file again: it imports as another new Plate, as before. (The "Already imported" verdict appears only after an unfinished import, which needs fault injection; `PackageReliabilityTests` covers it.)
10. **Duplicate while the order can't be written.** Make `Library/library.json` read-only, then Duplicate a Plate. The copy appears with no error message, and the log has an Error about the Library order. Undo the read-only flag and reload: both Plates are listed.
11. **Export an over-limit Plate.** Export a Plate saved by 0.1.5 with a typed font size over 1024, or hand-edit a text's `FontSize` to 2000. The message names "font size out of range" and asks to change it and save, and no file is written.
12. **Import.** Round-trip export and import a Plate with images. Also import a package another tester exported with 0.1.5 or 0.1.6: both import as before, and nothing existing is replaced.
13. **Add Image.** Add a PNG, a JPEG and a WebP. Each stored file in `assets/` has the extension matching its content, and each displays.

Anything failing in steps 1 to 7 or 12 blocks a release.

## 9. What could not be checked in the cloud

- **The game itself.** Everything in section 8 needs FFXIV: Dalamud's `IReliableFileStorage` in game, ImGui, and real Windows file locking and antivirus behavior.
- **Windows builds and tests** run only in GitHub CI.
- **Durability.** Flush-to-disk behavior under power loss can't be observed by a test.

## 10. Independent audit of `c724cd6`, and the corrections

An independent adversarial review of this branch at `c724cd6` confirmed regressions introduced by fix 2 as it then stood (`be5d5b0`). It also found the Recovery copy's central behaviour under-tested, and raised several low-severity issues. Nothing was merged or released in between.

### Confirmed regressions

`be5d5b0` refused, as damage, any decoded text holding U+FFFD, before the schema check. Decoded text can't tell a correctly encoded U+FFFD (bytes `EF BF BD`) from an invalid byte, so:

| # | Consequence | Why |
|---|---|---|
| R1 | A valid Plate holding a literal U+FFFD loaded an older backup in its place | The refusal made the store retry from its backup |
| R2 | With no backup, that valid Plate became Unreadable | The refusal was content damage |
| R3 | A hand edit saved in a legacy code page fell back to the pre-edit backup, or became unreadable | Its invalid bytes were damage, although the file parsed |
| R4 | A newer version's file holding U+FFFD was replaced by an older backup, losing its newer-version protection, and the next save could write over it | The refusal ran before the newer-version check |
| R5 | A binding with damaged text loaded an older backup's associations, or was unreadable, and its next write started a fresh binding, losing its Plate associations | Unreadable bindings are replaced after a Recovery copy |
| R6 | A Plate order file with unusual text was rebuilt, losing the player's order | A damaged index is rebuilt from the Plates |

### Corrections

- **`deb7a7e`, byte-level validity.** See fix 2 in section 3. The encoding never decides which copy is read; a newer version is recognised first; a usable file with invalid bytes loads from itself, as in 0.1.6, and its original bytes are kept in Recovery before its first overwrite, which is refused while that copy fails. Nothing is ever recreated because of text.
- **`17d9c4e`, a copy that is complete or absent.** See fix 3 in section 3.
- **Tests.** The tests of the withdrawn behaviour in `InvalidTextEncodingTests` asserted R1 to R3 and were rewritten to the corrected contract. The new regression tests reproduce each confirmed scenario (section 6 has the counts, and which fail on `c724cd6`).

### Low-severity findings

The review also reported low-severity issues in the rest of the branch. Each was checked against the code by a second reviewer before being acted on.

| Finding | Outcome |
|---|---|
| A Move refused by the Recovery rule stayed applied in memory, so repeating it saved nothing | Fixed in `ac8d6e5`: the previous order is put back |
| Export told the player to change "a value" in the editor for data the editor doesn't show, and could quote a log-only detail | Fixed in `d04d556`: only typed values get that advice |
| A package's "every image is used" check ran before its images were re-pointed, so an id used only in an unmapped form passed | Fixed in `d04d556`: the check runs again on what is imported |
| A damaged binding whose Recovery copy failed gave a generic error | Fixed in `a1c8d19` |
| The load logged "kept in Recovery" before the copy, even when it failed | Fixed in `a1c8d19` |
| The dormant cleanup's scan skipped unloaded Template files, and trusted memory for a loaded file whose damaged bytes had no Recovery copy | Fixed in `a1c8d19`; cleanup stays inactive |
| "A third start writes nothing" was checked by file hashes only | Fixed in `fc3d728`: every write is counted |
| The report called writes atomic and a failed write harmless, although Dalamud can move the file before its commit fails | Corrected (section 2); in-memory adoption deferred (D17) |
| The report said the migration writes only once its backups exist | Corrected (section 2); enforcement deferred (D16) |
| The report said a successful import shows "Already imported" | Corrected (fix 9, section 4, step 9 of section 8) |
| The report said every fix's tests fail without it | Corrected (sections 1 and 6) |
| The Recovery copy could leave a partial file under its name after a crash | Fixed by `17d9c4e` (fix 3) |
| Recovery and trash names were stamped with the player's calendar (found reviewing the new tests) | Fixed in `3e1ad14` |
| Save as Template of a Plate nested to the reader's limit showed the serializer's own text (found writing the new tests) | Fixed in `dbad379` |
