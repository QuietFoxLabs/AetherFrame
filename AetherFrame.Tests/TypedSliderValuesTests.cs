using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Packages;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// <see cref="TypedSliderValues"/>: a value typed into a style slider may go past the slider's
/// range, as it could in 0.1.5, up to what a Plate can hold and still export; it is always finite
/// and inside the package limits.
/// </summary>
public class TypedSliderValuesTests
{
    private const float Current = 42f;

    [Theory]
    [InlineData(20f, 20f)]
    [InlineData(150f, 150f)] // past the slider's 96, as 0.1.5 allowed
    [InlineData(1024f, 1024f)]
    [InlineData(5000f, PackagePolicy.MaxFontSize)]
    [InlineData(float.PositiveInfinity, PackagePolicy.MaxFontSize)] // a typed 1e39
    [InlineData(3f, 3f)] // below the slider's 6: the renderer draws it
    [InlineData(0f, TypedSliderValues.MinFontSize)]
    [InlineData(-5f, TypedSliderValues.MinFontSize)]
    [InlineData(float.NegativeInfinity, TypedSliderValues.MinFontSize)]
    [InlineData(float.NaN, Current)]
    public void FontSize_GoesPastTheSlider_UpToThePackageLimit(float typed, float expected)
    {
        Assert.Equal(expected, TypedSliderValues.FontSize(typed, Current));
        Assert.Equal(expected, TypedSliderValues.AutoFitMinimum(typed, Current));
    }

    [Theory]
    [InlineData(5f, 5f)]
    [InlineData(60f, 60f)] // letter spacing past the slider's 40
    [InlineData(-50f, -50f)] // past the slider's -10
    [InlineData(4.5f, 4.5f)] // line spacing past the slider's 3x
    [InlineData(0.2f, 0.2f)] // below the slider's 0.5x
    [InlineData(20_000f, PackagePolicy.MaxStyleMagnitude)]
    [InlineData(float.PositiveInfinity, PackagePolicy.MaxStyleMagnitude)]
    [InlineData(float.NegativeInfinity, -PackagePolicy.MaxStyleMagnitude)]
    [InlineData(float.NaN, Current)]
    public void Spacing_GoesPastTheSlider_UpToThePackageLimit(float typed, float expected)
    {
        Assert.Equal(expected, TypedSliderValues.Spacing(typed, Current));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(45f, 45f)]
    [InlineData(360f, 360f)] // the slider's own end is left as it is
    [InlineData(-90f, 270f)]
    [InlineData(450f, 90f)]
    [InlineData(720f, 0f)]
    [InlineData(-0.5f, 359.5f)]
    [InlineData(float.PositiveInfinity, Current)] // an infinite angle has no direction
    [InlineData(float.NegativeInfinity, Current)]
    [InlineData(float.NaN, Current)]
    public void Angle_WrapsATypedValueIntoTheSlider(float typed, float expected)
    {
        Assert.Equal(expected, TypedSliderValues.Angle(typed, Current), 3);
    }

    [Theory]
    [InlineData(1e30f)]
    [InlineData(-3.4e38f)]
    public void AnyFiniteTypedAngle_EndsInsideTheSlider(float typed)
    {
        Assert.InRange(TypedSliderValues.Angle(typed, Current), 0f, 360f);
    }

    [Fact]
    public void AWrappedAngle_DrawsInTheSameDirection_AsTheValueTyped()
    {
        foreach (var typed in new[] { -90f, 450f, 1000f, -1000f, 9999f })
        {
            var wrapped = TypedSliderValues.Angle(typed, Current);
            var typedRadians = typed * MathF.PI / 180f;
            var wrappedRadians = wrapped * MathF.PI / 180f;
            Assert.Equal(MathF.Cos(typedRadians), MathF.Cos(wrappedRadians), 3);
            Assert.Equal(MathF.Sin(typedRadians), MathF.Sin(wrappedRadians), 3);
        }
    }

    [Fact]
    public async Task EveryTypedExtreme_SavesExportsAndImports()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Typed extremes");
        var document = library.OpenDocumentForEditing(created.PlateId);
        var huge = new TextProfileElement { Text = "Huge", Position = new Vector2(10, 10), Size = new Vector2(400, 200), ZIndex = 1 };
        var tiny = new TextProfileElement { Text = "tiny", Position = new Vector2(10, 300), Size = new Vector2(100, 40), ZIndex = 2, AutoFitText = true };
        document.Elements.Add(huge);
        document.Elements.Add(tiny);

        // What each slider stores for the most extreme values a player can type.
        huge.FontSize = TypedSliderValues.FontSize(float.PositiveInfinity, huge.FontSize);
        huge.LetterSpacing = TypedSliderValues.Spacing(float.PositiveInfinity, huge.LetterSpacing);
        huge.LineSpacing = TypedSliderValues.Spacing(float.NegativeInfinity, huge.LineSpacing);
        tiny.FontSize = TypedSliderValues.FontSize(-1f, tiny.FontSize);
        tiny.AutoFitMinimumSize = TypedSliderValues.AutoFitMinimum(5000f, tiny.AutoFitMinimumSize);
        tiny.LetterSpacing = TypedSliderValues.Spacing(float.NegativeInfinity, tiny.LetterSpacing);
        document.Background!.Mode = ProfileBackgroundMode.TexturedFill;
        document.Background.Texture = ProfileBackgroundTexture.Honeycomb;
        document.Background.GradientAngle = TypedSliderValues.Angle(-12_345f, document.Background.GradientAngle);
        document.Background.TextureRotation = TypedSliderValues.Angle(99_999f, document.Background.TextureRotation);
        await library.SavePlateDocumentAsync(document);

        var path = fixture.Export(packages, created.PlateId, "typed-extremes.aetherframe");
        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport, string.Join("; ", staged.Diagnostics.Errors));
        var imported = await packages.ImportAsync(staged);
        Assert.True(imported.Succeeded, imported.Error?.Message);

        var back = library.OpenDocumentForEditing(imported.PlateId);
        var texts = back.Elements.OfType<TextProfileElement>().ToDictionary(t => t.Text);
        Assert.Equal(PackagePolicy.MaxFontSize, texts["Huge"].FontSize);
        Assert.Equal(PackagePolicy.MaxStyleMagnitude, texts["Huge"].LetterSpacing);
        Assert.Equal(-PackagePolicy.MaxStyleMagnitude, texts["Huge"].LineSpacing);
        Assert.Equal(TypedSliderValues.MinFontSize, texts["tiny"].FontSize);
        Assert.Equal(PackagePolicy.MaxFontSize, texts["tiny"].AutoFitMinimumSize);
        Assert.InRange(back.Background!.GradientAngle, 0f, 360f);
        Assert.InRange(back.Background.TextureRotation, 0f, 360f);
    }
}
