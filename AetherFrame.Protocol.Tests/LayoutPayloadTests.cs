using System;
using System.Linq;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Remote;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Validly signed schema 2 payloads with faults, built byte by byte from the tables of
/// docs/networking/ProtocolSpecification-v1.md, section 8.5, so only the payload decoder can
/// refuse them: each field is refused as soon as it is read, and an input with several faults is
/// refused for the first in reading order ("Input with several faults").
/// </summary>
public class LayoutPayloadTests
{
    public static TheoryData<string, ProtocolError> Faults()
    {
        var data = new TheoryData<string, ProtocolError>();
        foreach (var (name, error) in LayoutPayload.Faults)
        {
            data.Add(name, error);
        }

        return data;
    }

    [Fact]
    public void TheBuilder_WritesExactlyTheLibrarysEncodingOfAValidPayload()
    {
        Assert.Equal(LayoutSamples.Minimal([LayoutSamples.Text("t")]).EncodePayload(), LayoutPayload.Build(items: w => { w.U32(1); LayoutPayload.TextItem(w, text: "t"); }));
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public void SignedPayloadsWithOneFault_AreRefusedWithItsError(string name, ProtocolError expected)
    {
        using var signer = TestPersonas.CreateA();
        ProtocolAssert.Throws(expected, () => SignedDocumentCodec.Verify(PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, LayoutPayload.Case(name))));
    }

    [Fact]
    public void PayloadsWithSeveralFaults_AreRefusedForTheFirstInReadingOrder()
    {
        static void Image(ReferenceLayout.Bytes w, byte fill, ulong byteLength = 1234, uint width = 640, uint height = 480) => LayoutPayload.Image(w, fill, byteLength: byteLength, width: width, height: height);
        static void ImageItem(ReferenceLayout.Bytes w, byte fill) => LayoutPayload.ImageItem(w, fill);
        static void TextItem(ReferenceLayout.Bytes w, string? text = null, int x = 4_000, int fontSize = 2_400) => LayoutPayload.TextItem(w, x: x, text: text, fontSize: fontSize);

        using var signer = TestPersonas.CreateA();
        ProtocolException Refuse(byte[] payload) => ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, payload)));

        // The canvas before the background, both InvalidValue: the message names the field.
        var canvasFirst = Refuse(LayoutPayload.Build(canvasWidth: 99, background: w => LayoutPayload.Background(w, mode: 9)));
        Assert.Contains("canvasWidth", canvasFirst.Message, StringComparison.Ordinal);

        // A field's rule before the trailing byte, the trailing byte before the whole-payload rules.
        Assert.Equal(ProtocolError.InvalidValue, Refuse(LayoutPayload.Build(items: w => { w.U32(1); w.U8(9); }, trailing: [0])).Error);
        Assert.Equal(ProtocolError.TrailingBytes, Refuse(LayoutPayload.Build(images: w => { w.U32(1); LayoutPayload.Png(w, 0xc3); }, trailing: [0])).Error);

        // Among the whole-payload rules: the text total before the image references.
        var texts = Enumerable.Repeat(new string('a', 2_000), 16).Append("a").ToArray();
        var totalFirst = Refuse(LayoutPayload.Build(
            items: w => { w.U32((uint)texts.Length); foreach (var text in texts) { LayoutPayload.TextItem(w, text: text); } },
            images: w => { w.U32(1); LayoutPayload.Png(w, 0xc3); }));
        Assert.Equal(ProtocolError.LimitExceeded, totalFirst.Error);

        // The rules over the whole payload in order: bytes, then pixels, then text.
        var eight = Enumerable.Range(0, 8).Select(i => (byte)(0xa0 + i)).ToArray();
        void DrawAll(ReferenceLayout.Bytes w, int extraTexts)
        {
            w.U32((uint)(eight.Length + extraTexts));
            foreach (var fill in eight)
            {
                ImageItem(w, fill);
            }

            for (var i = 0; i < extraTexts; i++)
            {
                TextItem(w, text: new string('a', 2_000));
            }
        }

        var bytesFirst = Refuse(LayoutPayload.Build(items: w => DrawAll(w, 17), images: w => { w.U32(8); foreach (var fill in eight) { Image(w, fill, byteLength: 8_388_608, width: 5_000, height: 4_000); } }));
        Assert.Contains("bytes in total", bytesFirst.Message, StringComparison.Ordinal);
        var pixelsFirst = Refuse(LayoutPayload.Build(items: w => DrawAll(w, 17), images: w => { w.U32(8); foreach (var fill in eight) { Image(w, fill, width: 5_000, height: 4_000); } }));
        Assert.Contains("pixels in total", pixelsFirst.Message, StringComparison.Ordinal);

        // A field's own rule before a later truncation, for fields read in each part of the payload.
        var coordinateFirst = Refuse(LayoutPayload.Build(items: w => { w.U32(1); TextItem(w, x: 1_000_000_001); })[..^6]);
        Assert.Contains("text.position.x", coordinateFirst.Message, StringComparison.Ordinal);
        var fontSizeFirst = Refuse(LayoutPayload.Build(items: w => { w.U32(1); TextItem(w, fontSize: 99); })[..^6]);
        Assert.Contains("text.fontSize", fontSizeFirst.Message, StringComparison.Ordinal);
        var backgroundFirst = Refuse(LayoutPayload.Build(background: w => LayoutPayload.Background(w, mode: 1, assetFill: 0xc3))[..^6]);
        Assert.Contains("image mode", backgroundFirst.Message, StringComparison.Ordinal);

        // The format of an image before whether it is drawn.
        var webpFirst = Refuse(LayoutPayload.Build(images: w => { w.U32(1); LayoutPayload.Image(w, 0xc3, format: 3); }));
        Assert.Contains("PNG and JPEG", webpFirst.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RandomMutations_OfASignedLayout_AreRefusedOnlyWithProtocolErrors()
    {
        using var signer = TestPersonas.CreateA();
        var payload = LayoutSamples.Rich().EncodePayload();
        var random = new Random(20260929);
        var accepted = 0;
        for (var round = 0; round < 3_000; round++)
        {
            var mutated = (byte[])payload.Clone();
            var changes = random.Next(1, 4);
            for (var change = 0; change < changes; change++)
            {
                mutated[random.Next(2, mutated.Length)] = (byte)random.Next(256);
            }

            if (round % 5 == 0)
            {
                mutated = mutated[..random.Next(2, mutated.Length)];
            }

            try
            {
                Assert.IsType<ProfileLayoutSnapshot>(SignedDocumentCodec.Verify(PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, mutated)).Document);
                accepted++;
            }
            catch (ProtocolException)
            {
            }
        }

        // Some mutations land on free values (colours, opacities) and must still decode.
        Assert.True(accepted > 0);
    }
}

/// <summary>Schema 2 payloads written field by field, each part replaceable, for the fault tests.</summary>
internal static class LayoutPayload
{
    /// <summary>Every fault case: its name and the error it must be refused with. The tests and the rejected vectors both use this list.</summary>
    public static readonly (string Name, ProtocolError Error)[] Faults =
    [
        ("canvas width 99", ProtocolError.InvalidValue),
        ("canvas height over max", ProtocolError.InvalidValue),
        ("background mode 5", ProtocolError.InvalidValue),
        ("background texture 21", ProtocolError.InvalidValue),
        ("background scale 399", ProtocolError.InvalidValue),
        ("background angle over max", ProtocolError.InvalidValue),
        ("background image without image mode", ProtocolError.InvalidValue),
        ("background image mode without image", ProtocolError.InvalidValue),
        ("background fit 3", ProtocolError.InvalidValue),
        ("background flips 4", ProtocolError.InvalidValue),
        ("items count over max", ProtocolError.LimitExceeded),
        ("item kind 0", ProtocolError.InvalidValue),
        ("item kind 7", ProtocolError.InvalidValue),
        ("item kind 255", ProtocolError.InvalidValue),
        ("text coordinate over max", ProtocolError.InvalidValue),
        ("text width negative", ProtocolError.InvalidValue),
        ("text length over max bytes", ProtocolError.LimitExceeded),
        ("text over max scalars", ProtocolError.LimitExceeded),
        ("text with NUL", ProtocolError.InvalidText),
        ("text invalid utf8", ProtocolError.InvalidText),
        ("font empty", ProtocolError.InvalidLength),
        ("font 97 bytes", ProtocolError.LimitExceeded),
        ("font uppercase", ProtocolError.InvalidValue),
        ("font size 99", ProtocolError.InvalidValue),
        ("text align 3", ProtocolError.InvalidValue),
        ("text vertical align 3", ProtocolError.InvalidValue),
        ("letter spacing over max", ProtocolError.InvalidValue),
        ("line spacing over max", ProtocolError.InvalidValue),
        ("auto-fit minimum over max", ProtocolError.InvalidValue),
        ("outline thickness over max", ProtocolError.InvalidValue),
        ("shadow x over max", ProtocolError.InvalidValue),
        ("text layout 2", ProtocolError.InvalidValue),
        ("image asset zero", ProtocolError.InvalidValue),
        ("image rotation over max", ProtocolError.InvalidValue),
        ("image fit 3", ProtocolError.InvalidValue),
        ("image flips unknown bit", ProtocolError.InvalidValue),
        ("art id with a space", ProtocolError.InvalidValue),
        ("images count 9", ProtocolError.LimitExceeded),
        ("image webp", ProtocolError.InvalidValue),
        ("images unsorted", ProtocolError.NotCanonical),
        ("trailing byte", ProtocolError.TrailingBytes),
        ("truncated inside an item", ProtocolError.Truncated),
        ("image carried but not drawn", ProtocolError.InvalidValue),
        ("image drawn but not carried", ProtocolError.InvalidValue),
        ("texts over the total", ProtocolError.LimitExceeded),
        ("image pixels over the total", ProtocolError.LimitExceeded),
        ("field fault then truncation", ProtocolError.InvalidValue),
    ];

    public static byte[] Build(
        int canvasWidth = 128_000,
        int canvasHeight = 72_000,
        Action<ReferenceLayout.Bytes>? background = null,
        Action<ReferenceLayout.Bytes>? items = null,
        Action<ReferenceLayout.Bytes>? images = null,
        byte[]? trailing = null)
    {
        var w = new ReferenceLayout.Bytes();
        w.U16(2);
        w.Raw(Samples.Profile.ToArray());
        w.Raw(LayoutSamples.RevisionLayout.ToArray());
        w.U64((ulong)Samples.CreatedAt);
        w.Text("A");
        w.I32(canvasWidth);
        w.I32(canvasHeight);
        (background ?? (b => Background(b)))(w);
        (items ?? (b => b.U32(0)))(w);
        (images ?? (b => b.U32(0)))(w);
        if (trailing is not null)
        {
            w.Raw(trailing);
        }

        return w.ToArray();
    }

    public static void Background(ReferenceLayout.Bytes w, byte mode = 0, byte texture = 0, int scale = 2_400, int angle = 0, byte assetFill = 0, byte fit = 0, byte flips = 0)
    {
        w.U8(mode);
        w.Color(0, 0, 0, 0);
        w.Color(0, 0, 0, 0);
        w.I32(angle);
        w.U8(255);
        w.U8(texture);
        w.U8(0);
        w.I32(scale);
        w.I32(0);
        w.Raw(Enumerable.Repeat(assetFill, 16).ToArray());
        w.U8(fit);
        w.U8(flips);
    }

    public static void TextItem(
        ReferenceLayout.Bytes w, int x = 4_000, int width = 40_000, string? text = null, byte[]? textBytes = null, uint? textLength = null, string font = LayoutSamples.Font,
        int fontSize = 2_400, byte align = 0, byte verticalAlign = 0, int letterSpacing = 0, int lineSpacing = 100, int autoFit = 800, ushort outline = 200,
        int shadowX = 300, byte layout = 1)
    {
        w.U8(1);
        w.I32(x);
        w.I32(4_000);
        w.I32(width);
        w.I32(5_000);
        var bytes = textBytes ?? System.Text.Encoding.UTF8.GetBytes(text ?? "Sample text");
        w.U32(textLength ?? (uint)bytes.Length);
        w.Raw(bytes);
        w.U8((byte)font.Length);
        w.Raw(System.Text.Encoding.ASCII.GetBytes(font));
        w.I32(fontSize);
        w.Color(255, 255, 255, 255);
        w.U8(align);
        w.U8(verticalAlign);
        w.U8(1);
        w.I32(letterSpacing);
        w.I32(lineSpacing);
        w.I32(autoFit);
        w.Color(0, 0, 0, 255);
        w.U16(outline);
        w.Color(0, 0, 0, 153);
        w.I32(shadowX);
        w.I32(300);
        w.U8(layout);
    }

    public static void ImageItem(ReferenceLayout.Bytes w, byte assetFill = 0xc3, int rotation = 0, byte fit = 0, byte flips = 0)
    {
        w.U8(2);
        w.Raw(Enumerable.Repeat(assetFill, 16).ToArray());
        w.I32(0);
        w.I32(0);
        w.I32(100);
        w.I32(100);
        w.I32(rotation);
        w.U8(fit);
        w.U8(flips);
        w.U8(255);
    }

    public static void ArtItem(ReferenceLayout.Bytes w, string art)
    {
        w.U8(6);
        w.U8((byte)art.Length);
        w.Raw(System.Text.Encoding.ASCII.GetBytes(art));
        w.Points(0, 0, 100, 0, 100, 100, 0, 100);
        w.Color(255, 255, 255, 255);
    }

    public static void Image(ReferenceLayout.Bytes w, byte assetFill, byte format = 1, ulong byteLength = 1234, uint width = 640, uint height = 480) =>
        w.Image(Enumerable.Repeat(assetFill, 16).ToArray(), Samples.Digest(0x11), format, byteLength, width, height);

    public static void Png(ReferenceLayout.Bytes w, byte assetFill) => Image(w, assetFill);

    public static byte[] Case(string name) => name switch
    {
        "canvas width 99" => Build(canvasWidth: 99),
        "canvas height over max" => Build(canvasHeight: 819_201),
        "background mode 5" => Build(background: w => Background(w, mode: 5)),
        "background texture 21" => Build(background: w => Background(w, texture: 21)),
        "background scale 399" => Build(background: w => Background(w, scale: 399)),
        "background angle over max" => Build(background: w => Background(w, angle: 36_001)),
        "background image without image mode" => Build(background: w => Background(w, mode: 1, assetFill: 0xc3), images: w => { w.U32(1); Png(w, 0xc3); }),
        "background image mode without image" => Build(background: w => Background(w, mode: 4)),
        "background fit 3" => Build(background: w => Background(w, fit: 3)),
        "background flips 4" => Build(background: w => Background(w, flips: 4)),
        "items count over max" => Build(items: w => w.U32(2_049)),
        "item kind 0" => Build(items: w => { w.U32(1); w.U8(0); }),
        "item kind 7" => Build(items: w => { w.U32(1); w.U8(7); }),
        "item kind 255" => Build(items: w => { w.U32(1); w.U8(255); }),
        "text coordinate over max" => Build(items: w => { w.U32(1); TextItem(w, x: 1_000_000_001); }),
        "text width negative" => Build(items: w => { w.U32(1); TextItem(w, width: -1); }),
        "text length over max bytes" => Build(items: w => { w.U32(1); TextItem(w, textBytes: [0x61], textLength: 8_193); }),
        "text over max scalars" => Build(items: w => { w.U32(1); TextItem(w, text: new string('a', 2_049)); }),
        "text with NUL" => Build(items: w => { w.U32(1); TextItem(w, textBytes: [0x61, 0x00]); }),
        "text invalid utf8" => Build(items: w => { w.U32(1); TextItem(w, textBytes: [0xFF]); }),
        "font empty" => Build(items: w => { w.U32(1); TextItem(w, font: string.Empty); }),
        "font 97 bytes" => Build(items: w => { w.U32(1); TextItem(w, font: "a" + new string('b', 96)); }),
        "font uppercase" => Build(items: w => { w.U32(1); TextItem(w, font: "Sans"); }),
        "font size 99" => Build(items: w => { w.U32(1); TextItem(w, fontSize: 99); }),
        "text align 3" => Build(items: w => { w.U32(1); TextItem(w, align: 3); }),
        "text vertical align 3" => Build(items: w => { w.U32(1); TextItem(w, verticalAlign: 3); }),
        "letter spacing over max" => Build(items: w => { w.U32(1); TextItem(w, letterSpacing: 1_000_001); }),
        "line spacing over max" => Build(items: w => { w.U32(1); TextItem(w, lineSpacing: -1_000_001); }),
        "auto-fit minimum over max" => Build(items: w => { w.U32(1); TextItem(w, autoFit: 102_401); }),
        "outline thickness over max" => Build(items: w => { w.U32(1); TextItem(w, outline: 1_601); }),
        "shadow x over max" => Build(items: w => { w.U32(1); TextItem(w, shadowX: 1_000_001); }),
        "text layout 2" => Build(items: w => { w.U32(1); TextItem(w, layout: 2); }),
        "image asset zero" => Build(items: w => { w.U32(1); ImageItem(w, assetFill: 0); }),
        "image rotation over max" => Build(items: w => { w.U32(1); ImageItem(w, rotation: 36_001); }, images: w => { w.U32(1); Png(w, 0xc3); }),
        "image fit 3" => Build(items: w => { w.U32(1); ImageItem(w, fit: 3); }, images: w => { w.U32(1); Png(w, 0xc3); }),
        "image flips unknown bit" => Build(items: w => { w.U32(1); ImageItem(w, flips: 0x80); }, images: w => { w.U32(1); Png(w, 0xc3); }),
        "art id with a space" => Build(items: w => { w.U32(1); ArtItem(w, "af.asset one"); }),
        "images count 9" => Build(images: w => w.U32(9)),
        "image webp" => Build(items: w => { w.U32(1); ImageItem(w); }, images: w => { w.U32(1); Image(w, 0xc3, format: 3); }),
        "images unsorted" => Build(items: w => { w.U32(2); ImageItem(w, 0xc3); ImageItem(w, 0xd4); }, images: w => { w.U32(2); Png(w, 0xd4); Png(w, 0xc3); }),
        "trailing byte" => Build(trailing: [0]),
        "truncated inside an item" => Build(items: w => { w.U32(1); TextItem(w); })[..^3],
        "image carried but not drawn" => Build(images: w => { w.U32(1); Png(w, 0xc3); }),
        "image drawn but not carried" => Build(items: w => { w.U32(1); ImageItem(w); }),
        "texts over the total" => Build(items: w => { w.U32(17); for (var i = 0; i < 16; i++) { TextItem(w, text: new string('a', 2_000)); } TextItem(w, text: "a"); }),
        "image pixels over the total" => Build(items: w => { w.U32(2); ImageItem(w, 0xc3); ImageItem(w, 0xd4); }, images: w => { w.U32(2); Image(w, 0xc3, width: 5_000, height: 4_000); Image(w, 0xd4, width: 5_000, height: 4_000); }),
        "field fault then truncation" => Build(items: w => { w.U32(2); w.U8(9); })[..^1],
        _ => throw new ArgumentException(name),
    };
}
