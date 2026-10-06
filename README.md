# AetherFrame

[![Build and test](https://github.com/QuietFoxLabs/AetherFrame/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/QuietFoxLabs/AetherFrame/actions/workflows/build.yml)

**Enhanced character Plates for Final Fantasy XIV.**

**AetherFrame 0.1.9 Alpha** · [Changelog](CHANGELOG.md) · [Versioning](docs/Versioning.md)

AetherFrame is a [Dalamud](https://github.com/goatcorp/Dalamud) plugin for designing character Plates: profile cards that start from the familiar shape of the in-game Adventure Plate and can grow into fully freeform layouts.

> **Basic mode feels like FFXIV. Advanced mode removes the restrictions.**

> [!NOTE]
> AetherFrame is in **early development**. It is not an official Dalamud repository plugin, and its features, file formats and internal APIs may still change.

---

## Screenshots

AetherFrame includes a local Plate library, a guided Basic Editor, a freeform Advanced Editor, and a clean in-game Plate Viewer.

### Plate Viewer

![A finished Celestial Sakura Plate shown in the AetherFrame Plate Viewer](docs/screenshots/plate-viewer.png)

A finished Celestial Sakura Plate in the Plate Viewer, a clean presentation view for the saved Active Plate, opened with `/af view`.

### Editing and Plate Management

#### My Plates

![The My Plates window with saved Plate cards](docs/screenshots/my-plates.png)

Create, organize, duplicate, import, preview, and choose the Active Plate from a local Plate library.

#### Basic Editor

![The AetherFrame Basic Editor editing the Celestial Sakura Plate](docs/screenshots/basic-editor.png)

A familiar Adventure Plate-style workflow for quickly editing layout, identity, details, message content, and visual styling.

#### Advanced Editor

![The AetherFrame Advanced Editor with the Layers panel and canvas](docs/screenshots/advanced-editor.png)

Freeform control over text, images, Components, layering, placement, scaling, rotation, and the Plate canvas.

---

## Features

### Plates and My Plates

- **Multiple saved Plates.** Each Plate is a complete, independent design. Keep as many as you like.
- **My Plates.** Browse, search, preview, rename, duplicate and delete Plates, and choose which Plate is Active for each character. Duplicating a Plate is an easy way to keep several variations of a look side by side.
- **Plate previews.** Plate cards in My Plates and a preview pane show each Plate at a glance.
- **Plate Viewer.** A dedicated window that shows a Plate fitted to its size.
- **Preview.** Both editors' Preview opens the Plate in the Plate Viewer, exactly as it will look, and it follows your edits.

### Basic Editor

A structured editor modelled on FFXIV's Adventure Plates. You fill in sections, and AetherFrame handles the layout.

- **Identity and details**: name, title, Home World, Free Company, and up to eight Favorite Jobs in your chosen order, shown as full names when they fit and as job abbreviations when they don't.
- **Portrait**: import your own image, with Fill / Fit / Stretch framing and a mirrored layout option.
- **Playstyle, active hours and a free-form message.**
- **Themes, backgrounds and patterns**: built-in colour themes plus procedural background patterns.

### Advanced Editor

A freeform canvas for when the Basic layout isn't enough.

- Place, move, resize and rotate text and image elements anywhere on the Plate.
- Layers, snapping, undo/redo and per-element styling.
- Custom text with bundled fonts, so a Plate renders the same on every machine.
- Custom images from your own files.

### Components

Reusable decorative pieces you add to a Plate and restyle without redrawing anything.

- **Procedural Components**: Plate frames, portrait frames and overlays, name backings, dividers, section headers and corner ornaments, all drawn in code and tintable.
- **Graphical Components and Art Styles**: original artwork created with AI assistance. The Celestial Dream *Astrolabe Pivot* corner ornament ships inside the plugin. The Art Styles' artwork, the full-color **Celestial Sakura** set included (a background, Plate frame, portrait frame, name backing, two dividers, a section header and a corner ornament), downloads from GitHub the first time you use it and is kept on your PC; only the styles' preview cards ship inside the plugin.
- **Corner-specific placement**: choose which corners a corner ornament appears on.
- **Overflow**: Components can deliberately extend past the Plate's edges, and previews account for it.

### Templates

- Start a new Plate from a built-in Template (*Adventure Plate Classic* or *Blank Canvas*).
- Save any Plate as your own Template and reuse it later.

### Import and export

- Share a Plate as a single **`.aetherframe`** file that includes the images it uses.
- Imports are validated before anything is written. Size limits, path checks, image checks and document validation all run on a staging copy first.

### Local assets and data safety

- Imported images are stored and tracked locally. Unused images are not removed automatically in this version.
- **Forward compatibility.** Plate data is versioned and migrated. Content from a newer version of AetherFrame that this version doesn't understand is preserved rather than discarded.

---

## What’s coming

AetherFrame 0.1.9 is still an early version. There is a lot more I want to build before I consider it finished.

### More ways to design Plates

I want to keep expanding what you can actually make. That means more visual sets, Templates, backgrounds, typography options, images, Components, and more control in the Advanced Editor.

### More for My Plates

My Plates will grow beyond simply storing your Plates. I want better organization, restoring Plates from Trash, improved previews, more Template options, and better ways to keep different versions of the same design.

### More in the Basic Editor

The Basic Editor is supposed to feel familiar if you already know FFXIV Adventure Plates. I want to give it more customization while keeping it simple enough that you do not have to use the Advanced Editor unless you want to.

### Sharing (alpha)

Sharing is in the testing channel's build now, and off until you turn it on. Turn it on for a character in My Plates' **Sharing** window, prove the character is yours with a one-time code on your Lodestone profile, and its Active Plate becomes viewable by other players who share, like the game's Adventure Plates: from the game's right-click menu on your character, or by name and World. Saving your Active Plate updates what they see, and turning sharing off removes it from the server. Your own copy always stays on your PC. Next comes making it smoother and more reliable for more players.

### Optional RP details

I would also like to add a small amount of optional character information for people who want it. The idea is to complement the Plate, not turn AetherFrame into another full RP profile plugin.

### Dalamud release

There is still performance work, UI polish, accessibility work, and general cleanup to do before AetherFrame is ready for a wider release.

The finished idea is simple: start with something that feels familiar to anyone who has made an FFXIV Adventure Plate, then give people the freedom to take it much further.

---

## Local first

Everything AetherFrame does happens on your own machine, apart from sharing, which you turn on yourself, and Art Styles' artwork, which downloads from GitHub the first time you use a style and is then kept on your PC.

- No account is needed.
- Editing is entirely local.
- Plates, Templates and images are stored in the plugin's local configuration folder.
- Import and export are file-based: you choose what to export and who you give it to.
- No online service is required for the core experience. An Art Style needs GitHub once, the first time you use it; until its artwork arrives, a Plate shows the style's colors.

## Privacy

Sharing is opt-in, one character at a time, and it keeps to these rules:

- **Intentional sharing** rather than passive discovery.
- No silent telemetry.
- No automatic scraping of nearby players.
- No public Content IDs, no alt correlation, and no public location history.
- One exception to sending nothing in the background: while a character shares, the plugin tells the sharing server about once a minute that it is still logged in, so My Plates can show how many sharing characters are online. The server keeps this in memory only and stops counting the character at once when it stops, or within about 3 minutes after a crash or a last message that doesn't get through. It answers sharing players with the total alone ("fewer than 5" while it is under 5), the same to everyone for each 5 minutes, so the total follows a login or logout at its next refresh. It keeps no history of who was online: messages that get through aren't logged, one that fails leaves a line for 14 days saying what failed and when, never whose, and its rate limits remember, in memory for up to an hour, when a character started being counted. The total still moves by one from one 5 minutes to the next when a sharing character logs in or out once 5 or more are online, and below 5 for a player who adds characters of their own, so someone watching it closely can sometimes tell, to within about 5 minutes, when a character they know comes or goes. Characters that don't share, and players who never turn sharing on, send nothing.

---

## Installation

AetherFrame is in testing, through its own custom Dalamud repository:

1. Type `/xlsettings` in game and open **Experimental**.
2. Tick **Get plugin testing builds**.
3. Under **Custom Plugin Repositories**, add `https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/refs/heads/plugin-repository/pluginmaster.json`, make sure it is enabled, then **Save and Close**.
4. Type `/xlplugins`, search for **AetherFrame**, and install it.

Dalamud then keeps it up to date. The releases themselves are on [GitHub Releases](https://github.com/QuietFoxLabs/AetherFrame/releases), and Dalamud does the downloading. The [tester guide](docs/Testing.md) has what to look at and how to report problems. Submission to the official Dalamud repository is welcome but not required for this.

Developers can also build it from source and load it as a dev plugin (see below).

## Building from source

### Requirements

- Windows with FFXIV, XIVLauncher and Dalamud installed, and the game run with Dalamud at least once
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Dalamud API 15 (the project uses `Dalamud.NET.Sdk` 15)
- **x64** platform. The plugin project only builds for x64.

### Build

From the repository root:

```bash
dotnet build AetherFrame/AetherFrame.csproj --configuration Debug -p:Platform=x64
```

```bash
dotnet build AetherFrame/AetherFrame.csproj --configuration Release -p:Platform=x64
```

The output is written to `AetherFrame/bin/x64/<Configuration>/`.

### Tests

`AetherFrame.Tests` covers the Dalamud-independent logic (documents, persistence, packages, editor sessions and layout) and runs without the game:

```bash
dotnet test AetherFrame.Tests/AetherFrame.Tests.csproj
```

### Loading in game

1. Open `/xlsettings` → **Experimental** and add the full path to the built `AetherFrame.dll` under **Dev Plugin Locations**.
2. Open `/xlplugins` → **Dev Tools → Installed Dev Plugins** and enable AetherFrame.
3. Use **`/aetherframe`** (or **`/af`**) to open My Plates. See [Commands](#commands).

### Commands

`/af` is the short form of `/aetherframe`. Both accept the same arguments.

| Command | What it does |
|---|---|
| `/aetherframe`, `/af` | Opens or closes My Plates |
| `/aetherframe view`, `/af view` | Shows your character's Active Plate |
| `/aetherframe version`, `/af version` | Prints the running AetherFrame version and build in chat |

---

## Project structure

```
AetherFrame/            the plugin
  Domain/               Plate documents, Basic layout rules, Components, Templates (no Dalamud dependencies)
  Persistence/          versioned JSON storage, schema migrations, unknown-data preservation
  Services/             Plate & Template libraries, assets, fonts, .aetherframe packages, thumbnails
  UI/Editor/            editor sessions: history, selection, snapping, Basic/Advanced coordination
  UI/Rendering/         Plate renderer, backgrounds, text, Components, previews
  Windows/              Dalamud/ImGui windows: My Plates, Basic Editor, Advanced Editor, Plate Viewer, import
  Hosting/              thin adapters over Dalamud services
  Services/Network/, Hosting/Network/, Windows/Network/   sharing: the server client, personas and keys, the Sharing and AetherFrame Plates windows (left out of the player flavour, -p:AetherFrameNetworkPreview=false)
  UI/Theme/, UI/Tutorial/   design tokens and the tutorial's logic
  Assets/               Component artwork: previews and the Astrolabe embedded, the rest hosted (ArtFiles.txt)
  Fonts/                bundled fonts (SIL Open Font License 1.1, or Apache License 2.0 for some library families), embedded in the DLL
AetherFrame.Tests/      pure-logic tests that build without Dalamud
AetherFrame.Protocol/   the signed sharing protocol (compiled into the plugin)
AetherFrame.Personas/   persona and key management (compiled into the plugin)
server/                 the sharing server, its image worker and the Lodestone relay, with tests
deploy/                 the server's deployment kit (Docker, Caddy)
tools/                  release tooling (package and repository checks), art and font pipelines, tester-kit scripts
distribution/           the custom Dalamud repository's configuration, dry run and tester kit
```

### Key concepts

- **Plate / Profile document.** A Plate's content is a single versioned document (`ProfileDocument`) holding the canvas, background, elements (text and images), Components and Basic-mode settings. Both editors work on the same document, so a Plate can move from Basic to Advanced.
- **My Plates (`PlateLibraryService`).** Stores every saved Plate plus per-character bindings (which Plates belong to a character and which one is Active).
- **Active Plate.** The Plate AetherFrame presents for a character when nothing more specific is asked for (e.g. `/aetherframe view`). Resolved in one place (`ActivePlateResolver`); a character with no Active Plate gets an explicit empty state, never a substitute.
- **Basic Editor.** Structured input (identity, portrait, playstyle, message, theme) mapped onto an Adventure Plate-style layout.
- **Advanced Editor.** Direct manipulation of every element on the canvas, with layers, snapping and undo.
- **Templates.** Starting points for new Plates: built-in ones compiled into the plugin, plus user Templates saved locally.
- **Components.** Decorations described by a stable definition id and per-instance settings. Procedural ones are drawn in code, graphical ones use artwork that is inside the plugin (the Astrolabe) or downloaded from GitHub the first time it is used (the Art Styles). Plates store only ids, never the art itself.
- **Rendering.** One renderer draws a Plate for the editors, the Plate Viewer and My Plates previews, so all of them match.
- **Assets.** User images are copied into a local asset store, checked on import and tracked by reference. Unused images are not cleaned up automatically in this version.
- **Packages.** `.aetherframe` files are ZIP-based packages containing a manifest, the Plate document and its images. They are validated in a staging area before anything is imported.

The Component artwork is the optimized copies in `AetherFrame/Assets/` (`ArtFiles.txt` lists the hosted ones), and its icon is [`AetherFrame/images/icon.png`](AetherFrame/images/icon.png).

---

## Development note

I use AI heavily while developing AetherFrame, mainly for implementation and code review. I decide what gets built, how the product works, and test the plugin in game myself. In the terms of the [Dalamud AI Usage Policy](https://dalamud.dev/plugin-publishing/ai-policy), that is the *Copilot* level.

The plugin icon was generated with ChatGPT and then refined, and the Art Styles' artwork (their preview cards included) and the Celestial Dream and Celestial Sakura artwork were also created with AI assistance. The plugin's description in the Dalamud installer says so. The seven approved Celestial Sakura pieces are served unmodified, so they keep their embedded C2PA Content Credentials, which record how each image was made (the style's Section Header is a half-size copy of its source and carries none). I'd like to replace the icon with a hand-made one before AetherFrame goes into the official Dalamud repository.

## Support and feedback

AetherFrame is still taking shape, and feedback is very welcome on the [issue tracker](https://github.com/QuietFoxLabs/AetherFrame/issues):

- **Something broken?** Open a [bug report](https://github.com/QuietFoxLabs/AetherFrame/issues/new?template=bug_report.yml). Include what `/af version` prints.
- **An idea?** Open a [feature request](https://github.com/QuietFoxLabs/AetherFrame/issues/new?template=feature_request.yml).
- **Testing a build?** See the [tester guide](docs/Testing.md).

Issues are public, so leave out character names and anything else you'd rather keep to yourself.

## License

AetherFrame is licensed under the [GNU Affero General Public License v3.0](LICENSE.md).

Bundled fonts are licensed separately: AetherFrame's own three families under the SIL Open Font License 1.1 (`AetherFrame/Fonts/THIRD-PARTY-FONT-LICENSES.txt`), and the font library's Google Fonts families each under the SIL Open Font License 1.1 or the Apache License 2.0 (`AetherFrame/Fonts/Library/THIRD-PARTY-FONT-LICENSES.txt`).

## Links

- Repository: <https://github.com/QuietFoxLabs/AetherFrame>
- Changelog: [CHANGELOG.md](CHANGELOG.md)
- Tester guide: [docs/Testing.md](docs/Testing.md)
- Releasing and Dalamud submission: [docs/Releasing.md](docs/Releasing.md)

---

<sub>AetherFrame is a fan-made plugin and is not affiliated with or endorsed by Square Enix. FINAL FANTASY XIV © SQUARE ENIX CO., LTD.</sub>
