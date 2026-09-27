# Manual acceptance for the reliability milestone (v0.1.6)

These are the checks that need a running game. Everything else in the milestone is covered by the automated suite (`dotnet test`); nothing below is claimed as verified until someone has done it in FFXIV.

**Setup.** Windows, FFXIV with Dalamud (API 15), the Release build installed as a dev plugin (see [Testing.md](Testing.md)). Before starting, copy a real v0.1.5 data folder (`%AppData%\XIVLauncher\pluginConfigs\AetherFrame`) that holds several Plates, Templates, imported images and at least two characters, and keep the copy: several steps damage files on purpose. Keep `/xllog` open; every step says what to look for in `dalamud.log`. "Log names the file only" means the line contains `<guid>.json` and never a folder path.

Hash the data folder before step 1 (for example `Get-ChildItem -Recurse | Get-FileHash`) and compare after steps 1, 2 and 20: loading and reading must never change a file.

## A. Dalamud lifecycle

1. **Load with v0.1.5 data.** Enable the plugin. My Plates lists every Plate; the log has `Plate Library loaded` and `Template Library loaded` with no Warning or Error from AetherFrame; no file in the data folder changed.
2. **Reload from the installer** (unload and load in the same process) while an editor is open with unsaved edits. Unload finishes within about 5 s, no exception in the log, the Plate on disk is unchanged, and the reloaded plugin lists everything again.
3. **Disable mid-import.** Start Import as New Plate on a package with several large images, then disable the plugin while the button reads "Importing...". The unload waits for the import (at most 5 s) and the import finishes as one operation: the whole Plate exists with all its images after the next load, and `package-staging` and `asset-staging` are empty. Only if the 5 s ran out first (log: "stopped waiting") may nothing of it exist, and then no half of it does.
4. **Disable mid-check.** Pick a large `.aetherframe` in Import and disable the plugin while the window says "Checking". The log shows no `IOException` and no sweep racing the check; the next load's `SweepStaging` leaves nothing behind.
5. **Canceled load.** Close the game while the plugin is still loading (or delay the load past Dalamud's timeout). The log shows AetherFrame's own teardown (`UI shutdown`, `window and texture disposal`) before the `OperationCanceledException`, and no `could not load the Plate Library` error for the game-closing case.
6. **Game exit with an editor open.** No crash; after restart no leftover `.tmp` or `.importing` files anywhere under the data folder.

## B. Textures and fonts

7. **Mono text at FontSize 1024 from a package.** Import a package whose text element uses the Mono family at size 1024 (all four styles if possible). The Import Preview, the imported card and the Plate Viewer show the text; the log has no `Failed` line from the font atlas (`RebuildFontsPrivateReal`, `AetherFrame.ProfileFonts`); text at other sizes keeps rendering afterwards.
8. **Zoom.** In the Advanced editor zoom a 96 px Mono text to 4x: it renders, slightly upscaled from the largest Mono tier (140 px), with no font error. Repeat with a 96 px Sans (280 px tier) and Serif (240 px) text at 4x, and with a legacy Plate whose text uses the Dalamud default family at 2x (upscaled from 96 px, its cap).
9. **Glyph coverage.** Type a Cyrillic word, an ellipsis (…), a middle dot (·), guillemets (« »), a degree sign (°), a section sign (§), a euro sign (€), a trademark sign (™), a Greek omega (Ω) and a not-equal sign (≠) into a text element in each bundled family: all render. In Mono also an arrow (→), a heart (♥), a Hebrew letter (א), box drawing (─ │ ═ ║ ╔ ╝ ╬) and blocks (█ ▀ ▄ ░ ▒ ▓): they render, as they did in 0.1.5. A CJK character renders as the fallback glyph, as it did in 0.1.5.
10. **Atlas stability.** Open a Plate that uses all three families in all four styles plus a Dalamud-default element, then the Plate Viewer on another Plate and My Plates. Memory stays flat; the log shows no repeated font rebuilds.
11. **16-bit PNG.** Import a 16-bit RGBA PNG of 8192x4096 through Add Image: refused with the message naming the 16-bit limit, and the game does not allocate the texture. A 16-bit 4096x4096 PNG imports and displays.
12. **Stray staging file.** Put a file named `<32 hex>.importing` into `asset-staging`, restart the plugin: it is gone, and existing images still load.

## C. ImGui behaviour

13. **Typed slider values.** Ctrl+click each slider (Inspector: Rotation, Size, Letter Spacing, Line Spacing, Auto Fit Minimum, Text Opacity, Outline Thickness, Outline Opacity, Shadow Opacity, Image Opacity; Background panel: Opacity, Gradient Angle, Texture Intensity, Texture Rotation; Basic Styling: Size, Opacity, Outline Thickness, Shadow Opacity), type `5000` then `1e39`, press Enter. The value clamps to the slider's maximum, except the Inspector's image Rotation, which wraps as in 0.1.5 (`5000` becomes 320°, `-45` becomes 315°) and resets `1e39` to 0°; Save and Export succeed.
14. **Unsaved-changes prompt.** Click Save in the action bar and immediately the title-bar X. Both Save and Discard in the prompt are disabled until the save lands; then Discard closes the editor. Repeat with the My Plates "open another Plate" prompt.
15. **Save failure keeps the editor.** Make the `Profiles` folder read-only, edit and press Save: a plain message with no path, the editor stays open and still shows unsaved changes. Restore the folder; Save succeeds.
16. **Import Preview can't be closed mid-import.** Click Import as New Plate on a multi-image package: while it reads "Importing..." the title bar has no close button and Escape does nothing (no close or open sound, no flicker); the window then closes on its own with the "Imported ..." status in My Plates. Cancel and the close button work as usual before the import starts.
17. **Basic section at the element limit.** In the Advanced editor add text elements until 255, switch to Basic and reveal a section that does not exist yet: the message says the Plate is at its element limit, no stray heading element appears, and Undo is unchanged.

## D. Framework threading

18. **Large library load.** Duplicate Plates until the library holds about 100. Enable the plugin: the load completes, the frame hitch at load is shorter than in v0.1.5 (compare with a frame-time overlay), and the log shows the Library loading without an exception.
19. **Concurrent views.** With the Plate Viewer showing a Plate and My Plates open, Save, Rename, Duplicate, Delete and Set Active that Plate from both editors: no crash, the viewer and cards follow, log clean.
20. **Character context.** With My Plates and the Plate Viewer open, log out and back in: the character line and Active Plate follow within half a second; no exception.

## E. DPI

21. **Global scale 100 %, 150 %, 200 %.** Import Preview, both editors, the unsaved-changes prompt and My Plates render usable at each scale, unchanged from 0.1.5.

## F. Input and controller

22. **Ctrl+S during a drag.** Drag an element and press Ctrl+S while the mouse button is held: the saved file holds the on-screen position, no "being saved" flash, and one Ctrl+Z restores the pre-drag position.
23. **Title layout during a save.** In Basic, pick an inline title layout right after opening the Plate and press Ctrl+S at once: no error, the layout refines after the save, the editor shows unsaved changes once, Undo reverts the layout as one step.
24. **Controller.** Navigate My Plates, the Import Preview and the prompts with a gamepad: unchanged from 0.1.5.

## G. Real import and export

25. **Round trip.** Export a Plate, re-import it: a new Plate with the same name is added, nothing existing is replaced, nothing becomes Active.
26. **Hostile samples.** Import each sample the tests build (a two-end-record ZIP, an 8 MiB profile of tiny values, a profile with explicit `null` lists, a 16-bit 32 MP image, a name of only invisible characters): each is refused within a second with a plain message, the game stays responsive, and `package-staging` is empty after Cancel.
27. **Package images on a slow disk.** Import a package with 20 or more large images from an HDD or USB drive: the frame does not freeze on "Import as New Plate"; the log shows the import finishing on its own.
28. **Disk full.** With the plugin data folder on a nearly full drive, Save a Plate until it fails: the message is plain (in use or disk full), the editor stays dirty; free space and Save again: it succeeds, and the log shows one Warning naming only `<guid>.json` with "stuck open" if Dalamud's temporary file was left behind (`<guid>.json.tmp` stays until restart).
29. **Backup recovery.** With the plugin unloaded, truncate a Plate file that Dalamud has already saved once; reload. The Plate loads from Dalamud's backup, the log names the file with "read it from the backup copy", and a `Recovery/<guid>.damaged-*.json` copy of the truncated bytes exists. Saving the Plate then writes the recovered content.
30. **Unavailable binding.** With the plugin unloaded, lock a `Characters/<id>.json` file open in another program (one Dalamud has saved before, so a backup row exists), reload, then Set Active for that character: the message says the settings could not be read and asks for a restart; the file is untouched, and the log shows no "backup copy" line, since a file that merely can't be read is never replaced by its backup.
31. **Newer configuration.** Edit `AetherFrame.json` to `"Version": 3` with an extra field, load, open the Basic editor once: the file keeps version 3 and the extra field, and the one-time suggestion is not shown again.

Record each step as pass or fail with the build shown by `/af version`. Anything failing in sections A to D or G blocks the release.
