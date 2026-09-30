using System;
using Xunit;

namespace AetherFrame.Personas.Tests;

public class PersonaLabelTests
{
    [Theory]
    [InlineData("Main", "Main")]
    [InlineData("  RP alt  ", "RP alt")]
    [InlineData("\tMain\n", "Main")]
    [InlineData("Émilie 🌸", "Émilie 🌸")]
    [InlineData("日本語のラベル", "日本語のラベル")]
    [InlineData("two  spaces  inside", "two  spaces  inside")]
    [InlineData("a", "a")]
    public void TryNormalize_TrimsAndKeepsEverythingElse(string text, string expected)
    {
        Assert.True(PersonaLabel.TryNormalize(text, out var label));
        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("Main\u0000")]
    [InlineData("Ma\u0007in")]
    [InlineData("Main\u007f")]
    [InlineData("Main\u0085alt")]
    [InlineData("line\nbreak")]
    public void TryNormalize_RefusesEmptyAndControlCharacters(string? text)
    {
        Assert.False(PersonaLabel.TryNormalize(text, out var label));
        Assert.Equal("", label);
    }

    [Fact]
    public void TryNormalize_AllowsExactlyTheMaximumLength()
    {
        var atLimit = new string('x', PersonaLabel.MaxLength);
        Assert.True(PersonaLabel.TryNormalize(atLimit, out var label));
        Assert.Equal(atLimit, label);
        Assert.True(PersonaLabel.TryNormalize("  " + atLimit + "  ", out _));
        Assert.False(PersonaLabel.TryNormalize(atLimit + "x", out _));
    }

    [Fact]
    public void TheLimit_CountsUtf16CodeUnits()
    {
        // 32 supplementary characters are 64 code units; 33 are 66.
        Assert.True(PersonaLabel.TryNormalize(string.Concat(System.Linq.Enumerable.Repeat("🌸", 32)), out _));
        Assert.False(PersonaLabel.TryNormalize(string.Concat(System.Linq.Enumerable.Repeat("🌸", 33)), out _));
    }

    [Fact]
    public void TryNormalize_KeepsSurrogatePairs_AndRefusesUnpairedSurrogates()
    {
        // Built from a code point: an unpaired surrogate cannot be written in source, and the
        // registry keeps a label as code units, so only whole pairs may reach it.
        var pair = char.ConvertFromUtf32(0x1F98A);
        var high = pair[0].ToString();
        var low = pair[1].ToString();
        Assert.True(PersonaLabel.TryNormalize("Fox " + pair, out var kept));
        Assert.Equal("Fox " + pair, kept);
        Assert.True(PersonaLabel.TryNormalize(pair + pair, out _));

        Assert.False(PersonaLabel.TryNormalize("Fox " + high, out _));
        Assert.False(PersonaLabel.TryNormalize(high + "Fox", out _));
        Assert.False(PersonaLabel.TryNormalize("Fox " + low, out _));
        Assert.False(PersonaLabel.TryNormalize(low + high, out _));
        Assert.False(PersonaLabel.TryNormalize(high + high + low, out _));
        Assert.False(PersonaLabel.TryNormalize(pair + low, out _));
        Assert.Equal(PersonaError.InvalidLabel, Assert.Throws<PersonaException>(() => PersonaLabel.Normalize(high)).Error);
    }

    [Fact]
    public void TheWhitespaceTrimmed_IsAFixedList_EveryOtherCharacterKeptOrRefused()
    {
        // Unicode's White_Space set, written out: the rule trims exactly these at either end, and
        // keeps or refuses every other character the same way on every runtime.
        int[] whitespace =
        [
            0x0009, 0x000A, 0x000B, 0x000C, 0x000D, 0x0020, 0x0085, 0x00A0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003,
            0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000,
        ];

        for (var code = 0; code <= 0xFFFF; code++)
        {
            var c = (char)code;
            if (char.IsSurrogate(c))
            {
                continue;
            }

            var accepted = PersonaLabel.TryNormalize("x" + c, out var label);
            if (Array.IndexOf(whitespace, code) >= 0)
            {
                Assert.True(accepted && label == "x", $"U+{code:X4} is trimmed");
                Assert.True(PersonaLabel.TryNormalize(c + "x", out label) && label == "x", $"U+{code:X4} is trimmed");
            }
            else if (code is < 0x20 or (>= 0x7F and <= 0x9F))
            {
                Assert.False(accepted, $"U+{code:X4} is a control character");
            }
            else
            {
                Assert.True(accepted && label == "x" + c, $"U+{code:X4} is kept");
            }
        }
    }

    [Fact]
    public void Normalize_ThrowsInvalidLabelWithoutRepeatingTheText()
    {
        var exception = Assert.Throws<PersonaException>(() => PersonaLabel.Normalize("secret alt name\u0000"));
        Assert.Equal(PersonaError.InvalidLabel, exception.Error);
        Assert.DoesNotContain("secret alt name", exception.Message, StringComparison.Ordinal);
    }
}
