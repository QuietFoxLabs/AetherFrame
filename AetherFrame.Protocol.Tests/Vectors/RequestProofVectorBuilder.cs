using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Requests;
using AetherFrame.Protocol.Signing;
using static AetherFrame.Protocol.Tests.DocumentMutations;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// The request proof vectors (docs/networking/ProtocolSpecification-v1.md, sections 11 and 14):
/// valid proofs for three of the document vectors, one of them again at the longest deployment
/// name (the largest proof), and proofs a server refuses, each with one fault, or valid but checked
/// with another deployment, another document, another persona's document or a document that does
/// not verify. Every rule of the proof's reading order (section 14.3) and of its matching with a
/// submission (section 14.4) has an entry, except the document's size limit, which the unit tests
/// cover.
/// </summary>
internal static class RequestProofVectorBuilder
{
    public const string Deployment = ProofSamples.DeploymentText;

    public sealed record ValidProof(string Name, string Persona, string Document, string Deployment, string Challenge);

    public static IReadOnlyList<ValidProof> Valid() =>
    [
        new("submit-profile-snapshot", "A", "profile-snapshot", Deployment, ProofSamples.ChallengeText),
        new("submit-profile-layout-snapshot", "A", "profile-layout-snapshot", Deployment, "chl_" + string.Concat(Enumerable.Repeat("e8", 32))),
        new("submit-profile-retraction", "B", "profile-retraction", Deployment, "chl_" + string.Concat(Enumerable.Repeat("f9", 32))),
        new("submit-at-the-longest-deployment-name", "A", "profile-snapshot", ProofSamples.LongestDeploymentText, "chl_" + string.Concat(Enumerable.Repeat("a5", 32))),
    ];

    public static List<RequestProofVector> BuildValid(Func<string, byte[]> document, IReadOnlyDictionary<string, EcdsaPersonaSigner> signers)
    {
        var list = new List<RequestProofVector>();
        foreach (var valid in Valid())
        {
            var signer = signers[valid.Persona];
            var bytes = document(valid.Document);
            var deployment = DeploymentName.Parse(valid.Deployment);
            var challenge = RequestChallenge.Parse(valid.Challenge);
            var proof = RequestProofCodec.Sign(bytes, deployment, challenge, signer);
            var digest = SHA256.HashData(bytes);
            var input = SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, signer.PublicKey, deployment, challenge, digest);
            list.Add(new RequestProofVector
            {
                Name = valid.Name,
                Persona = valid.Persona,
                Document = valid.Document,
                Deployment = valid.Deployment,
                Challenge = valid.Challenge,
                SubjectDigest = Hex.Of(digest),
                SigningInput = Hex.Of(input.Bytes),
                Digest = Hex.Of(input.ComputeDigest()),
                Signature = Hex.Of(proof.AsSpan(proof.Length - 64)),
                Proof = Hex.Of(proof),
            });
        }

        return list;
    }

    /// <summary>
    /// The refusals, derived from <paramref name="baseProof"/>: persona A's valid proof of the
    /// profile-snapshot document at <see cref="Deployment"/>. Checked with that document and that
    /// deployment unless the entry names others.
    /// </summary>
    public static List<RejectedProofVector> BuildRejected(byte[] baseProof, Func<string, byte[]> document, Func<string, byte[]> rejectedDocument, EcdsaPersonaSigner a, EcdsaPersonaSigner b)
    {
        var list = new List<RejectedProofVector>();
        void Add(string name, byte[] proof, ProtocolError error, string reason, string documentName = "profile-snapshot", string deployment = Deployment, bool deterministic = true, string? documentSet = null) =>
            list.Add(new RejectedProofVector { Name = name, Proof = Hex.Of(proof), Document = documentName, DocumentSet = documentSet, Deployment = deployment, Error = error.ToString(), Reason = reason, Deterministic = deterministic });

        var n = Deployment.Length;
        var challenge = 73 + n;
        var digest = 105 + n;
        var signature = 137 + n;
        var s = new BigInteger(baseProof.AsSpan(signature + 32, 32), isUnsigned: true, isBigEndian: true);
        var key = baseProof.AsSpan(7, 65).ToArray();
        var subject = baseProof.AsSpan(digest, 32).ToArray();
        var challengeBytes = baseProof.AsSpan(challenge, 32).ToArray();
        var baseSignature = baseProof.AsSpan(signature, 64).ToArray();
        byte[] RenamedBytes(byte[] name) => ReferenceProtocol.Proof(1, key, name, challengeBytes, subject, baseSignature);
        byte[] Renamed(string name) => RenamedBytes(System.Text.Encoding.ASCII.GetBytes(name));

        // Framing, version and kind (section 14.3, steps 1 to 4).
        Add("proof-bad-magic", Mutate(baseProof, 3, (byte)'X'), ProtocolError.InvalidFraming, "magic AFRX");
        Add("proof-document-magic", Substitute(baseProof, 0, "AFPD"u8.ToArray()), ProtocolError.InvalidFraming, "a signed document's magic: a document is never read as a proof");
        Add("proof-over-size-limit", Append(baseProof, new byte[ProtocolLimits.MaxRequestProofBytes + 1 - baseProof.Length]), ProtocolError.LimitExceeded, "455 bytes, one over the largest proof");
        Add("proof-final-version", Substitute(baseProof, 4, [0x00, 0x01]), ProtocolError.UnsupportedVersion, "version 1, the final marker: a draft reader refuses it");
        Add("proof-version-2-draft", Substitute(baseProof, 4, [0x80, 0x02]), ProtocolError.UnsupportedVersion, "version 0x8002, a draft of version 2");
        Add("proof-kind-0", Mutate(baseProof, 6, 0), ProtocolError.InvalidValue, "proof kind 0");
        Add("proof-kind-9", Mutate(baseProof, 6, 9), ProtocolError.InvalidValue, "proof kind 9, not defined in version 1");
        Add("proof-kind-rewritten-to-lookup", Mutate(baseProof, 6, 6), ProtocolError.SignatureMismatch, "the kind byte changed to 6, a lookup: the kind is signed, so the signature fails");
        Add("proof-kind-255", Mutate(baseProof, 6, 255), ProtocolError.InvalidValue, "proof kind 255");

        // The deployment name (section 14.1) and the challenge (section 14.2).
        Add("proof-deployment-length-0", Mutate(baseProof, 72, 0), ProtocolError.InvalidLength, "an empty deployment name");
        Add("proof-deployment-length-254", Mutate(baseProof, 72, 254), ProtocolError.LimitExceeded, "a declared name one byte over the limit");
        Add("proof-deployment-length-253-short", Mutate(baseProof, 72, 253), ProtocolError.Truncated, "a declared name of 253 bytes with fewer remaining");
        Add("proof-deployment-uppercase", Mutate(baseProof, 73, (byte)'P'), ProtocolError.InvalidValue, "Plates.example.com: names are lowercase, never folded");
        Add("proof-deployment-trailing-dot", Mutate(baseProof, 73 + n - 1, (byte)'.'), ProtocolError.InvalidValue, "a name ending in a dot, an empty last label");
        Add("proof-deployment-ipv4", Renamed("127.0.0.1"), ProtocolError.InvalidValue, "an IPv4 literal: the last label starts with a digit");
        Add("proof-deployment-hex-ipv4", Renamed("0x7f000001"), ProtocolError.InvalidValue, "127.0.0.1 in hex, which URL parsers accept: the last label starts with a digit");
        Add("proof-deployment-with-port", Renamed("plates.example.com:443"), ProtocolError.InvalidValue, "a port is not part of the name");
        Add("proof-deployment-underscore", Renamed("plates_example.com"), ProtocolError.InvalidValue, "an underscore");
        Add("proof-deployment-label-64", Renamed(new string('a', 64) + ".com"), ProtocolError.InvalidValue, "a label of 64 bytes");
        Add("proof-deployment-leading-hyphen", Renamed("-plates.example.com"), ProtocolError.InvalidValue, "a label starting with a hyphen");
        Add("proof-deployment-trailing-hyphen", Renamed("plates-.example.com"), ProtocolError.InvalidValue, "a label ending with a hyphen");
        Add("proof-deployment-utf8-letter", RenamedBytes(System.Text.Encoding.UTF8.GetBytes("pl" + char.ConvertFromUtf32(0xE4) + "tes.example.com")), ProtocolError.InvalidValue, "U+00E4 in UTF-8: a byte outside ASCII; names carry A-labels only");
        Add("proof-deployment-ideographic-full-stop", RenamedBytes(System.Text.Encoding.UTF8.GetBytes("plates" + char.ConvertFromUtf32(0x3002) + "example.com")), ProtocolError.InvalidValue, "U+3002, which IDNA mapping would turn into a dot: names are never mapped");
        Add("proof-challenge-all-zero", Substitute(baseProof, challenge, new byte[32]), ProtocolError.InvalidValue, "the all-zero challenge");

        // Lengths and trailing bytes.
        Add("proof-truncated-last-byte", Truncate(baseProof, baseProof.Length - 1), ProtocolError.Truncated, "one byte short");
        Add("proof-truncated-in-deployment", Truncate(baseProof, 80), ProtocolError.Truncated, "cut inside the deployment name");
        Add("proof-truncated-after-kind", Truncate(baseProof, 7), ProtocolError.Truncated, "nothing after the kind");
        Add("proof-extra-trailing-byte", Append(baseProof, 0), ProtocolError.TrailingBytes, "one byte after the signature");

        // Key and signature form (sections 3 and 6).
        Add("proof-key-compressed", Mutate(baseProof, 7, 0x02), ProtocolError.InvalidKey, "compressed point prefix 0x02");
        Add("proof-key-off-curve", Flip(baseProof, 7 + 64), ProtocolError.InvalidKey, "last bit of y changed");
        Add("proof-signature-high-s", Substitute(baseProof, signature + 32, ReferenceP256.ToBytes32(ReferenceP256.N - s)), ProtocolError.InvalidSignature, "s replaced by n - s: valid ECDSA, not canonical");
        Add("proof-signature-r-zero", Substitute(baseProof, signature, new byte[32]), ProtocolError.InvalidSignature, "r = 0");

        // Every signed field is signed.
        Add("proof-signature-bit-flipped", Flip(baseProof, baseProof.Length - 1), ProtocolError.SignatureMismatch, "last signature bit changed");
        Add("proof-deployment-changed", Mutate(baseProof, 73, (byte)'q'), ProtocolError.SignatureMismatch, "qlates.example.com: a valid name, not the one signed");
        Add("proof-deployment-shortened", Renamed(Deployment[1..]), ProtocolError.SignatureMismatch, "lates.example.com with the same signature: the name's length is signed too");
        Add("proof-challenge-bit-flipped", Flip(baseProof, challenge), ProtocolError.SignatureMismatch, "one challenge bit changed");
        Add("proof-subject-digest-bit-flipped", Flip(baseProof, digest), ProtocolError.SignatureMismatch, "one digest bit changed");
        Add("proof-key-substituted-other-persona", Substitute(baseProof, 7, b.PublicKey.ToArray()), ProtocolError.SignatureMismatch, "persona B's key under persona A's signature");

        // Signing contexts (section 5.1): a signature from the document context never verifies as a proof.
        var snapshot = document("profile-snapshot");
        Add("proof-with-the-documents-signature", Substitute(baseProof, signature, snapshot.AsSpan(snapshot.Length - 64).ToArray()), ProtocolError.SignatureMismatch, "the signature of the document the proof binds, which is persona A's but in the document context");
        var crossContext = a.Sign(SigningInput.Create(DocumentType.ProfileSnapshot, a.PublicKey, baseProof.AsSpan(4, signature - 4)));
        Add("proof-signed-in-the-document-context", Substitute(baseProof, signature, crossContext.Bytes.ToArray()), ProtocolError.SignatureMismatch, "persona A's valid signature over the proof's fields as a document payload, under the document tag", deterministic: false);

        // Valid proofs that do not authorize the submission they come with (section 14.4).
        Add("proof-for-another-deployment", baseProof, ProtocolError.ProofMismatch, "a valid proof for plates.example.com checked by staging.example.com", deployment: "staging.example.com");
        Add("proof-for-another-document", baseProof, ProtocolError.ProofMismatch, "a valid proof of profile-snapshot submitted with profile-snapshot-minimal", documentName: "profile-snapshot-minimal");
        var bInput = SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, b.PublicKey, DeploymentName.Parse(Deployment), RequestChallenge.FromBytes(challengeBytes), subject);
        var bProof = RequestProofCodec.Assemble(RequestProofKind.DocumentSubmission, b.PublicKey, DeploymentName.Parse(Deployment), RequestChallenge.FromBytes(challengeBytes), subject, b.Sign(bInput));
        Add("proof-by-another-persona", bProof, ProtocolError.ProofMismatch, "persona B's valid proof over persona A's document: the proof and the document are signed by different keys", deterministic: false);

        // A valid proof over a document that fails its own verification: only step 4 of section
        // 14.4 refuses it, so an implementation that trusted a matching digest would accept it.
        var broken = rejectedDocument("signature-bit-flipped");
        var brokenDigest = SHA256.HashData(broken);
        var brokenInput = SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, a.PublicKey, DeploymentName.Parse(Deployment), RequestChallenge.FromBytes(challengeBytes), brokenDigest);
        var brokenProof = RequestProofCodec.Assemble(RequestProofKind.DocumentSubmission, a.PublicKey, DeploymentName.Parse(Deployment), RequestChallenge.FromBytes(challengeBytes), brokenDigest, a.Sign(brokenInput));
        Add("proof-of-a-document-that-does-not-verify", brokenProof, ProtocolError.SignatureMismatch, "persona A's valid proof over the rejected document signature-bit-flipped: the digest matches, and the document's own signature fails (section 14.4, step 4)", documentName: "signature-bit-flipped", documentSet: "rejected", deterministic: false);

        // An action's valid proof is never a submission (section 14.4, step 1). The lookup is signed
        // over the document's own bytes, so its deployment, key and digest all match the submission:
        // only the kind refuses it.
        var lookup = RequestProofCodec.SignAction(RequestProofKind.Lookup, snapshot, DeploymentName.Parse(Deployment), RequestChallenge.FromBytes(challengeBytes), a);
        Add("proof-of-an-action-as-a-submission", lookup, ProtocolError.ProofMismatch, "persona A's valid lookup proof whose body is profile-snapshot's own bytes, submitted with that document: everything matches but the kind, and an action never authorizes a submission", deterministic: false);
        return list;
    }

    public sealed record ValidAction(string Name, string Persona, RequestProofKind Kind, string Body, string Challenge);

    /// <summary>
    /// One valid request per action (section 14.5), each with an example body. The protocol binds a
    /// body's bytes, whatever they are; the bodies here only show the kind of thing each action carries.
    /// </summary>
    public static IReadOnlyList<ValidAction> ValidActions() =>
    [
        new("action-lodestone-code", "A", RequestProofKind.LodestoneCode, "{}", "chl_" + string.Concat(Enumerable.Repeat("12", 32))),
        new("action-lodestone-check", "A", RequestProofKind.LodestoneCheck, "{\"code\":\"AF-0123456789\",\"lodestoneId\":\"12345678\"}", "chl_" + string.Concat(Enumerable.Repeat("23", 32))),
        new("action-lodestone-reread", "A", RequestProofKind.LodestoneReread, "{}", "chl_" + string.Concat(Enumerable.Repeat("34", 32))),
        new("action-opt-out", "A", RequestProofKind.OptOut, "{}", "chl_" + string.Concat(Enumerable.Repeat("45", 32))),
        new("action-lookup", "B", RequestProofKind.Lookup, "{\"name\":\"Jane Doe\",\"world\":\"Gilgamesh\"}", "chl_" + string.Concat(Enumerable.Repeat("56", 32))),
        new("action-image", "B", RequestProofKind.Image, "{\"name\":\"Jane Doe\",\"world\":\"Gilgamesh\",\"marker\":\"00112233445566778899aabbccddeeff\",\"index\":0}", "chl_" + string.Concat(Enumerable.Repeat("67", 32))),
        new("action-report", "B", RequestProofKind.Report, "{\"name\":\"Jane Doe\",\"world\":\"Gilgamesh\",\"reason\":\"offensive\"}", "chl_" + string.Concat(Enumerable.Repeat("78", 32))),
    ];

    public static List<ActionProofVector> BuildActions(IReadOnlyDictionary<string, EcdsaPersonaSigner> signers)
    {
        var list = new List<ActionProofVector>();
        var deployment = DeploymentName.Parse(Deployment);
        foreach (var valid in ValidActions())
        {
            var signer = signers[valid.Persona];
            var body = System.Text.Encoding.UTF8.GetBytes(valid.Body);
            var challenge = RequestChallenge.Parse(valid.Challenge);
            var proof = RequestProofCodec.SignAction(valid.Kind, body, deployment, challenge, signer);
            var digest = SHA256.HashData(body);
            var input = SigningInput.CreateRequestProof(valid.Kind, signer.PublicKey, deployment, challenge, digest);
            list.Add(new ActionProofVector
            {
                Name = valid.Name,
                Persona = valid.Persona,
                Kind = valid.Kind.ToString(),
                Body = Hex.Of(body),
                Deployment = Deployment,
                Challenge = valid.Challenge,
                SubjectDigest = Hex.Of(digest),
                SigningInput = Hex.Of(input.Bytes),
                Digest = Hex.Of(input.ComputeDigest()),
                Signature = Hex.Of(proof.AsSpan(proof.Length - 64)),
                Proof = Hex.Of(proof),
            });
        }

        return list;
    }

    /// <summary>
    /// Action requests a server refuses (section 14.5), derived from the lookup vector and the first
    /// submission vector (<paramref name="submissionProof"/>, persona A's proof of
    /// <paramref name="submittedDocument"/>): each is checked as the action named, with the body and
    /// deployment named.
    /// </summary>
    public static List<RejectedActionVector> BuildRejectedActions(ActionProofVector lookup, byte[] submissionProof, byte[] submittedDocument)
    {
        var list = new List<RejectedActionVector>();
        void Add(string name, byte[] proof, RequestProofKind checkedAs, byte[] body, ProtocolError error, string reason, string deployment = Deployment) =>
            list.Add(new RejectedActionVector { Name = name, Proof = Hex.Of(proof), CheckedAs = checkedAs.ToString(), Body = Hex.Of(body), Deployment = deployment, Error = error.ToString(), Reason = reason });

        var proof = Hex.Parse(lookup.Proof);
        var body = Hex.Parse(lookup.Body);
        Add("action-checked-as-another-action", proof, RequestProofKind.Report, body, ProtocolError.ProofMismatch, "a valid lookup proof checked as a report");
        Add("action-kind-byte-rewritten", Mutate(proof, 6, (byte)RequestProofKind.Report), RequestProofKind.Report, body, ProtocolError.SignatureMismatch, "the lookup proof's kind byte rewritten to a report: the kind is signed");
        Add("action-for-another-deployment", proof, RequestProofKind.Lookup, body, ProtocolError.ProofMismatch, "a valid lookup proof checked by staging.example.com", deployment: "staging.example.com");
        Add("action-with-another-body", proof, RequestProofKind.Lookup, System.Text.Encoding.UTF8.GetBytes("{\"name\":\"John Doe\",\"world\":\"Gilgamesh\"}"), ProtocolError.ProofMismatch, "a valid lookup proof sent with another character's name");
        Add("action-body-over-the-limit", proof, RequestProofKind.Lookup, new byte[ProtocolLimits.MaxActionBodyBytes + 1], ProtocolError.LimitExceeded, "a body of 4,097 bytes, one over the limit");
        Add("submission-checked-as-an-action", submissionProof, RequestProofKind.Lookup, submittedDocument, ProtocolError.ProofMismatch, "a valid document submission proof checked as a lookup whose body is the document it proves: everything matches but the kind, and a submission never authorizes an action");
        return list;
    }
}
