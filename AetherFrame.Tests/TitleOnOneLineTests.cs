using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Basic offers the title only on the name's line (Inline Before, Inline After): the title above or
/// below the name (Classic, Subtitle) is retired, like Badge and Accent before it. New identity headers
/// start on one line; a Plate saved with a retired layout opens unchanged until the player puts its
/// title on one line (the editor's "Put on One Line", which keeps the title's order).
/// </summary>
public class TitleOnOneLineTests
{
    [Theory]
    [InlineData(IdentityTitleLayout.InlineBefore, true)]
    [InlineData(IdentityTitleLayout.InlineAfter, true)]
    [InlineData(IdentityTitleLayout.Classic, false)]
    [InlineData(IdentityTitleLayout.Subtitle, false)]
    [InlineData(IdentityTitleLayout.Badge, false)]
    [InlineData(IdentityTitleLayout.Accent, false)]
    public void OnlyTheOneLineLayouts_AreOffered(IdentityTitleLayout layout, bool offered)
    {
        Assert.Equal(offered, BasicIdentitySession.IsOffered(layout));
    }

    [Theory]
    [InlineData(IdentityTitleLayout.Classic, IdentityTitleLayout.InlineBefore)] // the title was above: it stays first
    [InlineData(IdentityTitleLayout.Subtitle, IdentityTitleLayout.InlineAfter)] // the title was below: it stays last
    [InlineData(IdentityTitleLayout.Badge, IdentityTitleLayout.InlineAfter)]
    [InlineData(IdentityTitleLayout.Accent, IdentityTitleLayout.InlineAfter)]
    [InlineData(IdentityTitleLayout.InlineBefore, IdentityTitleLayout.InlineBefore)]
    [InlineData(IdentityTitleLayout.InlineAfter, IdentityTitleLayout.InlineAfter)]
    public void PuttingOnOneLine_KeepsTheTitlesOrder(IdentityTitleLayout layout, IdentityTitleLayout expected)
    {
        Assert.Equal(expected, BasicIdentitySession.OneLineFor(layout));
        Assert.True(BasicIdentitySession.IsOffered(BasicIdentitySession.OneLineFor(layout)));
    }

    [Fact]
    public void AGameTitlesPlacement_IsBeforeOrAfterTheName_OnItsLine()
    {
        Assert.Equal(IdentityTitleLayout.InlineBefore, BasicIdentitySession.GamePlacement(isPrefix: true));
        Assert.Equal(IdentityTitleLayout.InlineAfter, BasicIdentitySession.GamePlacement(isPrefix: false));
    }

    [Fact]
    public void ANewPlate_PutsItsTitleAfterTheName_WhileSavedPlatesKeepTheirDefault()
    {
        Assert.Equal(IdentityTitleLayout.InlineAfter, BasicDocuments.Classic(FakeCharacter.Hero).BasicIdentity!.Layout);

        // A Plate saved without the field still reads as before this change.
        Assert.Equal(IdentityTitleLayout.Subtitle, new BasicIdentityHeader().Layout);
    }

    [Theory]
    [InlineData(IdentityTitleLayout.Classic)]
    [InlineData(IdentityTitleLayout.Subtitle)]
    public async Task APlateWithItsTitleAboveOrBelow_OpensUnchanged(IdentityTitleLayout layout)
    {
        var document = Stacked(layout);
        var expectedJson = JsonSerializer.Serialize(document, JsonOptions.Default);

        using var harness = await BasicHarness.OpenDocumentAsync(document);
        harness.SimulateBasicFrame();

        Assert.Equal(expectedJson, harness.Json());
        Assert.False(harness.Session.IsDirty);
        Assert.Equal(layout, harness.Document.BasicIdentity!.Layout);
    }

    [Theory]
    [InlineData(IdentityTitleLayout.Classic, IdentityTitleLayout.InlineBefore)]
    [InlineData(IdentityTitleLayout.Subtitle, IdentityTitleLayout.InlineAfter)]
    public async Task PuttingOnOneLine_PlacesTheTitleOnTheNamesLine_AsOneUndoStep(IdentityTitleLayout layout, IdentityTitleLayout expected)
    {
        using var harness = await BasicHarness.OpenDocumentAsync(Stacked(layout));
        harness.SimulateBasicFrame();
        var before = harness.Json();

        harness.Identity.SetLayout(BasicIdentitySession.OneLineFor(layout));
        harness.SimulateBasicFrame();

        Assert.Equal(expected, harness.Document.BasicIdentity!.Layout);
        var name = BasicSections.FindText(harness.Document, ProfileElementRole.BasicName)!;
        var title = BasicSections.FindText(harness.Document, ProfileElementRole.BasicTitle)!;
        var nameMiddle = name.Position.Y + (name.Size.Y / 2f);
        Assert.InRange(nameMiddle, title.Position.Y, title.Position.Y + title.Size.Y); // one line, not stacked
        Assert.Equal("the Brave", title.Text);

        harness.Session.Undo();
        Assert.Equal(before, harness.Json());
    }

    /// <summary>A Classic Plate with a custom title stacked above (Classic) or below (Subtitle) the name, as saved before.</summary>
    private static ProfileDocument Stacked(IdentityTitleLayout layout)
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var title = IdentityHeaderRules.Create(ProfileElementRole.BasicTitle, document, null);
        title.Text = "the Brave";
        title.ZIndex = 50;
        document.Elements.Add(title);
        document.BasicIdentity!.Layout = layout;
        document.BasicIdentity.TitleSource = IdentityTitleSource.Custom;
        document.BasicIdentity.CustomTitle = "the Brave";
        document.BasicIdentity.LayoutStyle = null;
        IdentityHeaderRules.Place(document, _ => 100f);
        return document;
    }
}
