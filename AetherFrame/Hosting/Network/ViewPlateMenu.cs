using System;
using System.Collections.Generic;
using AetherFrame.Services.Network.Sharing;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using World = Lumina.Excel.Sheets.World;

namespace AetherFrame.Hosting.Network;

/// <summary>
/// <b>View AetherFrame Plate</b> in the game's right-click menu on a player character (decision C5):
/// in the world, the party list, the friend list and names in chat. It is offered only while one
/// of the player's characters shares (V1), and it looks the character up by name and World only
/// when chosen, never when the menu opens. Compiled only in the networking preview flavour.
/// </summary>
internal sealed class ViewPlateMenu : IDisposable
{
    internal const string Label = "View AetherFrame Plate";

    // C5's menus: the world (no addon), the party list, the friend list, and names in chat.
    private static readonly HashSet<string> Addons = new(StringComparer.Ordinal) { "_PartyList", "PartyMemberList", "FriendList", "ChatLog" };

    private readonly IContextMenu menu;
    private readonly PlateViewing viewing;
    private readonly Action open;

    internal ViewPlateMenu(IContextMenu menu, PlateViewing viewing, Action open)
    {
        this.menu = menu ?? throw new ArgumentNullException(nameof(menu));
        this.viewing = viewing ?? throw new ArgumentNullException(nameof(viewing));
        this.open = open ?? throw new ArgumentNullException(nameof(open));
        menu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => menu.OnMenuOpened -= OnMenuOpened;

    /// <summary>The Worlds a player can search: the game's public Worlds, by name.</summary>
    internal static IReadOnlyList<string> PublicWorlds(IDataManager data)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var world in data.GetExcelSheet<World>())
        {
            var name = world.Name.ExtractText();
            if (world.IsPublic && PlateViewing.IsWorld(name))
            {
                names.Add(name);
            }
        }

        return [.. names];
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default || (args.AddonName is { } addon && !Addons.Contains(addon)) || args.Target is not MenuTargetDefault target)
        {
            return;
        }

        var name = target.TargetName;
        var world = target.TargetHomeWorld.ValueNullable?.Name.ExtractText();
        if (!PlateViewing.IsName(name) || !PlateViewing.IsWorld(world) || !viewing.CanView)
        {
            return;
        }

        args.AddMenuItem(new MenuItem
        {
            Name = Label,
            PrefixChar = 'A',
            OnClicked = _ =>
            {
                if (viewing.Open(name, world!))
                {
                    open();
                }
            },
        });
    }
}
