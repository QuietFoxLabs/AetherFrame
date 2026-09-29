using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas;

/// <summary>
/// Custody of persona private keys: the one seam through which a key is made, opened for signing
/// or handed to a backup codec. The <see cref="PersonaManager"/> holds public records and never a
/// private key; an implementation holds keys and never a record. No implementation in this assembly
/// stores anything. The protected store for real keys is a later increment, gated on the open
/// key-storage decisions (docs/networking/DecisionRegister.md, K2 and K3), and until a reviewed one
/// exists the only implementations are in-memory test doubles. Nothing about this interface makes a
/// key safe: that is a property of an implementation, and only a reviewed one may claim it.
/// Implementations may block (a store that reads files or unprotects keys does), so a caller on a
/// frame thread hands the work to another thread.
/// </summary>
public interface IPersonaKeyStore
{
    /// <summary>
    /// Makes a fresh P-256 key under <paramref name="slot"/>, keeps its private half and returns the
    /// public half. Every call generates a new random key: a store that handed out an existing key
    /// would give two records one identity, which the manager refuses.
    /// </summary>
    PersonaPublicKey CreateKey(PersonaSlotId slot);

    /// <summary>
    /// Takes custody of <paramref name="material"/>, a key a backup codec restored, under
    /// <paramref name="slot"/>, and returns its public half. Ownership passes on success: the store
    /// disposes the material when it is done with it, and the caller does not use it again. On
    /// failure the material stays the caller's.
    /// </summary>
    PersonaPublicKey AdoptKey(PersonaSlotId slot, PersonaKeyMaterial material);

    /// <summary>
    /// A signer over the slot's key for one operation, disposed by the caller when it is disposable,
    /// or null when the key cannot be opened now: no such slot, locked on this account, damaged, or
    /// a platform that cannot sign. An unavailable key is reported as null, never thrown.
    /// </summary>
    IPersonaSigner? OpenSigner(PersonaSlotId slot);

    /// <summary>
    /// The slot's key material for a backup codec, owned and disposed by the caller, or null when
    /// the key cannot be opened now. This is the only way private material leaves a store, and the
    /// manager calls it for nothing but an export the player asked for.
    /// </summary>
    PersonaKeyMaterial? OpenKey(PersonaSlotId slot);
}
