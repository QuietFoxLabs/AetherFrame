using System;
using System.Linq;
using AetherFrame.Protocol.Requests;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>The request challenge of section 14.2: 32 bytes a server issues, never all zero.</summary>
public class RequestChallengeTests
{
    private const string SampleText = "chl_d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7";

    [Fact]
    public void NewChallenges_AreThirtyTwoRandomBytes()
    {
        var first = RequestChallenge.NewRandom();
        var second = RequestChallenge.NewRandom();
        Assert.Equal(ProtocolConstants.ChallengeLength, first.Bytes.Length);
        Assert.Contains(first.Bytes.ToArray(), b => b != 0);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TheWireAndTextForms_RoundTrip()
    {
        var challenge = RequestChallenge.Parse(SampleText);
        Assert.Equal(Enumerable.Repeat((byte)0xd7, 32).ToArray(), challenge.ToArray());
        Assert.Equal(SampleText, challenge.ToString());
        Assert.Equal(challenge, RequestChallenge.FromBytes(challenge.Bytes));
        Assert.Equal(challenge.GetHashCode(), RequestChallenge.FromBytes(challenge.Bytes).GetHashCode());

        var copy = challenge.ToArray();
        copy[0] ^= 1;
        Assert.Equal(0xd7, challenge.Bytes[0]);
    }

    [Fact]
    public void TheWireForm_RefusesAnotherLengthAndAllZero()
    {
        ProtocolAssert.Throws(ProtocolError.InvalidLength, () => RequestChallenge.FromBytes(new byte[31]));
        ProtocolAssert.Throws(ProtocolError.InvalidLength, () => RequestChallenge.FromBytes(new byte[33]));
        ProtocolAssert.Throws(ProtocolError.InvalidLength, () => RequestChallenge.FromBytes([]));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => RequestChallenge.FromBytes(new byte[32]));
        var one = new byte[32];
        one[31] = 1;
        Assert.Equal(1, RequestChallenge.FromBytes(one).Bytes[31]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chl_")]
    [InlineData("chl_d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7")]
    [InlineData("chl_d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7")]
    [InlineData("chl_D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7D7")]
    [InlineData("CHL_d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7")]
    [InlineData("prf_d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7")]
    [InlineData("chl_0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("chl_d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7g7")]
    [InlineData(" chl_d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7")]
    public void TheTextForm_IsParsedStrictly(string? text)
    {
        Assert.False(RequestChallenge.TryParse(text, out var challenge));
        Assert.Null(challenge);
        if (text is not null)
        {
            ProtocolAssert.Throws(ProtocolError.InvalidValue, () => RequestChallenge.Parse(text));
        }
    }

    [Fact]
    public void Challenges_AreComparedByTheirBytes()
    {
        var a = RequestChallenge.Parse(SampleText);
        var b = RequestChallenge.Parse(SampleText.Replace("d7", "d8", StringComparison.Ordinal));
        Assert.NotEqual(a, b);
        Assert.False(a.Equals(null));
        Assert.False(a.Equals((object)"chl_"));
    }
}
