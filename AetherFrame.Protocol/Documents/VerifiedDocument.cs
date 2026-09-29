using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;

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

    /// <summary>
    /// The profile the document is about: the signing persona's profile of the id the document
    /// carries (docs/networking/ProtocolSpecification-v1.md, "Profile identity and ownership"). A
    /// snapshot is a revision of this profile and a retraction withdraws this profile; neither can
    /// touch a profile of any other persona, whatever id it carries. Null for a document that is not
    /// about a profile, which no version 1 document type is (decision N5,
    /// docs/networking/DecisionRegister.md).
    /// </summary>
    public RemoteProfileKey? Profile => Document is RemoteProfileDocument profileDocument ? new RemoteProfileKey(Persona, profileDocument.ProfileId) : null;

    /// <summary>The wire type of <see cref="Document"/>.</summary>
    public DocumentType DocumentType => Document.DocumentType;

    /// <summary>The decoded content: a <see cref="Remote.ProfileSnapshot"/> or a <see cref="Remote.ProfileRetraction"/>.</summary>
    public RemoteDocument Document { get; }
}
