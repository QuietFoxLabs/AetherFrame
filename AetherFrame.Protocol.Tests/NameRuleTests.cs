using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Remote;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// The name rule, decision D4 (docs/networking/DecisionRegister.md): 1 to 64 scalars in at most
/// 256 bytes, no refused code point, nothing normalized, and faults reported in reading order.
/// Every Unicode scalar value is checked against a table written from the register entry, so a
/// range that is too wide, too narrow or has a hole anywhere fails here. Special characters are
/// built from their code points, never written raw or as escapes in this file.
/// </summary>
public class NameRuleTests
{
    // Every refused range, from decision D4, as (first, last). Written from the register entry,
    // independently of ProtocolName.IsRefused.
    private static readonly (int First, int Last)[] RefusedRanges =
    [
        (0x0000, 0x001F), // C0 controls
        (0x007F, 0x009F), // DEL and the C1 controls
        (0x00AD, 0x00AD), // soft hyphen
        (0x061C, 0x061C), // ARABIC LETTER MARK
        (0x180E, 0x180E), // MONGOLIAN VOWEL SEPARATOR
        (0x200B, 0x200D), // zero width space, non-joiner, joiner
        (0x200E, 0x200F), // LRM, RLM
        (0x2028, 0x2028), // LINE SEPARATOR
        (0x2029, 0x2029), // PARAGRAPH SEPARATOR
        (0x202A, 0x202E), // LRE, RLE, PDF, LRO, RLO
        (0x2060, 0x2064), // word joiner and invisible operators
        (0x2066, 0x2069), // LRI, RLI, FSI, PDI
        (0x206A, 0x206F), // deprecated format characters
        (0xFEFF, 0xFEFF), // byte order mark
        (0xFFF9, 0xFFFB), // interlinear annotation
        (0xE0001, 0xE0001), // LANGUAGE TAG
        (0xE0020, 0xE007F), // tag characters
    ];

    public static TheoryData<int> RefusedEdges()
    {
        var data = new TheoryData<int>();
        foreach (var scalar in RefusedRanges.SelectMany(r => new[] { r.First, r.Last }).Distinct())
        {
            data.Add(scalar);
        }

        return data;
    }

    public static TheoryData<int> AllowedNeighbours()
    {
        // Every neighbour outside the table, assigned or not: the rule is fixed code points, so an
        // unassigned code point next to a range is allowed and must stay allowed.
        var data = new TheoryData<int>();
        var neighbours = RefusedRanges
            .SelectMany(r => new[] { r.First - 1, r.Last + 1 })
            .Where(scalar => scalar >= 0 && !IsSurrogate(scalar) && !InTable(scalar))
            .Distinct();
        foreach (var scalar in neighbours)
        {
            data.Add(scalar);
        }

        return data;
    }

    [Fact]
    public void EveryScalarValue_IsRefusedExactlyWhenTheTableSays_WrittenAndRead()
    {
        var refused = 0;
        for (var scalar = 0; scalar <= 0x10FFFF; scalar++)
        {
            if (IsSurrogate(scalar))
            {
                continue;
            }

            var expected = InTable(scalar);
            Assert.True(expected == ProtocolName.IsRefused(scalar), $"U+{scalar:X4}: the table says {(expected ? "refused" : "allowed")}.");

            var name = "a" + S(scalar);
            var bytes = Utf8(name);
            if (expected)
            {
                refused++;
                Assert.Equal(ProtocolError.InvalidText, Assert.Throws<ProtocolException>(() => ProtocolName.Encode(name, "name")).Error);
                Assert.Equal(ProtocolError.InvalidText, Assert.Throws<ProtocolException>(() => ProtocolName.Decode(bytes, "name")).Error);
            }
            else
            {
                Assert.Equal(bytes, ProtocolName.Encode(name, "name"));
                Assert.Equal(name, ProtocolName.Decode(bytes, "name"));
            }
        }

        Assert.Equal(RefusedRanges.Sum(r => r.Last - r.First + 1), refused);
    }

    [Theory]
    [MemberData(nameof(RefusedEdges))]
    public void EveryRefusedRange_IsRefusedAtBothEdges_ThroughTheSnapshot(int scalar)
    {
        var name = "a" + S(scalar) + "b";
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, name, []));
        using var signer = TestPersonas.CreateA();
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => SignedDocumentCodec.Verify(PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, PayloadBuilder.Snapshot(nameBytes: Utf8(name)))));
    }

    [Theory]
    [MemberData(nameof(AllowedNeighbours))]
    public void TheNeighboursOfEveryRefusedRange_AreAllowed_ThroughTheSnapshot(int scalar)
    {
        var name = "a" + S(scalar) + "b";
        using var signer = TestPersonas.CreateA();
        var verified = SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, name, []), signer));
        Assert.Equal(name, Assert.IsType<ProfileSnapshot>(verified.Document).Name);
    }

    [Fact]
    public void TheLimits_AreSixtyFourScalarsAndTwoHundredFiftySixBytes()
    {
        var sixtyFour = new string('a', ProtocolLimits.MaxNameScalars);
        Assert.Equal(sixtyFour, ProtocolName.Decode(ProtocolName.Encode(sixtyFour, "name"), "name"));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolName.Encode(sixtyFour + "a", "name"));

        // 64 four-byte scalars fill both limits exactly; one more byte is over the byte limit.
        var fullBytes = string.Concat(Enumerable.Repeat(S(0x1F600), ProtocolLimits.MaxNameScalars));
        Assert.Equal(ProtocolLimits.MaxNameBytes, ProtocolName.Encode(fullBytes, "name").Length);
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolName.Encode(fullBytes + "a", "name"));

        // 86 three-byte scalars are 258 bytes: over the byte limit, though well under the scalar limit.
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolName.Decode(Utf8(string.Concat(Enumerable.Repeat(S(0x65E5), 86))), "name"));

        // A string longer than 256 UTF-16 units is over the byte limit, whatever it holds.
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolName.Encode(new string('a', 100_000), "name"));

        Assert.Equal("a", ProtocolName.Decode(ProtocolName.Encode("a", "name"), "name"));
    }

    [Fact]
    public void AnEmptyName_IsAnInvalidLength_WhenWrittenAndWhenRead()
    {
        ProtocolAssert.Throws(ProtocolError.InvalidLength, () => ProtocolName.Encode(string.Empty, "name"));
        ProtocolAssert.Throws(ProtocolError.InvalidLength, () => ProtocolName.Decode([], "name"));
        using var signer = TestPersonas.CreateA();
        ProtocolAssert.Throws(ProtocolError.InvalidLength, () => SignedDocumentCodec.Verify(PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, PayloadBuilder.Snapshot(nameBytes: []))));
    }

    [Fact]
    public void Faults_AreReportedInReadingOrder()
    {
        // The byte limit comes before UTF-8 validity, validity before refused code points, and those
        // before the scalar limit: an input with two faults names the earlier one. Where two faults
        // share a code, the message tells them apart.
        var overBytes = Enumerable.Repeat((byte)'a', ProtocolLimits.MaxNameBytes + 1).ToArray();
        overBytes[0] = 0xFF;
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolName.Decode(overBytes, "name"));

        byte[] invalidThenRefused = [0xFF, .. Utf8(S(0x202E))];
        var invalid = Assert.Throws<ProtocolException>(() => ProtocolName.Decode(invalidThenRefused, "name"));
        Assert.Equal(ProtocolError.InvalidText, invalid.Error);
        Assert.Contains("not valid UTF-8", invalid.Message, StringComparison.Ordinal);

        var refusedAfterTooMany = Utf8(new string('a', ProtocolLimits.MaxNameScalars + 1) + S(0x202E));
        var refused = Assert.Throws<ProtocolException>(() => ProtocolName.Decode(refusedAfterTooMany, "name"));
        Assert.Equal(ProtocolError.InvalidText, refused.Error);
        Assert.Contains("U+202E", refused.Message, StringComparison.Ordinal);

        // A declared name length over the byte limit is LimitExceeded before the input's own length
        // is consulted, even when only a few bytes follow (specification, section 8.1.1).
        using var signer = TestPersonas.CreateA();
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => SignedDocumentCodec.Verify(PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, SnapshotPayloadCases.Build("name length 300 with few bytes present"))));
    }

    [Fact]
    public void UnpairedSurrogates_AreRefusedWhenWritten()
    {
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => ProtocolName.Encode("a" + (char)0xD800, "name"));
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => ProtocolName.Encode((char)0xDC00 + "a", "name"));
    }

    [Fact]
    public void AName_IsNeverNormalized_OrTrimmed()
    {
        var composed = "Caf" + S(0x00E9);
        var decomposed = "Cafe" + S(0x0301);
        Assert.NotEqual(Utf8(composed), Utf8(decomposed));
        foreach (var name in new[] { composed, decomposed, " a", "a ", "A", S(0x0301), S(0x1F600) + S(0x1F3FD) })
        {
            Assert.Equal(Utf8(name), ProtocolName.Encode(name, "name"));
            Assert.Equal(name, ProtocolName.Decode(Utf8(name), "name"));
        }
    }

    private static bool InTable(int scalar) => RefusedRanges.Any(r => scalar >= r.First && scalar <= r.Last);

    private static bool IsSurrogate(int scalar) => scalar is >= 0xD800 and <= 0xDFFF;

    private static string S(int scalar) => char.ConvertFromUtf32(scalar);

    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}
