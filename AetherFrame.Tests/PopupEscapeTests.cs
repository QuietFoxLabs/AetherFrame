using System.Collections.Generic;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Escape on a popup closes that popup, never the window behind it (interface task 7): the rule
/// each AetherFrame window with popups applies, played frame by frame inside a reproduction of how
/// Dalamud 15.0.3.6 routes a key and closes a window.
///
/// <para>Routing (<c>Win32InputHandler.ProcessWndProcW</c> and <c>ProcessKeyEventsWorkarounds</c>,
/// <c>InterfaceManager.OnNewInputFrame</c>): a key message reaches ImGui only if the last Draw left
/// <c>io.WantTextInput</c> set, the keyboard claimed, and then the game's key state never shows
/// it; otherwise it reaches only the game's key state, and ImGui sees nothing. At each new frame,
/// ImGui lets go of its keys unless the keyboard is claimed, the game's key state is cleared if it
/// is, and ImGui.NewFrame clears the claim (no text field is in use here).</para>
///
/// <para>Closing (<c>WindowHost.DrawInternal</c>): right after the window's Draw, a focused window
/// closes when the game's key state shows Escape, the window respects the close hotkey, and no
/// window has closed for this press yet (a latch, cleared once the game's Escape is up).</para>
/// </summary>
public class PopupEscapeTests
{
    private enum Popup
    {
        Menu,
        Prompt,
    }

    [Fact]
    public void WithNothingOpen_EscapeReachesOnlyTheGame_AndClosesTheWindow()
    {
        var game = new Game();
        game.Frame();
        Assert.False(game.KeyboardClaimed);

        game.Frame(escape: true);
        Assert.False(game.IsOpen);
        Assert.Equal(1, game.GamePresses);

        // Dalamud gave ImGui nothing: without the claim, a popup could never see Escape.
        Assert.Equal(0, game.ImGuiPresses);
    }

    [Fact]
    public void EscapeOnAMenu_ClosesOnlyTheMenu_HeldForTwoSeconds()
    {
        var game = new Game();
        game.Frame(opens: Popup.Menu);
        game.Frame();

        game.Frame(escape: true);
        Assert.Empty(game.OpenPopups);
        Assert.True(game.IsOpen);

        // Held, with the key repeats Windows sends: the press stays the menu's, and the game,
        // whose key state is what Dalamud closes windows on, never sees it.
        for (var frame = 0; frame < 120; frame++)
        {
            game.Frame(escape: true);
            Assert.True(game.IsOpen);
        }

        Assert.Equal(1, game.ImGuiPresses);
        Assert.Equal(0, game.GamePresses);
        Assert.True(game.KeyboardClaimed);

        // Released: the release reaches ImGui too, and the keyboard is the game's again.
        game.Frame();
        Assert.False(game.KeyboardClaimed);

        // The next press, with nothing open, closes the window as before.
        game.Frame(escape: true);
        Assert.False(game.IsOpen);
    }

    [Fact]
    public void ATapOnAMenu_ClosesTheMenu_ThenTheKeyboardIsTheGamesAgain()
    {
        var game = new Game();
        game.Frame(opens: Popup.Menu);
        game.Frame(escape: true);
        game.Frame();
        Assert.Empty(game.OpenPopups);
        Assert.True(game.IsOpen);
        Assert.False(game.KeyboardClaimed);
        Assert.Equal(0, game.GamePresses);

        game.Frame(escape: true);
        Assert.False(game.IsOpen);
    }

    [Fact]
    public void EscapeOnAPrompt_CancelsOnlyThePrompt_HeldForTwoSeconds()
    {
        var game = new Game();
        game.Frame(opens: Popup.Prompt);
        game.Frame();

        game.Frame(escape: true);
        Assert.Empty(game.OpenPopups);

        for (var frame = 0; frame < 120; frame++)
        {
            game.Frame(escape: true);
            Assert.True(game.IsOpen);
        }

        Assert.Equal(0, game.GamePresses);

        game.Frame();
        game.Frame(escape: true);
        Assert.False(game.IsOpen);
    }

    [Fact]
    public void OnePress_AnswersOnePopup_AMenuOverAPromptClosesAlone()
    {
        var game = new Game();
        game.Frame(opens: Popup.Prompt);
        game.Frame(opens: Popup.Menu);

        // The menu, a list or a picker open over the prompt has focus: it closes, and the prompt,
        // focused again in the same frame, doesn't take the same press as its Cancel, held or not.
        for (var frame = 0; frame < 120; frame++)
        {
            game.Frame(escape: true);
            Assert.Equal(new[] { Popup.Prompt }, game.OpenPopups);
        }

        // Each further press answers the next thing in front: the prompt, then the window.
        game.Frame();
        game.Frame(escape: true);
        Assert.Empty(game.OpenPopups);
        Assert.True(game.IsOpen);

        game.Frame();
        game.Frame(escape: true);
        Assert.False(game.IsOpen);
    }

    [Fact]
    public void APopupOpenedThisFrame_TakesTheVeryNextPress()
    {
        var game = new Game();

        // The menu opens during the window's Draw, after the rule ran at its start: the keyboard is
        // claimed once the window's content is drawn, so the press of the very next frame is the
        // menu's.
        game.Frame(opens: Popup.Menu);
        game.Frame(escape: true);
        Assert.Empty(game.OpenPopups);
        Assert.True(game.IsOpen);
        Assert.Equal(0, game.GamePresses);
    }

    [Fact]
    public void ThePressThatClosesAnEditor_AsksOnce_AndLeavesTheQuestionOpen_HoweverLongItIsHeld()
    {
        var game = new Game(asksBeforeClosing: true);
        game.Frame();

        // Nothing open: the press is the game's, and Dalamud closes the editor, which has unsaved
        // changes, so it stays open and asks instead, in its next Draw.
        game.Frame(escape: true);
        game.Frame(escape: true);
        Assert.Equal(new[] { Popup.Prompt }, game.OpenPopups);

        // Still held, past the key repeat: the press stays the game's, so its repeats never reach
        // the question as a new press, and the window, already closed once for it, stays.
        for (var frame = 0; frame < 120; frame++)
        {
            game.Frame(escape: true);
            Assert.Equal(new[] { Popup.Prompt }, game.OpenPopups);
            Assert.True(game.IsOpen);
        }

        Assert.Equal(0, game.ImGuiPresses);
        Assert.False(game.KeyboardClaimed);

        // Released: the question has the keyboard, and the next press cancels it.
        game.Frame();
        game.Frame(escape: true);
        Assert.Empty(game.OpenPopups);
        Assert.True(game.IsOpen);
        Assert.Equal(1, game.GamePresses);
    }

    [Fact]
    public void APressJustAfterAMenuClosed_IsTheMenus_AndNeverClosesTheWindow()
    {
        var game = new Game();
        game.Frame(opens: Popup.Menu);

        // A menu item chosen: the menu closes during this Draw, and the claim made at its start
        // still holds, so the press that follows reaches ImGui, with nothing open. The rule keeps
        // that press the menu's, however long it is held.
        game.Frame(click: true);
        Assert.Empty(game.OpenPopups);

        for (var frame = 0; frame < 120; frame++)
        {
            game.Frame(escape: true);
            Assert.True(game.IsOpen);
        }

        Assert.Equal(0, game.GamePresses);

        game.Frame();
        game.Frame(escape: true);
        Assert.False(game.IsOpen);
    }

    [Fact]
    public void TheRule_ClaimsTheKeyboardForAPopup_UnlessTheGameHoldsThePress()
    {
        var rule = new PopupEscape();
        Assert.True(rule.RespectsCloseHotkey);
        Assert.False(rule.ClaimsKeyboard);

        rule.Update(popupFocused: false, promptFocused: false, escapePressed: false, imguiEscapeDown: false, gameEscapeDown: false);
        Assert.False(rule.ClaimsKeyboard);
        Assert.False(rule.ClaimsKeyboardAfterDraw(popupFocused: false));

        // A popup opened during the Draw.
        Assert.True(rule.ClaimsKeyboardAfterDraw(popupFocused: true));

        rule.Update(popupFocused: true, promptFocused: false, escapePressed: false, imguiEscapeDown: false, gameEscapeDown: false);
        Assert.True(rule.ClaimsKeyboard);
        Assert.True(rule.RespectsCloseHotkey);

        // The game holds a press made before the popup had focus: it stays the game's, and the
        // window doesn't respect the close hotkey while it is held.
        rule.Update(popupFocused: true, promptFocused: false, escapePressed: false, imguiEscapeDown: false, gameEscapeDown: true);
        Assert.False(rule.ClaimsKeyboard);
        Assert.False(rule.ClaimsKeyboardAfterDraw(popupFocused: true));
        Assert.False(rule.RespectsCloseHotkey);

        rule.Update(popupFocused: true, promptFocused: false, escapePressed: false, imguiEscapeDown: false, gameEscapeDown: false);
        Assert.True(rule.ClaimsKeyboard);
        Assert.True(rule.RespectsCloseHotkey);
    }

    [Fact]
    public void EscapePresses_AnswerEachPressOnce()
    {
        var presses = new EscapePresses();

        // The window closes a menu over a prompt; the prompt, focused again, can't take the same press.
        Assert.True(presses.TryAnswer(100));
        Assert.False(presses.TryAnswer(100));

        // The next press is answered again.
        Assert.True(presses.TryAnswer(160));
        Assert.False(presses.TryAnswer(160));
    }

    /// <summary>
    /// One AetherFrame window, focused, with the rule as <c>PopupEscapeGuard</c> applies it, inside
    /// the routing and close check above. The player's Escape key is given frame by frame; Windows
    /// turns it into a press, key repeats while it is held, and a release.
    /// </summary>
    private sealed class Game
    {
        // Windows' default key repeat at 60 frames a second: after half a second, about 30 a second.
        private const int RepeatDelayFrames = 30;
        private const int RepeatEveryFrames = 2;

        private readonly bool asksBeforeClosing;
        private readonly PopupEscape rule = new();
        private readonly EscapePresses presses = new();
        private readonly List<Popup> popups = [];
        private readonly Queue<bool> imguiEvents = new();

        private int frame;
        private int heldFrames;
        private bool gameEscapeDown;
        private bool imguiEscapeDown;
        private bool closedForThisPress;
        private bool questionDue;

        /// <param name="asksBeforeClosing">An editor with unsaved changes: Dalamud's close is turned back, and the question opens in the next Draw.</param>
        internal Game(bool asksBeforeClosing = false) => this.asksBeforeClosing = asksBeforeClosing;

        internal bool IsOpen { get; private set; } = true;

        /// <summary><c>io.WantTextInput</c> as the last Draw left it: where the next key messages go.</summary>
        internal bool KeyboardClaimed { get; private set; }

        /// <summary>Escape presses the game's key state showed (a key repeat after a cleared state counts too).</summary>
        internal int GamePresses { get; private set; }

        /// <summary>Frames in which ImGui saw Escape go down.</summary>
        internal int ImGuiPresses { get; private set; }

        /// <summary>The window's open popups, the one in front last.</summary>
        internal IReadOnlyList<Popup> OpenPopups => popups;

        /// <param name="escape">The player holds Escape this frame.</param>
        /// <param name="opens">A popup the window opens during this Draw, after the rule ran.</param>
        /// <param name="click">The popup in front closes during this Draw (a menu item chosen).</param>
        internal void Frame(bool escape = false, Popup? opens = null, bool click = false)
        {
            Assert.True(IsOpen, "A closed window isn't drawn.");
            frame++;

            // The game's message pump, routed as the last Draw left the claim.
            if (KeyMessage(escape) is { } down)
            {
                if (KeyboardClaimed)
                {
                    imguiEvents.Enqueue(down);
                }
                else
                {
                    if (down && !gameEscapeDown)
                    {
                        GamePresses++;
                    }

                    gameEscapeDown = down;
                }
            }

            // Dalamud's new frame, then ImGui's.
            if (KeyboardClaimed)
            {
                gameEscapeDown = false;
            }
            else
            {
                imguiEvents.Enqueue(false);
            }

            KeyboardClaimed = false;
            var pressed = TakeImGuiEvents();

            // The window's Draw: the rule first (PopupEscapeGuard.Update).
            var popupFocused = popups.Count > 0;
            var action = rule.Update(popupFocused, popupFocused && popups[^1] == Popup.Prompt, pressed, imguiEscapeDown, gameEscapeDown);
            if (action == PopupEscapeAction.CloseMenus && presses.TryAnswer(frame))
            {
                // ClosePopupsExceptModals: every menu over the prompt in front.
                while (popups.Count > 0 && popups[^1] == Popup.Menu)
                {
                    popups.RemoveAt(popups.Count - 1);
                }
            }

            KeyboardClaimed |= rule.ClaimsKeyboard;

            // The window's content: a prompt in front takes the press as its Cancel
            // (PopupEscapeGuard.CancelsPrompt), a click closes the popup in front, popups open.
            if (pressed && popups.Count > 0 && popups[^1] == Popup.Prompt && presses.TryAnswer(frame))
            {
                popups.RemoveAt(popups.Count - 1);
            }

            if (click)
            {
                popups.RemoveAt(popups.Count - 1);
            }

            if (opens is { } popup)
            {
                popups.Add(popup);
            }

            if (questionDue)
            {
                questionDue = false;
                popups.Add(Popup.Prompt);
            }

            // Once the content is drawn (the guard's DrawScope).
            KeyboardClaimed |= rule.ClaimsKeyboardAfterDraw(popups.Count > 0);

            // Dalamud's close check.
            if (gameEscapeDown && !closedForThisPress && rule.RespectsCloseHotkey)
            {
                closedForThisPress = true;
                if (asksBeforeClosing)
                {
                    questionDue = !popups.Contains(Popup.Prompt);
                }
                else
                {
                    IsOpen = false;
                }
            }
            else if (!gameEscapeDown && closedForThisPress)
            {
                closedForThisPress = false;
            }
        }

        /// <summary>The key message Windows sends this frame: the press, a key repeat, the release, or none.</summary>
        private bool? KeyMessage(bool escape)
        {
            if (!escape)
            {
                var released = heldFrames > 0;
                heldFrames = 0;
                return released ? false : null;
            }

            heldFrames++;
            var repeat = heldFrames > RepeatDelayFrames && (heldFrames - RepeatDelayFrames) % RepeatEveryFrames == 1;
            return heldFrames == 1 || repeat ? true : null;
        }

        /// <summary>
        /// ImGui.NewFrame takes the queued key events, one change of the key a frame (its trickled
        /// queue). True when Escape went down this frame, as <c>ImGui.IsKeyPressed(Escape, false)</c>.
        /// </summary>
        private bool TakeImGuiEvents()
        {
            var wasDown = imguiEscapeDown;
            var changed = false;
            while (imguiEvents.Count > 0)
            {
                var down = imguiEvents.Peek();
                if (down != imguiEscapeDown)
                {
                    if (changed)
                    {
                        break;
                    }

                    changed = true;
                    imguiEscapeDown = down;
                }

                imguiEvents.Dequeue();
            }

            var pressed = imguiEscapeDown && !wasDown;
            if (pressed)
            {
                ImGuiPresses++;
            }

            return pressed;
        }
    }
}
