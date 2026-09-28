using System;
using System.Security.Cryptography;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Signing;

/// <summary>
/// A signer over a platform P-256 key held in memory. It owns the key it is given and disposes it;
/// it never exports, copies, logs or otherwise reveals private material, and has no way to. How a
/// key is generated, protected and stored across sessions is not part of NETWORK0: the caller that
/// creates the <see cref="ECDsa"/> decides that.
/// </summary>
public sealed class EcdsaPersonaSigner : IPersonaSigner, IDisposable
{
    private readonly ECDsa key;
    private bool disposed;

    /// <summary>Takes ownership of <paramref name="key"/>, which must be a named-curve P-256 key with a private half.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidKey"/>.</exception>
    public EcdsaPersonaSigner(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        PublicKey = PersonaPublicKey.FromEcdsa(key);
        this.key = key;
    }

    /// <inheritdoc />
    public PersonaPublicKey PublicKey { get; }

    /// <summary>A signer over a fresh random key that lives only in this process.</summary>
    public static EcdsaPersonaSigner CreateEphemeral() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <inheritdoc />
    public ProtocolSignature Sign(SigningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!input.PublicKey.Equals(PublicKey))
        {
            throw new ProtocolException(ProtocolError.InvalidKey, "The signing input names a different persona than this signer.");
        }

        Span<byte> rs = stackalloc byte[ProtocolConstants.SignatureLength];
        bool signed;
        int written;
        try
        {
            signed = key.TrySignData(input.Bytes, rs, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation, out written);
        }
        catch (CryptographicException)
        {
            throw new ProtocolException(ProtocolError.InvalidKey, "The signer's key cannot sign.");
        }

        if (!signed || written != ProtocolConstants.SignatureLength)
        {
            throw new ProtocolException(ProtocolError.InvalidSignature, "The platform produced a signature of an unexpected length.");
        }

        return ProtocolSignature.Normalize(rs);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            key.Dispose();
        }
    }
}
