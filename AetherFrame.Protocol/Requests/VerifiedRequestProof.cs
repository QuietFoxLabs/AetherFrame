using System;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Requests;

/// <summary>
/// A request proof whose framing, key and signature have been checked
/// (docs/networking/ProtocolSpecification-v1.md, section 14.3). On its own it authorizes nothing:
/// a server matches it with the request through <see cref="RequestProofCodec.VerifySubmission"/> or
/// <see cref="RequestProofCodec.VerifyAction"/>, which also check its kind.
/// Only this assembly creates one. Immutable.
/// </summary>
public sealed class VerifiedRequestProof
{
    private readonly byte[] subjectDigest;

    internal VerifiedRequestProof(RequestProofKind kind, PersonaPublicKey publicKey, DeploymentName deployment, RequestChallenge challenge, byte[] subjectDigest)
    {
        Kind = kind;
        PublicKey = publicKey;
        Deployment = deployment;
        Challenge = challenge;
        this.subjectDigest = subjectDigest;
    }

    /// <summary>What the proof authorizes.</summary>
    public RequestProofKind Kind { get; }

    /// <summary>The key that signed the proof.</summary>
    public PersonaPublicKey PublicKey { get; }

    /// <summary>The persona that signed the proof: the identity of <see cref="PublicKey"/>.</summary>
    public PersonaId Persona => PublicKey.Id;

    /// <summary>The deployment the proof was made for.</summary>
    public DeploymentName Deployment { get; }

    /// <summary>The challenge the proof carries, which the server must consume before it acts.</summary>
    public RequestChallenge Challenge { get; }

    /// <summary>
    /// The digest of what the proof authorizes, as its <see cref="Kind"/> defines it: for a
    /// document submission, SHA-256 of the complete signed document; for an action, SHA-256 of the
    /// request's body (section 14.5).
    /// </summary>
    public ReadOnlySpan<byte> SubjectDigest => subjectDigest;
}
