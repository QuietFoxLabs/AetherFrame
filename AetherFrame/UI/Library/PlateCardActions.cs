using System;
using System.Numerics;

namespace AetherFrame.UI.Library;

/// <summary>
/// Where the selected card's Plate actions button ("...") sits: at the right end of the name row,
/// under the thumbnail and the badges on it, in room of its own that the name is clipped short of.
/// Its size follows the text line and the card's padding, so it grows with the UI scale as the card
/// does. Pixels as measured at the current scale, so it has no ImGui dependency.
/// </summary>
internal readonly record struct PlateCardActionsLayout(Vector2 ButtonMin, Vector2 ButtonMax, float NameRight)
{
    /// <summary>The button's tooltip.</summary>
    internal const string Tooltip = "Plate actions";

    /// <summary>
    /// The button for a card from <paramref name="cardMin"/> to <paramref name="cardMax"/>, whose name
    /// row is one <paramref name="lineHeight"/> tall, <paramref name="padding"/> above the card's
    /// bottom edge and in from its right edge. The button is as tall as that row and half the padding
    /// above and below it (never reaching the thumbnail), as wide as it is tall, and never more than
    /// half the row, so the name always keeps the rest.
    /// </summary>
    internal static PlateCardActionsLayout For(Vector2 cardMin, Vector2 cardMax, float padding, float lineHeight)
    {
        var half = padding / 2f;
        var right = cardMax.X - padding;
        var width = MathF.Min(lineHeight + padding, (right - (cardMin.X + padding)) / 2f);
        var min = new Vector2(right - width, cardMax.Y - padding - lineHeight - half);
        var max = new Vector2(right, cardMax.Y - half);
        return new PlateCardActionsLayout(min, max, min.X - half);
    }

    /// <summary>Whether <paramref name="point"/> is in the button's hit region (min inclusive, max exclusive, as ImGui tests a rectangle).</summary>
    internal bool Contains(Vector2 point) =>
        point.X >= ButtonMin.X && point.Y >= ButtonMin.Y && point.X < ButtonMax.X && point.Y < ButtonMax.Y;
}

/// <summary>The mouse over one card this frame, as ImGui reports it.</summary>
/// <param name="Mouse">Where the mouse is.</param>
/// <param name="Hovered">The card is hovered: nothing in front of it (a menu, another window) takes the mouse.</param>
/// <param name="LeftPressed">The left button went down this frame.</param>
/// <param name="LeftDoubleClicked">That press was the second of a double-click.</param>
/// <param name="LeftReleased">The left button went up this frame.</param>
/// <param name="RightPressed">The right button went down this frame.</param>
internal readonly record struct PlateCardPointer(Vector2 Mouse, bool Hovered, bool LeftPressed, bool LeftDoubleClicked, bool LeftReleased, bool RightPressed);

/// <summary>What a card does with this frame's mouse (see <see cref="PlateCardInput"/>).</summary>
/// <param name="Select">The card becomes the selected one.</param>
/// <param name="Edit">The card's Plate opens in an editor (a double-click on the card itself).</param>
/// <param name="OpenMenu">The card's menu opens: a right-click on it, or a click on its actions button.</param>
/// <param name="MayStartDrag">The card may start a drag to reorder this frame.</param>
/// <param name="OverButton">The mouse is over the actions button (its tooltip shows).</param>
/// <param name="ButtonHeld">A left press that began on the actions button is still held.</param>
internal readonly record struct PlateCardResponse(bool Select, bool Edit, bool OpenMenu, bool MayStartDrag, bool OverButton, bool ButtonHeld);

/// <summary>
/// Who owns the left button on My Plates' cards. A card is one invisible button that selects on a
/// click, edits on a double-click and starts a drag to reorder; the selected card's actions button
/// lies over it, and wins: a press that begins anywhere in its hit region belongs to it until the
/// button is released, so that press never selects, edits or drags the card, even once the mouse
/// leaves the button. Releasing over the button opens the card's menu, the one its right-click opens;
/// releasing elsewhere does nothing. A double-click with either press on the button never edits.
/// The right button is untouched: a right-click anywhere on the card, the button included, selects it
/// and opens its menu, as before. Opening the menu does nothing else. One per window, used on the
/// render thread.
/// </summary>
internal sealed class PlateCardInput
{
    // The left press being held, if one began on a card, and whether it began on that card's button.
    private Guid? pressedCard;
    private bool pressedButton;

    // Whether the last left press on a card began on its button (a double-click's first press).
    private bool lastPressOnButton;

    /// <summary>
    /// The card's response to this frame's mouse. <paramref name="button"/> is the card's actions
    /// button, or null where it has none (it isn't the selected card).
    /// </summary>
    /// <param name="plateId">The card's Plate.</param>
    /// <param name="pointer">The mouse over the card this frame.</param>
    /// <param name="button">The card's actions button, if it shows one.</param>
    /// <param name="ready">The Plate can be opened (a double-click edits only then).</param>
    /// <param name="canReorder">Cards can be reordered now (no search filters them).</param>
    internal PlateCardResponse Update(Guid plateId, PlateCardPointer pointer, PlateCardActionsLayout? button, bool ready, bool canReorder)
    {
        var overButton = pointer.Hovered && button is { } actions && actions.Contains(pointer.Mouse);
        var select = false;
        var edit = false;
        var openMenu = false;

        if (pointer.LeftPressed && pointer.Hovered)
        {
            var firstPressOnButton = lastPressOnButton;
            pressedCard = plateId;
            pressedButton = overButton;
            lastPressOnButton = overButton;
            select = !overButton;
            edit = pointer.LeftDoubleClicked && !overButton && !firstPressOnButton && ready;
        }

        var holdsButton = pressedButton && pressedCard == plateId;
        if (pointer.LeftReleased && holdsButton && overButton)
        {
            openMenu = true;
        }

        if (pointer.RightPressed && pointer.Hovered)
        {
            select = true;
            openMenu = true;
        }

        return new PlateCardResponse(select, edit, openMenu, canReorder && !holdsButton, overButton, holdsButton && !pointer.LeftReleased);
    }

    /// <summary>Once a frame, after every card: a press ends once the left button is up.</summary>
    internal void EndFrame(bool leftDown)
    {
        if (!leftDown)
        {
            pressedCard = null;
            pressedButton = false;
        }
    }
}
