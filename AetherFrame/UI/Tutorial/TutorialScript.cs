using System.Collections.Generic;
using System.Linq;

namespace AetherFrame.UI.Tutorial;

/// <summary>
/// The first-time tutorial: fourteen short chapters over the controls that exist, in the words the
/// interface uses (twelve in a player build, which has no sharing to teach; see
/// <see cref="ForBuild"/>). Content only; the session (<see cref="TutorialSession"/>) decides how
/// each step shows. Every target here is a control a window marks (<see cref="TutorialTarget"/>);
/// a step whose control is off screen says so rather than pointing at nothing, and a step that
/// needs an editor open explains how to get there and offers to open it. No step creates, changes,
/// saves, imports, exports or deletes anything on the player's behalf, and none sends anything:
/// the sharing chapters point at sharing's controls and explain them, and leave every one of them
/// to the player.
///
/// <para>Bump <see cref="Version"/> when the chapters change enough that a player who completed
/// the old tour should see Help say the tutorial was updated. Authoring conventions:
/// docs/DesignGuide.md, "The tutorial", under "Authoring conventions".</para>
/// </summary>
internal static class TutorialScript
{
    internal const int Version = 3;

    private const string OpenAPlate = "Open a Plate first: double-click one in My Plates, or use Create Plate. Then this chapter continues on its own.";
    private const string SwitchToBasic = "Switch to the Basic Editor: the Basic | Advanced switch at the top of the editor moves this Plate between the two without losing anything.";
    private const string SwitchToAdvanced = "Switch to the Advanced Editor: the Basic | Advanced switch at the top of the editor moves this Plate between the two without losing anything.";
    private const string OpenMyPlates = "Open My Plates to continue: the button below brings it forward.";
    private const string OpenSharing = "Open the Sharing window to continue: click Sharing at the top of My Plates. The button below brings My Plates forward.";

    /// <summary>Every chapter, as the sharing build shows them.</summary>
    internal static readonly IReadOnlyList<TutorialChapter> Chapters =
    [
        new TutorialChapter("welcome", "Welcome to AetherFrame", "What AetherFrame is, what a Plate is, and the two ways to edit one.",
        [
            new TutorialStep("welcome.intro", "Welcome to AetherFrame",
                "AetherFrame designs Plates: character cards that start from the familiar shape of the in-game Adventure Plate and can grow into anything you like.\n\nThis short tour points at the real controls as you go. Use Next and Back, or Skip whenever you want to explore on your own.",
                Mode: TutorialStepMode.Narrative),
            new TutorialStep("welcome.local", "Your Plates stay on your PC",
                "Plates, Templates and the images you add are saved in AetherFrame's own folder inside Dalamud's configuration. There is no account. Nothing goes to AetherFrame's server unless you turn on sharing for a character in My Plates' Sharing window, and an Art Style's artwork downloads from GitHub the first time you use it. To give someone a Plate as a file, export it.",
                Mode: TutorialStepMode.Narrative),
            new TutorialStep("welcome.modes", "Two ways to edit",
                "The Basic Editor feels like FFXIV: fill in sections and AetherFrame lays them out like an Adventure Plate.\n\nThe Advanced Editor removes the restrictions: place text, images and decorative Components anywhere on a freeform canvas.\n\nBoth edit the same Plate, so you can switch at any time.",
                Mode: TutorialStepMode.Narrative),
        ]),

        new TutorialChapter("library", "My Plates", "Where Plates live, how to create and open one, and how several Plates work together.",
        [
            new TutorialStep("library.home", "My Plates",
                "This is My Plates, AetherFrame's home: every Plate you have saved, as a card. It opens with /af, and every editor has a button back to it.",
                TutorialTarget.LibraryHeader, Requires: TutorialCondition.MyPlatesOpen, FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("library.create", "Create Plate",
                "Create Plate starts a new Plate from a Template. Adventure Plate Classic gives you the familiar layout for the Basic Editor; Blank Canvas starts empty for freeform work in the Advanced Editor.",
                TutorialTarget.LibraryCreatePlate, Requires: TutorialCondition.MyPlatesOpen, FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("library.grid", "Your Plates",
                "Each card is one saved Plate, shown as it looks. Double-click a card to edit it, or right-click it for everything else: View, open in either editor, Set Active, Duplicate, Save as Template, Export, Rename and Delete. Drag cards to reorder them.",
                TutorialTarget.LibraryPlateGrid, Requires: TutorialCondition.MyPlatesOpen, FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("library.card", "A saved Plate",
                "This is one of your Plates. The gold Active badge marks the Plate your character currently presents; each character can choose its own. Keep as many Plates as you like and switch the Active one whenever you want.",
                TutorialTarget.LibraryFirstPlateCard, Requires: TutorialCondition.LibraryHasPlates, SkipIfUnmet: true),
            new TutorialStep("library.search", "Finding a Plate",
                "With many Plates, type here to filter the cards by name. Clear the search to reorder cards again.",
                TutorialTarget.LibrarySearch, Requires: TutorialCondition.MyPlatesOpen, FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("library.help", "Help is always here",
                "The Help menu reopens this tutorial, lets you jump to a chapter, and lists the commands and keyboard shortcuts.",
                TutorialTarget.LibraryHelp, Requires: TutorialCondition.MyPlatesOpen, FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates),
        ]),

        new TutorialChapter("first-plate", "Your First Plate", "Creating a Plate from a Template and finding your way around the editor.",
        [
            new TutorialStep("first.create", "Start a new Plate",
                "Click Create Plate. A chooser opens with the built-in Templates and any you saved yourself.\n\nIf you'd rather keep a Plate you already have, double-click its card instead; the tour continues either way.",
                TutorialTarget.LibraryCreatePlate, TutorialStepMode.Interact, Requires: TutorialCondition.MyPlatesOpen, AdvanceWhen: TutorialCondition.CreatingOrEditingPlate,
                FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates,
                WaitsForAction: true, WaitHint: "Click Create Plate to continue, or double-click a Plate you already have."),
            new TutorialStep("first.template", "Choose a Template",
                "Pick a Template, then choose Use Template. Adventure Plate Classic is the best start: it opens in the Basic Editor with every familiar section in place. The new Plate takes the Template's name; rename it any time from the Plate menu at the top of the editor, whose New Plate... opens this chooser again.\n\nThe tour continues as soon as the editor opens.",
                TutorialTarget.LibraryTemplateChooser, TutorialStepMode.Interact, Requires: TutorialCondition.TemplateChooserOpen, AdvanceWhen: TutorialCondition.AnyEditorOpen,
                SkipIfUnmet: true, FallbackBody: "The chooser was closed. Click Create Plate to open it again: the tour continues once your new Plate opens.", FallbackTarget: TutorialTarget.LibraryCreatePlate,
                WaitsForAction: true, WaitHint: "Create your Plate to continue: pick a Template and choose Use Template. If the chooser was closed, click Create Plate to open it again."),
            new TutorialStep("first.workspace", "The editor",
                "A Plate is open. At the top of every editor: the way back to My Plates, the Basic | Advanced switch, the Plate menu under the Plate's name, Undo and Redo, and on the right whether it is saved, plus Preview, Revert and Save.",
                TutorialTarget.EditorModeSwitch, Requires: TutorialCondition.AnyEditorOpen, FallbackBody: OpenAPlate, FallbackAction: TutorialAction.OpenMyPlates),
        ]),

        new TutorialChapter("basic", "Basic editing", "The guided editor: sections on the left, the live Plate on the right.",
        [
            new TutorialStep("basic.navigator", "Style, Portrait, Identity, Details, Message",
                "The Basic Editor is organised into Style, Portrait, Identity, Details and Message. Pick one here to edit it. A marker at the right of a row says when something needs attention or was customized in the Advanced Editor.",
                TutorialTarget.BasicNavigator, Requires: TutorialCondition.BasicEditorOpen, FallbackBody: SwitchToBasic, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenBasicEditor),
            new TutorialStep("basic.inspector", "Editing a section",
                "The controls of whatever you picked appear here, under its name and a short summary. Everything you change shows on the Plate immediately, and every change can be undone.",
                TutorialTarget.BasicInspector, Requires: TutorialCondition.BasicEditorOpen, FallbackBody: SwitchToBasic, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenBasicEditor),
            new TutorialStep("basic.preview", "The live Plate",
                "This is your Plate as it will look. Click a part of it to jump to that section. Use the zoom buttons above it to look closer; at 150% and 200% you can scroll or drag to look around.",
                TutorialTarget.BasicPreview, Requires: TutorialCondition.BasicEditorOpen, FallbackBody: SwitchToBasic, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenBasicEditor),
            new TutorialStep("basic.style", "Style",
                "Style is the fastest way to a good-looking Plate: pick a Theme. An Art Style sets the background artwork, frames, decorations and text colours together; a Simple Theme sets the colours. Pattern and Customize Background (colour, gradient and image) show while no background artwork covers the Plate.",
                TutorialTarget.BasicNavigatorStyle, TutorialStepMode.Interact, Requires: TutorialCondition.BasicEditorOpen, FallbackBody: SwitchToBasic, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenBasicEditor),
            new TutorialStep("basic.identity", "Identity and details",
                "Identity holds your character's name and title. Details holds Home World, Favorite Jobs, Free Company, Playstyle and Active Hours; Message is free text. Each section of the Plate can be hidden without losing what you typed.",
                TutorialTarget.BasicNavigatorIdentity, TutorialStepMode.Interact, Requires: TutorialCondition.BasicEditorOpen, FallbackBody: SwitchToBasic, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenBasicEditor),
        ]),

        new TutorialChapter("advanced", "Advanced editing", "The freeform canvas: tools, layers, the canvas and the Inspector.",
        [
            new TutorialStep("advanced.switch", "Removing the restrictions",
                "The Advanced Editor edits the same Plate with no layout rules. Switch here whenever Basic isn't enough; switch back any time, and your unsaved changes and undo history come along.",
                TutorialTarget.EditorModeSwitch, Requires: TutorialCondition.AnyEditorOpen, FallbackBody: OpenAPlate, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("advanced.toolbar", "Tools",
                "The toolbar adds content: + Text adds a text element, + Image imports a picture from your PC. Guides shows element bounds and handles on the canvas; Snap aligns elements to each other and the canvas edges while you move them.",
                TutorialTarget.AdvancedToolbar, Requires: TutorialCondition.AdvancedEditorOpen, FallbackBody: SwitchToAdvanced, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenAdvancedEditor),
            new TutorialStep("advanced.layers", "Layers",
                "Every element on the Plate is a layer, front to back. Click a layer to select it, drag to reorder, and use the eye and lock to hide or protect one while you work on others.",
                TutorialTarget.AdvancedLayers, Requires: TutorialCondition.AdvancedEditorOpen, FallbackBody: SwitchToAdvanced, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenAdvancedEditor),
            new TutorialStep("advanced.canvas", "The canvas",
                "The canvas is the Plate itself. Click an element to select it, drag to move, use the corner handles to resize. Scroll to zoom, middle-drag to pan, press F to fit the whole Plate in view.",
                TutorialTarget.AdvancedCanvas, Requires: TutorialCondition.AdvancedEditorOpen, FallbackBody: SwitchToAdvanced, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenAdvancedEditor),
            new TutorialStep("advanced.inspector", "The Inspector",
                "The Inspector shows what is selected: the Element tab for a text or image element; the Canvas tab for the Plate itself, with its size, its background, and its Components (frames, dividers and ornaments).",
                TutorialTarget.AdvancedInspector, Requires: TutorialCondition.AdvancedEditorOpen, FallbackBody: SwitchToAdvanced, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenAdvancedEditor),
            new TutorialStep("advanced.zoom", "Zoom and status",
                "Zoom in and out here, or pick a preset from the percentage. Fit keeps the whole Plate in view as the window changes. The status line shows the canvas size, the element count and what's selected.",
                TutorialTarget.AdvancedZoom, Requires: TutorialCondition.AdvancedEditorOpen, FallbackBody: SwitchToAdvanced, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenAdvancedEditor),
        ]),

        new TutorialChapter("text", "Text and typography", "Adding text and styling it: font, size, color and more.",
        [
            new TutorialStep("text.add", "Add text",
                "Click + Text to add a text element. It appears on the canvas, selected, ready to type into.\n\nIf you already have a text element, select it instead; the tour continues either way.",
                TutorialTarget.AdvancedAddText, TutorialStepMode.Interact, Requires: TutorialCondition.AdvancedEditorOpen, AdvanceWhen: TutorialCondition.TextElementSelected,
                FallbackBody: SwitchToAdvanced, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenAdvancedEditor,
                WaitsForAction: true, WaitHint: "Click + Text to continue, or select a text element you already have."),
            new TutorialStep("text.content", "What it says",
                "Type the text here. Several lines are fine; Auto Fit, under Typography, can shrink the text to keep it inside its box.",
                TutorialTarget.AdvancedTextContent, Requires: TutorialCondition.TextElementSelected, FallbackBody: "Select a text element to continue: click one on the canvas or in Layers, or add one with + Text.", FallbackTarget: TutorialTarget.AdvancedAddText),
            new TutorialStep("text.font", "Font and style",
                "Choose a font family, then bold, italic, underline or strikethrough (a font without a bold or italic face greys those out). AetherFrame bundles its fonts, so a Plate looks the same on every PC it is shown on.",
                TutorialTarget.AdvancedTextFont, Requires: TutorialCondition.TextElementSelected, FallbackBody: "Select a text element to continue: click one on the canvas or in Layers, or add one with + Text.", FallbackTarget: TutorialTarget.AdvancedAddText),
            new TutorialStep("text.size", "Size and spacing",
                "Size is in pixels on the Plate: the slider runs 6 to 96, and Ctrl+click lets you type any value from 1 to 1024. Letter and line spacing nudge the text tighter or looser.",
                TutorialTarget.AdvancedTextSize, Requires: TutorialCondition.TextElementSelected, FallbackBody: "Select a text element to continue: click one on the canvas or in Layers, or add one with + Text.", FallbackTarget: TutorialTarget.AdvancedAddText),
            new TutorialStep("text.color", "Color, outline and shadow",
                "Pick the text color here. An outline and a shadow keep text readable over busy backgrounds; each has its own color and opacity.",
                TutorialTarget.AdvancedTextColor, Requires: TutorialCondition.TextElementSelected, FallbackBody: "Select a text element to continue: click one on the canvas or in Layers, or add one with + Text.", FallbackTarget: TutorialTarget.AdvancedAddText),
        ]),

        new TutorialChapter("images", "Images and backgrounds", "Adding pictures, and setting the Plate's background.",
        [
            new TutorialStep("images.add", "Add an image",
                "+ Image opens a file picker for a PNG, JPEG or WebP on your PC. AetherFrame copies the file into its own folder, checks it, and shows it as an element you can move and resize. Nothing happens until you choose a file, so there's nothing to do here now.",
                TutorialTarget.AdvancedAddImage, Requires: TutorialCondition.AdvancedEditorOpen, FallbackBody: SwitchToAdvanced, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenAdvancedEditor),
            new TutorialStep("images.canvas-tab", "The Plate's canvas",
                "The Inspector's Canvas tab holds what belongs to the whole Plate rather than one element: its size and its background.",
                TutorialTarget.AdvancedInspectorCanvasTab, TutorialStepMode.Interact, Requires: TutorialCondition.AdvancedEditorOpen, FallbackBody: SwitchToAdvanced, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenAdvancedEditor),
            new TutorialStep("images.background", "Background",
                "A background can be a solid colour, a two-colour gradient at any angle, or an image; a Pattern chosen in the Basic Editor's Style keeps its intensity, scale and rotation here. Opacity lets the game show through.",
                TutorialTarget.AdvancedBackground, Requires: TutorialCondition.AdvancedEditorOpen, FallbackBody: SwitchToAdvanced, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenAdvancedEditor),
        ]),

        new TutorialChapter("layout", "Positioning and layers", "Moving, sizing and ordering elements precisely.",
        [
            new TutorialStep("layout.position", "Position and size",
                "Exact numbers when dragging isn't precise enough: X and Y place the element on the Plate, Width and Height size it; images can also be rotated. Arrow keys nudge the selected element; hold Shift for 10 px steps.",
                TutorialTarget.AdvancedElementPosition, Requires: TutorialCondition.ElementSelected, FallbackBody: "Select an element to continue: click one on the canvas or in Layers.", FallbackTarget: TutorialTarget.AdvancedLayers),
            new TutorialStep("layout.order", "Front and back",
                "Elements paint in layer order. Bring an element forward or send it back here, or drag it in Layers. The Plate frame and other Components have their own place in that order.",
                TutorialTarget.AdvancedLayerOrder, Requires: TutorialCondition.ElementSelected, FallbackBody: "Select an element to continue: click one on the canvas or in Layers.", FallbackTarget: TutorialTarget.AdvancedLayers),
            new TutorialStep("layout.snap", "Snapping",
                "With Snap on, an element you move or resize clicks into line with the canvas edges, its center and other elements. Hold Alt to move freely for a moment.",
                TutorialTarget.AdvancedSnap, Requires: TutorialCondition.AdvancedEditorOpen, FallbackBody: SwitchToAdvanced, FallbackTarget: TutorialTarget.EditorModeSwitch, FallbackAction: TutorialAction.OpenAdvancedEditor),
        ]),

        new TutorialChapter("saving", "Saving your work", "How saving works and where a saved Plate turns up.",
        [
            new TutorialStep("saving.state", "Saved or not",
                "This tells you whether the open Plate has unsaved changes. Nothing is saved on its own: closing the editor with unsaved changes asks you first. If AetherFrame closes before you save (an update, or the game closing), it keeps your changes and offers them back next time.",
                TutorialTarget.EditorSaveState, Requires: TutorialCondition.AnyEditorOpen, FallbackBody: OpenAPlate, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("saving.save", "Save",
                "Save writes the Plate to your PC (Ctrl+S does the same). Revert throws away unsaved changes and returns to the last saved version, after asking; even that can be undone.",
                TutorialTarget.EditorSave, Requires: TutorialCondition.AnyEditorOpen, FallbackBody: OpenAPlate, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("saving.preview", "Preview",
                "Preview opens the finished Plate in the Plate Viewer, over the game, exactly as others see it, and it follows your edits as you make them. Drag it to move it, Ctrl+scroll to resize it, and press Escape or its close button to close it.",
                TutorialTarget.EditorPreview, Requires: TutorialCondition.AnyEditorOpen, FallbackBody: OpenAPlate, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("saving.plate-menu", "The Plate menu",
                "The Plate menu (the card icon and the Plate's name) keeps you in the editor: View shows the Plate over the game, Set Active makes it your character's Active Plate, and Save as New Plate keeps what you see as a new Plate. Save as Template, Export and Rename are here too; Open another Plate and New Plate... take you to another Plate. Set Active, Save as Template and Export use the last saved version, so save first.",
                TutorialTarget.EditorPlateMenu, Requires: TutorialCondition.AnyEditorOpen, FallbackBody: OpenAPlate, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("saving.library", "Back in My Plates",
                "Every saved Plate is also a card in My Plates. Its right-click menu has the same actions, plus Delete.",
                TutorialTarget.EditorMyPlates, Requires: TutorialCondition.AnyEditorOpen, FallbackBody: OpenAPlate, FallbackAction: TutorialAction.OpenMyPlates),
        ]),

        new TutorialChapter("templates", "Templates", "Starting points for new Plates, built in or your own.",
        [
            new TutorialStep("templates.chooser", "Built-in Templates",
                "Every new Plate starts from a Template, chosen in Create Plate here, or with New Plate... in an editor's Plate menu, which opens the same chooser over the editor. Adventure Plate Classic and Blank Canvas are built in and always available.",
                TutorialTarget.LibraryCreatePlate, Requires: TutorialCondition.MyPlatesOpen, FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("templates.own", "Your own Templates",
                "Save as Template, in the editor's Plate menu or a card's right-click menu, reuses a Plate's design. Your Templates appear in Create Plate beside the built-in ones, and Manage Templates (a link at the bottom of the chooser) renames, duplicates or removes them.",
                TutorialTarget.LibraryPlateGrid, Requires: TutorialCondition.MyPlatesOpen, FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates),
        ]),

        new TutorialChapter("sharing", "Import and export", "Sharing a Plate as a file, safely.",
        [
            new TutorialStep("sharing.export", "Export",
                "Export, in the editor's Plate menu or a card's right-click menu, saves a Plate as one .aetherframe file, images included. Give that file to anyone; it contains only that Plate.",
                TutorialTarget.LibraryPlateGrid, Requires: TutorialCondition.MyPlatesOpen, FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("sharing.import", "Import",
                "Import opens an .aetherframe file from your PC. It is checked and previewed first, and always added as a new Plate: nothing you have is ever replaced.",
                TutorialTarget.LibraryImport, Requires: TutorialCondition.MyPlatesOpen, FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates),
        ]),

        // Sharing online. Every step points and explains: the controls that send anything (Turn on
        // sharing, Check, Pause, Turn off, View, Hide, Report) are only ever looked at, behind the
        // spotlight's cover, and the two the player may use from here only open a window. A step
        // whose control belongs to another state of sharing (the consent, once the character
        // shares; the check, before it starts) says its control isn't in view, and Next goes on.
        new TutorialChapter("online", "Sharing online", "Turning sharing on for a character, and how its Active Plate is shared.",
        [
            new TutorialStep("online.open", "Sharing",
                "Sharing lets players who use AetherFrame, and have turned sharing on, view each other's Active Plates, like the game's Adventure Plates. It's off until you turn it on, one character at a time, and the tour never turns it on or off for you.\n\nClick Sharing to open the Sharing window.",
                TutorialTarget.LibrarySharing, TutorialStepMode.Interact, Requires: TutorialCondition.MyPlatesOpen, AdvanceWhen: TutorialCondition.SharingWindowOpen,
                FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates,
                WaitsForAction: true, WaitHint: "Click Sharing to continue: it opens the Sharing window."),
            new TutorialStep("online.window", "The Sharing window",
                "This window shows the sharing of the character you're logged in as: log in to one to turn its sharing on or off. Each character shares on its own, and what other players see of it is its Active Plate. When something happens, such as a check passing or a Plate being shared, this window says so.",
                TutorialTarget.SharingWindow, Requires: TutorialCondition.SharingWindowOpen, FallbackBody: OpenSharing, FallbackTarget: TutorialTarget.LibrarySharing, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("online.consent", "Turning sharing on",
                "For a character that doesn't share yet, the window first lists what sharing means: what other players see, what the server keeps, and what turning it off deletes. Tick the box only if you agree, then press Turn on sharing. Nothing is sent for this character before that, and the tour never presses it for you.",
                TutorialTarget.SharingConsent, Requires: TutorialCondition.SharingWindowOpen, FallbackBody: OpenSharing, FallbackTarget: TutorialTarget.LibrarySharing, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("online.check", "Proving the character is yours",
                "Next comes the Lodestone check. Copy the code shown here (Get a code asks for one), paste it into your character's Character Profile on the Lodestone and save, then paste the address of your character's Lodestone page here and press Check. Once the check passes, delete the code from your profile. Cancel stops the check.",
                TutorialTarget.SharingLodestoneCheck, Requires: TutorialCondition.SharingWindowOpen, FallbackBody: OpenSharing, FallbackTarget: TutorialTarget.LibrarySharing, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("online.shared", "Sharing is on",
                "While this character shares, its Active Plate is shared without asking: saving it shares the new version, and making another Plate Active shares that one. My Plates marks the shared Plate Shared. A small window shows how sharing is going, and if it couldn't finish, why, with Try again.",
                TutorialTarget.SharingStatus, Requires: TutorialCondition.SharingWindowOpen, FallbackBody: OpenSharing, FallbackTarget: TutorialTarget.LibrarySharing, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("online.stop", "Pausing or turning it off",
                "Pause sharing deletes this character's Plate from the server and keeps its check, so Resume sharing shares again without a new one. Turn off sharing for this character deletes its Plate, its images and its check, after asking you; sharing again then needs a new Lodestone check. Turn off sharing for every character does that for each character on this PC.",
                TutorialTarget.SharingPauseAndTurnOff, Requires: TutorialCondition.SharingWindowOpen, FallbackBody: OpenSharing, FallbackTarget: TutorialTarget.LibrarySharing, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("online.preview", "What would be shared",
                "To see what sharing a Plate would send, right-click its card and choose Check what would be shared (preview); the editor's Plate menu has it too. It shows the Plate's name, texts and images as they would be sent, from its saved state, or why it can't be shared yet. It sends nothing.",
                TutorialTarget.LibraryPlateGrid, Requires: TutorialCondition.MyPlatesOpen, FallbackBody: OpenMyPlates, FallbackAction: TutorialAction.OpenMyPlates),
        ], SharingOnly: true),

        new TutorialChapter("viewing", "Other players' Plates", "Viewing the Plates of other players who share, and hiding or reporting one.",
        [
            new TutorialStep("viewing.find", "Finding a player's Plate",
                "Viewing is part of sharing: once one of your characters shares, you can view other players who share too. Right-click their character in game, in your party or friend list, or their name in chat, and choose View AetherFrame Plate. Or click Find a player's Plate here to search for them.",
                TutorialTarget.SharingFindPlayer, TutorialStepMode.Interact, Requires: TutorialCondition.SharingWindowOpen, FallbackBody: OpenSharing, FallbackTarget: TutorialTarget.LibrarySharing, FallbackAction: TutorialAction.OpenMyPlates),
            new TutorialStep("viewing.search", "Searching by name",
                "AetherFrame Plates finds a player by their character's full name and World: type the name, pick the World, and press View. Their Plate opens in the Plate Viewer, over the game, like your own. A lookup sends your network address to AetherFrame's server, which doesn't store it and keeps no record of who viewed whom.",
                TutorialTarget.PlateSearch),
            new TutorialStep("viewing.viewer", "Hide or report",
                "Right-click another player's Plate in the Plate Viewer to see whose it is, and to Refresh it, Hide this player or Report it. Hiding is kept on this PC: their Plate isn't looked up again until you choose Show their Plate again. Report sends AetherFrame's server the reason you pick.",
                TutorialTarget.ViewerOtherPlayersPlate),
        ], SharingOnly: true),

        new TutorialChapter("done", "You're all set", "Where to find the tutorial again.",
        [
            new TutorialStep("done.finish", "That's the tour",
                "You know where Plates live, how to create and edit one in both editors, and how saving, Templates and sharing work.\n\nTo see any of this again, open Help in My Plates: it starts the tutorial over or jumps straight to a chapter. Finish returns you to My Plates.",
                Mode: TutorialStepMode.Narrative),
        ]),
    ];

    // After Chapters: static fields are set in the order they're written.
    private static readonly IReadOnlyList<TutorialChapter> WithoutSharing = [.. Chapters.Where(chapter => !chapter.SharingOnly)];

    /// <summary>
    /// The chapters a build shows: every one in the sharing build; in a player build, which has no
    /// sharing code and so no sharing control to point at, all but the chapters that teach sharing
    /// (<see cref="TutorialChapter.SharingOnly"/>). The chapters before those keep their places in
    /// both builds.
    /// </summary>
    internal static IReadOnlyList<TutorialChapter> ForBuild(bool sharing) => sharing ? Chapters : WithoutSharing;
}
