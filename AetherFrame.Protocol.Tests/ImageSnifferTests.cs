using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Section 8.2.1, the rule every consumer of a shared image applies to its bytes before decoding
/// anything: a non-animated, non-interlaced 8-bit truecolour PNG with only IHDR, PLTE, IDAT and
/// IEND for critical chunks, or a JPEG whose one frame is SOF0 to SOF2, 8-bit, with 1 or 3
/// components, in at most 64 scans, holding only the markers such a JPEG needs; the first fault
/// in reading order (section 9.1); nothing decoded. The vectors in
/// Fixtures/image-vectors-v1.json each hold the outcome their case states, which the sniffer must
/// give; they are regenerated only on purpose, with AETHERFRAME_PROTOCOL_REGENERATE_IMAGE_VECTORS=1.
/// </summary>
public class ImageSnifferTests
{
    [Theory]
    [InlineData(6, 3, 2)]
    [InlineData(2, 8192, 1)]
    public void APng_OfTruecolourWithOrWithoutAlpha_IsReadForItsSize(byte colourType, int width, int height)
    {
        Assert.Equal(new SniffedImage(ImageFormat.Png, width, height), ImageSniffer.Sniff(Png(width, height, colourType)));
    }

    [Fact]
    public void APng_RefusesWhatTheRuleRefuses()
    {
        Refused(ProtocolError.InvalidValue, Png(2, 2, depth: 16));
        Refused(ProtocolError.InvalidValue, Png(2, 2, colourType: 3));
        Refused(ProtocolError.InvalidValue, Png(2, 2, colourType: 0));
        Refused(ProtocolError.InvalidValue, Png(2, 2, colourType: 4));
        Refused(ProtocolError.InvalidValue, Png(2, 2, interlace: 1));
        Refused(ProtocolError.InvalidValue, Png(2, 2, compression: 1));
        Refused(ProtocolError.InvalidValue, Png(2, 2, filter: 1));
        Refused(ProtocolError.InvalidValue, Png(0, 2));
        Refused(ProtocolError.InvalidValue, Png(2, 0));
        Refused(ProtocolError.LimitExceeded, Png(8193, 1));
        Refused(ProtocolError.LimitExceeded, Png(5000, 4001));

        // The chunks around the header.
        Refused(ProtocolError.InvalidValue, PngFrom(Chunk("tEXt", KeyedText()), Ihdr(2, 2), Idat(), Chunk("IEND")));
        Refused(ProtocolError.InvalidValue, PngFrom(Chunk("IHDR", new byte[12]), Idat(), Chunk("IEND")));
        Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(2, 2), Ihdr(2, 2), Idat(), Chunk("IEND")));
        foreach (var animation in new[] { "acTL", "fcTL", "fdAT" })
        {
            Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(2, 2), Chunk(animation, new byte[8]), Idat(), Chunk("IEND")));
            Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(2, 2), Idat(), Chunk(animation, new byte[8]), Chunk("IEND")));
        }

        // Only IHDR, PLTE, IDAT and IEND among critical chunks, and no compressed ancillary chunk.
        foreach (var type in new[] { "CgBI", "XYZW", "iCCP", "zTXt", "iTXt" })
        {
            Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(2, 2), Chunk(type, new byte[4]), Idat(), Chunk("IEND")));
        }

        // A PLTE is one, of 1 to 256 entries, before the image data.
        Assert.Equal(new SniffedImage(ImageFormat.Png, 2, 2), ImageSniffer.Sniff(PngFrom(Ihdr(2, 2), Chunk("PLTE", new byte[768]), Idat(), Chunk("IEND"))));
        foreach (var length in new[] { 0, 4, 771 })
        {
            Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(2, 2), Chunk("PLTE", new byte[length]), Idat(), Chunk("IEND")));
        }

        Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(2, 2), Chunk("PLTE", new byte[3]), Chunk("PLTE", new byte[3]), Idat(), Chunk("IEND")));
        Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(2, 2), Idat(), Chunk("PLTE", new byte[3]), Chunk("IEND")));

        Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(2, 2), Idat(), Chunk("tIME", new byte[7]), Idat(), Chunk("IEND")));
        Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(2, 2), Chunk("IEND")));
        Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(2, 2), Idat(), Chunk("IEND", new byte[1])));
        Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(2, 2), Idat(), Chunk("IE1D")));
        Refused(ProtocolError.TrailingBytes, [.. Png(2, 2), 0x00]);
        Refused(ProtocolError.TrailingBytes, [.. Png(2, 2), .. Png(2, 2)]);
        Refused(ProtocolError.Truncated, Png(2, 2)[..^1]);
        Refused(ProtocolError.Truncated, PngFrom(Ihdr(2, 2), Idat())[..^4]);

        // A length over 2^31 - 1 is over PNG's own limit; one within it that runs past the bytes is
        // a chunk running past the end.
        Refused(ProtocolError.LimitExceeded, WithIdatLength(0x8000_0000));
        Refused(ProtocolError.Truncated, WithIdatLength(0x7FFF_FFFF));

        // Other ancillary chunks are skipped: a copy's own inventory is its producer's stricter check.
        Assert.Equal(ImageFormat.Png, ImageSniffer.Sniff(PngFrom(Ihdr(2, 2), Chunk("tEXt", KeyedText()), Idat(), Idat(), Chunk("IEND"))).Format);
    }

    [Theory]
    [InlineData(0xC0, 3)]
    [InlineData(0xC1, 3)]
    [InlineData(0xC2, 3)]
    [InlineData(0xC0, 1)]
    public void AJpeg_OfOneBaselineExtendedOrProgressiveFrame_IsReadForItsSize(byte frame, int components)
    {
        Assert.Equal(new SniffedImage(ImageFormat.Jpeg, 640, 480), ImageSniffer.Sniff(Jpeg(640, 480, frame, components)));
    }

    [Fact]
    public void AJpegsImageData_MayHoldStuffedBytesAndRestartMarkers_AndAProgressiveOneMoreScans()
    {
        var progressive = JpegFrom(App0(), Dqt(), Sof(0xC2, 16, 16, 3), Dht(), Sos(3), Entropy(0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD3, 0x56), Dht(), Sos(1), Entropy(0x78, 0xFF, 0xFF, 0xD7, 0x9A), Eoi());
        Assert.Equal(new SniffedImage(ImageFormat.Jpeg, 16, 16), ImageSniffer.Sniff(progressive));

        // Fill bytes before a marker are allowed.
        var filled = JpegFrom(App0(), [0xFF, 0xFF, .. Dqt()], Sof(0xC0, 8, 8, 1), Dht(), Sos(1), Entropy(0x01), Eoi());
        Assert.Equal(new SniffedImage(ImageFormat.Jpeg, 8, 8), ImageSniffer.Sniff(filled));

        // Every allowed marker: APP1 to APP15, COM and DRI among them.
        Assert.Equal(new SniffedImage(ImageFormat.Jpeg, 8, 8), ImageSniffer.Sniff(Annotated()));
    }

    [Fact]
    public void AJpeg_HasAtMost64Scans()
    {
        Assert.Equal(new SniffedImage(ImageFormat.Jpeg, 8, 8), ImageSniffer.Sniff(ProgressiveWithScans(ProtocolLimits.MaxJpegScans)));
        Refused(ProtocolError.LimitExceeded, ProgressiveWithScans(ProtocolLimits.MaxJpegScans + 1));
    }

    [Fact]
    public void AJpeg_RefusesWhatTheRuleRefuses()
    {
        // Every marker outside the allowed ones, reserved codes among them.
        foreach (var code in new byte[] { 0x02, 0x40, 0xBF, 0xC3, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF, 0xDC, 0xDE, 0xDF, 0xF0, 0xF7, 0xFD })
        {
            Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Segment(code, new byte[9]), Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        }

        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), [0xFF, 0x01], Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), [0xFF, 0xD8], Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), [0xFF, 0xD0], Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Dht(), Sos(3), Entropy(0x01), Sof(0xC0, 8, 8, 3), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Sof(0xC0, 8, 8, 3), Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Sof(0xC1, 8, 8, 3, precision: 12), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Sof(0xC0, 8, 8, 2), Dht(), Sos(2), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Sof(0xC0, 8, 8, 4), Dht(), Sos(4), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Segment(0xC0, [8, 0, 8, 0, 8, 1, 1, 0x11, 0, 0]), Dht(), Sos(1), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Segment(0xC0, [8, 0, 8]), Dht(), Sos(1), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Sof(0xC0, 0, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.LimitExceeded, JpegFrom(App0(), Sof(0xC0, 8, 8193, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Sof(0xC0, 8, 8, 3), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), [0xFF, 0x00], Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), [0xFF, 0xE1, 0x00, 0x01], Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), [0x12], Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.Truncated, JpegFrom(App0(), Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01)));
        Refused(ProtocolError.Truncated, JpegFrom(App0(), [0xFF, 0xE1, 0x10, 0x00]));
        Refused(ProtocolError.TrailingBytes, [.. Jpeg(8, 8), 0x00]);

        // A second JPEG appended to the first ends in FF D9 too: the walk stops at the first end.
        Refused(ProtocolError.TrailingBytes, [.. Jpeg(8, 8), .. Jpeg(8, 8)]);
    }

    [Fact]
    public void AnInputWithTwoFaults_IsRefusedForTheFirstInReadingOrder()
    {
        // A JPEG marker's rules come before its segment is framed.
        Refused(ProtocolError.InvalidValue, [0xFF, 0xD8, 0xFF, 0xC3, 0x00, 0x10]);
        Refused(ProtocolError.InvalidValue, [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x10]);
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Sof(0xC0, 8, 8, 3), [0xFF, 0xC0, 0x00, 0x20]));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), [0xFF, 0x02, 0x00, 0x20]));
        Refused(ProtocolError.LimitExceeded, [.. ProgressiveWithScans(ProtocolLimits.MaxJpegScans)[..^2], 0xFF, 0xDA, 0x00, 0x20]);

        // A frame's fields in the order read: precision, height, width, their product, then the components.
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Sof(0xC1, 9000, 8, 3, precision: 12), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.LimitExceeded, JpegFrom(App0(), Sof(0xC0, 9000, 8, 4), Dht(), Sos(4), Entropy(0x01), Eoi()));
        Refused(ProtocolError.LimitExceeded, JpegFrom(App0(), Sof(0xC0, 9000, 0, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.InvalidValue, JpegFrom(App0(), Sof(0xC0, 0, 9000, 3), Dht(), Sos(3), Entropy(0x01), Eoi()));
        Refused(ProtocolError.LimitExceeded, JpegFrom(App0(), Sof(0xC0, 5000, 4001, 4), Dht(), Sos(4), Entropy(0x01), Eoi()));
        Refused(ProtocolError.LimitExceeded, JpegFrom(App0(), Segment(0xC0, [8, 0x23, 0x28, 0, 8]), Dht(), Sos(1), Entropy(0x01), Eoi()));

        // A PNG chunk's length limit before its type, its type's rules before its data, and its
        // header's fields in order: width, height, their product, then the rest.
        Refused(ProtocolError.LimitExceeded, [.. PngFrom(Ihdr(2, 2)), .. ChunkHeader(0x8000_0000, "1234")]);
        Refused(ProtocolError.InvalidValue, PngFrom(ChunkHeader(256, "tEXt"), new byte[10]));
        Refused(ProtocolError.InvalidValue, [.. PngFrom(Ihdr(2, 2)), .. ChunkHeader(64, "acTL")]);
        Refused(ProtocolError.InvalidValue, PngFrom(ChunkHeader(14, "IHDR"), new byte[13]));
        Refused(ProtocolError.LimitExceeded, PngFrom(Ihdr(9000, 0), Idat(), Chunk("IEND")));
        Refused(ProtocolError.InvalidValue, PngFrom(Ihdr(0, 9000), Idat(), Chunk("IEND")));
        Refused(ProtocolError.LimitExceeded, PngFrom(Ihdr(5000, 4001, depth: 16), Idat(), Chunk("IEND")));
    }

    [Fact]
    public void Bytes_ThatAreNeitherOrTooMany_AreRefused()
    {
        Refused(ProtocolError.InvalidLength, []);
        Refused(ProtocolError.InvalidValue, System.Text.Encoding.ASCII.GetBytes("GIF89a"));
        Refused(ProtocolError.InvalidValue, [0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50]);
        Refused(ProtocolError.InvalidValue, [0xFF]);
        var tooMany = new byte[ProtocolLimits.MaxImageBytes + 1];
        Png(2, 2).CopyTo(tooMany, 0);
        Refused(ProtocolError.LimitExceeded, tooMany);
    }

    [Fact]
    public void CheckDeclared_WantsTheDeclaredLengthAndDigestFirst_ThenTheRule_ThenTheDeclaredFormatAndSize()
    {
        var png = Png(4, 3);
        Assert.Equal(new SniffedImage(ImageFormat.Png, 4, 3), ImageSniffer.CheckDeclared(png, Declare(png, ImageFormat.Png, 4, 3)));

        RefusedBy(ProtocolError.InvalidValue, () => ImageSniffer.CheckDeclared(png, Declare(png, ImageFormat.Jpeg, 4, 3)));
        RefusedBy(ProtocolError.InvalidValue, () => ImageSniffer.CheckDeclared(png, Declare(png, ImageFormat.Png, 3, 4)));
        RefusedBy(ProtocolError.InvalidValue, () => ImageSniffer.CheckDeclared(png, new ImageReference(AssetId.NewId(), SHA256.HashData(png), ImageFormat.Png, png.Length + 1, 4, 3)));
        RefusedBy(ProtocolError.InvalidValue, () => ImageSniffer.CheckDeclared(png, new ImageReference(AssetId.NewId(), SHA256.HashData([1]), ImageFormat.Png, png.Length, 4, 3)));

        // The digest is checked before the structure: bytes that match no declaration say so first.
        var broken = png[..^1];
        RefusedBy(ProtocolError.InvalidValue, () => ImageSniffer.CheckDeclared(broken, Declare(png, ImageFormat.Png, 4, 3)));
        RefusedBy(ProtocolError.Truncated, () => ImageSniffer.CheckDeclared(broken, Declare(broken, ImageFormat.Png, 4, 3)));

        // And the structure before the declared format: bytes that match their digest but break
        // the rule say so, whatever format they claim.
        RefusedBy(ProtocolError.Truncated, () => ImageSniffer.CheckDeclared(broken, Declare(broken, ImageFormat.Jpeg, 4, 3)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SeededMutations_EndInAProtocolExceptionOrAnImageWithinTheLimits_NeverAnythingElse(int seed)
    {
        var bases = new[] { Png(3, 2), Png(64, 1, colourType: 2), Jpeg(8, 8), Jpeg(16, 16, 0xC2, 1), Annotated() };
        var random = new Random(seed * 1_000_003);
        for (var index = 0; index < 20_000; index++)
        {
            var bytes = bases[random.Next(bases.Length)].ToArray();
            var mutations = 1 + random.Next(4);
            for (var mutation = 0; mutation < mutations; mutation++)
            {
                switch (random.Next(4))
                {
                    case 0:
                        bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                        break;
                    case 1:
                        bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                        break;
                    case 2:
                        bytes = bytes[..Math.Max(1, bytes.Length - 1 - random.Next(bytes.Length / 2))];
                        break;
                    default:
                        var insert = random.Next(bytes.Length);
                        bytes = [.. bytes[..insert], (byte)random.Next(256), .. bytes[insert..]];
                        break;
                }
            }

            try
            {
                var sniffed = ImageSniffer.Sniff(bytes);
                Assert.InRange(sniffed.Width, 1, ProtocolLimits.MaxImageDimension);
                Assert.InRange(sniffed.Height, 1, ProtocolLimits.MaxImageDimension);
                Assert.True((long)sniffed.Width * sniffed.Height <= ProtocolLimits.MaxImagePixels);
                Assert.True(sniffed.Format is ImageFormat.Png or ImageFormat.Jpeg);
            }
            catch (ProtocolException)
            {
            }
        }
    }

    [Fact]
    public void TheVectors_HoldWhatTheirCasesState_AndTheSnifferSaysTheSame()
    {
        var vectors = VectorCases();
        foreach (var (name, bytes, expected) in vectors)
        {
            Assert.True(expected == Outcome(bytes), $"{name}: expected {expected}, the sniffer says {Outcome(bytes)}");
        }

        // One newline on every platform: the fixture is compared byte for byte, on Windows and Linux alike.
        var options = new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" };
        var actual = JsonSerializer.Serialize(vectors.Select(v => new ImageVector(v.Name, Convert.ToHexString(v.Bytes).ToLowerInvariant(), v.Expected)).ToList(), options) + "\n";
        var fixture = "image-vectors-v1.json";
        if (Environment.GetEnvironmentVariable("AETHERFRAME_PROTOCOL_REGENERATE_IMAGE_VECTORS") is { Length: > 0 })
        {
            var source = Path.Combine(VectorPaths.SourceFixtures(), fixture);
            File.WriteAllText(source, actual);
            Assert.Equal(actual, File.ReadAllText(source));
            return;
        }

        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)), actual);
        Assert.Contains(vectors, v => v.Expected.StartsWith("png", StringComparison.Ordinal));
        Assert.Contains(vectors, v => v.Expected.StartsWith("jpeg", StringComparison.Ordinal));
        foreach (var error in new[] { ProtocolError.InvalidLength, ProtocolError.InvalidValue, ProtocolError.LimitExceeded, ProtocolError.Truncated, ProtocolError.TrailingBytes })
        {
            Assert.Contains(vectors, v => v.Expected == error.ToString());
        }
    }

    private sealed record ImageVector(string Name, string Hex, string Expected);

    private static IReadOnlyList<(string Name, byte[] Bytes, string Expected)> VectorCases() =>
    [
        ("png-rgba-3x2", Png(3, 2), "png 3x2"),
        ("png-rgb-1x1", Png(1, 1, colourType: 2), "png 1x1"),
        ("png-with-text-chunk", PngFrom(Ihdr(2, 2), Chunk("tEXt", KeyedText()), Idat(), Chunk("IEND")), "png 2x2"),
        ("png-16-bit", Png(2, 2, depth: 16), "InvalidValue"),
        ("png-palette", Png(2, 2, colourType: 3), "InvalidValue"),
        ("png-greyscale", Png(2, 2, colourType: 0), "InvalidValue"),
        ("png-interlaced", Png(2, 2, interlace: 1), "InvalidValue"),
        ("png-animated", PngFrom(Ihdr(2, 2), Chunk("acTL", new byte[8]), Idat(), Chunk("IEND")), "InvalidValue"),
        ("png-idat-split", PngFrom(Ihdr(2, 2), Idat(), Chunk("tIME", new byte[7]), Idat(), Chunk("IEND")), "InvalidValue"),
        ("png-after-iend", Join(Png(2, 2), [0x00]), "TrailingBytes"),
        ("png-truncated", Png(2, 2)[..^1], "Truncated"),
        ("png-width-8193", Png(8193, 1), "LimitExceeded"),
        ("png-over-20-mp", Png(5000, 4001), "LimitExceeded"),
        ("jpeg-baseline", Jpeg(640, 480), "jpeg 640x480"),
        ("jpeg-extended", Jpeg(640, 480, 0xC1), "jpeg 640x480"),
        ("jpeg-progressive-greyscale", Jpeg(16, 16, 0xC2, 1), "jpeg 16x16"),
        ("jpeg-lossless", Jpeg(8, 8, 0xC3), "InvalidValue"),
        ("jpeg-arithmetic", Jpeg(8, 8, 0xC9), "InvalidValue"),
        ("jpeg-12-bit", JpegFrom(App0(), Sof(0xC1, 8, 8, 3, precision: 12), Dht(), Sos(3), Entropy(0x01), Eoi()), "InvalidValue"),
        ("jpeg-dnl-height", JpegFrom(App0(), Sof(0xC0, 0, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()), "InvalidValue"),
        ("jpeg-two-components", JpegFrom(App0(), Sof(0xC0, 8, 8, 2), Dht(), Sos(2), Entropy(0x01), Eoi()), "InvalidValue"),
        ("jpeg-second-frame", JpegFrom(App0(), Sof(0xC0, 8, 8, 3), Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()), "InvalidValue"),
        ("jpeg-two-appended", Join(Jpeg(8, 8), Jpeg(8, 8)), "TrailingBytes"),
        ("jpeg-no-end", JpegFrom(App0(), Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01)), "Truncated"),
        ("empty", Array.Empty<byte>(), "InvalidLength"),
        ("gif", System.Text.Encoding.ASCII.GetBytes("GIF89a"), "InvalidValue"),
        ("png-with-palette", PngFrom(Ihdr(2, 2), Chunk("PLTE", new byte[6]), Idat(), Chunk("IEND")), "png 2x2"),
        ("png-unknown-critical-chunk", PngFrom(Ihdr(2, 2), Chunk("CgBI", new byte[4]), Idat(), Chunk("IEND")), "InvalidValue"),
        ("png-colour-profile", PngFrom(Ihdr(2, 2), Chunk("iCCP", new byte[4]), Idat(), Chunk("IEND")), "InvalidValue"),
        ("png-chunk-length-over-2-31", WithIdatLength(0x8000_0000), "LimitExceeded"),
        ("jpeg-allowed-markers", Annotated(), "jpeg 8x8"),
        ("jpeg-reserved-marker", JpegFrom(App0(), Segment(0x02, new byte[4]), Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01), Eoi()), "InvalidValue"),
        ("jpeg-64-scans", ProgressiveWithScans(64), "jpeg 8x8"),
        ("jpeg-65-scans", ProgressiveWithScans(65), "LimitExceeded"),
    ];

    private static string Outcome(byte[] bytes)
    {
        try
        {
            var sniffed = ImageSniffer.Sniff(bytes);
            return $"{(sniffed.Format == ImageFormat.Png ? "png" : "jpeg")} {sniffed.Width}x{sniffed.Height}";
        }
        catch (ProtocolException e)
        {
            return e.Error.ToString();
        }
    }

    private static void Refused(ProtocolError error, byte[] bytes) => RefusedBy(error, () => ImageSniffer.Sniff(bytes));

    private static void RefusedBy(ProtocolError error, Func<SniffedImage> read) => Assert.Equal(error, Assert.Throws<ProtocolException>(() => read()).Error);

    private static ImageReference Declare(byte[] bytes, ImageFormat format, int width, int height) => new(AssetId.NewId(), SHA256.HashData(bytes), format, bytes.Length, width, height);

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Ascii(string text) => System.Text.Encoding.ASCII.GetBytes(text);

    /// <summary>A tEXt chunk's data: the keyword "k", its zero separator, and the text "v".</summary>
    private static byte[] KeyedText() => [(byte)'k', 0, (byte)'v'];

    // PNG: the signature, then chunks. CRCs are written correctly, though the rule doesn't check them.
    private static byte[] Png(int width, int height, byte colourType = 6, byte depth = 8, byte compression = 0, byte filter = 0, byte interlace = 0) =>
        PngFrom(Ihdr(width, height, depth, colourType, compression, filter, interlace), Idat(), Chunk("IEND"));

    private static byte[] PngFrom(params byte[][] chunks) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. chunks.SelectMany(c => c)];

    private static byte[] Ihdr(int width, int height, byte depth = 8, byte colourType = 6, byte compression = 0, byte filter = 0, byte interlace = 0)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)height);
        (data[8], data[9], data[10], data[11], data[12]) = (depth, colourType, compression, filter, interlace);
        return Chunk("IHDR", data);
    }

    /// <summary>An IDAT holding a fixed zlib stream (nine zero bytes, deflated), so the vectors never depend on a runtime's compressor.</summary>
    private static byte[] Idat() => Chunk("IDAT", Convert.FromHexString("78DA636080020000090001"));

    /// <summary>A PNG of 2 by 2 whose IDAT chunk declares <paramref name="length"/> bytes.</summary>
    private static byte[] WithIdatLength(uint length)
    {
        var png = PngFrom(Ihdr(2, 2), Idat(), Chunk("IEND"));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8 + 25), length);
        return png;
    }

    /// <summary>A chunk's length and type, with nothing after them.</summary>
    private static byte[] ChunkHeader(uint length, string type)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, length);
        Ascii(type).CopyTo(header, 4);
        return header;
    }

    private static byte[] Chunk(string type, byte[]? data = null)
    {
        data ??= [];
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Ascii(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
            }
        }

        return ~crc;
    }

    // JPEG: start of image, then marker segments; the image data is never decoded, so it is arbitrary.
    private static byte[] Jpeg(int width, int height, byte frame = 0xC0, int components = 3) =>
        JpegFrom(App0(), Dqt(), Sof(frame, height, width, components), Dht(), Sos(components), Entropy(0x12, 0x34), Eoi());

    private static byte[] JpegFrom(params byte[][] parts) => [0xFF, 0xD8, .. parts.SelectMany(p => p)];

    /// <summary>A progressive greyscale JPEG of 8 by 8 in <paramref name="count"/> scans.</summary>
    private static byte[] ProgressiveWithScans(int count) =>
        JpegFrom([App0(), Dqt(), Sof(0xC2, 8, 8, 1), Dht(), .. Enumerable.Range(0, count).Select(_ => Join(Sos(1), Entropy(0x01))), Eoi()]);

    /// <summary>A baseline JPEG holding every kind of segment the rule allows besides the frame and scan: APP1, APP15, COM, DQT, DRI and DHT.</summary>
    private static byte[] Annotated() =>
        JpegFrom(App0(), Segment(0xE1, [.. Ascii("Exif"), 0, 0]), Segment(0xEF, [1]), Segment(0xFE, Ascii("comment")), Dqt(), Segment(0xDD, [0, 4]), Sof(0xC0, 8, 8, 3), Dht(), Sos(3), Entropy(0x01, 0xFF, 0xD0, 0x02), Eoi());

    private static byte[] Segment(byte code, byte[] body)
    {
        var segment = new byte[4 + body.Length];
        (segment[0], segment[1]) = (0xFF, code);
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(2 + body.Length));
        body.CopyTo(segment, 4);
        return segment;
    }

    private static byte[] App0() => Segment(0xE0, [.. Ascii("JFIF"), 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);

    private static byte[] Dqt() => Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);

    private static byte[] Dht() => Segment(0xC4, [0x00, 1, .. new byte[15], 0x00]);

    private static byte[] Sof(byte code, int height, int width, int components, byte precision = 8)
    {
        var body = new byte[6 + (3 * components)];
        body[0] = precision;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3), (ushort)width);
        body[5] = (byte)components;
        for (var component = 0; component < components; component++)
        {
            (body[6 + (3 * component)], body[7 + (3 * component)], body[8 + (3 * component)]) = ((byte)(component + 1), 0x11, 0);
        }

        return Segment(code, body);
    }

    private static byte[] Sos(int components)
    {
        var body = new List<byte> { (byte)components };
        for (var component = 0; component < components; component++)
        {
            body.Add((byte)(component + 1));
            body.Add(0);
        }

        body.AddRange(new byte[] { 0, 63, 0 });
        return Segment(0xDA, body.ToArray());
    }

    private static byte[] Entropy(params byte[] data) => data;

    private static byte[] Eoi() => [0xFF, 0xD9];
}
