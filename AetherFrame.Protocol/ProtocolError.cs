namespace AetherFrame.Protocol;

/// <summary>
/// Why a document, a key, a signature or a value was refused. Every failure of this assembly is one
/// of these, carried by <see cref="ProtocolException"/>; a caller that wants to react to the kind of
/// failure switches on the code, never on the message.
/// </summary>
public enum ProtocolError
{
    /// <summary>The input does not start with the document magic, or its framing is otherwise not a signed document.</summary>
    InvalidFraming,

    /// <summary>The document or payload declares a protocol or schema version this build does not read.</summary>
    UnsupportedVersion,

    /// <summary>The document declares a document type this build does not know.</summary>
    UnknownDocumentType,

    /// <summary>The input ended before a value it declares was complete.</summary>
    Truncated,

    /// <summary>The input continues after the last value of the document.</summary>
    TrailingBytes,

    /// <summary>A length, count, size or total is over its limit (<see cref="ProtocolLimits"/>).</summary>
    LimitExceeded,

    /// <summary>A length or count is one the format does not allow, such as an empty payload.</summary>
    InvalidLength,

    /// <summary>The public key is not an uncompressed P-256 point on the curve.</summary>
    InvalidKey,

    /// <summary>The signature bytes are not a canonical P-256 signature (r or s out of range, or s not in the low half).</summary>
    InvalidSignature,

    /// <summary>The signature is well formed but does not verify over the document with its key.</summary>
    SignatureMismatch,

    /// <summary>A text field is not valid UTF-8 or contains U+0000. A text field over its length limit is <see cref="LimitExceeded"/>.</summary>
    InvalidText,

    /// <summary>A value is outside what its field allows: an unknown enumeration code, an all-zero identifier, a zero dimension, a timestamp out of range.</summary>
    InvalidValue,

    /// <summary>The input is well formed but is not the one canonical encoding of its content, for example a set that is not sorted.</summary>
    NotCanonical,

    /// <summary>
    /// A request proof verifies but does not authorize the request it came with: it names another
    /// deployment, binds another document, or is signed by a key other than the document's.
    /// </summary>
    ProofMismatch,
}
