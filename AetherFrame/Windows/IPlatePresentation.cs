using System.Numerics;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.Windows;

/// <summary>
/// A Plate the Plate Viewer presents that isn't a document on this PC, such as another player's
/// shared Plate (N2-10): it gives the viewer its bounds and draws itself, so it is presented exactly
/// as the viewer presents a local Plate (floating over the game, sized to its visual bounds,
/// moved, resized and closed the same way). While it has nothing to draw, the viewer shows its
/// message instead. Every method runs on the framework thread, from the viewer's frame.
/// </summary>
internal interface IPlatePresentation
{
    /// <summary>What it shows now, in logical canvas units: its visual bounds (canvas plus overflow) and its canvas size; false while there's only a message.</summary>
    bool TryGetBounds(out CanvasBounds bounds, out Vector2 canvasSize);

    /// <summary>Draws it with its canvas's (0, 0) at <paramref name="canvasOrigin"/>, clipped to the viewer's rectangle.</summary>
    void Draw(ImDrawListPtr drawList, Vector2 canvasOrigin, float scale, Vector2 clipMin, Vector2 clipMax);

    /// <summary>What the viewer says while there's nothing to draw: plain text, drawn unformatted.</summary>
    string Message { get; }

    /// <summary>A button under the message (a fixed label), or null.</summary>
    string? MessageAction { get; }

    /// <summary>What <see cref="MessageAction"/> does.</summary>
    void RunMessageAction();

    /// <summary>Its own items at the top of the viewer's right-click menu, inside the open popup.</summary>
    void DrawMenuItems();

    /// <summary>The viewer stopped presenting it (closed, or showing something else): it lets go of what it holds.</summary>
    void Released();
}
