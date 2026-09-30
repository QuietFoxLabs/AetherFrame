using System;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas;

/// <summary>
/// A persona's signer, borrowed for one operation: sign, then dispose. It signs only as the persona
/// it was opened for, and only while that persona is still the active selection it was opened
/// under. Once the player selects another persona or deselects this one, the lease is revoked: its
/// signer refuses with <see cref="PersonaError.LeaseRevoked"/>, even if the persona is selected
/// again later, and the operation must open a new lease. That is decision L10 in
/// docs/networking/DecisionRegister.md, which follows NETWORK1.md's rule that nothing signs for a
/// persona that is not the active one. L10 also binds each operation to the persona it showed the
/// player: it opens its lease with <see cref="PersonaManager.TryOpenSigner"/>, signs once, and
/// disposes the lease, never holding one across I/O or a dialog.
/// <para>
/// The store's own signer is never handed out: <see cref="Signer"/> is a guard that checks, under
/// the manager's lock and for every signature, that the lease is open and current, that the input
/// names this persona, and that the store's signer still reports this persona's key. Written to
/// text it says the slot, never the identity.
/// </para>
/// </summary>
public sealed class PersonaSignerLease : IDisposable
{
    private readonly PersonaManager owner;
    private readonly GuardedSigner guard;
    private IPersonaSigner? signer;

    internal PersonaSignerLease(PersonaManager owner, PersonaRecord persona, IPersonaSigner signer, long selection)
    {
        this.owner = owner;
        this.signer = signer;
        Persona = persona;
        Selection = selection;
        guard = new GuardedSigner(this);
    }

    /// <summary>The persona the lease signs as, as its record was when the lease was opened.</summary>
    public PersonaRecord Persona { get; }

    /// <summary>The signer: a guard over the store's signer, never the store's signer itself.</summary>
    /// <exception cref="ObjectDisposedException">After <see cref="Dispose"/>.</exception>
    public IPersonaSigner Signer
    {
        get
        {
            ObjectDisposedException.ThrowIf(owner.IsReleased(this), this);
            return guard;
        }
    }

    /// <summary>The selection this lease was opened under.</summary>
    internal long Selection { get; }

    /// <summary>
    /// Releases the signer, disposing it when it is disposable, under the manager's lock. Safe to call
    /// more than once, and from any thread. A revoked lease still holds the store's signer (and the
    /// key copy inside it) until this is called: revocation refuses signatures, it does not release.
    /// </summary>
    public void Dispose() => owner.Release(this);

    /// <summary>The slot, never the identity.</summary>
    public override string ToString() => Persona.ToString();

    /// <summary>The store's signer, or null once released. Read and written only under the manager's lock.</summary>
    internal IPersonaSigner? Inner
    {
        get => signer;
        set => signer = value;
    }

    private sealed class GuardedSigner : IPersonaSigner
    {
        private readonly PersonaSignerLease lease;

        public GuardedSigner(PersonaSignerLease lease)
        {
            this.lease = lease;
        }

        public PersonaPublicKey PublicKey => lease.Persona.PublicKey;

        public ProtocolSignature Sign(SigningInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            return lease.owner.SignUnderLease(lease, input);
        }

        public override string ToString() => lease.ToString();
    }
}
