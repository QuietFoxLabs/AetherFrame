using System.Linq;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services.Fonts;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// <see cref="ProfileFontCatalog"/>: the curated family list and the id lookup the font cache
/// runs for every text element it measures or draws (pinned so its allocation-free rewrite
/// behaves exactly like the search it replaced), and its agreement with
/// <see cref="FontTierPolicy"/>'s own family table.
/// </summary>
public class ProfileFontCatalogTests
{
    [Fact]
    public void EveryCatalogId_ResolvesToItsOwnDescriptor()
    {
        foreach (var descriptor in ProfileFontCatalog.All)
        {
            Assert.Same(descriptor, ProfileFontCatalog.Resolve(descriptor.Id));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("aetherframe-fancy")]
    [InlineData("AETHERFRAME-SANS")]
    [InlineData("aetherframe-sans ")]
    public void NullAndUnknownIds_ResolveToDalamudDefault(string? familyId)
    {
        Assert.Same(ProfileFontCatalog.DalamudDefault, ProfileFontCatalog.Resolve(familyId));
    }

    [Fact]
    public void TheCatalog_ListsCuratedFamiliesFirst_AndLegacyLast()
    {
        // The library's families sit between (FontLibraryTests checks them).
        var ids = ProfileFontCatalog.All.Select(d => d.Id).ToArray();
        Assert.Equal([ProfileFontFamilies.AetherFrameSans, ProfileFontFamilies.AetherFrameSerif, ProfileFontFamilies.AetherFrameMono], ids.Take(3));
        Assert.Equal(ProfileFontFamilies.DalamudDefault, ids[^1]);
        Assert.Equal(ProfileFontCatalog.All.Count, ProfileFontCatalog.All.Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public void OnlyCuratedFamilies_OfferRealBoldAndItalicFaces()
    {
        // AetherFrame's own and Dalamud Default; a library family offers what its files hold (FontLibraryTests).
        foreach (var descriptor in ProfileFontCatalog.All.Where(d => d.Category == AetherFrame.Domain.Rendering.FontCategory.AetherFrame))
        {
            var curated = descriptor.Id != ProfileFontFamilies.DalamudDefault;
            Assert.Equal(curated, descriptor.SupportsBold);
            Assert.Equal(curated, descriptor.SupportsItalic);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.DisplayName));
        }
    }

    [Fact]
    public void TheCatalog_AndTheTierPolicy_AgreeOnEveryFamily()
    {
        // The same ids resolve the same way in both tables, so a font handle's key, its glyph
        // ranges and its surface estimate always describe the same family.
        foreach (var descriptor in ProfileFontCatalog.All)
        {
            Assert.Equal(descriptor.Id, FontTierPolicy.ResolveFamilyId(descriptor.Id));
            Assert.Equal(descriptor.Id != ProfileFontFamilies.DalamudDefault, FontTierPolicy.GlyphRanges(descriptor.Id) is not null);
        }

        Assert.Equal(ProfileFontCatalog.DalamudDefault.Id, FontTierPolicy.ResolveFamilyId("not-a-family"));
    }
}
