using System;

namespace AetherFrame.Protocol;

/// <summary>
/// The one exception this assembly throws for input it refuses: hostile or malformed bytes, a key
/// or signature that does not check out, or a model value outside the protocol's limits. Any other
/// exception escaping the protocol is a bug. Messages name fields, limits and lengths, never the
/// content of a document, a key or a signature.
/// </summary>
public sealed class ProtocolException : Exception
{
    /// <summary>Creates the exception for <paramref name="error"/> with a message that describes the refused value without repeating it.</summary>
    public ProtocolException(ProtocolError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Why the input was refused.</summary>
    public ProtocolError Error { get; }
}
