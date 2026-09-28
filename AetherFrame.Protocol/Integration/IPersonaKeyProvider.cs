using AetherFrame.Protocol.Signing;

namespace AetherFrame.Protocol.Integration;

/// <summary>
/// The seam between the plugin and the protocol: whatever keeps the player's persona key (a later
/// milestone: generated once, protected with the platform's user-scoped protection, never leaving
/// the machine) hands out a signer for the active persona. NETWORK0 defines the interface only; no
/// implementation reads or writes anything, and the plugin does not reference this assembly yet.
/// </summary>
public interface IPersonaKeyProvider
{
    /// <summary>The signer of the active persona, or null when the player has no persona yet.</summary>
    IPersonaSigner? ActiveSigner { get; }
}
