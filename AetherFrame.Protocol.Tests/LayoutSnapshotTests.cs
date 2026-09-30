using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// ProfileSnapshot schema 2, the layout (docs/networking/ProtocolSpecification-v1.md, section 8.5;
/// decision D8): the encoding against the reference written from the tables, round trips, every
/// range at both edges, the rules over the whole payload, and routing by schema version.
/// </summary>
public class LayoutSnapshotTests
{
    [Fact]
    public void Rich_EncodesExactlyAsTheSpecificationTablesSay()
    {
        Assert.Equal(ReferenceLayout.RichPayload(), LayoutSamples.Rich().EncodePayload());
    }

    [Fact]
    public void Rich_RoundTripsThroughSignAndVerify()
    {
        using var signer = TestPersonas.CreateA();
        var rich = LayoutSamples.Rich();
        var verified = SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(rich, signer));
        var decoded = Assert.IsType<ProfileLayoutSnapshot>(verified.Document);

        Assert.Equal(rich.EncodePayload(), decoded.EncodePayload());
        Assert.Equal(new RemoteProfileKey(signer.PublicKey.Id, Samples.Profile), verified.Profile);
        Assert.Equal(6, decoded.Items.Count);
        Assert.Equal([LayoutItemKind.Text, LayoutItemKind.Image, LayoutItemKind.Quad, LayoutItemKind.Triangle, LayoutItemKind.ImageQuad, LayoutItemKind.ArtQuad], decoded.Items.Select(i => i.Kind));
        Assert.Equal("Aria Starfall", Assert.IsType<LayoutText>(decoded.Items[0]).Text);
        Assert.Equal(LayoutSamples.Art, Assert.IsType<LayoutArtQuad>(decoded.Items[5]).Art);
        Assert.Equal(Samples.Asset1, decoded.Background.ImageAssetId);
        Assert.Equal([Samples.Asset1, Samples.Asset2], decoded.Images.Select(i => i.AssetId));
        Assert.Equal(13, decoded.TotalTextScalars);
        Assert.Equal(1234 + 5678, decoded.TotalImageBytes);
    }

    [Fact]
    public void Minimal_RoundTrips_AndSchemaOneStillDecodesAsSchemaOne()
    {
        using var signer = TestPersonas.CreateA();
        var minimal = Assert.IsType<ProfileLayoutSnapshot>(SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(LayoutSamples.Minimal(), signer)).Document);
        Assert.Empty(minimal.Items);
        Assert.Empty(minimal.Images);
        Assert.Equal(LayoutBackgroundMode.None, minimal.Background.Mode);

        Assert.IsType<ProfileSnapshot>(SignedDocumentCodec.Verify(Samples.SignedSnapshot(signer)).Document);
        Assert.IsType<ProfileRetraction>(SignedDocumentCodec.Verify(Samples.SignedRetraction(signer)).Document);
    }

    [Fact]
    public void Canvas_IsOneTo8192Units()
    {
        Build(canvasWidth: 100);
        Build(canvasHeight: 819_200);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => Build(canvasWidth: 99));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => Build(canvasHeight: 819_201));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => Build(canvasWidth: -100));
    }

    [Fact]
    public void Coordinates_ExtentsAndAngles_AreRangeChecked()
    {
        new LayoutQuad(new LayoutPoint(-1_000_000, 1_000_000), default, default, default, default);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new LayoutQuad(new LayoutPoint(-1_000_001, 0), default, default, default, default));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new LayoutTriangle(default, default, new LayoutPoint(0, 1_000_001), default));

        LayoutSamples.Text(width: 0, height: 1_000_000);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(width: -1));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(height: 1_000_001));

        new LayoutImage(Samples.Asset1, default, 1, 1, 36_000, LayoutImageFit.Stretch, LayoutFlips.None, 255);
        new LayoutImage(Samples.Asset1, default, 1, 1, -36_000, LayoutImageFit.Stretch, LayoutFlips.None, 255);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new LayoutImage(Samples.Asset1, default, 1, 1, 36_001, LayoutImageFit.Stretch, LayoutFlips.None, 255));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new LayoutImage(Samples.Asset1, default, 1, 1, -36_001, LayoutImageFit.Stretch, LayoutFlips.None, 255));
    }

    [Fact]
    public void TextFields_AreRangeCheckedAtBothEdges()
    {
        LayoutSamples.Text(fontSize: 600);
        LayoutSamples.Text(fontSize: 9_600);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(fontSize: 599));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(fontSize: 9_601));

        LayoutSamples.Text(letterSpacing: -1_000);
        LayoutSamples.Text(letterSpacing: 4_000);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(letterSpacing: -1_001));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(letterSpacing: 4_001));

        LayoutSamples.Text(lineSpacing: 50);
        LayoutSamples.Text(lineSpacing: 300);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(lineSpacing: 49));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(lineSpacing: 301));

        LayoutSamples.Text(autoFitMinimum: 600);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(autoFitMinimum: 599));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(autoFitMinimum: 9_601));

        LayoutSamples.Text(outlineThickness: 0);
        LayoutSamples.Text(outlineThickness: 1_600);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(outlineThickness: -1));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(outlineThickness: 1_601));

        LayoutSamples.Text(shadowX: -4_000, shadowY: 4_000);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(shadowX: -4_001));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(shadowY: 4_001));

        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(layout: (LayoutTextLayout)2));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new LayoutText(default, 1, 1, "t", LayoutSamples.Font, 1_000, default, (LayoutHorizontalAlign)3, LayoutVerticalAlign.Top, LayoutTextFlags.None, 0, 100, 800, default, 0, default, 0, 0, LayoutTextLayout.Current));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new LayoutText(default, 1, 1, "t", LayoutSamples.Font, 1_000, default, LayoutHorizontalAlign.Left, (LayoutVerticalAlign)3, LayoutTextFlags.None, 0, 100, 800, default, 0, default, 0, 0, LayoutTextLayout.Current));
    }

    [Fact]
    public void TextContent_FollowsTheGeneralTextRules_WithItsOwnLimit()
    {
        // Section 2.3 with a limit of 2,000 scalars an item: line breaks and format characters are
        // content here (only a name refuses them), U+0000 and unpaired surrogates are refused.
        Assert.Equal("line\r\nbreak\t", LayoutSamples.Text("line\r\nbreak\t").Text);
        LayoutSamples.Text(string.Empty);
        LayoutSamples.Text(new string('a', 2_000));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => LayoutSamples.Text(new string('a', 2_001)));
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => LayoutSamples.Text("a" + (char)0));
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => LayoutSamples.Text("a" + (char)0xD800));
    }

    [Theory]
    [InlineData("a", null)]
    [InlineData("a-b.c9", null)]
    [InlineData("", ProtocolError.InvalidLength)]
    [InlineData("Font", ProtocolError.InvalidValue)]
    [InlineData("1font", ProtocolError.InvalidValue)]
    [InlineData(".font", ProtocolError.InvalidValue)]
    [InlineData("font_x", ProtocolError.InvalidValue)]
    [InlineData("font x", ProtocolError.InvalidValue)]
    [InlineData("font/x", ProtocolError.InvalidValue)]
    public void Identifiers_AreLowercaseAsciiStartingWithALetter(string ident, ProtocolError? expected)
    {
        if (expected is { } error)
        {
            ProtocolAssert.Throws(error, () => LayoutSamples.Text(font: ident));
            ProtocolAssert.Throws(error, () => new LayoutArtQuad(ident, default, default, default, default, default));
        }
        else
        {
            Assert.Equal(ident, LayoutSamples.Text(font: ident).Font);
            Assert.Equal(ident, new LayoutArtQuad(ident, default, default, default, default, default).Art);
        }
    }

    [Fact]
    public void Identifiers_AreAtMost96Bytes_AndPlainAscii()
    {
        LayoutSamples.Text(font: "a" + new string('b', 95));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => LayoutSamples.Text(font: "a" + new string('b', 96)));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Text(font: "caf" + (char)0xE9));
    }

    [Fact]
    public void CodesAndFlags_AreClosed()
    {
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => Background(mode: (LayoutBackgroundMode)5));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => Background(texture: (LayoutTexture)21));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => Background(fit: (LayoutImageFit)3));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => Background(flips: (LayoutFlips)4));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => Background(scale: 399));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => Background(scale: 12_801));
        Background(texture: LayoutTexture.Brick, scale: 400);
        Background(scale: 12_800);

        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new LayoutImage(Samples.Asset1, default, 1, 1, 0, (LayoutImageFit)3, LayoutFlips.None, 255));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new LayoutImage(Samples.Asset1, default, 1, 1, 0, LayoutImageFit.Fill, (LayoutFlips)0x80, 255));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new LayoutImage(default, default, 1, 1, 0, LayoutImageFit.Fill, LayoutFlips.None, 255));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new LayoutImageQuad(default, default, default, default, default, default));
    }

    [Fact]
    public void TheBackground_NamesAnImage_ExactlyInImageMode()
    {
        Background(mode: LayoutBackgroundMode.Image, image: Samples.Asset1);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => Background(mode: LayoutBackgroundMode.Image));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => Background(mode: LayoutBackgroundMode.SolidColor, image: Samples.Asset1));
    }

    [Fact]
    public void Images_ArePngOrJpeg_EachDrawn_AndEveryDrawnOneCarried()
    {
        var image = new LayoutImage(Samples.Asset1, default, 100, 100, 0, LayoutImageFit.Fit, LayoutFlips.None, 255);
        LayoutSamples.Minimal([image], [Samples.Image(Samples.Asset1)]);
        LayoutSamples.Minimal([image], [Samples.Image(Samples.Asset1, format: ImageFormat.Jpeg)]);

        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Minimal([image], [Samples.Image(Samples.Asset1, format: ImageFormat.WebP)]));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Minimal([], [Samples.Image(Samples.Asset1)]));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Minimal([image], []));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Minimal([], [], LayoutSamples.ImageBackground(Samples.Asset1)));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => LayoutSamples.Minimal([image], [Samples.Image(Samples.Asset1), Samples.Image(Samples.Asset1, 0x22)]));

        // The background alone can draw an image, and one image can be drawn by several items.
        LayoutSamples.Minimal([], [Samples.Image(Samples.Asset1)], LayoutSamples.ImageBackground(Samples.Asset1));
        LayoutSamples.Minimal([image, image], [Samples.Image(Samples.Asset1)], LayoutSamples.ImageBackground(Samples.Asset1));
    }

    [Fact]
    public void Counts_AndTotals_AreLimited()
    {
        var quad = new LayoutQuad(default, default, default, default, default);
        Assert.Equal(1_024, LayoutSamples.Minimal(Enumerable.Repeat<LayoutItem>(quad, 1_024)).Items.Count);
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => LayoutSamples.Minimal(Enumerable.Repeat<LayoutItem>(quad, 1_025)));

        var full = LayoutSamples.Text(new string('a', 2_000));
        Assert.Equal(32_000, LayoutSamples.Minimal(Enumerable.Repeat<LayoutItem>(full, 16)).TotalTextScalars);
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => LayoutSamples.Minimal([.. Enumerable.Repeat<LayoutItem>(full, 16), LayoutSamples.Text("a")]));

        var ids = Enumerable.Range(1, 9).Select(i => AssetId.Parse("ast_" + i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var drawers = ids.Select(id => (LayoutItem)new LayoutImage(id, default, 1, 1, 0, LayoutImageFit.Fit, LayoutFlips.None, 255)).ToArray();
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => LayoutSamples.Minimal(drawers, ids.Select(id => Samples.Image(id))));

        var big = ids.Take(6).Select(id => Samples.Image(id, bytes: ProtocolLimits.MaxImageBytes)).ToArray();
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => LayoutSamples.Minimal(drawers.Take(6), big));
        Assert.Equal(5 * ProtocolLimits.MaxImageBytes, LayoutSamples.Minimal(drawers.Take(5), big.Take(5)).TotalImageBytes);
    }

    private static ProfileLayoutSnapshot Build(int canvasWidth = 128_000, int canvasHeight = 72_000) =>
        new(Samples.Profile, LayoutSamples.RevisionLayout, Samples.CreatedAt, "A", canvasWidth, canvasHeight, LayoutBackground.None, [], []);

    private static LayoutBackground Background(
        LayoutBackgroundMode mode = LayoutBackgroundMode.SolidColor,
        LayoutTexture texture = LayoutTexture.None,
        int scale = 2_400,
        AssetId image = default,
        LayoutImageFit fit = LayoutImageFit.Stretch,
        LayoutFlips flips = LayoutFlips.None) =>
        new(mode, default, default, 0, 255, texture, 0, scale, 0, image, fit, flips);
}
