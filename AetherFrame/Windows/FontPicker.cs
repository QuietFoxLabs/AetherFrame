using System;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services.Fonts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Both editors' Font control: a dropdown of every family (<see cref="ProfileFontCatalog.All"/>),
/// grouped under its category, with a search box at its top that filters by name. A text whose
/// family this build doesn't know (one a newer AetherFrame saved) shows that, rather than
/// pretending to be the first family. The framework thread only; one picker is open at a time,
/// so the search text is shared and cleared each time a picker opens.
/// </summary>
internal static class FontPicker
{
    private const string UnknownLabel = "A font this AetherFrame doesn't have";

    private static string search = string.Empty;

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

        var opening = ImGui.IsWindowAppearing();
        if (opening)
        {
            search = string.Empty;
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##FontSearch", "Search fonts", ref search, 64);

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

            if (selected && opening)
            {
                ImGui.SetScrollHereY();
            }
        }

        if (heading is null)
        {
            ImGui.TextDisabled("No font has that name.");
        }

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
