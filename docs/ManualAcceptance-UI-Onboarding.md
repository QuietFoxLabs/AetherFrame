# Manual acceptance: the UI redesign and the interactive tutorial

These checks need a running game. Nothing about appearance, input capture, spotlight alignment or UI scaling is claimed as verified until someone has done them in FFXIV; the automated suite (`dotnet test`) covers the tutorial's logic, geometry and preferences, not what it looks like.

**Setup.** Windows, FFXIV with Dalamud (API 15), the Release build installed as a dev plugin (see [Testing.md](Testing.md)). Keep `/xllog` open. Two data folders are needed: a copy of a real v0.1.6 folder (`%AppData%\XIVLauncher\pluginConfigs\AetherFrame`, with Plates) and an empty one. Hash the real folder before starting and compare after section A: loading, the tutorial and the redesign must never change a Plate, Template, image or binding file.

"Not offered" below means the Welcome to AetherFrame window never appears on its own during the session.

## A. Existing installs are never treated as new

1. **Upgrade from v0.1.6.** Load with the real folder. My Plates lists every Plate, the log shows `AetherFrame tutorial: ExistingInstall`, the offer is not shown, no file in the folder changed except `AetherFrame.json`, which gained a `Tutorial` block with `"Install": 2`.
2. **Pre-0.1.6 shape.** Delete `AetherFrame.json` from a copy of the real folder (Plates remain). Load: `ExistingInstall`, not offered.
3. **Damaged configuration.** Replace `AetherFrame.json` with `{` and load: the plugin loads, the log warns about the configuration, not offered.
4. **Library failed to load.** Make the `Profiles` folder unreadable (deny permissions) and load: My Plates reports the failure, the log shows `Undetermined`, not offered, and `AetherFrame.json` gains no `Tutorial` block. Restore permissions.
5. **Second launch of an existing install.** Load again: `AlreadyDecided`, nothing written.

## B. A new install is offered the tutorial, once, politely

6. **Empty folder.** Load with the empty folder: the Welcome to AetherFrame window appears centered once the Library has loaded, with Start Tutorial (accent), Maybe Later and Do Not Show Again. The log shows `OfferTutorial`.
7. **Close without answering** (X or Escape): it disappears; reload: offered again; after the third unanswered showing it is not offered again and My Plates shows the quiet "Take the tour" reminder instead.
8. **Maybe Later.** Reload: not offered; the reminder is in My Plates; its dismiss removes it for good; Help still offers Start Tutorial.
9. **Do Not Show Again.** Reload: not offered, no reminder; Help still offers Start Tutorial.
10. **Start Tutorial**: the offer closes and the first card appears in the middle of the screen with the interface dimmed.

## C. The spotlight

11. **Following a window.** In chapter 2 (Your Plate Library) with the Create Plate step showing, drag My Plates around the screen and resize it: the spotlight stays exactly on the Create Plate button (no lag beyond a frame, no drift), the card moves to stay beside it and never leaves the screen.
12. **Edges.** Move My Plates so the button is near the right edge, the bottom edge and a corner: the card flips to the side with room; with the window off the bottom, the card goes above.
13. **UI scale.** Repeat 11 at Dalamud global scale 150 % and 200 %: the hole hugs the control with the same margin, the card text wraps inside the card, nothing overlaps.
14. **Dimming blocks clicks.** On an Inspect step (the My Plates step), click the Import button, a card, the search field and the title bar of the Advanced editor behind the dim: nothing happens, the dimmed window is not brought forward, no popup opens.
15. **The hole is live on Interact steps.** In chapter 3 (Your First Plate), click Create Plate through the spotlight: the chooser opens and the card moves on by itself. Cancel the chooser: the tour stays on the Choose a Template step (it says the chooser is expected) and Back returns to Create Plate.
16. **The hole is inert on Inspect steps.** On the Import step, click Import inside the spotlight: nothing opens.
17. **Scrolling.** In the Advanced editor, shrink the window until the Inspector scrolls, then reach the Size and spacing step: the Inspector scrolls the Size slider into view within a frame or two; if the Element tab isn't selected the tab is selected. Collapse the Text section by hand: the card says the control isn't in view and Next still works.
18. **Missing windows.** Close My Plates during chapter 2: the card explains how to open it and offers Open My Plates; the button brings My Plates back and the step resumes with its spotlight.
19. **Keyboard.** With the card focused, Right / Enter advance and Left goes back; with a text field focused in the editor, typing and arrow keys behave normally and the tour does not advance.
20. **Focus.** Click a text field inside the spotlight (the text content step): typing works; the editor window did not come in front of the dim; clicking the dim afterwards does not type into the field.
21. **Popups over the dim.** Open the Create Plate chooser during the tour: the chooser draws above the dim and is usable; the card is not clickable while the modal is open (ImGui's rule) and becomes clickable when it closes.
22. **Other plugins.** With another plugin's window open during the tour: it is neither dimmed nor blocked (only AetherFrame's windows are).
23. **Leaving.** The card's X closes the tour; Help shows Resume Tutorial and resumes at the same step. Skip tour closes it; Help shows Start Tutorial. Finish on the last step returns to My Plates and Help shows the tutorial as completed.
24. **Reload mid-tour.** Disable and enable the plugin during chapter 5: after enabling, Help offers Resume Tutorial at chapter 5.
25. **Nothing changed.** After the whole tour (without saving anything on purpose), compare the data folder's hash: only `AetherFrame.json` differs.

## D. The redesign

26. **Recognizable.** Open My Plates, both editors, the Plate Viewer and the Import Preview side by side with another plugin's window: every AetherFrame window shares the same midnight surfaces, the accent, the title row with the mark, and the same button styles; none of them changed the other plugin's look.
27. **My Plates.** Create Plate is the one primary button; Import, search and Help sit with it; an empty library shows the empty state with Create Your First Plate; the selected card shows the accent ring with the frame corners; the Active badge is gold; right-click menus are unchanged in content.
28. **Basic editor.** Every category's controls are grouped under section labels; each slider and choice has a tooltip with units where they apply; the navigator's selected row is the accent; the live Plate renders exactly as in v0.1.6 (compare a screenshot of the same Plate).
29. **Advanced editor.** Layers, canvas and Inspector read as one workspace; the selected element is obvious in Layers and on the canvas; the Inspector's sections carry the section labels; every property has a tooltip; the canvas rendering of a saved Plate is identical to v0.1.6.
30. **Prompts.** The unsaved-changes, revert, delete and rename prompts use the shared button row: red for the destructive choice, Cancel ghost; Escape and Enter behave as before.
31. **Clean Preview and the Plate Viewer** show only the Plate over the game, as before; the close control is unchanged.
32. **Scale.** Everything above at 100 %, 150 % and 200 %.
33. **Performance.** With the tour running and My Plates holding 100 Plates, the frame time does not visibly change when the tour is closed versus open (compare with a frame-time overlay); no per-frame GC spikes in the log's memory counters.
34. **Fonts.** Headings appear in the game's Axis face within a second of loading; before that they draw in the default font with no error in the log.

Record each step as pass or fail with the build shown by `/af version`. Anything failing in sections A, B or C.14–C.16 blocks the release.
