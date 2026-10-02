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
/// <para><b>Who gets a key</b> (read from Dalamud 15.0.3.6: <c>Win32InputHandler.ProcessWndProcW</c>
/// and <c>ProcessKeyEventsWorkarounds</c>, <c>InterfaceManager.OnNewInputFrame</c>): Dalamud hands
/// a key to ImGui only while <c>io.WantTextInput</c> is set, and then keeps it from the game.
/// Otherwise the key goes to the game alone: ImGui sees only Shift, Ctrl and Alt, and every other
/// key is let go of in ImGui each frame. ImGui.NewFrame sets the flag only for a text field in use,
/// and Dalamud reads it between frames, so one set during a Draw decides where the next key
/// messages go. While it is set, Dalamud also clears the game's key state every frame.</para>
///
/// <para><b>Dalamud's close rule</b> (<c>WindowHost.DrawInternal</c>): right after a window's
/// Draw, Dalamud closes the window when the game's Escape key is down, the window is focused, no
/// window has closed for this press yet (a latch every window shares, cleared once Escape is up),
/// and the window's <c>RespectCloseHotkey</c> is on. "Focused" is
/// <c>ImGui.IsWindowFocused(RootAndChildWindows)</c>, which counts a popup the window opened as the
/// window itself. So, before this rule, Escape on a menu went to the game, and Dalamud closed the
/// whole window (an editor with unsaved changes asked Save, Discard or Cancel instead), while ImGui,
/// which never saw the key, closed no popup.</para>
///
/// <para><b>This rule:</b> while one of the window's popups has focus, the window claims the
/// keyboard (<see cref="ClaimsKeyboard"/>, decided again every frame): Escape then reaches ImGui
/// and never the game, so Dalamud can't close the window for it. The press closes the popup: a
/// menu, list or picker as a click outside it would (<see cref="PopupEscapeAction.CloseMenus"/>),
/// and a prompt as its own Cancel, which the prompt answers itself. The claim lasts until Escape is
/// released, so the whole press, however long it is held, is the popup's, and its release reaches
/// ImGui too. A press with no popup open reaches the game, and Dalamud closes the window, as
/// before.</para>
///
/// <para>The edges. A popup opened during the window's Draw is claimed for once the content is
/// drawn (<see cref="ClaimsKeyboardAfterDraw"/>), so the very next press reaches it. A press made
/// while one of the window's popups had focus, or had it the frame before (a claim made at the
/// start of that frame still holds), is the popup's until Escape is up again: the claim holds for
/// it, and the window doesn't respect the close hotkey meanwhile. With the claim, the game never
/// sees such a press, so that last part is a second line of defence, for one that reaches the game
/// all the same. A press the game already holds (the Escape that closed an editor, which asks about
/// unsaved changes instead) stays the game's until it is released: claiming it midway would give
/// ImGui its key repeats as a new press, which would cancel the question that press raised.</para>
/// </summary>
internal sealed class PopupEscape
{
    private bool popupLastFrame;
    private bool heldForPopup;
    private bool gameHoldsEscape;

    /// <summary>Whether Dalamud may close the window with Escape this frame, as the last <see cref="Update"/> decided.</summary>
    internal bool RespectsCloseHotkey { get; private set; } = true;

    /// <summary>
    /// Whether the window claims the keyboard from the start of this frame's Draw, as the last
    /// <see cref="Update"/> decided: the ImGui side sets <c>io.WantTextInput</c>, so the next key
    /// messages go to ImGui alone.
    /// </summary>
    internal bool ClaimsKeyboard { get; private set; }

    /// <summary>
    /// Once a frame, first thing in the window's Draw, before it draws any of its popups.
    /// </summary>
    /// <param name="popupFocused">One of the window's popups has focus.</param>
    /// <param name="promptFocused">The popup with focus is a prompt (a modal popup).</param>
    /// <param name="escapePressed">Escape went down this frame, in ImGui's key state.</param>
    /// <param name="imguiEscapeDown">Escape is down in ImGui's key state: a press the keyboard claim (or a text field in use) took.</param>
    /// <param name="gameEscapeDown">Escape is down in the game's key state, which Dalamud's close rule reads: a press made while nothing claimed the keyboard.</param>
    /// <returns>What to do about the window's popups this frame.</returns>
    internal PopupEscapeAction Update(bool popupFocused, bool promptFocused, bool escapePressed, bool imguiEscapeDown, bool gameEscapeDown)
    {
        if (!escapePressed && !imguiEscapeDown && !gameEscapeDown)
        {
            heldForPopup = false;
        }
        else if (popupFocused || popupLastFrame)
        {
            heldForPopup = true;
        }

        popupLastFrame = popupFocused;
        gameHoldsEscape = gameEscapeDown;
        RespectsCloseHotkey = !heldForPopup;
        ClaimsKeyboard = Claims(popupFocused);
        return escapePressed && popupFocused && !promptFocused ? PopupEscapeAction.CloseMenus : PopupEscapeAction.None;
    }

    /// <summary>
    /// Once the window's content is drawn: whether the window claims the keyboard for the rest of
    /// the frame, so a popup it opened during this Draw takes the next press too.
    /// </summary>
    /// <param name="popupFocused">One of the window's popups has focus now.</param>
    internal bool ClaimsKeyboardAfterDraw(bool popupFocused) => Claims(popupFocused);

    private bool Claims(bool popupFocused) => (popupFocused || heldForPopup) && !gameHoldsEscape;
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
