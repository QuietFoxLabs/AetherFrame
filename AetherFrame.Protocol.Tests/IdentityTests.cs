using System;
using System.Linq;
using System.Security.Cryptography;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>Public keys, persona identities and opaque identifiers: strict forms, one text form each, derivation from the key alone.</summary>
public class IdentityTests
{
    [Fact]
    public void PublicKey_RoundTripsThroughBytesAndMatchesReferenceDerivation()
    {
        using var signer = TestPersonas.CreateA();
        var key = signer.PublicKey;
        Assert.Equal(65, key.Bytes.Length);
        Assert.Equal(0x04, key.Bytes[0]);

        var (x, y) = ReferenceP256.PublicKey(TestPersonas.ScalarA);
        Assert.Equal(x, key.Bytes.Slice(1, 32).ToArray());
        Assert.Equal(y, key.Bytes.Slice(33, 32).ToArray());

        var reparsed = PersonaPublicKey.FromBytes(key.ToArray());
        Assert.Equal(key, reparsed);
        Assert.Equal(key.Id, reparsed.Id);
        Assert.Equal(key.GetHashCode(), reparsed.GetHashCode());
        Assert.Equal(key.Id.ToString(), key.ToString());
    }

    [Fact]
    public void PublicKey_RefusesWrongLengthPrefixAndOffCurvePoints()
    {
        using var signer = TestPersonas.CreateA();
        var bytes = signer.PublicKey.ToArray();

        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => PersonaPublicKey.FromBytes(bytes.AsSpan(0, 64)));
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => PersonaPublicKey.FromBytes(bytes.Concat(new byte[] { 0 }).ToArray()));
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => PersonaPublicKey.FromBytes([]));
        foreach (var prefix in new byte[] { 0x00, 0x02, 0x03, 0x05, 0xFF })
        {
            var mutated = (byte[])bytes.Clone();
            mutated[0] = prefix;
            ProtocolAssert.Throws(ProtocolError.InvalidKey, () => PersonaPublicKey.FromBytes(mutated));
        }

        var offCurve = (byte[])bytes.Clone();
        offCurve[64] ^= 0x01;
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => PersonaPublicKey.FromBytes(offCurve));

        var zero = new byte[65];
        zero[0] = 0x04;
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => PersonaPublicKey.FromBytes(zero));

        var overField = new byte[65];
        overField[0] = 0x04;
        Array.Fill(overField, (byte)0xFF, 1, 64);
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => PersonaPublicKey.FromBytes(overField));
    }

    [Fact]
    public void PublicKey_NegatedYIsAnotherValidKeyWithAnotherIdentity()
    {
        using var signer = TestPersonas.CreateA();
        var bytes = signer.PublicKey.ToArray();
        var y = new System.Numerics.BigInteger(bytes.AsSpan(33, 32), isUnsigned: true, isBigEndian: true);
        ReferenceP256.ToBytes32(ReferenceP256.P - y).CopyTo(bytes, 33);

        var negated = PersonaPublicKey.FromBytes(bytes);
        Assert.NotEqual(signer.PublicKey, negated);
        Assert.NotEqual(signer.PublicKey.Id, negated.Id);
    }

    [Fact]
    public void PublicKey_RefusesKeysOnOtherCurves()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => PersonaPublicKey.FromEcdsa(p384));
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => new EcdsaPersonaSigner(p384));
    }

    [Fact]
    public void PersonaId_IsSha256OverTaggedKeyAndHasOneTextForm()
    {
        using var signer = TestPersonas.CreateA();
        var key = signer.PublicKey;

        var tag = "AetherFrame.Protocol.PersonaId.v1"u8.ToArray();
        var input = new byte[] { (byte)tag.Length }.Concat(tag).Concat(new byte[] { 0x01 }).Concat(key.ToArray()).ToArray();
        var expected = "psn_" + Hex.Of(SHA256.HashData(input));

        Assert.Equal(expected, key.Id.ToString());
        Assert.Equal(PersonaId.TextLength, expected.Length);
        Assert.Equal(key.Id, PersonaId.Parse(expected));
        Assert.Equal(SHA256.HashData(input), key.Id.ToArray());
        Assert.False(key.Id.IsEmpty);
        Assert.True(default(PersonaId).IsEmpty);

        Assert.False(PersonaId.TryParse(expected.ToUpperInvariant(), out _));
        Assert.False(PersonaId.TryParse(expected + "0", out _));
        Assert.False(PersonaId.TryParse(expected.Substring(0, expected.Length - 1), out _));
        Assert.False(PersonaId.TryParse("prf_" + expected.Substring(4), out _));
        Assert.False(PersonaId.TryParse("psn_" + new string('0', 64), out _));
        Assert.False(PersonaId.TryParse(null, out _));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => PersonaId.Parse(" " + expected));
    }

    [Fact]
    public void OpaqueIds_HaveOneStrictTextFormAndSortByBytes()
    {
        var id = ProfileId.NewId();
        Assert.False(id.IsEmpty);
        Assert.StartsWith("prf_", id.ToString(), StringComparison.Ordinal);
        Assert.Equal(36, id.ToString().Length);
        Assert.Equal(id, ProfileId.Parse(id.ToString()));
        Assert.Equal(id, ProfileId.FromBytes(id.ToArray()));
        Assert.NotEqual(id, ProfileId.NewId());

        Assert.False(ProfileId.TryParse(id.ToString().ToUpperInvariant(), out _));
        Assert.False(ProfileId.TryParse("rev_" + id.ToString().Substring(4), out _));
        Assert.False(ProfileId.TryParse("prf_" + new string('0', 32), out _));
        Assert.False(ProfileId.TryParse("prf_" + new string('g', 32), out _));
        Assert.False(RevisionId.TryParse(id.ToString(), out _));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => ProfileId.FromBytes(new byte[16]));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => AssetId.FromBytes(new byte[15]));
        Assert.True(default(AssetId).IsEmpty);

        var low = AssetId.Parse("ast_00000000000000000000000000000001");
        var mid = AssetId.Parse("ast_000000000000000000000000000000ff");
        var high = AssetId.Parse("ast_01000000000000000000000000000000");
        Assert.True(low.CompareTo(mid) < 0);
        Assert.True(mid.CompareTo(high) < 0);
        Assert.Equal(0, low.CompareTo(AssetId.Parse(low.ToString())));
        Assert.Equal([low, mid, high], new[] { high, low, mid }.OrderBy(a => a).ToArray());
    }
}
