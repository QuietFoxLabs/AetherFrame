using System;
using System.Globalization;
using System.Numerics;

namespace AetherFrame.Protocol.Identity;

/// <summary>
/// The NIST P-256 (secp256r1) parameters the protocol checks for itself, independently of the
/// platform's cryptography: whether a public key is a point on the curve, and whether a signature's
/// r and s are in range and s is in the low half. The platform still does every signature
/// computation; these checks make the protocol refuse bad material the same way on every platform.
/// </summary>
internal static class P256Curve
{
    /// <summary>The bytes of one field element or scalar.</summary>
    public const int FieldBytes = 32;

    /// <summary>The curve's object identifier (prime256v1 / secp256r1 / nistP256).</summary>
    public const string Oid = "1.2.840.10045.3.1.7";

    /// <summary>The prime of the field.</summary>
    public static readonly BigInteger P = Parse("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");

    /// <summary>The curve equation's b (a is -3).</summary>
    public static readonly BigInteger B = Parse("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B");

    /// <summary>The order of the base point (and of the whole group: the cofactor is 1).</summary>
    public static readonly BigInteger N = Parse("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");

    /// <summary>floor(N / 2): a signature's s must not exceed this (docs/networking/ProtocolSpecification-v1.md, "Signature").</summary>
    public static readonly BigInteger HalfN = N >> 1;

    /// <summary>
    /// True when a platform curve is P-256 by its identifier: the OID, or one of the names platforms
    /// give it when they report no OID value. A point on it is still checked against the curve
    /// equation afterwards; this only stops a key on another 256-bit curve from getting that far.
    /// </summary>
    public static bool IsP256(System.Security.Cryptography.ECCurve curve)
    {
        if (!curve.IsNamed || curve.Oid is not { } oid)
        {
            return false;
        }

        if (oid.Value is { } value)
        {
            return value == Oid;
        }

        return oid.FriendlyName is "nistP256" or "ECDSA_P256" or "prime256v1" or "secp256r1" or "P-256";
    }

    /// <summary>Reads a big-endian unsigned integer.</summary>
    public static BigInteger ToUnsigned(ReadOnlySpan<byte> bigEndian) => new(bigEndian, isUnsigned: true, isBigEndian: true);

    /// <summary>Writes <paramref name="value"/> (which must be below 2^256) as 32 big-endian bytes.</summary>
    public static void WriteFixed32(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        var count = value.GetByteCount(isUnsigned: true);
        if (!value.TryWriteBytes(destination.Slice(FieldBytes - count), out _, isUnsigned: true, isBigEndian: true))
        {
            throw new InvalidOperationException("A field element did not fit 32 bytes.");
        }
    }

    /// <summary>True when (x, y) satisfies y^2 = x^3 - 3x + b over the field, with both coordinates below the prime.</summary>
    public static bool IsOnCurve(ReadOnlySpan<byte> x32, ReadOnlySpan<byte> y32)
    {
        var x = ToUnsigned(x32);
        var y = ToUnsigned(y32);
        if (x >= P || y >= P)
        {
            return false;
        }

        var left = BigInteger.ModPow(y, 2, P);
        var right = (BigInteger.ModPow(x, 3, P) - (3 * x) + B) % P;
        if (right.Sign < 0)
        {
            right += P;
        }

        return left == right;
    }

    private static BigInteger Parse(string hex) => BigInteger.Parse("0" + hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
}
