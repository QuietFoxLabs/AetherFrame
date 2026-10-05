# Plate Library reliability and data preservation

This report covers a review of the local Plate Library, its persistence, backup, import and export, and the fixes that followed. It was written after v0.1.6, on top of `master` at `6384db6` (the NETWORK0 merge).

**Revised after an independent audit.** An independent adversarial review of this branch at `c724cd6` confirmed that one of its fixes, the treatment of text that isn't valid (fix 2, `be5d5b0`), lost data in several ways, and that the Recovery copy's central behaviour was not tested under a copy that fails partway. Both are corrected, with regression tests. Section 10 describes the audit's findings and the corrections; sections 3 to 8 describe the branch as it now stands.

**Scope.** It covers local files only: Plates, Templates, character bindings, the Plate order (`library.json`), images and their metadata, Recovery and backup copies, and `.aetherframe` packages. Section 11, added later, covers the unsaved changes an editor had when AetherFrame unloaded, kept in `Drafts/`. The NETWORK0 protocol, networking, the plugin's distribution and the saved file formats are out of scope and unchanged.

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
- **Tests that prove each fix.** Every fix has a regression test. For fixes 1 to 5, 9 and 12, section 3 names which tests fail with the fix removed and which only pin behaviour that was already correct; for the others it names the test class. Section 6 gives what was measured against the audited code and against mutations.
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

- **Reads prefer the file on disk.** The file is read first, and the backup is used only when the content is unusable: not valid JSON, or not a usable object of its kind (`ReliableReads`). A file that is locked or missing is "unavailable": it is never written over and never silently replaced by the backup. Bytes that aren't valid text are never on their own a reason to use the backup; a file in an encoding the reader doesn't detect (UTF-16 or UTF-32 without a byte order mark) doesn't parse, and is damage, as in 0.1.6 (section 5).
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
   - **Before:** `SystemFileStore.CopyFile` used `File.Copy`, which gives no durability guarantee, while the write that follows it over the original is flushed. `dc92e41` copied through streams and flushed, but still wrote straight to the copy's final name: a crash partway, or a failure whose cleanup couldn't delete the partial file, could leave a truncated file under a Recovery copy's name, where it looks like a kept copy. (The loader itself only skips a copy whose exact, millisecond-stamped name already exists, so in game it never took such a leftover for its own copy; the tests' frozen clock does.)
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
| A Plate or Template read with invalid bytes shows a note when its card is hovered, together with the unsupported-elements warning when that applies too | The same files |
| A write whose text wouldn't load as Ready is refused, with a message | No known editor path; only direct internal misuse |
| Duplicate succeeds even when the order file couldn't be written; the failure is logged | Only when writing `library.json` fails |
| Exporting an over-limit Plate names the value instead of saying the file is damaged | Plates saved by 0.1.5's unbounded sliders, or hand edits |
| Exporting a Plate refused for data the editor doesn't show (an element that isn't an object, a number in a newer build's data) says "This Plate holds data a Plate file can't carry, so it can't be exported." with no advice to change it in the editor | Hand edits |
| A package whose image is used only in a form the import doesn't re-point, next to an exact spelling of its id in a text field, is refused ("The file contains an image the Plate doesn't use.") | Crafted packages; AetherFrame never exports one |
| A Move whose order write fails or is refused puts the previous order back, so the same move can simply be made again | Only when writing `library.json` fails or is refused |
| Set Active on a character whose binding must first be kept in Recovery (damaged with no backup, or read with invalid bytes) shows the Recovery message instead of "See the Dalamud log" when that copy fails; Create, Use Template and Duplicate for that character still make the Plate and add the Recovery message to their "couldn't be linked" text | Only such a binding, on a full disk |
| Save as Template of a Plate nested to the reader's limit is refused with "the result wouldn't load again, so nothing was written", not the serializer's own text | Only Plates holding a newer build's deeply nested data |
| Recovery and trash file names are stamped in the Gregorian calendar | Players whose Windows culture uses another calendar (Thai, Saudi); before, those names held another year |
| A package whose image is referenced only from text is refused ("The file contains an image the Plate doesn't use.") | Crafted packages; AetherFrame never exports one |
| A package with invalid UTF-8 in a text value is refused as damaged instead of "isn't a valid AetherFrame Plate file" | Corrupted or crafted packages |
| A checked package can't be imported twice from the same Import window, which then says "Already imported" | Only after an unfinished import: the Plate file landed, the import reported a failure and the file couldn't be moved away. After a success the window closes, as before |
| Add Image names the stored file after the stored content | Only if the source changed format while being added |
| A locked Plate or Template is described as "couldn't be opened", not "damaged" | Files held by another program at load |
| Recovery and migration backup copies are flushed to disk and given their name only when complete; like before, they keep the original's modified time | Not visible, apart from a leftover `.{name}.{id}.tmp` file (visible in Explorer) in `Recovery`, or in `Backups/pre-plate-library` during a migration, if the game crashes mid-copy |
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

| | Before (`6384db6`) | At the audit (`c724cd6`) | After the corrections (`f07c10e`, and `6684a74` with `master` merged in) |
|---|---|---|---|
| AetherFrame.Tests | 2739 passed | 2798 passed | **3032 passed** |
| AetherFrame.Protocol.Tests | 153 passed | 153 passed | 153 passed |
| AetherFrame.ReleaseTools.Tests | 439 passed | 439 passed | 439 passed |
| Solution build (Release, Dalamud plugin included) | 0 warnings, 0 errors | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `validate-package` on the built package | OK | OK | OK |

- **Test files added by the first pass:** `DamagedFilePreservationTests`, `InvalidTextEncodingTests` (rewritten by the correction, see below), `SystemFileStoreCopyTests`, `WriteReadBackTests`, `PackageReliabilityTests`, `AssetReferenceCoverageTests`, `CompatibilityPreservationTests` and `UnopenableFileDiagnosticsTests`.
- **Test files added by the correction:** `EncodingRegressionTests`, `BindingAndIndexEncodingTests`, `VersionedDocumentEncodingTests`, `RecoveryCopyFaultTests`, `StoredTextDecoderTests` and `TemplateWriteReadBackTests`. Existing files gained cases for the review's other findings.
- **Against the audited code.** The three files that use only APIs `c724cd6` already had were run there. `EncodingRegressionTests` fails 25 of 27 cases, `BindingAndIndexEncodingTests` 19 of 19, and `VersionedDocumentEncodingTests` 21 of 65. The cases that pass there pin what must not change: real damage still reads the backup; a recovered Plate saves and renames with its damaged bytes kept once; every historical fixture, re-encoded with a UTF-8, UTF-16 or UTF-32 byte order mark, loads and saves exactly as its ASCII original (40 cases, both byte orders of UTF-16 and UTF-32); and the legacy bindings without a U+FFFD and the guard that every fixture is covered pass too. `InvalidTextEncodingTests` uses the new note constants and the decoder, so it can't be built there; its old version asserted the withdrawn behaviour.
- **Against mutations.** `RecoveryCopyFaultTests` injects a disk that fills up partway through a copy. Replacing the copy with plain `File.Copy` fails 18 of its 21 cases. The previous stream copies (`dc92e41`'s, and `c724cd6`'s, which also kept the modified time) each fail 14, because a crash at the fault would have left their partial file under the copy's own name. A rename allowed to overwrite fails its mid-copy case. `StoredTextDecoderTests` compares the decoder with `File.ReadAllText` on 2000 seeded files and at every buffer boundary. Making one strict decoder lenient fails 21 of its 80 cases for UTF-8, 9 for UTF-16 little-endian, 5 for UTF-16 big-endian, 6 for UTF-32 little-endian and 4 for UTF-32 big-endian; keeping a byte order mark in the strict check fails 24.
- **Negative checks, precisely.** For fixes 1 to 5, 9 and 12, and for every correction, the tests named were run with that fix removed. The ones that failed are named in section 3 or in the commit that adds them. Those that pass either way pin behaviour that was already correct: `DamagedFilePreservationTests.DamagedPlateWhoseCopySucceedsAtLoad_SavesAsBefore`, `WriteReadBackTests.EveryWrite_PutsOnDiskTheTextTheLibraryKeeps`, `UnopenableFileDiagnosticsTests.TemplateWhoseContentFailsToMaterialize_IsCalledDamaged_NotUnopenable`, `SystemFileStoreCopyTests`, `CompatibilityPreservationTests` and the byte order mark cases above.
- **Isolation.** All of them use isolated temporary directories and never touch a real Dalamud configuration.
- **Where they ran.** Locally on Linux for every commit up to `f07c10e`; on Windows (Release, Dalamud API 15) at `6684a74`, the branch with `master` at `45f32e3` merged in, with the same counts, 0 warnings and `validate-package` OK; and in CI on `windows-2022` and `ubuntu-24.04` (`build.yml`) for each push to the pull request, on its merge with `master` (the tip of every push, not each commit).
- **An independent reproduction** of the audit's six scenarios, written separately from these tests and not committed, fails all 7 of its cases on `c724cd6` and passes them all on the corrected branch.

## 7. Deferred findings

D1 and D18 can lose data in the narrow cases they describe, and D16 an original's exact bytes; none of the others puts existing data at risk today. Each needs a product decision, touches Dalamud-side code that can't be tested here, or is only relevant once a dormant feature is switched on.

| # | Finding | Why deferred |
|---|---|---|
| D1 | A configuration file damaged beyond Dalamud's own backup is replaced by the defaults and saved with no copy kept. That loses a newer build's settings held in its extension data. | The file holds settings, not Plates, and the fix lives in `Plugin.cs`, which can't be tested here. Suggested fix: copy `ConfigFile` to `Recovery` before the first save, and skip the save if the copy fails. |
| D2 | Elements and Components this build doesn't recognize move to the end of their lists on a model save. After a downgrade, a newer build may stack them differently. | Needs the original positions carried in memory, and only matters on a downgrade within the same schema version. |
| D3 | There is no v0.1.5 or v0.1.6 fixture set, and no fixture generator in the repository. | The formats are identical to v0.1.4 (section 5), so a new set adds little until a format changes. Suggestion: add an opt-in generator test (the protocol tests' `AETHERFRAME_PROTOCOL_REGENERATE_VECTORS` pattern) and a Releasing step, before the next format change. |
| D4 | Prerequisites before asset cleanup is ever switched on. See the list below the table. | Cleanup has no caller. Each of these must be settled when it is activated. |
| D5 | A Plate with repeated element ids from a hand edit can't be exported until it is saved with the ids repaired. | Exporting the repaired ids would change export output. That is a product decision. |
| D6 | When a Plate is served from a stale backup, only the log says so. | UI and product decision (a notice in My Plates). The card note added for text that isn't valid (fix 2) could carry it, once its wording and when it clears are decided. |
| D7 | No autosave, draft or version history: unsaved edits are lost on a crash, and each save replaces both the file and its backup. **Partly addressed (October 2, 2026, section 11):** an editor's unsaved changes are now kept when AetherFrame unloads (a test build's reload, an update, a disable, the game closing) and offered back at the next load. A crash still loses them, since nothing is written while editing, and there is still no autosave or version history. **[updated October 5, 2026, section 12]:** recovery checkpoints are now written while editing, so a crash loses at most the last few seconds; still no autosave or version history. | Product decision: autosave stays an OPEN product choice. |
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
- **Kept unsaved changes, `Drafts/` and `Trash/Drafts/`, must stay scanned.** A draft may be the only thing still using an image added while editing (section 11). The dormant scan already reads both folders, counting every GUID string in each file; one it can't read, or that a newer version wrote, leaves the scan incomplete.
- **The editor's `AssetsInUse` must be a required input to `Plan`.**
- **Uppercase-named asset files need handling.** They can be trashed but not restored.
- **A save that landed but reported failure (D17) must be scanned from disk.** Memory still holds the previous document, so the scan would miss images only the new file references. Reading every loaded Plate from disk instead would leave the scan incomplete for any Plate recovered from its backup.
- **Stray files must not win.** `ResolveAssetPath` picks the first `{id}.*` file alphabetically, so a stray `{id}.bak` wins. It can also throw from Draw when the folder can't be listed.

No specific change was stopped for being incompatible or destructive: none of the fixes needed a format change or a migration.

## 8. Manual acceptance in FFXIV

[updated 2026-10-02: this branch merged as 8077689 (#21) and shipped in 0.1.7, so these steps apply to any release from 0.1.7 on; ROADMAP.md section 2 (Known bugs and verification gaps) and section 8, and the Owner inbox, hold the recorded results.]

These need the game. Use Windows, Dalamud API 15, and this branch's build installed as a dev plugin. Keep `/xllog` open. "Unloaded" means disabled in `/xlplugins`.

**Data and backups.** Under the autopilot ([docs/process/AUTOPILOT.md](../process/AUTOPILOT.md#test-builds)), this branch reaches the game as a test build of `master`. Before installing it, the autopilot copies `%APPDATA%\XIVLauncher\pluginConfigs\AetherFrame\` and `AetherFrame.json` to a new folder under `E:\AetherFrame Archives\Acceptance backups\`, and the Owner inbox post names that folder. Step 1 runs on that real data. Step 2's rename, duplicate and delete, and steps 3 to 8, 10 and 11, change, damage, lock or edit files on purpose: use Plates made for the test, never the only copy of real work. Dalamud's own backup rows (the copy a damaged file is read from in step 3) live in Dalamud's storage, not in that folder, so the Acceptance backup is what restores the data folder if a step goes wrong.

**Results.** None of these steps has been run on this branch yet. Every result is OPEN until the owner reports it in the Owner inbox.

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
   1. With the plugin unloaded, use a hex editor to change one letter of a saved Plate's name to `FF`, then hash the edited file.
   2. Load. The card shows the name with a replacement character in place of that letter, not the backup's name, and hovering the card shows the note about text that isn't valid. The log has a Warning. The file's hash still matches the edited file, and `Recovery` has nothing for it.
   3. Move the `Recovery` folder aside, create the `Recovery` file as in step 3, then save the Plate: it is refused with the Recovery message, and the file is unchanged.
   4. Delete the `Recovery` file and save again: it succeeds, `Recovery/<guid>.damaged-*.json` holds exactly the edited bytes, and after a plugin reload the Plate has no note. Move step 3's copies back.
5. **Valid unusual text.** With the plugin unloaded, hand-edit a Plate's name to hold a real `�` character (U+FFFD, saved as UTF-8), and save a second Plate's file as UTF-16 with a byte order mark (Notepad's "UTF-16 LE"). Load: both are Ready with the exact names, no note, and nothing new in `Recovery`.
6. **Newer version.** With the plugin unloaded, set a Plate's `"Version"` to 99, add a `�` to its name, and hash the file. Load: it is listed as saved by a newer version and never shows the backup's content; Rename, Set Active, Duplicate, Save as Template and Export are disabled on its card; and after a plugin reload its file's hash is unchanged.
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
- **Windows builds and tests** ran in GitHub CI and, at `6684a74`, locally on Windows; neither runs the game.
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
| A damaged binding whose Recovery copy failed gave a generic error | Fixed in `a1c8d19` for Set Active, and in `f641dcf` for Create, Use Template and Duplicate |
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

### Fresh review of the correction

A new adversarial review of the correction (`c724cd6..17ff3ea`), across data loss, Unicode, backups and schema versions, bindings and the Plate order, and the accuracy of this report, found no high- or medium-severity defect. Its Unicode reviewer found nothing. Each other finding was checked by a second reviewer:

| Finding | Outcome |
|---|---|
| Create, Use Template and Duplicate replaced the Recovery refusal for a binding with a bare "couldn't be linked" | Fixed in `f641dcf` |
| The card note for invalid text hid the unsupported-elements warning | Fixed in `2c90265` |
| A Plate read from a stale backup gets no card note while one with invalid text does | Not a regression (unchanged from `c724cd6`); recorded under D6 |
| The dormant scan trusts memory for a save that landed but reported failure | Latent; added to the D4 prerequisites |
| Mutation counts, the byte order mark case count, CI coverage, manual steps 4 to 6, the Move comment, section 7's introduction and other wording | Corrected here; UTF-32 big-endian added to the fixture re-encodings |

## 11. Unsaved changes kept when AetherFrame unloads

Added on October 2, 2026, as ROADMAP.md's section 8, task 4 (the owner's choice that day); its design was decided under the owner's delegation. It partly addresses D7: before it, a test build's reload, an update, a disable or the game closing lost whatever an editor had not saved, because nothing in unloading touched the open document. A crash still lost it until section 12 (October 5, 2026) added recovery checkpoints written while editing; autosave stays an OPEN product choice. [updated October 5, 2026: the window's Restore is now labelled Resume Editing, and Restore as New Plate is Recover as New Plate (section 12).]

### Where and how it is kept

- **Beside the Library, never in the Plate.** Each unload with unsaved changes writes one draft to `Drafts/`, beside `Profiles/`, `Recovery/` and `Trash/`. Not the plugin configuration (rule 3, and D1's fragility), not `Recovery/` (damaged files' copies only), and not `Profiles/` (every Guid-named file there is loaded as a Plate).
- **Write-once names.** `{plateId}.unsaved-{yyyyMMdd-HHmmss-fff}-{draft id, 32 hex}.json`, with the invariant stamp Recovery and trash names use. A name is unique to its write, and one that is somehow taken gets a new id, so a draft never writes over anything: not an earlier draft, not one another game client wrote (D8), and Dalamud's backup row, keyed by the exact path, can only ever hold what was written under it. So, unlike a Plate (D18), a draft whose bytes aren't valid text is refused and read from that row instead; with no row it is damaged, case (g). Names are parsed strictly, as Plate names are; anything else in the folder, a `.tmp` leftover included, is ignored and left alone.
- **Nothing deletes an unanswered draft.** A draft that is answered, or retired because a save already holds it, moves intact, never over another file, to `Trash/Drafts/`: under its own name, or, when a file there has that name already (a draft the player copied back out of the trash), that name numbered, `{name}.2.json` and on. Putting a draft back after a failed restore moves it from wherever its claim put it. Since October 5, 2026 (section 12), `Trash/Drafts/` keeps its newest 50 files.
- **The format.** An envelope with its own schema (`PersistenceSchemas.Draft`, version 1), in the Template envelope's two-layer pattern: `Version`, `DraftId`, `PlateId`, `PlateName` (display only), `WrittenAtUtc`, `Editor` (Basic, Advanced or None), `Build`, `BaseRevision` and `BaseUpdatedAtUtc` (the saved version the edits started from), and `Document`, the whole open document as a save would write it, unknown fields and elements included. It never holds undo history, selection, zoom, sharing or persona state, or a character's Content ID: a legacy owner is written as 0. `AetherFrame.Tests/DraftFixtures/draft-v1.json` pins the format.
- **The same proof as a Plate write.** The text is serialized, put through a file's byte-level round trip, and read back through the draft reader as a usable draft before it is written. Text that fails is not written, and the log says so.

### When it is written

- **Read as the UI stops.** `StopNewWork` reads the open document on the framework thread, once drawing has stopped and before the windows are removed, while the editor showing it is still known. `EditorSession.CaptureUnsavedChanges` uses neither ImGui nor the editors' once-a-frame dirty memo. It keeps a copy only when the document differs from its saved state (or that state couldn't be captured). An edit still in progress, a slider or a drag, is already in the document, so it is kept too. The copy is taken under `ProfileService`'s lock even while a save is being written, so a save in flight still leaves a draft; the next load finds it equal to the saved Plate if the save landed, and retires it.
- **Written before anything waits.** `DisposeAsync` writes it right after `StopNewWork`, before unloading waits for running operations, and waits for it 5 seconds at most. It never throws: a refusal, a failure or a timeout is logged. It goes through the same reliable chain as every Plate write, `StuckTempFallbackFileStore` over Dalamud's `IReliableFileStorage` (`PluginFileStores.Unguarded`), but without `ShutdownGuardedFileStore`, which refuses every step once operations are abandoned. It is never a Library operation and never dispatched to the framework thread, either of which unloading or a closing game can refuse or never run.

### What the next load offers

Once both Libraries have loaded and the tutorial's first-run question is settled, the drafts are read off the framework thread as an owned operation, newest first, at most 20 (the rest wait, untouched, and the log says how many). A Plate Library that failed to load means nothing is read, touched or offered. Each draft is judged against the Library:

| Case | The Plate | Offered |
|---|---|---|
| (a) | Its saved content equals the draft (compared as dirty state compares) | Nothing: retired silently to `Trash/Drafts/` |
| (b) | Ready, saved at the draft's base revision and modified time | **Restore**, Discard, Decide Later |
| (c) | Ready, but saved since (here, in another game client, a save that landed while reporting failure, D17), or edited by hand in a way that changed its revision or modified time | **Restore as New Plate**, Discard, Decide Later. It is never written |
| (d) | Deleted or missing | **Restore as New Plate**, Discard, Decide Later. Its id never comes back |
| (e) | A newer version's, or damaged | **Restore as New Plate**, Discard, Decide Later, saying this version of AetherFrame can't open it. It is never written |
| (f) | Couldn't be opened this session (locked) | **Restore as New Plate** or Decide Later; the draft stays |
| (g) | The draft itself is damaged (bytes that aren't valid text included, when Dalamud has no backup row of it), can't be opened, or a newer version's | Nothing; it is left exactly as it is, and logged |

A hand edit to a Plate's file is caught only when it changes `Revision` or `UpdatedAtUtc`: the base check compares nothing else, so a hand edit that changes neither still gets (b). Restore never writes, so the file keeps the hand edit until the player chooses Save, which writes what the editor shows.

The "Unsaved changes kept" window offers them one at a time, once a character is logged in: at once on a reload while logged in, otherwise at the next login. Closing it keeps every draft, My Plates then shows a reminder with Review, and the next load asks again. The reminder counts every draft left for later (Decide Later, or the window closed), even while another is on offer or being restored. Its Review, while a draft is being acted on (its unsaved-changes question waiting, or its new Plate being made), keeps that draft first in the new round and on offer, so the window shows the draft its answer acts on, and a failure shows with the draft it belongs to. There is no limit on asking.

- **Claimed before acting** (D8: no lock spans game clients). Restore, Restore as New Plate and Discard first move the draft to `Trash/Drafts/`. A draft already gone was taken by another game window: the window says "These changes were already handled in another game window." and nothing else happens. A move that fails leaves the draft where it was, and nothing is restored. The question's Discard (below) claims the draft before it throws anything away.
- **Restore** never writes. When another Plate is open with unsaved changes, or the same Plate is, it first asks My Plates' Save, Discard or Cancel. For the same Plate the question says that only Discard lets these be restored over it: saving its own changes makes it a later version, so these can then only be restored as a new Plate. The window isn't modal, unlike My Plates' prompt, so the editors and My Plates stay usable while the question waits. The question records the open document it asks about, the instance and its Plate. Every frame, and again when Save or Discard is pressed, it checks that this document is still open with unsaved changes; once it isn't (another Plate opened, or its changes saved or undone), the question is dropped with "The open Plate changed, so choose again.", nothing is saved or discarded, and the draft is still on offer. The question names the open Plate as it is now. Its Discard throws the open Plate's changes away, a revert with no undo, only once the draft is secured: it checks the Plate again and claims the draft first. So when the Plate changed meanwhile (deleted while the question waited, say), another game window took the draft, or the draft can't be moved, the question goes with that message, nothing is discarded, the open Plate keeps its changes, undoable as before, and nothing is restored. Cancel while the question's Save is being written lets that save finish, so the Plate ends saved, and restores nothing; the draft is still on offer. Then the Plate opens, the editor takes its saved state as the baseline, and the kept changes go in as one undoable edit. The editor reads as unsaved, the Plate's file is unchanged until the player chooses Save (through the normal path, with its validation and sharing), Revert to Saved discards them, and one Undo returns to the saved Plate. The Library's name and the saved file's unknown data are kept. A restore that can't be done puts the draft back, still offered.
- **Restore as New Plate** inserts the kept document through the Library's own new-Plate path, with a new id, unbound and never Active, named "{name} (kept changes)" and made unique. It keeps every field of the draft, unknown ones included, and opens clean in the editor the changes were made in, except when the open Plate was edited while the new Plate was being made: that Plate then stays open, and the new Plate waits in My Plates. If the window was closed while the new Plate was being made, what came of it (a failure, with the draft put back, or that message) opens the window again.
- **Renames.** A rename writes only a Plate's name and modified time. So the open document takes the rename's modified time too, and changes kept after renaming a Plate in the editor's Plate menu still restore over it. Just before acting, the offer checks the Plate again: it is restorable while it is Ready at the same revision, since only Library operations change a loaded Plate.

### Images

The draft holds only references: images added in the editor are already final files in `assets/`. The dormant cleanup's scan (`ScanAssetReferencesAsync`, and through it `LiveAssetReferences`) reads `Drafts/` and `Trash/Drafts/`, counting every GUID string in each file; one it can't read, whose bytes aren't all valid text (the offer reads such a draft from its backup copy), or that a newer version wrote, makes the scan incomplete (a D4 prerequisite).

### Tests

`KeptChangesWriteTests`, `KeptChangesReadTests`, `KeptChangesConflictTests`, `KeptChangesClaimTests` and `KeptChangesOfferTests`, over the Library doubles (`LibraryFixture`, `FakeClock`, `BackupSimulatingStore`, `HeldWriteStore`, `FaultInjectingStore`), 89 cases: 64 at first, 18 added with the independent review's fixes, and 7 with its recheck's. `TheOffersSources_HoldNoRawSpecialCharacters` covers every C# file the feature added or changed. Negative checks, each guard removed in turn and the 64 run:

- **Unique names:** the draft id taken out of the name (and the retry and the name-against-content check with it) fails 3, among them `TwoDraftsInTheSameMillisecond_GetTwoFiles_AndNeitherIsWrittenOver` (the second draft wrote over the first). The retry alone fails `ANameThatIsSomehowTaken_GetsANewId_AndTheFileThereStaysAsItIs`.
- **Claim by move:** fails 12, among them `TwoGameWindows_SeeTheSameDraft_TheFirstClaimWins_TheSecondSaysItWasHandledThere` and `AFailedMove_KeepsTheDraft_AndRestoresNothing`.
- **The base check:** fails `AChangedBase_OffersOnlyANewPlate_AndTheOriginalStaysByteIdentical`.
- **The unguarded store:** writing through the guarded store fails `Unloading_KeepsTheDraft_AfterRunningOperationsWereAbandoned`.
- **The baseline before the changes:** applying the changes before the editor takes the saved state as its baseline fails 7, among them `AMatchingBase_RestoresIntoTheEditorUnsaved_FileUnchangedUntilSave_OneUndoBack_RenameKept`.
- **The rename's modified time:** fails `AMatchingBase_RestoresIntoTheEditorUnsaved_FileUnchangedUntilSave_OneUndoBack_RenameKept`.

The review's fixes, each removed in turn and the 82 run:

- **The question that went stale:** never dropping it fails 5, the four cases of `AQuestionLeftWaiting_WhileAnotherPlateIsOpenedAndEdited_IsDropped_AndNeverAnswersForIt` (before them, its Discard threw away the other Plate's edits, and its Save wrote them) and `ASaveThatLandsWhileTheQuestionWaits_DropsIt`. Without the check every frame, the second fails; without the check when Save is pressed, or when Discard is, that answer's case with no frame between fails. The check after the question's own save fails `TheQuestionsSave_FinishingAfterAnotherPlateWasOpenedAndEdited_RestoresNothingOverIt`, and a question cached without the open Plate's live name fails `TheQuestion_NamesTheOpenPlateAsItIsNow`.
- **Bytes that aren't valid text:** accepting them fails `ADraftWithBytesThatAreNotValidText_IsReadFromItsBackupCopy_ExactlyAsWritten` (it was offered with U+FFFD, which a Save then wrote into the Plate) and `ADraftWithBytesThatAreNotValidText_AndNoBackupCopy_IsDamaged_AndLeftAsItIs`.
- **Numbered trash names:** fails 3, both cases of `ADraftCopiedBackFromTheTrash_IsOfferedAgain_AndCanBeRestoredOrDiscarded` and `ADraftCopiedBackFromTheTrash_WhoseNewPlateFails_IsPutBackFromWhereItsClaimMovedIt`. Putting a draft back from its plain trash name fails the last.
- **Closing while a new Plate is made:** not opening the window again fails both `ClosingWhileANewPlateIsMade_ThatThenFails_OpensTheWindowAgain_WithTheDraftStillOffered` and `ClosingWhileANewPlateIsMade_AndTheOpenPlateIsEdited_SaysWhereTheNewPlateIs`. A reminder hidden while any draft is on offer fails the first, and a round without the draft being made fails `ReviewWhileANewPlateIsMade_ShowsIt_AndAFailureIsStillOffered`.
- **Wording:** newer and damaged Plates called saved again fail `ANewerVersionsPlate_IsNeverWritten` and `ADamagedPlate_IsNeverWritten`; the Deleted variant's discard tooltip fails its case of `EachVariant_HasItsWordsAndButtons`; the same Plate's question worded as another's fails `TheSamePlateOpenWithUnsavedChanges_IsAskedAboutToo` and `TwoDraftsForTheSamePlate_AreBothRestorable_AndNothingIsLost`.
- **The busy check before Restore:** fails `RestoreWhileASaveIsBeingWritten_SaysSo_AndTakesNothing`.

The recheck's fixes, each removed in turn and the 89 run:

- **The question's Discard, the draft first:** discarding before the draft is checked again and claimed fails 3, `TheQuestionsDiscard_WhenTheDraftCantBeMoved_DiscardsNothing_AndTheDraftStaysKept`, `TheQuestionsDiscard_AfterAnotherGameWindowTookTheDraft_DiscardsNothing` and `TheQuestionsDiscard_AfterTheKeptPlateWasDeleted_DiscardsNothing_AndOffersItAsANewPlate`. Before them, the open Plate's changes were gone, with no undo, and nothing was restored.
- **Cancel while the question's Save is written:** ignoring it fails `CancelWhileTheQuestionsSaveIsWritten_SavesThePlate_AndRestoresNothing` (the kept changes were restored anyway).
- **The scan, a draft whose bytes aren't all valid text:** dropping the check fails `KeptChangesWithBytesThatAreNotValidText_MakeTheScanIncomplete` (the scan counted the damaged text's ids, though the offer restores the draft's backup copy).
- **Review while a draft is acted on:** a round rebuilt newest first fails both `ReviewWhileTheQuestionWaits_KeepsItsDraftOnOffer_AndTheAnswerRestoresIt` and `ReviewWhileAnOlderDraftsNewPlateIsMade_KeepsItOnOffer_AndItsFailureShowsWithIt`: a newer draft was shown while the answer acted on the older one, or the older one's failure showed beside it.

### Manual acceptance in FFXIV

Use Plates made for the test. The build that adds this can't keep anything when it is itself installed: the reload that installs it unloads the build before it.

1. Open a Plate in the Basic editor, change it, don't save, and reload (install the same build again, or disable and enable AetherFrame). "Unsaved changes kept" names the Plate, Basic editor and the time. Restore: the Basic editor opens with the change, the save state says unsaved, and the Plate file's modified time hasn't changed. Ctrl+Z goes back to the saved Plate; Ctrl+Y brings the change back; Save keeps it.
2. The same in the Advanced editor, with a drag still held when the reload happens if you can: it opens in the Advanced editor.
3. Change a Plate, don't save, rename it from the Plate menu, and reload: Restore is offered, and the Plate keeps its new name.
4. Change a Plate, reload, and choose Decide Later: My Plates shows "Unsaved changes were kept for 1 Plate." with Review, which offers it again. Discard: the draft is in `Trash/Drafts/` and the Plate is as saved.
5. Change a Plate, reload, close the offer, save a different change to the same Plate, and reload: the offer says it was saved again and offers Restore as New Plate, which makes "{name} (kept changes)", opens it, and leaves the original as saved.
6. Change a Plate, reload while logged out (or at the title screen): the offer appears at the next login.
7. Close the game with an unsaved change: the next start offers it.
8. Keep a change, reload, then open another Plate and change it before answering. Restore asks about that Plate. Leave the question, save that Plate with Ctrl+S: the question goes away with "The open Plate changed, so choose again.", and Restore then works without asking. Again, but instead of saving, open a third Plate from My Plates (saving the second there) and change it: the question goes away, and the third Plate keeps its change.
9. Discard a kept change, copy its file from `Trash/Drafts/` back to `Drafts/`, and reload: it is offered again, and Discard works; `Trash/Drafts/` then holds it twice, the second named `.2.json`.
10. Keep changes to two Plates and reload. Decide Later on the first offered, then open a third Plate, change it, and choose Restore on the second: it asks about the third. Press Review in My Plates: the window still shows the second, "1 of 2". Then delete the second Plate in My Plates and choose the question's Discard: the question goes with "The Plate changed meanwhile, so check the choices again.", the third Plate keeps its change (Ctrl+Z and Ctrl+Y still work), and the offer is now Restore as New Plate.

## 12. Continuous crash recovery of unsaved edits

Added on October 5, 2026, at the owner's request; its design was decided under the owner's delegation (ROADMAP.md, section 5). Together with section 11 it addresses D7's loss on a crash: while a Plate is open, recovery checkpoints of its unsaved changes are written as editing goes on, so a crash, a killed process or a power cut loses at most the last few seconds. It is not autosave: nothing here writes, saves or shares the Plate, and autosave stays an OPEN product choice.

### When a checkpoint is written

- **Both editors, one document.** `ContinuousRecovery.Tick` runs once a frame on the framework thread, after the windows draw, for whatever Plate is open, in the Basic or the Advanced editor. Each document opened is one *editing*, with its own id.
- **Only what changed.** It compares the document with its last sample (the structural comparison dirty state uses, `ProfileService.DocumentState`) every 200 ms, and every frame once the editing may have checkpoints. A checkpoint is due when the content differs from the saved state and from the last checkpoint that finished (or one being written): 5 seconds after the last change, and no later than 30 seconds after the first change since the last checkpoint, however busy the editing. Elapsed time schedules it, never the clock. Everything a save writes counts: new Plates, text as it is typed, transforms, Components, the canvas and background, and image references. Layer names still being typed are not in the document until committed, as for Save.
- **A consistent copy on the framework thread.** The checkpoint is copied as a save copies its snapshot (`ProfileService.CopyOpenDocument`), on the thread that edits the document. Serializing, proving and writing it run on the writer's own thread. Nothing commits an edit in progress, ends a drag, takes focus from a text field, touches the undo history or the saved baseline, saves, changes which Plate is Active or shares anything.
- **Bounded, newest wins.** `RecoveryCheckpointWriter` is one background queue that runs one job at a time. A new checkpoint of an editing replaces one of the same editing still waiting, so at most one write per editing waits behind the one being written, and an older write never lands after a newer one. A failed write keeps the checkpoint due, retried after 5, 15, 30 and then every 60 seconds, or at once with Retry now.

### Where and how they are kept

- **One folder per run.** `Drafts/Sessions/{run id, 32 hex}/`, beside the unload drafts. Each plugin load is one run, with `session.lock` held open, unshared, while it runs: another game client sharing the folder sees the lock held and never offers, tidies or removes anything in it.
- **Names.** `{plateId}.checkpoint-{editing id, 32 hex}-{sequence, 10 digits}.json`, parsed strictly; the envelope is the unload draft's (`PersistenceSchemas.Draft`, version 1), with three optional fields, `SessionId`, `EditId` and `Sequence`, which earlier builds ignore. No schema change, so every existing draft stays readable.
- **Plain files, not Dalamud's reliable storage.** A checkpoint is written to a temporary file, flushed to disk, renamed to its name (never over an existing file), and read back and checked as an intact draft with the same id before anything older is removed. Reliable storage keeps a backup row for every path it writes and has no delete, so a new name every few seconds would grow its database without bound. This departs from rule 3 for this data only, and is recorded as a decision in ROADMAP.md.
- **The newest five per editing.** After a checkpoint reads back, its editing's checkpoints in this run's folder beyond the newest five are deleted. A write interrupted or damaged on disk never removes anything: the five intact ones before it stay.
- **Retired when nothing is unsaved.** Save, Discard, Revert to Saved, undoing back to the saved state and Save as New Plate that opens the copy delete the editing's checkpoints, after any write of it still in flight, so a late write never brings back saved or discarded work. A save that fails changes nothing. Opening another Plate, or the open one closing or being deleted, while it still has unsaved changes writes its last state as a final checkpoint, kept for the next load.
- **Unloading.** `StopNewWork` captures the unload draft (section 11) and stops new checkpoints; retirements already asked for still run. `DisposeAsync` waits up to 5 seconds for both, then releases the lock and removes the run's folder if it is empty. The unload draft records the run and editing it belongs to.

### What the next load offers

The kept changes offer (section 11) reads checkpoints with the drafts:

- **Ended runs only.** A folder whose lock is held belongs to a running game client and is skipped.
- **One entry per editing.** An editing's checkpoints, and the unload draft that names the same run and editing, make one entry. Its newest point is offered and judged as section 11's cases; an unload draft is its newest point. The newest 20 editings are offered (the limit was 20 drafts), so five checkpoints of one editing never crowd out other Plates or ask five times. The rest wait.
- **Damaged or newer.** An editing reads its checkpoints newest first until five read intact; a damaged or interrupted one is logged and left as it is, and an earlier one is offered. An editing with any checkpoint a newer version wrote is not offered from checkpoints, and none of its files are removed.
- **The window.** It names the Plate, the editor and the checkpoint's time. Resume Editing (the Restore case) opens the Plate with the changes, unsaved, as section 11's Restore does; Recover as New Plate is offered beside it, and is the only choice when the Plate was saved again, deleted, damaged, a newer version's or locked. Discard and Decide Later work as before. A "Recovery point" list chooses an older checkpoint of the same editing; the choice and its case apply to that point.
- **Answering.** The newest point is the editing's token: every answer first moves it to `Trash/Drafts/`, so two game windows can't both act, then deletes the editing's other checkpoints. Decide Later keeps everything. When the newest point equals the saved Plate, the editing is retired without asking.

### Bounded growth

- **Per editing:** five checkpoints. **Per run:** one folder, removed when empty at unload. Every crashed editing waits for an answer; nothing removes unanswered work.
- **Leftovers.** A load removes, in ended runs only, temporary files and a lone lock file once 10 minutes old, and empty folders of ended runs.
- **The trash.** `Trash/Drafts/` keeps its newest 50 files; older answered drafts and checkpoints are deleted. This replaces section 11's "nothing deletes a draft" for answered ones: every one of them was answered by the player or retired because a save holds it.

### Images

A checkpoint holds only references. The dormant cleanup's scan reads every run's folder under `Drafts/Sessions/` too, so an image a checkpoint uses counts as in use. There is still no general asset cleanup.

### In the editor

Both editors' action bar shows a small shield beside the save state while the Plate has unsaved changes: dim and solid once the current changes are in a checkpoint that finished, faint while the newest are not yet, and in the warning colour when writing failed. Its tooltip gives the last checkpoint's time. Only a write that read back counts. When writing fails, one warning line says when it tries again, with Retry now.

### Tests

`ContinuousRecoveryTests` (12: the schedule, what counts as a change, both editors, new Plates, images, and nothing saved, shared or made Active), `RecoveryLifecycleTests` (10: Save, failed Save, Discard and Revert with a write held mid-flight, Save as New Plate, switching Plates, unloading), `RecoveryStorageTests` (10: retention, interrupted and damaged writes, failed writes and retry, two game clients, the sweep) and `RecoveryOfferTests` (11: one entry per editing, older points, Resume, Recover as New Plate, conflicts, the 20 limit, unknown data, newer versions). `RecoveryProcessTests` starts the test assembly as a separate process (`RecoveryCrashHost`) that edits a Plate of a temporary Library with recovery running on real files and a real lock, kills it at three different moments, and checks that the next start offers the newest intact checkpoint, resumes it unsaved and never touched the saved Plate. No test uses the game or a real Library.

### Known limits

- A crash in the moment between a save landing and its retirement running leaves checkpoints equal to an earlier state; the next load sees the Plate saved since, and offers only Recover as New Plate, which the player can discard.
- An older point equal to the saved Plate is offered with the saved-again wording, since only the newest point is judged as identical.
- The unsaved changes of a Plate deleted while open are kept, and offered as Recover as New Plate.
- Two game clients editing the same Plate each keep their own checkpoints; both are offered after a crash.

### Manual acceptance in FFXIV

Use a Plate made for the test, and back up the plugin's data first. The checklist with the exact steps, for the build being tested, is in the pull request's In game section.
