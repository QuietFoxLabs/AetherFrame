namespace AetherFrame.Protocol.Requests;

/// <summary>
/// What a request proof authorizes (docs/networking/ProtocolSpecification-v1.md, section 14). A
/// <c>u8</c> on the wire, closed like every enumeration: an unknown kind is refused. Every kind
/// shares one layout; the kind is signed, so a proof made for one kind never verifies as another.
/// </summary>
public enum RequestProofKind : byte
{
    /// <summary>Submitting one signed document (a snapshot or a retraction) to one deployment.</summary>
    DocumentSubmission = 1,

    /// <summary>An action: asking for a Lodestone check code for the signer's character (decisions C2 and C9).</summary>
    LodestoneCode = 2,

    /// <summary>An action: checking a Lodestone code, which binds a character to the signer (C2).</summary>
    LodestoneCheck = 3,

    /// <summary>An action: asking the server to read the signer's character's Lodestone page again (C1).</summary>
    LodestoneReread = 4,

    /// <summary>An action: turning sharing off for the signer's character (C4).</summary>
    OptOut = 5,

    /// <summary>An action: looking a character's Plate up by name and World (C5).</summary>
    Lookup = 6,

    /// <summary>An action: fetching one image of a looked-up Plate (C5).</summary>
    Image = 7,

    /// <summary>An action: reporting a Plate to the operator (C5).</summary>
    Report = 8,

    /// <summary>
    /// An action: counting the signer's bound character as online, and starting the presence session
    /// whose heartbeats keep it counted ("The online count" in the decision register).
    /// </summary>
    Presence = 9,
}
