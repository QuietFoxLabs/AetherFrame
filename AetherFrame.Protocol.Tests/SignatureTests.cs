using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>Signing inputs, canonical signatures and verification, checked against the platform and against the reference implementation.</summary>
public class SignatureTests
{
    [Fact]
    public void SigningInput_IsTagVersionTypeKeyAndLengthPrefixedPayload()
    {
        using var signer = TestPersonas.CreateA();
        var payload = new byte[] { 1, 2, 3 };
        var input = SigningInput.Create(DocumentType.ProfileSnapshot, signer.PublicKey, payload);

        var tag = "AetherFrame.Protocol.SignedDocument.v1"u8.ToArray();
        var expected = new byte[] { (byte)tag.Length }.Concat(tag)
            .Concat(new byte[] { 0x00, 0x01 })
            .Concat(new byte[] { 0x01 })
            .Concat(signer.PublicKey.ToArray())
            .Concat(new byte[] { 0, 0, 0, 3, 1, 2, 3 })
            .ToArray();

        Assert.Equal(38, tag.Length);
        Assert.Equal(expected, input.Bytes.ToArray());
        Assert.Equal(SHA256.HashData(expected), input.ComputeDigest());
        Assert.Equal(DocumentType.ProfileSnapshot, input.DocumentType);
        Assert.Equal(signer.PublicKey, input.PublicKey);
    }

    [Fact]
    public void SigningInput_RefusesUnknownTypeEmptyAndOversizedPayloads()
    {
        using var signer = TestPersonas.CreateA();
        ProtocolAssert.Throws(ProtocolError.UnknownDocumentType, () => SigningInput.Create((DocumentType)3, signer.PublicKey, [1]));
        ProtocolAssert.Throws(ProtocolError.UnknownDocumentType, () => SigningInput.Create((DocumentType)0, signer.PublicKey, [1]));
        ProtocolAssert.Throws(ProtocolError.InvalidLength, () => SigningInput.Create(DocumentType.ProfileSnapshot, signer.PublicKey, []));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => SigningInput.Create(DocumentType.ProfileSnapshot, signer.PublicKey, new byte[ProtocolLimits.MaxPayloadBytes + 1]));
        Assert.Equal(ProtocolLimits.MaxPayloadBytes, SigningInput.Create(DocumentType.ProfileSnapshot, signer.PublicKey, new byte[ProtocolLimits.MaxPayloadBytes]).Bytes.Length - 1 - 38 - 2 - 1 - 65 - 4);
    }

    [Fact]
    public void Signature_VerifiesWithPlatformAndReferenceImplementation_AndIsLowS()
    {
        using var signer = TestPersonas.CreateA();
        var input = SigningInput.Create(DocumentType.ProfileRetraction, signer.PublicKey, Samples.Retraction().EncodePayload());

        for (var round = 0; round < 8; round++)
        {
            var signature = signer.Sign(input);
            Assert.Equal(64, signature.Bytes.Length);
            var s = new BigInteger(signature.Bytes.Slice(32), isUnsigned: true, isBigEndian: true);
            Assert.True(s <= (ReferenceP256.N >> 1), "s must be in the low half");

            Assert.True(SignatureVerifier.Verify(input, signature));
            Assert.True(ReferenceP256.Verify(signer.PublicKey.Bytes, input.ComputeDigest(), signature.Bytes));
        }
    }

    [Fact]
    public void Signature_RefusesOutOfRangeAndHighS()
    {
        using var signer = TestPersonas.CreateA();
        var input = SigningInput.Create(DocumentType.ProfileRetraction, signer.PublicKey, Samples.Retraction().EncodePayload());
        var valid = signer.Sign(input).ToArray();

        var n = ReferenceP256.ToBytes32(ReferenceP256.N);
        var nMinusOne = ReferenceP256.ToBytes32(ReferenceP256.N - 1);
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.FromBytes(new byte[63]));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.FromBytes(new byte[65]));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.FromBytes(new byte[32].Concat(valid.Skip(32)).ToArray()));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.FromBytes(valid.Take(32).Concat(new byte[32]).ToArray()));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.FromBytes(n.Concat(valid.Skip(32)).ToArray()));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.FromBytes(valid.Take(32).Concat(n).ToArray()));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.FromBytes(valid.Take(32).Concat(nMinusOne).ToArray()));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.FromBytes(Enumerable.Repeat((byte)0xFF, 64).ToArray()));

        // The other valid signature with the same r (s' = n - s) is refused: only one encoding is canonical.
        var s = new BigInteger(valid.AsSpan(32), isUnsigned: true, isBigEndian: true);
        var highS = valid.Take(32).Concat(ReferenceP256.ToBytes32(ReferenceP256.N - s)).ToArray();
        Assert.True(ReferenceP256.Verify(signer.PublicKey.Bytes, input.ComputeDigest(), highS), "n - s is a valid ECDSA signature");
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.FromBytes(highS));

        // r = n - 1 with a valid s is in range but is not a signature; it must fail verification, not parsing.
        var wrongR = nMinusOne.Concat(valid.Skip(32)).ToArray();
        Assert.False(SignatureVerifier.Verify(input, ProtocolSignature.FromBytes(wrongR)));
    }

    [Fact]
    public void Signature_DoesNotVerifyForAnotherKeyPayloadTypeOrVersion()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        var payload = Samples.Snapshot().EncodePayload();
        var input = SigningInput.Create(DocumentType.ProfileSnapshot, a.PublicKey, payload);
        var signature = a.Sign(input);

        Assert.True(SignatureVerifier.Verify(input, signature));
        Assert.False(SignatureVerifier.Verify(SigningInput.Create(DocumentType.ProfileSnapshot, b.PublicKey, payload), signature));
        Assert.False(SignatureVerifier.Verify(SigningInput.Create(DocumentType.ProfileRetraction, a.PublicKey, payload), signature));
        var other = (byte[])payload.Clone();
        other[^1] ^= 0x01;
        Assert.False(SignatureVerifier.Verify(SigningInput.Create(DocumentType.ProfileSnapshot, a.PublicKey, other), signature));

        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => b.Sign(input));
    }

    [Fact]
    public void Signer_RefusesToSignAfterDispose()
    {
        var signer = TestPersonas.CreateA();
        var input = SigningInput.Create(DocumentType.ProfileRetraction, signer.PublicKey, Samples.Retraction().EncodePayload());
        signer.Dispose();
        signer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => signer.Sign(input));
        Assert.False(signer.PublicKey.Id.IsEmpty);
    }

    [Fact]
    public void Signer_RefusesPublicOnlyKeys()
    {
        using var a = TestPersonas.CreateA();
        var publicOnly = a.PublicKey.CreateEcdsa();
        using var signer = new EcdsaPersonaSigner(publicOnly);
        var input = SigningInput.Create(DocumentType.ProfileRetraction, signer.PublicKey, Samples.Retraction().EncodePayload());
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => signer.Sign(input));
    }
}
