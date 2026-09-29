using System;
using System.Linq;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Remote;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// The name rule, decision D4 (docs/networking/DecisionRegister.md): 1 to 64 scalars in at most
/// 256 bytes, no refused code point, nothing normalized, and faults reported in reading order. The
/// refused list is checked at both edges of every range, with the neighbour on each side allowed,
/// so a range that is one code point too wide or too narrow fails here.
/// </summary>
public class NameRuleTests
{
    // Every refused range, from decision D4, as (first, last).
    private static readonly (int First, int Last)[] RefusedRanges =
    [
        (0x0000, 0x001F), // C0 controls
        (0x007F, 0x009F), // DEL and the C1 controls
        (0x00AD, 0x00AD), // soft hyphen
        (0x061C, 0x061C), // ARABIC LETTER MARK
        (0x180E, 0x180E), // MONGOLIAN VOWEL SEPARATOR
        (0x200B, 0x200F), // zero width space, non-joiner, joiner; LRM, RLM
        (0x2028, 0x2029), // line and paragraph separators
        (0x202A, 0x202E), // embeddings, PDF and overrides
        (0x2060, 0x2064), // word joiner and invisible operators
        (0x2066, 0x206F), // isolates and deprecated format characters
        (0xFEFF, 0xFEFF), // byte order mark
        (0xFFF9, 0xFFFB), // interlinear annotation
        (0xE0001, 0xE0001), // LANGUAGE TAG
        (0xE0020, 0xE007F), // tag characters
    ];

    public static TheoryData<int> RefusedEdges()
    {
        var data = new TheoryData<int>();
        foreach (var (first, last) in RefusedRanges)
        {
            data.Add(first);
            if (last != first)
            {
                data.Add(last);
            }
        }

        return data;
    }

    public static TheoryData<int> AllowedNeighbours()
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        var data = new TheoryData<int>();
        foreach (var (first, last) in RefusedRanges)
        {
            foreach (var candidate in new[] { first - 1, last + 1 })
            {
                var usable = candidate >= 0
                    && (candidate < 0xD800 || candidate > 0xDFFF)
                    && !RefusedRanges.Any(r => candidate >= r.First && candidate <= r.Last)
                    && IsAssignedOrUnassignedOnPurpose(candidate);
                if (usable && seen.Add(candidate))
                {
                    data.Add(candidate);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedEdges))]
    public void EveryRefusedRange_IsRefusedAtBothEdges(int scalar)
    {
        var name = "a" + char.ConvertFromUtf32(scalar) + "b";
        Assert.True(ProtocolName.IsRefused(scalar));
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => ProtocolName.Encode(name, "name"));
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => ProtocolName.Decode(Utf8(name), "name"));
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, name, []));
    }

    [Theory]
    [MemberData(nameof(AllowedNeighbours))]
    public void TheNeighboursOfEveryRefusedRange_AreAllowed(int scalar)
    {
        var name = "a" + char.ConvertFromUtf32(scalar) + "b";
        Assert.False(ProtocolName.IsRefused(scalar));
        Assert.Equal(name, ProtocolName.Decode(ProtocolName.Encode(name, "name"), "name"));
    }

    [Fact]
    public void TheLimits_AreSixtyFourScalarsAndTwoHundredFiftySixBytes()
    {
        var sixtyFour = new string('a', ProtocolLimits.MaxNameScalars);
        Assert.Equal(sixtyFour, ProtocolName.Decode(ProtocolName.Encode(sixtyFour, "name"), "name"));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolName.Encode(sixtyFour + "a", "name"));

        // 64 four-byte scalars fill both limits exactly; one more byte is over the byte limit.
        var fullBytes = string.Concat(Enumerable.Repeat("\U0001F600", ProtocolLimits.MaxNameScalars));
        Assert.Equal(ProtocolLimits.MaxNameBytes, ProtocolName.Encode(fullBytes, "name").Length);
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolName.Encode(fullBytes + "a", "name"));

        // 86 three-byte scalars are 258 bytes: over the byte limit, though well under the scalar limit.
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolName.Decode(Utf8(new string('日', 86)), "name"));

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
        // before the scalar limit: an input with two faults names the earlier one.
        var overBytes = Enumerable.Repeat((byte)'a', ProtocolLimits.MaxNameBytes + 1).ToArray();
        overBytes[0] = 0xFF;
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ProtocolName.Decode(overBytes, "name"));

        var invalidThenRefused = new byte[] { 0xFF, 0xE2, 0x80, 0xAE };
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => ProtocolName.Decode(invalidThenRefused, "name"));

        var refusedAfterTooMany = Utf8(new string('a', ProtocolLimits.MaxNameScalars + 1) + "‮");
        var failure = Assert.Throws<ProtocolException>(() => ProtocolName.Decode(refusedAfterTooMany, "name"));
        Assert.Equal(ProtocolError.InvalidText, failure.Error);
    }

    [Fact]
    public void UnpairedSurrogates_AreRefusedWhenWritten()
    {
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => ProtocolName.Encode("a\ud800", "name"));
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => ProtocolName.Encode("\udc00a", "name"));
    }

    [Fact]
    public void AName_IsNeverNormalized_OrTrimmed()
    {
        foreach (var name in new[] { "Café", "Café", " a", "a ", "A", "́", "\U0001F600\U0001F3FD" })
        {
            Assert.Equal(Utf8(name), ProtocolName.Encode(name, "name"));
            Assert.Equal(name, ProtocolName.Decode(Utf8(name), "name"));
        }
    }

    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    // U+2065 is unassigned and sits between two refused ranges: the rule is fixed code points, so it is allowed.
    private static bool IsAssignedOrUnassignedOnPurpose(int scalar) =>
        System.Globalization.CharUnicodeInfo.GetUnicodeCategory(scalar) != System.Globalization.UnicodeCategory.OtherNotAssigned
        || scalar is 0x2065;
}
