using System;
using System.Security.Cryptography;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas;

/// <summary>
/// A persona's private key while it is in memory, held as a platform key object and never as
/// bytes on this assembly's public surface: the only things a caller can get from it are the
/// public key and a signer. Custody code in this assembly (a future protected store or backup
/// codec) reads the private parameters through an internal member and zeroes them after use.
/// A value of this type is always a named-curve P-256 key with a private scalar in range; whether
/// the public point matches the private scalar is not checked here, so a store must validate that
/// before it trusts material it did not generate itself (docs/networking/NETWORK1_PersonaFoundation.md).
/// Disposing it disposes the platform key; what a platform key object leaves in process memory
/// afterwards is the platform's business, not a guarantee made here. Not thread-safe, like the
/// platform key it holds.
/// </summary>
public sealed class PersonaKeyMaterial : IDisposable
{
    // The order of the P-256 base point, big-endian: a private scalar is 1 to n - 1.
    private static ReadOnlySpan<byte> GroupOrder =>
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xBC, 0xE6, 0xFA, 0xAD, 0xA7, 0x17, 0x9E, 0x84, 0xF3, 0xB9, 0xCA, 0xC2, 0xFC, 0x63, 0x25, 0x51,
    ];

    private readonly ECDsa key;
    private bool disposed;

    /// <summary>
    /// Takes ownership of <paramref name="key"/>, which must be a named-curve P-256 key with a private
    /// half. A refused key is disposed before the exception leaves, so a caller never keeps a key
    /// this type would not hold.
    /// </summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidKeyMaterial"/>.</exception>
    public PersonaKeyMaterial(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            PublicKey = Validate(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        this.key = key;
    }

    /// <summary>The public half. Readable after disposal: it is public.</summary>
    public PersonaPublicKey PublicKey { get; }

    /// <summary>A signer over a copy of this key, for the caller to use for one operation and dispose. This material stays usable.</summary>
    /// <exception cref="ObjectDisposedException">After <see cref="Dispose"/>.</exception>
    public EcdsaPersonaSigner CreateSigner()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new EcdsaPersonaSigner(CopyKey());
    }

    /// <summary>Disposes the platform key.</summary>
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            key.Dispose();
        }
    }

    /// <summary>A placeholder: never the identity, never the key.</summary>
    public override string ToString() => "[persona key material]";

    /// <summary>Material over a copy of the key, with a lifetime of its own.</summary>
    internal PersonaKeyMaterial Copy()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new PersonaKeyMaterial(CopyKey());
    }

    /// <summary>
    /// The private parameters, for custody code in this assembly only. The caller zeroes <c>D</c>
    /// as soon as it is done (<see cref="CryptographicOperations.ZeroMemory"/>) and never lets it
    /// reach a log, a Plate model or an unprotected file.
    /// </summary>
    internal ECParameters ExportPrivateParameters()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return key.ExportParameters(includePrivateParameters: true);
    }

    private static PersonaPublicKey Validate(ECDsa key)
    {
        PersonaPublicKey publicKey;
        try
        {
            publicKey = PersonaPublicKey.FromEcdsa(key);
        }
        catch (ProtocolException e)
        {
            throw new PersonaException(PersonaError.InvalidKeyMaterial, "The key is not a named-curve P-256 key.", e);
        }

        ECParameters parameters;
        try
        {
            parameters = key.ExportParameters(includePrivateParameters: true);
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException or NotSupportedException or NotImplementedException or ObjectDisposedException)
        {
            throw new PersonaException(PersonaError.InvalidKeyMaterial, "The key's private half is not available.", e);
        }

        try
        {
            if (parameters.D is not { Length: 32 } scalar || !IsInRange(scalar))
            {
                throw new PersonaException(PersonaError.InvalidKeyMaterial, "The key has no private scalar in the P-256 range.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }

        return publicKey;
    }

    /// <summary>1 to n - 1, as 32 big-endian bytes.</summary>
    private static bool IsInRange(ReadOnlySpan<byte> scalar) =>
        scalar.IndexOfAnyExcept((byte)0) >= 0 && scalar.SequenceCompareTo(GroupOrder) < 0;

    private ECDsa CopyKey()
    {
        var parameters = key.ExportParameters(includePrivateParameters: true);
        try
        {
            return ECDsa.Create(parameters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }
}
