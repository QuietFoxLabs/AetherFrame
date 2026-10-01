using System;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Preview, as both editors' action bars offer it: one meaning everywhere, the finished Plate
/// exactly as a viewer sees it. Since the owner's request of October 1, 2026 ("all previews ...
/// should all use this view"), it opens the Plate in the Plate Viewer, the same floating, movable
/// view as My Plates' View and other players' Plates, showing the editor's live document, so edits
/// show as they are made (<see cref="Show"/>).
/// </summary>
internal static class EditorPreview
{
    /// <summary>The action bar's Preview tooltip, the same in both editors.</summary>
    internal const string Tooltip = "Preview: the finished Plate in the Plate Viewer, over the game, as others see it. It follows your edits.";

    /// <summary>
    /// Shows Preview: any edit still in progress (typing, a slider or canvas drag) is committed
    /// first, then <paramref name="view"/> opens the Plate Viewer on <paramref name="plateId"/>, which
    /// draws the editor's live document. Changes nothing on the Plate.
    /// </summary>
    internal static void Show(EditorSession editorSession, Guid plateId, Action<Guid> view)
    {
        editorSession.CommitPendingEdits();
        editorSession.EndInteraction();
        view(plateId);
    }
}
