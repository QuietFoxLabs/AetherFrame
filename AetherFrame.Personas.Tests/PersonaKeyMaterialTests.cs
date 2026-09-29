using System;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using Xunit;

namespace AetherFrame.Personas.Tests;

public class PersonaKeyMaterialTests
{
    [Fact]
    public void Constructor_RefusesNull()
    {
        Assert.Throws<ArgumentNullException>(() => new PersonaKeyMaterial(null!));
    }

    [Fact]
    public void Constructor_RefusesAnotherCurveAndDisposesTheKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var exception = Assert.Throws<PersonaException>(() => new PersonaKeyMaterial(key));
        Assert.Equal(PersonaError.InvalidKeyMaterial, exception.Error);
        Assert.Throws<ObjectDisposedException>(() => key.ExportParameters(includePrivateParameters: false));
    }

    [Fact]
    public void Constructor_RefusesAPublicOnlyKey()
    {
        using var source = SyntheticKeys.Create();
        var publicOnly = SyntheticKeys.PublicOnly(source);
        var exception = Assert.Throws<PersonaException>(() => new PersonaKeyMaterial(publicOnly));
        Assert.Equal(PersonaError.InvalidKeyMaterial, exception.Error);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    public void Constructor_NeverHoldsAScalarOutOfRange(byte fill)
    {
        // All-zero is 0; all-0xFF is above the group order. Platforms differ on such a key: one
        // refuses the import (Windows 11 CNG, OpenSSL), another imports it and hands back the scalar
        // as given, and another imports it and hands back a reduced scalar (Windows Server 2022 CNG,
        // for the value above the order). Whatever the platform does, material is either refused or
        // holds a scalar in range; it never holds the value that was given.
        using var source = SyntheticKeys.Create();
        var parameters = source.ExportParameters(includePrivateParameters: false);
        parameters.D = new byte[32];
        Array.Fill(parameters.D, fill);
        ECDsa key;
        try
        {
            key = ECDsa.Create(parameters);
        }
        catch (CryptographicException)
        {
            return;
        }

        PersonaKeyMaterial material;
        try
        {
            material = new PersonaKeyMaterial(key);
        }
        catch (PersonaException refusal)
        {
            Assert.Equal(PersonaError.InvalidKeyMaterial, refusal.Error);
            return;
        }

        using (material)
        {
            var held = material.ExportPrivateParameters();
            try
            {
                Assert.NotNull(held.D);
                Assert.Equal(32, held.D!.Length);
                Assert.True(held.D.AsSpan().IndexOfAnyExcept((byte)0) >= 0, "the material holds a zero scalar");
                Assert.True(held.D.AsSpan().SequenceCompareTo(SyntheticKeys.GroupOrder) < 0, "the material holds a scalar at or above the group order");
                Assert.NotEqual(Convert.ToHexString(parameters.D), Convert.ToHexString(held.D));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(held.D);
            }
        }
    }

    [Fact]
    public void PublicKey_IsTheKeysPublicHalf()
    {
        var key = SyntheticKeys.Create();
        var expected = PersonaPublicKey.FromEcdsa(key);
        using var material = new PersonaKeyMaterial(key);
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
    }

    [Fact]
    public void ToString_RevealsNeitherTheKeyNorTheIdentity()
    {
        var key = SyntheticKeys.Create();
        var privateHex = SyntheticKeys.PrivateHex(key);
        using var material = new PersonaKeyMaterial(key);
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
        var key = SyntheticKeys.Create();
        var expected = SyntheticKeys.PrivateHex(key);
        using var material = new PersonaKeyMaterial(key);
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
}
