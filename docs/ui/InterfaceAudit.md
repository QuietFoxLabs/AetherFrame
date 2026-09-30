# Interface audit: where AetherFrame makes players go back and forth

**Status (2026-09-30): an audit and a recommendation. It approves nothing.** It reads the plugin's interface at `714cf5c` and proposes tasks for [ROADMAP.md](../../ROADMAP.md), section 8. When a task needs a decision, the task names it, and the decision is recorded in ROADMAP.md, section 5, when that task merges. Everything said here about the interface is from code. Nothing was checked in game.

## 1. Why this exists

The owner asked on September 29, 2026 (ROADMAP.md, section 5):

> i want you to take a look at the overall UI interface and see if you can modernize it a little, make it more intuitive to use, right now there's a little bit of having to go back and forth between menus and i want the process to be as fluid as possible.

[NETWORK2.md](../networking/NETWORK2.md), section 6, puts this work after the networking increments, interleaved wherever networking waits. It says the first step is "an audit of the current flows: which tasks need a trip between windows or menus, and what one place could hold them instead." The sharing screens of N2-5, N2-9 and N2-10 are to fit where the audit is heading. This document is that audit.

## 2. How the audit was done

- **Code read:** `master` at `714cf5c`. At `beca12a` the plugin differs only by a doc comment in `Domain/Plates/PlateNaming.cs`, so every cite holds for both. Cites are `file:line`, relative to `AetherFrame/`. After its first mention, a file is named by its last part, for example Actions.cs for Windows/PlateLibraryWindow.Actions.cs.
- **Surfaces:** My Plates, Templates, both editors, and the shell: the Plate Viewer, Import Plate, commands, Help and the tutorial. A second reader checked every friction item against the code. Three directions were drafted and two judges scored them. Section 6 builds on the strongest.
- **What a trip is:**
  - A **window switch** is each time the player's work moves to a different top-level window: My Plates, the Basic editor, the Advanced editor, the Plate Viewer, Import Plate, or a file dialog. Opening, raising or returning to one counts, whether the player does it or the plugin does it for them.
    - A slash command and the window it opens count as one switch.
    - A file dialog counts once. Its closing back to the window it came from is not counted.
    - Basic to Advanced counts 1 today, because it swaps windows. Clean Preview counts 0, because it is the same window.
    - The tutorial card and the Welcome offer are never window switches.
  - A **menu** is a context menu, a submenu, a combo used as the route, a color picker, the Create Plate chooser, the title picker, Help or a naming modal (Rename, Save as Template).
  - A **prompt** is a confirmation or a question: Unsaved Changes, Delete, Replace File?, Import as New Plate, the Welcome offer, or a save-first question. A conditional prompt, one that appears only in some cases, is marked in the steps (appendix A) but not counted.
  - Counts are for the shortest common path, from code. Each journey starts with the game running, AetherFrame loaded and no AetherFrame window open, unless it says otherwise.
- **Not verified:** nothing was built, run or seen in game. Dalamud's focus, Escape and sizing behaviour was read from a decompiled Dalamud 15.0.3.6, outside the repository.

## 3. The interface today

The WindowSystem registers 13 windows: the five a player works in, the Welcome offer and seven tutorial windows (Plugin.cs:215-219, 241-244).

| Surface | Job | Opened from | Notes (from code) |
|---|---|---|---|
| My Plates | The Library, and every Plate action | /af and the installer, which toggle it (Plugin.cs:250, 264-265, 483); the editor's My Plates button, which raises it (Plugin.cs:489-493) | Stays open behind an editor. Its only way back to the editor is to open the loaded Plate's card again (Windows/PlateLibraryWindow.Actions.cs:205-213). |
| Card right-click menu | The only place for Preview, Set Active, Duplicate, Save as Template, Export, Rename and Delete. It also has Open, which a double-click on the card does too. | Right-click a card | Windows/PlateLibraryWindow.Actions.cs:103-182. No keyboard path (Windows/PlateLibraryWindow.cs:379, 399-404). |
| Create Plate chooser | Pick a Template. It has no name field. | Create Plate | A modal, so it blocks every other window, the tutorial card included (Windows/PlateLibraryWindow.Templates.cs:470; [DesignGuide.md](../DesignGuide.md)). |
| Manage Templates | A Template grid with a button bar | Only a dim link in the chooser's footer (Templates.cs:740-748) | Replaces the My Plates view. Has no Help (Templates.cs:123-143). |
| Basic editor | FFXIV-like editing | My Plates, Use Template, the mode switch | Its own window, minimum 520x560 (Windows/BasicProfileEditorWindow.cs:120-128) |
| Advanced editor | Freeform editing | The same | Its own window, minimum 980x560 (UI/Editor/AdvancedEditorLayout.cs:14-24) |
| Clean Preview | The finished Plate, in place of the editor | Preview (Windows/EditorActionBar.cs:157-163) | The same window, fixed in place (Windows/CleanPreviewPresenter.cs:35-37) |
| Plate Viewer | The Plate over the game | /af view raises it. A card's Preview doesn't raise it when it is already open (Windows/ProfileViewWindow.cs:95-118). | Offers only Close and a right-click menu of sizes and Center on Screen (ProfileViewWindow.cs:292-296, 302-342) |
| Import Plate | Check and import a file | Import, then a file dialog | Can't pick a file itself, and closes on success (Windows/PackageImportWindow.cs:156-160, 319-324) |

**Entry points.**
- There are seven ways into My Plates: /af, the installer, the editors' My Plates button, an editor's No Plate state, the viewer's No Active Plate state, a successful import and the tutorial card. They behave differently:
  - /af and the installer toggle it, so they close it while it sits behind an editor.
  - The viewer's No Active Plate state closes the viewer before opening it (ProfileViewWindow.cs:229-234).
  - A successful import opens it, but doesn't raise it when it is already open (Windows/PlateLibraryWindow.Packages.cs:26-33).
- No command reopens an editor, and outside the tour no button does. The way back is to open the loaded Plate's card again in My Plates (Windows/PlateLibraryWindow.Actions.cs:205-213). Closing an editor leaves the Plate loaded, because `ProfileService.CloseDocument` has no caller outside the tests (Services/ProfileService.cs:85-92).
- The editor's action bar has none of the card menu's actions. It shows the Plate's name as plain text (EditorActionBar.cs:89-195, 121).

## 4. Journeys

Counts are for the shortest common path, from code. Frequency is an estimate.

| # | Job | Frequency | Switches | Menus | Prompts | Back-and-forth |
|---|---|---|---|---|---|---|
| J01 | Learn with the tour | rare | 6 | 1 | 1 | Late chapters go back to My Plates, which from code stays under the editor (needs an in-game check) |
| J02 | First Plate from a Template | sometimes | 4 | 4 | 0 | Editor, My Plates, editor, only to rename |
| J03 | Change the portrait | often | 3 | 0 | 0 | Rotate, flip or opacity needs a Basic, Advanced, Basic swap |
| J04 | Theme, pattern, image, transparency | often | 3 | 1 | 0 | Opacity is only in Advanced, Pattern only in Basic |
| J05 | Add and place a frame | sometimes | 2 | 2 | 0 | Basic chooses the frame, Advanced places it |
| J06 | Fine-tune one Basic element | often | 2 | 1 | 0 | The element has to be found again by hand in Advanced |
| J07 | Switch mode mid-edit | often | 2 | 0 | 0 | Every switch swaps windows and drops context |
| J08 | Free text or image in Advanced | sometimes | 1 | 2 | 0 | None across windows |
| J09 | Preview, then show over the game | every session | 3 | 1 | 0 | Only the card menu shows the open Plate's live copy in the movable view. /af view shows only the saved Active Plate |
| J10 | Make this Plate Active, check it | sometimes | 4 | 1 | 0 | Editor, My Plates, chat, viewer, editor |
| J11 | No Active Plate: choose one and see it | rare | 5 | 1 | 0 | The viewer closes itself on the way to My Plates |
| J12 | Save as Template, start from it | sometimes | 4 | 5 | 0 | Both actions are only in My Plates, then a rename detour |
| J13 | Update a Template | rare | 2 | 6 | 2 | No way to replace one: chooser, editor, My Plates, chooser |
| J14 | Export the open Plate | sometimes | 3 | 1 | 0 | Editor, My Plates, save dialog, editor |
| J15 | Import and open a file | sometimes | 5 | 0 | 1 | Import Plate previews the Plate but can't open it |
| J16 | Find and reopen a Plate | every session | 2 | 0 | 0 | Always through My Plates, and /af may close it first |
| J17 | Try a variation | sometimes | 2 | 3 | 0 | Two card-menu trips, and unsaved edits can't be forked |
| J18 | Delete, undo a mistake | sometimes | 1 | 1 | 1 | Plates can be deleted only in My Plates, and the editor is left stranded |
| J19 | Get help later | rare | 1 | 2 | 0 | No Help in the viewer or in Import Plate |
| | **Total** | | **55** | **32** | **5** | |

Most window switches come from three sources:
- **Plate actions live only in My Plates:** J02, J10, J12, J13, J14, J17 and J18.
- **Basic and Advanced are two windows:** J04 to J07, and J03's optional path.
- **The Plate Viewer is a dead end, and only /af view shows whichever Plate is Active, as saved:** J09 to J11.

## 5. Findings

The findings are ranked by impact.

1. **Plate actions are missing from the editors (high).** Rename, Set Active, viewing a chosen Plate, Duplicate, Save as Template, Export and Delete are only in the card menu (PlateLibraryWindow.Actions.cs:103-182). /af view shows only the Active Plate. The editor never shows whether the Plate is Active (EditorActionBar.cs:82-201). The tutorial teaches the detour (UI/Tutorial/TutorialScript.cs:70, 176-178).
2. **Basic and Advanced are two windows, and each lacks controls the other has (high).**
   - The switch closes one window and opens the other (UI/Editor/EditorSurfaceCoordinator.cs:75-97). The document, unsaved edits and undo history come along, but the other mode doesn't open on the element or category the player was on.
   - Advanced resets zoom and pan every time the player returns to it (Windows/ProfileEditorWindow.cs:144-152).
   - Theme, Pattern and the title picker exist only in Basic (Windows/BasicProfileEditorWindow.Design.cs:27-40; Windows/BasicProfileEditorWindow.Identity.cs:143-150).
   - Advanced's presets add a pattern, although they are labelled "Background colors only", and Advanced can't remove it (Domain/Profiles/ProfileThemePresets.cs:85; Windows/BackgroundStylePanel.cs:150-152, 499).
   - Opacity is kept out of Basic on purpose, by a choice recorded in the code (BackgroundStylePanel.cs:107-111).
3. **My Plates and the editor overlap, and My Plates barely tracks the open Plate (high).** No card marks the Plate that is open (PlateLibraryWindow.cs:433-441). Only a footer line does, while that card is selected and no other message fills the footer, and the line stays after the editor closes (Actions.cs:66-84). /af can close My Plates while the editor covers it (Plugin.cs:483).
4. **Actions that use the saved version don't ask first (medium).** Duplicate, Export and Save as Template read the saved Plate (Services/Plates/PlateLibraryService.cs:822-824; Services/Packages/PlatePackageService.cs:76; Services/Templates/TemplateLibraryService.cs:402). Of the card menu's actions on the open Plate, only Delete mentions its unsaved edits (Actions.cs:532-537). Nothing lets the player fork unsaved edits into a new Plate (J17).
5. **The Plate Viewer can't act on what it shows (medium).** Its menu holds only size presets, Reset Size and Center on Screen (ProfileViewWindow.cs:302-342). In the No Active Plate state it closes itself on the way to My Plates, although it re-resolves the Active Plate every frame (ProfileViewWindow.cs:229-234; UI/Rendering/PlateViewerTarget.cs:44-48). A card's Preview doesn't raise a viewer that is already open (ProfileViewWindow.cs:103-118).
6. **Create Plate asks too little, and asks too late (medium).**
   - The chooser has no name field (Templates.cs:729-774).
   - The Plate is saved before the Unsaved Changes question, so Cancel leaves a stray Plate behind (Templates.cs:422-438).
   - From code, Use Template in a row's right-click menu leaves the modal open over the new editor (Templates.cs:597-608, 632-636). This needs an in-game check.
   - With an editor already open, the tour passes over "Choose a Template" (UI/Tutorial/TutorialSession.cs:366-368).
7. **Escape on a popup probably closes the window behind it too (medium; needs an in-game check).** In the decompiled Dalamud, a popup counts as focus on its owner window. Escape closes a focused window whose RespectCloseHotkey is on. For an editor with unsaved edits, the close guard turns that into the Save, Discard or Cancel question (UI/Editor/CloseGuard.cs:66-79). Of the windows that hold popups, only Clean Preview and a running import turn that setting off (CleanPreviewPresenter.cs:116; PackageImportWindow.cs:95, 103). The tutorial's overlay and shades keep it off, but they hold no popups (Windows/Tutorial/TutorialOverlayWindow.cs:53; Windows/Tutorial/TutorialShadeWindow.cs:43).
8. **Basic's Preview may leave an invisible area that catches clicks (medium; needs an in-game check).** Dalamud re-applies Basic's `Window.Size` after PreDraw, so it overrides Clean Preview's size. That size is also not clamped to the screen (BasicProfileEditorWindow.cs:127-128; Windows/EditorWidgets.cs:43-51).
9. **Late in the tour, the spotlight probably points at My Plates while it is under the editor (medium; needs an in-game check).** While a step shows, My Plates, the editors, the Plate Viewer and Import Plate can't be raised (Windows/Theme/AetherStyle.cs:316-326). Chapters 10 and 11 require only that My Plates is open (Plugin.cs:533; TutorialScript.cs:181-199). No step shows the player how to view a Plate or set it Active. The tour only lists them among the card menu's actions (TutorialScript.cs:50, 177). If the player declines the Welcome offer, My Plates doesn't open, and the offer never names /af. Only Help, inside My Plates and the editors, and Dalamud's command help name it (Windows/Tutorial/FirstRunPromptWindow.cs:80-111; UI/Tutorial/OnboardingCoordinator.cs:107-125; Windows/Tutorial/HelpMenu.cs:203-206).
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

## 6. Direction

**Recommended: "The Plate in hand".** Wherever a Plate is (in the editor, in the viewer, or on a card), one Plate menu offers every action on it, with the same words in the same order. Each window keeps one job: My Plates chooses among Plates, the editor makes them, and the viewer shows them and can act on them.

**Target structure (player build).**
- **Windows:** My Plates, one AetherFrame Editor with Basic and Advanced as modes, the Plate Viewer, and Import Plate. The tutorial windows and file dialogs stay. Preview builds add one Sharing window.
- **Plate menu:** one component, drawn by the editor's name button, by a card's right-click and by the viewer's right-click. Its items, in order:
  - Open in Basic or Advanced (cards only);
  - View;
  - Set Active (checked when the Plate is Active, and disabled with its reason when unavailable);
  - Rename (inline);
  - Save a Copy (editor) or Duplicate (card);
  - Save as Template;
  - Export;
  - an extension slot;
  - Switch to (editor only);
  - New Plate;
  - Delete.

  Plate names are drawn as text, never used as labels or IDs.
- **Editor bar:** My Plates; Basic | Advanced, switched in place; the Plate's name, which opens the Plate menu; a gold Active pill; then today's controls.
- **My Plates:** an Editing pill on the open Plate's card, a Continue Editing button, and the chooser. The chooser stays a modal and gains a name field. Manage Templates folds into the chooser.
- **Viewer:** the Plate menu above its size items. In the viewer, its first item is Edit, and it adds Show in My Plates. The No Active Plate state lets the player choose a Plate in place. (UI-7's menu list must then match: "The viewer's right-click draws the Plate menu, with Edit and Show in My Plates, then the sizes, \"How to move and resize\" and Help.")
- **Import Plate:** Choose File, then Open in Editor.
- **Plumbing:** one Library operation runner, pumped from Plugin.DrawUi (Plugin.cs:367-378), and one prompt host that draws each prompt where the player asked.
- **Words:** Preview happens inside the editor. View shows the Plate over the game.

**Counts after section 7.**
- Counted switches fall from 55 to 27, and menus from 32 to 28.
- The 5 prompts stay: the Welcome offer, plus confirmations that guard saved work or keep import explicit.
- Switches per journey afterwards: J01 5, J02 2, J03 3, J04 1, J05 to J07 0, J08 1, J09 to J11 2 each, J12 0, J13 1, J14 1, J15 4, J16 2, J17 0, J18 0, J19 1.

**Changes taken from the other proposals.**
- From "Act where you are":
  - UI-1's fixes;
  - Save and Set Active, which activates only after a successful save. It can wait on SaveAsync, which returns false unless a save succeeded (UI/Editor/EditorDocumentCommands.cs:75);
  - keeping the chooser a modal;
  - the /af rule;
  - Import's Open in Editor.
- From "One Workspace":
  - one operation runner;
  - one prompt host;
  - the save-first wording;
  - a stable `###` window ID;
  - viewer placement kept between sessions;
  - status lines that name what changed.

**The judges' flaws, and the fix for each.**

| Flaw | Fix |
|---|---|
| Two runners, and RunExclusiveAsync queues a second operation instead of refusing it (Services/Plates/PlateLibraryService.cs:1347-1366) | One runner (UI-2) |
| The guarded open, prompts and dialogs stay in My Plates' Draw (PlateLibraryWindow.cs:176, 197-219) | They move to the prompt host (UI-3) |
| An /af rule of "close when focused" probably fails after a click into chat, because ImGui drops focus on a click outside its windows (needs an in-game check) | Close only if My Plates was the last AetherFrame window focused |
| The Escape check read the same frame, and Esc in text fields was asserted, not checked | Use the previous frame's popup state. Text fields go to the in-game check. |
| Keyboard access relied on ImGui navigation | Explicit key handling (ROADMAP.md, section 4, rule 6) |
| Focus was used to mean "in front". Lifting NoBringToFrontOnFocus for one frame puts the window above the dim and the card, which come forward only on their first frames (Windows/Tutorial/TutorialShadeWindow.cs:76-80; Windows/Tutorial/TutorialCardWindow.cs:80-84) | A BringForward action raises the target, then the shades and the card, in one frame (UI-10, M) |
| The proposed fixes for the tour passing over "Choose a Template" fail, or advance on Cancel | A TemplateUsed latch (UI-6) |
| A chooser panel can hide under the editor during the tour | The chooser stays a modal |
| First-Plate activation was kept, against the CONFIRMED rule that activation is explicit | A visible checkbox (UI-6) |
| Save a Copy's rule for character bindings contradicted itself | Follow Duplicate's rule: keep the source's associations, never Active |
| Counted cuts were overstated | Every count here is recomputed from the journey table |
| A narrow Basic window widens when switched to Advanced | Kept, but Basic returns to its own width on the way back |
| Sharing was spread over three windows, and its seams waited on other tasks | One Sharing window. UI-12 waits on no other task. Before UI-2, its Share hook sits in today's card menu. |
| A Template overwrite was proposed for a rare journey | Deferred |
| Edit from the viewer and Open from Import went through a prompt that only My Plates draws | UI-7 and UI-9 depend on UI-3's host |
| Docking was said to be absent | Nothing here depends on docking, which Dalamud turns off by default |

**Why not the other two.**
- "Act where you are" keeps two editor windows. Its 8 Basic and Advanced swaps in J04 to J07 would remain, and sharing would get three windows.
- "One Workspace" cuts the most, from 55 switches to 19, but:
  - its editor merge and its retirement of My Plates are several pull requests each;
  - it would rewrite manual acceptance while test build `01a14a5` still waits for the owner (ROADMAP.md, section 2);
  - /af would close the window that holds the editor.

## 7. Tasks

The tasks are listed in order, one pull request each, and each meets the Done list in [CLAUDE.md](../../CLAUDE.md). A task that changes step text bumps `TutorialScript.Version` (UI/Tutorial/TutorialScript.cs:19). It also keeps the tutorial at 12 chapters of 1 to 7 steps (AetherFrame.Tests/TutorialSessionTests.cs:375-382, from the repository root). A bare `:line` in a task refers to UI/Tutorial/TutorialScript.cs. UI-1 is small and ships alone. UI-2 is the first task to cut counted trips, and it also ships alone.

**UI-1 Stop the stray trips (S).**
- *Changes:*
  - Basic uses EditorWidgets.SetFirstUseSize instead of `Window.Size`.
  - Each window turns RespectCloseHotkey off for the frame after one in which its popup was open. This is combined with Clean Preview's and Import's own rules.
  - The chooser row menu's Use Template sets a flag, and the chooser acts on it in its own scope (Templates.cs:632-636, 669-673).
  - Help says what Esc does.
- *Improves:* 0 counted. From code, it removes Basic's click-catching area, the 2-switch recovery after Esc on a menu, and a modal left open over the editor. All three need an in-game check.
- *Risk:* none to saved work.
- *Tutorial:* none.
- *Acceptance:* a unit test of the close rule.
- *In game:*
  - In Basic's Preview, clicks to the right of and below the Plate reach the game.
  - At 150% scale on a 1080p screen, the first Basic window fits.
  - Esc on a card menu, Help or the chooser closes only that.
  - A row menu's Use Template closes the chooser.
  - Note what Esc does while typing in Message.

**UI-2 The Plate menu (L).**
- *Changes:*
  - One Library operation runner, extracted from Actions.cs:570-649 and pumped from Plugin.DrawUi.
  - The editor's name becomes the Plate menu's button. It keeps the EditorPlateName anchor (EditorActionBar.cs:122) and shows a caret when the name doesn't fit.
  - Menu items: View (raises the viewer, shows the live copy); Set Active (Save and Set Active when there are unsaved edits); inline Rename; an empty extension slot.
  - A gold Active pill (Windows/AetherControls.cs:249).
  - The card menu uses the same component, and its Preview becomes View.
  - ShowPlate and ShowDocument raise the viewer.
- *Improves:* J02 4 to 2, J09 3 to 2, J10 4 to 2, J12 4 to 2: 7 switches.
- *Risk:* low. It uses existing service calls, and a rename keeps the dirty state (UI/Editor/EditorSession.cs:887-896).
- *Tutorial:* a new step, saving.plate, on EditorPlateName. Rewrite :70, :74 and :176-178.
- *Acceptance:* tests for:
  - Set Active's disabled reasons (no character, already Active, not Ready, busy);
  - Save and Set Active after a refused save;
  - an operation that finishes while My Plates is closed.
- *In game:*
  - Rename with unsaved edits: the card follows, and the bar still shows unsaved.
  - Set Active shows the pill. When logged out, it says why it is disabled.
  - View opens the viewer in front, with the unsaved edits.
  - At 200% scale, the caret still opens the menu.

**UI-3 Save first, copies and the prompt host (L).**
- *Changes:*
  - A prompt host draws Rename, Delete, Save as Template, Export and Replace File? where the player asked for them.
  - The guarded open moves into the host too. It asks about unsaved edits in the editor that holds them.
  - The Plate menu gains Save a Copy, Save as Template, Export and Delete.
  - With unsaved edits, Save as Template and Export first ask "Save and continue, Use last saved, Cancel".
  - Save a Copy writes the live document as a new Plate after the source, and the editor continues on the copy with no unsaved changes. The original stays as last saved. The copy keeps the source's character associations and is never Active. Moving to the copy drops the undo history, as any change of Plate does (UI/Editor/EditorSession.cs:163-173, 843-845), and the prompt says so.
  - Deleting the open Plate closes the editor and shows My Plates. Today the Delete prompt says only that the Plate will close (Actions.cs:536), and the editor stays open on "No Plate is open." (Windows/BasicProfileEditorWindow.cs:233-240).
- *Improves:* J14 3 to 1, J17 2 to 0, J18 1 to 0: 5 switches.
- *Risk:* medium in code, low for data. Save a Copy only creates a Plate.
- *Tutorial:* :177, :187 and :194 name the Plate menu. TemplateChooserOpen reads the host (Plugin.cs:535).
- *Acceptance:* tests for:
  - each save-first choice, and a failed save;
  - Save a Copy leaving the source file byte-identical;
  - the copy not being Active;
  - the copy's images still counted as in use.
- *In game:*
  - Export with unsaved edits asks first, and Save and continue exports them.
  - Save a Copy adds a card and leaves the original unchanged.
  - Delete from the name closes the editor and shows My Plates.
  - Use Template on Adventure Plate Classic, then /af at once: the editor still opens.

**UI-4 One editor window (L).**
- *Changes:*
  - One window, `AetherFrame Editor###AetherFrameEditor`, hosts both editor bodies as modes.
  - Two IEditorSurface adapters keep ActiveSurface (UI/Editor/EditorSurfaceCoordinator.cs:25-38, 62-65) and the 17 EditorModeSwitch references in TutorialScript.cs meaningful.
  - Each mode keeps its own minimum size, and Basic returns to its own width after Advanced.
  - Zoom and pan reset only when a different Plate opens.
  - Selection and category carry across a mode switch.
  - One close guard, one preview presenter, one shortcut owner and one file dialog manager (Plugin.cs:159-160).
- *Improves:* J04 3 to 1; J05, J06 and J07 2 to 0; J01 6 to 5: 9 switches.
- *Risk:* medium, with no change to data. One session already holds the document and its history (EditorSurfaceCoordinator.cs:42-49). The editor's saved window placement resets once.
- *Tutorial:* the dim list names one editor (Plugin.cs:231-232). The open actions set the mode (Plugin.cs:550-555). Re-run manual acceptance C.11 to C.20.
- *Acceptance:* tests that:
  - a mode switch commits pending edits and keeps the history;
  - selections map to the right category;
  - Basic still ignores Advanced-only keys.
- *In game:*
  - After five mode switches, the window hasn't moved, and undo and zoom still work.
  - A narrow Basic window returns to its own width after Advanced.
  - With the title selected in Advanced, switching opens Basic on Identity.
  - Chapters 4 to 9 still point at the right controls.

**UI-5 Take me there (M).**
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

**UI-6 Create Plate that asks the right things (M).**
- *Changes:*
  - The chooser stays a modal and is drawn by the prompt host, so New Plate opens it over the editor.
  - A Name field. CreatePlateFromTemplateAsync already takes a name (PlateLibraryService.cs:702-712). InstantiateAsync, its only caller, passes the Template's name today, so it gains a name parameter (Services/Templates/TemplateLibraryService.cs:579-605). A name already in use still gets a number (PlateLibraryService.cs:712).
  - The Unsaved Changes question comes before the Plate is created.
  - Use Template is disabled for a Template that isn't Ready, which the service already refuses after the click (Services/Templates/TemplateLibraryService.cs:599). Rows mark broken Templates.
  - A search field, and the chooser opens on the Template saved most recently.
  - Blank Canvas's one-time Basic suggestion shows before the Plate is created.
  - For a character's first Plate, a checkbox "Make it <character>'s Active Plate", checked by default, replaces the silent activation (PlateLibraryService.cs:757-762).
  - Manage Templates is retired. Chooser rows already rename, duplicate and delete Templates (Templates.cs:630-662).
- *Improves:* J12 2 to 0 and J13 2 to 1: 3 switches. Cancel no longer leaves a stray Plate.
- *Risk:* low to medium. The activation flag changes a service default.
- *Tutorial:* first.template (:69-72) advances on a TemplateUsed latch. Use Template sets the latch, and opening the chooser clears it, so Cancel keeps the player on the step. Text at :187 drops Manage Templates, along with its unused targets and condition.
- *Decisions to record:*
  - The checkbox, which applies "activation is explicit".
  - Retiring Manage Templates. Keeping Templates out of the top level was only a code comment (Templates.cs:28).
- *In game:*
  - Choose New Plate from the name and type a name: the Plate opens under that name.
  - With unsaved edits, Cancel leaves no new card.
  - A first Plate created with the box unchecked isn't Active.
  - Starting chapter 3 from an editor still shows "Choose a Template".

**UI-7 A Plate Viewer that acts (M; needs UI-2 and UI-3).**
- *Changes:*
  - The viewer's right-click starts with the Plate menu (section 6), with Edit and Show in My Plates in place of View, then the sizes, "How to move and resize" and Help.
  - The No Active Plate state lists Plates with a Set Active button for each, and stays open.
  - The viewer's placement is saved in the configuration.
- *Improves:* J11 5 to 2.
- *Risk:* low. Set Active stays one explicit click.
- *Tutorial:* a new step, saving.view.
- *Acceptance:* after Set Active, the viewer shows the new Plate without reopening.
- *In game:*
  - With no Active Plate, /af view, then Set Active: the Plate shows in place.
  - Edit asks, in the editor, about another Plate with unsaved edits.
  - After the game restarts, the viewer reopens where it was left.

**UI-8 My Plates knows where you are (M).**
- *Changes:*
  - An Editing pill on the open Plate's card (OpenPlateId), shown only while an editor is open (ActiveSurface).
  - A Continue Editing button.
  - The editor's My Plates button scrolls to the open Plate's card, and new cards scroll into view.
  - /af closes My Plates only if it was the last AetherFrame window focused. Otherwise it raises it.
  - A collapsed window expands when summoned.
  - Switch to in the Plate menu.
- *Improves:* 0 counted. It removes J16's second /af and the search for the open card.
- *Risk:* none to data.
- *Tutorial:* text at :44 and :53.
- *Decision to record:* the /af rule.
- *Acceptance:* tests of the /af rule and the Editing rule.
- *In game:*
  - With the editor in front, /af raises My Plates, and a second /af closes it.
  - The Editing pill clears when the editor closes.
  - A minimized My Plates expands.

**UI-9 Import that ends where you want (S; needs UI-3).**
- *Changes:* Choose File in the idle and rejected states. After a successful import, the window says "It isn't Active" and offers Open in Editor, View and Done (PackageImportWindow.cs:156-160, 262-324).
- *Improves:* J15 5 to 4.
- *Risk:* low. Import still only adds a Plate (Services/Packages/PlatePackageService.cs:105-107).
- *Tutorial:* text at :196-198.
- *In game:*
  - Open in Editor opens the new Plate, and it isn't Active.
  - After a bad file, another file can be chosen in the same window.

**UI-10 The tour catches up (M).**
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

**UI-11 One look and a keyboard path (M).**
- *Changes:*
  - Status lines clear when the view changes, and name what changed.
  - The remaining prompts use AetherControls.
  - Cards take the arrow keys, Enter and the Menu key, through a selection model that doesn't depend on Dalamud.
  - Search gets a clear button.
  - The README, screenshots and [ManualAcceptance-UI-Onboarding.md](../ManualAcceptance-UI-Onboarding.md) are refreshed.
- *Improves:* 0 counted.
- *Risk:* low.
- *Tutorial:* text at :55-56.
- *In game:*
  - Open a Plate using only the keyboard.
  - No stale "Deleted" line remains.
  - Prompts match at 100%, 150% and 200% scale.

**UI-12 Sharing seams, preview builds only (S; best after UI-2).**
- *Changes:* hooks that do nothing by default, for Share, Open Code, Sharing, a Delete note, a viewer source and the dim list. Only the preview composition root fills them (AetherFrame/AetherFrame.csproj:46-64, from the repository root).
- *When:* it lands with N2-5, which NETWORK2 merges before N2-9. If it lands before UI-2, the Share hook sits in today's card menu.
- *Risk:* network types could leak into player builds. A test asserts that the player DLL has no Sharing type.
- *Tutorial:* none.
- *In game:* a player build has no Share item. Share follows Export in a preview build, checked once N2-11's tester kit exists (UNRESOLVED).

## 8. How sharing fits

The sharing screens compile only in preview builds (AetherFrame.csproj:61-64). They reach the shared windows only through UI-12's hooks. No tutorial step anchors to them, and every local feature works without an account or a network.

| Planned screen | Home in the target structure |
|---|---|
| N2-5 persona window | The Persona page of one Sharing window in `Windows/Network`. It opens from My Plates' Sharing button, and from Share when no persona is selected. It is kept away from the character status (Windows/PlateLibraryWindow.cs:286-293; D3). Whether the persona switcher is disabled while an operation that signs is running waits on L10, which is UNRESOLVED. Key files that no persona record names are only listed (L12 is UNRESOLVED). There is room for the backup, but no control for it. |
| K4 acknowledgement | A short modal in the Sharing window before a persona's first publish to a real server, asked once per persona and recorded in the persona registry. The Persona page repeats it whenever a persona is created (K4). |
| N2-9 consent and publish | The This Plate page, reached from Share in the Plate menu. It starts with UI-3's save-first question, because the builder reads only the saved Plate (NETWORK2.md, section 5). A name refused under D4 is fixed with UI-2's inline rename. The share code comes with a Copy button. It is a page, not a modal, so it never blocks the tutorial card. |
| Update and unpublish | The same page, which shows the consent content again each time. Unpublish is also in My Shares, the P1 index, which includes Plates that no longer exist. The Delete prompt says that deleting a Plate doesn't unpublish it. The wording waits on D1. |
| N2-10 viewer | Open Code in My Plates' header, kept apart from Import. The Plate floats in the viewer through ShowDocument, read-only and never saved (Windows/ProfileViewWindow.cs:114-118). A strip holds Refresh, notes, "no longer shared" and the outdated-client message. There is no author line. Stage 2's "save a copy" reuses Save a Copy, which never activates. |

These rules hold throughout ([DecisionRegister.md](../networking/DecisionRegister.md)):
- Remote text is never an ImGui label, window title or ID, and is drawn only by calls that don't format it (N7).
- A Shared pill reads the publication index, never the Plate (P1).
- Refresh is a button (R2).
- When K3 turns persona features off, the Sharing window shows one message that names what is missing. Open Code still works when the probe's verification step passes (K3).

## 9. What this audit does not decide

- **The tasks' decisions:** the first-Plate checkbox and the retirement of Manage Templates (UI-6), and the /af rule (UI-8). Each is recorded in ROADMAP.md, section 5, when its task merges, and the owner can overrule any of them. ROADMAP.md, section 5, puts NETWORK2 first and the interface work after it, interleaved wherever networking waits. It leaves the order within the interface work open, so the order in section 7 is this audit's recommendation.
- **Whether My Plates ever docks into the editor, or the chooser becomes a panel.**
- **Replacing a Template, restoring a Plate, the shared gradient and pattern color, and the canvas work.**
- **Autosave and history**, which ROADMAP.md keeps OPEN.
- **Networking decisions:** D1, D6, L10, L12 and the tester kit stay with the register. The persona interface stays OPEN (ROADMAP.md, section 5), so section 8 is a proposal for N2-5, N2-9 and N2-10 to weigh.
- **How anything looks.** Every count here is from code. The In game lists are where the owner confirms or corrects them.