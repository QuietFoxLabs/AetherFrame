using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
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
/// Which appearance controls apply (issue #119, <see cref="AppearanceControls"/>), from what the
/// renderer draws rather than from the style chosen: the Plate's own background settings only while
/// some of that background shows, and a Component's color only where it tints. Both editors ask the
/// same rule; it follows every change at once, and never changes a Plate.
/// </summary>
public class AppearanceControlsTests
{
    // Throwing on a name that isn't in the catalog: the editor session would take a null style as a
    // failed edit and change nothing, which would pass for "nothing changed".
    private static ProfileThemePreset Art(string slug) => ProfileThemePresets.Find("af.style." + slug) ?? throw new ArgumentException(slug);

    private static ProfileThemePreset Simple(string id) => ProfileThemePresets.Find(id) ?? throw new ArgumentException(id);

    private static string BackgroundJson(ProfileDocument document) => JsonSerializer.Serialize(document.Background, JsonOptions.Default);

    private static string BackgroundArtworkOf(ProfileThemePreset style) =>
        style.Components.Single(id => BuiltInComponentCatalog.Find(id)!.Kind == PlateComponentKind.Background);

    /// <summary>A Plate on the Simple Theme Dark whose background the player made their own: a gradient with a Pattern.</summary>
    private static ProfileDocument CustomizedDark()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        BasicDocuments.Editor(document).ApplyTheme(Simple("Dark"));
        document.Background!.Mode = ProfileBackgroundMode.LinearGradient;
        document.Background.PrimaryColor = new Vector4(0.1f, 0.2f, 0.3f, 1f);
        document.Background.Texture = ProfileBackgroundTexture.Honeycomb;
        document.Background.TextureIntensity = 0.4f;
        return document;
    }

    // ---- an opaque Art Style's artwork --------------------------------------------------------------

    [Fact]
    public void EveryArtStylesArtwork_CoversThePlate_SoTheBackgroundSettingsDontApply_AndBothLinesNameIt()
    {
        var document = CustomizedDark();
        foreach (var style in ArtSets.Styles)
        {
            BasicDocuments.Editor(document).ApplyTheme(style);
            var cover = AppearanceControls.Background(document);

            Assert.False(cover.BackgroundShows);
            Assert.Equal(BackgroundArtworkOf(style), cover.Artwork!.Id);
            Assert.Same(PlateComponentEditor.FindSlot(document, PlateComponentKind.Background), cover.Component);
            Assert.Contains($"while the {cover.Artwork.Name} background artwork covers the whole Plate", AppearanceControls.BasicCoveredHint(cover), StringComparison.Ordinal);
            Assert.StartsWith($"The {cover.Artwork.Name} background artwork covers the whole Plate", AppearanceControls.AdvancedCoveredHint(cover), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The rule doesn't wait for the artwork: while it loads, downloads or fails, the renderer draws its
    /// style's plain color in its place, over the same whole Plate, so the background settings still
    /// can't show. And no Background artwork ships see-through or tintable: each is an opaque image
    /// drawn in its own colors at full opacity, so it is see-through only where a player makes it so.
    /// </summary>
    [Fact]
    public void EveryBackgroundArtwork_IsOpaque_InItsOwnColors_WithAStandInWhileItLoads()
    {
        var backgrounds = BuiltInComponentCatalog.OfKind(PlateComponentKind.Background).ToList();
        Assert.Equal(ArtSets.Styles.Count, backgrounds.Count);
        foreach (var definition in backgrounds)
        {
            var art = definition.Art!;
            Assert.False(art.Tintable);
            Assert.Equal(1f, definition.DefaultAlpha);
            Assert.NotNull(ArtSets.BackgroundStandIn(art));
            Assert.True(HasNoTransparency(art.ResourceName), $"{art.Id} can be see-through");
            Assert.False(AppearanceControls.ColorApplies(definition));
        }
    }

    // ---- see-through, tinted, removed or missing ---------------------------------------------------

    [Theory]
    [InlineData("opacity")]
    [InlineData("color alpha")]
    [InlineData("hidden")]
    [InlineData("smaller")]
    public void ArtworkMadeToLetTheBackgroundShow_LetsItsSettingsApply(string how)
    {
        var document = CustomizedDark();
        BasicDocuments.Editor(document).ApplyTheme(Art("celestial-sakura"));
        var artwork = PlateComponentEditor.FindSlot(document, PlateComponentKind.Background)!;
        switch (how)
        {
            case "opacity":
                artwork.Opacity = 0.6f;
                break;
            case "color alpha":
                artwork.Color = new Vector4(1f, 1f, 1f, 0.6f);
                break;
            case "hidden":
                artwork.Visible = false;
                break;
            case "smaller":
                artwork.Scale = 0.8f;
                break;
        }

        Assert.True(AppearanceControls.Background(document).BackgroundShows);
    }

    /// <summary>A color on artwork drawn in its own colors tints nothing, so at full alpha it still covers.</summary>
    [Fact]
    public void ACustomColor_OnArtworkInItsOwnColors_StillCovers()
    {
        var document = CustomizedDark();
        BasicDocuments.Editor(document).ApplyTheme(Art("allagan-tech"));
        PlateComponentEditor.FindSlot(document, PlateComponentKind.Background)!.Color = new Vector4(1f, 0f, 0f, 1f);

        Assert.False(AppearanceControls.Background(document).BackgroundShows);
    }

    [Fact]
    public async Task RemovingTheArtwork_InEitherEditor_BringsTheSettingsBack_AsTheyWereLeft_AsOneUndoStep()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Basic.ApplyTheme(Simple("Dark"));
        harness.Session.ApplyBackgroundEdit(style => style.Texture = ProfileBackgroundTexture.Honeycomb);
        var background = BackgroundJson(harness.Document);

        harness.Basic.ApplyTheme(Art("frontier-silver"));
        var cover = AppearanceControls.Background(harness.Document);
        Assert.False(cover.BackgroundShows);
        Assert.Equal(background, BackgroundJson(harness.Document));

        // Both editors' Remove the Artwork.
        harness.Session.RemoveComponent(cover.Component!.Id);
        Assert.True(AppearanceControls.Background(harness.Document).BackgroundShows);
        Assert.Equal(background, BackgroundJson(harness.Document));

        harness.Session.Undo();
        Assert.Equal(cover.Component.Id, AppearanceControls.Background(harness.Document).Component!.Id);
    }

    /// <summary>
    /// The artwork that covers is the one taken away, wherever it is in the list: the Advanced editor can
    /// hold more than one Background, and the first (Basic's slot) may not be the one drawn over the Plate.
    /// </summary>
    [Fact]
    public void TheCover_IsTheArtworkDrawnOverThePlate_NotTheFirstBackground()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var hidden = ComponentDocuments.Of(BuiltInComponentCatalog.BackgroundCelestialSakura);
        hidden.Visible = false;
        var drawn = ComponentDocuments.Of(BackgroundArtworkOf(Art("allagan-tech")), layerOrder: 1);
        document.Components = [hidden, drawn];

        var cover = AppearanceControls.Background(document);
        Assert.Same(drawn, cover.Component);

        PlateComponentEditor.Remove(document, cover.Component!.Id);
        Assert.True(AppearanceControls.Background(document).BackgroundShows);
    }

    /// <summary>A Plate holding the most Components a Plate can gets no Background from an Art Style, so its own background shows under the style.</summary>
    [Fact]
    public void AnArtStyle_OnAPlateAtTheComponentLimit_PlacesNoBackground_AndTheSettingsApply()
    {
        var document = CustomizedDark();
        var line = BuiltInComponentCatalog.Find(BuiltInComponentCatalog.DividerLine)!;
        while (PlateComponentEditor.HasCapacity(document))
        {
            PlateComponentEditor.Add(document, line);
        }

        BasicDocuments.Editor(document).ApplyTheme(Art("celestial-sakura"));

        Assert.Equal("af.style.celestial-sakura", document.BasicPlate!.ThemeId);
        Assert.Null(PlateComponentEditor.FindSlot(document, PlateComponentKind.Background));
        Assert.True(AppearanceControls.Background(document).BackgroundShows);
    }

    [Fact]
    public void ArtworkThisBuildDoesntKnow_IsNotDrawn_SoTheSettingsApply()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.Components = [new PlateComponent { Kind = PlateComponentKind.Background, DefinitionId = "af.background.some-future-art" }];

        Assert.True(AppearanceControls.Background(document).BackgroundShows);
    }

    // ---- Simple Themes, earlier Plates, and switching ----------------------------------------------

    [Fact]
    public void SwitchingBetweenTheSystems_ChangesWhatApplies_AtOnce()
    {
        var document = CustomizedDark();
        var editor = BasicDocuments.Editor(document);
        Assert.True(AppearanceControls.Background(document).BackgroundShows);

        editor.ApplyTheme(Art("celestial-sakura"));
        Assert.False(AppearanceControls.Background(document).BackgroundShows);

        editor.ApplyTheme(Art("dark-fantasy"));
        Assert.Equal(BackgroundArtworkOf(Art("dark-fantasy")), AppearanceControls.Background(document).Artwork!.Id);

        editor.ApplyTheme(Simple("Dark")); // back to the last Simple Theme: the style's pieces leave
        Assert.True(AppearanceControls.Background(document).BackgroundShows);

        editor.ApplyTheme(Simple("Ivory")); // another Simple Theme
        Assert.True(AppearanceControls.Background(document).BackgroundShows);

        editor.ApplyTheme(Art("allagan-tech"));
        Assert.False(AppearanceControls.Background(document).BackgroundShows);
    }

    [Fact]
    public async Task ThroughTheEditorSession_EveryChoiceAndUndo_UpdatesWhatApplies()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        Assert.True(AppearanceControls.Background(harness.Document).BackgroundShows);

        harness.Basic.ApplyTheme(Art("oceanic-siren"));
        Assert.False(AppearanceControls.Background(harness.Document).BackgroundShows);

        harness.Basic.ApplyTheme(Simple("Ivory"));
        Assert.True(AppearanceControls.Background(harness.Document).BackgroundShows);

        harness.Session.Undo();
        Assert.False(AppearanceControls.Background(harness.Document).BackgroundShows);
        harness.Session.Redo();
        Assert.True(AppearanceControls.Background(harness.Document).BackgroundShows);
    }

    /// <summary>Artwork the player chose stays through a Simple Theme, so the settings stay hidden until it is taken away.</summary>
    [Fact]
    public void ArtworkThePlayerChose_KeepsCovering_ThroughASimpleTheme()
    {
        var document = CustomizedDark();
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(Art("celestial-sakura"));
        PlateComponentEditor.SetSlot(document, PlateComponentKind.Background, BackgroundArtworkOf(Art("allagan-tech")), BuiltInComponentCatalog.Instance);

        editor.ApplyTheme(Simple("Dark"));

        Assert.Equal(BackgroundArtworkOf(Art("allagan-tech")), AppearanceControls.Background(document).Artwork!.Id);
    }

    /// <summary>
    /// A Plate saved by an earlier build with an Art Style (its background set to the style's colors,
    /// no remembered choices) covers as its artwork is drawn; one with no Basic settings at all, from
    /// before Basic, has no artwork, so its background settings apply.
    /// </summary>
    [Fact]
    public void EarlierPlates_FollowTheSameRule()
    {
        var earlierArt = BasicDocuments.Classic(FakeCharacter.Hero);
        BasicDocuments.Editor(earlierArt).ApplyTheme(Art("celestial-sakura"));
        Art("celestial-sakura").ApplyTo(earlierArt.Background!);
        earlierArt.BasicPlate!.ArtStyleId = null;
        earlierArt.BasicPlate.SimpleThemeId = null;
        Assert.False(AppearanceControls.Background(earlierArt).BackgroundShows);

        var beforeBasic = JsonSerializer.Deserialize<ProfileDocument>(LegacyData.VersionOneDocument(Guid.NewGuid(), owner: 0), JsonOptions.Default)!;
        Assert.Null(beforeBasic.BasicPlate);
        Assert.True(AppearanceControls.Background(beforeBasic).BackgroundShows);
    }

    // ---- Component color ---------------------------------------------------------------------------

    /// <summary>
    /// The renderer takes only the alpha of a color on artwork drawn in its own colors, so that artwork
    /// offers no color; line and shape styles, tintable artwork and images take the whole color.
    /// </summary>
    [Fact]
    public void AColor_AppliesWhereTheRendererTintsWithIt()
    {
        var red = new Vector4(1f, 0f, 0f, 0.5f);
        Assert.Equal(new Vector4(1f, 1f, 1f, 0.5f), DrawnColor(BuiltInComponentCatalog.PlateFrameCelestialSakura, red));
        Assert.Equal(red, DrawnColor(BuiltInComponentCatalog.PlateFrameLine, red));

        foreach (var style in ArtSets.Styles)
        {
            Assert.All(style.Components, id => Assert.False(AppearanceControls.ColorApplies(BuiltInComponentCatalog.Find(id))));
        }

        Assert.True(AppearanceControls.ColorApplies(BuiltInComponentCatalog.Find(BuiltInComponentCatalog.PlateFrameLine)));
        Assert.True(AppearanceControls.ColorApplies(BuiltInComponentCatalog.Find(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot))); // tintable artwork
        Assert.True(AppearanceControls.ColorApplies(BuiltInComponentCatalog.Find(BuiltInComponentCatalog.PortraitOverlayImage)));
        Assert.True(AppearanceControls.ColorApplies(null)); // a newer build's: its control stays
    }

    // ---- nothing is changed ------------------------------------------------------------------------

    /// <summary>
    /// Opening Plates of every kind and drawing both editors' style controls for a while changes none of
    /// them: not in memory, not on disk, not dirty, nothing to undo. Hidden and greyed-out values stay.
    /// </summary>
    [Theory]
    [InlineData("art style")]
    [InlineData("earlier art style")]
    [InlineData("see-through artwork")]
    [InlineData("kept color on artwork")]
    [InlineData("simple theme")]
    [InlineData("at the component limit")]
    [InlineData("before basic")]
    public async Task OpeningAndDrawing_ChangesNoPlate(string kind)
    {
        var plateId = Guid.NewGuid();
        string json;
        if (kind == "before basic")
        {
            json = LegacyData.VersionOneDocument(plateId, owner: 0);
        }
        else
        {
            var document = CustomizedDark();
            document.ProfileId = plateId;
            var editor = BasicDocuments.Editor(document);
            switch (kind)
            {
                case "art style":
                    editor.ApplyTheme(Art("celestial-sakura"));
                    break;
                case "earlier art style":
                    editor.ApplyTheme(Art("celestial-sakura"));
                    Art("celestial-sakura").ApplyTo(document.Background!);
                    document.BasicPlate!.ArtStyleId = null;
                    document.BasicPlate.SimpleThemeId = null;
                    break;
                case "see-through artwork":
                    editor.ApplyTheme(Art("allagan-tech"));
                    PlateComponentEditor.FindSlot(document, PlateComponentKind.Background)!.Opacity = 0.5f;
                    break;
                case "kept color on artwork":
                    editor.ApplyTheme(Art("allagan-tech"));
                    PlateComponentEditor.FindSlot(document, PlateComponentKind.PlateFrame)!.Color = new Vector4(1f, 0f, 0f, 0.7f);
                    break;
                case "at the component limit":
                    while (PlateComponentEditor.HasCapacity(document))
                    {
                        PlateComponentEditor.Add(document, BuiltInComponentCatalog.Find(BuiltInComponentCatalog.DividerLine)!);
                    }

                    editor.ApplyTheme(Art("celestial-sakura"));
                    break;
            }

            json = JsonSerializer.Serialize(document, JsonOptions.Default);
        }

        using var harness = await BasicHarness.OpenJsonAsync(json, plateId);
        var onDisk = harness.Fixture.ReadPlateJson(plateId);
        var before = harness.Json();
        var browser = new ThemeBrowserState();

        for (var frame = 0; frame < 3; frame++)
        {
            harness.SimulateBasicFrame();
            DrawStyleControls(harness.Document, browser);
            harness.Surfaces.Show(EditorSurfaceKind.Advanced);
            DrawStyleControls(harness.Document, browser);
            harness.Surfaces.Show(EditorSurfaceKind.Basic);
        }

        Assert.Equal(before, harness.Json());
        Assert.Equal(onDisk, harness.Fixture.ReadPlateJson(plateId));
        Assert.False(harness.Session.IsDirty);
        Assert.False(harness.Session.CanUndo);
    }

    /// <summary>Everything the two editors ask of a Plate to draw its style controls (issue #119), minus drawing.</summary>
    private static void DrawStyleControls(ProfileDocument document, ThemeBrowserState browser)
    {
        browser.ShowPlate(document);
        _ = PlateStyle.Describe(document);
        _ = ThemeBrowser.SwitchHint(document, browser.Showing);
        var cover = AppearanceControls.Background(document);
        if (!cover.BackgroundShows)
        {
            _ = AppearanceControls.BasicCoveredHint(cover);
            _ = AppearanceControls.AdvancedCoveredHint(cover);
        }

        foreach (var component in document.Components ?? [])
        {
            ComponentPaintPlan.Resolve(component, BuiltInComponentCatalog.Instance, out var definition);
            _ = AppearanceControls.ColorApplies(definition);
        }
    }

    /// <summary>The color the renderer draws a Component of <paramref name="definitionId"/> with, given <paramref name="color"/>.</summary>
    private static Vector4 DrawnColor(string definitionId, Vector4 color)
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var component = ComponentDocuments.Of(definitionId);
        component.Color = color;
        document.Components = [component];
        var step = ComponentDocuments.Plan(document).First(s => s.Component == component);
        var primitives = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, component, step.Definition!, step.Placement, primitives);
        return primitives[0].Color;
    }

    /// <summary>
    /// True when a PNG can't be see-through anywhere: its IHDR color type has no alpha channel (grey,
    /// RGB, or a palette) and no tRNS chunk adds transparency before its image data.
    /// </summary>
    private static bool HasNoTransparency(string resourceName)
    {
        using var stream = typeof(AppearanceControlsTests).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new BinaryReader(stream!);
        reader.ReadBytes(8); // the signature
        while (true)
        {
            var length = ReadBigEndian(reader);
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (type == "IHDR")
            {
                var header = reader.ReadBytes(length);
                if (header[9] is 4 or 6)
                {
                    return false;
                }
            }
            else if (type == "tRNS")
            {
                return false;
            }
            else if (type is "IDAT" or "IEND")
            {
                return true;
            }
            else
            {
                stream!.Seek(length, SeekOrigin.Current);
            }

            reader.ReadBytes(4); // the CRC
        }
    }

    private static int ReadBigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }
}
