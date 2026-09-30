# Interface audit: the journeys, step by step

This is the companion of [InterfaceAudit.md](InterfaceAudit.md): the path behind every count in its section 7.2 table, today and after the second pass's twelve tasks (UI-1 to UI-12; section 7.6 says where each goes in the queue). Everything here is from code at `beca12a` (the plugin source differs from `714cf5c` only by a doc comment in Domain/Plates/PlateNaming.cs). Nothing was run or seen in game. Cites are `file:line`, relative to `AetherFrame/`.

## How to read it

- Each step names where the player is, in brackets, and what the step adds: **S** a window switch, **M** a menu, **P** a prompt, numbered in order. A step with no mark adds nothing. The counting rules are section 7.1 of the audit.
- A conditional prompt, one that appears only in some cases, is named but not counted.
- **Starts.** J07 starts in the Basic editor, mid-edit. J09, J10, J12, J14, J17 and J18 start with an editor open on the Plate. J19 starts with the viewer showing a Plate. Every other journey starts with no AetherFrame window open.
- **Where a path ends.** A path ends where the job ends. A way back is counted only when the job needs it (J07), or when the plugin moves the player: Import Plate returning to My Plates in J15 today, and Delete moving to My Plates in J18 after UI-3.
- **Choices.**
  - J01: the chapter 10 move to My Plates counts once. Finish raises that same window, so it isn't counted again.
  - J03, J05 and J06 use the common double-click path. Today, opening in Advanced from the card menu trades one switch for one menu.
  - J08 counts a freeform Plate. A Basic-structured Plate adds 1 switch today and 0 after UI-4.
  - J10 checks with /af view both today and after, because that needs fewer menus than a card's Preview or the Plate menu's View.
  - J13 today deletes the old Template in Manage Templates, as the tour teaches (UI/Tutorial/TutorialScript.cs:187). UI-6 retires Manage Templates, so the path after it uses the chooser's row menu.
  - J19 is counted from the viewer. From Import Plate it is 1 switch and 1 menu today, and 1 menu after UI-10.
- **Assumptions.** UI-10 adds Help to Import Plate. The new tutorial steps of UI-2 (saving.plate) and UI-7 (saving.view) only spotlight; they open neither the Plate menu nor the viewer.

## Totals

| | Switches | Menus | Prompts |
|---|---|---|---|
| Today | 52 | 26 | 5 |
| After all twelve tasks | 33 | 26 | 5 |

Switches removed, by task: UI-2 3 (J02, J09, J10), UI-3 3 (J14 1, J17 2), UI-4 7 (J01, J03, J04, J05 and J06 1 each, J07 2), UI-6 2 (J12), UI-7 3 (J11 2, J19 1), UI-9 1 (J15). The other tasks remove none.

Menus stay at 26. J02 loses 2 (the Name field replaces the card menu and the Rename modal) and J11 loses 1. J12 gains 1 (two Plate menu openings), J13 gains 1 (the chooser's row menu replaces the Manage Templates view, which isn't a menu) and J19 gains 1 (the viewer's menu before Help). The 5 prompts stay: J01, J13 twice, J15 and J18.

## J01. Learn with the tour: a new install, from the Welcome offer through the twelve chapters.

Frequency (estimated): rare. Today: 4 switches, 1 menu, 1 prompt. After: 3 switches, 1 menu, 1 prompt.
Changed by: UI-4 (4 to 3 switches).
Back and forth today: The tour moves from My Plates (chapters 2 and 3) to two editor windows swapped mid-tour (4 to 9), then back to My Plates (10 and 11), which from code can't come out from under the editor while the spotlight is up.

**Today**

1. [Welcome offer] It opens by itself once both Libraries load. It is never a window switch. Click Start Tutorial: P1 (Windows/Tutorial/FirstRunPromptWindow.cs:39-47, 88-91; UI/Tutorial/OnboardingCoordinator.cs:107-115). The tutorial card opens, which is never a switch. Maybe Later and Do Not Show Again only close the offer (FirstRunPromptWindow.cs:94-111).
2. [Tutorial card] Chapter 1 has three narrative steps, and no AetherFrame window is open (UI/Tutorial/TutorialScript.cs:28-39). No count.
3. [Card to My Plates] library.home needs My Plates, so the card shows Open My Plates. Click it: S1 (TutorialScript.cs:43-45; Windows/Tutorial/TutorialCardWindow.cs:152-156, 298; Plugin.cs:489-493, 547-549).
4. [My Plates] Chapter 2 spotlights the header, Create Plate, the grid, search and Help (TutorialScript.cs:41-61). No count.
5. [My Plates to the Create Plate chooser] Chapter 3, first.create: Create Plate opens the modal chooser: M1 (TutorialScript.cs:65-68; Windows/PlateLibraryWindow.cs:259-262; Windows/PlateLibraryWindow.Templates.cs:456-470).
6. [Chooser to Basic editor] first.template: Use Template on Adventure Plate Classic. The Plate is created, and the Basic editor opens over My Plates: S2 (TutorialScript.cs:69-72; PlateLibraryWindow.Templates.cs:422-438, 768-772; Plugin.cs:500-504).
7. [Basic editor] Chapter 4 (TutorialScript.cs:78-95). No count.
8. [Basic editor to Advanced editor] Chapter 5: advanced.toolbar needs Advanced, so the card offers Switch to the Advanced Editor (the Basic | Advanced switch does the same). Basic closes and Advanced opens: S3 (TutorialScript.cs:99-104; TutorialCardWindow.cs:152-156, 300; Plugin.cs:553-555; UI/Editor/EditorSurfaceCoordinator.cs:75-97).
9. [Advanced editor] Chapters 6 to 9. saving.library only spotlights the My Plates button (TutorialScript.cs:119-179). No count.
10. [My Plates] Chapters 10 and 11 require only MyPlatesOpen, which reads IsOpen. My Plates has been open since step 3, so the spotlight moves to Create Plate, the grid and Import in My Plates: S4. From code, My Plates stays behind the editor, because every AetherFrame window gets NoBringToFrontOnFocus while a step shows (needs an in-game check) (TutorialScript.cs:181-199; Plugin.cs:533; Windows/Theme/AetherStyle.cs:317-326).
11. [Tutorial card] Chapter 12, Finish: raises My Plates, the window the work already moved to in chapter 10. Not counted again (TutorialScript.cs:203-205; TutorialCardWindow.cs:244-252).

**After all twelve tasks**

1. [Welcome offer] Start Tutorial: P1. Every answer now leaves My Plates open (UI-10), so My Plates opens along with the card: S1.
2. [My Plates] Chapters 1 and 2. library.home is already met. No count.
3. [My Plates to chooser] Chapter 3: Create Plate: M1. The chooser is still a modal, now with a Name field (UI-6), and first.template advances on the TemplateUsed latch.
4. [Chooser to AetherFrame Editor] Use Template opens the one editor window in Basic mode: S2.
5. [Editor] Chapter 4 in Basic mode. Chapter 5 switches to Advanced mode inside the same window, which counts 0 (UI-4). Chapters 6 to 9 stay there. This assumes the new saving.plate (UI-2) and saving.view (UI-7) steps only spotlight, and open neither the Plate menu nor the viewer.
6. [My Plates] Chapter 10: the BringForward step action raises My Plates above the editor, then the dim and the card (UI-10): S3.
7. [My Plates] Chapter 11, then Finish. My Plates is already in front, so nothing moves. No count.

## J02. First Plate from a Template, with an FFXIV title, a portrait and a name.

Frequency (estimated): sometimes. Today: 4 switches, 4 menus, 0 prompts. After: 3 switches, 2 menus, 0 prompts.
Changed by: UI-2 (4 to 3 switches).
Back and forth today: The player goes from the editor to My Plates and its card menu, only to give the Plate a name the chooser never asked for.

**Today**

1. [Chat to My Plates] /af opens My Plates: S1 (Plugin.cs:250, 483).
2. [My Plates to chooser] Click Create Your First Plate in the empty library, or Create Plate: M1. The chooser has no name field (Windows/PlateLibraryWindow.cs:259-262, 355-366; Windows/PlateLibraryWindow.Templates.cs:456-470, 729-774).
3. [Chooser to Basic editor] Adventure Plate Classic is preselected. Use Template creates the Plate under the Template's name. As the character's first Plate, it becomes Active, as the chooser's note says beforehand (Windows/PlateLibraryWindow.Templates.cs:733; UI/Library/MyPlatesCharacterText.cs:27-28). The Basic editor opens over My Plates: S2 (PlateLibraryWindow.Templates.cs:422-438, 463, 768-772; Services/Plates/PlateLibraryService.cs:757-762; Plugin.cs:500-504).
4. [Basic editor to title picker] On the rail, Identity. Type the character name, set Title to FFXIV Title, then Choose a title...: M2 (Windows/BasicProfileEditorWindow.cs:415; Windows/BasicProfileEditorWindow.Identity.cs:99-122, 131-150, 301-315).
5. [Basic editor to file dialog] Portrait, then Choose a Portrait...: S3. The dialog closes back to Basic (Windows/BasicProfileEditorWindow.Sections.cs:54-66; BasicProfileEditorWindow.cs:905-914).
6. [Basic editor] Save (Windows/EditorActionBar.cs:177-186). No count.
7. [Basic editor to My Plates] The bar draws the Plate's name as plain text, so the name has to be given in My Plates. Click the My Plates button: S4 (EditorActionBar.cs:89-92, 116-127; Plugin.cs:489-493).
8. [My Plates to card menu to Rename modal] Right-click the new card: M3. Rename: M4. Type the name and press Enter (PlateLibraryWindow.cs:399-404; Windows/PlateLibraryWindow.Actions.cs:165-171, 436-487).

**After all twelve tasks**

1. [Chat to My Plates] /af: S1.
2. [My Plates to chooser] Create Your First Plate or Create Plate: M1. Type the Plate's name in the new Name field. The first-Plate box, Make it <character>'s Active Plate, is shown checked (UI-6).
3. [Chooser to editor, Basic mode] Use Template: S2.
4. [Editor to title picker] Identity, the character name, then Choose a title...: M2.
5. [Editor to file dialog] Choose a Portrait...: S3.
6. [Editor] Save. The name came from the chooser, so there is no Plate menu and no trip to My Plates. With UI-2 alone, inline Rename in the Plate menu already removes the trip (3 switches, 3 menus).

## J03. Change the portrait image of an existing Plate, then rotate or flip it.

Frequency (estimated): often. Today: 4 switches, 0 menus, 0 prompts. After: 3 switches, 0 menus, 0 prompts.
Changed by: UI-4 (4 to 3 switches).
Back and forth today: The image is replaced in the Basic window, but it can be rotated or flipped only in the separate Advanced window.

**Today**

1. [Chat to My Plates] /af: S1 (Plugin.cs:250, 483).
2. [My Plates to Basic editor] Double-click the card. An Adventure Plate layout opens in Basic, on Style: S2 (Windows/PlateLibraryWindow.cs:394-397; Windows/PlateLibraryWindow.Actions.cs:192-199; UI/Editor/EditorSurfaceCoordinator.cs:19-23; UI/Editor/BasicEditorView.cs:102-112).
3. [Basic editor to file dialog] Portrait on the rail, then Replace Portrait...: S3 (Windows/BasicProfileEditorWindow.cs:415; Windows/BasicProfileEditorWindow.Sections.cs:85-87).
4. [Basic editor to Advanced editor] Basic's Portrait has no rotation or flip. Click the Advanced half of the switch. Basic closes and Advanced opens: S4 (Windows/EditorActionBar.cs:236-255; EditorSurfaceCoordinator.cs:75-97).
5. [Advanced editor] Select the portrait on the canvas or in Layers. On the Element tab, use the Rotation slider or the Flip X and Flip Y toggles, then Save (Windows/ProfileEditorWindow.Layers.cs:127-135; Windows/ProfileEditorWindow.Inspector.cs:72, 327-341, 845-855; EditorActionBar.cs:177-186). No count.
6. Variant: opening the Plate from the card menu's Open in Advanced Editor and using Replace Image... there gives 3 switches and 1 menu instead (PlateLibraryWindow.Actions.cs:120-123; Inspector.cs:818-821).

**After all twelve tasks**

1. [Chat to My Plates] /af: S1.
2. [My Plates to editor, Basic mode] Double-click the card: S2.
3. [Editor to file dialog] Portrait, then Replace Portrait...: S3.
4. [Editor] UI-5's portrait link, or the mode switch, changes to Advanced mode in the same window with the portrait selected. This counts 0 (UI-4).
5. [Editor] Rotate or flip, then Save. No count.

## J04. Change theme, pattern, background image and background transparency of an existing Plate.

Frequency (estimated): often. Today: 4 switches, 1 menu, 0 prompts. After: 3 switches, 1 menu, 0 prompts.
Changed by: UI-4 (4 to 3 switches).
Back and forth today: Theme and Pattern are only in the Basic window and background Opacity only in the Advanced window, so one look change spans both.

**Today**

1. [Chat to My Plates] /af: S1 (Plugin.cs:250, 483).
2. [My Plates to Basic editor] Double-click the card. It opens on Style: S2 (Windows/PlateLibraryWindow.cs:394-397; UI/Editor/BasicEditorView.cs:102-112).
3. [Basic editor] Click a Theme card, then a Pattern card (Windows/BasicProfileEditorWindow.Design.cs:22-40). No count.
4. [Basic editor, Mode combo] Customize Background, then the Mode combo, then Image: M1 (Design.cs:44-48; Windows/BackgroundStylePanel.cs:63-69).
5. [Basic editor to file dialog] Choose Image...: S3 (BackgroundStylePanel.cs:671).
6. [Basic editor to Advanced editor] Basic has no background Opacity. The panel draws it only when given a preset callback, and Basic passes null. Click Advanced: S4 (BackgroundStylePanel.cs:107-124; Design.cs:47; Windows/EditorActionBar.cs:236-255).
7. [Advanced editor] Canvas tab, Background, Opacity, then Save (Windows/ProfileEditorWindow.Inspector.cs:85; Windows/ProfileEditorWindow.CanvasSettings.cs:104-116; BackgroundStylePanel.cs:112-124). No count.

**After all twelve tasks**

1. [Chat to My Plates] /af: S1.
2. [My Plates to editor, Basic mode] Double-click: S2.
3. [Editor] Theme card, then Pattern card. No count.
4. [Editor, Mode combo] Customize Background, Mode, Image: M1.
5. [Editor to file dialog] Choose Image...: S3.
6. [Editor] UI-5's Opacity link, or the mode switch, changes to Advanced mode in the same window. This counts 0 (UI-4). Canvas tab, Opacity, then Save.

## J05. Add, place and recolor a frame Component on an existing Basic-structured Plate.

Frequency (estimated): sometimes. Today: 3 switches, 2 menus, 0 prompts. After: 2 switches, 2 menus, 0 prompts.
Changed by: UI-4 (3 to 2 switches).
Back and forth today: Placement and color are only in the Advanced window, so a Basic-structured Plate is finished in the other window.

**Today**

1. [Chat to My Plates] /af: S1 (Plugin.cs:250, 483).
2. [My Plates to Basic editor] Double-click the card. It opens on Style: S2 (Windows/PlateLibraryWindow.cs:394-397; UI/Editor/BasicEditorView.cs:102-112).
3. [Basic editor, style combo] Under Frame & Decorations, pick a Plate Frame style: M1. The hint sends placement and color to Advanced (Windows/BasicProfileEditorWindow.Components.cs:17-28, 43).
4. [Basic editor to Advanced editor] Click Advanced: S3 (Windows/EditorActionBar.cs:236-255; UI/Editor/EditorSurfaceCoordinator.cs:75-97).
5. [Advanced editor] Canvas tab, Components. Click the Plate Frame row to expand it, then set Offset and Size (Windows/ProfileEditorWindow.Inspector.cs:85; Windows/ProfileEditorWindow.CanvasSettings.cs:30-35; Windows/ProfileEditorWindow.Components.cs:127-130, 267-283). No count.
6. [Advanced editor, color picker] Check Custom, then open the swatch's color picker: M2. Save (ProfileEditorWindow.Components.cs:236-251).
7. Variant: Advanced's own Add Component combo also adds the frame (ProfileEditorWindow.Components.cs:69). Opening the Plate in Advanced from the card menu gives 2 switches and 3 menus.

**After all twelve tasks**

1. [Chat to My Plates] /af: S1.
2. [My Plates to editor, Basic mode] Double-click: S2.
3. [Editor, style combo] Plate Frame style: M1.
4. [Editor] UI-5's Components link changes to Advanced mode in the same window and reveals the Component's row. This counts 0 (UI-4). Set Offset and Size.
5. [Editor, color picker] Custom, then the swatch: M2. Save.

## J06. Move and recolor a Basic section caption on an existing Plate.

Frequency (estimated): sometimes. Today: 3 switches, 1 menu, 0 prompts. After: 2 switches, 1 menu, 0 prompts.
Changed by: UI-4 (3 to 2 switches).
Back and forth today: Basic owns the section, but moving or recoloring its caption means swapping to the Advanced window and finding the caption again by hand.

**Today**

1. [Chat to My Plates] /af: S1 (Plugin.cs:250, 483).
2. [My Plates to Basic editor] Double-click the card: S2 (Windows/PlateLibraryWindow.cs:394-397).
3. [Basic editor] Basic has no placement control and no caption color. Captions get only the shared heading size, and Details' Appearance styles the values (Windows/BasicProfileEditorWindow.Design.cs:57-110; Windows/BasicProfileEditorWindow.Sections.cs:174-182). No count.
4. [Basic editor to Advanced editor] Click Advanced. Nothing from Basic's category is selected, and zoom resets: S3 (Windows/EditorActionBar.cs:236-255; UI/Editor/EditorSurfaceCoordinator.cs:75-97; Windows/ProfileEditorWindow.cs:144-152).
5. [Advanced editor] Find the caption on the canvas or in Layers (its tooltip reads 'Basic: ...'). Drag it, or type X and Y (Windows/ProfileEditorWindow.Layers.cs:127-135, 248-257; Windows/ProfileEditorWindow.Inspector.cs:227-240). No count.
6. [Advanced editor, color picker] Appearance, then the Color swatch: M1. Save (Inspector.cs:656-670).

**After all twelve tasks**

1. [Chat to My Plates] /af: S1.
2. [My Plates to editor, Basic mode] Double-click: S2.
3. [Editor] The mode switch changes to Advanced mode in the same window. This counts 0 (UI-4). Find and drag the caption.
4. [Editor, color picker] Appearance, then Color: M1. Save.

## J07. Switch from Basic to Advanced mid-edit and back.

Frequency (estimated): often. Today: 2 switches, 0 menus, 0 prompts. After: 0 switches, 0 menus, 0 prompts.
Changed by: UI-4 (2 to 0 switches).
Back and forth today: Every mode switch closes one window and opens another, and the player finds their place again in each direction.

**Today**

1. Start: the Basic editor is open on a Plate, mid-edit.
2. [Basic editor to Advanced editor] Click the Advanced half of the switch. Pending edits are committed, and Basic closes. Advanced opens at its own position and size, with Auto Fit reset: S1 (Windows/EditorActionBar.cs:236-255; UI/Editor/EditorSurfaceCoordinator.cs:75-97; Windows/ProfileEditorWindow.cs:144-152).
3. [Advanced editor] Edit. No count.
4. [Advanced editor to Basic editor] Click the Basic half. Advanced closes, and Basic reopens on its previous category: S2 (UI/Editor/BasicEditorView.cs:102-112). Unsaved changes and undo history carry across (EditorActionBar.cs:245-252).

**After all twelve tasks**

1. Start: the one editor window, in Basic mode.
2. [Editor] Basic | Advanced changes the mode in place. Selection and category carry across, and zoom resets only for a different Plate. This counts 0 (UI-4).
3. [Editor] Switching back also counts 0. Basic returns to its own width (UI-4).

## J08. Add free text, set its font and color, and add an image in the Advanced editor.

Frequency (estimated): sometimes. Today: 3 switches, 2 menus, 0 prompts. After: 3 switches, 2 menus, 0 prompts.
Back and forth today: Nothing goes back and forth across windows beyond the file dialog: the work stays in the Advanced editor.

**Today**

1. [Chat to My Plates] /af: S1 (Plugin.cs:250, 483).
2. [My Plates to Advanced editor] Double-click a freeform Plate, which opens in Advanced: S2. Conditional: the one-time Basic suggestion before a first Advanced editor (Windows/PlateLibraryWindow.cs:394-397; Windows/PlateLibraryWindow.Actions.cs:192-199, 235-310, 339-351).
3. [Advanced editor] + Text, then type (Windows/ProfileEditorWindow.cs:303-306; Windows/ProfileEditorWindow.Inspector.cs:403-433). No count.
4. [Advanced editor, font combo] Typography, Font: M1 (Inspector.cs:483).
5. [Advanced editor, color picker] Appearance, Color: M2 (Inspector.cs:667). Drag the text into place.
6. [Advanced editor to file dialog] + Image: S3. Save (ProfileEditorWindow.cs:312-315).
7. A Basic-structured Plate opens in Basic instead, which adds a swap to Advanced: +1 today, 0 after UI-4.

**After all twelve tasks**

1. [Chat to My Plates] /af: S1.
2. [My Plates to editor, Advanced mode] Double-click the freeform Plate: S2. The one-time Basic suggestion stays conditional.
3. [Editor] + Text, then type. No count.
4. [Editor, font combo] Font: M1.
5. [Editor, color picker] Color: M2.
6. [Editor to file dialog] + Image: S3. Save.

## J09. Preview the open Plate, then show it over the game in the movable Plate Viewer.

Frequency (estimated): every session. Today: 2 switches, 1 menu, 0 prompts. After: 1 switch, 1 menu, 0 prompts.
Changed by: UI-2 (2 to 1 switch).
Back and forth today: The player goes from the editor to My Plates and its card menu only to reach the movable view of the Plate that is already open.

**Today**

1. Start: an editor is open on the Plate.
2. [Editor] Preview turns the same window into the finished Plate, fixed in place. This counts 0. Esc or its close control returns (Windows/EditorActionBar.cs:157-163; Windows/CleanPreviewPresenter.cs:35-37, 174-180).
3. [Editor to My Plates] No editor control shows the Plate over the game. Click the My Plates button: S1 (EditorActionBar.cs:89-92; Plugin.cs:489-493).
4. [My Plates, card menu] Right-click the open Plate's card, which isn't marked as open: M1 (Windows/PlateLibraryWindow.cs:399-404, 433-441).
5. [Plate Viewer] Preview opens the viewer on the live copy. It isn't raised if it was already open: S2 (Windows/PlateLibraryWindow.Actions.cs:110-113; Windows/ProfileViewWindow.cs:103-107; UI/Rendering/PlateViewerTarget.cs:84-88). Drag to move, Ctrl+wheel to resize, or right-click for sizes (ProfileViewWindow.cs:258-342).

**After all twelve tasks**

1. [Editor] Preview, then leave it. This counts 0.
2. [Editor, Plate menu] Click the Plate's name: M1 (UI-2).
3. [Plate Viewer] View raises the viewer with the live copy: S1 (UI-2).

## J10. From the editor, make the open Plate Active and check it in the viewer.

Frequency (estimated): sometimes. Today: 2 switches, 1 menu, 0 prompts. After: 1 switch, 1 menu, 0 prompts.
Changed by: UI-2 (2 to 1 switch).
Back and forth today: The player goes from the editor to My Plates' card menu to set the Plate Active, then to chat and the viewer to see it.

**Today**

1. Start: an editor is open on the Plate.
2. [Editor] Save. The Active Plate is presented from its saved copy, and the bar never shows whether this Plate is Active (Windows/EditorActionBar.cs:82-201, 177-186; UI/Rendering/PlateViewerTarget.cs:91-92). No count.
3. [Editor to My Plates] My Plates button: S1 (EditorActionBar.cs:89-92; Plugin.cs:489-493).
4. [My Plates, card menu] Right-click the card: M1. Set Active. It is disabled, with no reason given, when no character is logged in or the Plate is already Active. The footer confirms (Windows/PlateLibraryWindow.cs:399-404; Windows/PlateLibraryWindow.Actions.cs:128-137).
5. [Chat to Plate Viewer] /af view opens and raises the viewer on the saved Active Plate: S2 (Plugin.cs:250; Windows/ProfileViewWindow.cs:95-100). The card's Preview would show it too, at the cost of one more menu.

**After all twelve tasks**

1. [Editor, Plate menu] Click the Plate's name: M1. Choose Set Active, or Save and Set Active when there are unsaved edits, which activates only after the save succeeds. The gold Active pill appears (UI-2).
2. [Chat to Plate Viewer] /af view: S1. The Plate menu's View does the same, at the cost of one more menu.

## J11. No Active Plate: choose one and see it, starting from /af view.

Frequency (estimated): rare. Today: 3 switches, 1 menu, 0 prompts. After: 1 switch, 0 menus, 0 prompts.
Changed by: UI-7 (3 to 1 switch).
Back and forth today: The viewer closes itself on the way to My Plates, so the player types /af view again to see the Plate they chose.

**Today**

1. [Chat to Plate Viewer] /af view shows the No Active Plate state: S1 (Plugin.cs:250; Windows/ProfileViewWindow.cs:95-100, 191-194, 210-235).
2. [Plate Viewer to My Plates] Open My Plates closes the viewer, then opens My Plates: S2 (ProfileViewWindow.cs:229-234; Plugin.cs:489-493).
3. [My Plates, card menu] Right-click a card: M1. Set Active (Windows/PlateLibraryWindow.Actions.cs:128-137).
4. [Chat to Plate Viewer] /af view again: S3. The viewer re-resolves the Active Plate every frame, so it would have shown the Plate had it stayed open (UI/Rendering/PlateViewerTarget.cs:47-48).

**After all twelve tasks**

1. [Chat to Plate Viewer] /af view. The No Active Plate state now lists the Plates, each with Set Active (UI-7): S1.
2. [Plate Viewer] Click Set Active on one. The viewer stays open and shows it. No count.

## J12. Save the open Plate as a Template, then start a new Plate from it.

Frequency (estimated): sometimes. Today: 2 switches, 3 menus, 0 prompts. After: 0 switches, 4 menus, 0 prompts.
Changed by: UI-6 (2 to 0 switches).
Back and forth today: Save as Template and Create Plate are both only in My Plates, so the editor sends the player there and back.

**Today**

1. Start: an editor is open on the Plate.
2. [Editor] Save first. Save as Template takes the last saved state (Windows/PlateLibraryWindow.Templates.cs:832; Services/Templates/TemplateLibraryService.cs:402). No count.
3. [Editor to My Plates] My Plates button: S1 (Windows/EditorActionBar.cs:89-92).
4. [My Plates, card menu, then naming modal] Right-click the card: M1. Save as Template: M2. Save (Windows/PlateLibraryWindow.Actions.cs:150-156; PlateLibraryWindow.Templates.cs:819-874).
5. [My Plates to chooser] Create Plate: M3. The chooser opens on Adventure Plate Classic, so select the new Template under MY TEMPLATES (Windows/PlateLibraryWindow.cs:259-262; PlateLibraryWindow.Templates.cs:456-470, 535-553).
6. [Chooser to editor] Use Template creates the Plate and raises the editor its content suits: S2. Conditional: Unsaved Changes in My Plates, if the open Plate has unsaved edits (PlateLibraryWindow.Templates.cs:422-438, 768-772; PlateLibraryWindow.Actions.cs:205-225, 375-432).

**After all twelve tasks**

1. [Editor, Plate menu, then naming modal] Click the Plate's name: M1. Save as Template: M2. Conditional: the save-first question when there are unsaved edits (UI-3). Save.
2. [Editor, Plate menu, then chooser] Click the name again: M3. New Plate opens the chooser over the editor: M4. It has a Name field and opens on the Template saved most recently (UI-6).
3. [Editor] Use Template. The editor continues on the new Plate, in the mode its content suits and in the same window. This counts 0. Conditional: Unsaved Changes, asked before the Plate is created (UI-6). With UI-3 alone, the count stays 2, because Create Plate is still only in My Plates.

## J13. Update a Template, then delete the working Plate. No replace exists, so: make a Plate from it, edit it, save it as a Template, delete the old Template.

Frequency (estimated): rare. Today: 3 switches, 5 menus, 2 prompts. After: 3 switches, 6 menus, 2 prompts.
Back and forth today: The player goes from the chooser to the editor, then to My Plates' card menu, then to the chooser again, because a Template can't be edited or replaced in place.

**Today**

1. [Chat to My Plates] /af: S1 (Plugin.cs:250, 483).
2. [My Plates to chooser] Create Plate: M1. Select the Template under MY TEMPLATES (Windows/PlateLibraryWindow.cs:259-262; Windows/PlateLibraryWindow.Templates.cs:456-470, 535-553).
3. [Chooser to editor] Use Template creates a working Plate under the Template's name and opens it in an editor: S2 (PlateLibraryWindow.Templates.cs:422-438, 768-772).
4. [Editor] Edit, then Save. Save as Template reads the saved state (Windows/EditorActionBar.cs:177-186). No count.
5. [Editor to My Plates] My Plates button: S3 (EditorActionBar.cs:89-92).
6. [My Plates, card menu, then naming modal] Right-click the working Plate: M2. Save as Template: M3. The name is pre-filled, and no overwrite exists (Windows/PlateLibraryWindow.Actions.cs:150-156; PlateLibraryWindow.Templates.cs:819-874; Services/Templates/TemplateLibraryService.cs:398-410).
7. [My Plates to chooser] Create Plate: M4. The Manage Templates... link swaps My Plates to the Templates view, in the same window. Select the old Template, click Delete, then confirm: P1. The back arrow returns to My Plates (PlateLibraryWindow.Templates.cs:125-129, 380-385, 740-748, 933-987). The chooser row menu does the same with one more menu (Templates.cs:590-609, 657-661).
8. [My Plates, card menu] Right-click the working Plate: M5. Delete, then confirm; the prompt warns the Plate is open in the editor: P2. The editor behind is left on 'No Plate is open.' (PlateLibraryWindow.Actions.cs:174-181, 491-568; Services/ProfileService.cs:699-707; Windows/BasicProfileEditorWindow.cs:233-244).

**After all twelve tasks**

1. [Chat to My Plates] /af: S1.
2. [My Plates to chooser] Create Plate: M1. Select the Template. The Name field is optional (UI-6).
3. [Chooser to editor] Use Template: S2.
4. [Editor] Edit. No count.
5. [Editor, Plate menu, then naming modal] Click the name: M2. Save as Template: M3. Conditional: the save-first question (UI-3).
6. [Editor to My Plates] My Plates button: S3.
7. [My Plates to chooser, then row menu] Create Plate: M4. Right-click the old Template's row: M5. Delete Template, then confirm: P1. Cancel the chooser. Manage Templates is retired (UI-6).
8. [My Plates, card Plate menu] Right-click the working Plate: M6. Delete, then confirm: P2. The editor closes. My Plates is already in front, so no further switch (UI-3). Doing it all from the editor instead (New Plate, the chooser, the row menu, then Delete, which moves to My Plates automatically) is also 3 switches, but 7 menus.

## J14. Export the open Plate to a file.

Frequency (estimated): sometimes. Today: 2 switches, 1 menu, 0 prompts. After: 1 switch, 1 menu, 0 prompts.
Changed by: UI-3 (2 to 1 switch).
Back and forth today: The player goes from the editor to My Plates and its card menu for one action on the Plate that is already open.

**Today**

1. Start: an editor is open on the Plate.
2. [Editor] Save first. Export reads the saved Plate, and nothing warns about unsaved edits (Services/Packages/PlatePackageService.cs:70-76). No count.
3. [Editor to My Plates] My Plates button: S1 (Windows/EditorActionBar.cs:89-92; Plugin.cs:489-493).
4. [My Plates, card menu] Right-click the card: M1. Export (Windows/PlateLibraryWindow.Actions.cs:158-161).
5. [Save dialog] Dalamud's save dialog opens with a suggested name: S2. Conditional: Replace File? when the file exists. The footer says Exported (Windows/PlateLibraryWindow.Packages.cs:44-82, 96-138).

**After all twelve tasks**

1. [Editor, Plate menu] Click the Plate's name: M1. Export. Conditional: 'Save and continue, Use last saved, Cancel' when there are unsaved edits (UI-3).
2. [Save dialog] S1. It closes back to the editor. Conditional: Replace File?, drawn in the editor by the prompt host (UI-3).

## J15. Import a .aetherframe file and open the new Plate in an editor.

Frequency (estimated): sometimes. Today: 5 switches, 0 menus, 1 prompt. After: 4 switches, 0 menus, 1 prompt.
Changed by: UI-9 (5 to 4 switches).
Back and forth today: The player goes from My Plates to the file dialog and Import Plate, back to My Plates, then to the editor: Import Plate already shows the Plate but can't open it.

**Today**

1. [Chat to My Plates] /af: S1 (Plugin.cs:250, 483).
2. [My Plates to file dialog] Import opens the Import Plate file dialog, which My Plates draws: S2 (Windows/PlateLibraryWindow.cs:219, 267-270; Windows/PlateLibraryWindow.Packages.cs:35-42).
3. [File dialog to Import Plate] Choose the file. The Import Plate window opens, checks the file and previews the Plate: S3 (Windows/PackageImportWindow.cs:63-75, 145-175).
4. [Import Plate] Import as New Plate: P1 (PackageImportWindow.cs:262-277).
5. [Import Plate to My Plates] On success, Import Plate closes itself and sets My Plates open, with the new card selected: S4 (PackageImportWindow.cs:319-324; PlateLibraryWindow.Packages.cs:26-33).
6. [My Plates to editor] Double-click the new card: S5. Conditional: the one-time Basic suggestion, if it opens in Advanced (Windows/PlateLibraryWindow.cs:394-397; Windows/PlateLibraryWindow.Actions.cs:192-199, 235-310).

**After all twelve tasks**

1. [Chat to My Plates] /af: S1.
2. [My Plates to file dialog] Import: S2.
3. [File dialog to Import Plate] Choose the file: S3. After a rejection, Choose File picks another file in the same window (UI-9).
4. [Import Plate] Import as New Plate: P1.
5. [Import Plate to editor] The success state says the Plate isn't Active and offers Open in Editor, View and Done. Open in Editor: S4 (UI-9). Conditional: Unsaved Changes, asked in the editor that holds them (UI-3), and the one-time Basic suggestion.

## J16. Find and reopen a Plate, starting from /af.

Frequency (estimated): every session. Today: 2 switches, 0 menus, 0 prompts. After: 2 switches, 0 menus, 0 prompts.
Back and forth today: Every way back into a Plate goes through My Plates, and /af closes My Plates when it sits open behind an editor.

**Today**

1. [Chat to My Plates] /af toggles My Plates open: S1 (Plugin.cs:250, 483; Services/Commands/AetherFrameCommand.cs:43-54). Had My Plates been open behind an editor, /af would close it instead.
2. [My Plates] Search Plates filters the cards. No card marks the Plate last opened (Windows/PlateLibraryWindow.cs:276-279, 433-441; Windows/PlateLibraryWindow.Actions.cs:78-84). No count.
3. [My Plates to editor] Double-click the card. It opens in the editor its content suits: S2. Conditional: the one-time Basic suggestion before a first Advanced editor (PlateLibraryWindow.cs:394-397; PlateLibraryWindow.Actions.cs:192-225, 235-310, 339-351).

**After all twelve tasks**

1. [Chat to My Plates] /af: S1. /af now raises My Plates instead of closing it when it isn't the last window focused (UI-8).
2. [My Plates] Search. Continue Editing marks the Plate still loaded (UI-8); the Editing pill shows only while an editor is open. No count.
3. [My Plates to editor] Double-click, or Continue Editing: S2.

## J17. Try a variation of the open Plate without losing the original.

Frequency (estimated): sometimes. Today: 2 switches, 1 menu, 0 prompts. After: 0 switches, 1 menu, 0 prompts.
Changed by: UI-3 (2 to 0 switches).
Back and forth today: The player goes from the editor to My Plates for Duplicate, then back to the editor on the copy, and edits already made can't come along.

**Today**

1. Start: an editor is open on the Plate, which is saved.
2. [Editor] There is no Save a Copy. Duplicate copies only the saved state, so a variation has to start from a saved Plate, and edits already made can't be forked (Windows/EditorActionBar.cs:82-201; Services/Plates/PlateLibraryService.cs:812-824). No count.
3. [Editor to My Plates] My Plates button: S1 (EditorActionBar.cs:89-92).
4. [My Plates, card menu] Right-click the card: M1. Duplicate. 'Name Copy' appears after the source, selected but not opened (Windows/PlateLibraryWindow.Actions.cs:143-148).
5. [My Plates to editor] Double-click the copy. The editor opens on the copy, and the original's undo history is dropped: S2. Conditional: Unsaved Changes, if the original has unsaved edits. Save overwrites the original, and Discard loses the edits (Windows/PlateLibraryWindow.cs:394-397; PlateLibraryWindow.Actions.cs:205-225, 375-432).

**After all twelve tasks**

1. [Editor, Plate menu] Click the Plate's name: M1. Save a Copy writes the live document, unsaved edits included, as a new Plate after the source. The copy is never Active, and the editor continues on it. The original keeps its last save. This counts 0 (UI-3).

## J18. Delete the Plate that is open in the editor.

Frequency (estimated): rare. Today: 1 switch, 1 menu, 1 prompt. After: 1 switch, 1 menu, 1 prompt.
Back and forth today: The open Plate can be deleted only from My Plates, which leaves the editor behind on an empty state.

**Today**

1. Start: an editor is open on the Plate.
2. [Editor to My Plates] The editor has no Delete. My Plates button: S1 (Windows/EditorActionBar.cs:89-92; Plugin.cs:489-493).
3. [My Plates, card menu] Right-click the open Plate's card: M1. Delete (Windows/PlateLibraryWindow.Actions.cs:174-181).
4. [Delete prompt] It warns that the Plate is open in the editor. Delete: P1. The Plate goes to a trash folder, and nothing in the interface restores it (PlateLibraryWindow.Actions.cs:491-568, 513, 532-537).
5. [Editor, behind] The editor stays open on 'No Plate is open.' with an Open My Plates button (Services/ProfileService.cs:699-707; Windows/BasicProfileEditorWindow.cs:233-244; Windows/ProfileEditorWindow.cs:227-238). No count.

**After all twelve tasks**

1. [Editor, Plate menu] Click the Plate's name: M1. Delete (UI-3).
2. [Delete prompt] The prompt host draws it in the editor: P1.
3. [Editor to My Plates] The editor closes, and My Plates is shown, as the prompt promises: S1, an automatic move (UI-3).

## J19. Get help later, from the viewer or Import Plate.

Frequency (estimated): rare. Today: 1 switch, 1 menu, 0 prompts. After: 0 switches, 2 menus, 0 prompts.
Changed by: UI-7 (1 to 0 switches).
Back and forth today: The viewer and Import Plate have no Help, so the player leaves them for My Plates to find it.

**Today**

1. Start: the Plate Viewer shows a Plate, and no other AetherFrame window is open.
2. [Plate Viewer] There is no Help. Its right-click holds only sizes (Windows/ProfileViewWindow.cs:283-288, 302-342). No count.
3. [Chat to My Plates] /af: S1 (Plugin.cs:250, 483). From Import Plate, which also has no Help, My Plates is already open behind it, so the player clicks it; /af would close it (Windows/PackageImportWindow.cs:145-175).
4. [My Plates to Help] The ? button: M1. Help offers Resume, Start over or Take the tour again, and lists the shortcuts and commands. The tutorial card it opens is not a switch. Jump to a chapter is a submenu, +1 menu (Windows/PlateLibraryWindow.cs:281-285; Windows/Tutorial/HelpMenu.cs:54-71, 117-206, 172-187).

**After all twelve tasks**

1. [Plate Viewer, its menu] Right-click the viewer: M1. The menu holds the Plate menu, the sizes, 'How to move and resize' and Help (UI-7).
2. [Help] M2. Nothing moves to another window. From Import Plate, its own Help (UI-10) gives 0 switches and 1 menu.
