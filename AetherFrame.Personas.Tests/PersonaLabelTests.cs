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
    public void Normalize_ThrowsInvalidLabelWithoutRepeatingTheText()
    {
        var exception = Assert.Throws<PersonaException>(() => PersonaLabel.Normalize("secret alt name\u0000"));
        Assert.Equal(PersonaError.InvalidLabel, exception.Error);
        Assert.DoesNotContain("secret alt name", exception.Message, StringComparison.Ordinal);
    }
}
