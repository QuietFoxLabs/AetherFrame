# Changelog

All notable changes to AetherFrame are listed here. Versions follow the [versioning policy](docs/Versioning.md), and each released version has an annotated `v<version>` tag.

## [Unreleased]

### Added

- The snapshot builder, in the networking preview flavour only (NETWORK2 increment N2-6a; decisions D4, D5, D8 and N1 in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md)). A saved Plate can be turned into the layout another player's AetherFrame will draw: exactly what your Plate visibly shows, in the same order, and nothing else. Anything that draws nothing is left out: an empty or fully transparent text, an image at opacity 0, or anything outside the Plate's view. No ids or details of your character travel beyond what the Plate itself displays, and each image is to be shared only as the part the Plate shows. A Plate that can't be shared as it is (a name a shared Plate can't carry, too many images, a missing image, something a newer AetherFrame made) is refused with the reason, never cut down. Nothing uses it yet.
- The persona window, in the networking preview flavour only (NETWORK2 increment N2-5c; decisions D3, K2, K4 and L12 in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md)). My Plates gains a **Personas** button. The window lists your personas and which one is in use, and lets you create, rename, use and stop using them; none is ever chosen for you, and none is tied to a character. Whenever you create or restore one, it asks you to acknowledge that a lost key means never updating or unpublishing what it shared (only the server's operator could then remove it), and that keeping AetherFrame's `Network\Personas` folder keeps the list of what each persona shared, which updating or unpublishing needs, but is no backup. It also says plainly how Windows protects the key and what it doesn't protect against. It also shows any key file no persona uses, checks it on request, and can restore it as a persona; nothing is deleted. Nothing is shared yet.
- The persona files and session, in the networking preview flavour only (NETWORK2 increment N2-5b; decisions K2, K3 and P3 in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md)). When AetherFrame starts, it first checks in the background that keys can be created, protected, stored and used on this system. Then it takes a lock, so that only one game client at a time uses your personas, reads your persona list, and checks your key files. Everything it saves is written through to disk before it counts. Nothing uses it yet: the persona window comes next.
- The persona registry, in the networking preview flavour only (NETWORK2 increment N2-5a; decisions L10, L12 and P3 in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md)). A player's personas and their choice of active persona will be kept between sessions, and every change is saved before it takes effect, so a change that can't be saved doesn't take effect (if saving failed partway, it may appear after the next restart). A registry that can't be read is left exactly as it is and never replaced. A persona's key that no record names, which a failed save or a crash can leave behind, is found and offered back, never deleted. Each persona records whether the player has acknowledged what losing its key means, and anything that signs does so only for the persona it showed the player. Nothing uses it yet: the persona window comes next.
- The Windows key protector and the capability probe, in the networking preview flavour only (NETWORK2 increment N2-4; decisions K2 and K3 in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md)). A persona's key will be protected by Windows for the current user and bound to the file it belongs to. Persona features will turn on only where a check at startup shows that keys can be created, protected, stored and used here, and viewing shared Plates only where signatures provably verify; otherwise one message says what isn't available on this system yet. Nothing runs either yet, so no key is written outside tests, and player builds declare no native call of their own.
- The key store core, NETWORK1 increment 5 ([docs/networking/NETWORK1_KeyStoreCore.md](docs/networking/NETWORK1_KeyStoreCore.md); decisions K1, K2, K6 and K7 under the delegation in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md)): `AetherFrame.Personas` gains a store that keeps each persona's private key as a protected envelope, one per random slot, behind two seams the plugin fills (a protector and a blob storage); it proves every key it commits before anything is written, never replaces one, and reports an unavailable key instead of throwing. The plugin gains its first networking-folder file, a directory-of-files storage compiled only in the preview flavour and tested from the persona suite. No protector ships (the only one is the test fake, which protects nothing and says so), nothing is wired, and no persona key is written anywhere outside tests; player builds are unchanged.
- A networking preview flavour of the plugin, for testers and CI (`dotnet build -p:AetherFrameNetworkPreview=true`; [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md), D9b and P2): it compiles the remote protocol and persona foundation sources into `AetherFrame.dll`, says `[network preview]` in `/aetherframe version`, and still opens no connection and creates no key. Player builds compile none of it and are unchanged; boundary tests keep them free of any protocol, persona or networking code, and the release tooling's package check now refuses a DLL that holds any protocol or persona type, so a preview build can reach neither a release nor a test build. AetherFrame's log now hides persona, profile, revision and asset identifiers, should one ever appear in it.
- A visual identity and design system for AetherFrame's own windows ([docs/DesignGuide.md](docs/DesignGuide.md)): midnight surfaces, one aetherial accent, a restrained glow for focus and the tutorial spotlight, gold only for the Active Plate, headings and section labels in the game's Axis face, and a corner-bracket-and-spark mark drawn in code. Every color and measurement comes from one token layer (`AetherPalette`, `AetherMetrics`, with contrast tests), and the ImGui style is pushed around AetherFrame's windows only and always popped again, so every window shares the same surfaces, rounding and spacing. My Plates gains a brand row, one primary Create Plate button, an empty state that invites the first Plate, and cards with a raised frame, the accent selection ring with the frame corners and the gold Active badge; the Basic editor's category title and group labels use the section-label style; the unsaved-changes, open-another-Plate, revert, rename and delete prompts use the shared button row (red for the destructive choice, Cancel as a quiet ghost). Saved Plates render exactly as before: the redesign touches the windows' chrome, never the Plate.
- An interactive first-time tutorial: twelve short chapters that dim AetherFrame's windows, spotlight the real control, explain it on a card beside it, and let the player use it where the step asks for that. Steps whose control isn't on screen say so and let the interface be used; steps that need an editor open explain how to get there and offer to open it; nothing in the tour creates, saves, imports, exports, overwrites or deletes anything for the player. A new install (no configuration, no Plates, no Templates) is offered the tour once the Library has loaded, with Start Tutorial, Maybe Later and Do Not Show Again; an install upgrading from any earlier version is never offered it unasked. The Help menu in My Plates and both editors starts, resumes or jumps into the tour, and lists the keyboard shortcuts and chat commands. The tutorial's state is kept in the plugin configuration beside the guidance flag and never in a Plate; the configuration's version is unchanged.
- A Help button in My Plates and at the right end of both editors' action bar, and tooltips on My Plates' Create Plate and search field. The controls' names, the editors' collapsible sections and their existing tooltips are unchanged.
- Manual acceptance steps for the interface and the tutorial ([docs/ManualAcceptance-UI-Onboarding.md](docs/ManualAcceptance-UI-Onboarding.md)); nothing about appearance or input in game is claimed as verified until they have been run.
- The persona management foundation, the first NETWORK1 code increment ([docs/networking/NETWORK1_PersonaFoundation.md](docs/networking/NETWORK1_PersonaFoundation.md)): a standalone `AetherFrame.Personas` assembly holding the in-memory model of several independent personas per installation, one active persona chosen by the player and switched only on purpose, identities derived from public keys exactly as the protocol derives them, and the narrow interfaces that protected key storage and an encrypted `.afpersona` backup will implement later. Private keys are accepted only as key pairs checked in managed code (scalar range before any platform import, public point derived from the scalar and compared), a key is committed to custody only after its identity is checked so a refusal leaves no orphaned key, a restore opens only a private copy of the bytes it inspected and only after an explicit supported inspection, and a signer lease stops signing when the player switches personas (an interim policy awaiting decision L10 in the decision register). The private persona label is provisional under the unresolved D9a, and the Release workflow is unchanged while L9 is unresolved. The owner's decisions D3 (several manually selected personas, never bound to a character or a Content ID) and D2 (portable encrypted backups, approved in principle with the format still open) are recorded in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md); every other NETWORK1 decision stays unresolved there, apart from N5, L6, D9b and P2, decided later under the owner's delegation (see Changed, and the networking preview flavour above). No persistent key is generated, nothing is stored or encrypted, no backup format exists, and the plugin does not reference the assembly: nothing opens a connection, and the plugin is unchanged.
- The remote protocol foundation, NETWORK0 ([docs/networking/NETWORK0.md](docs/networking/NETWORK0.md)): a standalone `AetherFrame.Protocol` assembly with a canonical binary encoding, a signed document envelope (ECDSA P-256 over SHA-256, one canonical signature form), persona identities derived from public keys alone, and a deliberately narrow remote profile model (profile and revision ids, creation time, name, image references) that shares nothing with the local Plate model. Every limit the protocol enforces lives in one place, every refused input fails with a typed error, and the test suite covers committed test vectors, canonicalization proofs, and adversarial and seeded fuzz campaigns. An independent review followed by remediation: every reader copies its input before checking it, the codec verifies every document it signs, a remote profile is identified by its owning persona together with its id, and the wire format is marked a draft until the open product decisions are made. The plugin does not reference the assembly, opens no connection, and is unchanged; nothing here needs an account or a server. The protocol is a DRAFT (docs/networking/ProtocolSpecification-v1.md); two independent reviews and their corrections are recorded in docs/networking/NETWORK0_HANDOFF.md.
- Release tooling for a public custom Dalamud repository ([docs/CustomRepository.md](docs/CustomRepository.md)): `tools/AetherFrame.ReleaseTools` checks a release package against the full rule set (x64, built from the released commit, one version and one Dalamud API level everywhere, only the three plugin files, safe entry names), generates and checks the repository metadata Dalamud reads (`pluginmaster.json`, stable and testing channels), and writes and verifies SHA-256 checksum files. The Build and Release workflows run it; the Release dry run keeps the generated metadata as an artifact. Nothing is published to a repository yet, and the plugin itself is unchanged.
- A manual **Publish custom repository** workflow ([docs/CustomRepository.md](docs/CustomRepository.md#publishing)) that puts a published GitHub Release into the custom repository's testing or stable channel after the owner's approval, or rolls a channel back. It verifies every release it describes from scratch, never publishes a draft, never moves a channel to an older version by accident, and writes only `pluginmaster.json` and a README to its own branch. It has not been used yet.

### Changed

- The project moved to [QuietFoxLabs/AetherFrame](https://github.com/QuietFoxLabs/AetherFrame). The plugin's repository link and release downloads use that address, and so does the custom repository address for new installs ([docs/CustomRepository.md](docs/CustomRepository.md)). The old `richhiiee/AetherFrame` addresses still redirect, so a custom repository URL added earlier keeps working.
- The release tooling accepts the address from before the move only as history: in the custom repository file published before it, and in packages up to 0.1.6. Everything it publishes uses the new address ([docs/CustomRepository.md](docs/CustomRepository.md#repository-move)).
- The remote protocol's public API, which the plugin doesn't use yet (NETWORK1 increment 1; decisions N5 and L6 in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md)). Only documents about a profile carry a profile id now (`RemoteProfileDocument`), and a verified document's `Profile` is null for any later document type that isn't about one. The provisional key provider and the server-only limits left the protocol: key storage is the plugin's concern, and those limits are documented in [docs/networking/NETWORK0.md](docs/networking/NETWORK0.md). No signed bytes changed.
- The remote protocol's draft marker and name rule, which the plugin doesn't use yet (NETWORK2 increment N2-2; decisions N3 and D4 in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md), and the owner's advance approval of this signed-byte change). Every signed document is now a draft until the owner freezes version 1: its protocol version is 0x8001 and its signature tag ends in "-draft", so nothing signed during testing can ever be read or verified as a final document, and a final document is refused. A Plate's remote name is 1 to 64 characters in at most 256 bytes, and refuses control characters, line and paragraph separators, the byte order mark, directional formatting characters and the invisible format characters D4 lists; nothing is normalized. The test vectors are regenerated with new signatures. Persona identities are unchanged.
- The remote protocol's layout snapshot, which the plugin doesn't use yet (NETWORK2 increment N2-3a; decisions D8, D5, I1, N1 and N7 in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md), and the owner's advance approval of this signed-byte change). A published Plate is now described by ProfileSnapshot schema 2: the canvas size, the background, and the list of what a viewer draws, in order (texts with their resolved display text and style, images, filled quads and triangles, image quads and bundled-art quads), all in fixed-point integers with a limit on every count, length and value, and ranges that cover everything a local Plate can hold. Every image the snapshot carries is drawn and every drawn image is carried, and only PNG and JPEG are allowed. The metadata-only schema 1 stays for tests. The specification gains section 8.5 and Appendix A.
- The remote protocol's request proof, which the plugin doesn't use yet (NETWORK2 increment N2-3b; decisions S1, D7 and L8 in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md), and the owner's advance approval of this signed-byte change). Submitting a Plate to a server will need a small proof signed by the Plate's own key, in its own signing context: it names the server's address, carries a one-time challenge the server issued, and fingerprints the exact document, so nobody else can submit a copy of a player's Plate, nothing sent to one server works on another, and nothing can be replayed. The specification states the rules every signing context follows, and the test vectors gain valid and rejected proofs.

### Fixed

- The plugin installer shows AetherFrame's icon again. Its address now points at [`AetherFrame/images/icon.png`](AetherFrame/images/icon.png) in this repository; the separate repository it pointed at no longer exists.

Plate Library reliability and data preservation ([docs/reliability/PlateLibraryReliability.md](docs/reliability/PlateLibraryReliability.md)). Saved Plates, Templates, `.aetherframe` packages and the configuration format are unchanged.

- A damaged Plate, Template, character or Plate order file that was read from Dalamud's backup copy is never written over until its damaged bytes are kept under `Recovery`: if that copy failed at load (a full disk), the next save or change now retries it, and is refused with a plain message while it still fails, instead of replacing bytes that may be newer than the backup. Set Active on a character whose file can't be kept gives the same message, Create, Use Template and Duplicate for that character add it to their "couldn't be linked" message, and the log no longer claims a copy it couldn't make.
- A Plate, Template, character or Plate order file holding bytes that aren't valid text still loads from the file itself, exactly as in 0.1.6 (the bytes read as replacement characters), but its original bytes are now kept under `Recovery` before anything writes over it, and a write is refused with a plain message while that copy fails. A Plate or Template read this way says so when its card is hovered. Validity is checked on the bytes, in the file's own encoding (UTF-8, or UTF-16 or UTF-32 behind a byte order mark), so a correctly encoded replacement character is ordinary text, and invalid bytes alone never make a file load from an older backup, become unreadable, lose a newer version's protection, or have its binding or Plate order rebuilt. A Plate or Template card shows this note together with its unsupported-elements warning when both apply.
- Recovery and pre-migration backup copies are flushed to disk and only then given their name, so a copy that fails partway (a full disk) never leaves a partial file that looks like a kept copy.
- Every Plate and Template write is checked to read back as exactly the same text, and to load again, before anything is written.
- Duplicate no longer reports a failure (inviting a second copy) when only the Plate order couldn't be saved, and a reorder whose save fails or is refused puts the previous order back, so the same move can simply be made again.
- Exporting a Plate that holds a value a Plate file can't carry (for example a font size over 1024 typed in 0.1.5) says which value, instead of "The Plate in this file is damaged." Data the editor doesn't show (from a hand edit or a newer build) gets a plain refusal without advice to change it in the editor.
- A package whose image is only mentioned in a text field, or used only where the import doesn't re-point it, or whose text isn't valid UTF-8, is refused as such; a package can't be imported twice after an import that wrote its Plate but reported a failure.
- Add Image names the stored file after the stored content, and a Plate or Template file that couldn't be opened (in use by another program) is no longer described as damaged.
- Recovery and trash file names are stamped in the Gregorian calendar whatever the Windows culture, and Save as Template of a Plate holding data nested too deep for a Template is refused with a plain message instead of the serializer's.

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

[Unreleased]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.6...HEAD
[0.1.6]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.5...v0.1.6
[0.1.5]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/QuietFoxLabs/AetherFrame/releases/tag/v0.1.0
