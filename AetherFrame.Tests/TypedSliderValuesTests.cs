using System;
using System.Collections.Generic;
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

    // ---------------------------------------------------------------- a typed angle, frame by frame

    /// <summary>
    /// What Dalamud's ImGui (1.88) hands a 0-360 "%.0f deg" SliderFloat on each frame of a
    /// Ctrl+Click entry, recorded by driving the real slider headlessly: the text is applied on
    /// every keystroke (a prefix that doesn't parse changes nothing), and once more by Enter. So a
    /// typed 1e39 passes 1 and 1e3 = 1000 (which wraps to 280) before its last character
    /// overflows to +Infinity.
    /// </summary>
    private static readonly Dictionary<string, float[]> TypedFrames = new()
    {
        ["1e39"] = [1f, 1000f, float.PositiveInfinity, float.PositiveInfinity],
        ["-1e39"] = [-1f, -1000f, float.NegativeInfinity, float.NegativeInfinity],
        ["5000"] = [5f, 50f, 500f, 5000f, 5000f],
        ["-90"] = [-9f, -90f, -90f],
        ["100"] = [1f, 10f, 100f, 100f],
    };

    private static (Func<ProfileBackground, float> Read, Action<ProfileBackground, float> Write) AngleOf(string control) => control switch
    {
        "Gradient Angle" => (b => b.GradientAngle, (b, v) => b.GradientAngle = v),
        "Texture Rotation" => (b => b.TextureRotation, (b, v) => b.TextureRotation = v),
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    private static void Type(EditorSession session, string control, string typed)
    {
        var (read, write) = AngleOf(control);
        foreach (var frame in TypedFrames[typed])
        {
            session.ContinueBackgroundAngleEdit(frame, read, write);
        }

        session.CommitPendingBackgroundEdit(); // the slider deactivates after the edit
    }

    /// <summary>
    /// Step 13 of the 0.1.6 acceptance: after -90 (270 degrees), a typed 1e39 left the angle at 280,
    /// the wrap of the 1e3 its typing passed through. An angle that overflows changes nothing: it
    /// is what it was before the typed edit began, whatever the earlier keystrokes showed.
    /// </summary>
    [Theory]
    [InlineData("Gradient Angle", "1e39", 270f)]
    [InlineData("Gradient Angle", "-1e39", 270f)]
    [InlineData("Gradient Angle", "5000", 320f)]
    [InlineData("Gradient Angle", "-90", 270f)]
    [InlineData("Texture Rotation", "1e39", 270f)]
    [InlineData("Texture Rotation", "-1e39", 270f)]
    [InlineData("Texture Rotation", "5000", 320f)]
    [InlineData("Texture Rotation", "-90", 270f)]
    public async Task ATypedAngle_AsImGuiAppliesItPerKeystroke_EndsAtItsText_OrUnchangedWhenItOverflows(string control, string typed, float expected)
    {
        using var harness = await BasicHarness.OpenDocumentAsync(BasicDocuments.Blank());
        var session = harness.Session;
        var (read, _) = AngleOf(control);
        Type(session, control, "-90");
        Assert.Equal(270f, read(harness.Document.Background!));

        Type(session, control, typed);

        Assert.Equal(expected, read(harness.Document.Background!));
        Assert.Null(session.ErrorMessage);
    }

    [Theory]
    [InlineData("Gradient Angle")]
    [InlineData("Texture Rotation")]
    public async Task AnOverflowingTypedAngle_LeavesNoHistoryEntry_AndEachEditKeepsItsOwnStartingAngle(string control)
    {
        using var harness = await BasicHarness.OpenDocumentAsync(BasicDocuments.Blank());
        var session = harness.Session;
        var (read, _) = AngleOf(control);
        Type(session, control, "100");
        Assert.True(session.CanUndo);
        Assert.True(session.IsDirty);

        // A second edit starts from 100, not from anything the first one passed through.
        Type(session, control, "1e39");
        Assert.Equal(100f, read(harness.Document.Background!));

        // It changed nothing, so the only history entry is still the first edit.
        session.Undo();
        Assert.False(session.CanUndo);
        Assert.Equal(read(BasicDocuments.Blank().Background!), read(harness.Document.Background!));
    }

    [Fact]
    public async Task AnOverflowingAngle_ArrivingInOneFrame_AsWhenPasted_ChangesNothing()
    {
        using var harness = await BasicHarness.OpenDocumentAsync(BasicDocuments.Blank());
        var session = harness.Session;
        var before = harness.Document.Background!.GradientAngle;

        session.ContinueBackgroundAngleEdit(float.PositiveInfinity, b => b.GradientAngle, (b, v) => b.GradientAngle = v);
        session.CommitPendingBackgroundEdit();

        Assert.Equal(before, harness.Document.Background.GradientAngle);
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
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
