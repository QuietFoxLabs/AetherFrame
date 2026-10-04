using System;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services.Fonts;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Theme;
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
///
/// Each row shows what its font looks like (issue #116, <see cref="FontListLayout"/>): the family's
/// name in the interface font, so it can always be read, then the same sample in the family's own
/// face, on one baseline; the chosen family's row is highlighted, with an accent bar at its edge.
/// A face is built only for a row on screen, a few a frame (<see cref="FontPreviews{TAtlas, THandle}"/>),
/// and until it is, the row shows its sample in the interface font, at the same height. The
/// framework thread only.
/// </summary>
internal static class FontPicker
{
    private const string UnknownLabel = "A font this AetherFrame doesn't have";

    /// <summary>The fonts' list memory, shared by both editors' pickers (one list, one place in it).</summary>
    private static ChooserMemory Memory => ChooserMemories.For("Fonts");

    /// <summary>The list's tallest height, as a share of the screen's work area.</summary>
    private const float MaxListScreenShare = 0.6f;

    // Each row's ImGui id, made once: the catalog never changes, and the list is drawn every frame it is open.
    private static readonly string[] RowIds = ProfileFontCatalog.All.Select(family => "##" + family.Id).ToArray();

    /// <summary>Draws the control for <paramref name="currentFamilyId"/>, its rows' samples drawn with
    /// <paramref name="fonts"/>; true, with the family chosen, when the player picked one.</summary>
    internal static bool Draw(string id, string? currentFamilyId, ProfileFontService fonts, out string chosen)
    {
        chosen = currentFamilyId ?? ProfileFontFamilies.DalamudDefault;
        var known = currentFamilyId is null || currentFamilyId == ProfileFontFamilies.DalamudDefault || ProfileFontCatalog.Resolve(currentFamilyId).Id == currentFamilyId;
        var preview = known ? ProfileFontCatalog.Resolve(currentFamilyId).DisplayName : UnknownLabel;
        var comboWidth = ImGui.CalcItemWidth();
        using var combo = ImRaii.Combo(id, preview, ImGuiComboFlags.HeightLargest);
        if (!combo.Success)
        {
            return false;
        }

        // The search box is in the popup and the list scrolls in a child under it (see ChooserScroll),
        // so the box stays in view however far down the list is. A remembered scroll position means the
        // same place only for rows of the same height, which the interface's scale sets.
        var memory = Memory;
        var appearing = ImGui.IsWindowAppearing();
        var row = Row();
        var opening = ChooserScroll.Open(memory, appearing, chosen, (familyId, query) => Matches(ProfileFontCatalog.Resolve(familyId), query), (int)row.Height);
        if (appearing)
        {
            ImGui.SetKeyboardFocusHere();
        }

        var search = memory.Search;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##FontSearch", "Search fonts", ref search, 64, ImGuiInputTextFlags.AutoSelectAll))
        {
            memory.Search = search;
        }

        var query = search.Trim();
        var style = ImGui.GetStyle();
        var columns = Columns(row);
        var listSize = new Vector2(
            MathF.Max(comboWidth - (style.WindowPadding.X * 2f), columns.Width + style.ScrollbarSize + (style.FramePadding.X * 2f)),
            MathF.Min(ListHeight(query, row), ImGui.GetMainViewport().WorkSize.Y * MaxListScreenShare));

        var changed = false;
        using (var list = ImRaii.Child("##FontList", listSize, false))
        {
            if (list.Success)
            {
                ChooserScroll.Restore(opening, appearing);

                // No face is asked for on the frame the list appears: it is still scrolled where the
                // last chooser left it until the next frame (see ChooserScroll), so those rows aren't
                // the ones the player will see.
                var previews = appearing ? null : fonts;
                string? heading = null;
                var all = ProfileFontCatalog.All;
                for (var i = 0; i < all.Count; i++)
                {
                    var family = all[i];
                    if (!Matches(family, query))
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
                    if (DrawRow(family, RowIds[i], selected, row, columns, previews))
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

                ChooserScroll.End(memory, appearing, chosen, (int)row.Height);
            }
        }

        if (changed)
        {
            ImGui.CloseCurrentPopup();
        }

        return changed;
    }

    /// <summary>
    /// One family's row: a selectable as tall as every row, then, while it is on screen, its name and
    /// its sample on the row's baseline. True when it was chosen.
    /// </summary>
    private static bool DrawRow(ProfileFontFamilyDescriptor family, string rowId, bool selected, in FontListRow row, in ListColumns columns, ProfileFontService? fonts)
    {
        // Where a label would go: the row's highlight reaches half the item spacing beyond it.
        var top = ImGui.GetCursorScreenPos();
        var rowEnd = top.X + ImGui.GetContentRegionAvail().X;
        var chosen = ImGui.Selectable(rowId, selected, ImGuiSelectableFlags.None, new Vector2(0f, row.Height));
        if (!ImGui.IsItemVisible())
        {
            return chosen;
        }

        var drawList = ImGui.GetWindowDrawList();
        if (selected)
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            drawList.AddRectFilled(min, new Vector2(min.X + EditorWidgets.Scaled(AetherMetrics.AccentBarWidth), max.Y), ImGui.GetColorU32(AetherPalette.Aether));
        }

        // The name, in the interface font, on the baseline.
        var baseline = top.Y + row.Baseline;
        var interfaceTop = MathF.Round(baseline - InterfaceAscent());
        drawList.AddText(new Vector2(top.X + columns.NameX, interfaceTop), ImGui.GetColorU32(ImGuiCol.Text), family.DisplayName);

        // The sample, in the family's own face once it is built; until then in the interface font,
        // dimmed, where the face's will be.
        var sampleX = top.X + columns.SampleX;
        if (fonts?.GetPreview(family.Id, row.SampleSize) is not { } face)
        {
            drawList.AddText(new Vector2(sampleX, interfaceTop), ImGui.GetColorU32(ImGuiCol.TextDisabled), FontPreview.Sample);
            return chosen;
        }

        using (face.Push())
        {
            var font = ImGui.GetFont();
            var scale = FontListLayout.SampleScale(row, font.Ascent, -font.Descent, ImGui.CalcTextSize(FontPreview.Sample).X, rowEnd - sampleX);
            var y = baseline - (font.Ascent * scale);
            drawList.AddText(font, font.FontSize * scale, new Vector2(sampleX, scale < 1f ? y : MathF.Round(y)), ImGui.GetColorU32(ImGuiCol.Text), FontPreview.Sample);
        }

        return chosen;
    }

    /// <summary>The rows for the interface font now: samples at its preview tier, names beside them.</summary>
    private static FontListRow Row()
    {
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize();
        var scale = font.FontSize > 0f ? size / font.FontSize : 1f;
        return FontListLayout.Row(FontPreview.Size(size), font.Ascent * scale, -font.Descent * scale);
    }

    /// <summary>The interface font's ascent at its current size: where a name sits above the baseline.</summary>
    private static float InterfaceAscent()
    {
        var font = ImGui.GetFont();
        return font.FontSize > 0f ? font.Ascent * (ImGui.GetFontSize() / font.FontSize) : ImGui.GetFontSize();
    }

    /// <summary>Where a row's name and sample start, from its left edge, and the width the list needs.</summary>
    private readonly record struct ListColumns(float NameX, float SampleX, float Width);

    /// <summary>
    /// The columns, measured over the whole catalog: the names' column is as wide as the widest name,
    /// so every sample starts at one x, and the list keeps one width while searching.
    /// </summary>
    private static ListColumns Columns(in FontListRow row)
    {
        var widestName = 0f;
        var widestOther = ImGui.CalcTextSize(UnknownLabel).X;
        foreach (var family in ProfileFontCatalog.All)
        {
            widestName = MathF.Max(widestName, ImGui.CalcTextSize(family.DisplayName).X);
            widestOther = MathF.Max(widestOther, ImGui.CalcTextSize(GroupOf(family)).X);
        }

        var nameX = EditorWidgets.Scaled(AetherMetrics.SpaceXs);
        var sampleX = nameX + widestName + EditorWidgets.Scaled(AetherMetrics.SpaceLg);
        return new ListColumns(nameX, sampleX, MathF.Max(widestOther, sampleX + FontListLayout.SampleColumn(row.SampleSize)));
    }

    private static bool Matches(ProfileFontFamilyDescriptor family, string query) =>
        query.Length == 0 || family.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>The list's full height for <paramref name="query"/>: its rows and headings (see the loop
    /// in <see cref="Draw"/>), so a short result list isn't padded out.</summary>
    private static float ListHeight(string query, in FontListRow row)
    {
        var rows = 0;
        var headings = 0;
        string? heading = null;
        foreach (var family in ProfileFontCatalog.All)
        {
            if (!Matches(family, query))
            {
                continue;
            }

            rows++;
            var group = GroupOf(family);
            if (!ReferenceEquals(group, heading))
            {
                heading = group;
                headings++;
            }
        }

        // A heading is a spacing and a line of text; a row is its own height. Each is followed by the item spacing.
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var line = ImGui.GetTextLineHeightWithSpacing();
        return rows == 0 ? line : (rows * (row.Height + spacing)) + (headings * (line + spacing));
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
