using System;
using System.Globalization;
using System.Numerics;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// A second, independent P-256 implementation in plain arithmetic (affine coordinates, no
/// platform cryptography), written from docs/networking/ProtocolSpecification-v1.md alone. It is
/// far too slow for real use; its job is to prove that the specification and the test vectors are
/// enough to derive the test keys and to verify the signatures without reading the library.
/// </summary>
internal static class ReferenceP256
{
    public static readonly BigInteger P = Parse("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
    public static readonly BigInteger A = P - 3;
    public static readonly BigInteger B = Parse("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B");
    public static readonly BigInteger N = Parse("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
    private static readonly BigInteger Gx = Parse("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296");
    private static readonly BigInteger Gy = Parse("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5");

    private readonly record struct Point(BigInteger X, BigInteger Y, bool Infinity)
    {
        public static readonly Point AtInfinity = new(BigInteger.Zero, BigInteger.Zero, true);
    }

    /// <summary>The public point of a private scalar, as two 32-byte big-endian coordinates.</summary>
    public static (byte[] X, byte[] Y) PublicKey(BigInteger scalar)
    {
        var q = Multiply(scalar, new Point(Gx, Gy, false));
        return (ToBytes32(q.X), ToBytes32(q.Y));
    }

    /// <summary>
    /// ECDSA verification straight from the definition: e is the digest as an integer, w = s^-1
    /// mod n, u1 = e*w, u2 = r*w, R = u1*G + u2*Q, and the signature is valid when R is not the
    /// point at infinity and R.x mod n == r.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey65, ReadOnlySpan<byte> digest, ReadOnlySpan<byte> signature64)
    {
        if (publicKey65.Length != 65 || publicKey65[0] != 0x04 || signature64.Length != 64)
        {
            return false;
        }

        var q = new Point(Unsigned(publicKey65.Slice(1, 32)), Unsigned(publicKey65.Slice(33, 32)), false);
        var r = Unsigned(signature64.Slice(0, 32));
        var s = Unsigned(signature64.Slice(32, 32));
        if (r.IsZero || r >= N || s.IsZero || s >= N)
        {
            return false;
        }

        var e = Unsigned(digest);
        var w = BigInteger.ModPow(s, N - 2, N);
        var u1 = (e * w) % N;
        var u2 = (r * w) % N;
        var point = Add(Multiply(u1, new Point(Gx, Gy, false)), Multiply(u2, q));
        return !point.Infinity && point.X % N == r;
    }

    /// <summary>True when (x, y) satisfies y^2 = x^3 + ax + b with both coordinates in [0, p), straight from the specification.</summary>
    public static bool IsOnCurve(BigInteger x, BigInteger y) =>
        x.Sign >= 0 && y.Sign >= 0 && x < P && y < P && Mod((y * y) - (x * x * x) - (A * x) - B).IsZero;

    /// <summary>True when the 65 bytes are an uncompressed encoding of a point on the curve (section 3 of the specification).</summary>
    public static bool IsOnCurve(ReadOnlySpan<byte> publicKey65) =>
        publicKey65.Length == 65 && publicKey65[0] == 0x04 && IsOnCurve(Unsigned(publicKey65.Slice(1, 32)), Unsigned(publicKey65.Slice(33, 32)));

    /// <summary>
    /// The y with y^2 = x^3 + ax + b for <paramref name="x"/>, or null when there is none. Because
    /// p = 3 (mod 4), a square root is a^((p+1)/4); the other root is p - y.
    /// </summary>
    public static BigInteger? LiftX(BigInteger x)
    {
        var rhs = Mod((x * x * x) + (A * x) + B);
        var y = BigInteger.ModPow(rhs, (P + 1) / 4, P);
        return Mod(y * y) == rhs ? y : null;
    }

    public static byte[] ToBytes32(BigInteger value)
    {
        var bytes = new byte[32];
        var count = value.GetByteCount(isUnsigned: true);
        value.TryWriteBytes(bytes.AsSpan(32 - count), out _, isUnsigned: true, isBigEndian: true);
        return bytes;
    }

    private static BigInteger Unsigned(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

    private static BigInteger Mod(BigInteger value)
    {
        value %= P;
        return value.Sign < 0 ? value + P : value;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static Point Double(Point p)
    {
        if (p.Infinity || p.Y.IsZero)
        {
            return Point.AtInfinity;
        }

        var lambda = Mod(((3 * p.X * p.X) + A) * Inverse(2 * p.Y));
        var x = Mod((lambda * lambda) - (2 * p.X));
        var y = Mod((lambda * (p.X - x)) - p.Y);
        return new Point(x, y, false);
    }

    private static Point Add(Point p, Point q)
    {
        if (p.Infinity)
        {
            return q;
        }

        if (q.Infinity)
        {
            return p;
        }

        if (p.X == q.X)
        {
            return Mod(p.Y + q.Y).IsZero ? Point.AtInfinity : Double(p);
        }

        var lambda = Mod((q.Y - p.Y) * Inverse(q.X - p.X));
        var x = Mod((lambda * lambda) - p.X - q.X);
        var y = Mod((lambda * (p.X - x)) - p.Y);
        return new Point(x, y, false);
    }

    private static Point Multiply(BigInteger k, Point p)
    {
        var result = Point.AtInfinity;
        var addend = p;
        for (var bit = 0; bit < 256; bit++)
        {
            if (!((k >> bit) & BigInteger.One).IsZero)
            {
                result = Add(result, addend);
            }

            addend = Double(addend);
        }

        return result;
    }

    private static BigInteger Parse(string hex) => BigInteger.Parse("0" + hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
}
