using System;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// The ImGui side of <see cref="PopupEscape"/>: Escape on a popup closes that popup, never the
/// window behind it. A window that opens popups (menus, lists, color pickers, prompts) owns one and
/// starts its Draw with <c>using var popupEscape = escape.Update(this);</c>, so the guard runs
/// first and looks again once the window's content is drawn; every prompt's Cancel also answers
/// <see cref="CancelsPrompt"/>, so Escape cancels the prompt. Render thread only, like every ImGui call.
/// </summary>
internal sealed class PopupEscapeGuard
{
    // One press answers one popup, across every window (see EscapePresses).
    private static readonly EscapePresses Presses = new();

    private readonly PopupEscape rule = new();

    /// <summary>
    /// First thing in the window's Draw: closes the menus, lists and pickers open over the window
    /// when Escape is pressed on one of them, sets the window's <c>RespectCloseHotkey</c> for this
    /// frame, and claims the keyboard while one of its popups has focus, so that Escape reaches the
    /// popup, never the game, and Dalamud closes the window only for a press made with none of its
    /// popups open. Dispose the result at the end of the Draw (a <c>using</c> declaration does): it
    /// claims the keyboard for a popup opened during the Draw.
    /// </summary>
    /// <param name="window">The window being drawn.</param>
    /// <param name="respectsCloseHotkey">Whether the window's own rules let Escape close it now.</param>
    /// <returns>The rest of the window's Draw.</returns>
    internal DrawScope Update(Window window, bool respectsCloseHotkey = true)
    {
        var popupFocused = PopupFocused();
        var action = rule.Update(
            popupFocused,
            promptFocused: popupFocused && FocusedPopupIsPrompt(),
            escapePressed: ImGui.IsKeyPressed(ImGuiKey.Escape, false),
            imguiEscapeDown: ImGui.IsKeyDown(ImGuiKey.Escape),
            gameEscapeDown: DalamudServices.KeyState[VirtualKey.ESCAPE]);

        if (action == PopupEscapeAction.CloseMenus && Presses.TryAnswer(ImGui.GetFrameCount()))
        {
            // Every menu, list and picker over the topmost prompt, or over the window when no
            // prompt is open: Escape on a submenu closes its menu too.
            ImGuiP.ClosePopupsExceptModals();
        }

        window.RespectCloseHotkey = respectsCloseHotkey && rule.RespectsCloseHotkey;
        ClaimKeyboard(rule.ClaimsKeyboard);
        return new DrawScope(this);
    }

    /// <summary>
    /// Inside a prompt (a modal popup), beside its Cancel: true in the frame Escape is pressed while
    /// this prompt itself has focus (not a menu or list opened over it), once per press. The prompt
    /// then does what its Cancel does.
    /// </summary>
    internal static bool CancelsPrompt() =>
        ImGui.IsKeyPressed(ImGuiKey.Escape, false)
        && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows | ImGuiFocusedFlags.NoPopupHierarchy)
        && Presses.TryAnswer(ImGui.GetFrameCount());

    /// <summary>
    /// Whether one of the current window's popups has focus. Dalamud's focus check counts a popup
    /// the window opened as the window; without the popup hierarchy, only the window itself and its
    /// child regions count.
    /// </summary>
    private static bool PopupFocused() =>
        ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
        && !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows | ImGuiFocusedFlags.NoPopupHierarchy);

    /// <summary>
    /// Claims the keyboard until the next frame: Dalamud hands the next key messages to ImGui alone
    /// while <c>io.WantTextInput</c> is set (see <see cref="PopupEscape"/>). ImGui.NewFrame clears
    /// the flag, so it is set again every frame the claim holds. It is never cleared here: a text
    /// field, or another window, may want it.
    /// </summary>
    private static void ClaimKeyboard(bool claim)
    {
        if (claim)
        {
            var io = ImGui.GetIO();
            io.WantTextInput = true;
        }
    }

    /// <summary>Whether the popup with keyboard focus (it, or a child region of it) is a prompt.</summary>
    private static bool FocusedPopupIsPrompt()
    {
        var focused = ImGui.GetCurrentContext().NavWindow;
        if (focused.IsNull)
        {
            return false;
        }

        var popup = focused.RootWindow;
        return !popup.IsNull && (popup.Flags & ImGuiWindowFlags.Modal) != 0;
    }

    /// <summary>
    /// The rest of a window's Draw, from <see cref="Update"/>. Disposed once the window's content is
    /// drawn (still inside the window), it claims the keyboard for a popup opened during the Draw,
    /// so the very next press reaches that popup instead of the game.
    /// </summary>
    internal readonly struct DrawScope : IDisposable
    {
        private readonly PopupEscapeGuard? guard;

        internal DrawScope(PopupEscapeGuard guard) => this.guard = guard;

        public void Dispose()
        {
            if (guard is not null)
            {
                ClaimKeyboard(guard.rule.ClaimsKeyboardAfterDraw(PopupFocused()));
            }
        }
    }
}
