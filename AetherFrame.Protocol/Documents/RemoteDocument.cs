namespace AetherFrame.Protocol.Documents;

/// <summary>
/// The content of a signed document: one of the remote models, each immutable and validated
/// against <see cref="ProtocolLimits"/> when it is built, so that a value of one of these types can
/// always be encoded. The set of subtypes is closed (the constructor is private protected), and none
/// of them is or wraps a local editor model. Only the documents about a profile carry a profile id,
/// as <see cref="Remote.RemoteProfileDocument"/> (decision N5, docs/networking/DecisionRegister.md).
/// </summary>
public abstract class RemoteDocument
{
    private protected RemoteDocument()
    {
    }

    /// <summary>The wire type of this document.</summary>
    public abstract DocumentType DocumentType { get; }

    /// <summary>The canonical payload bytes (docs/networking/ProtocolSpecification-v1.md, "Payloads").</summary>
    internal abstract byte[] EncodePayload();
}
