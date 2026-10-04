using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services.Fonts;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The font list's previews (issue #116), against the same fake font system as the Plate's fonts:
/// each family's preview is built once, in an atlas of its own, and never rebuilds another family's
/// atlas or a Plate's font; at most two start a frame and four build at once, however fast the list
/// scrolls; what was scrolled past is let go down to a bound; reopening the list draws its previews
/// at once; and a before/after measurement of scrolling the list with one atlas for every preview.
/// </summary>
public partial class FontHandleCacheTests
{
    /// <summary>Every row of the list, in its order.</summary>
    private static readonly string[] Catalog = ProfileFontCatalog.All.Select(family => family.Id).ToArray();

    /// <summary>The preview tier at Dalamud's interface font at 100% (FontPreview.Size(16)).</summary>
    private const float PreviewSize = 28f;

    /// <summary>The rows a screen of the list shows (a 1080p screen at 100%).</summary>
    private const int Screen = 14;

    private const int MaxStartsPerFrame = FontPreviews<FakeAtlas, FakeHandle>.MaxStartsPerFrame;

    private const int MaxBuilding = FontPreviews<FakeAtlas, FakeHandle>.MaxBuilding;

    /// <summary>The font list, drawn a frame at a time over the fake font system.</summary>
    private sealed class PreviewList
    {
        public PreviewList(FakeFonts? fonts = null, Clock? clock = null)
        {
            Fonts = fonts ?? new FakeFonts();
            Clock = clock ?? new Clock();
            Previews = new FontPreviews<FakeAtlas, FakeHandle>(Fonts, () => Clock.Now, () => Frame);
        }

        public FakeFonts Fonts { get; }

        public Clock Clock { get; }

        public FontPreviews<FakeAtlas, FakeHandle> Previews { get; }

        public long Frame { get; private set; }

        /// <summary>The most previews started in one frame so far.</summary>
        public int MostStartedInAFrame { get; private set; }

        /// <summary>The most previews building at once so far.</summary>
        public int MostBuildingAtOnce { get; private set; }

        /// <summary>
        /// One frame of the list with <paramref name="rows"/> rows on screen from <paramref name="first"/>:
        /// each asks for its preview; the builds started finish before the next frame unless
        /// <paramref name="finish"/> is false; 16 ms pass. Returns the rows drawn in their own face.
        /// </summary>
        public int Show(int first, int rows = Screen, bool finish = true)
        {
            var created = Fonts.HandlesCreated;
            var drawn = 0;
            for (var i = first; i < Math.Min(first + rows, Catalog.Length); i++)
            {
                if (Previews.Get(Catalog[i], PreviewSize) is not null)
                {
                    drawn++;
                }
            }

            MostStartedInAFrame = Math.Max(MostStartedInAFrame, Fonts.HandlesCreated - created);
            MostBuildingAtOnce = Math.Max(MostBuildingAtOnce, Previews.Building);
            if (finish)
            {
                Fonts.CompleteBuilds();
            }

            Clock.Advance(16);
            Frame++;
            return drawn;
        }

        /// <summary>The list closed for <paramref name="milliseconds"/>: the game draws on, the list doesn't.</summary>
        public void Closed(long milliseconds)
        {
            Clock.Advance(milliseconds);
            Frame += milliseconds / 16;
        }
    }

    [Fact]
    public void ScrollingTheList_BuildsEachFamilysPreviewOnce_InAnAtlasOfItsOwn_AndNeverRebuildsAPlatesFont()
    {
        var fonts = new FakeFonts();
        var clock = new Clock();
        var plate = new FontHandleCache<FakeAtlas, FakeHandle>(fonts, () => clock.Now);

        // The Plate being edited, drawn every frame: AetherFrame Sans and Cinzel, which the list previews too.
        void DrawPlate()
        {
            plate.Get(ProfileFontFamilies.AetherFrameSans, Tier(32f), false, false);
            plate.Get("gf-cinzel", Tier(32f), false, false);
        }

        DrawPlate();
        fonts.CompleteBuilds();
        var plateAtlases = fonts.Atlases.ToList();
        var list = new PreviewList(fonts, clock);

        // From the top to the bottom, a row a frame, then left there.
        for (var first = 0; first <= Catalog.Length - Screen; first++)
        {
            DrawPlate();
            list.Show(first);
        }

        for (var frame = 0; frame < 10; frame++)
        {
            DrawPlate();
            list.Show(Catalog.Length - Screen);
        }

        // Each family's preview: one atlas, made once, built once, holding its one face.
        var previewAtlases = fonts.Atlases.Except(plateAtlases).ToList();
        Assert.Equal(Catalog.Select(id => FontTierPolicy.ResolveFamilyId(id)).Order(StringComparer.Ordinal), previewAtlases.Select(a => a.Name).Order(StringComparer.Ordinal));
        Assert.All(previewAtlases, a => Assert.Equal(1, a.Rebuilds));
        Assert.All(previewAtlases, a => Assert.Equal(Key(a.Name, PreviewSize), Assert.Single(a.Handles).Key));

        // The Plate's fonts were never rebuilt, and nothing else was built.
        Assert.All(plateAtlases, a => Assert.Equal(1, a.Rebuilds));
        Assert.Equal(plateAtlases.Count + Catalog.Length, fonts.Rebuilt.Count);
        Assert.Equal(Screen, list.Show(Catalog.Length - Screen));
        Assert.True(list.MostStartedInAFrame <= MaxStartsPerFrame);
    }

    [Fact]
    public void PreviewBuilds_StartAtMostTwoAFrame_AndAtMostFourAtOnce()
    {
        var list = new PreviewList();

        // The list opens on a screen of rows, none built.
        Assert.Equal(0, list.Show(0, finish: false));
        Assert.Equal(MaxStartsPerFrame, list.Fonts.Atlases.Count);
        list.Show(0, finish: false);
        Assert.Equal(MaxBuilding, list.Fonts.Atlases.Count);
        list.Show(0, finish: false);
        Assert.Equal(MaxBuilding, list.Fonts.Atlases.Count); // four building: the rest wait their turn

        list.Fonts.CompleteBuilds();
        Assert.Equal(MaxBuilding, list.Show(0)); // the four built are drawn, and two more start
        Assert.Equal(MaxBuilding + MaxStartsPerFrame, list.Fonts.Atlases.Count);

        // Every row gets its face in the end, never more than two started in a frame.
        for (var frame = 0; frame < 10; frame++)
        {
            list.Show(0);
        }

        Assert.Equal(Screen, list.Show(0));
        Assert.Equal(Screen, list.Fonts.Atlases.Count);
        Assert.Equal(MaxStartsPerFrame, list.MostStartedInAFrame);
        Assert.Equal(MaxBuilding, list.MostBuildingAtOnce);
    }

    [Fact]
    public void AFastScroll_StartsOnlyAFewAFrame_AndWhatItPassedIsLetGoDownToTheBound()
    {
        var list = new PreviewList();

        // Flung through the whole list and back, half a screen a frame, with builds taking two frames.
        var first = 0;
        var step = Screen / 2;
        for (var frame = 0; frame < 120; frame++)
        {
            list.Show(first, finish: frame % 2 == 1);
            if (first + step < 0 || first + step > Catalog.Length - Screen)
            {
                step = -step;
            }

            first += step;
        }

        Assert.True(list.MostStartedInAFrame <= MaxStartsPerFrame, $"{list.MostStartedInAFrame}");
        Assert.True(list.MostBuildingAtOnce <= MaxBuilding, $"{list.MostBuildingAtOnce}");

        // Then left on one screen: the rest goes, down to the bound, and the screen is drawn in full.
        for (var frame = 0; frame < 200; frame++)
        {
            list.Show(40);
        }

        Assert.Equal(Screen, list.Show(40));
        Assert.True(list.Previews.Count <= FontPreviews<FakeAtlas, FakeHandle>.MaxKept, $"{list.Previews.Count} kept");
        Assert.All(list.Fonts.Atlases.Where(a => a.Disposed), a => Assert.Empty(a.Handles));
        Assert.All(list.Fonts.Atlases.Where(a => !a.Disposed), a => Assert.Single(a.Handles));
    }

    [Fact]
    public void ScrollingBack_OrReopeningTheList_DrawsItsPreviewsAtOnce()
    {
        var list = new PreviewList();
        for (var frame = 0; frame < 20; frame++)
        {
            list.Show(0);
        }

        // A screen down for a few seconds, and back.
        for (var frame = 0; frame < 200; frame++)
        {
            list.Show(Screen);
        }

        var built = list.Fonts.Rebuilt.Count;
        Assert.Equal(Screen, list.Show(0));

        // Closed for ten minutes, then opened where it was (issue #114).
        list.Closed(10 * 60_000);
        Assert.Equal(Screen, list.Show(0));
        Assert.Equal(built, list.Fonts.Rebuilt.Count);
    }

    [Fact]
    public void AFaceThatNeverBuilds_HoldsTheOthersBack_OnlyUntilItsTimeRunsOut()
    {
        var list = new PreviewList();
        list.Show(0, finish: false);
        list.Show(0, finish: false);
        Assert.Equal(MaxBuilding, list.Previews.Building);

        // Their fonts failed to load: these four never build.
        foreach (var atlas in list.Fonts.Atlases)
        {
            atlas.Dirty = false;
        }

        Assert.Equal(0, list.Show(0));
        Assert.Equal(MaxBuilding, list.Fonts.Atlases.Count);

        list.Clock.Advance(FontPreviews<FakeAtlas, FakeHandle>.BuildTimeoutMilliseconds);
        list.Show(0);
        Assert.Equal(MaxBuilding + MaxStartsPerFrame, list.Fonts.Atlases.Count);
    }

    [Fact]
    public void AnotherInterfaceScale_ReplacesAFamilysPreview_SoEachHasOneFace()
    {
        var list = new PreviewList();
        list.Show(0, rows: 1);
        var first = Assert.Single(list.Fonts.Atlases);

        // The interface grew: the list asks for a larger tier.
        Assert.Null(list.Previews.Get(Catalog[0], 32f));
        Assert.True(first.Disposed);
        list.Fonts.CompleteBuilds();

        Assert.Equal(32f, list.Previews.Get(Catalog[0], 32f)!.Key.SizePx);
        Assert.Equal(1, list.Previews.Count);
    }

    [Fact]
    public void Dispose_LetsGoOfEveryPreview()
    {
        var list = new PreviewList();
        list.Show(0);
        list.Show(0, finish: false);

        list.Previews.Dispose();

        Assert.All(list.Fonts.Atlases, a => Assert.True(a.Disposed && a.Handles.Count == 0));
        Assert.Throws<ObjectDisposedException>(() => list.Previews.Get(Catalog[0], PreviewSize));
    }

    /// <summary>
    /// Scrolling the font list from top to bottom, a row a frame. Before (the model of putting the
    /// previews in one atlas, as the Plate's fonts were before #117): every new preview rebuilds every
    /// preview before it. After (an atlas each): every new preview rasterizes its own face only. The
    /// glyph surface is the fake's estimate for a Plate tier of the whole face; a preview holds Basic
    /// Latin only, a fraction of that (FontListLayoutTests measures it).
    /// </summary>
    [Fact]
    public void ScrollingTheList_EachPreviewCostsItsOwnFace_NotEveryPreviewBeforeIt()
    {
        var before = ScrollPreviews(oneSharedAtlas: true);
        var after = ScrollPreviews(oneSharedAtlas: false);

        output.WriteLine("Frame | one atlas for every preview: glyph px rebuilt | an atlas each: glyph px rebuilt");
        foreach (var frame in new[] { 0, 10, 30, 60, 90, after.Count - 1 })
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{frame + 1} | {before[frame]:N0} | {after[frame]:N0}"));
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Total over {after.Count} frames | {before.Sum():N0} | {after.Sum():N0}"));

        // After: in all, each family's face once, the most any frame costs being two faces.
        var oneFaceEach = Catalog.Select(id => FontTierPolicy.EstimatedSurfacePixels(id, PreviewSize)).Sum();
        var heaviestFace = Catalog.Max(id => FontTierPolicy.EstimatedSurfacePixels(id, PreviewSize));
        Assert.Equal(oneFaceEach, after.Sum());
        Assert.All(after, cost => Assert.True(cost <= MaxStartsPerFrame * heaviestFace));

        // Before: the frame that adds the last preview rebuilds every one before it.
        Assert.Equal(oneFaceEach, before.Max());
        Assert.True(before.Sum() > 20 * after.Sum());
    }

    /// <summary>The glyph surface rebuilt in each frame of scrolling the whole list.</summary>
    private static List<long> ScrollPreviews(bool oneSharedAtlas)
    {
        var list = new PreviewList(new FakeFonts(oneSharedAtlas));
        var perFrame = new List<long>();
        for (var frame = 0; frame < Catalog.Length - Screen + 10; frame++)
        {
            var rebuiltBefore = list.Fonts.Rebuilt.Sum();
            list.Show(Math.Min(frame, Catalog.Length - Screen));
            perFrame.Add(list.Fonts.Rebuilt.Sum() - rebuiltBefore);
        }

        Assert.Equal(Screen, list.Show(Catalog.Length - Screen));
        return perFrame;
    }
}
