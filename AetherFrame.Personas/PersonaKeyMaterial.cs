using System;
using System.Security.Cryptography;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas;

/// <summary>
/// A persona's private key while it is in memory, held as a platform key object and never as
/// bytes on this assembly's public surface: the only things a caller can get from it are the
/// public key and a signer. Only custody code in this assembly (a key store or backup codec) makes
/// material or reads the private parameters, through internal members, and zeroes them after use.
/// <para>
/// A value of this type is always a named-curve P-256 key pair that was checked before it was
/// accepted, in this order: the private scalar is exactly 32 big-endian bytes from 1 to n - 1,
/// checked here before the platform sees it; the platform then derives the public point from the
/// scalar alone; and that point must equal the public key recorded with the scalar, and the scalar
/// the platform holds must equal the one given. So a key whose scalar and point disagree, or whose
/// scalar a platform would silently reduce or pad, is refused on every platform, whatever the
/// platform itself checks at import (docs/networking/NETWORK1_PersonaFoundation.md, section 4).
/// </para>
/// <para>
/// The platform key it holds is one it created itself and shares with nobody, so nothing outside
/// can change the key after it was checked. Disposing it disposes that platform key; what a
/// platform key object leaves in process memory afterwards is the platform's business, not a
/// guarantee made here. Not thread-safe, like the platform key it holds.
/// </para>
/// </summary>
public sealed class PersonaKeyMaterial : IDisposable
{
    /// <summary>The bytes of a P-256 private scalar.</summary>
    internal const int ScalarLength = 32;

    // The order of the P-256 base point, big-endian: a private scalar is 1 to n - 1.
    private static ReadOnlySpan<byte> GroupOrder =>
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xBC, 0xE6, 0xFA, 0xAD, 0xA7, 0x17, 0x9E, 0x84, 0xF3, 0xB9, 0xCA, 0xC2, 0xFC, 0x63, 0x25, 0x51,
    ];

    private readonly ECDsa key;
    private bool disposed;

    private PersonaKeyMaterial(ECDsa key, PersonaPublicKey publicKey)
    {
        this.key = key;
        PublicKey = publicKey;
    }

    /// <summary>The public half. Readable after disposal: it is public.</summary>
    public PersonaPublicKey PublicKey { get; }

    /// <summary>A signer over a copy of this key, for the caller to use for one operation and dispose. This material stays usable.</summary>
    /// <exception cref="ObjectDisposedException">After <see cref="Dispose"/>.</exception>
    public EcdsaPersonaSigner CreateSigner()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var copy = CopyKey();
        EcdsaPersonaSigner signer;
        try
        {
            signer = new EcdsaPersonaSigner(copy);
        }
        catch
        {
            copy.Dispose();
            throw;
        }

        if (!signer.PublicKey.Equals(PublicKey))
        {
            signer.Dispose();
            throw Invalid("The signer's key does not match the material it was copied from.");
        }

        return signer;
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

    /// <summary>A fresh random P-256 key, for a key store to hand out.</summary>
    internal static PersonaKeyMaterial Generate()
    {
        using var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return FromEcdsa(fresh);
    }

    /// <summary>
    /// Material for the private scalar <paramref name="privateScalar"/> and the public key recorded
    /// with it, for custody code that read both from its own storage or from a backup. The scalar is
    /// checked in managed code before any platform import; the platform then derives the public point
    /// from the scalar alone, and the result must be <paramref name="publicKey"/> exactly. The caller
    /// keeps and zeroes its own <paramref name="privateScalar"/>; this makes and zeroes its own copy.
    /// </summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidKeyMaterial"/>.</exception>
    internal static PersonaKeyMaterial Import(ReadOnlySpan<byte> privateScalar, PersonaPublicKey publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return new PersonaKeyMaterial(CreateCheckedKey(privateScalar, publicKey), publicKey);
    }

    /// <summary>
    /// Material holding a checked copy of <paramref name="key"/>, which stays the caller's: it is
    /// read once, never retained and never disposed here, so whatever the caller does with it
    /// afterwards (import another key into it, generate a new one, dispose it) cannot reach the
    /// material. The copy goes through every check of <see cref="Import"/>.
    /// </summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidKeyMaterial"/>.</exception>
    internal static PersonaKeyMaterial FromEcdsa(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            // The curve is checked by the protocol's own rule before anything private is read.
            PersonaPublicKey.FromEcdsa(key);
        }
        catch (ProtocolException e)
        {
            throw Invalid("The key is not a named-curve P-256 key.", e);
        }

        ECParameters parameters;
        try
        {
            parameters = key.ExportParameters(includePrivateParameters: true);
        }
        catch (Exception e) when (IsPlatformRefusal(e))
        {
            throw Invalid("The key's private half is not available.", e);
        }

        try
        {
            if (parameters.D is null)
            {
                throw Invalid("The key has no private scalar.");
            }

            // D and Q come from one export, so they are one snapshot of the caller's key.
            PersonaPublicKey recorded;
            try
            {
                recorded = PersonaPublicKey.FromBytes(UncompressedPoint(parameters.Q));
            }
            catch (ProtocolException e)
            {
                throw Invalid("The key's public point is not a point on P-256.", e);
            }

            return Import(parameters.D, recorded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }

    /// <summary>
    /// True when <paramref name="scalar"/> is a canonical P-256 private scalar: exactly 32 big-endian
    /// bytes, not zero, and below the group order. Decided in managed code in time that does not
    /// depend on the value, so no platform's import rules are involved.
    /// </summary>
    internal static bool IsCanonicalScalar(ReadOnlySpan<byte> scalar)
    {
        if (scalar.Length != ScalarLength)
        {
            return false;
        }

        var order = GroupOrder;
        var borrow = 0;
        var any = 0;
        for (var index = ScalarLength - 1; index >= 0; index--)
        {
            var difference = scalar[index] - order[index] - borrow;
            borrow = (difference >> 8) & 1;
            any |= scalar[index];
        }

        // A borrow out of the top byte means scalar - n is negative; (any - 1) >> 31 is -1 only for zero.
        var zero = ((any - 1) >> 31) & 1;
        return (borrow & (zero ^ 1)) == 1;
    }

    /// <summary>Material over a copy of the key, with a lifetime of its own, checked again as it is made.</summary>
    internal PersonaKeyMaterial Copy()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new PersonaKeyMaterial(CopyKey(), PublicKey);
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

    private static ECDsa CreateCheckedKey(ReadOnlySpan<byte> privateScalar, PersonaPublicKey publicKey)
    {
        // 1. The scalar's range, before the platform sees it. Platforms differ here: some refuse an
        //    out-of-range or short scalar, some pad a short one, one reduces a large one modulo n.
        if (!IsCanonicalScalar(privateScalar))
        {
            throw Invalid("The private scalar is not 32 big-endian bytes from 1 to n - 1.");
        }

        var scalar = privateScalar.ToArray();
        ECDsa? key = null;
        try
        {
            // 2. The platform derives the public point from the scalar alone: no point is given, so
            //    no platform can accept a pair by trusting a point it was handed.
            try
            {
                key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = scalar });
            }
            catch (Exception e) when (IsPlatformRefusal(e))
            {
                throw Invalid("The platform could not derive a public key from the private scalar.", e);
            }

            // 3. The derived point must be the recorded one. PersonaPublicKey also checks the point is
            //    on the curve in managed code, and compares in fixed time.
            PersonaPublicKey derived;
            try
            {
                derived = PersonaPublicKey.FromEcdsa(key);
            }
            catch (ProtocolException e)
            {
                throw Invalid("The platform derived no valid public key from the private scalar.", e);
            }

            if (!derived.Equals(publicKey))
            {
                throw Invalid("The private scalar does not belong to the recorded public key.");
            }

            // 4. The platform holds exactly the scalar that was checked.
            ECParameters held;
            try
            {
                held = key.ExportParameters(includePrivateParameters: true);
            }
            catch (Exception e) when (IsPlatformRefusal(e))
            {
                throw Invalid("The platform key's private half is not available.", e);
            }

            try
            {
                if (held.D is not { Length: ScalarLength } heldScalar || !CryptographicOperations.FixedTimeEquals(heldScalar, scalar))
                {
                    throw Invalid("The platform holds a different private scalar than the one given.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(held.D);
            }

            var accepted = key;
            key = null;
            return accepted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
            key?.Dispose();
        }
    }

    private static byte[] UncompressedPoint(ECPoint point)
    {
        if (point.X is not { Length: ScalarLength } x || point.Y is not { Length: ScalarLength } y)
        {
            throw Invalid("The key's public point is not a P-256 point.");
        }

        var bytes = new byte[1 + (2 * ScalarLength)];
        bytes[0] = 0x04;
        x.CopyTo(bytes, 1);
        y.CopyTo(bytes, 1 + ScalarLength);
        return bytes;
    }

    private static bool IsPlatformRefusal(Exception e) =>
        e is CryptographicException or PlatformNotSupportedException or NotSupportedException or NotImplementedException or ArgumentException or ObjectDisposedException;

    private static PersonaException Invalid(string message) => new(PersonaError.InvalidKeyMaterial, message);

    private static PersonaException Invalid(string message, Exception inner) => new(PersonaError.InvalidKeyMaterial, message, inner);

    private ECDsa CopyKey()
    {
        var parameters = key.ExportParameters(includePrivateParameters: true);
        try
        {
            return CreateCheckedKey(parameters.D, PublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }
}
