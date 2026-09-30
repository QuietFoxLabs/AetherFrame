namespace AetherFrame.UI.Tutorial;

/// <summary>
/// Every control the tutorial can point at. A window marks a target right after drawing the
/// widget (<c>TutorialAnchorMarks.Mark</c>), which records the widget's real screen rectangle for
/// that frame; the spotlight follows whatever was recorded, so a moved, resized, scrolled or
/// rescaled window never leaves the spotlight behind. A target nobody marked this frame simply
/// isn't available, and the step says so instead of pointing at nothing.
///
/// <para>Names say where the control lives and what it is, never what it looks like, so a
/// redesign that moves a control keeps its target. Adding a target here is the first step of
/// adding a tutorial step (see docs/DesignGuide.md, "Adding tutorial targets").</para>
/// </summary>
internal enum TutorialTarget
{
    None = 0,

    // ---- My Plates (the Plate Library window)
    LibraryHeader,
    LibraryCreatePlate,
    LibraryImport,
    LibrarySearch,
    LibraryHelp,
    LibraryPlateGrid,
    LibraryFirstPlateCard,
    LibraryFooter,
    LibraryTemplateChooser,
    LibraryTemplatesGrid,
    LibraryBackToMyPlates,

    // ---- the action bar both editors share
    EditorMyPlates,
    EditorModeSwitch,
    EditorPlateMenu,
    EditorHistory,
    EditorSaveState,
    EditorPreview,
    EditorRevert,
    EditorSave,

    // ---- the Basic editor
    BasicNavigator,
    BasicNavigatorStyle,
    BasicNavigatorPortrait,
    BasicNavigatorIdentity,
    BasicNavigatorDetails,
    BasicNavigatorMessage,
    BasicInspector,
    BasicPreview,
    BasicPreviewZoom,
    BasicStyleThemes,
    BasicPortraitImport,
    BasicIdentityName,
    BasicMessageText,

    // ---- the Advanced editor
    AdvancedToolbar,
    AdvancedAddText,
    AdvancedAddImage,
    AdvancedGuides,
    AdvancedSnap,
    AdvancedLayers,
    AdvancedCanvas,
    AdvancedInspector,
    AdvancedInspectorElementTab,
    AdvancedInspectorCanvasTab,
    AdvancedInspectorComponents,
    AdvancedZoom,
    AdvancedStatusBar,
    AdvancedTextContent,
    AdvancedTextFont,
    AdvancedTextSize,
    AdvancedTextColor,
    AdvancedElementPosition,
    AdvancedElementSize,
    AdvancedLayerOrder,
    AdvancedBackground,
    AdvancedCanvasSize,
}
