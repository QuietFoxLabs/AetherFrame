using System;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Requests;

namespace AetherFrame.Protocol.Signing;

/// <summary>
/// The exact bytes a signature covers (docs/networking/ProtocolSpecification-v1.md, sections 5,
/// 5.1 and 14). Every input starts with its context's domain tag and that tag's one-byte length,
/// then binds the protocol version and the persona's public key. A document's input binds the
/// document type and the payload with its four-byte length, which makes a signature useless for any
/// other document type or protocol version; a request proof's input binds its kind, the deployment,
/// the challenge and the subject digest. Binding the key makes a signature useless with any other
/// key. Built by the protocol only, so nothing else is ever signed. Immutable.
/// </summary>
public sealed class SigningInput
{
    private readonly byte[] bytes;

    private SigningInput(byte[] bytes, SigningContext context, DocumentType? documentType, PersonaPublicKey publicKey)
    {
        this.bytes = bytes;
        Context = context;
        DocumentType = documentType;
        PublicKey = publicKey;
    }

    /// <summary>The signing context the input belongs to.</summary>
    public SigningContext Context { get; }

    /// <summary>The document type a document's input binds; null for every other context.</summary>
    public DocumentType? DocumentType { get; }

    /// <summary>The key the input binds: the only key a signature over it can verify with.</summary>
    public PersonaPublicKey PublicKey { get; }

    /// <summary>The bytes to sign or verify.</summary>
    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>Builds the signing input of a document.</summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.UnknownDocumentType"/>, <see cref="ProtocolError.InvalidLength"/> for an
    /// empty payload, or <see cref="ProtocolError.LimitExceeded"/> for one over <see cref="ProtocolLimits.MaxPayloadBytes"/>.
    /// </exception>
    public static SigningInput Create(DocumentType documentType, PersonaPublicKey publicKey, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (!DocumentTypes.IsKnown(documentType))
        {
            throw new ProtocolException(ProtocolError.UnknownDocumentType, $"Document type {ProtocolText.Number((byte)documentType)} is not known.");
        }

        if (payload.Length == 0)
        {
            throw new ProtocolException(ProtocolError.InvalidLength, "A payload is never empty.");
        }

        if (payload.Length > ProtocolLimits.MaxPayloadBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The payload is {ProtocolText.Number(payload.Length)} bytes; the limit is {ProtocolText.Number(ProtocolLimits.MaxPayloadBytes)}.");
        }

        var tag = ProtocolConstants.SignatureDomainTag;
        var writer = new CanonicalWriter(1 + tag.Length + 2 + 1 + ProtocolConstants.PublicKeyLength + 4 + payload.Length);
        writer.WriteU8((byte)tag.Length);
        writer.WriteFixed(tag);
        writer.WriteU16(ProtocolConstants.ProtocolVersion);
        writer.WriteU8((byte)documentType);
        writer.WriteFixed(publicKey.Bytes);
        writer.WriteLengthPrefixed(payload);
        return new SigningInput(writer.ToArray(), SigningContext.SignedDocument, documentType, publicKey);
    }

    /// <summary>
    /// Builds the signing input of a request proof: the proof's own tag, then exactly the bytes the
    /// proof carries between its magic and its signature.
    /// </summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.InvalidValue"/> for an unknown kind, or <see cref="ProtocolError.InvalidLength"/>
    /// for a subject digest that is not <see cref="ProtocolConstants.DigestLength"/> bytes.
    /// </exception>
    public static SigningInput CreateRequestProof(RequestProofKind kind, PersonaPublicKey publicKey, DeploymentName deployment, RequestChallenge challenge, ReadOnlySpan<byte> subjectDigest)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(challenge);
        if (kind != RequestProofKind.DocumentSubmission)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"Request proof kind {ProtocolText.Number((byte)kind)} is not known.");
        }

        if (subjectDigest.Length != ProtocolConstants.DigestLength)
        {
            throw new ProtocolException(ProtocolError.InvalidLength, $"A subject digest is {ProtocolText.Number(ProtocolConstants.DigestLength)} bytes; this one is {ProtocolText.Number(subjectDigest.Length)}.");
        }

        var tag = ProtocolConstants.RequestProofDomainTag;
        var writer = new CanonicalWriter(1 + tag.Length + ProtocolLimits.MaxRequestProofBytes);
        writer.WriteU8((byte)tag.Length);
        writer.WriteFixed(tag);
        WriteRequestProofBody(writer, kind, publicKey, deployment, challenge, subjectDigest);
        return new SigningInput(writer.ToArray(), SigningContext.RequestProof, null, publicKey);
    }

    /// <summary>SHA-256 of <see cref="Bytes"/>: the value ECDSA actually signs.</summary>
    public byte[] ComputeDigest() => SHA256.HashData(bytes);

    /// <summary>
    /// The part of a request proof its signature covers after the tag: version, kind, key,
    /// deployment name with its length, challenge and subject digest. One writer for the signing
    /// input and the proof itself, so the two can never disagree.
    /// </summary>
    internal static void WriteRequestProofBody(CanonicalWriter writer, RequestProofKind kind, PersonaPublicKey publicKey, DeploymentName deployment, RequestChallenge challenge, ReadOnlySpan<byte> subjectDigest)
    {
        writer.WriteU16(ProtocolConstants.ProtocolVersion);
        writer.WriteU8((byte)kind);
        writer.WriteFixed(publicKey.Bytes);
        writer.WriteU8((byte)deployment.Bytes.Length);
        writer.WriteFixed(deployment.Bytes);
        writer.WriteFixed(challenge.Bytes);
        writer.WriteFixed(subjectDigest);
    }
}
