using AetherFrame.Services;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// The ImGui side of <see cref="PopupEscape"/>: Escape on a popup closes that popup, never the
/// window behind it. A window that opens popups (menus, lists, color pickers, prompts) owns one and
/// calls <see cref="Update"/> first thing in its Draw; every prompt's Cancel also answers
/// <see cref="CancelsPrompt"/>, so Escape cancels the prompt. Render thread only, like every ImGui call.
/// </summary>
internal sealed class PopupEscapeGuard
{
    // One press answers one popup, across every window (see EscapePresses).
    private static readonly EscapePresses Presses = new();

    private readonly PopupEscape rule = new();

    /// <summary>
    /// First thing in the window's Draw: closes the menus, lists and pickers open over the window
    /// when Escape is pressed on one of them, and sets the window's <c>RespectCloseHotkey</c> for
    /// this frame, so that Dalamud closes the window only for a press made with none of its popups
    /// open.
    /// </summary>
    /// <param name="window">The window being drawn.</param>
    /// <param name="respectsCloseHotkey">Whether the window's own rules let Escape close it now.</param>
    internal void Update(Window window, bool respectsCloseHotkey = true)
    {
        // Dalamud's focus check counts a popup the window opened as the window; without the popup
        // hierarchy, only the window itself and its child regions count.
        var popupFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            && !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows | ImGuiFocusedFlags.NoPopupHierarchy);
        var pressed = ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        var down = ImGui.IsKeyDown(ImGuiKey.Escape) || DalamudServices.KeyState[VirtualKey.ESCAPE];

        if (rule.Update(popupFocused, popupFocused && FocusedPopupIsPrompt(), pressed, down) == PopupEscapeAction.CloseMenus
            && Presses.TryAnswer(ImGui.GetFrameCount()))
        {
            // Every menu, list and picker over the topmost prompt, or over the window when no
            // prompt is open: Escape on a submenu closes its menu too.
            ImGuiP.ClosePopupsExceptModals();
        }

        window.RespectCloseHotkey = respectsCloseHotkey && rule.RespectsCloseHotkey;
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
}
