namespace AetherFrame.UI.Editor;

/// <summary>What Escape does about a window's popups this frame (see <see cref="PopupEscape"/>).</summary>
internal enum PopupEscapeAction
{
    /// <summary>
    /// Nothing: Escape wasn't just pressed, none of the window's popups has focus, or the one that
    /// has is a prompt, which answers Escape itself, as its Cancel.
    /// </summary>
    None,

    /// <summary>Close the menus, lists and pickers open over the window (or over its prompt), as a click outside them would.</summary>
    CloseMenus,
}

/// <summary>
/// Escape on a popup closes that popup, never the window behind it (interface task 7, in
/// docs/InterfaceAudit.md). One per window that opens popups: menus, lists, color pickers and
/// prompts (modal popups).
///
/// <para><b>Dalamud's rule</b> (read from Dalamud 15.0.3.6's <c>WindowHost.DrawInternal</c>): right
/// after a window's Draw, Dalamud closes the window when the game's Escape key is down, the window
/// is focused, no window has closed for this press yet (a latch every window shares, cleared once
/// Escape is up), and the window's <c>RespectCloseHotkey</c> is on. "Focused" is
/// <c>ImGui.IsWindowFocused(RootAndChildWindows)</c>, which counts a popup the window opened as the
/// window itself. ImGui closes no popup on Escape by itself, since Dalamud leaves ImGui's keyboard
/// navigation off. So, before this rule, Escape on a menu closed the whole window, and in an editor
/// with unsaved changes it asked Save, Discard or Cancel instead.</para>
///
/// <para><b>This rule:</b> from the frame Escape goes down while one of the window's popups has
/// focus, or had it the frame before (the popup may close before the game's key state shows the
/// press), until Escape is up again, the window doesn't respect the close hotkey. The press is
/// then the popup's alone, however long it is held, and no window closes for it. It closes the
/// popup: a menu, list or picker as a click outside it would (<see cref="PopupEscapeAction.CloseMenus"/>),
/// and a prompt as its own Cancel, which the prompt answers itself. A press with no popup open
/// closes the window, as before.</para>
/// </summary>
internal sealed class PopupEscape
{
    private bool popupLastFrame;
    private bool heldForPopup;

    /// <summary>Whether Dalamud may close the window with Escape this frame, as the last <see cref="Update"/> decided.</summary>
    internal bool RespectsCloseHotkey { get; private set; } = true;

    /// <summary>
    /// Once a frame, first thing in the window's Draw, before it draws any of its popups.
    /// </summary>
    /// <param name="popupFocused">One of the window's popups has focus.</param>
    /// <param name="promptFocused">The popup with focus is a prompt (a modal popup).</param>
    /// <param name="escapePressed">Escape went down this frame.</param>
    /// <param name="escapeDown">Escape is down, in the game's key state (what Dalamud reads) or in ImGui's.</param>
    /// <returns>What to do about the window's popups this frame.</returns>
    internal PopupEscapeAction Update(bool popupFocused, bool promptFocused, bool escapePressed, bool escapeDown)
    {
        if (!escapeDown && !escapePressed)
        {
            heldForPopup = false;
        }
        else if (popupFocused || popupLastFrame)
        {
            heldForPopup = true;
        }

        popupLastFrame = popupFocused;
        RespectsCloseHotkey = !heldForPopup;
        return escapePressed && popupFocused && !promptFocused ? PopupEscapeAction.CloseMenus : PopupEscapeAction.None;
    }
}

/// <summary>
/// One Escape press answers one popup. Closing a menu open over a prompt gives the prompt focus
/// again in the same frame, and the prompt must not then take that press as its Cancel too.
/// Presses are told apart by the ImGui frame they go down in.
/// </summary>
internal sealed class EscapePresses
{
    private int answeredFrame = int.MinValue;

    /// <summary>True for the first to answer the press of frame <paramref name="frame"/>, false for anything after it.</summary>
    internal bool TryAnswer(int frame)
    {
        if (frame == answeredFrame)
        {
            return false;
        }

        answeredFrame = frame;
        return true;
    }
}
