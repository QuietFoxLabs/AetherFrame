using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Components;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// Keeps every numeric value of a Plate's elements and background a number. JSON can't express
/// NaN or infinity, so a Plate holding one could never be saved again; one gets in when a typed
/// value overflows (1e39 in a slider's text entry, or in a hand-edited file, becomes infinity) or
/// through arithmetic on it. Bounding replaces such a value with its property's default and leaves
/// every finite value exactly as it is: any finite value a saved Plate can hold is one the editor
/// or an imported package could legitimately have written, so bounding never rewrites a Plate that
/// was fine. The same rule runs on every load (<see cref="ProfileDocument.NormalizeValues"/>) and
/// after every edit (<c>ProfileService.UpdateElement</c> and <c>UpdateBackground</c>).
/// </summary>
public static class ProfileElementLimits
{
    // The property defaults, read from fresh instances so they can never drift from the
    // initializers. The base-element defaults (position, size) are the same for every element type.
    private static readonly TextProfileElement TextDefaults = new();
    private static readonly ImageProfileElement ImageDefaults = new();
    private static readonly ProfileBackground BackgroundDefaults = new();

    private static readonly FloatSlot<ProfileElement>[] ElementSlots =
    [
        ..Vector2Slots<ProfileElement>(nameof(ProfileElement.Position), e => e.Position, (e, v) => e.Position = v),
        ..Vector2Slots<ProfileElement>(nameof(ProfileElement.Size), e => e.Size, (e, v) => e.Size = v),
    ];

    private static readonly FloatSlot<TextProfileElement>[] TextSlots =
    [
        new(nameof(TextProfileElement.FontSize), e => e.FontSize, (e, v) => e.FontSize = v),
        ..Vector4Slots<TextProfileElement>(nameof(TextProfileElement.Color), e => e.Color, (e, v) => e.Color = v),
        new(nameof(TextProfileElement.LetterSpacing), e => e.LetterSpacing, (e, v) => e.LetterSpacing = v),
        new(nameof(TextProfileElement.VerticalOffset), e => e.VerticalOffset, (e, v) => e.VerticalOffset = v),
        new(nameof(TextProfileElement.LineSpacing), e => e.LineSpacing, (e, v) => e.LineSpacing = v),
        new(nameof(TextProfileElement.AutoFitMinimumSize), e => e.AutoFitMinimumSize, (e, v) => e.AutoFitMinimumSize = v),
        ..Vector4Slots<TextProfileElement>(nameof(TextProfileElement.OutlineColor), e => e.OutlineColor, (e, v) => e.OutlineColor = v),
        new(nameof(TextProfileElement.OutlineThickness), e => e.OutlineThickness, (e, v) => e.OutlineThickness = v),
        new(nameof(TextProfileElement.OutlineOpacity), e => e.OutlineOpacity, (e, v) => e.OutlineOpacity = v),
        ..Vector4Slots<TextProfileElement>(nameof(TextProfileElement.ShadowColor), e => e.ShadowColor, (e, v) => e.ShadowColor = v),
        new(nameof(TextProfileElement.ShadowOpacity), e => e.ShadowOpacity, (e, v) => e.ShadowOpacity = v),
        new(nameof(TextProfileElement.ShadowOffsetX), e => e.ShadowOffsetX, (e, v) => e.ShadowOffsetX = v),
        new(nameof(TextProfileElement.ShadowOffsetY), e => e.ShadowOffsetY, (e, v) => e.ShadowOffsetY = v),
    ];

    private static readonly FloatSlot<ImageProfileElement>[] ImageSlots =
    [
        new(nameof(ImageProfileElement.Opacity), e => e.Opacity, (e, v) => e.Opacity = v),
        new(nameof(ImageProfileElement.RotationDegrees), e => e.RotationDegrees, (e, v) => e.RotationDegrees = v),
    ];

    private static readonly FloatSlot<ProfileBackground>[] BackgroundSlots =
    [
        ..Vector4Slots<ProfileBackground>(nameof(ProfileBackground.PrimaryColor), b => b.PrimaryColor, (b, v) => b.PrimaryColor = v),
        ..Vector4Slots<ProfileBackground>(nameof(ProfileBackground.SecondaryColor), b => b.SecondaryColor, (b, v) => b.SecondaryColor = v),
        new(nameof(ProfileBackground.GradientAngle), b => b.GradientAngle, (b, v) => b.GradientAngle = v),
        new(nameof(ProfileBackground.Opacity), b => b.Opacity, (b, v) => b.Opacity = v),
        new(nameof(ProfileBackground.TextureIntensity), b => b.TextureIntensity, (b, v) => b.TextureIntensity = v),
        new(nameof(ProfileBackground.TextureScale), b => b.TextureScale, (b, v) => b.TextureScale = v),
        new(nameof(ProfileBackground.TextureRotation), b => b.TextureRotation, (b, v) => b.TextureRotation = v),
    ];

    /// <summary>
    /// Replaces every value of <paramref name="element"/> that isn't a number with its default
    /// (see the class summary). Finite values are never changed.
    /// </summary>
    /// <returns>True if anything was replaced.</returns>
    public static bool Bound(ProfileElement element)
    {
        var repaired = Bound(element, ElementSlots, TextDefaults);
        if (element is TextProfileElement text)
        {
            repaired |= Bound(text, TextSlots, TextDefaults);
        }
        else if (element is ImageProfileElement image)
        {
            repaired |= Bound(image, ImageSlots, ImageDefaults);
        }

        return repaired;
    }

    /// <summary>The background half of <see cref="Bound(ProfileElement)"/>; see <see cref="ProfileBackground.Bound"/>.</summary>
    internal static bool Bound(ProfileBackground background) => Bound(background, BackgroundSlots, BackgroundDefaults);

    /// <summary>
    /// A Component holding a value that isn't a number gets the editor's own bounds
    /// (<see cref="PlateComponentEditor.Bound"/>), exactly as an edit would apply them; one whose
    /// values are all numbers is left untouched, even out of range, for the reason in the class summary.
    /// </summary>
    /// <returns>True if the Component was bounded.</returns>
    internal static bool Bound(PlateComponent component)
    {
        if (FirstNonFinite(component) is null)
        {
            return false;
        }

        PlateComponentEditor.Bound(component);
        return true;
    }

    /// <summary>
    /// Where a document still holds a value that isn't a number, described for the player (e.g.
    /// "the "Title" element's FontSize"), or null when every value is a number. The save path's
    /// last resort (see <c>PlateDocuments.ToJson</c>); never a path or other local detail.
    /// </summary>
    internal static string? DescribeNonFiniteValue(ProfileDocument document)
    {
        if (!float.IsFinite(document.CanvasWidth) || !float.IsFinite(document.CanvasHeight))
        {
            return "the canvas size";
        }

        foreach (var element in document.Elements)
        {
            if (FirstNonFinite(element) is { } property)
            {
                return $"the \"{ProfileElementNames.GetDisplayName(element)}\" element's {property}";
            }
        }

        if (document.Background is { } background && FirstNonFinite(background, BackgroundSlots) is { } backgroundProperty)
        {
            return $"the background's {backgroundProperty}";
        }

        foreach (var component in document.Components ?? [])
        {
            if (FirstNonFinite(component) is { } property)
            {
                return $"a Component's {property}";
            }
        }

        if (document.BasicIdentity?.Clone() is { } identity && identity.NormalizeValues())
        {
            return "the Identity Header's layout";
        }

        if (document.BasicPlate?.Clone() is { } plate && plate.NormalizeValues())
        {
            return "a Basic section's placement";
        }

        return null;
    }

    internal static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    internal static bool IsFinite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    internal static bool IsFinite(ElementRect rect) => IsFinite(rect.Position) && IsFinite(rect.Size);

    private static string? FirstNonFinite(ProfileElement element) =>
        FirstNonFinite(element, ElementSlots) ?? element switch
        {
            TextProfileElement text => FirstNonFinite(text, TextSlots),
            ImageProfileElement image => FirstNonFinite(image, ImageSlots),
            _ => null,
        };

    private static string? FirstNonFinite(PlateComponent component)
    {
        if (!float.IsFinite(component.Opacity))
        {
            return nameof(PlateComponent.Opacity);
        }

        if (!float.IsFinite(component.Scale))
        {
            return nameof(PlateComponent.Scale);
        }

        if (!float.IsFinite(component.RotationDegrees))
        {
            return nameof(PlateComponent.RotationDegrees);
        }

        if (!IsFinite(component.Offset))
        {
            return nameof(PlateComponent.Offset);
        }

        if (component.Color is { } color && !IsFinite(color))
        {
            return nameof(PlateComponent.Color);
        }

        if (component.FixedAnchorPosition is { } anchorPosition && !IsFinite(anchorPosition))
        {
            return nameof(PlateComponent.FixedAnchorPosition);
        }

        if (component.FixedAnchorSize is { } anchorSize && !IsFinite(anchorSize))
        {
            return nameof(PlateComponent.FixedAnchorSize);
        }

        return null;
    }

    private static bool Bound<T>(T target, FloatSlot<T>[] slots, T defaults)
    {
        var repaired = false;
        foreach (var slot in slots)
        {
            if (!float.IsFinite(slot.Get(target)))
            {
                slot.Set(target, slot.Get(defaults));
                repaired = true;
            }
        }

        return repaired;
    }

    private static string? FirstNonFinite<T>(T target, FloatSlot<T>[] slots)
    {
        foreach (var slot in slots)
        {
            if (!float.IsFinite(slot.Get(target)))
            {
                return slot.Name;
            }
        }

        return null;
    }

    private static IEnumerable<FloatSlot<T>> Vector2Slots<T>(string name, Func<T, Vector2> get, Action<T, Vector2> set)
    {
        yield return new(name + ".X", t => get(t).X, (t, v) => set(t, new Vector2(v, get(t).Y)));
        yield return new(name + ".Y", t => get(t).Y, (t, v) => set(t, new Vector2(get(t).X, v)));
    }

    private static IEnumerable<FloatSlot<T>> Vector4Slots<T>(string name, Func<T, Vector4> get, Action<T, Vector4> set)
    {
        for (var index = 0; index < 4; index++)
        {
            var component = index;
            yield return new(name + "." + "XYZW"[component], t => get(t)[component], (t, v) =>
            {
                var vector = get(t);
                vector[component] = v;
                set(t, vector);
            });
        }
    }

    /// <summary>One float property, or one component of a vector property, of a <typeparamref name="T"/>:
    /// its name for messages, and how to read and write it.</summary>
    private sealed record FloatSlot<T>(string Name, Func<T, float> Get, Action<T, float> Set);
}
