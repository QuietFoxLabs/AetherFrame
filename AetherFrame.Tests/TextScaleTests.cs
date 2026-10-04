using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Issue #131: Plate text was measured at the wrong size whenever Dalamud's interface scale wasn't
/// 100%. ImGui.GetFontSize() is FontGlobalScale times the pushed font's FontSize, and Dalamud sets
/// FontGlobalScale to its interface scale; the Plate's fonts aren't globally scaled, and AddText
/// draws at size / FontSize whatever the scale, so text measured through GetFontSize came out
/// 1 / scale of its drawn width. The renderer measures by the font's own FontSize instead. The
/// source is read, as nothing here can run ImGui.
/// </summary>
public sealed class TextScaleTests
{
    [Fact]
    public void PlateText_IsMeasuredByTheFontsOwnSize_NeverByTheInterfaceScaledOne()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "UI", "Rendering", "ProfileTextRenderer.cs"));
        var code = string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("GetFontSize(", code, StringComparison.Ordinal);
        Assert.Contains("inverseBakedSize = font.FontSize > 0f ? 1f / font.FontSize : 0f;", code, StringComparison.Ordinal);
        Assert.Contains("var bakedFontSize = font.FontSize;", code, StringComparison.Ordinal);
    }
}
