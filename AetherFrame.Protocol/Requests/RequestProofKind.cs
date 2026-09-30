namespace AetherFrame.Protocol.Requests;

/// <summary>
/// What a request proof authorizes (docs/networking/ProtocolSpecification-v1.md, section 14). A
/// <c>u8</c> on the wire, closed like every enumeration: an unknown kind is refused.
/// </summary>
public enum RequestProofKind : byte
{
    /// <summary>Submitting one signed document (a snapshot or a retraction) to one deployment.</summary>
    DocumentSubmission = 1,
}
