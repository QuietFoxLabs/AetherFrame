using System.Linq;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The source scan behind <see cref="PluginAssemblyBoundaryTests"/>: whatever a player build
/// compiles is scanned, and only what the preview flavour alone compiles is not.
/// </summary>
public class PlayerBuildSourceScanTests
{
    private static readonly string[] Names = ["AetherFrame.Personas"];

    private static int[] Offending(params string[] lines) => PlayerBuildSourceScan.OffendingLines(lines, Names).ToArray();

    [Fact]
    public void ReportsALineThatNamesTheCode_ByItsNumber()
    {
        Assert.Equal(new[] { 2 }, Offending("using System;", "using AetherFrame.Personas;", "namespace AetherFrame;"));
    }

    [Fact]
    public void IgnoresCommentLines()
    {
        Assert.Empty(Offending("// AetherFrame.Personas is not referenced here", "    // nor here: AetherFrame.Personas"));
    }

    [Fact]
    public void SkipsAPreviewBlock_AndScansItsElseBranch()
    {
        Assert.Equal(
            new[] { 4, 6 },
            Offending(
                "#if AETHERFRAME_NETWORK_PREVIEW",
                "using AetherFrame.Personas;",
                "#else",
                "using AetherFrame.Personas;",
                "#endif",
                "using AetherFrame.Personas;"));
    }

    [Fact]
    public void ScansAnElifBranchOfAPreviewBlock()
    {
        Assert.Equal(
            new[] { 4 },
            Offending(
                "#if AETHERFRAME_NETWORK_PREVIEW",
                "using AetherFrame.Personas;",
                "#elif DEBUG",
                "using AetherFrame.Personas;",
                "#endif"));
    }

    [Fact]
    public void SkipsOnlyTheElseOfANegatedPreviewBlock()
    {
        Assert.Equal(
            new[] { 2 },
            Offending(
                "#if !AETHERFRAME_NETWORK_PREVIEW",
                "using AetherFrame.Personas;",
                "#else",
                "using AetherFrame.Personas;",
                "#endif"));
    }

    [Fact]
    public void KeepsSkippingThroughNestedBlocks_UntilThePreviewBlockEnds()
    {
        Assert.Equal(
            new[] { 11 },
            Offending(
                "#if AETHERFRAME_NETWORK_PREVIEW",
                "#if DEBUG",
                "using AetherFrame.Personas;",
                "#endif",
                "using AetherFrame.Personas;",
                "#if AETHERFRAME_NETWORK_PREVIEW",
                "using AetherFrame.Personas;",
                "#endif",
                "using AetherFrame.Personas;",
                "#endif",
                "using AetherFrame.Personas;"));
    }

    [Fact]
    public void ScansOrdinaryConditionalBlocks_AndACompoundCondition()
    {
        Assert.Equal(
            new[] { 2, 5 },
            Offending(
                "#if DEBUG",
                "using AetherFrame.Personas;",
                "#endif",
                "#if AETHERFRAME_NETWORK_PREVIEW || DEBUG",
                "using AetherFrame.Personas;",
                "#endif"));
    }

    [Fact]
    public void KeepsSkippingTheElseOfANegatedBlockNestedInAPreviewBlock()
    {
        Assert.Empty(
            Offending(
                "#if AETHERFRAME_NETWORK_PREVIEW",
                "#if !AETHERFRAME_NETWORK_PREVIEW",
                "using AetherFrame.Personas;",
                "#else",
                "using AetherFrame.Personas;",
                "#endif",
                "#endif"));
    }

    [Fact]
    public void ScansThroughStrayAndUnrelatedDirectives()
    {
        Assert.Equal(
            new[] { 2, 4, 6 },
            Offending(
                "#else",
                "using AetherFrame.Personas;",
                "#region Local",
                "using AetherFrame.Personas;",
                "#pragma warning disable CS0000",
                "using AetherFrame.Personas;",
                "#endregion"));
    }

    [Fact]
    public void ReportsASourceThatDefinesOrUndefinesTheSymbolItself()
    {
        Assert.Equal(
            new[] { 1, 2 },
            Offending(
                "#define AETHERFRAME_NETWORK_PREVIEW",
                "#undef AETHERFRAME_NETWORK_PREVIEW",
                "#define DEBUG"));
    }

    [Fact]
    public void RecognisesATabAfterTheDirective()
    {
        Assert.Empty(Offending("#if\tAETHERFRAME_NETWORK_PREVIEW", "using AetherFrame.Personas;", "#endif"));
    }

    [Fact]
    public void AllowsATrailingCommentOnTheDirective()
    {
        Assert.Equal(
            new[] { 4 },
            Offending(
                "#if AETHERFRAME_NETWORK_PREVIEW // the seam",
                "using AetherFrame.Personas;",
                "#endif // the seam",
                "using AetherFrame.Personas;"));
    }
}
