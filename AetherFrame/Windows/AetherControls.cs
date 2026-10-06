using System;
using System.Numerics;
using AetherFrame.Services;
using AetherFrame.UI.Theme;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>What a callout or status line is about; picks its color and icon.</summary>
internal enum AetherTone
{
    Info,
    Success,
    Warning,
    Danger,
}

/// <summary>
/// AetherFrame's reusable controls, over plain ImGui widgets: the same buttons, headings,
/// callouts and empty states in every window, so a screen reads as AetherFrame at a
/// glance. Every control here is a normal ImGui item (keyboard and gamepad navigation, tooltips
/// and ids all work as usual) with the palette applied; nothing is rebuilt from scratch. Each
/// takes unscaled sizes and scales them itself. No per-frame allocations beyond ImGui's own.
/// </summary>
internal static class AetherControls
{
    // ---------------------------------------------------------------- text

    /// <summary>A section label: small uppercase text in the accent, with a thin rule to the right edge.</summary>
    internal static void SectionHeader(string text, float topSpacing = AetherMetrics.SpaceSm)
    {
        var scale = ImGuiHelpers.GlobalScale;
        if (topSpacing > 0f)
        {
            ImGui.Dummy(new Vector2(0f, topSpacing * scale));
        }

        Vector2 size;
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        using (AetherFonts.Label())
        {
            size = ImGui.CalcTextSize(text);
            drawList.AddText(origin, ImGui.GetColorU32(AetherPalette.Aether), text);
        }

        var right = origin.X + ImGui.GetContentRegionAvail().X;
        var ruleStart = origin.X + size.X + (AetherMetrics.SpaceSm * scale);
        if (right - ruleStart > AetherMetrics.SpaceLg * scale)
        {
            var y = origin.Y + (size.Y / 2f);
            drawList.AddLine(new Vector2(ruleStart, y), new Vector2(right, y), ImGui.GetColorU32(AetherPalette.Divider), 1f);
        }

        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, size.Y));
        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceXs * scale));
    }

    /// <summary>Secondary text: a summary, a value, a card's status line. Wrapped.</summary>
    internal static void Secondary(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextSecondary))
        {
            ImGui.TextWrapped(text);
        }
    }

    /// <summary>Muted text: a hint, an explanation under a control. Wrapped.</summary>
    internal static void Muted(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextMuted))
        {
            ImGui.TextWrapped(text);
        }
    }

    /// <summary>Muted text on one line, aligned to a control row.</summary>
    internal static void MutedInline(string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, AetherPalette.TextMuted);
        try
        {
            ImGui.TextUnformatted(text);
        }
        finally
        {
            ImGui.PopStyleColor();
        }
    }

    // ---------------------------------------------------------------- buttons

    /// <summary>The one primary action on a screen: filled with the accent.</summary>
    internal static bool PrimaryButton(string label, Vector2 size = default, string? tooltip = null)
    {
        var clicked = FilledButton(label, size, AetherPalette.Aether, AetherPalette.AetherHover, AetherPalette.AetherActive, AetherPalette.TextOnAccent);
        EditorWidgets.Tooltip(tooltip);
        return clicked;
    }

    /// <summary>A button with its own four colors, pushed and popped without allocating (a button is drawn every frame).</summary>
    private static bool FilledButton(string label, Vector2 size, Vector4 fill, Vector4 hover, Vector4 active, Vector4 text)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, fill);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        try
        {
            return ImGui.Button(label, size);
        }
        finally
        {
            ImGui.PopStyleColor(4);
        }
    }

    /// <summary>An ordinary action: the raised surface.</summary>
    internal static bool SecondaryButton(string label, Vector2 size = default, string? tooltip = null)
    {
        var clicked = ImGui.Button(label, size);
        EditorWidgets.Tooltip(tooltip);
        return clicked;
    }

    /// <summary>A destructive action (Delete, Discard, Revert): filled red.</summary>
    internal static bool DangerButton(string label, Vector2 size = default, string? tooltip = null)
    {
        var clicked = FilledButton(label, size, AetherPalette.DangerButton, AetherPalette.DangerButtonHover, AetherPalette.DangerButtonActive, AetherPalette.TextOnAccent);
        EditorWidgets.Tooltip(tooltip);
        return clicked;
    }

    /// <summary>A quiet action: no surface until hovered (a Cancel, a Back).</summary>
    internal static bool GhostButton(string label, Vector2 size = default, string? tooltip = null)
    {
        var clicked = FilledButton(label, size, Vector4.Zero, AetherPalette.SurfaceHover, AetherPalette.SurfaceActive, AetherPalette.TextSecondary);
        EditorWidgets.Tooltip(tooltip);
        return clicked;
    }

    /// <summary>Moves the cursor so a row of <paramref name="rowWidth"/> ends at the content's right edge.</summary>
    internal static void AlignRight(float rowWidth)
    {
        var x = ImGui.GetWindowContentRegionMax().X - rowWidth;
        if (x > ImGui.GetCursorPosX())
        {
            ImGui.SetCursorPosX(x);
        }
    }

    /// <summary>
    /// Before a row of <paramref name="rowWidth"/> that ends at the content's right edge: on the last
    /// item's line when it fits after that item, otherwise on a line of its own, right-aligned there
    /// when that line can hold it, so it never draws over what came before. Returns whether it shares
    /// the last item's line.
    /// </summary>
    internal static bool AlignRightAfterItem(float rowWidth)
    {
        var x = ImGui.GetWindowContentRegionMax().X - rowWidth;
        var lastItemEnd = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
        if (x >= lastItemEnd + ImGui.GetStyle().ItemSpacing.X)
        {
            ImGui.SameLine(x);
            return true;
        }

        AlignRight(rowWidth);
        return false;
    }

    // ---------------------------------------------------------------- small pieces

    /// <summary>A keycap for a shortcut ("Ctrl+S").</summary>
    internal static void KeyHint(string keys)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var textSize = ImGui.CalcTextSize(keys);
        var padding = new Vector2(AetherMetrics.SpaceXs + 2f, 1f) * scale;
        var size = textSize + (padding * 2f);
        var min = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + size, ImGui.GetColorU32(AetherPalette.SurfaceRaised), AetherMetrics.RadiusSm * scale);
        drawList.AddRect(min, min + size, ImGui.GetColorU32(AetherPalette.BorderStrong), AetherMetrics.RadiusSm * scale, ImDrawFlags.None, 1f);
        drawList.AddText(min + padding, ImGui.GetColorU32(AetherPalette.TextSecondary), keys);
        ImGui.Dummy(size);
    }

    /// <summary>A wrapped tooltip for the last item (wider and softer than ImGui's default one-liner).</summary>
    internal static void Tooltip(string? text)
    {
        if (text is null || !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            return;
        }

        using (ImRaii.Tooltip())
        using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 24f))
        {
            ImGui.TextUnformatted(text);
        }
    }

    /// <summary>A thin divider with breathing room.</summary>
    internal static void Divider(float spacing = AetherMetrics.SpaceXs)
    {
        var s = spacing * ImGuiHelpers.GlobalScale;
        ImGui.Dummy(new Vector2(0f, s));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, s));
    }

    /// <summary>The color and icon of a tone.</summary>
    internal static (Vector4 Color, Vector4 Tint, FontAwesomeIcon Icon) Of(AetherTone tone) => tone switch
    {
        AetherTone.Success => (AetherPalette.Success, AetherPalette.SuccessTint, FontAwesomeIcon.CheckCircle),
        AetherTone.Warning => (AetherPalette.Warning, AetherPalette.WarningTint, FontAwesomeIcon.ExclamationTriangle),
        AetherTone.Danger => (AetherPalette.Danger, AetherPalette.DangerTint, FontAwesomeIcon.ExclamationCircle),
        _ => (AetherPalette.Info, AetherPalette.InfoTint, FontAwesomeIcon.InfoCircle),
    };

    /// <summary>A one-line status (saved, working, an error) with its icon; nothing when <paramref name="text"/> is null.</summary>
    internal static void StatusLine(AetherTone tone, string? text)
    {
        if (text is null)
        {
            return;
        }

        var (color, _, icon) = Of(tone);
        ImGui.AlignTextToFramePadding();
        EditorWidgets.IconText(icon, color);
        ImGui.SameLine(0f, AetherMetrics.ItemInnerSpacing * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        try
        {
            ImGui.TextUnformatted(text);
        }
        finally
        {
            ImGui.PopStyleColor();
        }
    }

    // ---------------------------------------------------------------- callouts and empty states

    /// <summary>
    /// A callout: a tinted box with an accent bar and icon at the left, an optional title, and
    /// wrapped text. For help that belongs on the screen, a warning that needs reading, or an
    /// error. Fills the available width.
    /// </summary>
    internal static void Callout(AetherTone tone, string text, string? title = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var (color, tint, icon) = Of(tone);
        var padding = AetherMetrics.PanelPadding * scale;
        var iconColumn = AetherMetrics.CalloutIconWidth * scale;
        var width = ImGui.GetContentRegionAvail().X;
        var textWidth = Math.Max(40f * scale, width - (padding * 2f) - iconColumn);

        var titleHeight = 0f;
        if (title is not null)
        {
            using (AetherFonts.Heading())
            {
                titleHeight = ImGui.GetTextLineHeight() + (AetherMetrics.SpaceXs * scale);
            }
        }

        var textHeight = ImGui.CalcTextSize(text, false, textWidth).Y;
        var height = titleHeight + textHeight + (padding * 2f);
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        var rounding = AetherMetrics.RadiusMd * scale;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(tint), rounding);
        drawList.AddRectFilled(min, new Vector2(min.X + (AetherMetrics.AccentBarWidth * scale), max.Y), ImGui.GetColorU32(color), rounding, ImDrawFlags.RoundCornersLeft);

        string glyph;
        Vector2 glyphSize;
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            glyph = EditorWidgets.GetIconString(icon);
            glyphSize = ImGui.CalcTextSize(glyph);
            drawList.AddText(new Vector2(min.X + padding + ((iconColumn - glyphSize.X) / 2f) - (AetherMetrics.SpaceXs * scale), min.Y + padding), ImGui.GetColorU32(color), glyph);
        }

        var textPos = new Vector2(min.X + padding + iconColumn, min.Y + padding);
        if (title is not null)
        {
            using (AetherFonts.Heading())
            {
                drawList.AddText(textPos, ImGui.GetColorU32(AetherPalette.TextPrimary), title);
            }

            textPos.Y += titleHeight;
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), textPos, ImGui.GetColorU32(AetherPalette.TextSecondary), text, textWidth);
        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>
    /// An empty state, centered in the remaining region: a large muted icon, a title, an
    /// explanation, and optionally the one action that fills the emptiness, with why it last failed
    /// (<paramref name="actionError"/>) just under it. Returns true when that action was clicked.
    /// </summary>
    internal static bool EmptyState(FontAwesomeIcon icon, string title, string description, string? actionLabel = null, string? actionTooltip = null, string? actionError = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var available = ImGui.GetContentRegionAvail();
        var textWidth = Math.Min(available.X - (AetherMetrics.SpaceXl * 2f * scale), 420f * scale);
        textWidth = Math.Max(textWidth, 120f * scale);

        float iconHeight;
        string glyph;
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            glyph = EditorWidgets.GetIconString(icon);
            iconHeight = ImGui.CalcTextSize(glyph).Y;
        }

        float titleHeight;
        using (AetherFonts.Display())
        {
            titleHeight = ImGui.GetTextLineHeight();
        }

        var descriptionHeight = ImGui.CalcTextSize(description, false, textWidth).Y;
        var buttonHeight = actionLabel is null ? 0f : ImGui.GetFrameHeight() + (AetherMetrics.SpaceMd * scale);
        var errorHeight = actionLabel is null || actionError is null ? 0f : ImGui.CalcTextSize(actionError, false, textWidth).Y + (AetherMetrics.SpaceSm * scale);
        var blockHeight = (iconHeight * 2.2f) + (AetherMetrics.SpaceMd * scale) + titleHeight + (AetherMetrics.SpaceXs * scale) + descriptionHeight + buttonHeight + errorHeight;

        var origin = ImGui.GetCursorScreenPos();
        var top = origin.Y + Math.Max(0f, (available.Y - blockHeight) / 2f);
        var centerX = origin.X + (available.X / 2f);
        var drawList = ImGui.GetWindowDrawList();

        // The icon inside a soft disc, the aether spark peeking behind it: an empty state is an invitation, not an error.
        var discRadius = iconHeight * 1.1f;
        var discCenter = new Vector2(centerX, top + discRadius);
        drawList.AddCircleFilled(discCenter, discRadius, ImGui.GetColorU32(AetherPalette.AetherFaint), 48);
        AetherBrand.DrawCorners(drawList, discCenter - new Vector2(discRadius), discCenter + new Vector2(discRadius), 3f * scale, discRadius * 0.5f, Math.Max(1f, 1.5f * scale), AetherPalette.Aether.WithOpacity(0.5f));
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var size = ImGui.CalcTextSize(glyph);
            drawList.AddText(discCenter - (size / 2f), ImGui.GetColorU32(AetherPalette.TextSecondary), glyph);
        }

        var y = top + (discRadius * 2f) + (AetherMetrics.SpaceMd * scale);
        using (AetherFonts.Display())
        {
            var size = ImGui.CalcTextSize(title);
            drawList.AddText(new Vector2(centerX - (size.X / 2f), y), ImGui.GetColorU32(AetherPalette.TextPrimary), title);
        }

        y += titleHeight + (AetherMetrics.SpaceXs * scale);
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(centerX - (textWidth / 2f), y), ImGui.GetColorU32(AetherPalette.TextMuted), description, textWidth);
        y += descriptionHeight;

        var clicked = false;
        if (actionLabel is not null)
        {
            y += AetherMetrics.SpaceMd * scale;
            var buttonWidth = Math.Max(AetherMetrics.ButtonMinWidth * scale, ImGui.CalcTextSize(actionLabel).X + (ImGui.GetStyle().FramePadding.X * 4f));
            ImGui.SetCursorScreenPos(new Vector2(centerX - (buttonWidth / 2f), y));
            clicked = PrimaryButton(actionLabel, new Vector2(buttonWidth, 0f), actionTooltip);
            y += ImGui.GetFrameHeight();

            if (actionError is not null)
            {
                y += AetherMetrics.SpaceSm * scale;
                var errorSize = ImGui.CalcTextSize(actionError, false, textWidth);
                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(centerX - (errorSize.X / 2f), y), ImGui.GetColorU32(AetherPalette.Danger), actionError, textWidth);
                y += errorSize.Y;
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, Math.Max(y, origin.Y)));
        ImGui.Dummy(new Vector2(available.X, Math.Max(0f, origin.Y + available.Y - y)));
        return clicked;
    }

    // ---------------------------------------------------------------- panels, cards, selection

    /// <summary>
    /// A card's frame: the raised surface, a border that brightens on hover, and when selected
    /// the accent ring with the frame-corner motif. Draw before the card's contents.
    /// </summary>
    internal static void CardFrame(ImDrawListPtr drawList, Vector2 min, Vector2 max, bool hovered, bool selected)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var rounding = AetherMetrics.RadiusLg * scale;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(hovered ? AetherPalette.SurfaceHover : AetherPalette.SurfaceRaised), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(hovered || selected ? AetherPalette.BorderStrong : AetherPalette.Border), rounding, ImDrawFlags.None, 1f);
        if (selected)
        {
            SelectionRing(drawList, min, max, rounding);
        }
    }

    /// <summary>The accent selection ring with the frame corners, around <paramref name="min"/>..<paramref name="max"/>.</summary>
    internal static void SelectionRing(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding)
    {
        var scale = ImGuiHelpers.GlobalScale;
        drawList.AddRect(min, max, ImGui.GetColorU32(AetherPalette.Aether), rounding, ImDrawFlags.None, AetherMetrics.BorderStrong * scale);
        AetherBrand.DrawCorners(drawList, min, max, 3f * scale, 12f * scale, Math.Max(1f, 2f * scale), AetherPalette.Glow);
    }

    /// <summary>A soft glow around a rectangle (the spotlight, a focused card): a few widening translucent rings.</summary>
    internal static void Glow(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding, Vector4 color, float spread)
    {
        const int rings = 4;
        for (var i = 1; i <= rings; i++)
        {
            var t = i / (float)rings;
            var expand = spread * t;
            var alpha = color.W * (1f - t) * 0.6f;
            drawList.AddRect(min - new Vector2(expand), max + new Vector2(expand), ImGui.GetColorU32(color with { W = alpha }), rounding + expand, ImDrawFlags.None, spread / rings + 0.5f);
        }
    }
}
