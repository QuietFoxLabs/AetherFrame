using System;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services.Fonts;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Both editors' Font control: a dropdown of every family (<see cref="ProfileFontCatalog.All"/>),
/// grouped under its category, with a search box at its top that filters by name. A text whose
/// family this build doesn't know (one a newer AetherFrame saved) shows that, rather than
/// pretending to be the first family. Both editors' pickers share one <see cref="ChooserMemory"/>
/// (issue #114): the search is kept between openings, and reopening returns the list to where it was
/// (or to the chosen font, if it changed elsewhere), so trying nearby fonts needs no scrolling back.
/// The framework thread only.
/// </summary>
internal static class FontPicker
{
    private const string UnknownLabel = "A font this AetherFrame doesn't have";

    /// <summary>The fonts' list memory, shared by both editors' pickers (one list, one place in it).</summary>
    private static ChooserMemory Memory => ChooserMemories.For("Fonts");

    /// <summary>Draws the control for <paramref name="currentFamilyId"/>; true, with the family chosen, when the player picked one.</summary>
    internal static bool Draw(string id, string? currentFamilyId, out string chosen)
    {
        chosen = currentFamilyId ?? ProfileFontFamilies.DalamudDefault;
        var known = currentFamilyId is null || currentFamilyId == ProfileFontFamilies.DalamudDefault || ProfileFontCatalog.Resolve(currentFamilyId).Id == currentFamilyId;
        var preview = known ? ProfileFontCatalog.Resolve(currentFamilyId).DisplayName : UnknownLabel;
        using var combo = ImRaii.Combo(id, preview, ImGuiComboFlags.HeightLargest);
        if (!combo.Success)
        {
            return false;
        }

        var memory = Memory;
        var opening = ChooserScroll.Begin(memory, chosen, (familyId, query) => ProfileFontCatalog.Resolve(familyId).DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        var search = memory.Search;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##FontSearch", "Search fonts", ref search, 64, ImGuiInputTextFlags.AutoSelectAll))
        {
            memory.Search = search;
        }

        var changed = false;
        string? heading = null;
        var query = search.Trim();
        foreach (var family in ProfileFontCatalog.All)
        {
            if (query.Length > 0 && !family.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var group = GroupOf(family);
            if (!ReferenceEquals(group, heading))
            {
                heading = group;
                ImGui.Spacing();
                ImGui.TextDisabled(group);
            }

            var selected = family.Id == chosen;
            if (ImGui.Selectable(family.DisplayName + "##" + family.Id, selected))
            {
                chosen = family.Id;
                changed = true;
            }

            ChooserScroll.ScrollHereIfOpening(opening, selected);
        }

        if (heading is null)
        {
            ImGui.TextDisabled("No font has that name.");
        }

        ChooserScroll.End(memory, chosen);
        return changed;
    }

    /// <summary>The heading a family is listed under.</summary>
    internal static string GroupOf(ProfileFontFamilyDescriptor family) => family.Id == ProfileFontFamilies.DalamudDefault
        ? "Game default"
        : family.Category switch
        {
            FontCategory.Fantasy => "Fantasy & medieval",
            FontCategory.Script => "Script & handwriting",
            FontCategory.Serif => "Serif",
            FontCategory.Sans => "Sans-serif",
            FontCategory.Display => "Display",
            FontCategory.Mono => "Monospace",
            _ => "AetherFrame",
        };
}
