using System;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Protocol.Requests;

/// <summary>
/// The request proof (docs/networking/ProtocolSpecification-v1.md, section 14; decisions S1, D7
/// and C9): a persona's signature, in its own signing context, authorizing one request to one
/// deployment under a challenge the server issued: either one submission of one exact document, or
/// one action (section 14.5) with one exact body. <see cref="Sign"/> and <see cref="SignAction"/> are
/// how a client makes one, and <see cref="VerifySubmission"/> and <see cref="VerifyAction"/> the only
/// ways a server checks one: together with the document or body it came with, and the kind the
/// server expects, since a proof read on its own authorizes nothing.
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
    /// verifies here authorizes nothing yet, and a server that stopped here would skip the kind, the
    /// deployment, and the document or body (sections 14.4 and 14.5), so only
    /// <see cref="VerifySubmission"/> and <see cref="VerifyAction"/> offer it.
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

        // The kind decides the layout after it; every kind version 1 knows shares the one below.
        var kind = (RequestProofKind)reader.ReadU8("proofKind");
        if (!IsKnown(kind))
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
        var verifiedProof = CheckSubmissionProof(proof, deployment);

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

    /// <summary>
    /// Section 14.4's first two steps alone, for a server that checks a submission's proof as soon as
    /// it arrives, before it reads the document that follows: the proof as section 14.3 reads it,
    /// its kind (a document submission), and its deployment. It authorizes nothing: it names the
    /// signer and the challenge, so a server can refuse early, but only <see cref="VerifySubmission"/>,
    /// with the document, lets it act (section 14.4).
    /// </summary>
    /// <exception cref="ProtocolException">The first rule the proof breaks, in the order of section 14.4, steps 1 and 2.</exception>
    public static VerifiedRequestProof CheckSubmissionProof(ReadOnlySpan<byte> proof, DeploymentName deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        var verifiedProof = Verify(proof);
        if (verifiedProof.Kind != RequestProofKind.DocumentSubmission)
        {
            throw new ProtocolException(ProtocolError.ProofMismatch, "The request proof authorizes an action, not a document submission.");
        }

        if (!verifiedProof.Deployment.Equals(deployment))
        {
            throw new ProtocolException(ProtocolError.ProofMismatch, "The request proof was made for another deployment.");
        }

        return verifiedProof;
    }

    /// <summary>Whether <paramref name="kind"/> is an action (section 14.5): every known kind but a document submission.</summary>
    public static bool IsAction(RequestProofKind kind) => IsKnown(kind) && kind != RequestProofKind.DocumentSubmission;

    /// <summary>
    /// Makes the proof for one action request (section 14.5): <paramref name="kind"/> with exactly
    /// <paramref name="body"/>, to <paramref name="deployment"/>, under <paramref name="challenge"/>.
    /// The finished proof is checked with the body before it is returned, so a signer that
    /// misreports its key or signs other bytes produces no proof.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not an action.</exception>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.LimitExceeded"/> for a body over <see cref="ProtocolLimits.MaxActionBodyBytes"/>;
    /// <see cref="ProtocolError.SignatureMismatch"/>, <see cref="ProtocolError.InvalidKey"/> or
    /// <see cref="ProtocolError.InvalidSignature"/> when the signer's output is not a valid proof. The
    /// signer's own exceptions pass through unchanged.
    /// </exception>
    public static byte[] SignAction(RequestProofKind kind, ReadOnlySpan<byte> body, DeploymentName deployment, RequestChallenge challenge, IPersonaSigner signer)
    {
        if (!IsAction(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an action; a document submission is signed with Sign.");
        }

        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(signer);

        var publicKey = signer.PublicKey;
        if (publicKey is null)
        {
            throw new ProtocolException(ProtocolError.InvalidKey, "The signer reports no public key.");
        }

        var bodyBytes = CopyBody(body);
        var digest = SHA256.HashData(bodyBytes);
        var input = SigningInput.CreateRequestProof(kind, publicKey, deployment, challenge, digest);
        var signature = signer.Sign(input);
        if (signature is null)
        {
            throw new ProtocolException(ProtocolError.InvalidSignature, "The signer returned no signature.");
        }

        var bytes = Assemble(kind, publicKey, deployment, challenge, digest, signature);
        try
        {
            VerifyAction(bytes, bodyBytes, deployment, kind);
        }
        catch (ProtocolException e)
        {
            throw new ProtocolException(e.Error, $"The signer's output is not a valid request proof, so none was produced: {e.Message}");
        }

        return bytes;
    }

    /// <summary>
    /// Checks an action request as a server receives it (section 14.5): the proof on its own, then
    /// that it authorizes the action the server expects at this endpoint, that it was made for
    /// <paramref name="deployment"/>, and that it binds exactly <paramref name="body"/>. What this
    /// cannot check is the challenge: the server must consume <see cref="VerifiedAction.Challenge"/>
    /// before it acts (section 13, rule 10).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expected"/> is not an action.</exception>
    /// <exception cref="ProtocolException">The first rule the request breaks, in the order of section 14.5.</exception>
    public static VerifiedAction VerifyAction(ReadOnlySpan<byte> proof, ReadOnlySpan<byte> body, DeploymentName deployment, RequestProofKind expected)
    {
        if (!IsAction(expected))
        {
            throw new ArgumentOutOfRangeException(nameof(expected), expected, "Not an action; a document submission is checked with VerifySubmission.");
        }

        ArgumentNullException.ThrowIfNull(deployment);

        var verifiedProof = Verify(proof);
        if (verifiedProof.Kind != expected)
        {
            throw new ProtocolException(ProtocolError.ProofMismatch, "The request proof authorizes another kind of request.");
        }

        if (!verifiedProof.Deployment.Equals(deployment))
        {
            throw new ProtocolException(ProtocolError.ProofMismatch, "The request proof was made for another deployment.");
        }

        var bodyBytes = CopyBody(body);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bodyBytes), verifiedProof.SubjectDigest))
        {
            throw new ProtocolException(ProtocolError.ProofMismatch, "The request proof binds another body.");
        }

        return new VerifiedAction(verifiedProof, bodyBytes);
    }

    /// <summary>
    /// Whether version 1 defines <paramref name="kind"/> (section 14.3, step 4). The one statement of
    /// the range, shared with <see cref="SigningInput.CreateRequestProof"/>, so a new kind is added
    /// in one place.
    /// </summary>
    internal static bool IsKnown(RequestProofKind kind) => kind is >= RequestProofKind.DocumentSubmission and <= RequestProofKind.Report;

    /// <summary>A private copy of an action's body, taken only once its size is within the limit.</summary>
    private static byte[] CopyBody(ReadOnlySpan<byte> body)
    {
        if (body.Length > ProtocolLimits.MaxActionBodyBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The action's body is {ProtocolText.Number(body.Length)} bytes; the limit is {ProtocolText.Number(ProtocolLimits.MaxActionBodyBytes)}.");
        }

        return body.ToArray();
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
