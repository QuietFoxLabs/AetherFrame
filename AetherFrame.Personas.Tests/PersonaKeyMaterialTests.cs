using System;
using System.Numerics;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// Key material is accepted only as a consistent P-256 key pair: the scalar's range is checked in
/// managed code before any platform import, the platform derives the public point from the scalar
/// alone, and that point must be the recorded one. Every assertion here holds on every platform:
/// none depends on what a platform happens to refuse, reduce or pad at import (Linux OpenSSL pads a
/// 31-byte scalar; the windows-2022 runner's CNG reduced one above the order).
/// </summary>
public class PersonaKeyMaterialTests
{
    public static TheoryData<string, byte[]> NonCanonicalScalars()
    {
        var n = SyntheticKeys.Order;
        return new TheoryData<string, byte[]>
        {
            { "zero", SyntheticKeys.Fixed32(0) },
            { "n", SyntheticKeys.Fixed32(n) },
            { "n + 1", SyntheticKeys.Fixed32(n + 1) },
            { "n + 2", SyntheticKeys.Fixed32(n + 2) },
            { "2^256 - 1", SyntheticKeys.Fixed32((BigInteger.One << 256) - 1) },
            { "empty", [] },
            { "31 bytes", SyntheticKeys.Fixed32(1)[1..] },
            { "33 bytes, a leading zero before 1", [0x00, .. SyntheticKeys.Fixed32(1)] },
            { "33 bytes, 1 followed by a zero", [.. SyntheticKeys.Fixed32(1), 0x00] },
            { "33 bytes, 1 followed by a one", [.. SyntheticKeys.Fixed32(1), 0x01] },
            { "64 bytes, the scalar 1 twice", [.. SyntheticKeys.Fixed32(1), .. SyntheticKeys.Fixed32(1)] },
            { "64 bytes, a canonical scalar then zeros", [.. SyntheticKeys.Fixed32(SyntheticKeys.Order - 1), .. new byte[32]] },
        };
    }

    [Theory]
    [MemberData(nameof(NonCanonicalScalars))]
    public void IsCanonicalScalar_RefusesEveryValueOutsideOneToNMinusOne(string name, byte[] scalar)
    {
        Assert.False(PersonaKeyMaterial.IsCanonicalScalar(scalar), name);
    }

    [Fact]
    public void IsCanonicalScalar_AcceptsTheBoundariesAndOrdinaryScalars()
    {
        var n = SyntheticKeys.Order;
        Assert.True(PersonaKeyMaterial.IsCanonicalScalar(SyntheticKeys.Fixed32(1)));
        Assert.True(PersonaKeyMaterial.IsCanonicalScalar(SyntheticKeys.Fixed32(n - 1)));
        Assert.True(PersonaKeyMaterial.IsCanonicalScalar(SyntheticKeys.Fixed32(n >> 1)));
        Assert.True(PersonaKeyMaterial.IsCanonicalScalar(SyntheticKeys.Fixed32(BigInteger.One << 255)));
        for (var i = 0; i < 32; i++)
        {
            using var key = SyntheticKeys.Create();
            var scalar = SyntheticKeys.Scalar(key);
            Assert.True(PersonaKeyMaterial.IsCanonicalScalar(scalar));
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    [Theory]
    [MemberData(nameof(NonCanonicalScalars))]
    public void Import_RefusesANonCanonicalScalarBeforeThePlatformSeesIt(string name, byte[] scalar)
    {
        // Paired with the point that the value reduced modulo n (or padded) would give where that
        // point exists, so a platform that reduces or pads would find the pair consistent: only the
        // managed range check can refuse it. A refusal from the platform would carry its exception
        // as the inner exception; the managed refusal has none.
        var value = scalar.Length == 0 ? BigInteger.Zero : new BigInteger(scalar, isUnsigned: true, isBigEndian: true) % SyntheticKeys.Order;
        var point = value.IsZero ? SyntheticKeys.BasePoint : SyntheticKeys.PointFor(value);
        var exception = Assert.Throws<PersonaException>(() => PersonaKeyMaterial.Import(scalar, point));
        Assert.Equal(PersonaError.InvalidKeyMaterial, exception.Error);
        Assert.Null(exception.InnerException);
        Assert.Contains("1 to n - 1", exception.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Fact]
    public void Import_OfNPlusOneWithTheBasePoint_IsRefusedAlthoughTheyMatchModuloN()
    {
        // (n + 1)·G = G. A platform that reduces the scalar would accept this pair as consistent.
        var exception = Assert.Throws<PersonaException>(() => PersonaKeyMaterial.Import(SyntheticKeys.Fixed32(SyntheticKeys.Order + 1), SyntheticKeys.BasePoint));
        Assert.Equal(PersonaError.InvalidKeyMaterial, exception.Error);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Import_AcceptsTheRangeBoundariesWithTheirPoints()
    {
        using (var one = PersonaKeyMaterial.Import(SyntheticKeys.Fixed32(1), SyntheticKeys.BasePoint))
        {
            Assert.Equal(SyntheticKeys.BasePoint, one.PublicKey);
            AssertHoldsScalar(one, SyntheticKeys.Fixed32(1));
        }

        using (var last = PersonaKeyMaterial.Import(SyntheticKeys.Fixed32(SyntheticKeys.Order - 1), SyntheticKeys.NegatedBasePoint))
        {
            Assert.Equal(SyntheticKeys.NegatedBasePoint, last.PublicKey);
            AssertHoldsScalar(last, SyntheticKeys.Fixed32(SyntheticKeys.Order - 1));
            using var signer = last.CreateSigner();
            Assert.Equal(last.PublicKey.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(signer)).Persona);
        }
    }

    [Fact]
    public void Import_RefusesAScalarThatDoesNotBelongToTheRecordedPoint()
    {
        using var mine = SyntheticKeys.Create();
        using var theirs = SyntheticKeys.Create();
        var scalar = SyntheticKeys.Scalar(mine);
        try
        {
            var mineKey = PersonaPublicKey.FromEcdsa(mine);
            var cases = new[]
            {
                PersonaPublicKey.FromEcdsa(theirs),
                SyntheticKeys.Negate(mineKey),
                SyntheticKeys.BasePoint,
            };
            foreach (var wrong in cases)
            {
                var exception = Assert.Throws<PersonaException>(() => PersonaKeyMaterial.Import(scalar, wrong));
                Assert.Equal(PersonaError.InvalidKeyMaterial, exception.Error);
                Assert.Contains("does not belong", exception.Message, StringComparison.Ordinal);
            }

            // The same scalar with its own point is accepted, so the refusals above are the pairing.
            using var material = PersonaKeyMaterial.Import(scalar, mineKey);
            Assert.Equal(mineKey, material.PublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    [Fact]
    public void Import_RefusesTheNeighbouringScalar()
    {
        // d + 1 against d's point: in range, on the curve, and wrong.
        using var key = SyntheticKeys.Create();
        var scalar = SyntheticKeys.Scalar(key);
        try
        {
            var next = (new BigInteger(scalar, isUnsigned: true, isBigEndian: true) + 1) % SyntheticKeys.Order;
            var neighbour = SyntheticKeys.Fixed32(next.IsZero ? BigInteger.One : next);
            var exception = Assert.Throws<PersonaException>(() => PersonaKeyMaterial.Import(neighbour, PersonaPublicKey.FromEcdsa(key)));
            Assert.Equal(PersonaError.InvalidKeyMaterial, exception.Error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    [Fact]
    public void Import_RefusesNullAndLeavesTheCallersScalarAlone()
    {
        Assert.Throws<ArgumentNullException>(() => PersonaKeyMaterial.Import(SyntheticKeys.Fixed32(1), null!));
        var scalar = SyntheticKeys.Fixed32(1);
        using var material = PersonaKeyMaterial.Import(scalar, SyntheticKeys.BasePoint);
        Assert.Equal(SyntheticKeys.Fixed32(1), scalar);
    }

    [Fact]
    public void FromEcdsa_HoldsACopy_SoTheCallerChangingItsKeyCannotReachTheMaterial()
    {
        using var callers = SyntheticKeys.Create();
        using var other = SyntheticKeys.Create();
        var original = PersonaPublicKey.FromEcdsa(callers);
        using var material = PersonaKeyMaterial.FromEcdsa(callers);
        Assert.Equal(original, material.PublicKey);

        // The caller imports another key into the same object, then generates a new one in it.
        callers.ImportParameters(other.ExportParameters(includePrivateParameters: true));
        Assert.NotEqual(original, PersonaPublicKey.FromEcdsa(callers));
        AssertSignsAs(material, original);

        callers.GenerateKey(ECCurve.NamedCurves.nistP256);
        Assert.NotEqual(original, PersonaPublicKey.FromEcdsa(callers));
        AssertSignsAs(material, original);

        // The copy is the material's own: disposing the caller's key does not end it either.
        callers.Dispose();
        AssertSignsAs(material, original);
        using var copy = material.Copy();
        AssertSignsAs(copy, original);
    }

    [Fact]
    public void FromEcdsa_NeverDisposesTheCallersKey()
    {
        using var accepted = SyntheticKeys.Create();
        using (PersonaKeyMaterial.FromEcdsa(accepted))
        {
        }

        _ = accepted.ExportParameters(includePrivateParameters: false);

        using var anotherCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var refusal = Assert.Throws<PersonaException>(() => PersonaKeyMaterial.FromEcdsa(anotherCurve));
        Assert.Equal(PersonaError.InvalidKeyMaterial, refusal.Error);
        _ = anotherCurve.ExportParameters(includePrivateParameters: false);
    }

    [Fact]
    public void FromEcdsa_RefusesNullAndAPublicOnlyKey()
    {
        Assert.Throws<ArgumentNullException>(() => PersonaKeyMaterial.FromEcdsa(null!));
        using var source = SyntheticKeys.Create();
        using var publicOnly = SyntheticKeys.PublicOnly(source);
        var exception = Assert.Throws<PersonaException>(() => PersonaKeyMaterial.FromEcdsa(publicOnly));
        Assert.Equal(PersonaError.InvalidKeyMaterial, exception.Error);
    }

    [Fact]
    public void FromEcdsa_ZeroesEveryPrivateScalarItReadsFromTheCallersKey()
    {
        using var callers = new ScalarWatchingEcdsa();
        using var material = PersonaKeyMaterial.FromEcdsa(callers);
        Assert.Equal(callers.PublicKey, material.PublicKey);
        Assert.NotEmpty(callers.ScalarsHandedOut);
        Assert.All(callers.ScalarsHandedOut, scalar => Assert.All(scalar, b => Assert.Equal(0, b)));
    }

    [Fact]
    public void CopyAndCreateSigner_CheckTheKeyAgain()
    {
        // The platform key inside material is never exposed, so it cannot drift in practice; this
        // swaps it by reflection to show that every copy is checked again rather than trusted.
        var material = SyntheticKeys.Material();
        using var other = SyntheticKeys.Create();
        var field = typeof(PersonaKeyMaterial).GetField("key", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var original = (ECDsa)field.GetValue(material)!;
        field.SetValue(material, SyntheticKeys.Copy(other));
        original.Dispose();

        using (material)
        {
            Assert.Equal(PersonaError.InvalidKeyMaterial, Assert.Throws<PersonaException>(() => material.Copy()).Error);
            Assert.Equal(PersonaError.InvalidKeyMaterial, Assert.Throws<PersonaException>(() => material.CreateSigner()).Error);
        }
    }

    [Fact]
    public void Generate_GivesDistinctValidKeys()
    {
        using var first = PersonaKeyMaterial.Generate();
        using var second = PersonaKeyMaterial.Generate();
        Assert.NotEqual(first.PublicKey, second.PublicKey);
        AssertSignsAs(first, first.PublicKey);
        var parameters = first.ExportPrivateParameters();
        try
        {
            Assert.True(PersonaKeyMaterial.IsCanonicalScalar(parameters.D));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }

    [Fact]
    public void PublicSurface_OffersNoWayToHandInACallersKey()
    {
        // No public constructor and no public member that takes a platform key or its parameters:
        // only custody code in this assembly makes material, and it never keeps a caller's object.
        Assert.Empty(typeof(PersonaKeyMaterial).GetConstructors());
        foreach (var method in typeof(PersonaKeyMaterial).GetMethods())
        {
            foreach (var parameter in method.GetParameters())
            {
                Assert.False(typeof(ECDsa).IsAssignableFrom(parameter.ParameterType), method.Name);
                Assert.NotEqual(typeof(ECParameters), parameter.ParameterType);
            }
        }
    }

    [Fact]
    public void PublicKey_IsTheKeysPublicHalf()
    {
        using var key = SyntheticKeys.Create();
        var expected = PersonaPublicKey.FromEcdsa(key);
        using var material = PersonaKeyMaterial.FromEcdsa(key);
        Assert.Equal(expected, material.PublicKey);
        Assert.Equal(expected.Id, material.PublicKey.Id);
    }

    [Fact]
    public void CreateSigner_SignsAsThePersonaAndLeavesTheMaterialUsable()
    {
        using var material = SyntheticKeys.Material();
        using var first = material.CreateSigner();
        using var second = material.CreateSigner();
        Assert.Equal(material.PublicKey, first.PublicKey);
        Assert.Equal(material.PublicKey, second.PublicKey);

        var verified = SignedDocumentCodec.Verify(Documents.SignedRetraction(first));
        Assert.Equal(material.PublicKey.Id, verified.Persona);

        // Signers have lifetimes of their own.
        first.Dispose();
        Assert.Equal(material.PublicKey.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(second)).Persona);
        using var third = material.CreateSigner();
        Assert.Equal(material.PublicKey, third.PublicKey);
    }

    [Fact]
    public void Copy_HasTheSameIdentityAndItsOwnLifetime()
    {
        var material = SyntheticKeys.Material();
        using var copy = material.Copy();
        Assert.Equal(material.PublicKey, copy.PublicKey);
        material.Dispose();
        using var signer = copy.CreateSigner();
        Assert.Equal(copy.PublicKey.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(signer)).Persona);
    }

    [Fact]
    public void Dispose_EndsSigningButNotThePublicHalf()
    {
        var material = SyntheticKeys.Material();
        var publicKey = material.PublicKey;
        material.Dispose();
        material.Dispose();
        Assert.Throws<ObjectDisposedException>(() => material.CreateSigner());
        Assert.Throws<ObjectDisposedException>(() => material.Copy());
        Assert.Throws<ObjectDisposedException>(() => material.ExportPrivateParameters());
        Assert.Equal(publicKey, material.PublicKey);
        Assert.True(Disposal.IsDisposed(material));
    }

    [Fact]
    public void ToString_RevealsNeitherTheKeyNorTheIdentity()
    {
        using var key = SyntheticKeys.Create();
        var privateHex = SyntheticKeys.PrivateHex(key);
        using var material = PersonaKeyMaterial.FromEcdsa(key);
        var text = material.ToString();
        Assert.Equal("[persona key material]", text);
        Assert.DoesNotContain(privateHex.Substring(0, 16), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticKeys.PublicHex(material.PublicKey).Substring(2, 16), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(material.PublicKey.Id.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportPrivateParameters_IsInternalAndGivesTheScalar()
    {
        // The one way custody code gets the scalar, and it is not on the public surface (see
        // AssemblyBoundaryTests). The caller zeroes it.
        using var key = SyntheticKeys.Create();
        var expected = SyntheticKeys.PrivateHex(key);
        using var material = PersonaKeyMaterial.FromEcdsa(key);
        var parameters = material.ExportPrivateParameters();
        try
        {
            Assert.Equal(expected, Convert.ToHexStringLower(parameters.D!));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }

    private static void AssertSignsAs(PersonaKeyMaterial material, PersonaPublicKey expected)
    {
        Assert.Equal(expected, material.PublicKey);
        using var signer = material.CreateSigner();
        Assert.Equal(expected, signer.PublicKey);
        Assert.Equal(expected.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(signer)).Persona);
    }

    private static void AssertHoldsScalar(PersonaKeyMaterial material, byte[] expected)
    {
        var parameters = material.ExportPrivateParameters();
        try
        {
            Assert.Equal(expected, parameters.D);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }
}
