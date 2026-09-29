using System;

namespace AetherFrame.Personas;

/// <summary>A persona operation refused for a <see cref="PersonaError"/>. Messages never carry labels, identities or key bytes.</summary>
public sealed class PersonaException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PersonaException(PersonaError error, string message)
        : base(message)
    {
        Error = error;
    }

    internal PersonaException(PersonaError error, string message, Exception inner)
        : base(message, inner)
    {
        Error = error;
    }

    /// <summary>Which rule refused the operation.</summary>
    public PersonaError Error { get; }
}
