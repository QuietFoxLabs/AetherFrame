using System;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.Windows;

/// <summary>
/// Applies a <see cref="ChooserMemory"/> to the ImGui list it belongs to. Call <see cref="Begin"/>
/// first thing inside the open combo, popup or list child (the window that scrolls), scroll the
/// selected row into view when <see cref="ChooserOpening.ScrollToSelection"/> says so (see
/// <see cref="ScrollHereIfOpening"/>), and call <see cref="End"/> last, still inside it. Every
/// combo shares ImGui's one combo popup window, so its own scroll can't be relied on between
/// different choosers: this is what keeps each chooser's place.
/// </summary>
internal static class ChooserScroll
{
    /// <summary>On the frame the list appears, restores its scroll or asks for the selection to be shown.</summary>
    internal static ChooserOpening Begin(ChooserMemory memory, string? selection, Func<string, string, bool>? selectionMatches = null)
    {
        if (!ImGui.IsWindowAppearing())
        {
            return default;
        }

        var opening = memory.Open(selection, selectionMatches);
        ImGui.SetScrollY(opening.RestoreScrollY ?? 0f);
        return opening;
    }

    /// <summary>Scrolls the row just drawn into the middle of the list, when it is the selected row of a list that is opening.</summary>
    internal static void ScrollHereIfOpening(in ChooserOpening opening, bool selected)
    {
        if (opening.ScrollToSelection && selected)
        {
            ImGui.SetScrollHereY(0.5f);
        }
    }

    /// <summary>Remembers where the list is and what is selected; call after the list, every frame it is
    /// open. Not on the frame it appears: the scroll set in <see cref="Begin"/> only applies from the
    /// next frame, so the window's scroll then is still the last chooser's (combos share one window).</summary>
    internal static void End(ChooserMemory memory, string? selection)
    {
        if (!ImGui.IsWindowAppearing())
        {
            memory.Record(ImGui.GetScrollY(), selection);
        }
    }
}
