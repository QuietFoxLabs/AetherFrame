# AetherFrame design guide

How AetherFrame's interface looks, how it is built, and how to extend it without breaking either. This covers the plugin's own windows (My Plates, both editors, the Plate Viewer, prompts, the offer of unsaved changes kept at unload, and the tutorial); it does not cover how a Plate itself renders, which is the renderer's business and unchanged by any of this.

## The identity

**Basic mode feels like FFXIV. Advanced mode removes the restrictions.** The interface follows the same rule: familiar to anyone who has used the game's own windows, and quiet enough that the Plate being made is the brightest thing on screen.

- **Surfaces** are deep midnight: a near-black window (`Void`), panels one step lighter (`Surface`), cards, inputs and popups one step lighter again (`SurfaceRaised`), and hover and press states above that. Layers read as layers because each is brighter than the one behind it, never because of a border alone.
- **One accent.** The aether (`Aether`, a violet-blue) marks what is selected, active or primary: the selected navigator row, the active editor mode, the one primary button on a screen, section rules. It is used for nothing decorative.
- **One glow.** A restrained cyan (`Glow`) appears only for keyboard focus and the tutorial spotlight, so when it appears it means "here".
- **One gold.** The Active Plate badge keeps the warm gold FFXIV players already read as "chosen". Nothing else is gold.
- **Text** is near-white, never pure white; secondary and muted tones carry summaries and hints. Every tone meets a readable contrast ratio on every surface it is drawn on, and the tests in `AetherPaletteTests` hold that.
- **The mark**: four frame corners around a four-point spark, drawn in code (`AetherBrand`). It sits beside window titles, on the tutorial card and the first-run offer, and its corners frame a selected card and the spotlight. No asset, nothing borrowed.
- **Typography**: headings in the game's own Axis face (18 / 14 / 12 pt for display, heading and small labels), body text in Dalamud's default font so it matches every other plugin and the player's scale setting. Section labels are small, uppercase, in the accent, with a thin rule.

Avoided on purpose: neon, gradients on chrome, decorative borders, more than one accent, and anything that competes with the Plate for attention.

## Tokens

Everything comes from two Dalamud-free files under `AetherFrame/UI/Theme/`:

- `AetherPalette`: colors by role (surfaces, lines, text tones, accents, semantic tones and their tints, dimming). Values are sRGB 0..1 as ImGui takes them. `AetherPalette.Contrast` and `Luminance` implement the WCAG formulas the tests use.
- `AetherMetrics`: measurements in unscaled pixels. Spacing follows a 4 px grid (`SpaceXs` 4, `SpaceSm` 8, `SpaceMd` 12, `SpaceLg` 16, `SpaceXl` 24, `SpaceXxl` 32); radii are 4 / 6 / 10 for inputs, panels and cards; borders 1 / 2; dialog buttons 120 px wide. The editors' property label column (92 px) lives in `EditorWidgets.LabelColumnWidth`.

A window never hardcodes a color or a spacing; it names a role. Multiply every metric by `ImGuiHelpers.GlobalScale` (or use `EditorWidgets.Scaled`) at draw time: Dalamud scales its own style the same way, so the interface stays usable at 150 % and 200 %.

## The ImGui style and the window chrome

`AetherFrame/Windows/Theme/AetherStyle.cs` maps the tokens onto ImGui's style colors and variables. It is pushed for AetherFrame's windows only and popped after each one:

```csharp
public override void PreDraw()  { chrome.PushStyle(); /* the window's own PreDraw */ AetherWindowChrome.ApplyPolicy(this); }
public override void PostDraw() { /* the window's own PostDraw */ chrome.PopStyle(); }
```

Dalamud calls `PostDraw` whenever it called `PreDraw`, including the frame in which `Draw` threw, so the pair is always balanced. Inside `Draw`, every push is an `ImRaii` `using` scope, which unwinds on an exception. Dalamud does not recover ImGui's stacks after a plugin throws, so this discipline is not optional. Nothing here changes Dalamud's global style or another plugin's.

`ApplyPolicy` is the one window flag the tutorial needs (below).

## Reusable controls

`AetherFrame/Windows/AetherControls.cs` holds the controls every window shares, all of them ordinary ImGui items with the palette applied (keyboard and gamepad navigation, tooltips and ids work as always):

| Control | Use |
|---|---|
| `SectionHeader(text)` | A small uppercase accent label with a rule: the heading of a group of controls. |
| `Secondary`, `Muted`, `MutedInline` | Text in the secondary / muted tone (wrapped). |
| `PrimaryButton`, `SecondaryButton`, `DangerButton`, `GhostButton` | One primary action per screen; destructive actions red; quiet actions ghost. |
| `KeyHint` | Keycaps for shortcuts. |
| `Tooltip` | A wrapped tooltip for the last item. |
| `StatusLine(tone, text)` | Saved / working / error lines with an icon. |
| `Callout(tone, text, title)` | A tinted box with an accent bar: on-screen help, a warning, an error. |
| `EmptyState(icon, title, description, action)` | The centered invitation shown when a list or panel is empty. |
| `CardFrame`, `SelectionRing`, `Glow` | A card's surface and its selected state; the accent ring with the frame corners. |

`EditorWidgets` (the editors' compact building blocks: property labels, icon buttons and toggles, segmented choices, swatches, collapsible sections) stays, with its colors now aliasing the palette.

## Layout patterns

- **My Plates** is the home: a brand row, one primary action (Create Plate), the search, Import and Help; then the card grid; then a status footer. Cards are 196 px wide with a 16:9 preview and the name below.
- **Both editors** share the action bar (My Plates, Basic | Advanced, the Plate menu under the Plate's name, Undo/Redo, the save state, Preview / Revert / Save), always in view above everything that scrolls. The Plate menu's control keeps its icon and caret at every width; the name fills the room that's left. When one row can't hold the bar, the save state and its buttons take a second row, so nothing is cut off. The menu's results and errors show on a line of their own under the save state.
- **Plate menus** are one component (`PlateMenu`): a card's right-click menu in My Plates and the Plate menu in the editors offer the same actions with the same words, in the same order, and ask the same prompts. Delete is only on cards, where the whole Library is in view. A Plate menu shows the Plate over the game with View; the action bar's Preview opens the same Plate Viewer on the editor's live document, edits in progress committed first (there is no in-editor preview). An action that uses the saved Plate says so while the editor has unsaved changes, and nothing saves on its own.
- **The Basic editor** is a navigator rail (Style, Portrait, Identity, Details, Message), the selected category's controls with its title and summary pinned, and the live Plate. Narrow windows fold the rail into a strip.
- **The Advanced editor** is Layers, the canvas and the Inspector side by side, with a tool row above and a status bar below.
- **Prompts** (unsaved changes, revert, delete, rename) are modal popups with the question, one line of consequence in the muted tone, and a right-aligned button row: the destructive choice red, the safe one primary or secondary, Cancel ghost.
- **Unsaved changes kept** (`KeptChangesWindow`, worded and run by `KeptChangesOffer`) is a small centered window like the first-run offer, shown at a load once a character is logged in when AetherFrame kept an editor's unsaved changes as it unloaded. It names the Plate, the editor and the time, adds what changed meanwhile (saved again, can't be opened by this version, deleted, couldn't be opened just now) in the warning tone, then one consequence line in the muted tone and a right-aligned row of 120 px buttons, wider only for a label that needs it: Restore or Restore as New Plate primary, Discard red, Decide Later ghost. Several kept changes come one at a time, newest first, with "1 of N" beside the title, and errors show under the text in the error colour. When the open Plate has unsaved changes of its own, the row gives way to My Plates' Save, Discard or Cancel question. The window isn't modal, so that question goes away, with a line saying so, once that Plate isn't open with those changes. Closing it keeps everything, and My Plates shows a reminder under its header, in the tutorial reminder's style, with Review.

## The tutorial

### Architecture

Two halves, joined by one narrow seam.

`AetherFrame/UI/Tutorial/` is Dalamud-free and fully tested:

- `TutorialTarget`: every control the tutorial can point at.
- `TutorialAnchorRegistry`: where each target was drawn this frame (its rectangle and the clip rectangle it was drawn under). Anchors expire after two frames, so a closed window or collapsed section leaves no stale target.
- `SpotlightGeometry`: the hole around a target and the up-to-four dimming strips that tile the rest of the viewport (no gaps, no overlap; tested with random sampling).
- `TutorialCardPlacement`: where the card goes (below, above, right, left of the hole, never over it when there is room, always inside the viewport).
- `TutorialScriptModel` and `TutorialScript`: chapters and steps as data, validated at startup and in tests.
- `TutorialSession`: the progression. Next, Back, chapter jumps, skip and completion; steps whose requirement isn't met are skipped (when the author said so) or show a navigation hint; steps already done are passed over on the way forward; a step whose control is off screen says so rather than pointing at nothing.
- `TutorialPreferences`, `FirstRunDetector`, `OnboardingCoordinator`: persisted state, first-run detection and the one owner of it all.

`AetherFrame/Windows/Tutorial/` is the ImGui side:

- `TutorialAnchorMarks`: what windows call (`Mark`, `MarkRect`, `MarkWindow`, `IsWanted`, `RevealIfWanted`).
- `TutorialOverlayWindow`: an invisible, input-free window added right after every other AetherFrame window, so its PreDraw runs after they have drawn and marked their anchors; it computes the frame's overlay and draws the spotlight ring over everything.
- `TutorialShadeWindow` (five): four strips and a hole cover. Each is an ordinary ImGui window that takes mouse input, so a click on the dim lands nowhere else, while a click inside the hole (when the step allows interaction) reaches the control beneath. A piece with nothing to cover is parked off screen rather than closed, so it never re-steals focus by reappearing.
- `TutorialCardWindow`: the explanation card.
- `FirstRunPromptWindow`: Start Tutorial / Maybe Later / Do Not Show Again.

### How overlay input is controlled

ImGui hit-tests windows front to back. The shades are created in front of AetherFrame's windows, and while the spotlight is up every AetherFrame window carries `NoBringToFrontOnFocus` (`AetherWindowChrome.ApplyPolicy`), so clicking the highlighted control gives it keyboard focus without letting the rest of its window out from under the dim. Steps that only explain a control cover the hole with a transparent window, so nothing is clickable; steps that ask the player to use the control leave the hole open. Modal popups (the Create Plate chooser) block input to everything else on their own, exactly as they do outside the tutorial; the tutorial waits for the condition it needs.

Each anchor records the top-level window it was drawn in: a child region's root, a docked window's dock host, and a popup is its own. `MarkWindow` on a top-level window records all of it, title bar included. When the step changes, the driver brings that window in front of AetherFrame's other windows, then the shades and then the card in front of it, so My Plates never hides the editor a step points into. Only the display order changes (`ImGuiP.BringWindowToDisplayFront`), never focus: focusing another window would close an open popup. It happens again only when that window rises above the card (a popup opened later comes to the front by itself), never every frame, so a menu the player opens afterwards still shows in front.

A popup is the exception. It is in front of AetherFrame's windows already, and a modal one dims everything behind it by itself, so the shades stay behind it and never cover any of it. Only the card comes in front of it, so a modal doesn't dim the card. Even the card stays behind when it would overlap the spotlight, which happens on a small screen with no room beside the popup: a card over a modal popup can't be clicked, and would hide the part of the popup beneath it.

The dim covers only the area AetherFrame's own open windows occupy (the driver looks their ImGui windows up by name), so the game's interface and other plugins' windows stay reachable and undimmed; a window that overlaps that area is dimmed with it. When a step has nothing to point at and needs the player to get somewhere (open a section, select an element, open My Plates), the shades take no input at all and lighten to a tint: the interface stays usable and the card waits. A click on the dim hands focus to the card, so Escape closes the tutorial (remembered where it stopped) and Left, Right and Enter page it only while the pointer is over the focused card.

The driver computes in PreDraw, which Dalamud does not guard, so it guards itself: a fault logs, stands the overlay down and suspends the tour. Every push a window makes around its frame is recorded in a ledger (`AetherStyle.NotePushed`), and the plugin's draw pops whatever is still outstanding if an exception escapes a PreDraw, so AetherFrame's style can never be left on the windows drawn after it. The card is opened only when the tour starts, so a card Dalamud closed after a fault stays closed and its close stops the tour instead of reopening into the same fault.

### Authoring conventions

- A step is one card: a title, at most ~420 characters of body, one control. Explain what the control does and when to use it, in the interface's own words.
- `Inspect` for a control the player looks at; `Interact` for one they use; `Narrative` for a card with no control (a chapter's opening, the completion).
- `Requires` names what must be true (an editor open, a text element selected). Give `FallbackBody` (how to get there), and where a control is the way there, `FallbackTarget`; where a window can be opened safely, `FallbackAction`. `SkipIfUnmet` for a step that simply doesn't apply (a card about your first Plate when there is none).
- `AdvanceWhen` moves on by itself once the condition turns true, having been false while the step showed; a step already done when reached is passed over.
- `WaitsForAction` for a step the player must actually do (creating their first Plate): Next holds until `AdvanceWhen` is met, shows `WaitHint` instead, and flashes the spotlight ring on the control to click. Every `Interact` step with an `AdvanceWhen` waits this way (the owner's rule of September 30, held by a test). So does any step whose `Requires` isn't met while it spotlights a `FallbackTarget` (switching to the Advanced Editor, say): Next waits for the requirement and flashes the way there. A requirement with only a `FallbackAction` (a button on the card) still lets Next on. Back, Chapters and Skip tour still work, so it is never a trap. Use it sparingly, and never for anything the player might not want to do.
- Keep the tour current: a pull request that adds or changes something a player sees updates the tutorial in the same pull request, or says in its description why not (the owner's request of September 30, 2026). Sharing is taught too (the owner's request of October 2, 2026), in chapters marked `SharingOnly`: `TutorialScript.ForBuild` leaves them out of the player build, which has no sharing to point at. Their steps only point and explain. A control that sends anything (Turn on sharing, Check, Pause, Turn off, View, Hide, Report) is only ever an `Inspect` target; the player may use only the controls that open a window (Sharing in My Plates, Find a player's Plate). A control drawn only in one sharing state (the consent before a character shares, its status after) shows as not in view in the other, and Next goes on. `TutorialSessionTests` holds all of this, and checks each label a sharing step names against the control's own string.
- Never require a destructive or file action: no deleting, overwriting, importing, exporting or saving to advance.
- Bump `TutorialScript.Version` when the tour changes enough that Help should say it was updated. `TutorialSessionTests` walks the real script end to end from every interface state and fails on any authoring mistake `TutorialScriptValidation` catches.

### Adding a tutorial target

1. Add the value to `TutorialTarget` (named by where it lives and what it is).
2. In the window that draws it, call `TutorialAnchorMarks.Mark(TutorialTarget.X)` right after the widget (or `MarkRect` / `MarkWindow` for a region). If it lives in a scrolling region, add `TutorialAnchorMarks.RevealIfWanted(TutorialTarget.X)` too; if it lives in a tab or collapsible section, the owner may select that tab or open that section while `TutorialAnchorMarks.IsWanted(TutorialTarget.X)`.
3. Point a step at it. Run the tests.

### First-run behaviour

Once the Library has loaded, `FirstRunDetector` decides once, without touching a Plate: an install with a configuration file (every v0.1.6 install wrote one on its first load), or with any saved Plate or Template, is an established install and is never offered the tutorial unasked; only an install with neither is new. The kind is stored, so the configuration this build writes on a new player's first load cannot turn them into an "existing" player next time. A new player sees the offer; Maybe Later leaves a quiet reminder in My Plates; Do Not Show Again leaves nothing; an unanswered offer is shown again on a later launch, three times at most. Help in My Plates starts, resumes or jumps into the tutorial at any time. Nothing here needs an account, and nothing is sent anywhere.

## Safe ImGui rendering

- Push in `PreDraw`, pop in `PostDraw`; inside `Draw`, only `ImRaii` scopes.
- `OpenPopup` and `BeginPopup` from the same id-stack scope (the windows' "pending flag, open at window level" pattern).
- No per-frame allocations beyond ImGui's own: cache ids and glyph strings, avoid LINQ and interpolation in `Draw`.
- Sizes in unscaled pixels times the global scale.
- A window that must not be closed by Escape sets `RespectCloseHotkey = false`; one that must make no sound sets `DisableWindowSounds = true`.

## Compromises forced by Dalamud or ImGui

- ImGui windows are rectangles, so the dim is made of strips over the bounding rectangle of AetherFrame's open windows, and the spotlight is a rectangle with rounded chrome, not an arbitrary shape. Another plugin's window that overlaps that rectangle is dimmed and blocked with it.
- The spotlight is cut where the control was drawn, not where it is visible after other windows are drawn on top of it; an AetherFrame window dragged over the highlighted control would receive clicks meant for it.
- The card's height depends on the step's text and is known a frame late, so the frame after a step change can place it a few pixels off before it settles.
- A modal popup blocks every other window, the tutorial card included, so a step whose control is inside a modal (the Create Plate chooser) explains and waits rather than being clicked through.
- ImGui has no letter-spacing, so small-caps section labels use the Axis face rather than tracking.
- Font handles are built asynchronously by Dalamud; until then headings draw in the default font.

## Manual acceptance in FFXIV

See [ManualAcceptance-UI-Onboarding.md](ManualAcceptance-UI-Onboarding.md). Nothing about appearance, input capture, spotlight alignment or scaling is claimed as verified until someone has run those steps in the game.
