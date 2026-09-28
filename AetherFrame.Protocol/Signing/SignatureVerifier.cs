using System;
using System.Security.Cryptography;

namespace AetherFrame.Protocol.Signing;

/// <summary>
/// Checks a canonical signature over a signing input with the key the input binds. There is no
/// way to pass a different key: the key a document names is the key it is verified with, and the
/// persona identity is derived from that key afterwards, never taken from the caller.
/// </summary>
public static class SignatureVerifier
{
    /// <summary>True when <paramref name="signature"/> is a valid ECDSA P-256 / SHA-256 signature over <paramref name="input"/> by its key.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidKey"/> when the platform refuses the input's key, which the protocol already checked.</exception>
    public static bool Verify(SigningInput input, ProtocolSignature signature)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(signature);

        using var key = input.PublicKey.CreateEcdsa();
        try
        {
            return key.VerifyData(input.Bytes, signature.Bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
