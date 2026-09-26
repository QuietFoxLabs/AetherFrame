# Changelog

All notable changes to AetherFrame are listed here. Versions follow the [versioning policy](docs/Versioning.md), and each released version has an annotated `v<version>` tag.

## [Unreleased]

Reliability, data safety and import security, the v0.1.6 milestone. Saved Plates, Templates, `.aetherframe` packages and the configuration format are unchanged: a file written by 0.1.0 through 0.1.5 loads and re-saves byte for byte, and the schema versions did not move.

### Fixed

- A `.aetherframe` file with an explicit `null` where a list or text belongs no longer passes validation and then throws inside the editor; a local Plate with the same shape opens with those fields empty.
- A package whose ZIP end record disagrees with the one .NET reads is refused before the archive's directory is parsed, so a hostile file can no longer make the plugin allocate hundreds of megabytes on import.
- A package whose `profile.json` is made of millions of tiny values is refused before it is parsed (new limits on JSON value count and property-name length), instead of costing over a gigabyte and several seconds to check.
- Very large text sizes, from a package or from zooming far in, no longer make the plugin rasterize hundreds of megapixels of glyphs: each font family builds tiers only up to a bounded size (Sans 280 px, Serif 240 px, Mono 160 px; the Dalamud default family, whose glyph set depends on the game's language, 96 px) and text is slightly upscaled above it. The bundled fonts are built with the Latin, Greek, Cyrillic and Hebrew scripts, punctuation, currency, arrows, mathematical and common symbols they map, and leave out their phonetic alphabets, combining marks and box drawing.
- Typing a value such as `1e39` into an editor slider, or an overflowing number in a hand-edited file, no longer leaves a Plate that can never be saved: sliders clamp typed values, values that aren't numbers are repaired in memory, and the save error names the offending value if one ever gets through.
- Create Plate and Use Template now report a Plate that was created but could not be linked to the character, instead of a total failure that invited a duplicate.
- A character binding whose file could not be read at startup is treated as unavailable (never replaced) rather than as damaged; the player is told to restart the game.
- A failed index or migration write at startup, or a failed Recovery copy of a damaged index, no longer marks the whole Plate Library as unloadable.
- Plate, Template, character and index files are read from disk by the plugin itself, and Dalamud's backup copy is asked for only when the file's content is unusable: a file that is merely locked or missing is reported unavailable instead of being silently replaced by an older backup. A file served from the backup is logged, and the damaged on-disk copy is kept under `Recovery` before anything writes over it.
- A Basic editor action that fails partway (for example revealing a section when the Plate is already at its element limit) now rolls back completely instead of leaving a half-applied, un-undoable change.
- Undoing a delete puts the element back where it was, so overlapping elements keep their paint order.
- Saving while dragging commits the drag first; a layout refinement can no longer land while a save is being written; Discard is unavailable while a save is in flight, and a Discard that is refused keeps the question open and says why.
- A disk-full or locked-file failure inside Dalamud's reliable write no longer blocks every later save of that Plate until the game restarts: the plugin writes the file directly when Dalamud's temporary file is stuck open, and says so once in the log.
- 16-bit PNGs are counted at 8 bytes per pixel against the decoded-memory limit, matching what the game's decoder allocates.
- Importing a package commits its images on a background thread instead of inside a frame; the Import Preview can't be closed until the import finishes; checking a package counts as running work during unload, and an import already copying its images when the plugin unloads finishes as one operation instead of being rolled back; a package check never leaves a stray staging folder.
- A canceled plugin load now tears the plugin down itself, since Dalamud does not dispose a plugin whose load was canceled.
- A newer build's configuration settings and asset metadata are preserved instead of being overwritten by this build.
- Plate names made only of invisible characters are refused, and a name's zero-width joiners and other format characters become spaces; a package an earlier version exported with such a name still imports, under the folded name. A Template file named with a built-in Template's id is ignored instead of listed twice; Use Template always instantiates from the saved file.
- Stale temporary files from an interrupted image import or thumbnail generation are removed at load.

### Changed

- The Plate and Template Libraries read and parse their files on a background thread while loading, instead of inside one framework tick, and their threading contract is documented.
- Loading a Plate whose values had to be repaired in memory logs one line per file; the file itself is unchanged until it is saved.
- Card previews no longer draw a bar for an affix on empty text.
- The test suite grew from 2071 to over 2600 tests, including real fixture sets written by every tagged build since 0.1.0, failure injection for every multi-step file operation, and a threading contract test against a queued dispatcher.

## [0.1.5] - 2026-09-26

Distribution and submission readiness: the first version meant to reach testers. Saved Plates, Templates, `.aetherframe` packages and the configuration format are unchanged, and so is how the plugin behaves in game.

### Added

- This changelog.
- Issue templates for bug reports and feature requests, with guidance on keeping personal details out of public reports.
- A guide for testers ([docs/Testing.md](docs/Testing.md)): installing through Dalamud's testing builds or a GitHub Release ZIP, what to test, and how to report problems.
- A tag-based release workflow that builds, tests and checks the plugin package and prepares a **draft** GitHub Release for review ([docs/Releasing.md](docs/Releasing.md)).
- Preparation notes and a draft `manifest.toml` for submitting AetherFrame to the official Dalamud plugin repository's testing track.

### Changed

- The plugin description in the Dalamud plugin installer now says that the plugin icon is AI-generated and that the bundled Celestial Dream and Celestial Sakura Components use AI-assisted artwork.
- The README describes the installation plan, where to get support, and AetherFrame's AI use at the Dalamud policy's *Copilot* level.

## [0.1.4] - 2026-09-25

Runtime readiness fixes before broader testing. Saved Plates, Templates, `.aetherframe` packages and the configuration format are unchanged.

### Fixed

- The Advanced Editor, My Plates, Templates and Import windows now follow Dalamud's global UI scale, so they stay usable at 150% and 200%.
- My Plates and the Advanced Editor open at a sensible size the first time, held to the screen.
- If saved Templates fail to load, Create Plate still works with the built-in Templates, and the Template windows say what happened instead of waiting forever.
- Editor errors (image import, save, edits) show a plain description instead of raw exception text that could include local file paths.
- A damaged configuration file no longer stops the plugin from loading. It is logged and replaced by the defaults.
- If startup fails part-way, AetherFrame undoes what it had already hooked up.

## [0.1.3] - 2026-09-25

### Changed

- The Basic Editor no longer shows the logged-in character's name anywhere, including the Character Name placeholder and hints. Typing a name, saved names and the Active Plate for each character are unchanged.
- The plugin installer text describes character Plates, the Basic Editor and the Advanced Editor.
- The README has screenshots and a What's coming section.

## [0.1.2] - 2026-09-25

### Added

- **Celestial Sakura**: a full-color set of seven bundled Components (Background, Plate Frame, Portrait Frame, Name Backing, two Dividers and a Corner Ornament). This is original artwork created with AI assistance. The files are shipped exactly as approved and keep their C2PA Content Credentials.
- A Background Component kind that paints over the whole canvas, under everything else.
- In the Advanced Editor, Name Backings and Dividers can stop following the name ("Follows the name") and be placed independently.

### Fixed

- A Portrait Frame now paints over the picture when the Plate has no portrait element, instead of being hidden underneath it.
- Bundled artwork loads in the background instead of stalling the frame the first time it is drawn.

## [0.1.1] - 2026-09-25

### Fixed

- Unloading AetherFrame while a file operation is still running no longer risks writing after Dalamud has released the plugin's storage, and window teardown runs on the game's framework thread.
- Editor keyboard shortcuts no longer take keys from the game while Dalamud hides plugin UI (cutscenes, gpose, or hiding the UI).

### Changed

- My Plates no longer shows the character's name and World.

## [0.1.0] - 2026-09-25

The first versioned alpha.

### Added

- **My Plates**: a local library of saved Plates with previews, search, rename, duplicate and delete, and an Active Plate for each character.
- **Basic Editor**: an Adventure Plate-style editor for identity and details, portrait, playstyle, active hours, message, Themes and background patterns.
- **Advanced Editor**: a freeform canvas with text and image elements, layers, snapping, rotation, undo and redo, bundled fonts and Clean Preview.
- **Components**: procedural frames, backings, dividers, section headers and corner ornaments, plus the bundled Celestial Dream *Astrolabe Pivot* corner ornament (original artwork created with AI assistance).
- **Templates**: the built-in *Adventure Plate Classic* and *Blank Canvas*, and saving your own.
- **Import and export**: `.aetherframe` package files, validated on a staging copy before anything is imported.
- **Plate Viewer** and the commands `/aetherframe` (`/af`), `/af view` and `/af version`.
- Builds and tests on Windows and Linux in CI.

[Unreleased]: https://github.com/richhiiee/AetherFrame/compare/v0.1.5...HEAD
[0.1.5]: https://github.com/richhiiee/AetherFrame/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/richhiiee/AetherFrame/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/richhiiee/AetherFrame/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/richhiiee/AetherFrame/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/richhiiee/AetherFrame/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/richhiiee/AetherFrame/releases/tag/v0.1.0
