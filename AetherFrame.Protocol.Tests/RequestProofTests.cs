using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Requests;
using AetherFrame.Protocol.Signing;
using Xunit;
using static AetherFrame.Protocol.Tests.DocumentMutations;

namespace AetherFrame.Protocol.Tests;

/// <summary>The request proof of section 14: its layout, its reading order, and how a server matches it with a submission.</summary>
public class RequestProofTests
{
    private static readonly DeploymentName Deployment = DeploymentName.Parse(ProofSamples.DeploymentText);
    private static readonly RequestChallenge Challenge = RequestChallenge.Parse(ProofSamples.ChallengeText);

    [Fact]
    public void AProof_AuthorizesExactlyItsDocumentAtItsDeployment()
    {
        using var a = TestPersonas.CreateA();
        foreach (var document in new[] { Samples.SignedSnapshot(a), Samples.SignedRetraction(a) })
        {
            var proof = RequestProofCodec.Sign(document, Deployment, Challenge, a);
            var submission = RequestProofCodec.VerifySubmission(proof, document, Deployment);
            Assert.Equal(a.PublicKey, submission.Document.PublicKey);
            Assert.Equal(a.PublicKey, submission.Proof.PublicKey);
            Assert.Equal(a.PublicKey.Id, submission.Proof.Persona);
            Assert.Equal(Challenge, submission.Challenge);
            Assert.Equal(Deployment, submission.Proof.Deployment);
            Assert.Equal(RequestProofKind.DocumentSubmission, submission.Proof.Kind);
            Assert.Equal(SHA256.HashData(document), submission.Proof.SubjectDigest.ToArray());
            Assert.Equal(document, submission.DocumentBytes.ToArray());
            Assert.Equal(SignedDocumentCodec.Verify(document).DocumentType, submission.Document.DocumentType);
        }
    }

    [Fact]
    public void TheProof_IsLaidOutAsTheSpecificationSays()
    {
        using var a = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(a);
        var proof = RequestProofCodec.Sign(document, Deployment, Challenge, a);
        var n = Deployment.Bytes.Length;

        Assert.Equal(ProtocolLimits.RequestProofOverheadBytes + n, proof.Length);
        Assert.Equal(201 + n, proof.Length);
        Assert.Equal("AFRQ"u8.ToArray(), proof[..4]);
        Assert.Equal(new byte[] { 0x80, 0x01 }, proof[4..6]);
        Assert.Equal(1, proof[6]);
        Assert.Equal(a.PublicKey.ToArray(), proof[7..72]);
        Assert.Equal(n, proof[72]);
        Assert.Equal(Deployment.Bytes.ToArray(), proof[73..(73 + n)]);
        Assert.Equal(Challenge.ToArray(), proof[(73 + n)..(105 + n)]);
        Assert.Equal(SHA256.HashData(document), proof[(105 + n)..(137 + n)]);

        // The signing input is the proof's tag, then everything between the magic and the signature.
        var input = SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, a.PublicKey, Deployment, Challenge, SHA256.HashData(document));
        Assert.Equal(SigningContext.RequestProof, input.Context);
        Assert.Null(input.DocumentType);
        Assert.Equal([0x2a, .. "AetherFrame.Protocol.RequestProof.v1-draft"u8.ToArray(), .. proof[4..^64]], input.Bytes.ToArray());
        Assert.Equal(ReferenceProtocol.ProofSigningInput(proof[6], a.PublicKey.ToArray(), Deployment.Bytes.ToArray(), Challenge.ToArray(), SHA256.HashData(document)), input.Bytes.ToArray());
        Assert.True(SignatureVerifier.Verify(input, ProtocolSignature.FromBytes(proof.AsSpan(proof.Length - 64))));
    }

    [Fact]
    public void TheLongestAndShortestNames_MakeTheLargestAndSmallestProofs()
    {
        using var a = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(a);

        var longest = DeploymentName.Parse(ProofSamples.LongestDeploymentText);
        Assert.Equal(ProtocolLimits.MaxDeploymentNameBytes, longest.Bytes.Length);
        var largest = RequestProofCodec.Sign(document, longest, Challenge, a);
        Assert.Equal(ProtocolLimits.MaxRequestProofBytes, largest.Length);
        Assert.Equal(454, largest.Length);
        Assert.Equal(longest, RequestProofCodec.VerifySubmission(largest, document, longest).Proof.Deployment);
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => RequestProofCodec.Verify(Append(largest, 0)));

        var shortest = DeploymentName.Parse("a");
        var smallest = RequestProofCodec.Sign(document, shortest, Challenge, a);
        Assert.Equal(202, smallest.Length);
        Assert.Equal(shortest, RequestProofCodec.VerifySubmission(smallest, document, shortest).Proof.Deployment);
    }

    [Fact]
    public void Verify_ChecksEachRuleAtItsPlaceInReadingOrder()
    {
        // Inputs with two faults are refused for the first in the order of section 14.3.
        using var a = TestPersonas.CreateA();
        var proof = ProofSamples.Proof(a);
        var n = Deployment.Bytes.Length;

        // The kind, the name and the challenge as soon as each is read, before a later truncation.
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => RequestProofCodec.Verify(Truncate(Mutate(proof, 6, 9), 20)));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => RequestProofCodec.Verify(Truncate(Mutate(proof, 73, (byte)'P'), 73 + n)));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => RequestProofCodec.Verify(Truncate(Substitute(proof, 73 + n, new byte[32]), 105 + n)));

        // Trailing bytes before the key, the key before the signature's form, the form before the verification.
        ProtocolAssert.Throws(ProtocolError.TrailingBytes, () => RequestProofCodec.Verify(Append(Mutate(proof, 7, 0x02), 0)));
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => RequestProofCodec.Verify(ProofSamples.WithHighS(Mutate(proof, 7, 0x02))));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => RequestProofCodec.Verify(ProofSamples.WithHighS(Flip(proof, 105 + n))));
    }

    [Fact]
    public void Verify_RefusesEachFramingFaultWithItsOwnError()
    {
        using var a = TestPersonas.CreateA();
        var proof = ProofSamples.Proof(a);
        var n = Deployment.Bytes.Length;

        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => RequestProofCodec.Verify(new byte[ProtocolLimits.MaxRequestProofBytes + 1]));
        ProtocolAssert.Throws(ProtocolError.Truncated, () => RequestProofCodec.Verify([]));
        ProtocolAssert.Throws(ProtocolError.Truncated, () => RequestProofCodec.Verify("AFR"u8));
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => RequestProofCodec.Verify("AFRX"u8));
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => RequestProofCodec.Verify(Substitute(proof, 0, "AFPD"u8.ToArray())));
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => RequestProofCodec.Verify(Samples.SignedRetraction(a)));

        ProtocolAssert.Throws(ProtocolError.UnsupportedVersion, () => RequestProofCodec.Verify(Substitute(proof, 4, [0x00, 0x01])));
        ProtocolAssert.Throws(ProtocolError.UnsupportedVersion, () => RequestProofCodec.Verify(Substitute(proof, 4, [0x80, 0x02])));
        ProtocolAssert.Throws(ProtocolError.UnsupportedVersion, () => RequestProofCodec.Verify(Substitute(proof, 4, [0x00, 0x00])));

        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => RequestProofCodec.Verify(Mutate(proof, 6, 0)));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => RequestProofCodec.Verify(Mutate(proof, 6, 9)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Verify(Mutate(proof, 6, 2)));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => RequestProofCodec.Verify(Mutate(proof, 6, 0xff)));

        ProtocolAssert.Throws(ProtocolError.InvalidLength, () => RequestProofCodec.Verify(Mutate(proof, 72, 0)));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => RequestProofCodec.Verify(Mutate(proof, 72, 254)));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => RequestProofCodec.Verify(Mutate(proof, 72, 0xff)));
        ProtocolAssert.Throws(ProtocolError.Truncated, () => RequestProofCodec.Verify(Mutate(proof, 72, 253)));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => RequestProofCodec.Verify(Mutate(proof, 73, (byte)'P')));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => RequestProofCodec.Verify(Mutate(proof, 73 + n - 1, (byte)'.')));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => RequestProofCodec.Verify(Substitute(proof, 73 + n, new byte[32])));

        ProtocolAssert.Throws(ProtocolError.TrailingBytes, () => RequestProofCodec.Verify(Append(proof, 0)));
        for (var length = 0; length < proof.Length; length++)
        {
            ProtocolAssert.Throws(ProtocolError.Truncated, () => RequestProofCodec.Verify(Truncate(proof, length)));
        }
    }

    [Fact]
    public void Verify_RefusesTamperedKeysSignaturesAndSignedFields()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        var proof = ProofSamples.Proof(a);
        var n = Deployment.Bytes.Length;
        var signature = proof.Length - 64;

        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => RequestProofCodec.Verify(Mutate(proof, 7, 0x02)));
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => RequestProofCodec.Verify(Flip(proof, 7 + 40)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Verify(Substitute(proof, 7, b.PublicKey.ToArray())));

        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => RequestProofCodec.Verify(Substitute(proof, signature, new byte[32])));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => RequestProofCodec.Verify(ProofSamples.WithHighS(proof)));

        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Verify(Mutate(proof, 73, (byte)'q')));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Verify(Flip(proof, 73 + n)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Verify(Flip(proof, 105 + n)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Verify(Flip(proof, 137 + n - 1)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Verify(Flip(proof, proof.Length - 1)));

        // A shorter deployment name with the same signature: the length is signed too.
        var shorter = DeploymentName.Parse(ProofSamples.DeploymentText[1..]);
        var reframed = RequestProofCodec.Assemble(RequestProofKind.DocumentSubmission, a.PublicKey, shorter, Challenge, proof.AsSpan(105 + n, 32), ProtocolSignature.FromBytes(proof.AsSpan(signature)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Verify(reframed));
    }

    [Fact]
    public void VerifySubmission_RefusesAProofForAnotherRequest()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        var document = Samples.SignedSnapshot(a);
        var proof = RequestProofCodec.Sign(document, Deployment, Challenge, a);

        // Another deployment.
        var staging = DeploymentName.Parse("staging.example.com");
        var e = ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifySubmission(proof, document, staging));
        Assert.Contains("deployment", e.Message, StringComparison.Ordinal);

        // Another document of the same persona: a second signing of the same snapshot differs in its signature.
        var again = Samples.SignedSnapshot(a);
        e = ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifySubmission(proof, again, Deployment));
        Assert.Contains("another document", e.Message, StringComparison.Ordinal);
        ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifySubmission(proof, Samples.SignedRetraction(a), Deployment));

        // B's valid proof over A's document: the proof verifies on its own, and the keys differ.
        var digest = SHA256.HashData(document);
        var bProof = RequestProofCodec.Assemble(RequestProofKind.DocumentSubmission, b.PublicKey, Deployment, Challenge, digest, b.Sign(SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, b.PublicKey, Deployment, Challenge, digest)));
        Assert.Equal(b.PublicKey, RequestProofCodec.Verify(bProof).PublicKey);
        e = ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifySubmission(bProof, document, Deployment));
        Assert.Contains("different keys", e.Message, StringComparison.Ordinal);

        // A's proof over bytes that are no document: the document's own error.
        var garbage = new byte[] { 1, 2, 3, 4 };
        var garbageDigest = SHA256.HashData(garbage);
        var garbageProof = RequestProofCodec.Assemble(RequestProofKind.DocumentSubmission, a.PublicKey, Deployment, Challenge, garbageDigest, a.Sign(SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, a.PublicKey, Deployment, Challenge, garbageDigest)));
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => RequestProofCodec.VerifySubmission(garbageProof, garbage, Deployment));

        // A document over the size limit is refused before it is hashed.
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => RequestProofCodec.VerifySubmission(proof, new byte[ProtocolLimits.MaxDocumentBytes + 1], Deployment));
    }

    [Fact]
    public void VerifySubmission_ChecksInTheSpecifiedOrder()
    {
        using var a = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(a);
        var proof = RequestProofCodec.Sign(document, Deployment, Challenge, a);
        var staging = DeploymentName.Parse("staging.example.com");

        // The proof on its own first, whatever else is wrong.
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.VerifySubmission(Flip(proof, proof.Length - 1), [1, 2, 3], staging));

        // Then the deployment, before the document is looked at.
        var e = ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifySubmission(proof, new byte[ProtocolLimits.MaxDocumentBytes + 1], staging));
        Assert.Contains("deployment", e.Message, StringComparison.Ordinal);

        // Then the document's size, then its digest, before its own verification.
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => RequestProofCodec.VerifySubmission(proof, new byte[ProtocolLimits.MaxDocumentBytes + 1], Deployment));
        e = ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifySubmission(proof, Flip(document, 0), Deployment));
        Assert.Contains("another document", e.Message, StringComparison.Ordinal);

        // Then the document's own verification, before the keys are compared: persona B's valid
        // proof over a copy of A's document whose signature is broken fails as the document, not
        // as a key mismatch, so no key is ever compared on bytes that did not verify.
        using var b = TestPersonas.CreateB();
        var broken = Flip(document, document.Length - 1);
        var brokenDigest = SHA256.HashData(broken);
        var bProof = RequestProofCodec.Assemble(RequestProofKind.DocumentSubmission, b.PublicKey, Deployment, Challenge, brokenDigest, b.Sign(SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, b.PublicKey, Deployment, Challenge, brokenDigest)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.VerifySubmission(bProof, broken, Deployment));
    }

    [Fact]
    public void Sign_ProvesOnlyTheSignersOwnValidDocuments()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        var document = Samples.SignedSnapshot(a);

        var e = ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.Sign(document, Deployment, Challenge, b));
        Assert.Contains("another persona", e.Message, StringComparison.Ordinal);
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Sign(Flip(document, document.Length - 1), Deployment, Challenge, a));
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => RequestProofCodec.Sign([1, 2, 3, 4], Deployment, Challenge, a));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => RequestProofCodec.Sign(new byte[ProtocolLimits.MaxDocumentBytes + 1], Deployment, Challenge, a));
        Assert.Throws<ArgumentNullException>(() => RequestProofCodec.Sign(document, null!, Challenge, a));
        Assert.Throws<ArgumentNullException>(() => RequestProofCodec.Sign(document, Deployment, null!, a));
        Assert.Throws<ArgumentNullException>(() => RequestProofCodec.Sign(document, Deployment, Challenge, null!));
        Assert.Throws<ArgumentNullException>(() => RequestProofCodec.VerifySubmission([], document, null!));
    }

    [Fact]
    public void Sign_ProducesNothingFromAFaultySigner()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        var document = Samples.SignedSnapshot(a);

        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => RequestProofCodec.Sign(document, Deployment, Challenge, new FaultySigner(() => null!, _ => null!)));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => RequestProofCodec.Sign(document, Deployment, Challenge, new FaultySigner(() => a.PublicKey, _ => null!)));

        // Reports A's key but signs with B's: the proof does not verify, so none is produced.
        var e = ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Sign(document, Deployment, Challenge, new FaultySigner(() => a.PublicKey, input => b.Sign(SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, b.PublicKey, Deployment, Challenge, SHA256.HashData(document))))));
        Assert.Contains("none was produced", e.Message, StringComparison.Ordinal);

        // Signs a document input instead of the proof's.
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Sign(document, Deployment, Challenge, new FaultySigner(() => a.PublicKey, _ => a.Sign(SigningInput.Create(DocumentType.ProfileSnapshot, a.PublicKey, [1])))));
    }

    [Fact]
    public void SignaturesNeverCrossBetweenContexts()
    {
        using var a = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(a);
        var proof = RequestProofCodec.Sign(document, Deployment, Challenge, a);
        var proofSignature = ProtocolSignature.FromBytes(proof.AsSpan(proof.Length - 64));
        var proofInput = SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, a.PublicKey, Deployment, Challenge, SHA256.HashData(document));

        // The two tags differ, and each input starts with its own tag's length, so no input of one context is an input of the other.
        Assert.NotEqual(ProtocolConstants.SignatureDomainTag.Length, ProtocolConstants.RequestProofDomainTag.Length);
        var documentInput = SigningInput.Create(DocumentType.ProfileSnapshot, a.PublicKey, proofInput.Bytes.ToArray());
        Assert.NotEqual(documentInput.Bytes[0], proofInput.Bytes[0]);

        // A proof's signature over its fields does not verify as a document over the same bytes, and the reverse.
        Assert.False(SignatureVerifier.Verify(SigningInput.Create(DocumentType.ProfileSnapshot, a.PublicKey, proof.AsSpan(6, proof.Length - 70)), proofSignature));
        var documentSignature = document.AsSpan(document.Length - 64).ToArray();
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Verify(Substitute(proof, proof.Length - 64, documentSignature)));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(Substitute(document, document.Length - 64, proofSignature.Bytes.ToArray())));
    }

    [Fact]
    public void SigningInputs_NameTheirContext()
    {
        using var a = TestPersonas.CreateA();
        var input = SigningInput.Create(DocumentType.ProfileRetraction, a.PublicKey, [1]);
        Assert.Equal(SigningContext.SignedDocument, input.Context);
        Assert.Equal(DocumentType.ProfileRetraction, input.DocumentType);

        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => SigningInput.CreateRequestProof((RequestProofKind)9, a.PublicKey, Deployment, Challenge, new byte[32]));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => SigningInput.CreateRequestProof((RequestProofKind)0, a.PublicKey, Deployment, Challenge, new byte[32]));
        ProtocolAssert.Throws(ProtocolError.InvalidLength, () => SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, a.PublicKey, Deployment, Challenge, new byte[31]));
    }

    private sealed class FaultySigner(Func<PersonaPublicKey> publicKey, Func<SigningInput, ProtocolSignature> sign) : IPersonaSigner
    {
        public PersonaPublicKey PublicKey => publicKey();

        public ProtocolSignature Sign(SigningInput input) => sign(input);
    }
}

/// <summary>Sample request proofs shared by the proof tests and the vectors.</summary>
internal static class ProofSamples
{
    public const string DeploymentText = "plates.example.com";

    public const string ChallengeText = "chl_d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7";

    /// <summary>The longest deployment name, 253 bytes, under a name reserved for tests: it makes the largest proof, 454 bytes.</summary>
    public static readonly string LongestDeploymentText =
        new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 49) + ".example.com";

    public static byte[] Proof(IPersonaSigner signer) =>
        RequestProofCodec.Sign(Samples.SignedSnapshot(signer), DeploymentName.Parse(DeploymentText), RequestChallenge.Parse(ChallengeText), signer);

    /// <summary>The same proof with s replaced by n - s: the other ECDSA signature over the same input, refused for its form.</summary>
    public static byte[] WithHighS(byte[] proof)
    {
        var s = new BigInteger(proof.AsSpan(proof.Length - 32), isUnsigned: true, isBigEndian: true);
        return Substitute(proof, proof.Length - 32, ReferenceP256.ToBytes32(ReferenceP256.N - s));
    }
}
