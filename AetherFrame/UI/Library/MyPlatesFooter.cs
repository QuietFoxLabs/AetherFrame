using System;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.UI.Library;

/// <summary>
/// One quiet item at the right of the My Plates footer's bottom row, such as the loaded version:
/// its text, and the tooltip that explains it.
/// </summary>
internal sealed record MyPlatesFooterItem(string Text, string Tooltip);

/// <summary>
/// Where the My Plates footer's bottom row puts its right-hand items (the loaded version, and any
/// item that joins it): on the row of the right-click hint, right-aligned, while both fit side by
/// side, or else right-aligned on a row of their own under it, so the hint and the items never
/// overlap and neither is cut off by a narrow window or a large UI scale. Pure arithmetic in pixels
/// as measured at the current scale, so it has no ImGui dependency.
/// </summary>
internal readonly record struct MyPlatesFooterLayout(bool ItemsOnOwnRow, float ItemsX)
{
    /// <summary>Rows the hint and the items take: one side by side, two stacked.</summary>
    internal int Rows => ItemsOnOwnRow ? 2 : 1;

    /// <summary>
    /// The layout for a footer <paramref name="available"/> pixels wide, given the hint's width, the
    /// items' total width and the least gap kept between them. Items wider than the footer start at
    /// its left edge.
    /// </summary>
    internal static MyPlatesFooterLayout For(float available, float hintWidth, float itemsWidth, float gap)
    {
        var onOwnRow = hintWidth > 0f && itemsWidth > 0f && hintWidth + gap + itemsWidth > available;
        return new MyPlatesFooterLayout(onOwnRow, MathF.Max(0f, available - itemsWidth));
    }

    /// <summary>The items' total width: each one's width, with <paramref name="separator"/> between each two.</summary>
    internal static float ItemsWidth(ReadOnlySpan<float> widths, float separator)
    {
        var total = 0f;
        for (var i = 0; i < widths.Length; i++)
        {
            total += widths[i] + (i > 0 ? separator : 0f);
        }

        return total;
    }
}

/// <summary>The words of the My Plates footer's version item.</summary>
internal static class MyPlatesFooterText
{
    /// <summary>The loaded plugin's version, as the footer shows it: "v0.1.9".</summary>
    internal static string Version(AetherFrameBuildInfo build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return $"v{build.Version}";
    }

    /// <summary>The version item's tooltip: the loaded plugin, then its build revision, or that the build recorded none.</summary>
    internal static string VersionTooltip(AetherFrameBuildInfo build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var revision = build.Revision is { } id ? $"Build {id}" : "This build has no build revision recorded.";
        return $"Loaded plugin: {build.DisplayName}\n{revision}";
    }

    /// <summary>The version item for <paramref name="build"/>.</summary>
    internal static MyPlatesFooterItem VersionItem(AetherFrameBuildInfo build) => new(Version(build), VersionTooltip(build));
}
