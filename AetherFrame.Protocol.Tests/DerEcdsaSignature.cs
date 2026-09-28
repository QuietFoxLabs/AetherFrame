using System;
using System.Numerics;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// A strict reader of the DER form of an ECDSA signature (RFC 3279 ECDSA-Sig-Value: SEQUENCE of two
/// INTEGERs, X.690 distinguished encoding), for checking what a platform or a third party produced
/// without assuming anything about its length. A DER signature over P-256 is between 8 bytes (r = s
/// = 1) and 72 bytes (both integers need a leading zero), and every length in between is legal; each
/// integer is exactly as long as its value needs, so the total length follows from r and s.
/// </summary>
internal static class DerEcdsaSignature
{
    public const int MinLength = 8;
    public const int MaxLength = 72;

    /// <summary>Parses <paramref name="der"/> or explains why it is not a DER P-256 signature.</summary>
    public static bool TryParse(ReadOnlySpan<byte> der, out BigInteger r, out BigInteger s, out string? error)
    {
        r = default;
        s = default;
        error = null;
        if (der.Length < MinLength || der.Length > MaxLength)
        {
            error = $"length {der.Length} is outside {MinLength}..{MaxLength}";
            return false;
        }

        if (der[0] != 0x30)
        {
            error = "not a SEQUENCE";
            return false;
        }

        // Short-form length only: a P-256 signature never needs more than 70 content bytes.
        if (der[1] != der.Length - 2)
        {
            error = "the SEQUENCE length is not the remaining length in short form";
            return false;
        }

        var offset = 2;
        if (!TryReadInteger(der, ref offset, out r, out error) || !TryReadInteger(der, ref offset, out s, out error))
        {
            return false;
        }

        if (offset != der.Length)
        {
            error = "bytes after the second INTEGER";
            return false;
        }

        if (r.IsZero || r >= ReferenceP256.N || s.IsZero || s >= ReferenceP256.N)
        {
            error = "r or s outside 1..n-1";
            return false;
        }

        return true;
    }

    /// <summary>Parses or fails the test.</summary>
    public static (BigInteger R, BigInteger S) Parse(ReadOnlySpan<byte> der)
    {
        if (!TryParse(der, out var r, out var s, out var error))
        {
            throw new Xunit.Sdk.XunitException("not a DER P-256 signature: " + error);
        }

        return (r, s);
    }

    /// <summary>The length DER gives a non-negative integer: its minimal big-endian bytes, plus a zero byte when the top bit is set.</summary>
    public static int IntegerLength(BigInteger value)
    {
        var bytes = value.GetByteCount(isUnsigned: true);
        return value.IsZero ? 1 : (value >> ((bytes - 1) * 8)) >= 0x80 ? bytes + 1 : bytes;
    }

    /// <summary>The length of the DER signature for (r, s): 6 bytes of tags and lengths plus the two integers.</summary>
    public static int Length(BigInteger r, BigInteger s) => 6 + IntegerLength(r) + IntegerLength(s);

    /// <summary>Encodes (r, s) as DER, for the negative tests; values are not range-checked.</summary>
    public static byte[] Encode(BigInteger r, BigInteger s)
    {
        var body = new byte[IntegerLength(r) + IntegerLength(s) + 4];
        var offset = 0;
        WriteInteger(body, ref offset, r);
        WriteInteger(body, ref offset, s);
        return [0x30, (byte)body.Length, .. body];
    }

    private static bool TryReadInteger(ReadOnlySpan<byte> der, ref int offset, out BigInteger value, out string? error)
    {
        value = default;
        error = null;
        if (offset + 2 > der.Length || der[offset] != 0x02)
        {
            error = "expected an INTEGER tag";
            return false;
        }

        int length = der[offset + 1];
        if (length < 1 || length > 33 || offset + 2 + length > der.Length)
        {
            error = "INTEGER length is not 1..33 or runs past the end";
            return false;
        }

        var content = der.Slice(offset + 2, length);
        if ((content[0] & 0x80) != 0)
        {
            error = "INTEGER is negative";
            return false;
        }

        if (content[0] == 0x00 && (length == 1 || (content[1] & 0x80) == 0))
        {
            error = length == 1 ? "INTEGER is zero" : "INTEGER has a leading zero it does not need";
            return false;
        }

        value = new BigInteger(content, isUnsigned: true, isBigEndian: true);
        offset += 2 + length;
        return true;
    }

    private static void WriteInteger(byte[] target, ref int offset, BigInteger value)
    {
        var length = IntegerLength(value);
        target[offset++] = 0x02;
        target[offset++] = (byte)length;
        var count = value.IsZero ? 1 : value.GetByteCount(isUnsigned: true);
        if (length > count)
        {
            target[offset++] = 0x00;
        }

        value.TryWriteBytes(target.AsSpan(offset, count), out _, isUnsigned: true, isBigEndian: true);
        offset += count;
    }
}
