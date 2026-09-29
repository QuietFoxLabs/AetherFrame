using System;
using System.Collections.Generic;
using Xunit;

namespace AetherFrame.Personas.Tests;

public class PersonaSlotIdTests
{
    [Fact]
    public void NewId_IsNeverEmptyAndDiffersEveryTime()
    {
        var seen = new HashSet<PersonaSlotId>();
        for (var i = 0; i < 1000; i++)
        {
            var id = PersonaSlotId.NewId();
            Assert.False(id.IsEmpty);
            Assert.True(seen.Add(id));
        }
    }

    [Fact]
    public void ToString_RoundTripsThroughParse()
    {
        var id = PersonaSlotId.NewId();
        var text = id.ToString();
        Assert.Equal(PersonaSlotId.TextLength, text.Length);
        Assert.StartsWith(PersonaSlotId.Prefix, text, StringComparison.Ordinal);
        Assert.Equal(id, PersonaSlotId.Parse(text));
        Assert.True(PersonaSlotId.TryParse(text, out var parsed));
        Assert.Equal(id, parsed);
        Assert.Equal(id.GetHashCode(), parsed.GetHashCode());
        Assert.True(id == parsed);
        Assert.False(id != parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("slot_")]
    [InlineData("slot_0123456789abcdef0123456789abcde")]
    [InlineData("slot_0123456789abcdef0123456789abcdef0")]
    [InlineData("slot_0123456789ABCDEF0123456789abcdef")]
    [InlineData("slot_0123456789abcdef0123456789abcdeg")]
    [InlineData("slot_00000000000000000000000000000000")]
    [InlineData("psn_0123456789abcdef0123456789abcdef0")]
    [InlineData("prf_0123456789abcdef0123456789abcdef0")]
    [InlineData("SLOT_0123456789abcdef0123456789abcdef")]
    [InlineData(" slot_0123456789abcdef0123456789abcdef")]
    [InlineData("slot_0123456789abcdef0123456789abcdef ")]
    [InlineData("slot_0123456789abcdef0123456789abcdéf")]
    public void TryParse_RefusesEverythingButTheOneTextForm(string? text)
    {
        Assert.False(PersonaSlotId.TryParse(text, out var id));
        Assert.True(id.IsEmpty);
        if (text is not null)
        {
            var exception = Assert.Throws<PersonaException>(() => PersonaSlotId.Parse(text));
            Assert.Equal(PersonaError.InvalidIdentifier, exception.Error);
            if (text.Length > 0)
            {
                // The message names the rule, never the refused text.
                Assert.DoesNotContain(text, exception.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TryParse_AcceptsTheOneTextForm()
    {
        Assert.True(PersonaSlotId.TryParse("slot_0123456789abcdef0123456789abcdef", out var id));
        Assert.False(id.IsEmpty);
        Assert.Equal("slot_0123456789abcdef0123456789abcdef", id.ToString());
    }

    [Fact]
    public void Default_IsEmptyAndEqualsOnlyItself()
    {
        var empty = default(PersonaSlotId);
        Assert.True(empty.IsEmpty);
        Assert.Equal(empty, default);
        Assert.NotEqual(empty, PersonaSlotId.NewId());
        Assert.False(empty.Equals(null));
        Assert.False(empty.Equals("slot_00000000000000000000000000000000"));
    }

    [Fact]
    public void ASlot_IsUnrelatedToAnyPersonaIdentity()
    {
        // The handle is minted from randomness, so two managers holding the same persona give it
        // different slots, and a slot's text never contains the persona id's prefix.
        var id = PersonaSlotId.NewId();
        Assert.DoesNotContain("psn_", id.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(id.ToString(), PersonaSlotId.NewId().ToString());
    }
}
