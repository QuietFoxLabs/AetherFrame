using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>The element list's edges: a ZIndex that can't be exceeded, and insertion by index.</summary>
public class ZIndexEdgeTests
{
    private static async Task<(LibraryFixture Fixture, ProfileService Profiles)> OpenBlankAsync()
    {
        var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var profiles = new ProfileService(library);
        profiles.OpenPlate(plate.PlateId);
        return (fixture, profiles);
    }

    [Fact]
    public async Task AddingAboveAnElementAtIntMaxValue_PaintsOnTop_InsteadOfWrapping()
    {
        var (fixture, profiles) = await OpenBlankAsync();
        using (fixture)
        {
            var top = new TextProfileElement { Text = "top", ZIndex = int.MaxValue };
            profiles.CurrentProfile!.Elements.Add(top);

            var added = profiles.AddTextElement("new");
            var duplicated = profiles.DuplicateElement(added);
            var image = profiles.AddElement(new ImageProfileElement { AssetId = Guid.NewGuid() });

            var elements = profiles.CurrentProfile.Elements;
            Assert.All(elements, e => Assert.Equal(int.MaxValue, e.ZIndex));
            var paintOrder = new List<ProfileElement>();
            ProfilePaintOrder.Fill(profiles.CurrentProfile, paintOrder, includeHidden: true);
            Assert.Equal([top.Id, added, duplicated, image], paintOrder.Select(e => e.Id));
        }
    }

    [Fact]
    public async Task AddingAboveAnOrdinaryElement_StillTakesTheNextZIndex()
    {
        var (fixture, profiles) = await OpenBlankAsync();
        using (fixture)
        {
            profiles.CurrentProfile!.Elements.Add(new TextProfileElement { ZIndex = 5 });

            var added = profiles.AddTextElement("new");

            Assert.Equal(6, profiles.CurrentProfile.Elements.Single(e => e.Id == added).ZIndex);
        }
    }

    [Fact]
    public async Task InsertElement_AtAnIndex_PutsItBackAmongItsPeers()
    {
        var (fixture, profiles) = await OpenBlankAsync();
        using (fixture)
        {
            var elements = profiles.CurrentProfile!.Elements;
            var a = new TextProfileElement { Text = "a" };
            var b = new TextProfileElement { Text = "b" };
            var c = new TextProfileElement { Text = "c" };
            elements.AddRange([a, b, c]);
            var snapshot = profiles.CloneElement(b.Id);

            profiles.RemoveElement(b.Id);
            profiles.InsertElement(snapshot.Clone(), 1);
            Assert.Equal([a.Id, b.Id, c.Id], elements.Select(e => e.Id));

            // Replacing an existing element keeps a single copy at the requested index; an index
            // past the end appends; no index appends; a negative index goes first.
            profiles.InsertElement(snapshot.Clone(), 99);
            Assert.Equal([a.Id, c.Id, b.Id], elements.Select(e => e.Id));
            profiles.InsertElement(snapshot.Clone(), 0);
            Assert.Equal([b.Id, a.Id, c.Id], elements.Select(e => e.Id));
            profiles.InsertElement(snapshot.Clone());
            Assert.Equal([a.Id, c.Id, b.Id], elements.Select(e => e.Id));
            profiles.InsertElement(snapshot.Clone(), -1);
            Assert.Equal([b.Id, a.Id, c.Id], elements.Select(e => e.Id));

            var paintOrder = new List<ProfileElement>();
            ProfilePaintOrder.Fill(profiles.CurrentProfile, paintOrder, includeHidden: true);
            Assert.Equal(elements.Select(e => e.Id), paintOrder.Select(e => e.Id));
        }
    }
}
