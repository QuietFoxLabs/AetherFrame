using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Basic editor's Pattern and Customize Background show only while the Plate's own background
/// does (the owner's request of October 2, 2026): background artwork that covers it, as an Art
/// Style's does, leaves them out (<see cref="PlateComponentEditor.CoveringBackground"/>).
/// </summary>
public class BackgroundSettingsTests
{
    private static ComponentDefinition? Covering(ProfileDocument document) =>
        PlateComponentEditor.CoveringBackground(document, BuiltInComponentCatalog.Instance);

    [Fact]
    public async Task EveryArtStyle_CoversTheBackground_AndASimpleTheme_DoesNot()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        Assert.Null(Covering(harness.Document));

        foreach (var style in ArtSets.Styles)
        {
            harness.Basic.ApplyTheme(style);
            var artwork = style.Components.Single(id => BuiltInComponentCatalog.Find(id)!.Kind == PlateComponentKind.Background);
            Assert.Equal(artwork, Covering(harness.Document)?.Id);
        }

        harness.Basic.ApplyTheme(ProfileThemePresets.Find("Dark")!);
        Assert.Null(Covering(harness.Document));
    }

    [Fact]
    public async Task SettingBackgroundToNone_ShowsTheSettings_AndUndoHidesThemAgain()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Basic.ApplyTheme(ProfileThemePresets.Find("af.style.frontier-silver")!);
        Assert.NotNull(Covering(harness.Document));

        harness.Session.SetComponentSlot(PlateComponentKind.Background, null);
        Assert.Null(Covering(harness.Document));

        harness.Session.Undo();
        Assert.NotNull(Covering(harness.Document));
    }

    [Theory]
    [InlineData(AdventurePlateOrientation.Normal)]
    [InlineData(AdventurePlateOrientation.Mirrored)]
    public void ArtworkDrawnOverTheWholePlate_Covers_InEitherOrientation(AdventurePlateOrientation orientation)
    {
        var document = WithBackground(out _);
        document.BasicPlate!.Orientation = orientation;

        Assert.Equal(BuiltInComponentCatalog.BackgroundCelestialSakura, Covering(document)?.Id);
    }

    /// <summary>Wherever any of the Plate's own background can show, its settings matter, so they show.</summary>
    [Theory]
    [InlineData("hidden")]
    [InlineData("opacity")]
    [InlineData("color alpha")]
    [InlineData("smaller")]
    [InlineData("moved")]
    [InlineData("turned")]
    [InlineData("another canvas shape")]
    [InlineData("unknown artwork")]
    public void ArtworkThatLetsTheBackgroundShow_DoesNotCover(string how)
    {
        var document = WithBackground(out var background);
        switch (how)
        {
            case "hidden":
                background.Visible = false;
                break;
            case "opacity":
                background.Opacity = 0.9f;
                break;
            case "color alpha":
                background.Color = new Vector4(1f, 1f, 1f, 0.5f);
                break;
            case "smaller":
                background.Scale = 0.9f;
                break;
            case "moved":
                background.Offset = new Vector2(20f, 0f);
                break;
            case "turned":
                background.RotationDegrees = 10f;
                break;
            case "another canvas shape":
                document.CanvasHeight = 1000f;
                break;
            case "unknown artwork":
                background.DefinitionId = "af.background.some-future-art";
                break;
        }

        Assert.Null(Covering(document));
    }

    /// <summary>Moved, enlarged or turned artwork that still reaches past every edge covers.</summary>
    [Fact]
    public void EnlargedArtwork_ThatStillReachesEveryEdge_Covers()
    {
        var document = WithBackground(out var background);
        background.Scale = 2f;
        background.Offset = new Vector2(30f, -20f);
        background.RotationDegrees = 10f;

        Assert.NotNull(Covering(document));
    }

    [Fact]
    public void APlateWithNoBackgroundArtwork_IsNeverCovered()
    {
        var document = BasicDocuments.Classic();
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameCelestialSakura)];

        Assert.Null(Covering(document));
    }

    private static ProfileDocument WithBackground(out PlateComponent background)
    {
        var document = BasicDocuments.Classic();
        background = ComponentDocuments.Of(BuiltInComponentCatalog.BackgroundCelestialSakura);
        document.Components = [background];
        return document;
    }
}
