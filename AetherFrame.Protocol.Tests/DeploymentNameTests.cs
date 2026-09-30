using System;
using System.Linq;
using AetherFrame.Protocol.Requests;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>The deployment name rule of section 14.1: the canonical DNS hostname a client connects to.</summary>
public class DeploymentNameTests
{
    public static TheoryData<string> Valid => new()
    {
        "plates.example.com",
        "localhost",
        "a",
        "1password.com",
        "a-b.c-d",
        "xn--bcher-kva.example",
        "x1.y2.z3",
        new string('a', 63) + ".com",
        string.Join('.', Enumerable.Repeat(new string('a', 63), 3)) + "." + new string('b', 61),
    };

    public static TheoryData<string, ProtocolError> Invalid => new()
    {
        { string.Empty, ProtocolError.InvalidLength },
        { string.Join('.', Enumerable.Repeat(new string('a', 63), 3)) + "." + new string('b', 62), ProtocolError.LimitExceeded },
        { new string('A', 254), ProtocolError.LimitExceeded },
        { "Plates.example.com", ProtocolError.InvalidValue },
        { "plates.example.com.", ProtocolError.InvalidValue },
        { ".example.com", ProtocolError.InvalidValue },
        { "plates..com", ProtocolError.InvalidValue },
        { ".", ProtocolError.InvalidValue },
        { "-plates.com", ProtocolError.InvalidValue },
        { "plates-.com", ProtocolError.InvalidValue },
        { "plates.-com", ProtocolError.InvalidValue },
        { new string('a', 64) + ".com", ProtocolError.InvalidValue },
        { "192.168.0.1", ProtocolError.InvalidValue },
        { "1.2.3.4", ProtocolError.InvalidValue },
        { "example.123", ProtocolError.InvalidValue },
        { "8", ProtocolError.InvalidValue },
        { "0x7f000001", ProtocolError.InvalidValue },
        { "0x7f.0.0.1", ProtocolError.InvalidValue },
        { "plates.0x7f", ProtocolError.InvalidValue },
        { "plates.1com", ProtocolError.InvalidValue },
        { "017700000001", ProtocolError.InvalidValue },
        { "[::1]", ProtocolError.InvalidValue },
        { "::1", ProtocolError.InvalidValue },
        { "plates.example.com:443", ProtocolError.InvalidValue },
        { "https://plates.example.com", ProtocolError.InvalidValue },
        { "plates_example.com", ProtocolError.InvalidValue },
        { " plates.com", ProtocolError.InvalidValue },
        { "plates.com\n", ProtocolError.InvalidValue },
        { "pl" + char.ConvertFromUtf32(0xE4) + "tes.com", ProtocolError.InvalidValue },
        { "plates" + char.ConvertFromUtf32(0x3002) + "com", ProtocolError.InvalidValue },
        { "plates.com" + char.ConvertFromUtf32(0), ProtocolError.InvalidValue },
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public void CanonicalNames_ParseAndReadTheSame(string text)
    {
        var name = DeploymentName.Parse(text);
        Assert.Equal(text, name.Value);
        Assert.Equal(text, name.ToString());
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(text), name.Bytes.ToArray());
        Assert.Equal(name, DeploymentName.FromBytes(name.Bytes));
        Assert.True(DeploymentName.TryParse(text, out var again));
        Assert.Equal(name, again);
        Assert.Equal(name.GetHashCode(), again!.GetHashCode());
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void OtherNames_AreRefusedWithTheirError(string text, ProtocolError expected)
    {
        ProtocolAssert.Throws(expected, () => DeploymentName.Parse(text));
        Assert.False(DeploymentName.TryParse(text, out var name));
        Assert.Null(name);
        if (text.All(c => c <= 0x7f))
        {
            ProtocolAssert.Throws(expected, () => DeploymentName.FromBytes(System.Text.Encoding.ASCII.GetBytes(text)));
        }
    }

    [Fact]
    public void TheLongestNameIsExactlyTheLimit()
    {
        var longest = string.Join('.', Enumerable.Repeat(new string('a', 63), 3)) + "." + new string('b', 61);
        Assert.Equal(ProtocolLimits.MaxDeploymentNameBytes, longest.Length);
        Assert.Equal(253, ProtocolLimits.MaxDeploymentNameBytes);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("api.localhost", true)]
    [InlineData("server", true)]
    [InlineData("plates.test", true)]
    [InlineData("plates.example", true)]
    [InlineData("plates.invalid", true)]
    [InlineData("example.com", true)]
    [InlineData("plates.example.com", true)]
    [InlineData("a.b.example.net", true)]
    [InlineData("example.org", true)]
    [InlineData("plates.example.co", false)]
    [InlineData("example.community", false)]
    [InlineData("myexample.com", false)]
    [InlineData("plates.aetherframe.net", false)]
    [InlineData("test.com", false)]
    [InlineData("localhost.com", false)]
    [InlineData("printer.local", true)]
    [InlineData("plates.internal", true)]
    [InlineData("plates.alt", true)]
    [InlineData("abcdefghijklmnop.onion", true)]
    [InlineData("1.0.0.127.in-addr.arpa", true)]
    [InlineData("local.example.net", true)]
    [InlineData("plates.local.com", false)]
    [InlineData("internal.dev", false)]
    public void ReservedNames_AreOnlyForTests(string text, bool reserved)
    {
        Assert.Equal(reserved, DeploymentName.Parse(text).IsReservedForTesting);
    }

    [Fact]
    public void NamesAreComparedByTheirBytes()
    {
        Assert.Equal(DeploymentName.Parse("plates.example.com"), DeploymentName.Parse("plates.example.com"));
        Assert.NotEqual(DeploymentName.Parse("plates.example.com"), DeploymentName.Parse("staging.example.com"));
        Assert.NotEqual(DeploymentName.Parse("plates.example.com"), DeploymentName.Parse("plates.example.co"));
        Assert.False(DeploymentName.Parse("a").Equals(null));
        Assert.False(DeploymentName.TryParse(null, out _));
        Assert.Throws<ArgumentNullException>(() => DeploymentName.Parse(null!));
    }
}
