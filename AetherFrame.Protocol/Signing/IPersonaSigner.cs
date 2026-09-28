using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Signing;

/// <summary>
/// Whatever holds a persona's private key: it signs signing inputs and nothing else, so no caller
/// can obtain a signature over bytes the protocol did not frame. NETWORK0 ships one implementation,
/// the in-memory <see cref="EcdsaPersonaSigner"/>; a signer over protected local storage is a later
/// milestone (docs/networking/NETWORK0.md, "NETWORK1 integration points").
/// </summary>
public interface IPersonaSigner
{
    /// <summary>The public key every signature from this signer verifies with.</summary>
    PersonaPublicKey PublicKey { get; }

    /// <summary>Signs a signing input built for <see cref="PublicKey"/>.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidKey"/> when the input names another persona.</exception>
    ProtocolSignature Sign(SigningInput input);
}
