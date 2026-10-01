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
/// start on one line, and a title turned on while none is shown goes on the name's line, on a Plate
/// saved before too. A title already shown above or below keeps its place until the player puts it on
/// one line (the editor's "Put on One Line", which keeps the title's order).
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
    public async Task ANewPlate_PutsItsTitleAfterTheName_WhileAPlateSavedWithoutTheFieldReadsAsBefore()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        Assert.Equal(IdentityTitleLayout.InlineAfter, document.BasicIdentity!.Layout);

        // The same Plate as a file without the field: it opens as Subtitle, as before this change.
        var json = JsonSerializer.SerializeToNode(document, JsonOptions.Default)!;
        Assert.True(json[nameof(ProfileDocument.BasicIdentity)]!.AsObject().Remove(nameof(BasicIdentityHeader.Layout)));
        using var harness = await BasicHarness.OpenJsonAsync(json.ToJsonString(JsonOptions.Default), document.ProfileId);
        Assert.Equal(IdentityTitleLayout.Subtitle, harness.Document.BasicIdentity!.Layout);
    }

    /// <summary>
    /// A new Plate is placed before any font is built, so its widths are estimates. Its name, with no
    /// title beside it, has the whole header region: a font wider than the estimate still draws the
    /// name at its full size, on one line.
    /// </summary>
    [Theory]
    [InlineData("Momomi Momo")]
    [InlineData("Mamaroon Mama")]
    [InlineData("Wuk Lamat")]
    public void ANewPlatesName_HasTheWholeHeaderRegion_HoweverWideItsFont(string characterName)
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero with { Name = characterName });
        var identity = document.BasicIdentity!;
        var name = BasicSections.FindText(document, ProfileElementRole.BasicName)!;

        Assert.Equal(characterName, name.Text);
        Assert.Equal(identity.RegionPosition, name.Position);
        Assert.Equal(identity.RegionWidth, name.Size.X, 3);

        // A quarter wider than the estimate (half the font size per character).
        var wide = characterName.Length * name.FontSize * 0.5f * 1.25f;
        Assert.Equal((name.FontSize, false), BasicNameFit.ForBox(name, wide));
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
        AssertOnTheNamesLine(harness.Document, expected);
        Assert.Equal("the Brave", Title(harness.Document).Text);

        harness.Session.Undo();
        Assert.Equal(before, harness.Json());
    }

    /// <summary>
    /// A Plate saved before this change has a retired layout even with no title (Subtitle was every
    /// new Plate's). Choosing a title puts it on the name's line, in the order the old layout kept,
    /// without Badge's or Accent's look, in the same undo step.
    /// </summary>
    [Theory]
    [InlineData(IdentityTitleLayout.Subtitle, IdentityTitleLayout.InlineAfter)]
    [InlineData(IdentityTitleLayout.Classic, IdentityTitleLayout.InlineBefore)]
    [InlineData(IdentityTitleLayout.Badge, IdentityTitleLayout.InlineAfter)]
    [InlineData(IdentityTitleLayout.Accent, IdentityTitleLayout.InlineAfter)]
    public async Task ChoosingATitle_OnAPlateSavedBefore_PutsItOnTheNamesLine(IdentityTitleLayout saved, IdentityTitleLayout expected)
    {
        using var harness = await BasicHarness.OpenDocumentAsync(SavedWithoutATitle(saved));
        harness.SimulateBasicFrame();
        var before = harness.Json();

        harness.Identity.SelectGameTitle(FakeTitles.Prefix);
        harness.SimulateBasicFrame();

        Assert.Equal(expected, harness.Document.BasicIdentity!.Layout);
        AssertOnTheNamesLine(harness.Document, expected);
        var title = Title(harness.Document);
        Assert.False(title.Bold);
        Assert.False(title.Italic);
        Assert.Equal(0f, title.LetterSpacing);

        harness.Session.Undo();
        Assert.Equal(before, harness.Json());
    }

    [Fact]
    public async Task TypingACustomTitle_OnAPlateSavedBefore_PutsItOnTheNamesLine()
    {
        using var harness = await BasicHarness.OpenDocumentAsync(SavedWithoutATitle(IdentityTitleLayout.Subtitle));
        harness.SimulateBasicFrame();
        var before = harness.Json();

        // Custom chosen, nothing typed yet: no title shows, and the layout is already the one-line one.
        harness.Identity.SetTitleSource(IdentityTitleSource.Custom);
        Assert.Equal(IdentityTitleLayout.InlineAfter, harness.Document.BasicIdentity!.Layout);

        harness.Identity.SetCustomTitle("the Brave");
        harness.Identity.Commit();
        harness.SimulateBasicFrame();
        AssertOnTheNamesLine(harness.Document, IdentityTitleLayout.InlineAfter);

        harness.Session.Undo();
        harness.Session.Undo();
        Assert.Equal(before, harness.Json());
    }

    [Theory]
    [InlineData(IdentityTitleLayout.Classic, IdentityTitleLayout.InlineBefore)]
    [InlineData(IdentityTitleLayout.Subtitle, IdentityTitleLayout.InlineAfter)]
    public async Task ATitleShownAboveOrBelow_KeepsItsPlaceWhenEdited_UntilItIsTurnedOffAndOn(IdentityTitleLayout layout, IdentityTitleLayout oneLine)
    {
        using var harness = await BasicHarness.OpenDocumentAsync(Stacked(layout));
        harness.SimulateBasicFrame();

        // A new text for a title already shown above or below the name: it stays there.
        harness.Identity.SetCustomTitle("the Bold");
        harness.Identity.Commit();
        harness.SimulateBasicFrame();

        Assert.Equal(layout, harness.Document.BasicIdentity!.Layout);
        var name = Name(harness.Document);
        var title = Title(harness.Document);
        Assert.Equal("the Bold", title.Text);
        if (layout == IdentityTitleLayout.Classic)
        {
            Assert.True(title.Position.Y + title.Size.Y <= name.Position.Y);
        }
        else
        {
            Assert.True(title.Position.Y >= name.Position.Y + name.Size.Y);
        }

        // Turned off and on again, it comes back on the name's line.
        harness.Identity.SetTitleSource(IdentityTitleSource.None);
        harness.Identity.SetTitleSource(IdentityTitleSource.Custom);
        harness.SimulateBasicFrame();

        Assert.Equal(oneLine, harness.Document.BasicIdentity!.Layout);
        AssertOnTheNamesLine(harness.Document, oneLine);
        Assert.Equal("the Bold", Title(harness.Document).Text);
    }

    private static TextProfileElement Name(ProfileDocument document) => BasicSections.FindText(document, ProfileElementRole.BasicName)!;

    private static TextProfileElement Title(ProfileDocument document) => BasicSections.FindText(document, ProfileElementRole.BasicTitle)!;

    /// <summary>The title shares the name's line, before or after it as <paramref name="layout"/> says.</summary>
    private static void AssertOnTheNamesLine(ProfileDocument document, IdentityTitleLayout layout)
    {
        var name = Name(document);
        var title = Title(document);
        var nameMiddle = name.Position.Y + (name.Size.Y / 2f);
        Assert.InRange(nameMiddle, title.Position.Y, title.Position.Y + title.Size.Y); // one line, not stacked

        if (layout == IdentityTitleLayout.InlineBefore)
        {
            Assert.True(title.Position.X + title.Size.X <= name.Position.X + 0.01f, "The title comes before the name.");
        }
        else
        {
            Assert.True(name.Position.X + name.Size.X <= title.Position.X + 0.01f, "The title comes after the name.");
        }
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

    /// <summary>A Classic Plate with no title, saved with <paramref name="layout"/>, as an earlier build saved it.</summary>
    private static ProfileDocument SavedWithoutATitle(IdentityTitleLayout layout)
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.BasicIdentity!.Layout = layout;
        document.BasicIdentity.LayoutStyle = null;
        IdentityHeaderRules.Place(document, _ => 100f);
        return document;
    }
}
