using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;
using static AetherFrame.Protocol.Tests.DocumentMutations;

namespace AetherFrame.Protocol.Tests;

/// <summary>Documents that must be refused, each derived from the profile-snapshot vector with one fault, plus a few signed by a real key over a payload that breaks the schema.</summary>
internal static class RejectedVectorBuilder
{
    public static List<RejectedVector> Build(byte[] baseDocument, IPersonaSigner signer, PersonaPublicKey otherKey)
    {
        var list = new List<RejectedVector>();
        void Add(string name, byte[] document, ProtocolError error, string reason, bool deterministic = true) =>
            list.Add(new RejectedVector { Name = name, Document = Hex.Of(document), Error = error.ToString(), Reason = reason, Deterministic = deterministic });

        var length = baseDocument.Length;
        var payloadLength = (uint)Layout.PayloadLengthOf(baseDocument);
        var signatureOffset = Layout.SignatureOffset(baseDocument);
        var r = baseDocument.AsSpan(signatureOffset, 32).ToArray();
        var s = baseDocument.AsSpan(signatureOffset + 32, 32).ToArray();
        var n = ReferenceP256.ToBytes32(ReferenceP256.N);
        var highS = ReferenceP256.ToBytes32(ReferenceP256.N - new BigInteger(s, true, true));

        Add("bad-magic", Mutate(baseDocument, 0, (byte)'X'), ProtocolError.InvalidFraming, "first byte is not 'A'");
        Add("wrong-protocol-version", Mutate(baseDocument, Layout.Version + 1, 2), ProtocolError.UnsupportedVersion, "protocol version 2");
        Add("protocol-version-zero", Mutate(baseDocument, Layout.Version + 1, 0), ProtocolError.UnsupportedVersion, "protocol version 0");
        Add("unknown-document-type", Mutate(baseDocument, Layout.Type, 3), ProtocolError.UnknownDocumentType, "document type 3");
        Add("document-type-zero", Mutate(baseDocument, Layout.Type, 0), ProtocolError.UnknownDocumentType, "document type 0");
        Add("type-changed-to-retraction", Mutate(baseDocument, Layout.Type, 2), ProtocolError.SignatureMismatch, "a snapshot's signature under the retraction type: the type is signed");
        Add("truncated-last-byte", Truncate(baseDocument, length - 1), ProtocolError.Truncated, "one byte short");
        Add("truncated-inside-payload", Truncate(baseDocument, Layout.Payload + 10), ProtocolError.Truncated, "cut inside the payload");
        Add("truncated-after-header", Truncate(baseDocument, Layout.Payload), ProtocolError.Truncated, "nothing after the payload length");
        Add("extra-trailing-byte", Append(baseDocument, 0), ProtocolError.TrailingBytes, "one byte after the signature");
        Add("payload-length-zero", WithPayloadLength(baseDocument, 0), ProtocolError.InvalidLength, "declares an empty payload");
        Add("payload-length-max-uint", WithPayloadLength(baseDocument, 0xFFFFFFFF), ProtocolError.LimitExceeded, "declares 4 GiB");
        Add("payload-length-one-over-limit", WithPayloadLength(baseDocument, (uint)ProtocolLimits.MaxPayloadBytes + 1), ProtocolError.LimitExceeded, "declares MaxPayloadBytes + 1");
        Add("payload-length-one-too-many", WithPayloadLength(baseDocument, payloadLength + 1), ProtocolError.Truncated, "declares one byte more than present");
        Add("payload-length-one-too-few", WithPayloadLength(baseDocument, payloadLength - 1), ProtocolError.TrailingBytes, "declares one byte less than present");
        Add("payload-bit-flipped", Flip(baseDocument, Layout.Payload + 10), ProtocolError.SignatureMismatch, "one payload bit changed");
        Add("signature-bit-flipped", Flip(baseDocument, length - 1), ProtocolError.SignatureMismatch, "last signature bit changed");
        Add("signature-high-s", Substitute(baseDocument, signatureOffset + 32, highS), ProtocolError.InvalidSignature, "s replaced by n - s: valid ECDSA, not canonical");
        Add("signature-r-zero", Substitute(baseDocument, signatureOffset, new byte[32]), ProtocolError.InvalidSignature, "r = 0");
        Add("signature-s-equals-n", Substitute(baseDocument, signatureOffset + 32, n), ProtocolError.InvalidSignature, "s = n");
        var swappedError = new BigInteger(r, true, true) > (ReferenceP256.N >> 1) ? ProtocolError.InvalidSignature : ProtocolError.SignatureMismatch;
        Add("signature-r-s-swapped", Substitute(baseDocument, signatureOffset, [.. s, .. r]), swappedError, "r and s swapped: refused as non-canonical when the old r is in the high half, otherwise as a mismatch");
        Add("key-substituted-other-persona", Substitute(baseDocument, Layout.Key, otherKey.ToArray()), ProtocolError.SignatureMismatch, "persona B's key under persona A's signature");
        Add("key-prefix-compressed", Mutate(baseDocument, Layout.Key, 0x02), ProtocolError.InvalidKey, "compressed point prefix");
        Add("key-off-curve", Flip(baseDocument, Layout.Key + 64), ProtocolError.InvalidKey, "last bit of y changed");
        Add("key-x-equals-p", Substitute(baseDocument, Layout.Key + 1, ReferenceP256.ToBytes32(ReferenceP256.P)), ProtocolError.InvalidKey, "x = p");

        foreach (var (caseName, error) in new (string, ProtocolError)[]
        {
            ("schema 2", ProtocolError.UnsupportedVersion), ("zero profile id", ProtocolError.InvalidValue), ("createdAt over max", ProtocolError.InvalidValue),
            ("name with NUL", ProtocolError.InvalidText), ("name invalid utf8", ProtocolError.InvalidText), ("name length huge", ProtocolError.LimitExceeded),
            ("images unsorted", ProtocolError.NotCanonical), ("images duplicate", ProtocolError.NotCanonical), ("image format 4", ProtocolError.InvalidValue),
            ("image pixels over max", ProtocolError.LimitExceeded), ("images total bytes over max", ProtocolError.LimitExceeded), ("image count huge", ProtocolError.LimitExceeded),
            ("trailing byte", ProtocolError.TrailingBytes),
        })
        {
            var document = PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, SnapshotPayloadCases.Build(caseName));
            Add("signed-snapshot-" + caseName.Replace(' ', '-'), document, error, "validly signed snapshot payload: " + caseName, deterministic: false);
        }

        Add("signed-retraction-schema-2", PayloadBuilder.Signed(DocumentType.ProfileRetraction, signer, PayloadBuilder.Retraction(schema: 2)), ProtocolError.UnsupportedVersion, "validly signed retraction payload with schema 2", deterministic: false);
        Add("signed-retraction-trailing-byte", PayloadBuilder.Signed(DocumentType.ProfileRetraction, signer, PayloadBuilder.Retraction(trailing: [0])), ProtocolError.TrailingBytes, "validly signed retraction payload with a byte after issuedAt", deterministic: false);
        return list;
    }
}
