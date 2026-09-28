using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Documents;

/// <summary>
/// A document whose signature verified with the key it names, whose payload then decoded strictly:
/// the only way to obtain a <see cref="RemoteDocument"/> from bytes. The persona is derived from
/// the verified key, never supplied by the caller. Immutable.
/// </summary>
public sealed class VerifiedDocument
{
    internal VerifiedDocument(PersonaPublicKey publicKey, RemoteDocument document)
    {
        PublicKey = publicKey;
        Document = document;
    }

    /// <summary>The key the signature verified with.</summary>
    public PersonaPublicKey PublicKey { get; }

    /// <summary>The persona that signed the document.</summary>
    public PersonaId Persona => PublicKey.Id;

    /// <summary>The wire type of <see cref="Document"/>.</summary>
    public DocumentType DocumentType => Document.DocumentType;

    /// <summary>The decoded content: a <see cref="Remote.ProfileSnapshot"/> or a <see cref="Remote.ProfileRetraction"/>.</summary>
    public RemoteDocument Document { get; }
}
