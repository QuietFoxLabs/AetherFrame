namespace AetherFrame.Protocol.Documents;

/// <summary>
/// What a signed document is. The byte value is part of the wire format and of every signing
/// input, so a signature over one type can never verify as another. The set is closed: a value not
/// listed here is refused, never mapped to a nearest type.
/// </summary>
public enum DocumentType : byte
{
    /// <summary>An immutable published revision of a remote profile (<see cref="Remote.ProfileSnapshot"/>).</summary>
    ProfileSnapshot = 1,

    /// <summary>The withdrawal of a remote profile from publication (<see cref="Remote.ProfileRetraction"/>).</summary>
    ProfileRetraction = 2,
}

internal static class DocumentTypes
{
    public static bool IsKnown(DocumentType type) => type is DocumentType.ProfileSnapshot or DocumentType.ProfileRetraction;
}
