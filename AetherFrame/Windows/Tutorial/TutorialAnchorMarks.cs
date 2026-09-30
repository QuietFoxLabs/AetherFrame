using System.Numerics;
using AetherFrame.UI.Tutorial;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.Windows.Tutorial;

/// <summary>
/// What the tutorial overlay shares with every AetherFrame window this frame, and the one seam
/// the windows use to take part in the tutorial. Windows never see the tutorial's state; they
/// only (1) mark the controls they draw and (2) may ask which control the tutorial wants, to
/// bring it into view. Render thread only.
/// </summary>
internal static class TutorialOverlayState
{
    /// <summary>The registry the overlay reads; set by the plugin at startup.</summary>
    internal static TutorialAnchorRegistry Registry { get; set; } = new();

    /// <summary>Whether the spotlight is up this frame (windows then stay behind the dim; see <c>AetherWindowChrome.ApplyPolicy</c>).</summary>
    internal static bool IsSpotlightActive { get; set; }

    /// <summary>The control the current step points at, or None; a window that owns it may scroll to it or open its section.</summary>
    internal static TutorialTarget WantedTarget { get; set; }
}

/// <summary>
/// Marks tutorial targets while a window draws. Call <see cref="Mark"/> right after the widget
/// that is the target (it reads ImGui's last-item rectangle and the current clip rectangle), or
/// <see cref="MarkRect"/> for a region that isn't one item (a custom-drawn row, a canvas). Each
/// call is a dictionary write; nothing allocates.
/// </summary>
internal static class TutorialAnchorMarks
{
    /// <summary>Records the last drawn item as <paramref name="target"/>.</summary>
    internal static void Mark(TutorialTarget target)
    {
        if (target == TutorialTarget.None)
        {
            return;
        }

        MarkRect(target, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
    }

    /// <summary>Records the rectangle <paramref name="min"/>..<paramref name="max"/> as <paramref name="target"/>, clipped like the current window's content.</summary>
    internal static void MarkRect(TutorialTarget target, Vector2 min, Vector2 max)
    {
        if (target == TutorialTarget.None)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var clip = new ScreenRect(ImGui.GetClipRectMin(drawList), ImGui.GetClipRectMax(drawList));
        TutorialOverlayState.Registry.Record(target, new ScreenRect(min, max), clip, ImGui.GetFrameCount(), CurrentTopLevelWindowId());
    }

    /// <summary>The id of the top-level window being drawn (the root of a child region; a popup is its own root), or 0.</summary>
    private static uint CurrentTopLevelWindowId()
    {
        var window = ImGuiP.GetCurrentWindow();
        if (window.IsNull)
        {
            return 0;
        }

        var root = window.RootWindow;
        return root.IsNull ? window.ID : root.ID;
    }

    /// <summary>Records the current window (a child region, say) as <paramref name="target"/>.</summary>
    internal static void MarkWindow(TutorialTarget target)
    {
        if (target == TutorialTarget.None)
        {
            return;
        }

        var min = ImGui.GetWindowPos();
        MarkRect(target, min, min + ImGui.GetWindowSize());
    }

    /// <summary>Whether the tutorial is pointing at <paramref name="target"/> right now (to scroll to it, open its section or tab).</summary>
    internal static bool IsWanted(TutorialTarget target) => target != TutorialTarget.None && TutorialOverlayState.WantedTarget == target;

    /// <summary>
    /// For a window drawing <paramref name="target"/> inside a scrolling region: when the tutorial
    /// is pointing at it and asked for it to be revealed, scrolls the region so it shows. Call
    /// right after the item.
    /// </summary>
    internal static void RevealIfWanted(TutorialTarget target)
    {
        if (IsWanted(target) && TutorialOverlayState.Registry.ConsumeReveal(target, ImGui.GetFrameCount()))
        {
            ImGui.SetScrollHereY(0.5f);
        }
    }
}
