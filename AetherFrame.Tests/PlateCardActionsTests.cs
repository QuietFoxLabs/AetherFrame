using System;
using System.Numerics;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The selected My Plates card's "..." button (interface task 3): where it sits at any UI scale, and
/// that it owns every left press that begins in its hit region, so the card under it never selects,
/// edits or drags from there, while the rest of the card keeps its click, double-click, right-click
/// and drag.
/// </summary>
public class PlateCardActionsTests
{
    private static readonly Guid Card = Guid.NewGuid();

    // A card as My Plates draws it at 100%: 196 px wide, 8 px padding, a 16:9 thumbnail and one text line.
    private static (Vector2 Min, Vector2 Max, float Padding, float Line, float ThumbnailBottom) CardAt(float scale)
    {
        var width = 196f * scale;
        var padding = 8f * scale;
        var line = 17f * scale;
        var thumbnail = (width - (padding * 2f)) / (16f / 9f);
        var min = new Vector2(40f, 120f);
        var max = min + new Vector2(width, thumbnail + (padding * 3f) + line);
        return (min, max, padding, line, min.Y + padding + thumbnail);
    }

    private static PlateCardActionsLayout ButtonAt(float scale)
    {
        var card = CardAt(scale);
        return PlateCardActionsLayout.For(card.Min, card.Max, card.Padding, card.Line);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(3f)]
    public void Layout_SitsInTheNameRow_InsideTheCard_LeavingTheNameAndBadgesClear(float scale)
    {
        var card = CardAt(scale);
        var button = PlateCardActionsLayout.For(card.Min, card.Max, card.Padding, card.Line);

        // Inside the card, and under the thumbnail, where the Active and sharing badges are.
        Assert.True(button.ButtonMin.X >= card.Min.X && button.ButtonMax.X <= card.Max.X);
        Assert.True(button.ButtonMin.Y > card.ThumbnailBottom && button.ButtonMax.Y <= card.Max.Y);

        // A target that grows with the scale: at least the text line plus the padding, each way.
        Assert.True(button.ButtonMax.Y - button.ButtonMin.Y >= card.Line + card.Padding - 0.01f);
        Assert.True(button.ButtonMax.X - button.ButtonMin.X >= card.Line + card.Padding - 0.01f);

        // The name is clipped short of the button, and keeps most of the row.
        var nameLeft = card.Min.X + card.Padding;
        Assert.True(button.NameRight < button.ButtonMin.X);
        Assert.True(button.NameRight - nameLeft >= 0.7f * (card.Max.X - card.Padding - nameLeft));
    }

    [Fact]
    public void Layout_NeverTakesMoreThanHalfTheNameRow()
    {
        // A text line far taller than the card is wide (a huge font in a tiny card).
        var button = PlateCardActionsLayout.For(Vector2.Zero, new Vector2(100f, 300f), 8f, 120f);
        Assert.Equal(42f, button.ButtonMax.X - button.ButtonMin.X, 3);
        Assert.True(button.NameRight >= 8f + 30f);
    }

    [Fact]
    public void Layout_HitRegion_IsTheWholeButton_MinInclusiveMaxExclusive()
    {
        var button = ButtonAt(1f);
        Assert.True(button.Contains(button.ButtonMin));
        Assert.True(button.Contains(button.ButtonMax - new Vector2(0.01f)));
        Assert.True(button.Contains((button.ButtonMin + button.ButtonMax) / 2f));
        Assert.False(button.Contains(button.ButtonMax));
        Assert.False(button.Contains(button.ButtonMin - new Vector2(0.01f, 0f)));
        Assert.False(button.Contains(button.ButtonMin - new Vector2(0f, 0.01f)));
    }

    [Fact]
    public void ClickOnTheButton_OpensTheMenuOnRelease_AndNeverSelectsEditsOrDrags()
    {
        var mouse = new Mouse(ButtonAt(1f));
        mouse.MoveTo(mouse.ButtonCenter);

        var press = mouse.Press();
        Assert.False(press.Select);
        Assert.False(press.Edit);
        Assert.False(press.OpenMenu);
        Assert.False(press.MayStartDrag);
        Assert.True(press.ButtonHeld);

        Assert.False(mouse.Hold().MayStartDrag);

        var release = mouse.Release();
        Assert.True(release.OpenMenu);
        Assert.False(release.Select);
        Assert.False(release.Edit);

        // Opening it is a one-off: the next frame opens nothing more, and the card may drag again.
        var after = mouse.Hold(down: false);
        Assert.False(after.OpenMenu);
        Assert.True(after.MayStartDrag);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.999f, 0.999f)]
    [InlineData(0f, 0.999f)]
    [InlineData(0.999f, 0f)]
    [InlineData(0.5f, 0.5f)]
    public void DoubleClickAnywhereOnTheButton_NeverEdits(float x, float y)
    {
        var mouse = new Mouse(ButtonAt(1.5f));
        mouse.MoveTo(mouse.Within(x, y));

        Assert.False(mouse.Press().Edit);
        Assert.True(mouse.Release().OpenMenu);
        var second = mouse.Press(doubleClick: true);
        Assert.False(second.Edit);
        Assert.False(second.Select);
        Assert.False(mouse.Release().Edit);
    }

    [Fact]
    public void DoubleClick_WhoseFirstPressWasOnTheButton_NeverEdits()
    {
        var mouse = new Mouse(ButtonAt(1f));
        mouse.MoveTo(mouse.ButtonMin);
        mouse.Press();
        mouse.Release();

        // A few pixels left, on the card itself, within ImGui's double-click distance.
        mouse.MoveTo(mouse.ButtonMin - new Vector2(3f, 0f));
        var second = mouse.Press(doubleClick: true);
        Assert.False(second.Edit);
        Assert.True(second.Select);
    }

    [Fact]
    public void PressOnTheButton_DraggedOff_NeverDragsTheCard_AndOpensNothing()
    {
        var mouse = new Mouse(ButtonAt(1f));
        mouse.MoveTo(mouse.ButtonCenter);
        mouse.Press();

        // Onto the card's body, then off the card altogether.
        mouse.MoveTo(mouse.ButtonMin - new Vector2(40f, 20f));
        var overBody = mouse.Hold();
        Assert.False(overBody.MayStartDrag);
        Assert.False(overBody.OverButton);
        Assert.False(mouse.Hold(hovered: false).MayStartDrag);

        var release = mouse.Release(hovered: false);
        Assert.False(release.OpenMenu);
        Assert.False(release.Select);
        Assert.False(release.Edit);

        // Back over the button with the button up: hovering opens nothing.
        mouse.MoveTo(mouse.ButtonCenter);
        Assert.False(mouse.Hold(down: false).OpenMenu);
    }

    [Fact]
    public void PressOnTheBody_ReleasedOverTheButton_OpensNothing()
    {
        var mouse = new Mouse(ButtonAt(1f));
        mouse.MoveTo(mouse.ButtonMin - new Vector2(40f, 20f));
        Assert.True(mouse.Press().MayStartDrag);

        mouse.MoveTo(mouse.ButtonCenter);
        Assert.True(mouse.Hold().MayStartDrag);
        Assert.False(mouse.Release().OpenMenu);
    }

    [Fact]
    public void TheRestOfTheCard_KeepsItsClickDoubleClickAndDrag()
    {
        var mouse = new Mouse(ButtonAt(1f));
        mouse.MoveTo(mouse.ButtonMin - new Vector2(40f, 20f));

        var press = mouse.Press();
        Assert.True(press.Select);
        Assert.True(press.MayStartDrag);
        Assert.False(press.OpenMenu);
        Assert.False(mouse.Release().OpenMenu);

        Assert.True(mouse.Press(doubleClick: true).Edit);
    }

    [Fact]
    public void ACardWithoutTheButton_IsAllCard()
    {
        // An unselected card shows no button: a press where it would be selects the card, as before.
        var mouse = new Mouse(button: null);
        mouse.MoveTo(ButtonAt(1f).ButtonMin + new Vector2(2f));

        var press = mouse.Press();
        Assert.True(press.Select);
        Assert.True(press.MayStartDrag);
        Assert.False(press.OverButton);
        Assert.False(mouse.Release().OpenMenu);
        Assert.True(mouse.Press(doubleClick: true).Edit);
    }

    [Fact]
    public void Search_StillKeepsCardsFromReordering()
    {
        var mouse = new Mouse(ButtonAt(1f), canReorder: false);
        mouse.MoveTo(mouse.ButtonMin - new Vector2(40f, 20f));
        Assert.False(mouse.Press().MayStartDrag);
        Assert.False(mouse.Hold().MayStartDrag);
    }

    [Fact]
    public void RightClick_AnywhereOnTheCard_StillSelectsAndOpensTheMenu()
    {
        var mouse = new Mouse(ButtonAt(1f));
        mouse.MoveTo(mouse.ButtonMin - new Vector2(40f, 20f));
        var body = mouse.RightClick();
        Assert.True(body.Select && body.OpenMenu);
        Assert.False(body.Edit);

        mouse.MoveTo(mouse.ButtonCenter);
        var onButton = mouse.RightClick();
        Assert.True(onButton.OpenMenu);
        Assert.False(onButton.Edit);
    }

    [Fact]
    public void WithSomethingInFrontOfTheCard_ThePointerDoesNothing()
    {
        // An open menu or another window over the card: ImGui doesn't report the card hovered.
        var mouse = new Mouse(ButtonAt(1f));
        mouse.MoveTo(mouse.ButtonCenter);

        var press = mouse.Press(hovered: false);
        Assert.False(press.OverButton);
        Assert.False(press.Select);
        Assert.False(mouse.Release(hovered: false).OpenMenu);
        Assert.False(mouse.RightClick(hovered: false).OpenMenu);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheButton_ReachesTheMenu_ForEveryPlate_WhileOnlyAReadyPlateEdits(bool ready)
    {
        // A Plate that can't be opened still has its menu (Delete, at least), as its right-click does;
        // the menu's own items keep their disabled states, since both open the same PlateMenu.
        var mouse = new Mouse(ButtonAt(1f), ready: ready);
        mouse.MoveTo(mouse.ButtonCenter);
        mouse.Press();
        Assert.True(mouse.Release().OpenMenu);

        mouse.MoveTo(mouse.ButtonMin - new Vector2(40f, 20f));
        mouse.Press();
        mouse.Release();
        Assert.Equal(ready, mouse.Press(doubleClick: true).Edit);
    }

    [Fact]
    public void APressOnOneCardsButton_DoesntOpenAnotherCardsMenu()
    {
        var input = new PlateCardInput();
        var button = ButtonAt(1f);
        var at = (button.ButtonMin + button.ButtonMax) / 2f;
        var other = Guid.NewGuid();

        input.Update(Card, new PlateCardPointer(at, true, true, false, false, false), button, true, true);
        input.EndFrame(true);

        // Released over the same spot on another card (cards reflowed under the mouse, say).
        var released = input.Update(other, new PlateCardPointer(at, true, false, false, true, false), button, true, true);
        Assert.False(released.OpenMenu);
        Assert.True(released.MayStartDrag);
    }

    /// <summary>One card's mouse, frame by frame, as My Plates feeds it to <see cref="PlateCardInput"/>.</summary>
    private sealed class Mouse(PlateCardActionsLayout? button, bool ready = true, bool canReorder = true)
    {
        private readonly PlateCardInput input = new();
        private Vector2 position;
        private bool down;

        internal Vector2 ButtonMin => button!.Value.ButtonMin;

        internal Vector2 ButtonCenter => (button!.Value.ButtonMin + button.Value.ButtonMax) / 2f;

        /// <summary>A point in the button, as fractions of its width and height.</summary>
        internal Vector2 Within(float x, float y)
        {
            var size = button!.Value.ButtonMax - button.Value.ButtonMin;
            return button.Value.ButtonMin + new Vector2(size.X * x, size.Y * y);
        }

        internal void MoveTo(Vector2 point) => position = point;

        internal PlateCardResponse Press(bool doubleClick = false, bool hovered = true)
        {
            down = true;
            return Frame(new PlateCardPointer(position, hovered, true, doubleClick, false, false));
        }

        internal PlateCardResponse Hold(bool hovered = true, bool down = true)
        {
            this.down = down;
            return Frame(new PlateCardPointer(position, hovered, false, false, false, false));
        }

        internal PlateCardResponse Release(bool hovered = true)
        {
            down = false;
            return Frame(new PlateCardPointer(position, hovered, false, false, true, false));
        }

        internal PlateCardResponse RightClick(bool hovered = true) =>
            Frame(new PlateCardPointer(position, hovered, false, false, false, true));

        private PlateCardResponse Frame(PlateCardPointer pointer)
        {
            var response = input.Update(Card, pointer, button, ready, canReorder);
            input.EndFrame(down);
            return response;
        }
    }
}
