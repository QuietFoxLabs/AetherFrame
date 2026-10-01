using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Theme;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows.Network;

/// <summary>
/// The search for another player's Plate (NETWORK2's N2-10; decisions V5 and C5): a full name and
/// a World. The Plate itself opens in the Plate Viewer, as any Plate does
/// (<see cref="ServedPlatePresentation"/>), and so does one chosen from the game's right-click
/// menu. It offers nothing until one of the player's characters shares (V1). A name or World is
/// only ever drawn unformatted, never in an ImGui label (N7). Compiled only in the networking
/// preview flavour.
/// </summary>
internal sealed class PlateViewerWindow : Window
{
    internal const string NotSharing = "Viewing other players' Plates is part of sharing. Turn sharing on for one of your characters first, in My Plates' Sharing window: then you can view other players who share too, from the game's right-click menu on their character or by searching for them here.";
    internal const string Address = "Looking a Plate up sends your network address to AetherFrame's server, which doesn't store it, and keeps no record of who viewed whom.";

    private const int NameBytes = 64;

    private readonly PlateViewing viewing;
    private readonly Func<IReadOnlyList<string>> worlds;
    private readonly Func<CharacterContext?> currentCharacter;
    private readonly Action showPlate;
    private readonly AetherWindowChrome chrome = new();
    private string name = "";
    private string world = "";

    /// <param name="showPlate">Opens the Plate Viewer on the Plate asked for.</param>
    internal PlateViewerWindow(PlateViewing viewing, Func<IReadOnlyList<string>> worlds, Func<CharacterContext?> currentCharacter, Action showPlate)
        : base("AetherFrame Plates##AetherFramePlateViewer", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.viewing = viewing;
        this.worlds = worlds;
        this.currentCharacter = currentCharacter;
        this.showPlate = showPlate;
        RespectCloseHotkey = true;
    }

    /// <summary>Opens the search.</summary>
    internal void Open()
    {
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw()
    {
        chrome.PushStyle();
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

    public override void Draw()
    {
        var width = EditorWidgets.Scaled(520f);
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width))
        {
            if (!viewing.CanView)
            {
                Muted(NotSharing);
                return;
            }

            Muted("Search for a player by their character's full name and World. You can also right-click their character in game and choose View AetherFrame Plate.");
            ImGui.Spacing();
            DrawSearch(width);
            ImGui.Spacing();
            Muted(Address);
        }
    }

    private static void Muted(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextMuted))
        {
            ImGui.TextUnformatted(text);
        }
    }

    private void DrawSearch(float width)
    {
        if (world.Length == 0 && currentCharacter() is { HomeWorld: { } home })
        {
            world = home;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var viewWidth = ImGui.CalcTextSize("View").X + (ImGui.GetStyle().FramePadding.X * 2f);
        var worldWidth = EditorWidgets.Scaled(150f);
        var nameWidth = MathF.Max(EditorWidgets.Scaled(120f), width - worldWidth - viewWidth - (spacing * 2f));

        ImGui.SetNextItemWidth(nameWidth);
        var entered = ImGui.InputTextWithHint("##AetherFrameViewerName", "Character name, e.g. Aria Starfall", ref name, NameBytes, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(worldWidth);
        using (var combo = ImRaii.Combo("##AetherFrameViewerWorld", world.Length == 0 ? "World" : world))
        {
            if (combo.Success)
            {
                foreach (var option in worlds())
                {
                    if (ImGui.Selectable(option, string.Equals(option, world, StringComparison.Ordinal)))
                    {
                        world = option;
                    }
                }
            }
        }

        ImGui.SameLine();
        var valid = PlateViewing.IsName(name) && PlateViewing.IsWorld(world);
        using (ImRaii.Disabled(!valid))
        {
            if ((AetherControls.PrimaryButton("View##AetherFrameViewerSearch") || entered) && valid && viewing.Open(name, world))
            {
                // The Plate opens in the Plate Viewer; the search has done its job.
                showPlate();
                IsOpen = false;
            }
        }
    }
}
