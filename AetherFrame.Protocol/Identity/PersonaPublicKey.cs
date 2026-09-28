using System;
using System.Security.Cryptography;
using AetherFrame.Protocol.Encoding;

namespace AetherFrame.Protocol.Identity;

/// <summary>
/// A persona's public key: an uncompressed NIST P-256 point, 0x04 followed by X and Y as 32
/// big-endian bytes each (docs/networking/ProtocolSpecification-v1.md, "Public key"). A value of
/// this type is always a point on the curve; the check is the protocol's own, so it behaves the same
/// on every platform. Immutable, and never carries private material.
/// </summary>
public sealed class PersonaPublicKey : IEquatable<PersonaPublicKey>
{
    private readonly byte[] bytes;

    private PersonaPublicKey(byte[] bytes)
    {
        this.bytes = bytes;
        Id = PersonaId.Derive(bytes);
    }

    /// <summary>The persona identity derived from this key.</summary>
    public PersonaId Id { get; }

    /// <summary>The 65 bytes of the uncompressed point.</summary>
    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>
    /// Reads the wire form, refusing anything that is not an uncompressed point on P-256. The bytes
    /// are copied before they are checked, so the key this returns holds exactly the bytes that
    /// passed, whatever happens to the caller's buffer meanwhile.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidKey"/>.</exception>
    public static PersonaPublicKey FromBytes(ReadOnlySpan<byte> uncompressedPoint)
    {
        if (uncompressedPoint.Length != ProtocolConstants.PublicKeyLength)
        {
            throw new ProtocolException(ProtocolError.InvalidKey, $"A public key is {ProtocolText.Number(ProtocolConstants.PublicKeyLength)} bytes; this one is {ProtocolText.Number(uncompressedPoint.Length)}.");
        }

        var copy = uncompressedPoint.ToArray();
        if (copy[0] != 0x04)
        {
            throw new ProtocolException(ProtocolError.InvalidKey, "A public key must be an uncompressed point (0x04 prefix).");
        }

        if (!P256Curve.IsOnCurve(copy.AsSpan(1, P256Curve.FieldBytes), copy.AsSpan(1 + P256Curve.FieldBytes, P256Curve.FieldBytes)))
        {
            throw new ProtocolException(ProtocolError.InvalidKey, "The public key is not a point on P-256.");
        }

        return new PersonaPublicKey(copy);
    }

    /// <summary>Takes the public half of a platform key, which must be a named-curve P-256 key.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidKey"/>.</exception>
    public static PersonaPublicKey FromEcdsa(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Whatever the platform (or an ECDsa subclass) uses to refuse the export, the caller sees an
        // invalid key, as for every other platform refusal in this type.
        ECParameters parameters;
        try
        {
            parameters = key.ExportParameters(includePrivateParameters: false);
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException or NotSupportedException or ObjectDisposedException)
        {
            throw new ProtocolException(ProtocolError.InvalidKey, "The key's public parameters could not be exported.");
        }

        // The curve is checked by its identifier first (a 256-bit key on another curve has the right
        // coordinate lengths), and the point against the P-256 equation afterwards in FromBytes.
        if (!P256Curve.IsP256(parameters.Curve)
            || parameters.Q.X is not { Length: P256Curve.FieldBytes } x
            || parameters.Q.Y is not { Length: P256Curve.FieldBytes } y)
        {
            throw new ProtocolException(ProtocolError.InvalidKey, "The key is not a named-curve P-256 key.");
        }

        Span<byte> point = stackalloc byte[ProtocolConstants.PublicKeyLength];
        point[0] = 0x04;
        x.CopyTo(point.Slice(1));
        y.CopyTo(point.Slice(1 + P256Curve.FieldBytes));
        return FromBytes(point);
    }

    /// <summary>A copy of the 65 bytes.</summary>
    public byte[] ToArray() => (byte[])bytes.Clone();

    /// <summary>
    /// A platform key holding only this public point, for verification. The caller disposes it. The
    /// point already passed the protocol's own on-curve check, so the platform refusing it is a
    /// disagreement between the two and is reported as an invalid key, whatever exception the
    /// platform uses: Windows CNG reports an invalid point as <see cref="PlatformNotSupportedException"/>
    /// ("the curve or its parameters are not valid"), OpenSSL as <see cref="CryptographicException"/>.
    /// </summary>
    internal ECDsa CreateEcdsa()
    {
        try
        {
            return ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = bytes.AsSpan(1, P256Curve.FieldBytes).ToArray(),
                    Y = bytes.AsSpan(1 + P256Curve.FieldBytes, P256Curve.FieldBytes).ToArray(),
                },
            });
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException or ArgumentException)
        {
            throw new ProtocolException(ProtocolError.InvalidKey, "The platform refused the public key.");
        }
    }

    /// <inheritdoc />
    public bool Equals(PersonaPublicKey? other) => other is not null && CryptographicOperations.FixedTimeEquals(bytes, other.bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PersonaPublicKey);

    /// <inheritdoc />
    public override int GetHashCode() => Id.GetHashCode();

    /// <summary>The persona identity, never the key bytes.</summary>
    public override string ToString() => Id.ToString();
}
