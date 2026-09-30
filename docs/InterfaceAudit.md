# Interface audit: where AetherFrame makes players go back and forth

**Status (2026-09-30): a first audit, written from the code, for the owner to review.** The owner asked on September 29, 2026 for an interface that is "more intuitive to use", because "right now there's a little bit of having to go back and forth between menus" (ROADMAP.md, section 5). [NETWORK2.md](networking/NETWORK2.md), section 6 makes an audit the first step, and ROADMAP.md, section 8 schedules it.

Everything below is a proposal, not a decision. Nothing here changes what players see. The owner reviews the audit before the first proposal is built, and each proposal that is built gets its own pull request, reviews and in-game checks. Section 4 lists what only an in-game check can confirm.

## 1. The windows today

| Window | What it holds | How players reach it |
|---|---|---|
| **My Plates** (`PlateLibraryWindow`) | The header: Create Plate, Import, search, Help, whether a character is logged in, and the Plate count. The card grid: click selects, double-click edits, drag reorders. A right-click menu on each card: Preview, Open in Basic Editor, Open in Advanced Editor, Set Active, Duplicate, Save as Template, Export, Rename, Delete. A footer: "Select a Plate. Double-click to edit; drag to reorder." and "Right click a Plate for actions". | `/aetherframe` or `/af`; Dalamud's plugin buttons; the editors' My Plates button |
| **Template chooser** (a popup in My Plates) | Built-in and saved Templates on the left, a preview on the right, Use Template. Its footer links to Manage Templates. | Create Plate |
| **Templates** (a view inside My Plates) | Preview, Use Template, Rename, Duplicate and Delete for saved Templates; a back arrow to My Plates. | Only the chooser's "Manage Templates..." link |
| **Basic Editor** and **Advanced Editor** | One editing session shared by both: the same document, unsaved state and undo history. The action bar holds My Plates, the Basic/Advanced switch (which hands the Plate over in place), the Plate's name (display only), Undo and Redo, the save state, Preview (Clean Preview), Revert, Save and Help. | Double-click a card (the editor is chosen by the Plate's content), the card menu, or Use Template |
| **Plate Viewer** (`ProfileViewWindow`) | A read-only overlay of one Plate, drawn over the game. | `/aetherframe view` (the character's Active Plate), or a card's Preview |
| **Package Import** | Checks an `.aetherframe` file and previews it; it is always added as a new Plate. | My Plates' Import, after the file dialog |
| **Tutorial and Help** | Twelve chapters that spotlight the real controls, and Help menus. | The Help buttons; offered once to new installs |

## 2. Tasks and the trips they take

A **trip** is leaving the window where the player is working to do something elsewhere, and usually coming back.

| # | Task | The path today | Trips |
|---|---|---|---|
| 1 | Create a Plate | My Plates, Create Plate, the chooser, Use Template, and the editor opens | none |
| 2 | Edit a Plate | My Plates, then double-click its card | none |
| 3 | Make the Plate just edited the character's Active Plate | Editor, Save, My Plates, find the card, right-click it, Set Active | **one**, through a hidden menu |
| 4 | Rename the Plate being edited | Editor, My Plates, right-click the card, Rename, back to the editor | **two** (there and back) |
| 5 | Save it as a Template | Editor, My Plates, right-click the card, Save as Template, back | **one or two** |
| 6 | Export it to a file | Editor, My Plates, right-click the card, Export, the file dialog, back | **one or two** |
| 7 | Duplicate it to try a variation | Editor, My Plates, right-click the card, Duplicate, double-click the copy | **one**, through a hidden menu |
| 8 | Switch to another Plate | Editor, My Plates, double-click another card; unsaved changes prompt first | one (expected) |
| 9 | See the Plate as others will | The editor's Preview; for the Active Plate outside the editors, `/af view` | none |
| 10 | Rename or delete a saved Template | My Plates, Create Plate, the chooser, Manage Templates..., the Templates view, the back arrow | **two levels deep**, with no direct way in |
| 11 | Import a Plate | My Plates, Import, the file dialog, Package Import, the new card | none |
| 12 | Do anything with the selected Plate in My Plates | Only through the right-click menu; the footer says so | none, but hidden |

Tasks 3 to 7 share a cause: the editors show the Plate's name and nothing else about it, so every action on the Plate as a whole lives only in My Plates' right-click menu. Task 10's cause is that Templates have no entry of their own. Task 12's cause is that My Plates' actions exist only in a context menu.

## 3. Proposals

Each is the smallest change that removes a trip, reusing the operations My Plates already runs (the same code, confirmations and character checks, never a second copy) and the design system ([DesignGuide.md](DesignGuide.md)). They are ordered by how much going back and forth each removes.

1. **A Plate menu in both editors' action bar** (tasks 3 to 7). The Plate's name, already in the bar, becomes a menu button:
   - Rename...
   - Set as Active Plate (disabled with a reason when no character is logged in, or when it already is)
   - Save as Template...
   - Export...
   - Duplicate (opening the copy)

   Delete stays in My Plates, where the whole library is in view. A Plate with unsaved changes is saved first, or the action says what it uses (for example, Export uses the saved Plate, as My Plates' does today). Which one each action does is decided per action in its pull request.
2. **Visible actions for the selected card in My Plates** (task 12, and faster tasks 3 and 7). When a card is selected, a row shows Edit, Preview and Set Active, and More... with the rest of the menu. The right-click menu stays as it is.
3. **A Templates button in My Plates' header** (task 10), next to Create Plate and Import, opening the Templates view directly. The chooser's link stays.
4. **Open another Plate... in the editors' Plate menu** (task 8): a short list of recent Plates, with today's unsaved-changes prompt. It is lower priority, since switching Plates is a trip players expect.
5. **An offer after a new Plate's first save** (task 3's first time only): when the logged-in character has no Active Plate, one line in the editor offers to make this Plate active. It is never automatic, and it can be dismissed for good. It is optional: it may be more noise than help, and the owner decides.
6. **Window placement, once checked in game** (task 8): if My Plates opens over the editor and hides it, open it beside the editor, or keep the editor in view. This depends on section 4.

The tutorial moves with the interface: every control a proposal adds or moves gets a tutorial anchor, and the chapters that mention the old path are rewritten in the same pull request.

## 4. What only an in-game check can confirm

- How the windows sit on common screen sizes and UI scales, and whether My Plates covers the editor when opened from it.
- Whether players find the right-click menu at all. The footer hints at it; the tutorial shows it.
- How each proposal feels in use: the owner's in-game check is part of every proposal's acceptance.

## 5. How this fits sharing (NETWORK2)

- The editors' Plate menu (proposal 1) and My Plates' card actions (proposal 2) are where **Share...** and **Unpublish** go when N2-9 adds them. Sharing then never needs a trip either, and the consent screen opens from either place.
- A Plate's sharing state (not shared, shared, or changed since sharing) belongs on its card and next to its name in the editor bar, from N2-6 and N2-9.
- The persona window (N2-5) is reached from My Plates' header and from the share flow, not only from settings.

## 6. Not proposed

- Merging the Basic and Advanced editors: they already share one session and hand a Plate over in place.
- Changing the design system or the look of the windows: this audit is about paths, not appearance.
- Removing the right-click menus: the proposals add visible paths beside them.
