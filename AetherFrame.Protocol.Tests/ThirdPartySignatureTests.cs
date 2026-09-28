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
            var (r, s) = ParseDer(der);
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
        Assert.InRange(der.Length, 70, 72);

        // The envelope's signature field is exactly 64 bytes: DER shifts the document's end, so the
        // reader sees trailing bytes, and a DER prefix cut to 64 bytes is not a valid r ‖ s.
        var body = SignedDocumentCodec.Assemble(DocumentType.ProfileSnapshot, key, payload, ProtocolSignature.FromBytes(FirstCanonical(platform, key, payload)));
        var withDer = Append(Truncate(body, Layout.SignatureOffset(body)), der);
        ProtocolAssert.Throws(ProtocolError.TrailingBytes, () => SignedDocumentCodec.Verify(withDer));
        ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(Substitute(body, Layout.SignatureOffset(body), der[..64])));
    }

    private static byte[] FirstCanonical(ECDsa platform, PersonaPublicKey key, byte[] payload)
    {
        var rs = platform.SignData(ReferenceProtocol.SigningInput(1, key.Bytes, payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return ProtocolSignature.Normalize(rs).ToArray();
    }

    private static (BigInteger R, BigInteger S) ParseDer(byte[] der)
    {
        Assert.Equal(0x30, der[0]);
        Assert.Equal(der.Length - 2, der[1]);
        var offset = 2;
        var values = new BigInteger[2];
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(0x02, der[offset]);
            var length = der[offset + 1];
            values[index] = new BigInteger(der.AsSpan(offset + 2, length), isUnsigned: true, isBigEndian: true);
            offset += 2 + length;
        }

        Assert.Equal(der.Length, offset);
        return (values[0], values[1]);
    }
}
