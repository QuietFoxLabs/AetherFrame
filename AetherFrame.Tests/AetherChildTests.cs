using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// While the screen eyedropper picks (issue #120), none of AetherFrame's windows takes the mouse,
/// child windows included: ImGui gives a child none of its window's NoMouseInputs and hovers
/// whichever child is under the pointer, so on another monitor a pick's click would still select or
/// drag on the canvas. The source is read, as nothing here can run ImGui.
/// </summary>
public sealed class AetherChildTests
{
    [Fact]
    public void EveryChildWindow_OpensThroughAetherChild()
    {
        var offenders = Sources()
            .Where(source => !source.Path.EndsWith("AetherChild.cs", System.StringComparison.Ordinal))
            .SelectMany(source => Regex.Matches(source.Text, @"ImRaii\.Child\(|ChildDisposable\(|BeginChild|ChildFrame\(|BeginListBox|ListBox\(")
                .Select(match => $"{source.Path}: {match.Value}"))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryMultiLineTextBox_IsInertWhilePicking()
    {
        // Its child window is ImGui's own, out of AetherChild.Begin's reach.
        var unguarded = Sources()
            .SelectMany(source => Regex.Matches(source.Text, @"InputTextMultiline\(")
                .Where(match => !source.Text[..match.Index].Split('\n').TakeLast(3).Any(line => line.Contains("AetherChild.WhilePicking()", System.StringComparison.Ordinal)))
                .Select(match => source.Path))
            .ToList();
        Assert.Empty(unguarded);
        Assert.Contains(Sources(), source => source.Text.Contains("InputTextMultiline(", System.StringComparison.Ordinal));
    }

    [Fact]
    public void EveryFileDialog_IsHiddenWhilePicking()
    {
        // Dalamud's file dialog is a window of its own, which AetherFrame's flags don't reach.
        var draws = Sources()
            .SelectMany(source => Regex.Matches(source.Text, @"\b\w*[dD]ialog\w*\.Draw\(\)")
                .Select(match => (source.Path, Guarded: source.Text[..match.Index].Split('\n').TakeLast(3)
                    .Any(line => line.Contains("if (!ScreenEyedropper.ClaimsInput)", System.StringComparison.Ordinal)))))
            .ToList();
        Assert.Equal(4, draws.Count);
        Assert.All(draws, draw => Assert.True(draw.Guarded, draw.Path));
        Assert.Equal(
            draws.Count,
            Sources().Sum(source => Regex.Matches(source.Text, @"new FileDialogManager\(\)").Count));
    }

    [Fact]
    public void NoTable_Scrolls()
    {
        // A scrolling table is a child window of ImGui's own too.
        var scrolling = Sources()
            .SelectMany(source => Regex.Matches(source.Text, @"ImGuiTableFlags\.Scroll[XY]").Select(match => source.Path))
            .ToList();
        Assert.Empty(scrolling);
    }

    [Fact]
    public void AWindow_TakesNoMouseInput_ExactlyWhileAPickClaimsIt()
    {
        var chrome = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Windows", "Theme", "AetherStyle.cs"));
        var policy = chrome[chrome.IndexOf("internal static void ApplyPolicy(Window window)", System.StringComparison.Ordinal)..];
        Assert.Matches(
            @"if \(ScreenEyedropper\.ClaimsInput\)\s*\{\s*window\.Flags \|= ImGuiWindowFlags\.NoMouseInputs;\s*\}\s*else\s*\{\s*window\.Flags &= ~ImGuiWindowFlags\.NoMouseInputs;\s*\}",
            policy);

        var child = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Windows", "AetherChild.cs"));
        Assert.Contains("ScreenEyedropper.ClaimsInput ? flags | ImGuiWindowFlags.NoMouseInputs : flags", child, System.StringComparison.Ordinal);
    }

    private static (string Path, string Text)[] Sources() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", System.StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", System.StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(RepositoryPaths.Root().FullName, path), File.ReadAllText(path)))
            .ToArray();
}
