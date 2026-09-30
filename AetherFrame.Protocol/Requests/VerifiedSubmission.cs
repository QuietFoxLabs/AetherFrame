using System;
using AetherFrame.Protocol.Documents;

namespace AetherFrame.Protocol.Requests;

/// <summary>
/// A document submission whose proof and document have both verified and match each other and the
/// deployment (docs/networking/ProtocolSpecification-v1.md, section 14.4). The one thing left to
/// the server is the challenge: it acts on the document only after it has consumed
/// <see cref="Challenge"/> as one it issued, that has not expired and was never used (section 13,
/// rule 10). Only this assembly creates one. Immutable.
/// </summary>
public sealed class VerifiedSubmission
{
    private readonly byte[] documentBytes;

    internal VerifiedSubmission(VerifiedDocument document, VerifiedRequestProof proof, byte[] documentBytes)
    {
        Document = document;
        Proof = proof;
        this.documentBytes = documentBytes;
    }

    /// <summary>The verified document, signed by the same key as the proof.</summary>
    public VerifiedDocument Document { get; }

    /// <summary>The verified proof.</summary>
    public VerifiedRequestProof Proof { get; }

    /// <summary>The challenge the server must consume before it acts on <see cref="Document"/>.</summary>
    public RequestChallenge Challenge => Proof.Challenge;

    /// <summary>
    /// The exact document bytes that were hashed and verified: the library's private copy, taken
    /// once. A server stores these (section 13, rule 3), never the request buffer again, which may
    /// have changed since.
    /// </summary>
    public ReadOnlySpan<byte> DocumentBytes => documentBytes;
}
