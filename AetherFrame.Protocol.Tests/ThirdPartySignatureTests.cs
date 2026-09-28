using System;
using System.Numerics;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;
using Xunit;
using static AetherFrame.Protocol.Tests.DocumentMutations;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Signatures made outside the protocol's signer, as a second implementation (OpenSSL, WebCrypto,
/// another language) would make them: DER-encoded and with s in either half. The protocol accepts
/// exactly the P1363 low-S form of every such signature and nothing else, so an implementer must
/// convert from DER and normalize s; roughly half of raw signatures are refused until they do.
/// </summary>
public class ThirdPartySignatureTests
{
    [Fact]
    public void PlatformDerSignatures_AreAcceptedOnceNormalized_AndHighSIsRefusedRaw()
    {
        using var platform = TestPersonas.CreateEcdsa(TestPersonas.ScalarA);
        var key = PersonaPublicKey.FromEcdsa(platform);
        var payload = Samples.Snapshot().EncodePayload();
        var input = ReferenceProtocol.SigningInput(1, key.Bytes, payload);
        var digest = SHA256.HashData(input);

        var sawLow = false;
        var sawHigh = false;
        for (var round = 0; round < 64 && !(sawLow && sawHigh); round++)
        {
            var der = platform.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var (r, s) = DerEcdsaSignature.Parse(der);
            Assert.Equal(DerEcdsaSignature.Length(r, s), der.Length);
            byte[] raw = [.. ReferenceP256.ToBytes32(r), .. ReferenceP256.ToBytes32(s)];
            Assert.True(ReferenceP256.Verify(key.Bytes, digest, raw), "a platform signature is valid ECDSA whichever half s is in");
            ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.FromBytes(der));

            var rawDocument = ReferenceProtocol.Document(1, key.Bytes, payload, raw);
            if (s > (ReferenceP256.N >> 1))
            {
                sawHigh = true;
                ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => SignedDocumentCodec.Verify(rawDocument));
                byte[] normalized = [.. ReferenceP256.ToBytes32(r), .. ReferenceP256.ToBytes32(ReferenceP256.N - s)];
                Assert.Equal(key.Id, SignedDocumentCodec.Verify(ReferenceProtocol.Document(1, key.Bytes, payload, normalized)).Persona);
            }
            else
            {
                sawLow = true;
                Assert.Equal(key.Id, SignedDocumentCodec.Verify(rawDocument).Persona);
            }
        }

        Assert.True(sawLow && sawHigh, "64 platform signatures land in both halves (the chance of missing one is 2^-63)");
    }

    [Fact]
    public void ADerSignatureInsideTheEnvelope_IsNeverAccepted()
    {
        using var platform = TestPersonas.CreateEcdsa(TestPersonas.ScalarA);
        var key = PersonaPublicKey.FromEcdsa(platform);
        var payload = Samples.Snapshot().EncodePayload();
        var der = platform.SignData(ReferenceProtocol.SigningInput(1, key.Bytes, payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        // Whatever length the platform produced (68 to 72 bytes are all common; shorter is legal), it
        // is a well-formed DER signature whose length follows from r and s: no assumption about the
        // usual length is made, so this cannot fail on a short signature.
        var (r, s) = DerEcdsaSignature.Parse(der);
        Assert.Equal(DerEcdsaSignature.Length(r, s), der.Length);
        Assert.InRange(der.Length, DerEcdsaSignature.MinLength, DerEcdsaSignature.MaxLength);

        // The envelope's signature field is exactly 64 bytes: DER of any length shifts the document's
        // end, so the reader sees trailing bytes, and the first 64 bytes of a DER signature (padded
        // with zeros when it is shorter) are never a valid r ‖ s.
        var body = SignedDocumentCodec.Assemble(DocumentType.ProfileSnapshot, key, payload, ProtocolSignature.FromBytes(FirstCanonical(platform, key, payload)));
        var withDer = Append(Truncate(body, Layout.SignatureOffset(body)), der);
        ProtocolAssert.Throws(ProtocolError.TrailingBytes, () => SignedDocumentCodec.Verify(withDer));
        var prefix = new byte[64];
        der.AsSpan(0, Math.Min(64, der.Length)).CopyTo(prefix);
        ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(Substitute(body, Layout.SignatureOffset(body), prefix)));
    }

    private static byte[] FirstCanonical(ECDsa platform, PersonaPublicKey key, byte[] payload)
    {
        var rs = platform.SignData(ReferenceProtocol.SigningInput(1, key.Bytes, payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return ProtocolSignature.Normalize(rs).ToArray();
    }

    [Fact]
    public void PlatformDerSignatures_OfEveryLength_ParseAndVerify()
    {
        // 2000 platform signatures: r and s each need 33 bytes about half the time and fewer than
        // 32 about one time in 256, so lengths from 68 to 72 all occur here and shorter ones are
        // legal. Each must parse as strict DER whose length follows from its integers, and verify.
        using var platform = TestPersonas.CreateEcdsa(TestPersonas.ScalarA);
        var key = PersonaPublicKey.FromEcdsa(platform);
        var input = ReferenceProtocol.SigningInput(2, key.Bytes, Samples.Retraction().EncodePayload());
        var digest = SHA256.HashData(input);
        var lengths = new System.Collections.Generic.HashSet<int>();
        for (var round = 0; round < 2000; round++)
        {
            var der = platform.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var (r, s) = DerEcdsaSignature.Parse(der);
            Assert.Equal(DerEcdsaSignature.Length(r, s), der.Length);
            Assert.Equal(der, DerEcdsaSignature.Encode(r, s));
            byte[] raw = [.. ReferenceP256.ToBytes32(r), .. ReferenceP256.ToBytes32(s)];
            Assert.True(platform.VerifyData(input, raw, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            if (round < 8)
            {
                Assert.True(ReferenceP256.Verify(key.Bytes, digest, raw));
            }

            lengths.Add(der.Length);
        }

        Assert.Contains(72, lengths);
        Assert.Contains(70, lengths);
        Assert.All(lengths, length => Assert.InRange(length, DerEcdsaSignature.MinLength, DerEcdsaSignature.MaxLength));
    }

    [Fact]
    public void DerReader_AcceptsEveryLegalLengthAndRefusesEveryNonCanonicalEncoding()
    {
        // Deterministic: the shortest and longest legal signatures, one of each length between, and
        // the encodings DER forbids (a padding zero, a negative integer, long-form lengths, wrong tags,
        // trailing bytes, zero, and n itself).
        var one = BigInteger.One;
        var nMinusOne = ReferenceP256.N - 1;
        Assert.Equal(8, DerEcdsaSignature.Encode(one, one).Length);
        Assert.Equal(72, DerEcdsaSignature.Encode(nMinusOne, nMinusOne).Length);
        for (var bits = 1; bits <= 256; bits += 5)
        {
            var r = (BigInteger.One << bits) - 1;
            var s = BigInteger.One << (bits - 1);
            if (r >= ReferenceP256.N)
            {
                r = nMinusOne;
            }

            var der = DerEcdsaSignature.Encode(r, s);
            var (parsedR, parsedS) = DerEcdsaSignature.Parse(der);
            Assert.Equal((r, s), (parsedR, parsedS));
            Assert.Equal(DerEcdsaSignature.Length(r, s), der.Length);
        }

        var valid = DerEcdsaSignature.Encode(one, one);
        Assert.True(DerEcdsaSignature.TryParse(valid, out _, out _, out _));
        foreach (var (name, bytes) in new (string, byte[])[]
        {
            ("padding zero before a small byte", [0x30, 0x07, 0x02, 0x02, 0x00, 0x01, 0x02, 0x01, 0x01]),
            ("negative integer", [0x30, 0x06, 0x02, 0x01, 0x80, 0x02, 0x01, 0x01]),
            ("zero integer", [0x30, 0x06, 0x02, 0x01, 0x00, 0x02, 0x01, 0x01]),
            ("long-form sequence length", [0x30, 0x81, 0x06, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01]),
            ("wrong sequence tag", [0x31, 0x06, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01]),
            ("wrong integer tag", [0x30, 0x06, 0x04, 0x01, 0x01, 0x02, 0x01, 0x01]),
            ("trailing byte", [0x30, 0x06, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01, 0x00]),
            ("sequence length too short", [0x30, 0x05, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01]),
            ("three integers", [0x30, 0x09, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01]),
            ("integer of 34 bytes", DerEcdsaSignature.Encode(BigInteger.One << 263, one)),
            ("s equals n", DerEcdsaSignature.Encode(one, ReferenceP256.N)),
            ("r equals n", DerEcdsaSignature.Encode(ReferenceP256.N, one)),
            ("empty", []),
        })
        {
            Assert.False(DerEcdsaSignature.TryParse(bytes, out _, out _, out var error), name);
            Assert.NotNull(error);
        }
    }
}
