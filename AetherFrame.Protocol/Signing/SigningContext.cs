namespace AetherFrame.Protocol.Signing;

/// <summary>
/// What a signing input is for (docs/networking/ProtocolSpecification-v1.md, section 5.1). Each
/// context starts its input with its own domain tag, so a signature made in one never verifies in
/// another. Closed: a new context is a new specification section, never a new value here alone.
/// </summary>
public enum SigningContext
{
    /// <summary>A signed document (section 5).</summary>
    SignedDocument = 1,

    /// <summary>A request proof (section 14).</summary>
    RequestProof = 2,
}
