using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// AetherFrame's child windows: its panels, lists, grids and the canvas all open through
/// <see cref="Begin"/>, never <c>ImRaii.Child</c> or <c>ImGui.BeginChild</c> (AetherChildTests).
/// While the screen eyedropper picks (issue #120), a child takes no mouse input, as its window
/// takes none (<c>AetherWindowChrome.ApplyPolicy</c>). ImGui gives a child none of its window's
/// NoMouseInputs and hovers whichever child is under the pointer, so without this, on another
/// monitor, a pick's click would still select or drag on the canvas, or move a slider, in the
/// panel under it.
/// </summary>
internal static class AetherChild
{
    /// <summary><c>ImRaii.Child</c>, taking no mouse input while the eyedropper picks.</summary>
    internal static ImRaii.ChildDisposable Begin(string id, Vector2 size, bool border = false, ImGuiWindowFlags flags = ImGuiWindowFlags.None) =>
        ImRaii.Child(id, size, border, ScreenEyedropper.ClaimsInput ? flags | ImGuiWindowFlags.NoMouseInputs : flags);

    /// <summary>
    /// Around a widget that opens a child window of ImGui's own, which <see cref="Begin"/> can't
    /// reach (a multi-line text box): while the eyedropper picks, the widget takes no click, and it
    /// looks as it always does. Dispose right after the widget.
    /// </summary>
    internal static Inert WhilePicking() => new(ScreenEyedropper.ClaimsInput);

    /// <summary>What <see cref="WhilePicking"/> returns.</summary>
    internal readonly struct Inert : IDisposable
    {
        private readonly bool active;

        internal Inert(bool active)
        {
            this.active = active;
            if (active)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.DisabledAlpha, 1f);
                ImGui.BeginDisabled();
            }
        }

        public void Dispose()
        {
            if (active)
            {
                ImGui.EndDisabled();
                ImGui.PopStyleVar();
            }
        }
    }
}
