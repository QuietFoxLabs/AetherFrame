using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Persistence;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The bundled art sets (<see cref="ArtSets"/>): nineteen new sets of seven pieces and Celestial Sakura's
/// Section Header, as artwork, Components and Art Styles; where their pieces paint; and what applying a
/// style does to a Plate.
/// </summary>
public class ArtSetsTests
{
    private static readonly PlateComponentKind[] SevenKinds =
    [
        PlateComponentKind.Background, PlateComponentKind.PlateFrame, PlateComponentKind.PortraitFrame, PlateComponentKind.NameBacking,
        PlateComponentKind.Divider, PlateComponentKind.SectionHeader, PlateComponentKind.CornerOrnament,
    ];

    private static ProfileThemePreset Style(string slug) => ProfileThemePresets.Find("af.style." + slug)!;

    public static IEnumerable<object[]> StyleIds() => ArtSets.Styles.Select(s => new object[] { s.Id });

    public static IEnumerable<object[]> AssetIds() => ArtSets.Assets.Select(a => new object[] { a.Id });

    // ---- Catalog --------------------------------------------------------------------------------

    [Fact]
    public void NineteenNewSets_OfSevenPieces_AndSakurasSectionHeader_WithIdsMadeFromTheirSlugs()
    {
        Assert.Equal(19, ArtSetData.Sets.Length);
        Assert.Equal((19 * 7) + 1, ArtSets.Assets.Count);
        Assert.Equal(ArtSets.Assets.Count, ArtSets.Definitions.Count);
        Assert.Equal(20, ArtSets.Styles.Count);
        Assert.Equal(19, ArtSetData.Sets.Select(s => s.Slug).Distinct(StringComparer.Ordinal).Count());

        foreach (var set in ArtSetData.Sets)
        {
            foreach (var kind in SevenKinds)
            {
                var slug = KindSlug(kind);
                var definition = BuiltInComponentCatalog.Find($"af.{slug}.{set.Slug}");
                Assert.NotNull(definition);
                Assert.Equal(kind, definition!.Kind);
                Assert.Same(BuiltInArtCatalog.Find($"af.asset.{set.Slug}.{slug}.standard"), definition.Art);
                Assert.Equal(set.Name, definition.Family);
                Assert.StartsWith(set.Name + ": ", definition.Description, StringComparison.Ordinal);
            }
        }

        Assert.Equal(PlateComponentKind.SectionHeader, BuiltInComponentCatalog.Find(ArtSets.SectionHeaderCelestialSakura)!.Kind);
        Assert.Equal(BuiltInArtCatalog.CelestialSakuraFamily, BuiltInArtCatalog.Find(ArtSets.CelestialSakuraSectionHeader)!.Family);
    }

    [Theory]
    [MemberData(nameof(StyleIds))]
    public void EveryStyle_PlacesOnePieceOfEachKind_FromItsOwnSet(string styleId)
    {
        var style = ProfileThemePresets.Find(styleId)!;
        Assert.True(style.IsArtStyle);
        Assert.Equal(ThemeFamily.ArtStyle, style.Family);
        Assert.NotNull(style.PreviewResource);

        var definitions = style.Components.Select(id => BuiltInComponentCatalog.Find(id)!).ToList();
        Assert.Equal(SevenKinds.Order(), definitions.Select(d => d.Kind).Order());
        Assert.All(definitions, d => Assert.Equal(style.Name, d.Family));
        Assert.All(style.Components, id => Assert.Same(style, ArtSets.StyleOf(id)));
    }

    [Theory]
    [MemberData(nameof(AssetIds))]
    public void EveryPiece_IsEmbedded_AndLoadsAtItsCatalogSize(string assetId)
    {
        var art = BuiltInArtCatalog.Find(assetId)!;
        var png = ReadResource(art.ResourceName);
        var levels = BundledArtImage.LoadLevels(png, art); // throws unless the PNG is exactly the catalog's size

        Assert.Equal((art.PixelWidth, art.PixelHeight), (levels[0].Width, levels[0].Height));
        Assert.False(art.Tintable);
        var image = BundledArtImage.DecodePng(png);
        if (art.Kind == PlateComponentKind.Background)
        {
            Assert.Equal(255, Alpha(image, image.Width / 2, image.Height / 2));
        }
        else
        {
            // Real transparency, no backdrop: every corner invisible (a few unedited sources keep a
            // stray 1/255, which nothing can see).
            foreach (var (x, y) in new[] { (0, 0), (image.Width - 1, 0), (0, image.Height - 1), (image.Width - 1, image.Height - 1) })
            {
                Assert.InRange(Alpha(image, x, y), 0, 2);
            }
        }

        Assert.Equal(ComponentPaintPlan.FollowsText(art.Kind), ComponentPaintPlan.IsSliced(art));
        Assert.InRange(art.SizeFactor, 0.25f, 4f);
    }

    [Theory]
    [MemberData(nameof(StyleIds))]
    public void EveryStylesPreview_IsEmbedded_AndLoads(string styleId)
    {
        var preview = ArtSets.PreviewArt(ProfileThemePresets.Find(styleId)!)!;
        var levels = BundledArtImage.LoadLevels(ReadResource(preview.ResourceName), preview);
        Assert.Equal((ArtSets.PreviewWidth, ArtSets.PreviewHeight), (levels[0].Width, levels[0].Height));
        Assert.Null(BuiltInArtCatalog.Find(preview.Id)); // never a Component
    }

    [Fact]
    public void RuntimeFiles_AreExactlyWhatTheirRecordSays_AndStayInBudget()
    {
        var assets = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Assets");
        var folders = ArtSetData.Sets.ToDictionary(s => s.Name, s => s.Folder, StringComparer.Ordinal);
        folders.Add(BuiltInArtCatalog.CelestialSakuraFamily, "CelestialSakura");

        var rows = 0;
        long bytes = 0;
        foreach (var line in File.ReadAllLines(Path.Combine(assets, "ArtSets.md")))
        {
            var cells = line.Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length != 10 || !folders.TryGetValue(cells[1], out var folder))
            {
                continue;
            }

            var file = Path.Combine(assets, "Components", folder, $"{folder}_{cells[2]}.png");
            var data = File.ReadAllBytes(file);
            Assert.Equal(cells[8].Trim('`'), Convert.ToHexStringLower(SHA256.HashData(data)));
            Assert.Equal(long.Parse(cells[4], System.Globalization.CultureInfo.InvariantCulture), data.Length);
            bytes += data.Length;
            rows++;
        }

        Assert.Equal(ArtSets.Assets.Count, rows);
        foreach (var set in ArtSetData.Sets)
        {
            Assert.Equal(7, Directory.GetFiles(Path.Combine(assets, "Components", set.Folder)).Length);
        }

        // The owner's choice: every set, with the small pieces at half size, about 77 MB.
        bytes += Directory.GetFiles(Path.Combine(assets, "StylePreviews")).Sum(f => new FileInfo(f).Length);
        Assert.InRange(bytes, 50_000_000L, 80_000_000L);
    }

    // ---- Readability -----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(StyleIds))]
    public void EveryStylesText_ContrastsWithTheArtBehindIt(string styleId)
    {
        var style = ProfileThemePresets.Find(styleId)!;
        var pieces = style.Components.Select(id => BuiltInComponentCatalog.Find(id)!).ToDictionary(d => d.Kind, d => d.Art!);

        // The name on its backing's band, the headings (and the title) on theirs: WCAG AA for text.
        Assert.True(Contrast(style.PreferredNameColor, BandColor(pieces[PlateComponentKind.NameBacking])) >= 4.5f, "name");
        Assert.True(Contrast(style.AccentTextColor, BandColor(pieces[PlateComponentKind.SectionHeader])) >= 4.5f, "headings");

        // The Details on the background, behind the panel they fill.
        var background = BundledArtImage.DecodePng(ReadResource(pieces[PlateComponentKind.Background].ResourceName));
        var panel = MeanColor(background, (int)(background.Width * 0.375f), (int)(background.Height * 0.25f), (int)(background.Width * 0.97f), (int)(background.Height * 0.95f));
        Assert.True(Contrast(style.TextColor, panel) >= 4.5f, "details");
    }

    // ---- Where the pieces paint -------------------------------------------------------------------

    [Fact]
    public void ASectionHeaderBacking_SitsBehindEveryElement_AroundEachHeadingsMeasuredText()
    {
        var document = ComponentDocuments.WithAnchors();
        var heading = (TextProfileElement)document.Elements.Single(e => e.Role == ProfileElementRole.BasicWorldHeading);
        heading.Wrap = false;
        var header = ComponentDocuments.Of("af.section-header.allagan-tech");
        document.Components = [header];
        var art = BuiltInComponentCatalog.Find(header.DefinitionId)!.Art!;

        var plan = Plan(document, e => e.Text.Length * 9f);
        var step = Assert.Single(plan, s => ReferenceEquals(s.Component, header));
        Assert.True(plan.IndexOf(step) < plan.FindIndex(s => s.IsElement), "behind every element");

        var rect = step.Placement.Rect;
        var anchor = new Vector2((10 * 9f) + (2f * TextProfileElement.LayoutPadding) + 2f + (2f * ComponentPaintPlan.SectionHeaderPadX), heading.Size.Y);
        Assert.Equal(heading.Size.Y * art.SizeFactor, rect.Size.Y, 3);
        Assert.Equal(ComponentPaintPlan.SlicedSize(anchor, art, art.SizeFactor).X, rect.Size.X, 2);
        var center = rect.Position + (rect.Size / 2f);
        Assert.Equal(heading.Position.X + ((anchor.X - (2f * ComponentPaintPlan.SectionHeaderPadX)) / 2f), center.X, 2);
        Assert.Equal(heading.Position.Y + (heading.Size.Y / 2f), center.Y, 2);

        // A longer heading, a longer label at the same height.
        heading.Text = "HOME WORLD OF THE HERO";
        var longer = Plan(document, e => e.Text.Length * 9f).Single(s => ReferenceEquals(s.Component, header)).Placement.Rect;
        Assert.True(longer.Size.X > rect.Size.X);
        Assert.Equal(rect.Size.Y, longer.Size.Y, 3);
    }

    [Fact]
    public void AProceduralSectionHeader_StillMarksItsHeadingFromAbove()
    {
        var document = ComponentDocuments.WithAnchors();
        var underline = ComponentDocuments.Of(BuiltInComponentCatalog.SectionHeaderUnderline);
        document.Components = [underline];

        var plan = ComponentDocuments.Plan(document);
        Assert.True(plan.FindIndex(s => ReferenceEquals(s.Component, underline)) > plan.FindLastIndex(s => s.IsElement));
    }

    [Fact]
    public void PlateFrameArtwork_PaintsUnderEveryText_AProceduralFrameStillOnTop()
    {
        var document = ComponentDocuments.WithAnchors();
        var art = ComponentDocuments.Of("af.plate-frame.allagan-tech");
        var line = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine);
        document.Components = [line, art];

        var plan = ComponentDocuments.Plan(document);
        var artFrame = plan.FindIndex(s => ReferenceEquals(s.Component, art));
        Assert.Equal(plan.FindIndex(s => s.Element is TextProfileElement) - 1, artFrame);
        Assert.True(artFrame > plan.FindIndex(s => s.Element?.Role == ProfileElementRole.BasicPortrait), "over the portrait");
        Assert.Same(line, plan[^1].Component);
    }

    /// <summary>
    /// A Plate made the way the Basic editor makes it: the portrait is added after the starter text,
    /// so it is stacked above that text. Plate Frame artwork still paints over the portrait and its
    /// frame, as the preview cards draw it, and under every text and the name plaque.
    /// </summary>
    [Fact]
    public void OnARealPlate_PlateFrameArtwork_PaintsOverThePortraitAndItsFrame_UnderEveryText()
    {
        var document = PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "Style", ComponentDocuments.Now, new PlateStarterContent(FakeCharacter.Hero));
        var portrait = BasicDocuments.Editor(document).CreatePortrait(Guid.NewGuid());
        Assert.All(document.Elements.OfType<TextProfileElement>(), text => Assert.True(text.ZIndex < portrait.ZIndex));
        document.Components = Style("allagan-tech").Components.Select(id => ComponentDocuments.Of(id)).ToList();

        var plan = ComponentDocuments.Plan(document);
        var frame = plan.FindIndex(s => s.Component?.Kind == PlateComponentKind.PlateFrame);
        Assert.Single(plan, s => ReferenceEquals(s.Element, portrait));
        Assert.True(plan.FindIndex(s => ReferenceEquals(s.Element, portrait)) < frame, "over the portrait");
        Assert.True(plan.FindIndex(s => s.Component?.Kind == PlateComponentKind.PortraitFrame) < frame, "over the portrait's frame");
        Assert.True(frame < plan.FindIndex(s => s.Component?.Kind == PlateComponentKind.NameBacking), "under the name plaque");
        Assert.True(frame < plan.FindIndex(s => s.Element is TextProfileElement), "under every text");

        // Without Plate Frame artwork, the portrait keeps its place in the stack.
        document.Components.RemoveAll(c => c.Kind == PlateComponentKind.PlateFrame);
        var unframed = ComponentDocuments.Plan(document);
        Assert.True(unframed.FindIndex(s => ReferenceEquals(s.Element, portrait)) > unframed.FindLastIndex(s => s.Element is TextProfileElement));
    }

    [Fact]
    public void PlateFrameArtwork_OnAPlateWithoutText_PaintsOnTop()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Elements.RemoveAll(e => e is TextProfileElement);
        var art = ComponentDocuments.Of("af.plate-frame.allagan-tech");
        document.Components = [art];

        Assert.Same(art, ComponentDocuments.Plan(document)[^1].Component);
    }

    // ---- Applying a style -----------------------------------------------------------------------------

    [Fact]
    public async Task ApplyingAStyle_PlacesItsSevenPieces_AndItsColors_AsOneUndoStep()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var before = harness.Json();
        var style = Style("allagan-tech");

        harness.Basic.ApplyTheme(style);

        foreach (var id in style.Components)
        {
            var kind = BuiltInComponentCatalog.Find(id)!.Kind;
            Assert.Equal(id, PlateComponentEditor.FindSlot(harness.Document, kind)!.DefinitionId);
        }

        Assert.Equal(style.Id, harness.Document.BasicPlate!.ThemeId);
        Assert.Equal(style.PreferredNameColor, Text(harness, ProfileElementRole.BasicName).Color);
        Assert.Equal(style.AccentTextColor with { W = 1f }, Text(harness, ProfileElementRole.BasicWorldHeading).Color with { W = 1f });

        harness.Session.Undo();
        Assert.Equal(before, harness.Json());
    }

    /// <summary>
    /// A style's background artwork covers the Plate's own background. It is a Basic slot like every
    /// other piece: taking it away is one undo step, leaves the style and its other pieces, and shows
    /// the Plate's own background, which the artwork never changed.
    /// </summary>
    [Fact]
    public async Task AStylesBackground_IsABasicSlot_AndTakingItAway_ShowsThePlatesOwnBackground()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var style = Style("allagan-tech");
        harness.Basic.ApplyTheme(style);
        var artwork = style.Components.Single(id => BuiltInComponentCatalog.Find(id)!.Kind == PlateComponentKind.Background);
        Assert.Equal(artwork, PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.Background)!.DefinitionId);
        var background = JsonSerializer.Serialize(harness.Document.Background, JsonOptions.Default);
        var withArtwork = harness.Json();

        harness.Session.SetComponentSlot(PlateComponentKind.Background, null);

        Assert.Null(PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.Background));
        Assert.All(SevenKinds.Where(k => k != PlateComponentKind.Background), kind => Assert.NotNull(PlateComponentEditor.FindSlot(harness.Document, kind)));
        Assert.Equal(style.Id, harness.Document.BasicPlate!.ThemeId);
        Assert.Equal(background, JsonSerializer.Serialize(harness.Document.Background, JsonOptions.Default));

        harness.Session.Undo();
        Assert.Equal(withArtwork, harness.Json());
    }

    [Fact]
    public async Task LeavingAStyle_ForASimpleTheme_TakesAwayItsPieces_ButNotOnesYouChose()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Basic.ApplyTheme(Style("allagan-tech"));
        harness.Session.SetComponentSlot(PlateComponentKind.PlateFrame, BuiltInComponentCatalog.PlateFrameLine);

        harness.Basic.ApplyTheme(ProfileThemePresets.Find("Dark")!);

        Assert.Equal(BuiltInComponentCatalog.PlateFrameLine, PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.PlateFrame)!.DefinitionId);
        Assert.All(SevenKinds.Where(k => k != PlateComponentKind.PlateFrame), kind => Assert.Null(PlateComponentEditor.FindSlot(harness.Document, kind)));
        Assert.Equal("Dark", harness.Document.BasicPlate!.ThemeId);
    }

    [Fact]
    public async Task SwitchingStyles_KeepsEachSlotsComponent_AndItsRefinements()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Basic.ApplyTheme(Style("allagan-tech"));
        var backing = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.NameBacking)!;
        harness.Session.EditComponent(backing.Id, c => c.Offset = new Vector2(5f, -3f), continuous: false);
        var ids = SevenKinds.ToDictionary(k => k, k => PlateComponentEditor.FindSlot(harness.Document, k)!.Id);

        var sakura = Style("celestial-sakura");
        harness.Basic.ApplyTheme(sakura);

        foreach (var id in sakura.Components)
        {
            var kind = BuiltInComponentCatalog.Find(id)!.Kind;
            var slot = PlateComponentEditor.FindSlot(harness.Document, kind)!;
            Assert.Equal(id, slot.DefinitionId);
            Assert.Equal(ids[kind], slot.Id);
        }

        Assert.Equal(new Vector2(5f, -3f), PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.NameBacking)!.Offset);
    }

    [Fact]
    public void TheBrowser_ListsTheArtStylesFirst_ThenTheSimpleThemes()
    {
        var groups = ThemeBrowser.Group(ProfileThemePresets.All, string.Empty, null);
        Assert.Equal(ThemeFamily.ArtStyle, groups[0].Family);
        Assert.Equal(20, groups[0].Themes.Count);
        Assert.All(groups.Skip(1), g => Assert.All(g.Themes, t => Assert.False(t.IsArtStyle)));

        Assert.Equal("Art Styles", ThemeBrowser.FamilyLabel(ThemeFamily.ArtStyle));
        Assert.Equal(ProfileThemePresets.SimpleThemes, ThemeBrowser.Filter(ProfileThemePresets.All, "simple", null));
        Assert.Equal("af.style.steampunk-machinist", Assert.Single(ThemeBrowser.Filter(ProfileThemePresets.All, "steampunk", null)).Id);
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static List<PaintStep> Plan(ProfileDocument document, Func<TextProfileElement, float?> measure)
    {
        var steps = new List<PaintStep>();
        ComponentPaintPlan.Build(document, document.Elements.Where(e => e.Visible).OrderBy(e => e.ZIndex).ToList(), BuiltInComponentCatalog.Instance, steps, measure);
        return steps;
    }

    private static TextProfileElement Text(BasicHarness harness, ProfileElementRole role) =>
        (TextProfileElement)harness.Document.Elements.Single(e => e.Role == role);

    /// <summary>The mean color of a sliced piece's plain band, where text sits: the left fill's middle rows.</summary>
    private static Vector4 BandColor(BuiltInArtAsset art)
    {
        var image = BundledArtImage.DecodePng(ReadResource(art.ResourceName));
        var slices = art.Slices!;
        var x0 = slices.CapLeft + 2;
        var x1 = slices.CenterLeft - 2;
        var column = (x0 + x1) / 2;
        var top = Enumerable.Range(0, image.Height).First(y => Alpha(image, column, y) > 128);
        var bottom = Enumerable.Range(0, image.Height).Last(y => Alpha(image, column, y) > 128);
        var middle = (top + bottom) / 2;
        var half = Math.Max(1, (bottom - top) / 6);
        return MeanColor(image, x0, middle - half, x1, middle + half + 1);
    }

    private static Vector4 MeanColor(ArtLevel image, int x0, int y0, int x1, int y1)
    {
        var sum = Vector3.Zero;
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = ((y * image.Width) + x) * 4;
                if (image.Rgba[i + 3] > 200)
                {
                    sum += new Vector3(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2]);
                    count++;
                }
            }
        }

        Assert.True(count > 0);
        var mean = sum / (count * 255f);
        return new Vector4(mean, 1f);
    }

    private static float Contrast(Vector4 a, Vector4 b)
    {
        var la = ComponentDefinition.RelativeLuminance(a);
        var lb = ComponentDefinition.RelativeLuminance(b);
        return (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
    }

    private static int Alpha(ArtLevel image, int x, int y) => image.Rgba[(((y * image.Width) + x) * 4) + 3];

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(ArtSetsTests).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream!.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string KindSlug(PlateComponentKind kind) => kind switch
    {
        PlateComponentKind.Background => "background",
        PlateComponentKind.PlateFrame => "plate-frame",
        PlateComponentKind.PortraitFrame => "portrait-frame",
        PlateComponentKind.NameBacking => "name-backing",
        PlateComponentKind.Divider => "divider",
        PlateComponentKind.SectionHeader => "section-header",
        _ => "corner-ornament",
    };
}
