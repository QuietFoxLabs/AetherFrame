using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Sample schema 2 layouts. <see cref="Rich"/> uses every item kind and an image background, so a
/// round trip and the reference payload cover every field of section 8.5 at least once.
/// </summary>
internal static class LayoutSamples
{
    public static readonly RevisionId RevisionLayout = RevisionId.Parse("rev_b6b6b6b6b6b6b6b6b6b6b6b6b6b6b6b6");

    public const string Font = "aetherframe-sans";

    public const string Art = "af.asset.celestial-sakura.plate-frame.blossom";

    public static LayoutColor Color(byte r, byte g, byte b, byte a = 255) => new(r, g, b, a);

    public static LayoutText Text(string text = "Sample text", LayoutPoint? position = null, int width = 40_000, int height = 5_000, string font = Font, int fontSize = 2_400,
        LayoutTextFlags flags = LayoutTextFlags.Wrap, int letterSpacing = 0, int lineSpacing = 100, int autoFitMinimum = 800, int outlineThickness = 200,
        int shadowX = 300, int shadowY = 300, LayoutTextLayout layout = LayoutTextLayout.Current) =>
        new(position ?? new LayoutPoint(4_000, 4_000), width, height, text, font, fontSize, Color(255, 255, 255), LayoutHorizontalAlign.Left, LayoutVerticalAlign.Top,
            flags, letterSpacing, lineSpacing, autoFitMinimum, Color(0, 0, 0), outlineThickness, Color(0, 0, 0, 153), shadowX, shadowY, layout);

    public static LayoutBackground ImageBackground(AssetId assetId) =>
        new(LayoutBackgroundMode.Image, Color(10, 20, 30), Color(40, 50, 60), 9_000, 230, LayoutTexture.Dots, 90, 2_400, -4_500, assetId, LayoutImageFit.Fill, LayoutFlips.Horizontal);

    /// <summary>Every item kind once, an image background, a PNG and a JPEG.</summary>
    public static ProfileLayoutSnapshot Rich()
    {
        var png = Samples.Image(Samples.Asset1);
        var jpeg = Samples.Image(Samples.Asset2, 0x22, ImageFormat.Jpeg, 5678, 1920, 1080);
        var items = new List<LayoutItem>
        {
            Text("Aria Starfall", flags: LayoutTextFlags.Wrap | LayoutTextFlags.Bold | LayoutTextFlags.Outline | LayoutTextFlags.Shadow, letterSpacing: -150),
            new LayoutImage(Samples.Asset2, new LayoutPoint(-1_000, 20_000), 30_000, 20_000, -1_250, LayoutImageFit.Fit, LayoutFlips.Vertical, 200),
            new LayoutQuad(new LayoutPoint(0, 0), new LayoutPoint(128_000, 0), new LayoutPoint(128_000, 1_000), new LayoutPoint(0, 1_000), Color(200, 170, 90)),
            new LayoutTriangle(new LayoutPoint(100, 100), new LayoutPoint(900, 100), new LayoutPoint(100, 900), Color(200, 170, 90, 128)),
            new LayoutImageQuad(Samples.Asset1, new LayoutPoint(1_000, 1_000), new LayoutPoint(9_000, 1_000), new LayoutPoint(9_000, 9_000), new LayoutPoint(1_000, 9_000), Color(255, 255, 255, 200)),
            new LayoutArtQuad(Art, new LayoutPoint(0, 0), new LayoutPoint(128_000, 0), new LayoutPoint(128_000, 72_000), new LayoutPoint(0, 72_000), Color(255, 240, 250)),
        };
        return new ProfileLayoutSnapshot(Samples.Profile, RevisionLayout, Samples.CreatedAt, Samples.Name, 128_000, 72_000, ImageBackground(Samples.Asset1), items, [png, jpeg]);
    }

    /// <summary>The smallest valid layout: no background, no items, no images.</summary>
    public static ProfileLayoutSnapshot Minimal(IEnumerable<LayoutItem>? items = null, IEnumerable<ImageReference>? images = null, LayoutBackground? background = null) =>
        new(Samples.Profile, RevisionLayout, Samples.CreatedAt, "A", 128_000, 72_000, background ?? LayoutBackground.None, items ?? [], images ?? []);
}

/// <summary>
/// The schema 2 payload of <see cref="LayoutSamples.Rich"/>, written out from the tables of
/// docs/networking/ProtocolSpecification-v1.md, section 8.5, with no call into the library.
/// </summary>
internal static class ReferenceLayout
{
    public static byte[] RichPayload()
    {
        var w = new Bytes();
        w.U16(2);
        w.Raw(Samples.Profile.ToArray());
        w.Raw(LayoutSamples.RevisionLayout.ToArray());
        w.U64((ulong)Samples.CreatedAt);
        w.Text(Samples.Name);
        w.I32(128_000);
        w.I32(72_000);

        // Background.
        w.U8(4);
        w.Color(10, 20, 30, 255);
        w.Color(40, 50, 60, 255);
        w.I32(9_000);
        w.U8(230);
        w.U8(2);
        w.U8(90);
        w.I32(2_400);
        w.I32(-4_500);
        w.Raw(Samples.Asset1.ToArray());
        w.U8(2);
        w.U8(1);

        // Items.
        w.U32(6);

        w.U8(1);
        w.I32(4_000);
        w.I32(4_000);
        w.I32(40_000);
        w.I32(5_000);
        w.Text("Aria Starfall");
        w.Ident(LayoutSamples.Font);
        w.I32(2_400);
        w.Color(255, 255, 255, 255);
        w.U8(0);
        w.U8(0);
        w.U8(0b1100_0011);
        w.I32(-150);
        w.I32(100);
        w.I32(800);
        w.Color(0, 0, 0, 255);
        w.U16(200);
        w.Color(0, 0, 0, 153);
        w.I32(300);
        w.I32(300);
        w.U8(1);

        w.U8(2);
        w.Raw(Samples.Asset2.ToArray());
        w.I32(-1_000);
        w.I32(20_000);
        w.I32(30_000);
        w.I32(20_000);
        w.I32(-1_250);
        w.U8(1);
        w.U8(2);
        w.U8(200);

        w.U8(3);
        w.Points(0, 0, 128_000, 0, 128_000, 1_000, 0, 1_000);
        w.Color(200, 170, 90, 255);

        w.U8(4);
        w.Points(100, 100, 900, 100, 100, 900);
        w.Color(200, 170, 90, 128);

        w.U8(5);
        w.Raw(Samples.Asset1.ToArray());
        w.Points(1_000, 1_000, 9_000, 1_000, 9_000, 9_000, 1_000, 9_000);
        w.Color(255, 255, 255, 200);

        w.U8(6);
        w.Ident(LayoutSamples.Art);
        w.Points(0, 0, 128_000, 0, 128_000, 72_000, 0, 72_000);
        w.Color(255, 240, 250, 255);

        // Images, in ascending asset id order: the PNG (Asset1), then the JPEG (Asset2).
        w.U32(2);
        w.Image(Samples.Asset1.ToArray(), Samples.Digest(0x11), 1, 1234, 640, 480);
        w.Image(Samples.Asset2.ToArray(), Samples.Digest(0x22), 2, 5678, 1920, 1080);
        return w.ToArray();
    }

    /// <summary>A byte sink for payloads written field by field from the tables.</summary>
    internal sealed class Bytes
    {
        private readonly List<byte> bytes = [];

        public void U8(byte value) => bytes.Add(value);

        public void U16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, value);
            bytes.AddRange(b.ToArray());
        }

        public void U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
            bytes.AddRange(b.ToArray());
        }

        public void I32(int value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, value);
            bytes.AddRange(b.ToArray());
        }

        public void U64(ulong value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, value);
            bytes.AddRange(b.ToArray());
        }

        public void Raw(byte[] value) => bytes.AddRange(value);

        public void Text(string value)
        {
            var utf8 = System.Text.Encoding.UTF8.GetBytes(value);
            U32((uint)utf8.Length);
            Raw(utf8);
        }

        public void Ident(string value)
        {
            U8((byte)value.Length);
            Raw(System.Text.Encoding.ASCII.GetBytes(value));
        }

        public void Color(byte r, byte g, byte b, byte a) => Raw([r, g, b, a]);

        public void Points(params int[] coordinates)
        {
            foreach (var coordinate in coordinates)
            {
                I32(coordinate);
            }
        }

        public void Image(byte[] assetId, byte[] digest, byte format, ulong byteLength, uint width, uint height)
        {
            Raw(assetId);
            Raw(digest);
            U8(format);
            U64(byteLength);
            U32(width);
            U32(height);
        }

        public byte[] ToArray() => bytes.ToArray();
    }
}
