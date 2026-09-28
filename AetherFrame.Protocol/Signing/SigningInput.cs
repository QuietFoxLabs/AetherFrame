using System;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Signing;

/// <summary>
/// The exact bytes a signature covers (docs/networking/ProtocolSpecification-v1.md, "Signing
/// input"): the signature domain tag with its one-byte length, the protocol version, the document
/// type, the persona's public key, and the payload with its four-byte length. Binding the type and
/// the version makes a signature useless for any other document type or protocol version; binding
/// the key makes it useless with any other key. Built by the protocol only, so nothing else is ever
/// signed. Immutable.
/// </summary>
public sealed class SigningInput
{
    private readonly byte[] bytes;

    private SigningInput(byte[] bytes, DocumentType documentType, PersonaPublicKey publicKey)
    {
        this.bytes = bytes;
        DocumentType = documentType;
        PublicKey = publicKey;
    }

    /// <summary>The document type the input binds.</summary>
    public DocumentType DocumentType { get; }

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
        return new SigningInput(writer.ToArray(), documentType, publicKey);
    }

    /// <summary>SHA-256 of <see cref="Bytes"/>: the value ECDSA actually signs.</summary>
    public byte[] ComputeDigest() => SHA256.HashData(bytes);
}
