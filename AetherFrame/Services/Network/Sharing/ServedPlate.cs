using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Fonts;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>What one step of a served Plate draws: the renderer's own element, or a shape.</summary>
internal abstract record ServedStep;

/// <summary>A text, as the renderer's own element, drawn with its carried display text. <see cref="FontKnown"/> is false for a font this build doesn't bundle, which is drawn as a placeholder.</summary>
internal sealed record ServedText(TextProfileElement Element, string Text, bool FontKnown) : ServedStep;

/// <summary>An image, as the renderer's own element, drawn from the served image at <see cref="Index"/>.</summary>
internal sealed record ServedImageStep(ImageProfileElement Element, int Index) : ServedStep;

/// <summary>What a shape is: a filled quad or triangle, an image quad, or an art quad.</summary>
internal enum ServedShapeKind
{
    Quad,
    Triangle,
    Image,
    Art,
}

/// <summary>
/// A shape in canvas units, clockwise from the top left (a triangle leaves <see cref="D"/> unused),
/// in its colour or tint: an image quad names its served image's <see cref="Index"/>, and an art
/// quad its artwork, which is null for an ident this build doesn't bundle.
/// </summary>
internal sealed record ServedShape(ServedShapeKind Kind, Vector2 A, Vector2 B, Vector2 C, Vector2 D, Vector4 Color, int Index = -1, BuiltInArtAsset? Art = null) : ServedStep;

/// <summary>
/// A served profile (D6; the specification's section 8.6), turned into what AetherFrame's renderer
/// draws (section 8.5's "Drawing"): the background as the renderer's own background, each text and
/// image as the renderer's own element, and each shape in canvas units. Every value is the carried
/// fixed-point value turned back into the renderer's units, so a Plate draws here as it draws for
/// the player who shared it, and nothing in it is ever saved: it touches no Library, Template or
/// binding. A font or an artwork is looked up by exact ident among this build's own, never
/// fetched; one it doesn't bundle is named in <see cref="Notes"/>. Compiled only in the networking
/// preview flavour, and free of Dalamud and ImGui.
/// </summary>
internal sealed class ServedPlate
{
    private ServedPlate(string name, Vector2 canvas, ProfileBackground? background, int backgroundIndex, IReadOnlyList<ServedStep> steps, IReadOnlyList<string> notes)
    {
        Name = name;
        Canvas = canvas;
        Background = background;
        BackgroundIndex = backgroundIndex;
        Steps = steps;
        Notes = notes;

        // The canvas united with every shape: shapes are what Components paint, which may overflow
        // the canvas on purpose, so this is the publisher's own visual bounds (ProfileVisualBounds).
        var min = Vector2.Zero;
        var max = canvas;
        foreach (var step in steps)
        {
            if (step is ServedShape shape)
            {
                foreach (var point in shape.Kind == ServedShapeKind.Triangle ? new[] { shape.A, shape.B, shape.C } : new[] { shape.A, shape.B, shape.C, shape.D })
                {
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
            }
        }

        VisualMin = min;
        VisualMax = max;
        var texts = new List<ProfileElement>();
        foreach (var step in steps)
        {
            if (step is ServedText { FontKnown: true } text)
            {
                texts.Add(text.Element);
            }
        }

        FontWarmup = new ProfileDocument { Elements = texts };
    }

    /// <summary>A document holding only the texts, for nothing but warming their fonts up before they are first drawn: never saved or shown.</summary>
    internal ProfileDocument FontWarmup { get; }

    /// <summary>The Plate's name, as the player who shared it named it: plain text (N7).</summary>
    internal string Name { get; }

    /// <summary>The canvas, in canvas units.</summary>
    internal Vector2 Canvas { get; }

    /// <summary>The top left of what it shows: the canvas and everything its Components paint outside it.</summary>
    internal Vector2 VisualMin { get; }

    /// <summary>The bottom right of what it shows.</summary>
    internal Vector2 VisualMax { get; }

    /// <summary>The background, or null when it draws nothing.</summary>
    internal ProfileBackground? Background { get; }

    /// <summary>The served image the background draws, or -1.</summary>
    internal int BackgroundIndex { get; }

    internal IReadOnlyList<ServedStep> Steps { get; }

    /// <summary>What this build can't draw as shared: each font or artwork it doesn't bundle, once.</summary>
    internal IReadOnlyList<string> Notes { get; }

    /// <summary>The key the renderer's own elements name the served image at <paramref name="index"/> by: never a real asset id.</summary>
    internal static Guid ImageKey(int index) => new(index + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>The served image index <paramref name="key"/> stands for, or -1.</summary>
    internal static int IndexOf(Guid key)
    {
        for (var index = 0; index < Protocol.ProtocolLimits.MaxImagesPerProfile; index++)
        {
            if (ImageKey(index) == key)
            {
                return index;
            }
        }

        return -1;
    }

    internal static ServedPlate From(ServedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var notes = new List<string>();
        var (background, backgroundIndex) = BackgroundOf(profile);
        var steps = new List<ServedStep>(profile.Items.Count);
        foreach (var item in profile.Items)
        {
            switch (item)
            {
                case LayoutText text:
                    var known = IsBundledFont(text.Font);
                    if (!known)
                    {
                        Note(notes, "A text uses a font this AetherFrame doesn't have (" + text.Font + "), so it shows as a box.");
                    }

                    steps.Add(new ServedText(Text(text, known), text.Text, known));
                    break;

                case LayoutImage image:
                    var index = profile.ImageIndexOf(image.AssetId);
                    steps.Add(new ServedImageStep(new ImageProfileElement
                    {
                        AssetId = ImageKey(index),
                        Position = Point(image.Position),
                        Size = new Vector2(Units(image.Width), Units(image.Height)),
                        RotationDegrees = Units(image.Rotation),
                        DisplayMode = Fit(image.Fit),
                        FlipX = image.Flips.HasFlag(LayoutFlips.Horizontal),
                        FlipY = image.Flips.HasFlag(LayoutFlips.Vertical),
                        Opacity = Unit(image.Opacity),
                    }, index));
                    break;

                case LayoutQuad quad:
                    steps.Add(new ServedShape(ServedShapeKind.Quad, Point(quad.A), Point(quad.B), Point(quad.C), Point(quad.D), Color(quad.Color)));
                    break;

                case LayoutTriangle triangle:
                    steps.Add(new ServedShape(ServedShapeKind.Triangle, Point(triangle.A), Point(triangle.B), Point(triangle.C), Point(triangle.C), Color(triangle.Color)));
                    break;

                case LayoutImageQuad imageQuad:
                    steps.Add(new ServedShape(ServedShapeKind.Image, Point(imageQuad.A), Point(imageQuad.B), Point(imageQuad.C), Point(imageQuad.D), Color(imageQuad.Tint), profile.ImageIndexOf(imageQuad.AssetId)));
                    break;

                case LayoutArtQuad art:
                    var found = BuiltInArtCatalog.Find(art.Art);
                    if (found is null)
                    {
                        Note(notes, "A piece of artwork this AetherFrame doesn't have (" + art.Art + ") shows as a box.");
                    }

                    steps.Add(new ServedShape(ServedShapeKind.Art, Point(art.A), Point(art.B), Point(art.C), Point(art.D), Color(art.Tint), Art: found));
                    break;
            }
        }

        return new ServedPlate(profile.Name, new Vector2(Units(profile.CanvasWidth), Units(profile.CanvasHeight)), background, backgroundIndex, steps, notes);
    }

    /// <summary>Whether <paramref name="font"/> is exactly one of the font families this build bundles.</summary>
    internal static bool IsBundledFont(string font)
    {
        foreach (var family in ProfileFontCatalog.All)
        {
            if (string.Equals(family.Id, font, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static (ProfileBackground? Background, int Index) BackgroundOf(ServedProfile profile)
    {
        var served = profile.Background;
        var mode = served.Mode switch
        {
            LayoutBackgroundMode.SolidColor => ProfileBackgroundMode.SolidColor,
            LayoutBackgroundMode.LinearGradient => ProfileBackgroundMode.LinearGradient,
            LayoutBackgroundMode.TexturedFill => ProfileBackgroundMode.TexturedFill,
            LayoutBackgroundMode.Image => ProfileBackgroundMode.Image,
            _ => ProfileBackgroundMode.None,
        };

        if (mode == ProfileBackgroundMode.None)
        {
            return (null, -1);
        }

        var index = mode == ProfileBackgroundMode.Image ? profile.ImageIndexOf(served.ImageAssetId) : -1;
        return (new ProfileBackground
        {
            Mode = mode,
            PrimaryColor = Color(served.Primary),
            SecondaryColor = Color(served.Secondary),
            GradientAngle = Units(served.GradientAngle),
            Opacity = Unit(served.Opacity),
            Texture = Texture(served.Texture),
            TextureIntensity = Unit(served.TextureIntensity),
            TextureScale = Units(served.TextureScale),
            TextureRotation = Units(served.TextureRotation),
            ImageAssetId = index >= 0 ? ImageKey(index) : null,
            ImageFit = Fit(served.ImageFit),
            ImageFlipX = served.ImageFlips.HasFlag(LayoutFlips.Horizontal),
            ImageFlipY = served.ImageFlips.HasFlag(LayoutFlips.Vertical),
        }, index);
    }

    /// <summary>
    /// The renderer's element for a text: what the publisher's renderer resolved, carried back. Its
    /// display text is carried whole, so it has no affixes of its own and no role (a Basic name's
    /// size was resolved before it was shared). An outline's or shadow's colour is drawn at its
    /// carried alpha times the text's (section 8.5), which is the renderer's opacity for it.
    /// </summary>
    private static TextProfileElement Text(LayoutText text, bool fontKnown)
    {
        var flags = text.Flags;
        var outline = Color(text.OutlineColor);
        var shadow = Color(text.ShadowColor);
        return new TextProfileElement
        {
            Position = Point(text.Position),
            Size = new Vector2(Units(text.Width), Units(text.Height)),
            Text = text.Text,
            FontFamily = fontKnown ? text.Font : ProfileFontFamilies.DalamudDefault,
            FontSize = Units(text.FontSize),
            Color = Color(text.Color),
            Alignment = text.Align switch
            {
                LayoutHorizontalAlign.Center => TextAlignment.Center,
                LayoutHorizontalAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
            VerticalAlignment = text.VerticalAlign switch
            {
                LayoutVerticalAlign.Middle => TextVerticalAlignment.Middle,
                LayoutVerticalAlign.Bottom => TextVerticalAlignment.Bottom,
                _ => TextVerticalAlignment.Top,
            },
            Wrap = flags.HasFlag(LayoutTextFlags.Wrap),
            Bold = flags.HasFlag(LayoutTextFlags.Bold),
            Italic = flags.HasFlag(LayoutTextFlags.Italic),
            Underline = flags.HasFlag(LayoutTextFlags.Underline),
            Strikethrough = flags.HasFlag(LayoutTextFlags.Strikethrough),
            AutoFitText = flags.HasFlag(LayoutTextFlags.AutoFit),
            AutoFitMinimumSize = Units(text.AutoFitMinimum),
            LetterSpacing = Units(text.LetterSpacing),
            LineSpacing = Units(text.LineSpacing),
            OutlineEnabled = flags.HasFlag(LayoutTextFlags.Outline) && text.OutlineThickness > 0,
            OutlineColor = outline with { W = 1f },
            OutlineOpacity = outline.W,
            OutlineThickness = Units(text.OutlineThickness),
            ShadowEnabled = flags.HasFlag(LayoutTextFlags.Shadow),
            ShadowColor = shadow with { W = 1f },
            ShadowOpacity = shadow.W,
            ShadowOffsetX = Units(text.ShadowX),
            ShadowOffsetY = Units(text.ShadowY),
            LayoutVersion = text.Layout == LayoutTextLayout.Legacy ? TextProfileElement.LegacyLayoutVersion : TextProfileElement.CurrentLayoutVersion,
        };
    }

    private static ProfileImageFit Fit(LayoutImageFit fit) => fit switch
    {
        LayoutImageFit.Fit => ProfileImageFit.Fit,
        LayoutImageFit.Fill => ProfileImageFit.Fill,
        _ => ProfileImageFit.Stretch,
    };

    private static ProfileBackgroundTexture Texture(LayoutTexture texture) =>
        Enum.IsDefined((ProfileBackgroundTexture)(byte)texture) ? (ProfileBackgroundTexture)(byte)texture : ProfileBackgroundTexture.None;

    private static Vector2 Point(LayoutPoint point) => new(Units(point.X), Units(point.Y));

    /// <summary>A value carried in hundredths, in the renderer's units.</summary>
    private static float Units(int hundredths) => (float)(hundredths / 100d);

    /// <summary>A carried byte as the renderer's 0 to 1, which ImGui turns back into the same byte.</summary>
    private static float Unit(byte value) => value / 255f;

    private static Vector4 Color(LayoutColor color) => new(Unit(color.R), Unit(color.G), Unit(color.B), Unit(color.A));

    /// <summary>The most notes kept: a Plate could name thousands of unknown idents.</summary>
    internal const int MaxNotes = 6;

    private static void Note(List<string> notes, string note)
    {
        if (notes.Count < MaxNotes && !notes.Contains(note))
        {
            notes.Add(note);
        }
        else if (notes.Count == MaxNotes && !notes.Contains(note))
        {
            notes.Add("It uses more fonts or artwork this AetherFrame doesn't have.");
        }
    }
}
