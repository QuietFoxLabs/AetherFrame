using System;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Protocol.Documents;

/// <summary>
/// The signed document envelope (docs/networking/ProtocolSpecification-v1.md, "Signed document"):
/// magic, protocol version, document type, the persona's public key, the payload with its length,
/// and the signature. <see cref="Sign"/> is the only way to produce one and <see cref="Verify"/> the
/// only way to read one: the payload of a document whose signature does not verify is never decoded.
/// </summary>
public static class SignedDocumentCodec
{
    /// <summary>
    /// Encodes <paramref name="document"/> and signs it as <paramref name="signer"/>'s persona. The
    /// signer's public key is read once, for the signing input and the envelope alike, and the
    /// finished document is verified before it is returned: a signer that misreports its key, signs
    /// with another key or signs other bytes produces no document.
    /// </summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.SignatureMismatch"/> when the signer's signature does not verify over the
    /// document with the key the signer reported, <see cref="ProtocolError.InvalidKey"/> or
    /// <see cref="ProtocolError.InvalidSignature"/> when the signer returns nothing. The signer's own
    /// exceptions pass through unchanged.
    /// </exception>
    public static byte[] Sign(RemoteDocument document, IPersonaSigner signer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signer);

        var publicKey = signer.PublicKey;
        if (publicKey is null)
        {
            throw new ProtocolException(ProtocolError.InvalidKey, "The signer reports no public key.");
        }

        var payload = document.EncodePayload();
        var input = SigningInput.Create(document.DocumentType, publicKey, payload);
        var signature = signer.Sign(input);
        if (signature is null)
        {
            throw new ProtocolException(ProtocolError.InvalidSignature, "The signer returned no signature.");
        }

        var bytes = Assemble(document.DocumentType, publicKey, payload, signature);

        // The reader decides what a document is: bytes it would refuse never leave here as one. The
        // cost is one verification per document signed.
        try
        {
            Verify(bytes);
        }
        catch (ProtocolException e)
        {
            throw new ProtocolException(e.Error, $"The signer's output is not a valid document, so none was produced: {e.Message}");
        }

        return bytes;
    }

    /// <summary>
    /// Reads a document from hostile bytes: framing, version, type, key, lengths and trailing bytes
    /// are checked, then the signature, and only then is the payload decoded. Everything refused
    /// throws <see cref="ProtocolException"/>. The input is copied once before anything is read from
    /// it, so a buffer another thread writes to meanwhile can make the document invalid but can never
    /// make a value that was checked differ from the value that is used.
    /// </summary>
    public static VerifiedDocument Verify(ReadOnlySpan<byte> document)
    {
        if (document.Length > ProtocolLimits.MaxDocumentBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The document is {ProtocolText.Number(document.Length)} bytes; the limit is {ProtocolText.Number(ProtocolLimits.MaxDocumentBytes)}.");
        }

        // One private copy of the whole input (at most 1 MiB, in proportion to the input, never to a
        // declared length), taken before the first byte is read: every check below, the signature
        // verification and the payload decoding all see exactly these bytes.
        var bytes = document.ToArray();
        var reader = new CanonicalReader(bytes);
        if (!reader.ReadFixed(ProtocolConstants.DocumentMagic.Length, "magic").SequenceEqual(ProtocolConstants.DocumentMagic))
        {
            throw new ProtocolException(ProtocolError.InvalidFraming, "The input does not start with the signed document magic.");
        }

        var version = reader.ReadU16("protocolVersion");
        if (version != ProtocolConstants.ProtocolVersion)
        {
            throw new ProtocolException(ProtocolError.UnsupportedVersion, $"Protocol version {ProtocolText.Number(version)} is not supported; this build reads version {ProtocolText.Number(ProtocolConstants.ProtocolVersion)}.");
        }

        var type = (DocumentType)reader.ReadU8("documentType");
        if (!DocumentTypes.IsKnown(type))
        {
            throw new ProtocolException(ProtocolError.UnknownDocumentType, $"Document type {ProtocolText.Number((byte)type)} is not known.");
        }

        var keyBytes = reader.ReadFixed(ProtocolConstants.PublicKeyLength, "personaPublicKey");
        var payload = reader.ReadLengthPrefixed(ProtocolLimits.MaxPayloadBytes, "payload");
        if (payload.Length == 0)
        {
            throw new ProtocolException(ProtocolError.InvalidLength, "A payload is never empty.");
        }

        var signatureBytes = reader.ReadFixed(ProtocolConstants.SignatureLength, "signature");
        reader.ExpectEnd("The signed document");

        // Structural checks are done; from here each step costs more, and each is fail-closed.
        var key = PersonaPublicKey.FromBytes(keyBytes);
        var signature = ProtocolSignature.FromBytes(signatureBytes);
        var input = SigningInput.Create(type, key, payload);
        if (!SignatureVerifier.Verify(input, signature))
        {
            throw new ProtocolException(ProtocolError.SignatureMismatch, "The signature does not verify over the document with the key it names.");
        }

        return new VerifiedDocument(key, PayloadCodec.Decode(type, payload));
    }

    /// <summary>Lays out the envelope. Internal so tests can build documents whose parts disagree.</summary>
    internal static byte[] Assemble(DocumentType type, PersonaPublicKey publicKey, ReadOnlySpan<byte> payload, ProtocolSignature signature)
    {
        var writer = new CanonicalWriter(ProtocolLimits.SignedDocumentOverheadBytes + payload.Length);
        writer.WriteFixed(ProtocolConstants.DocumentMagic);
        writer.WriteU16(ProtocolConstants.ProtocolVersion);
        writer.WriteU8((byte)type);
        writer.WriteFixed(publicKey.Bytes);
        writer.WriteLengthPrefixed(payload);
        writer.WriteFixed(signature.Bytes);
        return writer.ToArray();
    }
}
