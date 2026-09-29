# Changelog

All notable changes to AetherFrame are listed here. Versions follow the [versioning policy](docs/Versioning.md), and each released version has an annotated `v<version>` tag.

## [Unreleased]

### Added

- The persona management foundation, the first NETWORK1 increment ([docs/networking/NETWORK1_PersonaFoundation.md](docs/networking/NETWORK1_PersonaFoundation.md)): a standalone `AetherFrame.Personas` assembly holding the in-memory model of several independent personas per installation, one active persona chosen by the player and switched only on purpose, identities derived from public keys exactly as the protocol derives them, and the narrow interfaces that protected key storage and an encrypted `.afpersona` backup will implement later. Private keys are accepted only as key pairs checked in managed code (scalar range before any platform import, public point derived from the scalar and compared), a key is committed to custody only after its identity is checked so a refusal leaves no orphaned key, a restore opens only a private copy of the bytes it inspected and only after an explicit supported inspection, and a signer lease stops signing when the player switches personas (an interim policy awaiting an owner decision). The private persona label is provisional under the unresolved D9a, and the Release workflow is unchanged while L9 is unresolved. The owner's decisions D3 (several manually selected personas, never bound to a character or a Content ID) and D2 (portable encrypted backups, approved in principle with the format still open) are recorded in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md); every other NETWORK1 decision stays unresolved there. No persistent key is generated, nothing is stored or encrypted, no backup format exists, and the plugin does not reference the assembly: nothing opens a connection, and the plugin is unchanged.
- The remote protocol foundation, NETWORK0 ([docs/networking/NETWORK0.md](docs/networking/NETWORK0.md)): a standalone `AetherFrame.Protocol` assembly with a canonical binary encoding, a signed document envelope (ECDSA P-256 over SHA-256, one canonical signature form), persona identities derived from public keys alone, and a deliberately narrow remote profile model (profile and revision ids, creation time, name, image references) that shares nothing with the local Plate model. Every limit the protocol enforces lives in one place, every refused input fails with a typed error, and the test suite covers committed test vectors, canonicalization proofs, and adversarial and seeded fuzz campaigns. An independent review followed by remediation: every reader copies its input before checking it, the codec verifies every document it signs, a remote profile is identified by its owning persona together with its id, and the wire format is marked a draft until the open product decisions are made. The plugin does not reference the assembly, opens no connection, and is unchanged; nothing here needs an account or a server. The protocol is a DRAFT (docs/networking/ProtocolSpecification-v1.md); two independent reviews and their corrections are recorded in docs/networking/NETWORK0_HANDOFF.md.
- Release tooling for a public custom Dalamud repository ([docs/CustomRepository.md](docs/CustomRepository.md)): `tools/AetherFrame.ReleaseTools` checks a release package against the full rule set (x64, built from the released commit, one version and one Dalamud API level everywhere, only the three plugin files, safe entry names), generates and checks the repository metadata Dalamud reads (`pluginmaster.json`, stable and testing channels), and writes and verifies SHA-256 checksum files. The Build and Release workflows run it; the Release dry run keeps the generated metadata as an artifact. Nothing is published to a repository yet, and the plugin itself is unchanged.
- A manual **Publish custom repository** workflow ([docs/CustomRepository.md](docs/CustomRepository.md#publishing)) that puts a published GitHub Release into the custom repository's testing or stable channel after the owner's approval, or rolls a channel back. It verifies every release it describes from scratch, never publishes a draft, never moves a channel to an older version by accident, and writes only `pluginmaster.json` and a README to its own branch. It has not been used yet.

## [0.1.6] - 2026-09-27

Reliability, data safety and import security, the v0.1.6 milestone. Saved Plates, Templates, `.aetherframe` packages and the configuration format are unchanged: a file written by 0.1.0 through 0.1.5 loads and re-saves byte for byte, and the schema versions did not move.

### Fixed

- A `.aetherframe` file with an explicit `null` where a list or text belongs no longer passes validation and then throws inside the editor; a local Plate with the same shape opens with those fields empty.
- A package whose ZIP end record disagrees with the one .NET reads is refused before the archive's directory is parsed, so a hostile file can no longer make the plugin allocate hundreds of megabytes on import.
- A package whose `profile.json` is made of millions of tiny values is refused before it is parsed (new limits on JSON value count and property-name length), instead of costing over a gigabyte and several seconds to check.
- Very large text sizes, from a package or from zooming far in, no longer make the plugin rasterize hundreds of megapixels of glyphs: each font family builds tiers only up to a bounded size (Sans 280 px, Serif 240 px, Mono 140 px; the Dalamud default family, whose glyph set depends on the game's language, 96 px) and text is slightly upscaled above it. The bundled fonts keep every glyph they rendered in 0.1.5; only the size of a tier is bounded.
- Typing a value such as `1e39` into an editor slider, or an overflowing number in a hand-edited file, no longer leaves a Plate that can never be saved. A value typed into a slider still goes past the slider's range, as in 0.1.5, but only as far as a Plate can hold and still export: font size and Auto Fit minimum 1 to 1024 px, letter and line spacing ±10,000, and angles and rotations wrap into 0-360°. Opacity, Pattern intensity and outline thickness stay within their slider, which is all the renderer draws. A typed `1e39` takes the limit for font size, Auto Fit minimum and spacing; it leaves a background's gradient angle or Pattern rotation as it was before the entry, and resets an image's rotation to 0°. Values that aren't numbers are repaired in memory, and the save error names the offending value if one ever gets through.
- Create Plate and Use Template now report a Plate that was created but could not be linked to the character, instead of a total failure that invited a duplicate.
- A character binding whose file could not be read at startup is treated as unavailable (never replaced) rather than as damaged; the player is told to restart the game.
- A failed index or migration write at startup, or a failed Recovery copy of a damaged index, no longer marks the whole Plate Library as unloadable.
- Plate, Template, character and index files are read from disk by the plugin itself, and Dalamud's backup copy is asked for only when the file's content is unusable: a file that is merely locked or missing is reported unavailable instead of being silently replaced by an older backup. A file served from the backup is logged, and the damaged on-disk copy is kept under `Recovery` before anything writes over it.
- A Basic editor action that fails partway (for example revealing a section when the Plate is already at its element limit) now rolls back completely instead of leaving a half-applied, un-undoable change.
- Undoing a delete puts the element back where it was, so overlapping elements keep their paint order.
- Saving while dragging commits the drag first; a layout refinement can no longer land while a save is being written; Discard is unavailable while a save is in flight, and a Discard that is refused keeps the question open and says why.
- A write that fails inside Dalamud's reliable storage (a full disk, for example) no longer blocks every later save of that Plate until the game restarts: the plugin writes the file directly while Dalamud's temporary file for it is stuck open, and says so once in the log.
- 16-bit PNGs are counted at 8 bytes per pixel against the decoded-memory limit, matching what the game's decoder allocates.
- Importing a package commits its images on a background thread instead of inside a frame; the Import Preview can't be closed until the import finishes; checking a package counts as running work during unload, and an import already copying its images when the plugin unloads finishes as one operation instead of being rolled back; a package check never leaves a stray staging folder.
- An import whose Plate file was written although the import then failed, and which couldn't move that file away (or was stopped by unloading first), keeps its images: the Plate appears whole the next time AetherFrame starts instead of pointing at images that were rolled back.
- A canceled plugin load now tears the plugin down itself, since Dalamud does not dispose a plugin whose load was canceled.
- A newer build's configuration settings and asset metadata are preserved instead of being overwritten by this build.
- AetherFrame's log no longer shows a character's Content ID: a character's settings file, which is named after it, appears as "character binding file", including inside a logged error.
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

[Unreleased]: https://github.com/richhiiee/AetherFrame/compare/v0.1.6...HEAD
[0.1.6]: https://github.com/richhiiee/AetherFrame/compare/v0.1.5...v0.1.6
[0.1.5]: https://github.com/richhiiee/AetherFrame/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/richhiiee/AetherFrame/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/richhiiee/AetherFrame/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/richhiiee/AetherFrame/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/richhiiee/AetherFrame/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/richhiiee/AetherFrame/releases/tag/v0.1.0
