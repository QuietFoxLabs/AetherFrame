using System;
using System.Linq;
using AetherFrame.Protocol.Encoding;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>The encoding primitives: fixed widths, big-endian, length prefixes, strict text, and limits checked before allocation.</summary>
public class CanonicalEncodingTests
{
    [Fact]
    public void Integers_AreBigEndianFixedWidth()
    {
        var writer = new CanonicalWriter();
        writer.WriteU8(0xAB);
        writer.WriteU16(0x0102);
        writer.WriteU32(0x01020304);
        writer.WriteU64(0x0102030405060708);

        Assert.Equal("ab0102010203040102030405060708", Hex.Of(writer.ToArray()));

        var reader = new CanonicalReader(writer.ToArray());
        Assert.Equal(0xAB, reader.ReadU8("a"));
        Assert.Equal(0x0102, reader.ReadU16("b"));
        Assert.Equal(0x01020304u, reader.ReadU32("c"));
        Assert.Equal(0x0102030405060708ul, reader.ReadU64("d"));
        reader.ExpectEnd("The probe");
    }

    [Fact]
    public void LengthPrefixed_WritesFourByteLengthThenBytes()
    {
        var writer = new CanonicalWriter();
        writer.WriteLengthPrefixed([0xDE, 0xAD]);
        Assert.Equal("00000002dead", Hex.Of(writer.ToArray()));

        var reader = new CanonicalReader(writer.ToArray());
        Assert.Equal(new byte[] { 0xDE, 0xAD }, reader.ReadLengthPrefixed(2, "bytes").ToArray());
        reader.ExpectEnd("The probe");
    }

    [Fact]
    public void Text_IsUtf8WithoutBomAndRoundTripsExactly()
    {
        const string text = "Plate \u00e9\u65e5\U0001F600 \r\n\t end";
        var writer = new CanonicalWriter();
        writer.WriteText(text, "name");
        var bytes = writer.ToArray();

        Assert.Equal(new byte[] { 0, 0, 0, (byte)(bytes.Length - 4) }, bytes.Take(4));
        Assert.NotEqual(0xEF, bytes[4]);
        var reader = new CanonicalReader(bytes);
        Assert.Equal(text, reader.ReadText("name"));
        reader.ExpectEnd("The probe");
    }

    [Fact]
    public void Text_RefusesNulAndUnpairedSurrogatesOnTheWayOut()
    {
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => new CanonicalWriter().WriteText("a\0b", "name"));
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => new CanonicalWriter().WriteText("a\ud83d", "name"));
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => new CanonicalWriter().WriteText("\ude00b", "name"));
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => new CanonicalWriter().WriteText("\ude00\ud83d", "name"));
    }

    [Fact]
    public void Text_RefusesMalformedUtf8AndNulOnTheWayIn()
    {
        Assert.Equal(ProtocolError.InvalidText, Decode([0xC0, 0xAF]).Error);          // overlong '/'
        Assert.Equal(ProtocolError.InvalidText, Decode([0xED, 0xA0, 0x80]).Error);    // encoded surrogate
        Assert.Equal(ProtocolError.InvalidText, Decode([0xF4, 0x90, 0x80, 0x80]).Error); // above U+10FFFF
        Assert.Equal(ProtocolError.InvalidText, Decode([0xE2, 0x82]).Error);          // truncated sequence
        Assert.Equal(ProtocolError.InvalidText, Decode([0xFF]).Error);
        Assert.Equal(ProtocolError.InvalidText, Decode([0x80]).Error);                // lone continuation
        Assert.Equal(ProtocolError.InvalidText, Decode([0x61, 0x00, 0x62]).Error);
    }

    [Fact]
    public void Text_CountsScalarsNotCodeUnits()
    {
        var fourByte = string.Concat(Enumerable.Repeat("\U0001F600", ProtocolLimits.MaxTextScalars));
        Assert.Equal(ProtocolLimits.MaxTextScalars * 2, fourByte.Length);
        var encoded = ProtocolText.Encode(fourByte, "name");
        Assert.Equal(ProtocolLimits.MaxTextBytes, encoded.Length);
        Assert.Equal(fourByte, ProtocolText.Decode(encoded, "name"));

        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolText.Encode(fourByte + "\U0001F600", "name"));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolText.Encode(new string('a', ProtocolLimits.MaxTextScalars + 1), "name"));
        Assert.Equal(ProtocolLimits.MaxTextScalars, ProtocolText.Decode(ProtocolText.Encode(new string('a', ProtocolLimits.MaxTextScalars), "name"), "name").Length);
    }

    [Fact]
    public void Text_DecoderRefusesOverTheLimits_InReadingOrder()
    {
        // The general text rules (section 2.3), which no version 1 field uses since the name rule
        // (decision D4) and which schema 2's texts will: the byte limit, UTF-8, U+0000, the scalar limit.
        var overScalars = System.Text.Encoding.UTF8.GetBytes(new string('a', ProtocolLimits.MaxTextScalars + 1));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolText.Decode(overScalars, "text"));

        var overBytes = new byte[ProtocolLimits.MaxTextBytes + 1];
        overBytes.AsSpan().Fill((byte)'a');
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolText.Decode(overBytes, "text"));

        byte[] nulAfterTooMany = [.. overScalars, 0x00];
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => ProtocolText.Decode(nulAfterTooMany, "text"));

        // A declared length over the limit is refused before the input's own length is consulted.
        byte[] declared = [0x00, 0x01, 0xF4, 0x01, (byte)'a'];
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => new CanonicalReader(declared).ReadText("text"));

        var atLimit = System.Text.Encoding.UTF8.GetBytes(new string('a', ProtocolLimits.MaxTextScalars));
        Assert.Equal(ProtocolLimits.MaxTextScalars, ProtocolText.Decode(atLimit, "text").Length);
    }

    [Fact]
    public void Reader_RefusesDeclaredLengthOverLimitBeforeLookingAtRemainingInput()
    {
        byte[] huge = [0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => { var r = new CanonicalReader(huge); r.ReadLengthPrefixed(16, "bytes"); });
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => { var r = new CanonicalReader(huge); r.ReadCount(8, "items"); });

        byte[] short8 = [0x00, 0x00, 0x00, 0x08, 0x00];
        ProtocolAssert.Throws(ProtocolError.Truncated, () => { var r = new CanonicalReader(short8); r.ReadLengthPrefixed(16, "bytes"); });
    }

    [Fact]
    public void Reader_RefusesTruncationAndTrailingBytes()
    {
        ProtocolAssert.Throws(ProtocolError.Truncated, () => { var r = new CanonicalReader([0x01]); r.ReadU16("x"); });
        ProtocolAssert.Throws(ProtocolError.Truncated, () => { var r = new CanonicalReader([]); r.ReadU8("x"); });
        ProtocolAssert.Throws(ProtocolError.TrailingBytes, () => { var r = new CanonicalReader([0x01]); r.ExpectEnd("The probe"); });
    }

    private static ProtocolException Decode(byte[] utf8) => Assert.Throws<ProtocolException>(() => ProtocolText.Decode(utf8, "name"));
}
