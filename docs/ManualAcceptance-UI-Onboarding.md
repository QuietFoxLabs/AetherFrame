# Manual acceptance: the UI redesign and the interactive tutorial

These checks need a running game. Nothing about appearance, input capture, spotlight alignment or UI scaling is claimed as verified until someone has done them in FFXIV; the automated suite (`dotnet test`) covers the tutorial's logic, geometry and preferences, not what it looks like.

**Setup.** Windows, FFXIV with Dalamud (API 15), the Release build installed as a dev plugin (see [Testing.md](Testing.md)). Keep `/xllog` open. Two data folders are needed: a copy of a real v0.1.6 folder (`%AppData%\XIVLauncher\pluginConfigs\AetherFrame`, with Plates) and an empty one. Hash the real folder before starting and compare after section A: loading, the tutorial and the redesign must never change a Plate, Template, image or binding file.

"Not offered" below means the Welcome to AetherFrame window never appears on its own during the session. The tutorial is never offered on its own: it starts from Help in My Plates.

## A. Existing installs are never treated as new

1. **Upgrade from v0.1.6.** Load with the real folder. My Plates lists every Plate, the log shows `AetherFrame tutorial: ExistingInstall`, the offer is not shown, no file in the folder changed except `AetherFrame.json`, which gained a `Tutorial` block with `"Install": 2`.
2. **Pre-0.1.6 shape.** Delete `AetherFrame.json` from a copy of the real folder (Plates remain). Load: `ExistingInstall`, not offered.
3. **Damaged configuration.** Replace `AetherFrame.json` with `{` and load: the plugin loads, the log warns about the configuration, the tutorial line reads `AlreadyDecided (install ExistingInstall …)`, not offered, and the rewritten `AetherFrame.json` holds `"Install": 2`.
4. **Library failed to load.** In a copy with `AetherFrame.json` deleted (the pre-0.1.6 shape), make the `Profiles` folder unreadable (deny permissions) and load: My Plates reports the failure, the log shows `Undetermined`, not offered, and `AetherFrame.json` (written for the guidance flag) holds `"Install": 3`, the pending kind, which a later launch decides from the Library. Restore permissions and load again: `ExistingInstall`, because the copy has Plates.
5. **Second launch of an existing install.** Load again: `AlreadyDecided`, nothing written.

## B. A new install is welcomed with guided creation, once, politely

Since guided creation became the introductory route, a new player is offered it, not the tutorial; the tutorial starts from Help (section C starts it there). The guided steps themselves have their own checklist in the onboarding pull request.

6. **Empty folder.** Load with the empty folder and log in: the Welcome to AetherFrame window appears centered once the Library has loaded and a character is logged in, with Create My First Plate (accent), Not Now and Don't Show Again. The log shows `OfferTutorial` and `welcome waits`. Loaded at the title screen, it waits for the login.
7. **Close without answering** (X or Escape): it disappears and My Plates, still empty, offers Create My First Plate; reload: welcomed again; after the third showing it is not shown again.
8. **Not Now.** The same as closing it: My Plates offers Create My First Plate, no reminder row; reload: welcomed again, within the same three showings.
9. **Don't Show Again.** Reload: not welcomed, no reminder; Help still offers Create Step by Step and the full tutorial.
10. **Create My First Plate**: the welcome closes, an Adventure Plate Classic is made and opens in the Basic editor on step 1, Choose a look.
10b. **Recovery first.** With kept unsaved changes waiting for a Plate that is no longer in My Plates (so My Plates is empty), load: the "Unsaved changes kept" window comes first, and the welcome appears only once it has been answered (Decide Later counts). Recovery checkpoints join this once crash recovery (#141) is in.
10c. **Made another way.** With the welcome open, make a Plate from My Plates' Create Plate: the welcome closes. With it open again on a new install, log out: it closes, comes back after the next login, and that showing isn't counted twice.

## C. The spotlight

11. **Following a window.** In chapter 2 (Your Plate Library) with the Create Plate step showing, drag My Plates around the screen and resize it: the spotlight stays exactly on the Create Plate button (no lag beyond a frame, no drift), the card moves to stay beside it and never leaves the screen.
12. **Edges.** Move My Plates so the button is near the right edge, the bottom edge and a corner: the card flips to the side with room; with the window off the bottom, the card goes above.
13. **UI scale.** Repeat 11 at Dalamud global scale 150 % and 200 %: the hole hugs the control with the same margin, the card text wraps inside the card, nothing overlaps.
14. **Dimming blocks clicks.** On an Inspect step (the My Plates step), click the Import button, a card, the search field and the title bar of the Advanced editor behind the dim: nothing happens, the dimmed window is not brought forward, no popup opens; the card gains focus (its border brightens) after the click.
14b. **Nothing to point at.** Reach a step whose control isn't there (on the Size and spacing step, deselect the text element by clicking an empty part of the canvas; or cancel the Create Plate chooser on the Choose a Template step): the dim lightens, every control underneath is clickable again, the card says what to do and points at the way there (+ Text; Create Plate), and the step recovers by itself once the control is back (the element selected again on the canvas or in Layers; Create Plate clicked again). A section the tour points into cannot be collapsed by hand: it reopens on the next frame, which is intended.
15. **The hole is live on Interact steps.** In chapter 3 (Your First Plate), click Create Plate through the spotlight: the chooser opens and the card moves on by itself. Cancel the chooser: the tour stays on the Choose a Template step (it says the chooser is expected) and Back returns to Create Plate.
16. **The hole is inert on Inspect steps.** On the Import step, click Import inside the spotlight: nothing opens.
17. **Scrolling and tabs.** In the Advanced editor, shrink the window until the Inspector scrolls, then reach the Size and spacing step: the Inspector scrolls the Size slider into view within a frame or two, the Typography section is open, and if the Canvas tab was selected the Element tab comes forward. Click the Canvas tab by hand during that step: the Element tab comes straight back. Reach the Background step: the Canvas tab comes forward and the Background section is open.
18. **Missing windows.** Close My Plates during chapter 2: the card explains how to open it and offers Open My Plates; the button brings My Plates back and the step resumes with its spotlight.
19. **Keyboard.** With the card focused, Right / Enter advance and Left goes back; with a text field focused in the editor, typing and arrow keys behave normally and the tour does not advance.
20. **Focus.** Click a text field inside the spotlight (the text content step): typing works; the editor window did not come in front of the dim; clicking the dim afterwards does not type into the field.
21. **Popups over the dim.** Open the Create Plate chooser during the tour: the chooser draws above the dim and is usable; the card is not clickable while the modal is open (ImGui's rule) and becomes clickable when it closes.
22. **Other plugins and the game.** With another plugin's window open beside (not over) AetherFrame's during the tour: it is neither dimmed nor blocked, and the game's hotbars and chat outside AetherFrame's windows work as usual; only the area AetherFrame's open windows occupy is dimmed. Move the other plugin's window over that area: it is dimmed and blocked with it (a known limit).
22b. **Escape.** With the card focused, Escape closes the tour (Help then offers Resume); with the game's chat focused, Enter sends the chat line and does not page the tour.
23. **Leaving.** The card's X closes the tour; Help shows Resume Tutorial and resumes at the same step. Skip tour closes it; Help shows Start Tutorial. Finish on the last step returns to My Plates and Help shows the tutorial as completed.
24. **Reload mid-tour.** Disable and enable the plugin during chapter 5: after enabling, Help offers Resume Tutorial at chapter 5.
25. **Nothing changed but what you did.** After the whole tour (without saving anything on purpose), compare the data folder's hash: only `AetherFrame.json` differs, plus the one Plate that chapter 3 had you create yourself through Create Plate (a new file under `Profiles` and, if it became the character's first Plate, that character's binding). Nothing else was written, and no existing file changed.

## D. The redesign

26. **Recognizable.** Open My Plates, both editors, the Plate Viewer and the Import Preview side by side with another plugin's window: every AetherFrame window shares the same midnight surfaces, rounding, spacing and accent; My Plates opens with the brand row (the mark and the title in the Axis face); none of them changed the other plugin's look.
27. **My Plates.** Create Plate is the one primary (accent-filled) button; Import, search and the Help button sit with it; an empty library shows the empty state with Create My First Plate (guided creation); the selected card shows the accent ring with the frame corners; the Active badge is gold; right-click menus are unchanged in content.
28. **Basic editor.** The selected category's title is a small accent label with a rule, its summary lines are in the secondary tone, and each group inside the category (Theme, Portrait, Name…) has the same small accent label; the navigator's selected row is the accent; sliders and choices keep the tooltips they had; the live Plate renders exactly as in v0.1.6 (compare a screenshot of the same Plate).
29. **Advanced editor.** Layers, canvas and Inspector share the themed chrome and read as one workspace; the action bar ends with the Help button; the Inspector's collapsible sections and their tooltips are unchanged in content; the canvas rendering of a saved Plate is identical to v0.1.6.
30. **Prompts.** The unsaved-changes, open-another-Plate, revert, rename and delete prompts use the shared button row: the destructive choice red, Save or Rename accent, Cancel a quiet ghost; Escape and Enter behave as before.
31. **The Plate Viewer** (View, and both editors' Preview) shows only the Plate over the game, as before; the close control is unchanged.
32. **Scale.** Everything above at 100 %, 150 % and 200 %.
33. **Performance.** With the tour running and My Plates holding 100 Plates, the frame time does not visibly change when the tour is closed versus open (compare with a frame-time overlay); no per-frame GC spikes in the log's memory counters.
34. **Fonts.** Headings appear in the game's Axis face within a second of loading; before that they draw in the default font with no error in the log.

Record each step as pass or fail with the build shown by `/af version`. Anything failing in sections A, B or C.14–C.16 blocks the release.
