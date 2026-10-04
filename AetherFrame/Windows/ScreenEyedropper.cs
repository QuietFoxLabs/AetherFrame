using System;
using System.Numerics;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

namespace AetherFrame.Windows;

/// <summary>
/// The screen eyedropper in the editors (issue #120). Each color control has an eyedropper button
/// beside it (<see cref="Button"/>); while a pick is under way, <see cref="Draw"/>, run every frame
/// after the windows:
/// <list type="bullet">
/// <item>covers the game's window with a clear, focused window, so a click there picks and reaches
/// neither the game nor any window under it (Dalamud keeps the mouse from the game while ImGui
/// has it). AetherFrame's own windows and their panels also stop taking the mouse
/// (<see cref="ClaimsInput"/>, <c>AetherWindowChrome.ApplyPolicy</c>, <see cref="AetherChild"/>): one
/// on another monitor is a window of its own, out from under the cover;</item>
/// <item>claims the keyboard (<c>io.WantTextInput</c>, as <see cref="PopupEscapeGuard"/> does), so
/// Escape, Enter and Space reach the eyedropper alone, never the game or a window's close
/// hotkey;</item>
/// <item>shows the color under the pointer, with how to pick and cancel, beside the pointer.</item>
/// </list>
/// Outside the game's window the mouse belongs to Windows: the buttons are read wherever the
/// pointer is, so a click there picks too, but it also reaches the window clicked. Enter or Space
/// picks without clicking. What each press means is <see cref="Eyedropper"/>'s; what is read where,
/// <see cref="DalamudScreenColorReader"/>'s. Render thread only.
/// </summary>
internal static class ScreenEyedropper
{
    private const string ButtonTooltip =
        "Pick a color from anywhere on your screen: the Plate, the game, or another window.\n"
        + "Click (or press Enter) on the color you want. Escape or right-click cancels.";

    private const ImGuiWindowFlags CatcherFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollWithMouse;

    private static Eyedropper? eyedropper;
    private static DalamudScreenColorReader? reader;

    /// <summary>Once, as the plugin starts.</summary>
    internal static void Initialize(ITextureProvider textures, ITextureReadbackProvider readback, IAetherFrameLog log)
    {
        reader = new DalamudScreenColorReader(textures, readback, () => DalamudServices.PluginInterface.UiBuilder.WindowHandlePtr, log);
        eyedropper = new Eyedropper(reader);
    }

    /// <summary>As the plugin stops: any pick ends with nothing changed.</summary>
    internal static void Shutdown()
    {
        eyedropper?.Stop();
        eyedropper = null;
        reader = null;
    }

    /// <summary>
    /// Whether a pick has the mouse and keyboard: AetherFrame's windows and their panels then take no
    /// mouse input, wherever they are (<c>AetherWindowChrome.ApplyPolicy</c>, <see cref="AetherChild"/>).
    /// </summary>
    internal static bool ClaimsInput => eyedropper?.ClaimsInput == true;

    /// <summary>The room <see cref="Button"/> takes after a control on its line, to keep for it.</summary>
    internal static float ButtonRoom => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;

    /// <summary>For a color control that fills its line: makes the next control leave room for the button.</summary>
    internal static void LeaveRoom() => ImGui.SetNextItemWidth(-ButtonRoom);

    /// <summary>
    /// The eyedropper button, on the line of the color control drawn just before it. True on the frame
    /// a color picked with it arrives, with <paramref name="color"/> set to it (its alpha kept): the
    /// caller applies it as one undoable edit, as a finished change of the control.
    /// </summary>
    /// <param name="id">The control's id, unique in its window.</param>
    /// <param name="color">The control's color.</param>
    internal static bool Button(string id, ref Vector4 color)
    {
        ImGui.SameLine();
        var key = ImGui.GetID($"Eyedropper##{id}");
        var arrived = false;
        if (eyedropper is not null && eyedropper.TryTakePick(key, out var picked))
        {
            color = new Vector4(picked, color.W);
            arrived = true;
        }

        var picking = eyedropper?.IsPickingFor(key) == true;
        using (ImRaii.Disabled(eyedropper is null))
        {
            if (EditorWidgets.IconToggle($"Eyedropper{id}", FontAwesomeIcon.EyeDropper, picking, ButtonTooltip))
            {
                eyedropper?.Start(key, Environment.TickCount64, ScreenPixels.KeysDown());
            }
        }

        // A pick whose control is no longer drawn (its window closed) ends by itself.
        eyedropper?.SeeOwner(key, ImGui.GetFrameCount());
        return arrived;
    }

    /// <summary>Every frame, after the windows: what the player did, and the pick's own windows.</summary>
    internal static void Draw()
    {
        if (eyedropper is not { } current)
        {
            return;
        }

        if (current.Phase == EyedropperPhase.Idle)
        {
            current.Update(new EyedropperInput(ImGui.GetFrameCount(), Environment.TickCount64, null, false, false, false, false, false));
            return;
        }

        current.Update(new EyedropperInput(
            ImGui.GetFrameCount(),
            Environment.TickCount64,
            ScreenPixels.Pointer(),
            PickButtonDown: ScreenPixels.ButtonDown(primary: true) || ImGui.IsMouseDown(ImGuiMouseButton.Left),
            CancelButtonDown: ScreenPixels.ButtonDown(primary: false) || ImGui.IsMouseDown(ImGuiMouseButton.Right),
            PickKeyPressed: ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false) || ImGui.IsKeyPressed(ImGuiKey.Space, false),
            CancelKeyPressed: ImGui.IsKeyPressed(ImGuiKey.Escape, false),
            // Windows' own key state too, as the pick started with (Start's keysHeld): a key held
            // since before ImGui saw it go down never reads as down to ImGui, and its key repeat
            // would then count as a fresh press.
            KeysDown: ScreenPixels.KeysDown() || ImGui.IsKeyDown(ImGuiKey.Escape) || ImGui.IsKeyDown(ImGuiKey.Enter) || ImGui.IsKeyDown(ImGuiKey.KeypadEnter) || ImGui.IsKeyDown(ImGuiKey.Space)));

        if (!current.ClaimsInput)
        {
            return;
        }

        // The keyboard is the eyedropper's until the press that ended the pick is let go: Dalamud
        // hands the next key messages to ImGui alone while this is set (see PopupEscape).
        var io = ImGui.GetIO();
        io.WantTextInput = true;

        DrawCatcher();
        if (current.ShowsPreview)
        {
            DrawPreview(current);
        }
    }

    /// <summary>A clear window over the whole game window, in front and focused: clicks there are the eyedropper's.</summary>
    private static void DrawCatcher()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(viewport.Pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(viewport.Size, ImGuiCond.Always);
        ImGui.SetNextWindowFocus();
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        {
            ImGui.Begin("AetherFrame Eyedropper##AetherFrameEyedropper", CatcherFlags);
            ImGui.End();
        }
    }

    /// <summary>Beside the pointer, never under it: the color there, and how to pick or cancel.</summary>
    private static void DrawPreview(Eyedropper current)
    {
        ImGui.BeginTooltip();
        var swatch = new Vector2(ImGui.GetFrameHeight() * 1.5f);
        if (current.Live is { } live)
        {
            ImGui.ColorButton("##EyedropperLive", new Vector4(live, 1f), ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoPicker, swatch);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Eyedropper.Hex(live));
        }
        else if (current.Problem is null)
        {
            ImGui.TextUnformatted("Reading...");
        }

        if (current.Problem is { } problem)
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.Warning))
            {
                ImGui.TextWrapped(problem);
            }

            ImGui.PopTextWrapPos();
        }

        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextMuted))
        {
            ImGui.TextUnformatted("Click or Enter: pick  ·  Escape or right-click: cancel");
            ImGui.TextUnformatted("Outside the game, Enter picks without clicking that window.");
        }

        ImGui.EndTooltip();
    }
}
