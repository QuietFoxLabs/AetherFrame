using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Values that aren't numbers (NaN, infinity) can't be written as JSON, so a Plate holding one
/// could never be saved. They are replaced by their defaults on load and after every edit, finite
/// values are never touched, and the save path names the culprit if one still slips through.
/// </summary>
public class NonFiniteValueTests
{
    public static TheoryData<float> NotANumber => new() { float.NaN, float.PositiveInfinity, float.NegativeInfinity };

    private static TextProfileElement WildText(float wild) => new()
    {
        Position = new Vector2(wild, wild),
        Size = new Vector2(wild, wild),
        FontSize = wild,
        Color = new Vector4(wild, wild, wild, wild),
        LetterSpacing = wild,
        LineSpacing = wild,
        AutoFitMinimumSize = wild,
        OutlineColor = new Vector4(wild, wild, wild, wild),
        OutlineThickness = wild,
        OutlineOpacity = wild,
        ShadowColor = new Vector4(wild, wild, wild, wild),
        ShadowOpacity = wild,
        ShadowOffsetX = wild,
        ShadowOffsetY = wild,
    };

    private static ProfileBackground WildBackground(float wild) => new()
    {
        PrimaryColor = new Vector4(wild, wild, wild, wild),
        SecondaryColor = new Vector4(wild, wild, wild, wild),
        GradientAngle = wild,
        Opacity = wild,
        TextureIntensity = wild,
        TextureScale = wild,
        TextureRotation = wild,
    };

    // ---------------------------------------------------------------- Bound

    [Theory]
    [MemberData(nameof(NotANumber))]
    public void Bound_ReplacesEveryTextValueThatIsNotANumber_WithItsDefault(float wild)
    {
        var element = WildText(wild);

        Assert.True(ProfileElementLimits.Bound(element));

        Assert.True(element.ContentEquals(new TextProfileElement { Id = element.Id }));
        Assert.False(ProfileElementLimits.Bound(element));
    }

    [Theory]
    [MemberData(nameof(NotANumber))]
    public void Bound_ReplacesEveryImageValueThatIsNotANumber_WithItsDefault(float wild)
    {
        var element = new ImageProfileElement { Position = new Vector2(wild, wild), Size = new Vector2(wild, wild), Opacity = wild, RotationDegrees = wild };

        Assert.True(ProfileElementLimits.Bound(element));

        Assert.True(element.ContentEquals(new ImageProfileElement { Id = element.Id }));
    }

    [Theory]
    [MemberData(nameof(NotANumber))]
    public void Bound_ReplacesEveryBackgroundValueThatIsNotANumber_WithItsDefault(float wild)
    {
        var background = WildBackground(wild);

        Assert.True(background.Bound());

        Assert.True(background.ContentEquals(new ProfileBackground()));
        Assert.False(background.Bound());
    }

    [Fact]
    public void Bound_ReplacesOnlyTheComponentThatIsNotANumber()
    {
        var element = new TextProfileElement { Color = new Vector4(0.2f, 0.3f, 0.4f, float.NaN), Position = new Vector2(float.PositiveInfinity, 77f) };

        Assert.True(ProfileElementLimits.Bound(element));

        Assert.Equal(new Vector4(0.2f, 0.3f, 0.4f, 1f), element.Color);
        Assert.Equal(new Vector2(ProfileElement.DefaultPositionX, 77f), element.Position);
    }

    [Fact]
    public void Bound_LeavesFiniteValuesUntouched_EvenOutOfRange()
    {
        // Finite values outside the sliders' ranges are what an imported package (which allows
        // them) or a typed value produces; they are the user's, and are never rewritten.
        var text = new TextProfileElement { FontSize = 5000f, LetterSpacing = -1e6f, Color = new Vector4(2f, 2f, 2f, 2f), Position = new Vector2(-1e5f, 1e5f) };
        var image = new ImageProfileElement { Opacity = 7f, RotationDegrees = 1e4f };
        var background = new ProfileBackground { TextureScale = 1e6f, GradientAngle = -720f, Opacity = 3f };
        var expectedText = text.Clone();
        var expectedImage = image.Clone();
        var expectedBackground = background.Clone();

        Assert.False(ProfileElementLimits.Bound(text));
        Assert.False(ProfileElementLimits.Bound(image));
        Assert.False(background.Bound());

        Assert.True(text.ContentEquals(expectedText));
        Assert.True(image.ContentEquals(expectedImage));
        Assert.True(background.ContentEquals(expectedBackground));
    }

    [Fact]
    public void NormalizeValues_LeavesARichDocumentByteForByteUnchanged()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine)];
        var before = PlateDocuments.ToJson(document).ToJsonString();

        Assert.False(document.NormalizeValues());
        Assert.All(document.Elements, e => Assert.False(ProfileElementLimits.Bound(e)));

        Assert.Equal(before, PlateDocuments.ToJson(document).ToJsonString());
    }

    [Fact]
    public void NormalizeValues_RepairsEveryPartOfTheDocument()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.CanvasWidth = float.PositiveInfinity;
        var text = document.Elements.OfType<TextProfileElement>().First();
        text.Position = new Vector2(float.PositiveInfinity, 44f);
        text.FontSize = float.NaN;
        document.Background!.Opacity = float.PositiveInfinity;
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine);
        component.Opacity = float.PositiveInfinity;
        component.Scale = 99f;
        var untouched = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine);
        untouched.Scale = 99f;
        document.Components = [component, untouched];
        document.BasicIdentity = new BasicIdentityHeader
        {
            RegionWidth = float.NegativeInfinity,
            AppliedLayout = new IdentityLayoutSnapshot { Name = new ElementRect(new Vector2(float.NaN, 0f), Vector2.One), Title = new ElementRect(Vector2.Zero, Vector2.One) },
            LayoutStyle = new IdentityLayoutStyle { Applied = new TitleStyleValues { FontSize = float.PositiveInfinity, LetterSpacing = 1f } },
        };
        document.BasicPlate!.SetPlacement(ProfileElementRole.BasicMessage, new ElementRect(Vector2.Zero, new Vector2(float.PositiveInfinity, 10f)));
        var placements = document.BasicPlate.Placements.Count(p => p.Role != ProfileElementRole.BasicMessage);

        Assert.True(document.NormalizeValues());

        Assert.Equal(ProfileDocument.LegacyCanvasWidth, document.CanvasWidth);
        Assert.Equal(ProfileDocument.LegacyCanvasHeight, document.CanvasHeight);
        Assert.Equal(new Vector2(ProfileElement.DefaultPositionX, 44f), text.Position);
        Assert.Equal(16f, text.FontSize);
        Assert.Equal(1f, document.Background.Opacity);
        Assert.Equal(1f, component.Opacity);
        Assert.Equal(PlateComponentLimits.MaxScale, component.Scale);
        Assert.Equal(99f, untouched.Scale);
        Assert.Equal(0f, document.BasicIdentity.RegionWidth);
        Assert.Null(document.BasicIdentity.AppliedLayout!.Name);
        Assert.NotNull(document.BasicIdentity.AppliedLayout.Title);
        Assert.Null(document.BasicIdentity.LayoutStyle!.Applied.FontSize);
        Assert.Equal(1f, document.BasicIdentity.LayoutStyle.Applied.LetterSpacing);
        Assert.Equal(placements, document.BasicPlate.Placements.Count);
        Assert.Null(document.BasicPlate.GetPlacement(ProfileElementRole.BasicMessage));
        Assert.False(document.NormalizeValues());
        Assert.Null(ProfileElementLimits.DescribeNonFiniteValue(document));
        _ = PlateDocuments.ToJson(document);
    }

    // ---------------------------------------------------------------- local files

    /// <summary>A Plate file with numbers beyond float's range, which read back as infinity.</summary>
    private static (Guid PlateId, string Json, Guid TextId) OverflowingPlate(DateTime now)
    {
        var plateId = Guid.NewGuid();
        var document = PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Overflow", now);
        var text = new TextProfileElement { Text = "hi", Position = new Vector2(10f, 20f), Size = new Vector2(300f, 60f) };
        document.Elements.Add(text);
        document.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), Position = new Vector2(50f, 50f), Size = new Vector2(100f, 100f), ZIndex = 1 });
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine)];

        var json = JsonNode.Parse(JsonSerializer.Serialize(document, JsonOptions.Default))!.AsObject();
        json["CanvasWidth"] = 1e39;
        json["Elements"]![0]!["Position"]!["X"] = 1e39;
        json["Elements"]![0]!["FontSize"] = 1e39;
        json["Elements"]![0]!["Color"]!["W"] = -1e39;
        json["Background"]!["Opacity"] = 1e39;
        json["Components"]![0]!["Opacity"] = 1e39;
        return (plateId, json.ToJsonString(JsonOptions.Default), text.Id);
    }

    private static void AssertEveryNumberIsFinite(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    AssertEveryNumberIsFinite(child);
                }

                break;

            case JsonArray array:
                foreach (var child in array)
                {
                    AssertEveryNumberIsFinite(child);
                }

                break;

            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                Assert.True(value.TryGetValue<double>(out var number) && double.IsFinite(number) && Math.Abs(number) < 1e30, "a saved number is not a usable float");
                break;
        }
    }

    [Fact]
    public async Task LocalPlate_WithOverflowingNumbers_LoadsRepaired_OpensClean_AndSaves()
    {
        using var fixture = new LibraryFixture();
        var (plateId, json, textId) = OverflowingPlate(fixture.Clock.Now);
        fixture.WritePlateJson(plateId, json);

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        Assert.Equal(json, fixture.ReadPlateJson(plateId));
        var document = library.OpenDocumentForEditing(plateId);
        Assert.Equal(ProfileDocument.LegacyCanvasWidth, document.CanvasWidth);
        var text = Assert.IsType<TextProfileElement>(document.Elements.Single(e => e.Id == textId));
        Assert.Equal(new Vector2(ProfileElement.DefaultPositionX, 20f), text.Position);
        Assert.Equal(16f, text.FontSize);
        Assert.Equal(1f, text.Color.W);
        Assert.Equal(1f, document.Background!.Opacity);
        Assert.Equal(1f, document.Components!.Single().Opacity);

        using var harness = await BasicHarness.OpenJsonAsync(json, plateId);
        Assert.False(harness.Session.IsDirty);
        Assert.True(await harness.Session.SaveProfileAsync(), harness.Session.ErrorMessage);
        AssertEveryNumberIsFinite(JsonNode.Parse(harness.Fixture.ReadPlateJson(plateId)));
    }

    [Fact]
    public void Materialize_ReportsTheRepair_OnlyWhenOneWasNeeded()
    {
        var (_, json, _) = OverflowingPlate(DateTime.UtcNow);

        _ = PlateDocuments.Materialize(JsonNode.Parse(json)!.AsObject(), out var repaired);
        _ = PlateDocuments.Materialize(JsonNode.Parse(JsonSerializer.Serialize(BasicDocuments.Blank(), JsonOptions.Default))!.AsObject(), out var clean);

        Assert.True(repaired);
        Assert.False(clean);
    }

    // ---------------------------------------------------------------- the save backstop

    [Fact]
    public void ToJson_NamesTheElementAndPropertyThatIsNotANumber()
    {
        var document = BasicDocuments.Blank();
        document.Elements.Add(new TextProfileElement { Name = "Caption", FontSize = float.NaN });

        var ex = Assert.Throws<InvalidOperationException>(() => PlateDocuments.ToJson(document));

        Assert.Contains("\"Caption\"", ex.Message);
        Assert.Contains("FontSize", ex.Message);
        Assert.Equal(ex.Message, UserFacingError.Describe(ex, "fallback"));
        Assert.False(UserFacingError.ContainsPath(ex.Message));
    }

    [Fact]
    public void ToJson_NamesTheBackgroundOrCanvas_WhenTheyAreNotNumbers()
    {
        var background = BasicDocuments.Blank();
        background.Background!.GradientAngle = float.PositiveInfinity;
        var canvas = BasicDocuments.Blank();
        canvas.CanvasHeight = float.NegativeInfinity;

        Assert.Contains("background's GradientAngle", Assert.Throws<InvalidOperationException>(() => PlateDocuments.ToJson(background)).Message);
        Assert.Contains("canvas size", Assert.Throws<InvalidOperationException>(() => PlateDocuments.ToJson(canvas)).Message);
    }

    [Fact]
    public void ToJson_OfARepairedDocument_DoesNotThrow()
    {
        var document = BasicDocuments.Blank();
        document.Elements.Add(WildText(float.PositiveInfinity));
        document.Background = WildBackground(float.NaN);

        Assert.True(document.NormalizeValues());

        AssertEveryNumberIsFinite(PlateDocuments.ToJson(document));
    }

    // ---------------------------------------------------------------- the edit choke points

    [Fact]
    public async Task EditorEdits_ThatLeaveAValueThatIsNotANumber_AreBounded_AndTheSaveSucceeds()
    {
        using var harness = await BasicHarness.OpenDocumentAsync(BasicDocuments.Blank());
        var session = harness.Session;
        var textId = session.AddTextElement()!.Value;
        var imageId = harness.Profiles.AddElement(new ImageProfileElement { AssetId = Guid.NewGuid() });
        var text = (TextProfileElement)harness.Document.Elements.Single(e => e.Id == textId);
        var image = (ImageProfileElement)harness.Document.Elements.Single(e => e.Id == imageId);
        text.Color = new Vector4(0.5f, 0.5f, 0.5f, 0.5f);

        session.BeginOrContinueEdit(textId, e => ((TextProfileElement)e).FontSize = float.PositiveInfinity);
        session.CommitPendingEdit();
        session.BeginOrContinueEdit(textId, e => ((TextProfileElement)e).LineSpacing = float.NaN);
        session.CommitPendingEdit();
        session.BeginOrContinueEdit(textId, e => ((TextProfileElement)e).Color = new Vector4(0.5f, 0.5f, 0.5f, float.PositiveInfinity));
        session.CommitPendingEdit();
        session.BeginOrContinueEdit(imageId, e => ((ImageProfileElement)e).RotationDegrees = float.PositiveInfinity);
        session.CommitPendingEdit();
        session.BeginOrContinueBackgroundEdit(style => style.GradientAngle = float.PositiveInfinity);
        session.CommitPendingBackgroundEdit();

        Assert.Null(session.ErrorMessage);
        Assert.Equal(16f, text.FontSize);
        Assert.Equal(1f, text.LineSpacing);
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 1f), text.Color);
        Assert.Equal(0f, image.RotationDegrees);
        Assert.Equal(90f, harness.Document.Background!.GradientAngle);
        Assert.True(await session.SaveProfileAsync(), session.ErrorMessage);
        AssertEveryNumberIsFinite(JsonNode.Parse(harness.Fixture.ReadPlateJson(harness.PlateId)));
    }

    [Fact]
    public async Task UpdateElementAndBackground_BoundAfterTheEdit_AndUndoRestoresTheFiniteValue()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var profiles = new ProfileService(library);
        profiles.OpenPlate(plate.PlateId);
        var id = profiles.AddTextElement("x");
        profiles.UpdateElement(id, e => ((TextProfileElement)e).FontSize = 5000f);
        var before = profiles.CloneElement(id);

        profiles.UpdateElement(id, e => ((TextProfileElement)e).FontSize = float.NaN);
        profiles.UpdateBackground(b => b.Opacity = float.NegativeInfinity);

        Assert.Equal(16f, ((TextProfileElement)profiles.CurrentProfile!.Elements[0]).FontSize);
        Assert.Equal(1f, profiles.CurrentProfile.Background!.Opacity);

        // The undo path (CopyFrom of a snapshot) leaves the snapshot's finite, out-of-range value exactly as it was.
        profiles.UpdateElement(id, e => e.CopyFrom(before));
        Assert.Equal(5000f, ((TextProfileElement)profiles.CurrentProfile.Elements[0]).FontSize);
    }
}
