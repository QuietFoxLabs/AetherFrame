using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;
using static AetherFrame.Protocol.Tests.DocumentMutations;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Documents that must be refused: each derived from the profile-snapshot or profile-retraction
/// vector with one fault, or signed by a real key over a payload that breaks exactly one schema
/// rule. These are the conformance negatives a second implementation checks against. Every
/// envelope, key and signature rule has an entry, and so does every rule of the name (decision D4):
/// its limits, each family of refused code points, and the three names the rule turned from valid
/// vectors into rejected ones. The general text limit of section 2.3 (32,000 scalars), which no
/// version 1 field can reach any more, is covered by the unit tests only: a second implementation
/// must take it from the specification, not from this file. Two documents carry the final version
/// 1 marker instead of the draft one (decision N3): a draft reader refuses both.
/// </summary>
internal static class RejectedVectorBuilder
{
    /// <summary>
    /// Signs <paramref name="payload"/> with the final version 1 marker, around the library, which
    /// only writes drafts. The key must be the one <paramref name="scalar"/> belongs to.
    /// </summary>
    public static byte[] FinalSnapshot(BigInteger scalar, PersonaPublicKey key, byte[] payload)
    {
        var (x, y) = ReferenceP256.PublicKey(scalar);
        byte[] expected = [0x04, .. x, .. y];
        if (!key.Bytes.SequenceEqual(expected))
        {
            throw new ArgumentException("The key is not the scalar's.", nameof(key));
        }

        using var platform = TestPersonas.CreateEcdsa(scalar);
        var input = ReferenceProtocol.SigningInput(ReferenceProtocol.Final, (byte)DocumentType.ProfileSnapshot, key.Bytes, payload);
        var rs = platform.SignData(input, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var signature = ProtocolSignature.Normalize(rs).ToArray();
        return ReferenceProtocol.Document(ReferenceProtocol.Final, (byte)DocumentType.ProfileSnapshot, key.Bytes, payload, signature);
    }

    public static List<RejectedVector> Build(byte[] baseDocument, byte[] retractionDocument, IPersonaSigner signer, PersonaPublicKey otherKey)
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
        var halfN = ReferenceP256.N >> 1;
        var highS = ReferenceP256.ToBytes32(ReferenceP256.N - new BigInteger(s, true, true));

        // Framing.
        Add("bad-magic", Mutate(baseDocument, 0, (byte)'X'), ProtocolError.InvalidFraming, "first byte is not 'A'");
        Add("wrong-protocol-version", Mutate(baseDocument, Layout.Version + 1, 2), ProtocolError.UnsupportedVersion, "protocol version 0x8002: a draft of version 2");
        Add("protocol-version-zero", Mutate(baseDocument, Layout.Version + 1, 0), ProtocolError.UnsupportedVersion, "protocol version 0x8000: the draft bit with no version");
        Add("unknown-document-type", Mutate(baseDocument, Layout.Type, 3), ProtocolError.UnknownDocumentType, "document type 3");
        Add("document-type-zero", Mutate(baseDocument, Layout.Type, 0), ProtocolError.UnknownDocumentType, "document type 0");
        Add("document-type-255", Mutate(baseDocument, Layout.Type, 255), ProtocolError.UnknownDocumentType, "document type 255");
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

        // Signature form: every boundary of section 6.
        Add("signature-bit-flipped", Flip(baseDocument, length - 1), ProtocolError.SignatureMismatch, "last signature bit changed");
        Add("signature-high-s", Substitute(baseDocument, signatureOffset + 32, highS), ProtocolError.InvalidSignature, "s replaced by n - s: valid ECDSA, not canonical");
        Add("signature-r-zero", Substitute(baseDocument, signatureOffset, new byte[32]), ProtocolError.InvalidSignature, "r = 0");
        Add("signature-s-zero", Substitute(baseDocument, signatureOffset + 32, new byte[32]), ProtocolError.InvalidSignature, "s = 0");
        Add("signature-r-equals-n", Substitute(baseDocument, signatureOffset, n), ProtocolError.InvalidSignature, "r = n");
        Add("signature-s-equals-n", Substitute(baseDocument, signatureOffset + 32, n), ProtocolError.InvalidSignature, "s = n");
        Add("signature-s-half-n-plus-one", Substitute(baseDocument, signatureOffset + 32, ReferenceP256.ToBytes32(halfN + 1)), ProtocolError.InvalidSignature, "s = floor(n/2) + 1: the smallest high s, refused as non-canonical");
        Add("signature-s-half-n", Substitute(baseDocument, signatureOffset + 32, ReferenceP256.ToBytes32(halfN)), ProtocolError.SignatureMismatch, "s = floor(n/2): the largest canonical s is well formed, and with this r a mismatch");
        var swappedError = new BigInteger(r, true, true) > halfN ? ProtocolError.InvalidSignature : ProtocolError.SignatureMismatch;
        Add("signature-r-s-swapped", Substitute(baseDocument, signatureOffset, [.. s, .. r]), swappedError, "r and s swapped: refused as non-canonical when the old r is in the high half, otherwise as a mismatch");

        // Keys: every rule of section 3.
        Add("key-substituted-other-persona", Substitute(baseDocument, Layout.Key, otherKey.ToArray()), ProtocolError.SignatureMismatch, "persona B's key under persona A's signature");
        Add("key-prefix-compressed", Mutate(baseDocument, Layout.Key, 0x02), ProtocolError.InvalidKey, "compressed point prefix 0x02");
        Add("key-prefix-0x03", Mutate(baseDocument, Layout.Key, 0x03), ProtocolError.InvalidKey, "compressed point prefix 0x03");
        Add("key-prefix-0x00", Mutate(baseDocument, Layout.Key, 0x00), ProtocolError.InvalidKey, "prefix 0x00");
        Add("key-all-zero", Substitute(baseDocument, Layout.Key, new byte[65]), ProtocolError.InvalidKey, "65 zero bytes");
        Add("key-off-curve", Flip(baseDocument, Layout.Key + 64), ProtocolError.InvalidKey, "last bit of y changed");
        Add("key-x-equals-p", Substitute(baseDocument, Layout.Key + 1, ReferenceP256.ToBytes32(ReferenceP256.P)), ProtocolError.InvalidKey, "x = p");
        Add("key-x-not-reduced", Substitute(baseDocument, Layout.Key, CurvePoints.SmallXKeyNonReduced), ProtocolError.InvalidKey, "the point with x = 5 encoded with x + p: satisfies the curve equation mod p, so only the range rule refuses it");
        Add("key-y-not-reduced", Substitute(baseDocument, Layout.Key, CurvePoints.SmallYKeyNonReduced), ProtocolError.InvalidKey, "the point with y = 1 encoded with y + p: satisfies the curve equation mod p, so only the range rule refuses it");

        // Retraction envelope, from the retraction vector.
        Add("retraction-truncated-last-byte", Truncate(retractionDocument, retractionDocument.Length - 1), ProtocolError.Truncated, "retraction one byte short");
        Add("retraction-extra-trailing-byte", Append(retractionDocument, 0), ProtocolError.TrailingBytes, "retraction with one byte after the signature");
        Add("retraction-type-changed-to-snapshot", Mutate(retractionDocument, Layout.Type, 1), ProtocolError.SignatureMismatch, "a retraction's signature under the snapshot type: the type is signed");

        // Validly signed payloads that break one schema rule each.
        foreach (var (caseName, error) in new (string, ProtocolError)[]
        {
            ("schema 3", ProtocolError.UnsupportedVersion), ("schema 2 over schema 1 fields", ProtocolError.InvalidValue), ("zero profile id", ProtocolError.InvalidValue), ("zero revision id", ProtocolError.InvalidValue),
            ("createdAt over max", ProtocolError.InvalidValue), ("name with NUL", ProtocolError.InvalidText), ("name invalid utf8", ProtocolError.InvalidText),
            ("name overlong utf8", ProtocolError.InvalidText), ("name encoded surrogate", ProtocolError.InvalidText), ("name length huge", ProtocolError.LimitExceeded),
            // The name rule, decision D4: its limits, each refused family, and the names it turned into rejected vectors.
            ("name empty", ProtocolError.InvalidLength), ("name one scalar over max", ProtocolError.LimitExceeded), ("name one byte over max bytes", ProtocolError.LimitExceeded),
            ("name length claims more than present", ProtocolError.Truncated), ("name with line break", ProtocolError.InvalidText), ("name with tab", ProtocolError.InvalidText),
            ("name with DEL", ProtocolError.InvalidText), ("name with C1 control", ProtocolError.InvalidText), ("name with line separator", ProtocolError.InvalidText),
            ("name with paragraph separator", ProtocolError.InvalidText), ("name with byte order mark", ProtocolError.InvalidText), ("name with right-to-left override", ProtocolError.InvalidText),
            ("name with right-to-left isolate", ProtocolError.InvalidText), ("name with arabic letter mark", ProtocolError.InvalidText), ("name with zero width space", ProtocolError.InvalidText),
            ("name with soft hyphen", ProtocolError.InvalidText), ("name with interlinear annotation", ProtocolError.InvalidText), ("name with tag character", ProtocolError.InvalidText),
            ("name of the old unicode vector", ProtocolError.InvalidText), ("name of the old maximal vector", ProtocolError.LimitExceeded),
            ("name length 300 with few bytes present", ProtocolError.LimitExceeded), ("name with left-to-right mark", ProtocolError.InvalidText), ("name with pop directional formatting", ProtocolError.InvalidText),
            ("name with first strong isolate", ProtocolError.InvalidText), ("name with mongolian vowel separator", ProtocolError.InvalidText), ("name with word joiner", ProtocolError.InvalidText),
            ("name with deprecated format character", ProtocolError.InvalidText), ("name with language tag", ProtocolError.InvalidText),
            ("images unsorted", ProtocolError.NotCanonical), ("images duplicate", ProtocolError.NotCanonical), ("image zero digest", ProtocolError.InvalidValue),
            ("image format 4", ProtocolError.InvalidValue), ("image zero bytes", ProtocolError.InvalidValue), ("image bytes u64 max", ProtocolError.LimitExceeded),
            ("image zero width", ProtocolError.InvalidValue), ("image pixels over max", ProtocolError.LimitExceeded), ("images total bytes over max", ProtocolError.LimitExceeded),
            ("image count huge", ProtocolError.LimitExceeded), ("image count more than present", ProtocolError.Truncated), ("trailing byte", ProtocolError.TrailingBytes),
            // The per-field limits a second implementation could omit and still pass the rest (review L2).
            ("createdAt u64 max", ProtocolError.InvalidValue), ("image zero asset id", ProtocolError.InvalidValue), ("image format 0", ProtocolError.InvalidValue),
            ("image bytes over max", ProtocolError.LimitExceeded), ("image width over max", ProtocolError.LimitExceeded), ("image zero height", ProtocolError.InvalidValue),
            ("image height over max", ProtocolError.LimitExceeded), ("image width u32 max", ProtocolError.LimitExceeded), ("image count 9 declared", ProtocolError.LimitExceeded),
            ("image count less than present", ProtocolError.TrailingBytes), ("truncated image", ProtocolError.Truncated), ("nested count abuse", ProtocolError.LimitExceeded),
        })
        {
            var document = PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, SnapshotPayloadCases.Build(caseName));
            Add("signed-snapshot-" + caseName.Replace(' ', '-'), document, error, "validly signed snapshot payload: " + caseName, deterministic: false);
        }

        Add("signed-retraction-schema-0", PayloadBuilder.Signed(DocumentType.ProfileRetraction, signer, PayloadBuilder.Retraction(schema: 0)), ProtocolError.UnsupportedVersion, "validly signed retraction payload with schema 0", deterministic: false);
        Add("signed-retraction-schema-2", PayloadBuilder.Signed(DocumentType.ProfileRetraction, signer, PayloadBuilder.Retraction(schema: 2)), ProtocolError.UnsupportedVersion, "validly signed retraction payload with schema 2", deterministic: false);
        Add("signed-retraction-zero-profile-id", PayloadBuilder.Signed(DocumentType.ProfileRetraction, signer, PayloadBuilder.Retraction(profileId: new byte[16])), ProtocolError.InvalidValue, "validly signed retraction payload with an all-zero profile id", deterministic: false);
        Add("signed-retraction-issuedAt-over-max", PayloadBuilder.Signed(DocumentType.ProfileRetraction, signer, PayloadBuilder.Retraction(issuedAt: (ulong)ProtocolLimits.MaxUnixSeconds + 1)), ProtocolError.InvalidValue, "validly signed retraction payload with issuedAt one over the maximum", deterministic: false);
        Add("signed-retraction-truncated", PayloadBuilder.Signed(DocumentType.ProfileRetraction, signer, PayloadBuilder.Retraction()[..20]), ProtocolError.Truncated, "validly signed retraction payload cut inside issuedAt", deterministic: false);
        Add("signed-retraction-trailing-byte", PayloadBuilder.Signed(DocumentType.ProfileRetraction, signer, PayloadBuilder.Retraction(trailing: [0])), ProtocolError.TrailingBytes, "validly signed retraction payload with a byte after issuedAt", deterministic: false);

        // Schema 2, the layout (section 8.5): every fault case, each a validly signed payload.
        foreach (var (caseName, error) in LayoutPayload.Faults)
        {
            Add("signed-layout-" + caseName.Replace(' ', '-'), PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, LayoutPayload.Case(caseName)), error, "validly signed layout payload: " + caseName, deterministic: false);
        }

        // The draft marker, decision N3: the sample snapshot signed as a frozen version 1 document
        // (version 1 and the tag without "-draft"). A draft reader refuses it by its version; with
        // its version changed to the draft's, its signature is over the other tag and never verifies.
        var payload = Samples.Snapshot().EncodePayload();
        var final = FinalSnapshot(TestPersonas.ScalarA, signer.PublicKey, payload);
        Add("final-version-1-document", final, ProtocolError.UnsupportedVersion, "a validly signed final version 1 document (version 1, tag without -draft): refused by a draft reader", deterministic: false);
        Add("final-signature-under-draft-version", Mutate(final, Layout.Version, 0x80), ProtocolError.SignatureMismatch, "the final document with its version changed to the draft's 0x8001: signed over the final tag, so it never verifies as a draft", deterministic: false);
        return list;
    }
}
