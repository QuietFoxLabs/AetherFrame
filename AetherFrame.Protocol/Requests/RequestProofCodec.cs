using System;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Protocol.Requests;

/// <summary>
/// The request proof (docs/networking/ProtocolSpecification-v1.md, section 14; decisions S1 and
/// D7): a persona's signature, in its own signing context, authorizing one submission of one exact
/// document to one deployment, under a challenge the server issued. <see cref="Sign"/> is how a
/// client makes one, and <see cref="VerifySubmission"/> the only way a server checks one: together
/// with the document it came with, since a proof read on its own authorizes nothing.
/// </summary>
public static class RequestProofCodec
{
    /// <summary>
    /// Makes the proof for submitting <paramref name="document"/> to <paramref name="deployment"/>
    /// under <paramref name="challenge"/>. The document must verify and be the signer's own: a
    /// persona proves only its own documents. The signer's public key is read once, and the finished
    /// proof is checked with the document before it is returned, so a signer that misreports its key
    /// or signs other bytes produces no proof.
    /// </summary>
    /// <exception cref="ProtocolException">
    /// Any error of <see cref="SignedDocumentCodec.Verify"/> for the document;
    /// <see cref="ProtocolError.ProofMismatch"/> when the document is another persona's;
    /// <see cref="ProtocolError.SignatureMismatch"/>, <see cref="ProtocolError.InvalidKey"/> or
    /// <see cref="ProtocolError.InvalidSignature"/> when the signer's output is not a valid proof. The
    /// signer's own exceptions pass through unchanged.
    /// </exception>
    public static byte[] Sign(ReadOnlySpan<byte> document, DeploymentName deployment, RequestChallenge challenge, IPersonaSigner signer)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(signer);

        var publicKey = signer.PublicKey;
        if (publicKey is null)
        {
            throw new ProtocolException(ProtocolError.InvalidKey, "The signer reports no public key.");
        }

        var documentBytes = CopyDocument(document);
        var verifiedDocument = SignedDocumentCodec.Verify(documentBytes);
        if (!verifiedDocument.PublicKey.Equals(publicKey))
        {
            throw new ProtocolException(ProtocolError.ProofMismatch, "The document is signed by another persona; a persona proves only its own documents.");
        }

        var digest = SHA256.HashData(documentBytes);
        var input = SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, publicKey, deployment, challenge, digest);
        var signature = signer.Sign(input);
        if (signature is null)
        {
            throw new ProtocolException(ProtocolError.InvalidSignature, "The signer returned no signature.");
        }

        var bytes = Assemble(RequestProofKind.DocumentSubmission, publicKey, deployment, challenge, digest, signature);

        // The server's check decides what a proof is: bytes it would refuse never leave here as one.
        try
        {
            VerifySubmission(bytes, documentBytes, deployment);
        }
        catch (ProtocolException e)
        {
            throw new ProtocolException(e.Error, $"The signer's output is not a valid request proof, so none was produced: {e.Message}");
        }

        return bytes;
    }

    /// <summary>
    /// Reads a request proof from hostile bytes (section 14.3): framing, version, kind, the
    /// deployment name, the challenge, lengths and trailing bytes are checked, then the key and the
    /// signature. The input is copied once before anything is read from it. Internal: a proof that
    /// verifies here authorizes nothing yet, and a server that stopped here would skip the
    /// deployment, the document and the key (section 14.4), so only <see cref="VerifySubmission"/>
    /// offers it.
    /// </summary>
    /// <exception cref="ProtocolException">The first rule the input breaks, in the order of section 14.3.</exception>
    internal static VerifiedRequestProof Verify(ReadOnlySpan<byte> proof)
    {
        if (proof.Length > ProtocolLimits.MaxRequestProofBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The request proof is {ProtocolText.Number(proof.Length)} bytes; the limit is {ProtocolText.Number(ProtocolLimits.MaxRequestProofBytes)}.");
        }

        var bytes = proof.ToArray();
        var reader = new CanonicalReader(bytes);
        if (!reader.ReadFixed(ProtocolConstants.RequestProofMagic.Length, "magic").SequenceEqual(ProtocolConstants.RequestProofMagic))
        {
            throw new ProtocolException(ProtocolError.InvalidFraming, "The input does not start with the request proof magic.");
        }

        var version = reader.ReadU16("protocolVersion");
        if (version != ProtocolConstants.ProtocolVersion)
        {
            throw new ProtocolException(ProtocolError.UnsupportedVersion, $"Protocol version {SignedDocumentCodec.DescribeVersion(version)} is not supported; this build reads only {SignedDocumentCodec.DescribeVersion(ProtocolConstants.ProtocolVersion)}.");
        }

        // The kind decides the layout after it; version 1 knows one kind, whose layout follows.
        var kind = (RequestProofKind)reader.ReadU8("proofKind");
        if (kind != RequestProofKind.DocumentSubmission)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"Request proof kind {ProtocolText.Number((byte)kind)} is not known.");
        }

        var keyBytes = reader.ReadFixed(ProtocolConstants.PublicKeyLength, "personaPublicKey");
        var deploymentLength = reader.ReadU8("deploymentLength");
        DeploymentName.CheckLength(deploymentLength);
        var deployment = DeploymentName.FromBytes(reader.ReadFixed(deploymentLength, "deployment"));
        var challenge = RequestChallenge.FromBytes(reader.ReadFixed(ProtocolConstants.ChallengeLength, "challenge"));
        var subjectDigest = reader.ReadFixed(ProtocolConstants.DigestLength, "subjectDigest").ToArray();
        var signatureBytes = reader.ReadFixed(ProtocolConstants.SignatureLength, "signature");
        reader.ExpectEnd("The request proof");

        // Structural checks are done; from here each step costs more, and each is fail-closed.
        var key = PersonaPublicKey.FromBytes(keyBytes);
        var signature = ProtocolSignature.FromBytes(signatureBytes);
        var input = SigningInput.CreateRequestProof(kind, key, deployment, challenge, subjectDigest);
        if (!SignatureVerifier.Verify(input, signature))
        {
            throw new ProtocolException(ProtocolError.SignatureMismatch, "The signature does not verify over the request proof with the key it names.");
        }

        return new VerifiedRequestProof(kind, key, deployment, challenge, subjectDigest);
    }

    /// <summary>
    /// Checks a document submission as a server receives it (section 14.4): the proof on its own,
    /// then that it was made for <paramref name="deployment"/>, that it binds exactly
    /// <paramref name="document"/>, that the document verifies, and that both are signed by the same
    /// key. What this cannot check is the challenge: the server must consume
    /// <see cref="VerifiedSubmission.Challenge"/> before it acts (section 13, rule 10).
    /// </summary>
    /// <exception cref="ProtocolException">The first rule the submission breaks, in the order of section 14.4.</exception>
    public static VerifiedSubmission VerifySubmission(ReadOnlySpan<byte> proof, ReadOnlySpan<byte> document, DeploymentName deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        var verifiedProof = Verify(proof);
        if (!verifiedProof.Deployment.Equals(deployment))
        {
            throw new ProtocolException(ProtocolError.ProofMismatch, "The request proof was made for another deployment.");
        }

        // One private copy of the document, hashed and verified alike.
        var documentBytes = CopyDocument(document);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(documentBytes), verifiedProof.SubjectDigest))
        {
            throw new ProtocolException(ProtocolError.ProofMismatch, "The request proof binds another document.");
        }

        var verifiedDocument = SignedDocumentCodec.Verify(documentBytes);
        if (!verifiedDocument.PublicKey.Equals(verifiedProof.PublicKey))
        {
            throw new ProtocolException(ProtocolError.ProofMismatch, "The request proof and the document are signed by different keys.");
        }

        return new VerifiedSubmission(verifiedDocument, verifiedProof, documentBytes);
    }

    /// <summary>Lays out a proof. Internal so tests can build proofs whose parts disagree.</summary>
    internal static byte[] Assemble(RequestProofKind kind, PersonaPublicKey publicKey, DeploymentName deployment, RequestChallenge challenge, ReadOnlySpan<byte> subjectDigest, ProtocolSignature signature)
    {
        var writer = new CanonicalWriter(ProtocolLimits.RequestProofOverheadBytes + deployment.Bytes.Length);
        writer.WriteFixed(ProtocolConstants.RequestProofMagic);
        SigningInput.WriteRequestProofBody(writer, kind, publicKey, deployment, challenge, subjectDigest);
        writer.WriteFixed(signature.Bytes);
        return writer.ToArray();
    }

    /// <summary>A private copy of a document, taken only once its size is within the document limit (section 7.2, step 1).</summary>
    private static byte[] CopyDocument(ReadOnlySpan<byte> document)
    {
        if (document.Length > ProtocolLimits.MaxDocumentBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The document is {ProtocolText.Number(document.Length)} bytes; the limit is {ProtocolText.Number(ProtocolLimits.MaxDocumentBytes)}.");
        }

        return document.ToArray();
    }
}
