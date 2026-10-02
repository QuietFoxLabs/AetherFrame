using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Signing;

/// <summary>
/// Whatever holds a persona's private key: it signs signing inputs and nothing else, so no caller
/// can obtain a signature over bytes the protocol did not frame. The in-memory
/// <see cref="EcdsaPersonaSigner"/> is the implementation that holds a key; the key store core
/// (AetherFrame.Personas, ProtectedPersonaKeyStore) opens one over a key it rebuilds from protected
/// storage, and the other implementations (a PersonaSignerLease's guard, the plugin's leased signer)
/// only forward to it. The protocol does not trust an implementation:
/// <see cref="Documents.SignedDocumentCodec.Sign"/> and
/// <see cref="Requests.RequestProofCodec.Sign"/> read <see cref="PublicKey"/> once and verify every
/// signature before it becomes part of a document or a proof.
/// </summary>
public interface IPersonaSigner
{
    /// <summary>The public key every signature from this signer verifies with.</summary>
    PersonaPublicKey PublicKey { get; }

    /// <summary>Signs a signing input built for <see cref="PublicKey"/>.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidKey"/> when the input names another persona.</exception>
    ProtocolSignature Sign(SigningInput input);
}
