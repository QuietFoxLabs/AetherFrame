using System;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas;

/// <summary>
/// A persona's signer, borrowed for one operation: sign, then dispose. It is bound to the persona
/// it was opened for; selecting another persona meanwhile does not change what it signs as. Not
/// thread-safe, like the signer it wraps. Written to text it says the slot, never the identity.
/// </summary>
public sealed class PersonaSignerLease : IDisposable
{
    private IPersonaSigner? signer;

    internal PersonaSignerLease(PersonaRecord persona, IPersonaSigner signer)
    {
        Persona = persona;
        this.signer = signer;
    }

    /// <summary>The persona the signer belongs to, as its record was when the lease was opened.</summary>
    public PersonaRecord Persona { get; }

    /// <summary>The signer.</summary>
    /// <exception cref="ObjectDisposedException">After <see cref="Dispose"/>.</exception>
    public IPersonaSigner Signer => signer ?? throw new ObjectDisposedException(nameof(PersonaSignerLease));

    /// <summary>Releases the signer, disposing it when it is disposable.</summary>
    public void Dispose()
    {
        var released = signer;
        signer = null;
        (released as IDisposable)?.Dispose();
    }

    /// <summary>The slot, never the identity.</summary>
    public override string ToString() => Persona.ToString();
}
