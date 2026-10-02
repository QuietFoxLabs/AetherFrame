using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Escape on a popup closes that popup, never the window behind it (interface task 7): the close
/// rule each AetherFrame window with popups applies, played frame by frame against Dalamud's own
/// close check, reproduced from Dalamud 15.0.3.6's <c>WindowHost.DrawInternal</c>.
/// </summary>
public class PopupEscapeTests
{
    /// <summary>
    /// Dalamud's check, run right after each window's Draw: with Escape down in the game's key
    /// state, a focused window that respects the close hotkey closes, unless a window has already
    /// closed for this press. The latch is shared by every window and cleared once Escape is up.
    /// </summary>
    private sealed class DalamudEscapeCheck
    {
        private bool wasEscPressedLastFrame;

        internal bool Closes(bool gameEscapeDown, bool focused, bool respectCloseHotkey)
        {
            if (gameEscapeDown && focused && !wasEscPressedLastFrame && respectCloseHotkey)
            {
                wasEscPressedLastFrame = true;
                return true;
            }

            if (!gameEscapeDown && wasEscPressedLastFrame)
            {
                wasEscPressedLastFrame = false;
            }

            return false;
        }
    }

    /// <summary>One window, focused itself or through one of its popups, drawn frame after frame.</summary>
    private sealed class GuardedWindow
    {
        private readonly PopupEscape rule = new();
        private readonly DalamudEscapeCheck dalamud = new();

        internal PopupEscapeAction LastAction { get; private set; }

        /// <summary>
        /// A frame: the rule first thing in Draw, then Dalamud's check. Escape is down in ImGui and in
        /// the game alike unless one of them says otherwise. True when Dalamud closes the window.
        /// </summary>
        internal bool Frame(bool popup = false, bool prompt = false, bool pressed = false, bool down = false, bool? gameDown = null, bool? imguiDown = null)
        {
            var game = gameDown ?? down;
            var imgui = imguiDown ?? down;
            LastAction = rule.Update(popup, popup && prompt, pressed, game || imgui);
            return dalamud.Closes(game, focused: true, rule.RespectsCloseHotkey);
        }
    }

    [Fact]
    public void EscapeWithNoPopupOpen_ClosesTheWindow()
    {
        var window = new GuardedWindow();
        Assert.False(window.Frame());

        Assert.True(window.Frame(pressed: true, down: true));
        Assert.Equal(PopupEscapeAction.None, window.LastAction);
    }

    [Fact]
    public void EscapeOnAMenu_ClosesTheMenu_AndNotTheWindow_HoweverLongItIsHeld()
    {
        var window = new GuardedWindow();
        Assert.False(window.Frame(popup: true));

        // The press: the menu closes, and the window stays.
        Assert.False(window.Frame(popup: true, pressed: true, down: true));
        Assert.Equal(PopupEscapeAction.CloseMenus, window.LastAction);

        // Still held, the menu gone: Dalamud would close the window on any of these frames if the
        // window respected the close hotkey again, since no window closed for this press.
        for (var frame = 0; frame < 30; frame++)
        {
            Assert.False(window.Frame(down: true));
            Assert.Equal(PopupEscapeAction.None, window.LastAction);
        }

        // Released: the next press, with nothing open, closes the window as before.
        Assert.False(window.Frame());
        Assert.True(window.Frame(pressed: true, down: true));
    }

    [Fact]
    public void EscapeOnAPrompt_IsLeftToThePrompt_AndNeverClosesTheWindow()
    {
        var window = new GuardedWindow();
        Assert.False(window.Frame(popup: true, prompt: true));

        // The prompt answers the press itself, as its Cancel; the window has nothing to close.
        Assert.False(window.Frame(popup: true, prompt: true, pressed: true, down: true));
        Assert.Equal(PopupEscapeAction.None, window.LastAction);

        for (var frame = 0; frame < 30; frame++)
        {
            Assert.False(window.Frame(down: true));
        }

        Assert.False(window.Frame());
        Assert.True(window.Frame(pressed: true, down: true));
    }

    [Fact]
    public void AMenuOverAPrompt_ClosesAlone()
    {
        var window = new GuardedWindow();
        Assert.False(window.Frame(popup: true));

        // The menu, a list or a picker open over the prompt has focus: it closes, the prompt doesn't.
        Assert.False(window.Frame(popup: true, prompt: false, pressed: true, down: true));
        Assert.Equal(PopupEscapeAction.CloseMenus, window.LastAction);

        // The prompt has focus again while Escape is held: it isn't closed by the same press.
        Assert.False(window.Frame(popup: true, prompt: true, down: true));
        Assert.Equal(PopupEscapeAction.None, window.LastAction);
    }

    [Fact]
    public void TheGameSeesThePressAFrameAfterImGui_TheWindowStays()
    {
        var window = new GuardedWindow();
        Assert.False(window.Frame(popup: true));

        // ImGui has the press and the menu closes; the game's key state doesn't show it yet.
        Assert.False(window.Frame(popup: true, pressed: true, imguiDown: true, gameDown: false));
        Assert.Equal(PopupEscapeAction.CloseMenus, window.LastAction);

        // The frame after, the menu is gone and Dalamud sees the press: the window stays.
        Assert.False(window.Frame(down: true));
        Assert.False(window.Frame(down: true));

        // The game's key state lets go a frame after ImGui's: still the menu's press.
        Assert.False(window.Frame(imguiDown: false, gameDown: true));
        Assert.False(window.Frame());
        Assert.True(window.Frame(pressed: true, down: true));
    }

    [Fact]
    public void TheGameSeesThePressAFrameBeforeImGui_TheWindowStays()
    {
        var window = new GuardedWindow();
        Assert.False(window.Frame(popup: true));

        // Dalamud sees Escape down while the menu is still open, before ImGui has the press.
        Assert.False(window.Frame(popup: true, imguiDown: false, gameDown: true));
        Assert.Equal(PopupEscapeAction.None, window.LastAction);

        // ImGui's press closes the menu.
        Assert.False(window.Frame(popup: true, pressed: true, down: true));
        Assert.Equal(PopupEscapeAction.CloseMenus, window.LastAction);

        Assert.False(window.Frame(down: true));
    }

    [Fact]
    public void APopupThatClosedTheFrameBefore_StillTakesThePress()
    {
        var window = new GuardedWindow();
        Assert.False(window.Frame(popup: true));

        // The popup closed during the last frame (a menu item chosen, say), and Escape comes now:
        // the press may be the one that closed it, so the window stays.
        Assert.False(window.Frame(pressed: true, down: true));
        Assert.Equal(PopupEscapeAction.None, window.LastAction);
    }

    [Fact]
    public void AWindowWithoutAPopup_RespectsTheCloseHotkey_UntilAPopupTakesAPress()
    {
        var rule = new PopupEscape();
        Assert.True(rule.RespectsCloseHotkey);

        rule.Update(popupFocused: true, promptFocused: false, escapePressed: false, escapeDown: false);
        Assert.True(rule.RespectsCloseHotkey);

        rule.Update(popupFocused: true, promptFocused: false, escapePressed: true, escapeDown: true);
        Assert.False(rule.RespectsCloseHotkey);

        rule.Update(popupFocused: false, promptFocused: false, escapePressed: false, escapeDown: false);
        Assert.True(rule.RespectsCloseHotkey);
    }

    [Fact]
    public void OnePress_AnswersOnePopup()
    {
        var presses = new EscapePresses();

        // The window closes a menu over a prompt; the prompt, focused again, can't take the same press.
        Assert.True(presses.TryAnswer(100));
        Assert.False(presses.TryAnswer(100));

        // The next press is answered again.
        Assert.True(presses.TryAnswer(160));
        Assert.False(presses.TryAnswer(160));
    }
}
