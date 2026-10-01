using System;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Preview, as both editors' action bars offer it: one meaning everywhere, the finished Plate
/// exactly as a viewer sees it. Since the owner's request of October 1, 2026 ("all previews ...
/// should all use this view"), it opens the Plate in the Plate Viewer, the same floating, movable
/// view as My Plates' View and other players' Plates, showing the editor's live document, so edits
/// show as they are made (<see cref="Show"/>).
/// <para>
/// <see cref="Enter"/> and <see cref="Exit"/> are Clean Preview's (<c>CleanPreviewPresenter</c>),
/// which turned the editor's own window into the preview over the one shared
/// <see cref="EditorSession.PreviewActive"/>. Nothing enters it any more; it stays until its removal,
/// a follow-up of its own.
/// </para>
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

    /// <summary>
    /// Shows Preview: any edit still in progress (typing, a slider or canvas drag) is committed
    /// first, so the preview shows exactly the Plate as it now is. Changes nothing on the Plate.
    /// </summary>
    internal static void Enter(EditorSession editorSession)
    {
        editorSession.CommitPendingEdits();
        editorSession.EndInteraction();
        editorSession.PreviewActive = true;
    }

    /// <summary>Leaves Preview: the editor comes back as it was.</summary>
    internal static void Exit(EditorSession editorSession) => editorSession.PreviewActive = false;
}
