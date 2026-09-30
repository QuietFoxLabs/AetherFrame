using System;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Requests;

/// <summary>
/// An action request that passed every check of docs/networking/ProtocolSpecification-v1.md,
/// section 14.5: its proof is valid, made for this deployment and for the action the server
/// expected, and binds exactly this body. What it cannot check is the challenge: the server must
/// consume <see cref="Challenge"/> before it acts (section 13, rule 10). Only this assembly creates
/// one. Immutable.
/// </summary>
public sealed class VerifiedAction
{
    private readonly byte[] body;

    internal VerifiedAction(VerifiedRequestProof proof, byte[] body)
    {
        Proof = proof;
        this.body = body;
    }

    /// <summary>The proof, as checked.</summary>
    public VerifiedRequestProof Proof { get; }

    /// <summary>The action the proof authorizes.</summary>
    public RequestProofKind Kind => Proof.Kind;

    /// <summary>The key that signed the request.</summary>
    public PersonaPublicKey PublicKey => Proof.PublicKey;

    /// <summary>The challenge the proof carries, which the server must consume before it acts.</summary>
    public RequestChallenge Challenge => Proof.Challenge;

    /// <summary>
    /// The exact body the proof binds: a private copy, hashed once, which the server parses and
    /// acts on, never the request's buffer read a second time.
    /// </summary>
    public ReadOnlySpan<byte> Body => body;
}
