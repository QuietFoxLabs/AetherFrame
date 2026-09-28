using AetherFrame.Protocol.Signing;

namespace AetherFrame.Protocol.Integration;

/// <summary>
/// The seam between the plugin and the protocol: whatever keeps the player's persona key (a later
/// milestone: generated once, protected with the platform's user-scoped protection, never leaving
/// the machine) hands out a signer for the active persona. NETWORK0 defines the interface only; no
/// implementation reads or writes anything, and the plugin does not reference this assembly yet.
///
/// PROVISIONAL (review finding L6, docs/networking/NETWORK0.md, "Open product decisions"): the shape
/// of this interface, a synchronous getter for one active signer, is a placeholder until NETWORK1
/// designs key storage (loading protected keys, prompting, several personas, D3). It may change or
/// be removed then; nothing should depend on it before that.
/// </summary>
public interface IPersonaKeyProvider
{
    /// <summary>The signer of the active persona, or null when the player has no persona yet.</summary>
    IPersonaSigner? ActiveSigner { get; }
}
