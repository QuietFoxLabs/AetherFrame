using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Art Styles and Simple Themes as two style systems (issue #118): each keeps its own choice, choosing
/// from one never loses the other's, the Plate's own background is kept under an Art Style for its
/// Simple Theme, and Plates saved before all this read as they always did.
/// </summary>
public class PlateStyleTests
{
    private static ProfileThemePreset Art(string slug) => ProfileThemePresets.Find("af.style." + slug)!;

    private static ProfileThemePreset Simple(string id) => ProfileThemePresets.Find(id)!;

    private static string BackgroundJson(ProfileDocument document) => JsonSerializer.Serialize(document.Background, JsonOptions.Default);

    private static TextProfileElement Text(ProfileDocument document, ProfileElementRole role) =>
        (TextProfileElement)document.Elements.Single(e => e.Role == role);

    private static bool HasPieceOf(ProfileDocument document, ProfileThemePreset style) =>
        document.Components?.Exists(c => style.Components.Contains(c.DefinitionId)) == true;

    /// <summary>A Plate on the Simple Theme Dark whose background the player then made their own.</summary>
    private static ProfileDocument CustomizedDark()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(Simple("Dark"));
        document.Background!.Texture = ProfileBackgroundTexture.Honeycomb;
        document.Background.TextureIntensity = 0.4f;
        document.Background.PrimaryColor = new Vector4(0.1f, 0.2f, 0.3f, 1f);
        return document;
    }

    // ---- the saved fields ---------------------------------------------------------------------------

    [Fact]
    public void ThePlatesChoices_AreNotWrittenUntilMade_SoEarlierPlatesSaveAsTheyWere()
    {
        var settings = new BasicPlateSettings { ThemeId = "Royal" };
        var json = JsonSerializer.Serialize(settings, JsonOptions.Default);

        Assert.DoesNotContain("\"ArtStyle\"", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"SimpleTheme\"", json, System.StringComparison.Ordinal);
        Assert.Equal("Royal", JsonDocument.Parse(json).RootElement.GetProperty("ThemeName").GetString());
    }

    [Fact]
    public void ThePlatesChoices_RoundTrip_Clone_AndCompare()
    {
        var settings = new BasicPlateSettings { ThemeId = "af.style.allagan-tech", ArtStyleId = "af.style.allagan-tech", SimpleThemeId = "Dark" };
        var read = JsonSerializer.Deserialize<BasicPlateSettings>(JsonSerializer.Serialize(settings, JsonOptions.Default), JsonOptions.Default)!;

        Assert.Equal("af.style.allagan-tech", read.ArtStyleId);
        Assert.Equal("Dark", read.SimpleThemeId);
        Assert.True(read.ContentEquals(settings));
        Assert.True(settings.Clone().ContentEquals(settings));
        Assert.False(settings.ContentEquals(settings.Clone().With(s => s.SimpleThemeId = "Pastel")));
        Assert.False(settings.ContentEquals(settings.Clone().With(s => s.ArtStyleId = null)));

        var empty = JsonSerializer.Deserialize<BasicPlateSettings>("""{ "ArtStyle": "", "SimpleTheme": null }""", JsonOptions.Default)!;
        Assert.Null(empty.ArtStyleId);
        Assert.Null(empty.SimpleThemeId);
    }

    // ---- a Plate saved before the two systems --------------------------------------------------------

    [Fact]
    public void AnEarlierArtStylePlate_UsesItsArtStyle_AndHasNoSimpleThemeYet()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.BasicPlate!.ThemeId = "af.style.celestial-sakura";

        Assert.Equal(StyleSystem.ArtStyle, PlateStyle.SystemInUse(document));
        Assert.Equal("af.style.celestial-sakura", PlateStyle.ChosenArtStyle(document)!.Id);
        Assert.Null(PlateStyle.ChosenSimpleTheme(document));
        Assert.Equal(StyleSystem.ArtStyle, PlateStyle.OpensOn(document));
        Assert.Equal("Art Style: Celestial Sakura", PlateStyle.Describe(document));
    }

    [Fact]
    public void TheBrowser_OpensOnArtStyles_UnlessThePlayerChoseASimpleTheme()
    {
        var starter = BasicDocuments.Classic(FakeCharacter.Hero); // the starter theme, never chosen
        Assert.Equal(StyleSystem.SimpleTheme, PlateStyle.SystemInUse(starter));
        Assert.Equal(StyleSystem.ArtStyle, PlateStyle.OpensOn(starter));

        var earlierDark = BasicDocuments.Classic(FakeCharacter.Hero);
        earlierDark.BasicPlate!.ThemeId = "Dark"; // chosen before the two systems
        Assert.Equal(StyleSystem.SimpleTheme, PlateStyle.OpensOn(earlierDark));

        var royal = BasicDocuments.Classic(FakeCharacter.Hero);
        BasicDocuments.Editor(royal).ApplyTheme(Simple("Royal")); // the starter, chosen on purpose
        Assert.Equal(StyleSystem.SimpleTheme, PlateStyle.OpensOn(royal));

        var none = new ProfileDocument();
        Assert.Equal(StyleSystem.ArtStyle, PlateStyle.OpensOn(none));
        Assert.Equal("No style chosen", PlateStyle.Describe(none));
    }

    // ---- choosing ---------------------------------------------------------------------------------

    [Fact]
    public void ChoosingAnArtStyle_PlacesItsPieces_AndLeavesThePlatesOwnBackground_AndSimpleTheme()
    {
        var document = CustomizedDark();
        var background = BackgroundJson(document);
        var style = Art("allagan-tech");

        BasicDocuments.Editor(document).ApplyTheme(style);

        Assert.Equal(background, BackgroundJson(document));
        Assert.True(HasPieceOf(document, style));
        Assert.Equal(style.Id, document.BasicPlate!.ThemeId);
        Assert.Equal(style.Id, document.BasicPlate.ArtStyleId);
        Assert.Equal("Dark", document.BasicPlate.SimpleThemeId);
        Assert.Equal(style.AccentTextColor with { W = 1f }, Text(document, ProfileElementRole.BasicWorldHeading).Color with { W = 1f });
        Assert.Equal("Art Style: Allagan Tech", PlateStyle.Describe(document));
    }

    [Fact]
    public void GoingBackToTheLastSimpleTheme_TakesTheArtAway_AndFindsTheBackgroundAsItWasLeft()
    {
        var document = CustomizedDark();
        var background = BackgroundJson(document);
        var editor = BasicDocuments.Editor(document);
        var style = Art("allagan-tech");
        editor.ApplyTheme(style);

        editor.ApplyTheme(Simple("Dark"));

        Assert.Equal(background, BackgroundJson(document)); // its pattern and its own color kept
        Assert.False(HasPieceOf(document, style));
        Assert.Equal("Dark", document.BasicPlate!.ThemeId);
        Assert.Equal(style.Id, document.BasicPlate.ArtStyleId); // kept for later
        Assert.Equal(Simple("Dark").AccentTextColor with { W = 1f }, Text(document, ProfileElementRole.BasicWorldHeading).Color with { W = 1f });

        // Choosing the theme again while it is in use sets its colors, as a theme always has.
        editor.ApplyTheme(Simple("Dark"));
        Assert.Equal(Simple("Dark").PrimaryColor, document.Background!.PrimaryColor);
        Assert.Equal(Simple("Dark").Texture, document.Background.Texture);
    }

    [Fact]
    public void AnotherSimpleTheme_AfterAnArtStyle_SetsItsOwnBackgroundColors()
    {
        var document = CustomizedDark();
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(Art("allagan-tech"));

        editor.ApplyTheme(Simple("Pastel"));

        Assert.Equal(Simple("Pastel").PrimaryColor, document.Background!.PrimaryColor);
        Assert.Equal(Simple("Pastel").Texture, document.Background.Texture);
        Assert.Equal("Pastel", document.BasicPlate!.SimpleThemeId);
        Assert.Equal("af.style.allagan-tech", document.BasicPlate.ArtStyleId);
    }

    [Fact]
    public void AnEarlierArtStylePlate_GoingToASimpleTheme_GetsThatThemesColors()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var style = Art("celestial-sakura");
        BasicDocuments.Editor(document).ApplyTheme(style);
        document.BasicPlate!.ArtStyleId = null; // as an earlier build saved it: the style in use only
        document.BasicPlate.SimpleThemeId = null;

        BasicDocuments.Editor(document).ApplyTheme(Simple("Forest"));

        Assert.Equal(Simple("Forest").PrimaryColor, document.Background!.PrimaryColor);
        Assert.False(HasPieceOf(document, style));
        Assert.Equal("Forest", document.BasicPlate.ThemeId);
    }

    [Fact]
    public void ANewPlate_KeepsItsStarterTheme_AndItsBackground_ThroughAnArtStyle()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero); // the starter theme, never chosen
        var starter = ProfileThemePresets.All[0];
        document.Background!.Texture = ProfileBackgroundTexture.Honeycomb;
        document.Background.TextureIntensity = 0.4f;
        document.Background.PrimaryColor = new Vector4(0.1f, 0.2f, 0.3f, 1f);
        var background = BackgroundJson(document);
        var editor = BasicDocuments.Editor(document);

        editor.ApplyTheme(Art("allagan-tech"));
        Assert.Equal(starter.Id, PlateStyle.ChosenSimpleTheme(document)!.Id); // marked in Simple Themes

        editor.ApplyTheme(starter);
        Assert.Equal(background, BackgroundJson(document));
        Assert.Equal(starter.Id, document.BasicPlate!.ThemeId);
        Assert.Equal(StyleSystem.SimpleTheme, PlateStyle.OpensOn(document)); // chosen now
    }

    [Fact]
    public void AnEarlierSimpleThemePlate_KeepsItsThemeAndBackground_ThroughAnArtStyle()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.BasicPlate!.ThemeId = "Dark"; // as an earlier build saved it: the style in use only
        document.Background!.Texture = ProfileBackgroundTexture.Honeycomb;
        document.Background.PrimaryColor = new Vector4(0.1f, 0.2f, 0.3f, 1f);
        var background = BackgroundJson(document);
        var editor = BasicDocuments.Editor(document);

        editor.ApplyTheme(Art("allagan-tech"));
        Assert.Equal("Dark", document.BasicPlate.SimpleThemeId);

        editor.ApplyTheme(Simple("Dark"));
        Assert.Equal(background, BackgroundJson(document));
    }

    [Fact]
    public void AnEarlierArtStylePlate_KeepsItsArtStyle_ThroughASimpleTheme()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.BasicPlate!.ThemeId = "af.style.celestial-sakura"; // as an earlier build saved it

        BasicDocuments.Editor(document).ApplyTheme(Simple("Forest"));

        Assert.Equal("af.style.celestial-sakura", document.BasicPlate.ArtStyleId);
        Assert.Equal("af.style.celestial-sakura", PlateStyle.ChosenArtStyle(document)!.Id); // marked in Art Styles
    }

    [Fact]
    public void AnEarlierBuildsThemeChange_ReplacesTheStaleChoice_WhenAnArtStyleIsChosen()
    {
        var document = CustomizedDark(); // this build: SimpleTheme "Dark"
        document.BasicPlate!.ThemeId = "Ocean"; // an earlier build then changed the style in use
        var background = BackgroundJson(document);
        var editor = BasicDocuments.Editor(document);

        editor.ApplyTheme(Art("allagan-tech"));
        Assert.Equal("Ocean", document.BasicPlate.SimpleThemeId);

        editor.ApplyTheme(Simple("Ocean"));
        Assert.Equal(background, BackgroundJson(document));
    }

    [Fact]
    public void OneArtStyleForAnother_SwapsThePieces_AndKeepsTheSimpleTheme()
    {
        var document = CustomizedDark();
        var editor = BasicDocuments.Editor(document);
        var first = Art("allagan-tech");
        var second = Art("celestial-sakura");
        editor.ApplyTheme(first);

        editor.ApplyTheme(second);

        Assert.False(HasPieceOf(document, first));
        Assert.True(HasPieceOf(document, second));
        Assert.Equal(second.Id, document.BasicPlate!.ArtStyleId);
        Assert.Equal("Dark", document.BasicPlate.SimpleThemeId);
        Assert.Equal("Dark", PlateStyle.ChosenSimpleTheme(document)!.Id);
    }

    [Fact]
    public void ASimpleThemeForAnother_KeepsTheArtStyle()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(Art("allagan-tech"));
        editor.ApplyTheme(Simple("Dark"));

        editor.ApplyTheme(Simple("Ocean"));

        Assert.Equal("Ocean", document.BasicPlate!.SimpleThemeId);
        Assert.Equal("af.style.allagan-tech", PlateStyle.ChosenArtStyle(document)!.Id);
        Assert.Equal(StyleSystem.SimpleTheme, PlateStyle.SystemInUse(document));
    }

    [Fact]
    public async Task EveryChoice_IsOneUndoStep_ChoicesIncluded()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Basic.ApplyTheme(Simple("Dark"));
        var dark = harness.Json();

        harness.Basic.ApplyTheme(Art("allagan-tech"));
        var art = harness.Json();
        harness.Basic.ApplyTheme(Simple("Dark"));

        harness.Session.Undo();
        Assert.Equal(art, harness.Json());
        harness.Session.Undo();
        Assert.Equal(dark, harness.Json());
    }

    // ---- what both editors show --------------------------------------------------------------------

    [Fact]
    public void BothEditors_DescribeTheStyleTheSameWay()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        BasicDocuments.Editor(document).ApplyTheme(Art("celestial-sakura"));

        Assert.Equal(["Normal layout  ·  Art Style: Celestial Sakura"], BasicEditorView.SummaryOf(document, BasicEditorCategory.Style));
        Assert.Equal("af.style.celestial-sakura", ThemeBrowser.Current(document)!.Id);
    }

    [Fact]
    public void EachSystemsCatalog_HoldsOnlyItsOwnStyles()
    {
        Assert.All(ThemeBrowser.Catalog(StyleSystem.ArtStyle), style => Assert.True(style.IsArtStyle));
        Assert.All(ThemeBrowser.Catalog(StyleSystem.SimpleTheme), theme => Assert.False(theme.IsArtStyle));
        Assert.Equal(ProfileThemePresets.All.Length, ThemeBrowser.Catalog(StyleSystem.ArtStyle).Count + ThemeBrowser.Catalog(StyleSystem.SimpleTheme).Count);
        Assert.Equal("Art Styles", ThemeBrowser.SystemLabel(StyleSystem.ArtStyle));
        Assert.Equal("Simple Themes", ThemeBrowser.SystemLabel(StyleSystem.SimpleTheme));
    }

    [Fact]
    public void BrowsingTheOtherSystem_SaysWhatChoosingFromItDoes()
    {
        var document = CustomizedDark();
        Assert.Null(ThemeBrowser.SwitchHint(document, StyleSystem.SimpleTheme));
        Assert.Contains("Your Plate uses the Simple Theme Dark", ThemeBrowser.SwitchHint(document, StyleSystem.ArtStyle), System.StringComparison.Ordinal);

        BasicDocuments.Editor(document).ApplyTheme(Art("allagan-tech"));
        Assert.Null(ThemeBrowser.SwitchHint(document, StyleSystem.ArtStyle));
        var hint = ThemeBrowser.SwitchHint(document, StyleSystem.SimpleTheme)!;
        Assert.Contains("Your Plate uses the Art Style Allagan Tech", hint, System.StringComparison.Ordinal);
        Assert.Contains("Your last Simple Theme, Dark", hint, System.StringComparison.Ordinal);
    }

    // ---- while the artwork isn't drawn --------------------------------------------------------------

    [Fact]
    public void AStylesBackground_StandsInWithItsOwnColor_UntilItIsDrawn()
    {
        foreach (var style in ArtSets.Styles)
        {
            foreach (var id in style.Components)
            {
                var art = BuiltInComponentCatalog.Find(id)!.Art!;
                Assert.Same(style, ArtSets.StyleOfArt(art));
                Assert.Equal(art.Kind == PlateComponentKind.Background ? style.PrimaryColor : null, ArtSets.BackgroundStandIn(art));
            }
        }

        Assert.Null(ArtSets.BackgroundStandIn(BuiltInComponentCatalog.Find(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot)!.Art!));
    }
}

internal static class BasicPlateSettingsTestExtensions
{
    internal static BasicPlateSettings With(this BasicPlateSettings settings, System.Action<BasicPlateSettings> change)
    {
        change(settings);
        return settings;
    }
}
