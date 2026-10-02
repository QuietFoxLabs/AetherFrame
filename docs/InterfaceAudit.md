# Interface audit: where AetherFrame makes players go back and forth

**Status (2026-09-30): a first audit, written from the code, for the owner to review.** On September 29, 2026 the owner asked for an interface that is more intuitive, with less going back and forth between menus (their words are in ROADMAP.md, section 5). [NETWORK2.md](networking/NETWORK2.md), section 6 makes an audit the first step, and ROADMAP.md, section 8 schedules it.

Section 7 adds a second, deeper pass (September 30, 2026): every journey counted step by step, defects, tasks 7 to 16, and the owner's decision to merge the editors into one window. Apart from that decision, everything below is a proposal, not a decision. Nothing here changes what players see. The audit is posted in the Owner inbox before the first proposal is built, and each proposal that is built gets its own pull request, reviews and in-game checks. Section 4 lists what only an in-game check can confirm.

`[updated 2026-10-02: refreshed against the windows at 9dfc4f7 (0.1.9's plugin code), before interface task 7 and then task 2, the tasks the owner chose on October 2 (ROADMAP.md, section 8, task 5). Both passes stay as written; the notes dated 2026-10-02 say what has changed since, what a later change settled or superseded, and where task 7 and task 2 now start. Task 7 is built by the pull request that adds these notes.]`

## 1. The windows today

| Window | What it holds | How players reach it |
|---|---|---|
| **My Plates** (`PlateLibraryWindow`) | The header: Create Plate, Import, search, Help, whether a character is logged in, and the Plate count. The card grid: click selects, double-click edits, drag reorders. A right-click menu on each card: Preview, Open in Basic Editor, Open in Advanced Editor, Set Active, Duplicate, Save as Template, Export, Rename, Delete. A footer: "Select a Plate. Double-click to edit; drag to reorder." and "Right click a Plate for actions". The persistent action row the menu replaced was removed on purpose. | `/aetherframe` or `/af` and Dalamud's plugin buttons, which toggle it; the editors' My Plates button; the editors' state when no Plate is open; the Plate Viewer's "no Active Plate" state |
| **Template chooser** (a popup in My Plates) | Built-in and saved Templates on the left, a preview on the right, Use Template. A right-click menu on each row: Use Template, and for a saved Template also Rename, Duplicate and Delete Template, whose prompts open over the chooser. Its footer links to Manage Templates. | Create Plate |
| **Manage Templates** (a view inside My Plates) | The Templates card grid with Preview, Use Template, Rename, Duplicate and Delete, and a back arrow to My Plates. By design it is not a permanent tab beside My Plates. | Only the chooser's "Manage Templates..." link |
| **Basic Editor** and **Advanced Editor** | One editing session shared by both: the same document, unsaved state and undo history. The action bar holds My Plates, the Basic/Advanced switch (which hands the Plate over in place), the Plate's name (display only, and hidden when the bar is too narrow), Undo and Redo, the save state, Preview (Clean Preview), Revert, Save and Help. | Double-click a card (the editor is chosen by the Plate's content; the Plate already open stays in the editor showing it), the card menu, or Use Template |
| **Plate Viewer** (`ProfileViewWindow`) | A read-only overlay of one Plate, drawn over the game. | `/aetherframe view` (the character's Active Plate), a card's Preview, or Preview in Manage Templates |
| **Package Import** | Checks an `.aetherframe` file and previews it; it is always added as a new Plate. | My Plates' Import, after the file dialog |
| **Tutorial and Help** | Twelve chapters that spotlight the real controls, and Help menus. | The Help buttons; offered once to new installs |

AetherFrame has no settings window: Dalamud's settings button toggles My Plates.

`[updated 2026-09-30: this table is the audit's snapshot. Since then, task 1 ([#59](https://github.com/QuietFoxLabs/AetherFrame/pull/59)) gave both editors a Plate menu under the Plate's name, and the card menu's Preview became View; section 7.5 records the choices.]`

`[updated 2026-10-02: also since then: the editors' Preview opens the Plate Viewer, the same floating view as View ([#78](https://github.com/QuietFoxLabs/AetherFrame/pull/78)), and Clean Preview, which nothing reached after that, is gone ([#79](https://github.com/QuietFoxLabs/AetherFrame/pull/79)). The tour has 14 chapters in the sharing build, which every release since 0.1.8 is, and 12 in the player build ([#93](https://github.com/QuietFoxLabs/AetherFrame/pull/93)). The sharing build adds My Plates' Sharing button and its window ([#70](https://github.com/QuietFoxLabs/AetherFrame/pull/70)), the AetherFrame Plates search for another player's Plate ([#76](https://github.com/QuietFoxLabs/AetherFrame/pull/76)), and a small window that follows each share ([#95](https://github.com/QuietFoxLabs/AetherFrame/pull/95)). The Personas window was removed in N2-9c ([#71](https://github.com/QuietFoxLabs/AetherFrame/pull/71)).]`

## 2. Tasks and the trips they take

A **trip** is leaving the window where the player is working to do something elsewhere, usually coming back. Each task counts its trips that way: leaving and coming back is one trip.

| # | Task | The path today | Trips |
|---|---|---|---|
| 1 | Create a Plate | My Plates, Create Plate, the chooser, Use Template. The Plate is written before the editor opens, and a character's first Plate becomes its Active Plate right then; the chooser says so. | none |
| 2 | Edit a Plate | My Plates, then double-click its card | none |
| 3 | Make a Plate the character's Active Plate, other than its first | Editor, My Plates, find the card, right-click it, Set Active | **one**, through a hidden menu |
| 4 | Rename the Plate being edited | Editor, My Plates, right-click the card, Rename | **one**, through a hidden menu |
| 5 | Save it as a Template | Editor, My Plates, right-click the card, Save as Template | **one**, through a hidden menu |
| 6 | Export it to a file | Editor, My Plates, right-click the card, Export, the file dialog | **one**, through a hidden menu |
| 7 | Duplicate it to try a variation | Editor, My Plates, right-click the card, Duplicate, double-click the copy | **one**, through a hidden menu |
| 8 | Switch to another Plate | Editor, My Plates, double-click another card; unsaved changes prompt first | one (expected) |
| 9 | Start a new Plate while editing | Editor, My Plates, Create Plate | **one** |
| 10 | See the Plate as others will | The editor's Preview; for the Active Plate outside the editors, `/af view` | none |
| 11 | Rename or delete a saved Template | My Plates, Create Plate, then right-click the Template's row in the chooser; or Manage Templates... from the chooser's footer | none, but hidden |
| 12 | Import a Plate | My Plates, Import, the file dialog, Package Import, the new card | none |
| 13 | Do anything with the selected Plate in My Plates | Only through the right-click menu; the footer says so | none, but hidden |

Tasks 3 to 7 and 9 share a cause: the editors show the Plate's name and nothing else about it, so every action on a Plate as a whole lives only in My Plates. Tasks 11 and 13 have the other cause: their actions exist, but behind right-click menus or, for Templates, a quiet link inside Create Plate.

## 3. Proposals

Each is the smallest change that removes a trip or makes a hidden action findable. They are ordered by the trips they remove, and then by how often the hidden action is needed.

1. **A Plate menu in both editors' action bar** (tasks 3 to 7). It is a control that stays visible at every width, since the Plate's name is hidden on a narrow bar. It uses the card menu's own words:
   - Rename
   - Set Active (disabled with the reason when no character is logged in, or when it already is)
   - Save as Template
   - Export
   - Duplicate

   Delete stays in My Plates, where the whole library is in view.

   **First, one shared component.** Today the prompts, the busy, status and error handling, the unsaved-changes guard and the Export file dialog belong to My Plates alone. Proposal 1 first moves them into one Plate-actions component that My Plates and the editors both use, with tests showing My Plates behaves exactly as before. Results and errors then appear in the editor, beside its save state.

   **Unsaved changes, action by action:**
   - Rename is safe: the library's name wins on the next save.
   - Set Active points the character at the saved Plate.
   - Duplicate, Save as Template and Export use the saved Plate, as they do from My Plates today. From an editor with unsaved changes, the menu says each uses the last saved version, or offers to save first. Today only Save as Template's prompt says so; Duplicate and Export give no warning.
   - Duplicate then opens the copy, so the original's unsaved changes go through today's prompt.

   Which of those choices each action makes is decided in its pull request, and shown to the player, never left implicit.
2. **Open another Plate... and New Plate... in the editors' Plate menu** (tasks 8 and 9). The first lists the Plates in My Plates' own order, since nothing records recent Plates today, and switching goes through today's unsaved-changes prompt. The second opens the template chooser. The chooser is a popup drawn inside My Plates, and Use Template runs through My Plates' operation runner and open guard; the tutorial also reads the chooser's state from My Plates. So New Plate... first moves the chooser and Use Template into the shared component; until then it would only bring My Plates forward with the chooser open, which is still a trip. It is lower priority, since switching Plates is a trip players expect.

   `[updated 2026-10-02: where interface task 2 starts, at 9dfc4f7 with task 7 in. The editors' Plate menu is Windows/EditorPlateMenu.cs (its control and menu, :121-196) over Windows/PlateMenu.cs's DrawEditorItems (:178-223), built once for both editors (Plugin.cs:264-271). Task 1 moved the Plate actions and their prompts into that shared component (PlateMenu, over PlateActions), so what is left to move is the chooser: DrawTemplateChooserPopup (Windows/PlateLibraryWindow.Templates.cs:455-529), and Use Template through My Plates' runner and open guard (UseTemplate, :417-438, then RequestOpen, Windows/PlateLibraryWindow.Actions.cs:89-107). The tutorial reads whether the chooser shows from My Plates (Plugin.cs:836-839), and the steps to rewrite are saving.plate-menu (UI/Tutorial/TutorialScript.cs:185-187), first.template (:76-80) and the Templates chapter (:193-201). Since task 7, a row menu's Use Template asks the chooser to act (chooserUseRequestedId), so the chooser closes as for its button; a chooser moved out of My Plates keeps that.]`
3. **A "..." button on the selected card in My Plates** (task 13) that opens the same card menu. The persistent action row was removed on purpose in favour of the menu, so this finds the menu without bringing the row back. Each card is one invisible button that handles click, double-click, right-click and drag, so the "..." button overlapping it must not select, open or start dragging the card.
4. **Finding Template management** (task 11). The chooser's rows already have a right-click menu for Rename, Duplicate and Delete Template, and Manage Templates has visible buttons for them behind a quiet link. A line in the chooser says so, each saved row gets a "..." button, or the Manage Templates link becomes easier to see. Templates stay out of My Plates' top level, as designed. It removes no trip, only makes the actions findable.
5. **A notice when My Plates holds Plates but the logged-in character has no Active Plate** (optional, for the owner to decide). A character's first Plate is Active automatically, so this helps in four cases: a character with no Plates of its own, such as an alt, while My Plates holds another character's Plates (every new character starts that way, which is also where the notice is most likely to be noise); after the Active Plate is deleted; for Plates made while logged out (which no character is linked to); and after a failed link. The notice goes in My Plates' header, pointing to Set Active; it is never automatic.
6. **Window placement, once checked in game** (task 8): if My Plates opens over the editor and hides it, open it beside the editor, or keep the editor in view. This depends on section 4.

The tutorial moves with the interface. Every control a proposal adds or moves gets a tutorial anchor, and the steps that describe the old path are rewritten in the same pull request: the steps in `TutorialScript.cs` that send players to My Plates or its card menu for these actions (the card menu, rename, the saving step that points to My Plates for Set Active, Duplicate and Export, Save as Template, and Export). `TutorialScript.Version` is bumped when they change, as the design guide says. The tutorial's word "View" for the card menu's "Preview" is fixed at the same time.

## 4. What only an in-game check can confirm

- Open a Plate in an editor, then choose My Plates in the editor's bar. Expected: My Plates opens. Tell whether it sits beside the editor or over it, at your usual window size and UI scale.
- Which of section 2's tasks you do most often. That is not a check, but it decides the order in which proposals 2 to 6 are built.
- For each proposal that is built, its own pull request's In game section.

## 5. How this fits sharing (NETWORK2)

- The editors' Plate menu (proposal 1) and the card menu are where **Share...** and **Unpublish** go when N2-9 adds them. Sharing then needs no trip either, and the consent screen opens from either place.
- A Plate's sharing state (not shared, shared, or changed since sharing) belongs on its card and next to its name in the editor bar, from N2-6 and N2-9.
- The persona window (N2-5) is reached from My Plates' header and from the share flow.

`[updated 2026-10-02: superseded by sharing as built. The sharing re-plan ([#58](https://github.com/QuietFoxLabs/AetherFrame/pull/58)) made sharing follow each character's Active Plate (the owner's V1 to V5), with no share codes (R5), and since [#95](https://github.com/QuietFoxLabs/AetherFrame/pull/95) a Plate is shared as soon as it is Active, so no Plate menu has Share... or Unpublish. Sharing is turned on and off per character in the Sharing window, opened from My Plates' header; a card shows Shared or Not shared yet; and the persona window was removed in N2-9c ([#71](https://github.com/QuietFoxLabs/AetherFrame/pull/71)).]`

## 6. Not proposed

- Merging the Basic and Advanced editors: they already share one session and hand a Plate over in place. `[updated 2026-09-30: the owner has since decided to merge them into one window with two modes; section 7.4, and interface task 8.]`
- Bringing back My Plates' persistent action row, or making Templates a permanent tab: both were removed or avoided on purpose. Proposals 3 and 4 are the smaller alternatives.
- Changing the design system or the look of the windows: this audit is about paths, not appearance.
- Removing the right-click menus: the proposals add visible paths beside them.

## 7. A second pass: journeys, defects and more tasks (September 30, 2026)

A second session audited the same code at `714cf5c` (at `beca12a` the plugin source differs only by a doc comment in Domain/Plates/PlateNaming.cs) while sections 1 to 6 were being written. On September 30, 2026 the owner chose, from the options Claude offered in chat, to fold it into this audit rather than keep two: "Fold mine into #43's". This section adds what the first pass does not have: journeys counted step by step, defects, the owner's decision on the editors, and tasks 7 to 16. Where the two passes differ, section 7.5 lays out both options; the queued tasks keep the first pass's choice until a task's pull request records otherwise.

### 7.1 How it was done

- Five readers mapped My Plates, Templates, both editors, and the shell (the Plate Viewer, Import Plate, commands, Help and the tutorial) from code. A second reader checked every friction item. Three directions were drafted and two judges scored them. Eight more readers checked the draft's 403 claims, and a recount of every journey was checked by another reader. Nothing was built, run or seen in game. Dalamud's focus, Escape and sizing behaviour was read from a decompiled Dalamud 15.0.3.6, outside the repository.
- Cites are `file:line`, relative to `AetherFrame/`, at `beca12a` unless a cite says otherwise. Later merges shift some lines. After its first mention, a file is named by its last part, for example Actions.cs for Windows/PlateLibraryWindow.Actions.cs.
- **What a trip is:**
  - A **window switch** is each time the player's work moves to a different top-level window: My Plates, the Basic editor, the Advanced editor, the Plate Viewer, Import Plate, or a file dialog. Opening, raising or returning to one counts, whether the player does it or the plugin does it for them.
    - A slash command and the window it opens count as one switch.
    - A file dialog counts once. Its closing back to the window it came from is not counted.
    - Basic to Advanced counts 1 today, because it swaps windows. Clean Preview counts 0, because it is the same window. `[updated 2026-10-02: Clean Preview is gone ([#79](https://github.com/QuietFoxLabs/AetherFrame/pull/79)): Preview opens the Plate Viewer, another window.]`
    - The tutorial card and the Welcome offer are never window switches.
  - A **menu** is a context menu, a submenu, a combo used as the route, a color picker, the Create Plate chooser, the title picker, Help or a naming modal (Rename, Save as Template).
  - A **prompt** is a confirmation or a question: Unsaved Changes, Delete, Replace File?, Import as New Plate, the Welcome offer, or a save-first question. A conditional prompt, one that appears only in some cases, is marked in the steps of [InterfaceAudit-Journeys.md](InterfaceAudit-Journeys.md) but not counted.
  - Counts are for the shortest common path, from code. Each journey starts with the game running, AetherFrame loaded and no AetherFrame window open, unless it says otherwise.

This counts differently from section 2, where leaving and coming back is one trip. Here each window the player's work moves to counts once.

### 7.2 Journeys

[InterfaceAudit-Journeys.md](InterfaceAudit-Journeys.md) gives every path step by step, today and after the second pass's tasks, with its cites.

| # | Job | Frequency | Switches | Menus | Prompts | Back and forth |
|---|---|---|---|---|---|---|
| J01 | Learn with the tour | rare | 4 | 1 | 1 | Late chapters send the tour back to My Plates, which from code stays under the editor (needs an in-game check) |
| J02 | First Plate from a Template, with a title, portrait and name | sometimes | 4 | 4 | 0 | Editor, My Plates and its card menu, only to name the Plate |
| J03 | Change the portrait, then rotate or flip it | often | 4 | 0 | 0 | Replace in Basic, rotate or flip only in Advanced |
| J04 | Theme, pattern, background image and transparency | often | 4 | 1 | 0 | Theme and Pattern only in Basic, Opacity only in Advanced |
| J05 | Add, place and recolor a frame | sometimes | 3 | 2 | 0 | Placement and color only in Advanced |
| J06 | Move and recolor a Basic caption | sometimes | 3 | 1 | 0 | The caption has to be found again by hand in Advanced |
| J07 | Switch Basic to Advanced mid-edit, and back | often | 2 | 0 | 0 | Every switch swaps windows and drops the element or category |
| J08 | Free text with font and color, and an image, in Advanced | sometimes | 3 | 2 | 0 | None, apart from the file dialog |
| J09 | Preview, then show the open Plate over the game | every session | 2 | 1 | 0 | Only a card's Preview shows the open Plate's live copy over the game |
| J10 | Make the open Plate Active, check it | sometimes | 2 | 1 | 0 | Set Active is only in the card menu |
| J11 | No Active Plate: choose one and see it | rare | 3 | 1 | 0 | The viewer closes itself on the way to My Plates |
| J12 | Save as Template, start a new Plate from it | sometimes | 2 | 3 | 0 | Save as Template and Create Plate are only in My Plates |
| J13 | Update a Template, then delete the working Plate | rare | 3 | 5 | 2 | No way to replace one: chooser, editor, My Plates, chooser |
| J14 | Export the open Plate | sometimes | 2 | 1 | 0 | Export is only in the card menu |
| J15 | Import a file and open it | sometimes | 5 | 0 | 1 | Import Plate previews the Plate but can't open it |
| J16 | Find and reopen a Plate | every session | 2 | 0 | 0 | Always through My Plates, and /af may close it first |
| J17 | Try a variation of the open Plate | sometimes | 2 | 1 | 0 | Two card menu trips, and unsaved edits can't be forked |
| J18 | Delete the open Plate | rare | 1 | 1 | 1 | Only My Plates can delete it, and the editor is left empty |
| J19 | Get help from the viewer or Import Plate | rare | 1 | 1 | 0 | No Help in the viewer or in Import Plate |
| | **Total** | | **52** | **26** | **5** | |

Most window switches come from three sources:
- **Plate actions live only in My Plates:** J02, J10, J12 to J14, J17 and J18.
- **Basic and Advanced are two windows:** J01 and J03 to J07.
- **The Plate Viewer is a dead end, and only /af view shows whichever Plate is Active, as saved:** J09 to J11 and J19.

The rest are the trip in through /af and My Plates, which every journey that starts with nothing open pays, and file dialogs.

### 7.3 Findings

The findings are ranked by impact. Findings 1, 3, 4 and 10 give the evidence for section 2's two causes, finding 2 is the question section 7.4 settles, and finding 13's Preview and View wording is also in section 3. The rest are new.

1. **Plate actions are missing from the editors (high).** Rename, Set Active, viewing a chosen Plate, Duplicate, Save as Template, Export and Delete are only in the card menu (PlateLibraryWindow.Actions.cs:103-182). /af view shows only the Active Plate. The editor never shows whether the Plate is Active (EditorActionBar.cs:82-201). The tutorial teaches the detour (UI/Tutorial/TutorialScript.cs:70, 176-178).
2. **Basic and Advanced are two windows, and each lacks controls the other has (high).**
   - The switch closes one window and opens the other (UI/Editor/EditorSurfaceCoordinator.cs:75-97). The document, unsaved edits and undo history come along, but the other mode doesn't open on the element or category the player was on.
   - Advanced resets zoom and pan every time the player returns to it (Windows/ProfileEditorWindow.cs:144-152).
   - Theme, Pattern and the title picker exist only in Basic (Windows/BasicProfileEditorWindow.Design.cs:27-40; Windows/BasicProfileEditorWindow.Identity.cs:143-150).
   - Advanced's presets add a pattern, although they are labelled "Background colors only", and Advanced can't remove it (Domain/Profiles/ProfileThemePresets.cs:85; Windows/BackgroundStylePanel.cs:150-152, 499).
   - Opacity is kept out of Basic on purpose, by a choice recorded in the code (BackgroundStylePanel.cs:107-111).
3. **My Plates and the editor overlap, and My Plates barely tracks the open Plate (high).** No card marks the Plate that is open (PlateLibraryWindow.cs:433-441). Only a footer line does, while that card is selected and no other message fills the footer, and the line stays after the editor closes (Actions.cs:66-84). /af can close My Plates while the editor covers it (Plugin.cs:483).
4. **Actions that use the saved version don't ask first (medium).** Duplicate, Export and Save as Template read the saved Plate (Services/Plates/PlateLibraryService.cs:822-824; Services/Packages/PlatePackageService.cs:76; Services/Templates/TemplateLibraryService.cs:402). Only Save as Template's prompt says it uses the last saved state, and only Delete's prompt checks for unsaved edits (Templates.cs:832; Actions.cs:532-537). Nothing lets the player fork unsaved edits into a new Plate (J17).
5. **The Plate Viewer can't act on what it shows (medium).** Its menu holds only size presets, Reset Size and Center on Screen (ProfileViewWindow.cs:302-342). In the No Active Plate state it closes itself on the way to My Plates, although it re-resolves the Active Plate every frame (ProfileViewWindow.cs:229-234; UI/Rendering/PlateViewerTarget.cs:44-48). A card's Preview doesn't raise a viewer that is already open (ProfileViewWindow.cs:103-118).
6. **Create Plate asks too little, and asks too late (medium).**
   - The chooser has no name field (Templates.cs:729-774).
   - The Plate is saved before the Unsaved Changes question, so Cancel leaves a stray Plate behind (Templates.cs:422-438).
   - From code, Use Template in a row's right-click menu leaves the modal open over the new editor (Templates.cs:597-608, 632-636). This needs an in-game check. `[updated 2026-10-02: task 7 has the chooser act on the row menu's Use Template in its own scope, so the chooser closes as for its button.]`
   - With an editor already open, the tour passes over "Choose a Template" (UI/Tutorial/TutorialSession.cs:367-369).
7. **Escape on a popup probably closes the window behind it too (medium; needs an in-game check).** In the decompiled Dalamud, a popup counts as focus on its owner window. Escape closes a focused window whose RespectCloseHotkey is on. For an editor with unsaved edits, the close guard turns that into the Save, Discard or Cancel question (UI/Editor/CloseGuard.cs:66-79). Only Clean Preview and a running import turn that setting off (CleanPreviewPresenter.cs:116; PackageImportWindow.cs:95, 103). The tutorial's overlay and shades keep it off, but they hold no popups (Windows/Tutorial/TutorialOverlayWindow.cs:53; Windows/Tutorial/TutorialShadeWindow.cs:43). `[updated 2026-10-02: confirmed from Dalamud 15.0.3.6's WindowHost.DrawInternal, which closes a focused window on the game's Escape key once per press, through a latch every window shares that is set only when a window closes. It also showed that ImGui closes no popup on Escape here, since Dalamud leaves ImGui's keyboard navigation off, so the popup went with its window. Since [#79](https://github.com/QuietFoxLabs/AetherFrame/pull/79) both editors keep RespectCloseHotkey on, and Clean Preview is gone. Task 7 fixes it.]`
8. **Basic's Preview may leave an invisible area that catches clicks (medium; needs an in-game check).** Dalamud re-applies Basic's `Window.Size` after PreDraw, so it overrides Clean Preview's size. That size is also not clamped to the screen (BasicProfileEditorWindow.cs:127-128; Windows/EditorWidgets.cs:43-51). `[updated 2026-10-02: moot: Clean Preview is gone ([#79](https://github.com/QuietFoxLabs/AetherFrame/pull/79)), and Preview opens the Plate Viewer. Basic's first size was still Window.Size, which Dalamud scales and doesn't keep on the screen; task 7 moves it to EditorWidgets.SetFirstUseSize.]`
9. **Late in the tour, the spotlight probably points at My Plates while it is under the editor (medium; needs an in-game check).** While a step shows, My Plates, the editors, the Plate Viewer and Import Plate can't be raised (Windows/Theme/AetherStyle.cs:316-326). Chapters 10 and 11 require only that My Plates is open (Plugin.cs:533; TutorialScript.cs:181-199). No step shows the player how to view a Plate or set it Active. The tour only lists them among the card menu's actions (TutorialScript.cs:50, 177). If the player declines the Welcome offer, My Plates doesn't open, and the offer never names /af. Only Help, inside My Plates and the editors, and Dalamud's command help name it (Windows/Tutorial/FirstRunPromptWindow.cs:80-111; UI/Tutorial/OnboardingCoordinator.cs:107-125; Windows/Tutorial/HelpMenu.cs:203-206). `[updated 2026-10-02: since [#57](https://github.com/QuietFoxLabs/AetherFrame/pull/57), the window a step explains is brought in front of AetherFrame's other windows, with the dim and the card in front of it (task 14, in part).]`
10. **Templates are managed in two places that work differently (medium).** Chooser rows use a right-click menu, as Plate cards do (Templates.cs:590-609; Actions.cs:103-182). Manage Templates' cards look like Plate cards but have no right-click menu. They use a button bar instead (Templates.cs:179-233, 330-391). Manage Templates' cards show only a color or an icon. No thumbnail generator exists (Plugin.cs:163, 171), and unlike Plate cards, they don't fall back to drawing the Plate (Templates.cs:238-296; PlateLibraryWindow.cs:487-518).
11. **Import stops one step short (low).** There is no Open button. After a rejection, the window can't pick another file itself, so the player goes back to Import in My Plates (PackageImportWindow.cs:262-287; Windows/PlateLibraryWindow.Packages.cs:35-42).
12. **Some controls sit away from where the player is working (low).** A new category keeps the previous category's scroll position (BasicProfileEditorWindow.cs:576-582). Orientation is set in Style, not Portrait (Design.cs:115-126). Any click on an element pulls the Element tab forward (Windows/ProfileEditorWindow.Canvas.cs:295-302).
13. **Words and style drift (low).**
    - "Preview" names two different features, and the tutorial calls one of them "View" (Actions.cs:110; TutorialScript.cs:50).
    - Footer messages stay after they stop applying (Actions.cs:62-91).
    - Help lists Advanced-only keys in Basic (Windows/Tutorial/HelpMenu.cs:190-200).
    - The chooser's buttons and several prompts don't use the #33 controls (Templates.cs:757-773, 851-987; Windows/PlateLibraryWindow.Packages.cs:120-135).

**Outside this direction.** Each of these is a task of its own:
- The gradient's To color and the pattern color are one value, `SecondaryColor` (BackgroundStylePanel.cs:452, 632; UI/Rendering/ProfileBackgroundRenderer.cs:75, 105). The fix changes the document (medium).
- A Template can't be updated in place: nothing overwrites a Template's content, so J13 goes through a new Template.
- A deleted Plate can't be restored. The Delete prompt says it moves to a trash folder, but no control restores it.
- Canvas work in Advanced: one element selected at a time; panning only by a middle-button drag; new text and images always placed at (40, 40); images only through Dalamud's file picker; different conventions for fit, rotation and size; Undo that doesn't say what it undoes; a choice popup on every canvas resize; side panels that can't be collapsed.

### 7.4 One editor window: the owner's decision

Section 6 did not propose merging the editors. The second pass recommended it, and Claude put the choice to the owner in chat on September 30, 2026. The owner chose "Merge into one window", whose stated terms were:

> One AetherFrame Editor with Basic and Advanced as modes. Switching keeps the window's place, zoom and the element being edited. Largest cut in back-and-forth, one large PR, and tutorial chapters 4 to 9 get rechecked in game.

It is recorded in ROADMAP.md, section 5, as the owner's own decision, and it is interface task 8. Basic mode still feels like FFXIV and Advanced still removes the restrictions; both keep editing one saved Plate through the shared renderer.

### 7.5 Where the two passes differ

Each difference is chosen, and recorded, in the pull request of the task named. Until then the queued task keeps the first pass's choice.

| Question | First pass (queued) | Second pass | Task |
|---|---|---|---|
| Delete in the editor's Plate menu | No: Delete stays in My Plates, where the whole library is in view. | Yes: deleting the open Plate then closes the editor and shows My Plates. Today it leaves the editor empty (J18). | 1 |
| Trying a variation | Duplicate, from the saved Plate, then open the copy; the original's unsaved changes go through today's prompt. | Save a Copy of the live document; the editor continues on the copy, and the original stays as last saved. | 1 |
| Seeing the open Plate over the game | Not in the Plate menu. | A View item that raises the viewer with the live copy (removes J09's switch). | 1 |
| Set Active with unsaved changes | Sets the saved Plate Active. | Save and Set Active, which activates only after a successful save. | 1 |
| Template management | Made findable: a line, a "..." button, or a clearer link to Manage Templates. Templates stay out of My Plates' top level. | Manage Templates retired; the chooser's rows already rename, duplicate and delete. | 4 or 10 |
| The word for showing a Plate over the game | Preview, the card menu's word; the tutorial's "View" becomes "Preview". | View, with Preview kept for Clean Preview inside the editor, so one word never names two features. | 1 |
| A character's first Plate | Active automatically. The chooser says so beforehand (Windows/PlateLibraryWindow.Templates.cs:733), and My Plates' footer after (Templates.cs:431-434). | A checkbox in the chooser, checked by default, so the player can also say no, since ROADMAP.md, section 5, says activation is explicit. | 10 |

**Chosen in task 1** ([#59](https://github.com/QuietFoxLabs/AetherFrame/pull/59), September 30, 2026; APPROVED (Claude, under the owner's delegation of September 29, 2026), with the rationale in ROADMAP.md, section 5):
- Delete in the editor's Plate menu: the first pass. Delete stays on My Plates' cards.
- Trying a variation: the second pass, named **Save as New Plate** for what it makes. The editor continues on the new Plate, and the original keeps its last saved version. Cards keep Duplicate.
- Seeing the open Plate over the game: the second pass. The editors' Plate menu has **View**, which raises the Plate Viewer with the live document.
- Set Active with unsaved changes: the first pass. While there are unsaved changes, the menu says that Set Active, Save as Template and Export use the last saved version.
- The word for showing a Plate over the game: the second pass. **View** in both menus and in Manage Templates; Preview is only the editors' own. The tutorial already said View.

### 7.6 The second pass's tasks

The second pass's direction is "the Plate in hand": wherever a Plate is (in the editor, on a card, in the viewer), one Plate menu offers every action on it, with the same words in the same order. Its target structure, which tasks 1 and 2 build towards:

- **Windows:** My Plates, one AetherFrame Editor with Basic and Advanced as modes, the Plate Viewer, and Import Plate. The tutorial windows and file dialogs stay. Preview builds add the persona window, which N2-5c built, and one Sharing window. `[updated 2026-10-02: the persona window was removed in N2-9c ([#71](https://github.com/QuietFoxLabs/AetherFrame/pull/71)). Every release since 0.1.8 is the sharing build, which adds the Sharing window, the AetherFrame Plates search and the small window that follows each share.]`
- **Plate menu:** one component, drawn by the editor's name button, by a card's right-click and by the viewer's right-click. Its items, in order:
  - Open in Basic or Advanced (cards only);
  - View;
  - Set Active (checked when the Plate is Active, and disabled with its reason when unavailable);
  - Rename (inline);
  - Save a Copy (editor) or Duplicate (card);
  - Save as Template;
  - Export;
  - an extension slot;
  - Switch to (editor only; proposal 2's Open another Plate...);
  - New Plate;
  - Delete.

  Plate names are drawn as text, never used as labels or IDs.
- **Editor bar:** My Plates; Basic | Advanced, switched in place; the Plate's name, which opens the Plate menu and stays drawn at every width (as proposal 1 requires); a gold Active pill; then today's controls.
- **My Plates:** an Editing pill on the open Plate's card, a Continue Editing button, and the chooser. The chooser stays a modal and gains a name field. Manage Templates folds into the chooser.
- **Viewer:** the Plate menu above its size items. In the viewer, its first item is Edit, and it adds Show in My Plates. The No Active Plate state lets the player choose a Plate in place.
- **Import Plate:** Choose File, then Open in Editor.
- **Plumbing:** one Library operation runner, pumped from Plugin.DrawUi (Plugin.cs:367-378), and one prompt host that draws each prompt where the player asked.
- **Words:** Preview happens inside the editor. View shows the Plate over the game. `[updated 2026-10-02: since [#78](https://github.com/QuietFoxLabs/AetherFrame/pull/78), the editors' Preview opens the Plate Viewer too, with the open Plate as it is being edited.]`

Its twelve tasks were named UI-1 to UI-12, and the journeys file uses those names. Here is where each goes in the queue:

| Second pass | Queue |
|---|---|
| UI-2 the Plate menu, UI-3 save first, copies and one prompt host | Tasks 1 and 2 (proposals 1 and 2), with the choices of section 7.5 |
| UI-1 stop the stray trips | Task 7 |
| UI-4 one editor window | Task 8 (the owner's decision, section 7.4) |
| UI-5 take me there | Task 9 |
| UI-6 Create Plate that asks the right things | Task 10, beside proposals 2 and 4 |
| UI-7 a Plate Viewer that acts | Task 11, beside proposal 5 |
| UI-8 My Plates knows where you are | Task 12, beside proposal 6 |
| UI-9 Import that ends where you want | Task 13 |
| UI-10 the tour catches up | Task 14 |
| UI-11 one look and a keyboard path | Task 15, beside proposal 3 |
| UI-12 sharing seams | Task 16, before N2-9 |

`[updated 2026-10-02: task 16 is superseded (below), and task 14 is done in part by [#57](https://github.com/QuietFoxLabs/AetherFrame/pull/57).]`

**Counts.** Today the journeys take 52 window switches, 26 menus and 5 prompts. After UI-1 to UI-12, they take 33, 26 and 5: 19 switches fewer, about a third. The Plate menu replaces trips to My Plates with a menu where the player already is, so menus stay at 26. By the same count, tasks 1 and 2 remove the switches UI-2, UI-3 and UI-6's New Plate remove, except J09's, which needs the View item of section 7.5.

The tasks below follow the queue's tasks 1 to 6. Each is one pull request and meets the Done list in [CLAUDE.md](../CLAUDE.md). A task that changes step text bumps `TutorialScript.Version` (UI/Tutorial/TutorialScript.cs:19) and keeps the tutorial at 12 chapters of 1 to 7 steps (AetherFrame.Tests/TutorialSessionTests.cs:375-382, from the repository root). A bare `:line` refers to UI/Tutorial/TutorialScript.cs.

`[updated 2026-10-02: the script has grown since beca12a (the Plate menu's step and the sharing chapters, among others), so the bare :line references in tasks 8 to 15 have moved. At 9dfc4f7, by step: library.home :49-51 (was :44), library.grid :55-57 (was :50), library.card :58-60 (was :53), library.search :61-63 (was :55-56), first.template :76-80 (was :69-72), images.background :156-158 (was :147-149), saving.library :188-190 (was :176-178), templates.own :198-200 (was :187), sharing.import :208-210 (was :196-198) and done.finish :260-262 (was :204). Version is at :23, now 3. The tour has 14 chapters of 1 to 7 steps in the sharing build, which every release since 0.1.8 is, and 12 in the player build (AetherFrame.Tests/TutorialSessionTests.cs:395-410).]`

**Interface task 7 (UI-1): Stop the stray trips (S).**
- *Changes:*
  - Basic uses EditorWidgets.SetFirstUseSize instead of `Window.Size`.
  - Each window turns RespectCloseHotkey off for the frame after one in which its popup was open. This is combined with Clean Preview's and Import's own rules. `[updated 2026-10-02: Clean Preview is gone, and Import, which turns the hotkey off while it imports, opens no popup, so neither needs combining.]`
  - The chooser row menu's Use Template sets a flag, and the chooser acts on it in its own scope (Templates.cs:632-636, 669-673). `[updated 2026-10-02: at 9dfc4f7, Templates.cs:626-629 and 663-666.]`
  - Help says what Esc does.
- *Improves:* 0 counted. From code, it removes Basic's click-catching area, the 2-switch recovery after Esc on a menu, and a modal left open over the editor. All three need an in-game check. `[updated 2026-10-02: the click-catching area is moot, since Clean Preview went ([#79](https://github.com/QuietFoxLabs/AetherFrame/pull/79)).]`
- *Risk:* none to saved work.
- *Tutorial:* none.
- *Acceptance:* a unit test of the close rule.
- *In game:*
  - In Basic's Preview, clicks to the right of and below the Plate reach the game. `[updated 2026-10-02: dropped: moot since Clean Preview went.]`
  - At 150% scale on a 1080p screen, the first Basic window fits.
  - Esc on a card menu, Help or the chooser closes only that.
  - A row menu's Use Template closes the chooser.
  - Note what Esc does while typing in Message.

`[updated 2026-10-02]` Task 7 is built by the pull request that adds this note. As built, where it differs from the plan above:
- The rule (UI/Editor/PopupEscape.cs, tested in AetherFrame.Tests/PopupEscapeTests.cs against Dalamud's own check, reproduced) holds RespectCloseHotkey off from the frame Escape goes down while one of the window's popups has focus, or had it the frame before, until Escape is up again, not for one frame. Dalamud's latch is set only when a window closes, so a window that respected the hotkey again while Escape was still held would close then.
- ImGui closes no popup on Escape here, so the rule closes it too (Windows/PopupEscapeGuard.cs): a menu, list or color picker as a click outside it would, and a prompt as its own Cancel, the chooser's included. The Basic Editor suggestion, which has no Cancel, takes Escape as its close button. One press answers one popup, so Escape on a row menu leaves the chooser open.
- Every AetherFrame window with popups has the rule: My Plates, both editors, the Plate Viewer, the AetherFrame Plates search and the tutorial card.
- The row menus ask with chooserUseRequestedId (Windows/PlateLibraryWindow.Templates.cs:675-689), and the chooser takes the request after its footer (:511-518). A row menu's Use Template is greyed out while an action runs, as the chooser's button is.
- Help's Esc line reads "Close the open menu, or cancel the open prompt. With neither open, close the window; an editor with unsaved changes asks first."

**Interface task 8 (UI-4): One editor window (L).**
- *Changes:*
  - One window, `AetherFrame Editor###AetherFrameEditor`, hosts both editor bodies as modes.
  - Two IEditorSurface adapters keep ActiveSurface (UI/Editor/EditorSurfaceCoordinator.cs:25-38, 62-65) and the 17 EditorModeSwitch references in TutorialScript.cs meaningful.
  - Each mode keeps its own minimum size, and Basic returns to its own width after Advanced.
  - Zoom and pan reset only when a different Plate opens.
  - Selection and category carry across a mode switch.
  - One close guard, one preview presenter, one shortcut owner and one file dialog manager (Plugin.cs:159-160). `[updated 2026-10-02: Clean Preview's presenter is gone ([#79](https://github.com/QuietFoxLabs/AetherFrame/pull/79)): both editors' Preview opens the Plate Viewer.]`
- *Improves:* J01 and J03 to J06 by 1 each, and J07 2 to 0: 7 switches.
- *Risk:* medium, with no change to data. One session already holds the document and its history (EditorSurfaceCoordinator.cs:42-49). The editor's saved window placement resets once.
- *Tutorial:* the dim list names one editor (Plugin.cs:231-232). The open actions set the mode (Plugin.cs:550-555). Re-run steps 11 to 25 and 28 to 30 of [ManualAcceptance-UI-Onboarding.md](ManualAcceptance-UI-Onboarding.md).
- *Acceptance:* tests that:
  - a mode switch commits pending edits and keeps the history;
  - selections map to the right category;
  - Basic still ignores Advanced-only keys.
- *In game:*
  - After five mode switches, the window hasn't moved, and undo and zoom still work.
  - A narrow Basic window returns to its own width after Advanced.
  - With the title selected in Advanced, switching opens Basic on Identity.
  - Chapters 4 to 9 still point at the right controls.

**Interface task 9 (UI-5): Take me there (M).**
- *Changes:*
  - Hints that point from one mode to the other become links. Each link switches mode and hands over a one-shot reveal, reusing the tutorial's reveal code (Windows/ProfileEditorWindow.Inspector.cs:67-69, 99-107).
  - The links go to Components, the portrait, "Customized in Advanced Editor", "Basic editor: Title" and Opacity. The Components section starts marking AdvancedInspectorComponents, which is declared but marked nowhere today (UI/Tutorial/TutorialTarget.cs:67). Opacity is a new link, because no hint names it today.
  - Advanced gets the Pattern picker, including None, and the preset label is corrected. This part can ship earlier on its own.
  - Basic shows Orientation in Portrait, and the title symbols under Title.
  - The Element tab comes forward only when a new element is selected.
- *Improves:* 0 counted after UI-4. It removes the search after each switch in J03 and J05 to J07.
- *Risk:* low. Basic gains no placement and no Opacity.
- *Tutorial:* text at :147-149.
- *Acceptance:* a reveal is used once, and is ignored when its element is gone.
- *In game:*
  - Basic's frame link opens that Component's row in Advanced.
  - Remove a preset's pattern in Advanced.
  - Set Mirrored from Portrait.

**Interface task 10 (UI-6): Create Plate that asks the right things (M).**
- *Changes:*
  - The chooser stays a modal and is drawn by task 1's shared component, so New Plate (proposal 2) opens it over the editor.
  - A Name field. CreatePlateFromTemplateAsync already takes a name (PlateLibraryService.cs:702-712). InstantiateAsync, its only caller, passes the Template's name today, so it gains a name parameter (Services/Templates/TemplateLibraryService.cs:579-605). A name already in use still gets a number (PlateLibraryService.cs:712).
  - The Unsaved Changes question comes before the Plate is created.
  - Use Template is disabled for a Template that isn't Ready, which the service already refuses after the click (Services/Templates/TemplateLibraryService.cs:599). Rows mark broken Templates.
  - A search field, and the chooser opens on the Template saved most recently.
  - Blank Canvas's one-time Basic suggestion shows before the Plate is created.
  - A character's first Plate: automatic activation as today, or a checkbox "Make it <character>'s Active Plate", checked by default (PlateLibraryService.cs:757-762). Section 7.5 lays out both.
  - Manage Templates: made findable (proposal 4) or retired into the chooser, whose rows already rename, duplicate and delete Templates (Templates.cs:630-662). Section 7.5 lays out both.
- *Improves:* 0 counted: J12's 2 switches go with task 2's New Plate. J13 stays at 3. Cancelling the Unsaved Changes question after Use Template no longer leaves a stray Plate.
- *Risk:* low to medium. The activation flag changes a service default.
- *Tutorial:* first.template (:69-72) advances on a TemplateUsed latch. Use Template sets the latch, and opening the chooser clears it, so Cancel keeps the player on the step. Text at :187 drops Manage Templates, along with its unused targets and condition.
- *Decisions to record:* the two choices of section 7.5 this task makes, first Plate activation and Manage Templates.
- *In game:*
  - Choose New Plate from the name and type a name: the Plate opens under that name.
  - With unsaved edits, Cancel leaves no new card.
  - If the checkbox is chosen: a first Plate created with the box unchecked isn't Active.
  - Starting chapter 3 from an editor still shows "Choose a Template".

**Interface task 11 (UI-7): A Plate Viewer that acts (M; needs interface task 1).**
- *Changes:*
  - The viewer's right-click starts with the Plate menu (section 7.6), with Edit and Show in My Plates in place of View, then the sizes, "How to move and resize" and Help.
  - The No Active Plate state lists Plates with a Set Active button for each, and stays open.
  - The viewer's placement is saved in the configuration.
- *Improves:* J11 3 to 1 and J19 1 to 0: 3 switches.
- *Risk:* low. Set Active stays one explicit click.
- *Tutorial:* a new step, saving.view.
- *Acceptance:* after Set Active, the viewer shows the new Plate without reopening.
- *In game:*
  - With no Active Plate, /af view, then Set Active: the Plate shows in place.
  - Edit asks, in the editor, about another Plate with unsaved edits.
  - After the game restarts, the viewer reopens where it was left.

**Interface task 12 (UI-8): My Plates knows where you are (M).**
- *Changes:*
  - An Editing pill on the open Plate's card (OpenPlateId), shown only while an editor is open (ActiveSurface).
  - A Continue Editing button.
  - The editor's My Plates button scrolls to the open Plate's card, and new cards scroll into view.
  - /af closes My Plates only if it was the last AetherFrame window focused. Otherwise it raises it.
  - A collapsed window expands when summoned.
- *Improves:* 0 counted. It removes J16's second /af and the search for the open card.
- *Risk:* none to data.
- *Tutorial:* text at :44 and :53.
- *Decision to record:* the /af rule.
- *Acceptance:* tests of the /af rule and the Editing rule.
- *In game:*
  - With the editor in front, /af raises My Plates, and a second /af closes it.
  - The Editing pill clears when the editor closes.
  - A minimized My Plates expands.

**Interface task 13 (UI-9): Import that ends where you want (S; needs interface task 1).**
- *Changes:* Choose File in the idle and rejected states. After a successful import, the window says "It isn't Active" and offers Open in Editor, View and Done (PackageImportWindow.cs:156-160, 262-324).
- *Improves:* J15 5 to 4.
- *Risk:* low. Import still only adds a Plate (Services/Packages/PlatePackageService.cs:105-107).
- *Tutorial:* text at :196-198.
- *In game:*
  - Open in Editor opens the new Plate, and it isn't Active.
  - After a bad file, another file can be chosen in the same window.

**Interface task 14 (UI-10): The tour catches up (M).** `[updated 2026-10-02: done in part by [#57](https://github.com/QuietFoxLabs/AetherFrame/pull/57): for every step, the window it explains is brought in front of AetherFrame's other windows, then the dim and the card, which is what BringForward was for. The Welcome offer's answers, Help following the editor mode and appearing in Import Plate, and the card's two exits are still to do.]`
- *Changes:*
  - A BringForward step action raises the target window, then the shades and the card. It is used for chapters 10 and 11 and for Finish.
  - Every answer to the Welcome offer leaves My Plates open, and the offer names /af.
  - Help follows the editor mode and appears in Import Plate.
  - The card's two exits read Pause tour and End tour.
- *Improves:* 0 counted. J01's switch back to My Plates now works as intended.
- *Risk:* none to data.
- *Tutorial:* TutorialScriptValidation checks BringForward. Text at :204.
- *Acceptance:* a test that a raise keeps the dim and the card on top.
- *In game:*
  - In chapter 10, My Plates comes forward under the spotlight.
  - Maybe Later opens My Plates.
  - Help in Basic lists only Basic's keys.

**Interface task 15 (UI-11): One look and a keyboard path (M).**
- *Changes:*
  - Status lines clear when the view changes, and name what changed.
  - The remaining prompts use AetherControls.
  - Cards take the arrow keys, Enter and the Menu key, through a selection model that doesn't depend on Dalamud.
  - Search gets a clear button.
  - The README, screenshots and [ManualAcceptance-UI-Onboarding.md](ManualAcceptance-UI-Onboarding.md) are refreshed.
- *Improves:* 0 counted.
- *Risk:* low.
- *Tutorial:* text at :55-56.
- *In game:*
  - Open a Plate using only the keyboard.
  - No stale "Deleted" line remains.
  - Prompts match at 100%, 150% and 200% scale.

**Interface task 16 (UI-12): Sharing seams, preview builds only (S; best after interface task 1).** `[updated 2026-10-02: superseded. Sharing shipped through N2-9 and N2-10 with seams of its own; there are no share codes and no per-Plate Share or Unpublish; and since 0.1.8 the released build is the sharing build (ROADMAP.md, section 8).]`
- *Changes:* hooks that do nothing by default, for Share, Open Code, Sharing, a Delete note, a viewer source and the dim list. Only the preview composition root fills them (AetherFrame/AetherFrame.csproj:46-64, from the repository root).
- *When:* before N2-9, which adds Share. If it lands before task 1, the Share hook sits in today's card menu.
- *Risk:* network types could leak into player builds. A test asserts that the player DLL has no Sharing type.
- *Tutorial:* none.
- *In game:* a player build has no Share item. Share follows Export in a preview build, checked once N2-11's tester kit exists (UNRESOLVED).

### 7.7 Sharing, in more detail

`[updated 2026-10-02: superseded. The sharing re-plan ([#58](https://github.com/QuietFoxLabs/AetherFrame/pull/58), the owner's V1 to V5, and R5's no share codes) replaced share codes, the This Plate page, My Shares and Open Code with sharing each character's Active Plate, which other players who share view from the game's right-click menu or a name search ([#76](https://github.com/QuietFoxLabs/AetherFrame/pull/76)). Since [#95](https://github.com/QuietFoxLabs/AetherFrame/pull/95), a Plate is shared as soon as it is Active, with no screen before it. The persona window was removed in N2-9c ([#71](https://github.com/QuietFoxLabs/AetherFrame/pull/71)). The decision register ([DecisionRegister.md](networking/DecisionRegister.md)) holds the rules as built.]`

This adds to section 5. Section 5's Share... and Unpublish in the Plate and card menus open the This Plate page below. The sharing screens compile only in preview builds (AetherFrame.csproj:61-64, from the repository root). They reach the shared windows only through hooks that the preview composition root fills: the persona window already does, through My Plates' `OpenPersonas` (Windows/PlateLibraryWindow.cs:144, 290-297, at `5d6e3e2`), and the others would use task 16's. No tutorial step anchors to them, and every local feature works without an account or a network. N2-5 has built the persona window; the rest is a proposal for N2-9 and N2-10 to weigh.

| Planned screen | Home in the target structure |
|---|---|
| N2-5 persona window | Built by N2-5c ([#46](https://github.com/QuietFoxLabs/AetherFrame/pull/46), merged as `04e3976`), in the preview flavour only: a Personas button in My Plates opens it. The Sharing window below links to it rather than holding a persona page of its own. |
| K4 acknowledgement | The persona window already asks for it whenever a persona is made or restored (N2-5c). Share checks it is recorded before a persona's first publish to a real server (K4). |
| N2-9 consent and publish | The This Plate page, reached from Share in the Plate menu. It starts with the save-first question of task 1, because the builder reads only the saved Plate (NETWORK2.md, section 5). A name refused under D4 is fixed with the Plate menu's Rename. The share code comes with a Copy button. It is a page, not a modal, so it never blocks the tutorial card. |
| Update and unpublish | The same page, which shows the consent content again each time. Unpublish is also in My Shares, the P1 index, which includes Plates that no longer exist. The Delete prompt says that deleting a Plate doesn't unpublish it. Its wording follows D1 (DecisionRegister.md). |
| N2-10 viewer | Open Code in My Plates' header, kept apart from Import. The Plate Viewer gains a read-only source that draws the served profile's paint list (specification, section 8.5; D6), which is task 16's "viewer source" hook. Nothing becomes a local Plate and nothing is saved: the paint list shares nothing with the local Plate model. A strip holds Refresh, notes, "no longer shared" and the outdated-client message. There is no author line. Stage 2's "save a copy" is left to stage 2 (NETWORK2.md, section 5). |

These rules hold throughout ([DecisionRegister.md](networking/DecisionRegister.md)):
- Remote text is never an ImGui label, window title or ID, and is drawn only by calls that don't format it (N7).
- A Shared pill reads the publication index, never the Plate (P1).
- Refresh is a button (R2).
- When K3 turns persona features off, the Sharing window shows one message that names what is missing. Open Code still works when the probe's verification step passes (K3).

### 7.8 What this section does not decide

- The choices of section 7.5, each made in its task's pull request; the /af rule (task 12), recorded in ROADMAP.md, section 5, when that task merges. The owner can overrule any of them.
- Whether My Plates ever docks into the editor, or the chooser becomes a panel.
- Replacing a Template, restoring a Plate, the shared gradient and pattern color, and the canvas work.
- Autosave and history, which ROADMAP.md keeps OPEN.
- Networking decisions, which stay with the register.
- How anything looks. Every count here is from code. The In game lists are where the owner confirms or corrects them.
