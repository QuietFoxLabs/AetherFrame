# Interface audit: where AetherFrame makes players go back and forth

**Status (2026-09-30): a first audit, written from the code, for the owner to review.** On September 29, 2026 the owner asked for an interface that is more intuitive, with less going back and forth between menus (their words are in ROADMAP.md, section 5). [NETWORK2.md](networking/NETWORK2.md), section 6 makes an audit the first step, and ROADMAP.md, section 8 schedules it.

Everything below is a proposal, not a decision. Nothing here changes what players see. The audit is posted in the Owner inbox before the first proposal is built, and each proposal that is built gets its own pull request, reviews and in-game checks. Section 4 lists what only an in-game check can confirm.

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

## 6. Not proposed

- Merging the Basic and Advanced editors: they already share one session and hand a Plate over in place.
- Bringing back My Plates' persistent action row, or making Templates a permanent tab: both were removed or avoided on purpose. Proposals 3 and 4 are the smaller alternatives.
- Changing the design system or the look of the windows: this audit is about paths, not appearance.
- Removing the right-click menus: the proposals add visible paths beside them.
