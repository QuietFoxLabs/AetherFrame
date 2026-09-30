using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Served profiles (docs/networking/ProtocolSpecification-v1.md, section 8.6) written out field by
/// field from the tables, with no call into the library. <see cref="ServedSpec"/>'s defaults are the
/// served form of <see cref="LayoutSamples.Rich"/>; a test changes one field to make a refusal.
/// </summary>
internal static class ReferenceServed
{
    /// <summary>The marker every sample uses.</summary>
    public static readonly RevisionMarker Marker = RevisionMarker.Parse("mrk_" + string.Concat(Enumerable.Repeat("e1", 16)));

    public static byte[] Write(ServedSpec spec)
    {
        var w = new ReferenceLayout.Bytes();
        w.Raw(spec.Magic);
        w.U16(spec.Version);
        w.Raw(spec.Marker);
        w.Text(spec.Name);
        w.I32(spec.CanvasWidth);
        w.I32(spec.CanvasHeight);

        // Background, as section 8.5's with the image named by index.
        w.U8(spec.BackgroundMode);
        w.Color(10, 20, 30, 255);
        w.Color(40, 50, 60, 255);
        w.I32(9_000);
        w.U8(230);
        w.U8(2);
        w.U8(90);
        w.I32(2_400);
        w.I32(-4_500);
        w.U8(spec.BackgroundImage);
        w.U8(2);
        w.U8(1);

        w.U32(spec.ItemCount ?? (uint)spec.Items.Count);
        foreach (var item in spec.Items)
        {
            item(w);
        }

        w.U32(spec.ImageCount ?? (uint)spec.Images.Count);
        foreach (var (format, width, height) in spec.Images)
        {
            w.U8(format);
            w.U32(width);
            w.U32(height);
        }

        w.Raw(spec.Trailing);
        return w.ToArray();
    }

    /// <summary>The rich sample's six items, with its image and image quad named by index.</summary>
    public static List<Action<ReferenceLayout.Bytes>> RichItems(byte imageIndex = 1, byte quadIndex = 0) =>
    [
        w =>
        {
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
        },
        w => Image(w, imageIndex),
        w =>
        {
            w.U8(3);
            w.Points(0, 0, 128_000, 0, 128_000, 1_000, 0, 1_000);
            w.Color(200, 170, 90, 255);
        },
        w =>
        {
            w.U8(4);
            w.Points(100, 100, 900, 100, 100, 900);
            w.Color(200, 170, 90, 128);
        },
        w =>
        {
            w.U8(5);
            w.U8(quadIndex);
            w.Points(1_000, 1_000, 9_000, 1_000, 9_000, 9_000, 1_000, 9_000);
            w.Color(255, 255, 255, 200);
        },
        w =>
        {
            w.U8(6);
            w.Ident(LayoutSamples.Art);
            w.Points(0, 0, 128_000, 0, 128_000, 72_000, 0, 72_000);
            w.Color(255, 240, 250, 255);
        },
    ];

    /// <summary>An image item drawing image <paramref name="index"/>, as the rich sample's.</summary>
    public static void Image(ReferenceLayout.Bytes w, byte index)
    {
        w.U8(2);
        w.U8(index);
        w.I32(-1_000);
        w.I32(20_000);
        w.I32(30_000);
        w.I32(20_000);
        w.I32(-1_250);
        w.U8(1);
        w.U8(2);
        w.U8(200);
    }

    /// <summary>A text item holding <paramref name="text"/>, otherwise the rich sample's.</summary>
    public static void Text(ReferenceLayout.Bytes w, string text)
    {
        w.U8(1);
        w.I32(4_000);
        w.I32(4_000);
        w.I32(40_000);
        w.I32(5_000);
        w.Text(text);
        w.Ident(LayoutSamples.Font);
        w.I32(2_400);
        w.Color(255, 255, 255, 255);
        w.U8(0);
        w.U8(0);
        w.U8(0);
        w.I32(0);
        w.I32(100);
        w.I32(800);
        w.Color(0, 0, 0, 255);
        w.U16(0);
        w.Color(0, 0, 0, 153);
        w.I32(0);
        w.I32(0);
        w.U8(1);
    }

    /// <summary>One served profile's fields; the defaults are the rich sample's served form.</summary>
    internal sealed class ServedSpec
    {
        public byte[] Magic { get; set; } = "AFSP"u8.ToArray();

        public ushort Version { get; set; } = 1;

        public byte[] Marker { get; set; } = ReferenceServed.Marker.ToArray();

        public string Name { get; set; } = Samples.Name;

        public int CanvasWidth { get; set; } = 128_000;

        public int CanvasHeight { get; set; } = 72_000;

        public byte BackgroundMode { get; set; } = 4;

        public byte BackgroundImage { get; set; }

        public List<Action<ReferenceLayout.Bytes>> Items { get; set; } = RichItems();

        public uint? ItemCount { get; set; }

        /// <summary>The images, in index order: the rich sample's PNG (the lower asset id), then its JPEG.</summary>
        public List<(byte Format, uint Width, uint Height)> Images { get; set; } = [(1, 640, 480), (2, 1920, 1080)];

        public uint? ImageCount { get; set; }

        public byte[] Trailing { get; set; } = [];
    }
}
