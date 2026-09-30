using System;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Where the shared editor action bar's groups sit on its row: the left group (My Plates, the
/// Basic / Advanced switch, then the Plate menu's control at its minimum width) at the start, the
/// history group (Undo, Redo) centered in the row, and the document group (save state, Preview,
/// Revert, Save) against the end: the same positions in both editors. The Plate's name widens the
/// Plate menu's control into whatever space is left between it and the history group. When the
/// row is too narrow to center, the groups keep their order and never overlap: the history group
/// follows the left group, the document group follows it. The left group, the Plate menu's control
/// with it, always starts the row, so it stays reachable at any width. When one row can't hold all
/// three groups, the document group moves to a second row, against its end (see
/// <see cref="ArrangeRows"/>), so no control is ever cut off.
/// </summary>
internal static class EditorActionBarLayout
{
    /// <param name="rowStart">The row's first usable x.</param>
    /// <param name="rowEnd">The row's last usable x.</param>
    /// <param name="leftEnd">Where the left group ends.</param>
    /// <param name="centerWidth">The history group's width.</param>
    /// <param name="rightWidth">The document group's width.</param>
    /// <param name="spacing">The gap kept between groups.</param>
    /// <returns>The history group's start, the document group's start, and the room between the left group and the history group (0 when none).</returns>
    internal static (float CenterX, float RightX, float NameWidth) Arrange(
        float rowStart, float rowEnd, float leftEnd, float centerWidth, float rightWidth, float spacing)
    {
        var earliestCenter = leftEnd + spacing;
        var rightX = Math.Max(earliestCenter + centerWidth + spacing, rowEnd - rightWidth);
        var centered = rowStart + ((rowEnd - rowStart - centerWidth) / 2f);
        var centerX = Math.Clamp(centered, earliestCenter, Math.Max(earliestCenter, rightX - spacing - centerWidth));
        var nameWidth = Math.Max(0f, centerX - spacing - earliestCenter);
        return (centerX, rightX, nameWidth);
    }

    /// <summary>
    /// The whole bar with the Plate menu's control: <see cref="Arrange"/>'s one row when all three
    /// groups fit on it. Otherwise the document group takes a second row, against its end, and the
    /// history group ends the first, which leaves the Plate's name the most room.
    /// </summary>
    /// <param name="rowStart">The row's first usable x.</param>
    /// <param name="rowEnd">The row's last usable x.</param>
    /// <param name="controlStart">Where the Plate menu's control starts, after the left group.</param>
    /// <param name="controlMinimum">The control's width without the name.</param>
    /// <param name="centerWidth">The history group's width.</param>
    /// <param name="rightWidth">The document group's width.</param>
    /// <param name="spacing">The gap kept between groups.</param>
    internal static EditorActionBarRows ArrangeRows(
        float rowStart, float rowEnd, float controlStart, float controlMinimum, float centerWidth, float rightWidth, float spacing)
    {
        var leftEnd = controlStart + controlMinimum;
        if (leftEnd + spacing + centerWidth + spacing + rightWidth <= rowEnd)
        {
            var (centerX, rightX, _) = Arrange(rowStart, rowEnd, leftEnd, centerWidth, rightWidth, spacing);
            return new EditorActionBarRows(centerX, rightX, NameRoom(controlStart, controlMinimum, centerX, spacing), TwoRows: false);
        }

        var historyX = Math.Max(leftEnd + spacing, rowEnd - centerWidth);
        return new EditorActionBarRows(historyX, Math.Max(rowStart, rowEnd - rightWidth), NameRoom(controlStart, controlMinimum, historyX, spacing), TwoRows: true);
    }

    /// <summary>
    /// The room the Plate menu's control has beyond its minimum width, for the Plate's name: from
    /// the end of its minimum width to one gap before the history group. Never negative.
    /// </summary>
    /// <param name="controlStart">Where the control starts.</param>
    /// <param name="controlMinimum">The control's width without the name.</param>
    /// <param name="centerX">Where the history group starts (see <see cref="Arrange"/>).</param>
    /// <param name="spacing">The gap kept between groups.</param>
    internal static float NameRoom(float controlStart, float controlMinimum, float centerX, float spacing) =>
        Math.Max(0f, centerX - spacing - (controlStart + controlMinimum));
}

/// <summary>
/// Where the action bar's groups go (<see cref="EditorActionBarLayout.ArrangeRows"/>): the history
/// group's start, the document group's start (on the second row when <paramref name="TwoRows"/>),
/// and the room the Plate menu's control has for the Plate's name.
/// </summary>
internal readonly record struct EditorActionBarRows(float CenterX, float RightX, float NameRoom, bool TwoRows);
