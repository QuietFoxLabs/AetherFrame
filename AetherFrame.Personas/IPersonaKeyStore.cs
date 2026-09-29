using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas;

/// <summary>
/// Custody of persona private keys: the one seam through which a key is made, kept, opened for
/// signing or handed to a backup codec. The <see cref="PersonaManager"/> holds public records and
/// never keeps a private key; an implementation keeps keys and never a record. No implementation in
/// this assembly stores anything. The protected store for real keys is a later increment, gated on
/// the open key-storage decisions (docs/networking/DecisionRegister.md, K2 and K3), and until a
/// reviewed one exists the only implementations are in-memory test doubles. Nothing about this
/// interface makes a key safe: that is a property of an implementation, and only a reviewed one may
/// claim it.
/// <para>
/// The manager makes every call to a store, and to the signers it returns (signing and disposal
/// alike), under its one lock, so an implementation is never called concurrently. The price is that
/// every manager member, listings included, waits while a store call runs: an implementation that
/// blocks (reading files, unprotecting keys, prompting) stalls a frame thread that only wanted a
/// listing. No implementation exists yet; the one that does must keep its calls short or the
/// manager's locking must be split, and that is a decision for the key store increment.
/// </para>
/// <para>
/// Custody is taken in two steps so that nothing is committed before it is checked: a key is made
/// (<see cref="GenerateKey"/>) or restored by a codec without the store holding it, the manager
/// checks its identity against the personas it holds, and only then does the store commit it
/// (<see cref="AddKey"/>). A refusal therefore never leaves a key in a store without a record, and
/// the manager never needs to delete one: nothing in this interface deletes a key.
/// </para>
/// <para>
/// Ownership is the same everywhere: material or a signer a store returns is the caller's to
/// dispose, and material a caller passes in stays the caller's; a store keeps its own copy of what
/// it holds and never the caller's object.
/// </para>
/// </summary>
public interface IPersonaKeyStore
{
    /// <summary>
    /// A fresh random P-256 key that this store is able to hold, not held yet: nothing is committed
    /// until <see cref="AddKey"/>. The caller owns and disposes it. Every call makes a new key; a
    /// store that handed out a key it already holds would give two records one identity, which the
    /// manager refuses before anything is committed.
    /// </summary>
    PersonaKeyMaterial GenerateKey();

    /// <summary>
    /// Commits a copy of <paramref name="material"/>'s key to custody under <paramref name="slot"/>.
    /// Four rules bind every implementation. It commits exactly that key: the manager records the
    /// persona as soon as this returns and never sees the key again, so a store that commits
    /// anything else (a scalar that lost a byte in serialization, say) leaves a persona that can
    /// never sign or be backed up; a store verifies what it wrote before it returns. It never
    /// replaces: a slot the store already holds is refused with an exception and its key is left
    /// exactly as it was. It is atomic: it returns only once the key is held, and when it throws,
    /// nothing is held under <paramref name="slot"/>. It never retains <paramref name="material"/>
    /// itself, which stays the caller's to dispose whether this returns or throws.
    /// </summary>
    void AddKey(PersonaSlotId slot, PersonaKeyMaterial material);

    /// <summary>
    /// A signer over the slot's key for one operation, disposed by the caller when it is disposable,
    /// or null when the key cannot be opened now: no such slot, locked on this account, damaged, or
    /// a platform that cannot sign. An unavailable key is reported as null, never thrown. The signer
    /// must hold the private half: the manager checks the public key it reports, not that it can sign,
    /// so a public-only signer passes the check and fails at its first signature (L4 in
    /// docs/networking/DecisionRegister.md, still open for the key store's design). A signer made by
    /// <see cref="PersonaKeyMaterial.CreateSigner"/> always holds it.
    /// </summary>
    IPersonaSigner? OpenSigner(PersonaSlotId slot);

    /// <summary>
    /// The slot's key material for a backup codec, owned and disposed by the caller, or null when
    /// the key cannot be opened now. This is the only way private material leaves a store, and the
    /// manager calls it for nothing but an export the player asked for.
    /// </summary>
    PersonaKeyMaterial? OpenKey(PersonaSlotId slot);
}
