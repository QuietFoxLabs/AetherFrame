using System;
using System.Linq;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Signing;
using Xunit;
using static AetherFrame.Protocol.Tests.DocumentMutations;

namespace AetherFrame.Protocol.Tests;

/// <summary>Every framing, key, payload and signature refusal of the envelope, each with its own error.</summary>
public class SignedDocumentFramingTests
{
    [Fact]
    public void Verify_RefusesEachFramingFaultWithItsOwnError()
    {
        using var signer = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(signer);
        var payloadLength = (uint)Layout.PayloadLengthOf(document);

        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => SignedDocumentCodec.Verify(new byte[ProtocolLimits.MaxDocumentBytes + 1]));
        ProtocolAssert.Throws(ProtocolError.Truncated, () => SignedDocumentCodec.Verify([]));
        ProtocolAssert.Throws(ProtocolError.Truncated, () => SignedDocumentCodec.Verify("AFP"u8));
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => SignedDocumentCodec.Verify("AFPX"u8));
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => SignedDocumentCodec.Verify(Mutate(document, 0, 0x61)));
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => SignedDocumentCodec.Verify(new byte[document.Length]));

        ProtocolAssert.Throws(ProtocolError.UnsupportedVersion, () => SignedDocumentCodec.Verify(Mutate(document, Layout.Version + 1, 0x02)));
        ProtocolAssert.Throws(ProtocolError.UnsupportedVersion, () => SignedDocumentCodec.Verify(Mutate(document, Layout.Version + 1, 0x00)));
        ProtocolAssert.Throws(ProtocolError.UnsupportedVersion, () => SignedDocumentCodec.Verify(Mutate(document, Layout.Version, 0x01)));
        ProtocolAssert.Throws(ProtocolError.UnsupportedVersion, () => SignedDocumentCodec.Verify(Mutate(document, Layout.Version, 0xFF)));

        ProtocolAssert.Throws(ProtocolError.UnknownDocumentType, () => SignedDocumentCodec.Verify(Mutate(document, Layout.Type, 0x00)));
        ProtocolAssert.Throws(ProtocolError.UnknownDocumentType, () => SignedDocumentCodec.Verify(Mutate(document, Layout.Type, 0x03)));
        ProtocolAssert.Throws(ProtocolError.UnknownDocumentType, () => SignedDocumentCodec.Verify(Mutate(document, Layout.Type, 0xFF)));

        ProtocolAssert.Throws(ProtocolError.InvalidLength, () => SignedDocumentCodec.Verify(WithPayloadLength(document, 0)));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => SignedDocumentCodec.Verify(WithPayloadLength(document, 0xFFFFFFFF)));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => SignedDocumentCodec.Verify(WithPayloadLength(document, 0x80000000)));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => SignedDocumentCodec.Verify(WithPayloadLength(document, (uint)ProtocolLimits.MaxPayloadBytes + 1)));
        ProtocolAssert.Throws(ProtocolError.Truncated, () => SignedDocumentCodec.Verify(WithPayloadLength(document, (uint)ProtocolLimits.MaxPayloadBytes)));
        ProtocolAssert.Throws(ProtocolError.Truncated, () => SignedDocumentCodec.Verify(WithPayloadLength(document, payloadLength + 1)));
        ProtocolAssert.Throws(ProtocolError.TrailingBytes, () => SignedDocumentCodec.Verify(WithPayloadLength(document, payloadLength - 1)));
        ProtocolAssert.Throws(ProtocolError.Truncated, () => SignedDocumentCodec.Verify(Truncate(document, document.Length - 1)));
        ProtocolAssert.Throws(ProtocolError.TrailingBytes, () => SignedDocumentCodec.Verify(Append(document, 0)));
        ProtocolAssert.Throws(ProtocolError.TrailingBytes, () => SignedDocumentCodec.Verify(document.Concat(document).ToArray()));
    }

    [Fact]
    public void Verify_RefusesTamperedKeyPayloadAndSignature()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        var document = Samples.SignedSnapshot(a);
        var signatureOffset = Layout.SignatureOffset(document);

        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => SignedDocumentCodec.Verify(Mutate(document, Layout.Key, 0x02)));
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => SignedDocumentCodec.Verify(Flip(document, Layout.Key + 40)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(Substitute(document, Layout.Key, b.PublicKey.ToArray())));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(Flip(document, Layout.Payload)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(Flip(document, signatureOffset - 1)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(Flip(document, signatureOffset + 5)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(Flip(document, document.Length - 1)));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => SignedDocumentCodec.Verify(Substitute(document, signatureOffset, new byte[64])));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => SignedDocumentCodec.Verify(Substitute(document, signatureOffset, Enumerable.Repeat((byte)0xFF, 64).ToArray())));

        // A snapshot's signature presented under the retraction type: the type is signed, so it is a mismatch, never a decode.
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(Mutate(document, Layout.Type, 0x02)));
    }

    [Fact]
    public void Verify_DerivesThePersonaFromTheKeyItVerifiedWith()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        var payload = Samples.Snapshot().EncodePayload();
        var signature = a.Sign(SigningInput.Create(DocumentType.ProfileSnapshot, a.PublicKey, payload));

        var forged = SignedDocumentCodec.Assemble(DocumentType.ProfileSnapshot, b.PublicKey, payload, signature);
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(forged));

        var genuine = SignedDocumentCodec.Assemble(DocumentType.ProfileSnapshot, a.PublicKey, payload, signature);
        Assert.Equal(a.PublicKey.Id, SignedDocumentCodec.Verify(genuine).Persona);
    }
}
