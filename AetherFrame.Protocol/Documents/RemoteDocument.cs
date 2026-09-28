using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Documents;

/// <summary>
/// The content of a signed document: one of the remote models, each immutable and validated
/// against <see cref="ProtocolLimits"/> when it is built, so that a value of one of these types can
/// always be encoded. The set of subtypes is closed (the constructor is private protected), and none
/// of them is or wraps a local editor model.
/// </summary>
public abstract class RemoteDocument
{
    private protected RemoteDocument()
    {
    }

    /// <summary>The wire type of this document.</summary>
    public abstract DocumentType DocumentType { get; }

    /// <summary>
    /// The profile the document is about. On its own it names nothing: a document only ever refers
    /// to a profile of the persona that signed it, so the profile is (persona, profile id), which a
    /// verified document exposes as <see cref="VerifiedDocument.Profile"/>.
    /// </summary>
    public abstract ProfileId ProfileId { get; }

    /// <summary>The canonical payload bytes (docs/networking/ProtocolSpecification-v1.md, "Payloads").</summary>
    internal abstract byte[] EncodePayload();
}
