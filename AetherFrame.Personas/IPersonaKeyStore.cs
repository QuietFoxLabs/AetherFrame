using AetherFrame.Personas.Storage;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas;

/// <summary>
/// Custody of persona private keys: the one seam through which a key is made, kept, opened for
/// signing or handed to a backup codec. The <see cref="PersonaManager"/> holds public records and
/// never keeps a private key; an implementation keeps keys and never a record. The implementation
/// for real keys is <see cref="Storage.ProtectedPersonaKeyStore"/> (the key store core, K1, K2, K6
/// and K7 in docs/networking/DecisionRegister.md), which keeps protected envelopes in a storage and
/// through a protector the plugin supplies: the Windows DPAPI protector (N2-4, NETWORK1 increment 7)
/// is compiled only into the networking preview flavour, and nothing wires a store to the plugin
/// yet. Nothing about this interface makes a key safe: that is a property of an implementation and
/// its protector, and only a reviewed one may claim it.
/// <para>
/// The manager makes every call to a store, and to the signers it returns (signing and disposal
/// alike), under its one lock, so an implementation is never called concurrently. The price is that
/// every manager member, listings included, waits while a store call runs: an implementation that
/// blocks (reading files, unprotecting keys, prompting) stalls a frame thread that only wanted a
/// listing. The key store core keeps its calls short (a few small reads and writes and at most two
/// protector calls); whether the wiring (increment 9) calls the manager off the frame thread or
/// splits its lock is decided there.
/// </para>
/// <para>
/// Custody is taken in two steps so that nothing is committed before it is checked: a key is made
/// (<see cref="GenerateKey"/>) or restored by a codec without the store holding it, the manager
/// checks its identity against the personas it holds, and only then does the store commit it
/// (<see cref="AddKey"/>). A refusal before the commit therefore never leaves a key in a store
/// without a record. Nothing in this interface deletes a key, so a key can be held with no record in
/// two cases: a commit that fails after the store's storage accepted the key (see
/// <see cref="AddKey"/>), and a registry save that fails after a commit (P3). Both are found by
/// <see cref="ListHeld"/> and <see cref="PeekPublicKey"/>, and offered to the player for restore
/// (L12 in docs/networking/DecisionRegister.md).
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
    /// exactly as it was. It returns only once the key is held and verified, and when it throws
    /// before anything became durable, nothing is held under <paramref name="slot"/>; a store that
    /// cannot delete may be left holding an unverified key under that slot when the verification
    /// after a durable write fails, and must say so in its exception. The caller never records or
    /// reuses a slot whose commit threw (L12 in docs/networking/DecisionRegister.md). It never
    /// retains <paramref name="material"/> itself, which stays the caller's to dispose whether this
    /// returns or throws.
    /// </summary>
    void AddKey(PersonaSlotId slot, PersonaKeyMaterial material);

    /// <summary>
    /// A signer over the slot's key for one operation, disposed by the caller when it is disposable,
    /// or null when the key cannot be opened now: no such slot, locked on this account, damaged, or
    /// a platform that cannot sign. An unavailable key is reported as null, never thrown. The signer
    /// must hold the private half: the manager checks the public key it reports, not that it can sign,
    /// so a public-only signer passes the check and fails at its first signature (L4 in
    /// docs/networking/DecisionRegister.md, settled for the key store core, which makes every signer
    /// through <see cref="PersonaKeyMaterial.CreateSigner"/>). A signer made that way always holds it.
    /// </summary>
    IPersonaSigner? OpenSigner(PersonaSlotId slot);

    /// <summary>
    /// The slot's key material for a backup codec, owned and disposed by the caller, or null when
    /// the key cannot be opened now. This is the only way private material leaves a store, and the
    /// manager calls it for nothing but an export the player asked for.
    /// </summary>
    PersonaKeyMaterial? OpenKey(PersonaSlotId slot);

    /// <summary>Every slot a key is held under, as the storage lists them. Throws only when the storage itself fails.</summary>
    PersonaKeyListing ListHeld();

    /// <summary>
    /// What the key's envelope header says about <paramref name="slot"/>, read without opening the
    /// key: <paramref name="publicKey"/> is the public key it names when the status is
    /// <see cref="PersonaKeyPeekStatus.Held"/>, and null otherwise. The header is unverified: anyone
    /// who can write the key storage could forge it, which only opening the key would show. Never throws.
    /// </summary>
    PersonaKeyPeekStatus PeekPublicKey(PersonaSlotId slot, out Protocol.Identity.PersonaPublicKey? publicKey);

    /// <summary>
    /// The public key of the key held under <paramref name="slot"/>, proven by opening it (the same
    /// checks as <see cref="OpenSigner"/>) and disposing the material at once; null when it does not
    /// open. No private material leaves the store. Unavailability is null, never an exception.
    /// </summary>
    Protocol.Identity.PersonaPublicKey? OpenPublicKey(PersonaSlotId slot);
}
