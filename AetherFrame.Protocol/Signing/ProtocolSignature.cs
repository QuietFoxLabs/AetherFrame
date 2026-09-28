using System;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Signing;

/// <summary>
/// An ECDSA P-256 signature in its one canonical form: r then s, 32 big-endian bytes each, both in
/// [1, n-1], and s in the low half (s &lt;= floor(n/2)), so that a given signature has exactly one
/// byte representation and no third party can produce a second valid encoding of it
/// (docs/networking/ProtocolSpecification-v1.md, "Signature"). Immutable.
/// </summary>
public sealed class ProtocolSignature
{
    private readonly byte[] bytes;

    private ProtocolSignature(byte[] bytes)
    {
        this.bytes = bytes;
    }

    /// <summary>The 64 bytes: r then s.</summary>
    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>
    /// Reads the wire form, refusing anything that is not a canonical signature. The bytes are
    /// copied before they are checked, so the signature this returns holds exactly the bytes that
    /// passed, whatever happens to the caller's buffer meanwhile.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidSignature"/>.</exception>
    public static ProtocolSignature FromBytes(ReadOnlySpan<byte> rs)
    {
        if (rs.Length != ProtocolConstants.SignatureLength)
        {
            throw new ProtocolException(ProtocolError.InvalidSignature, $"A signature is {ProtocolText.Number(ProtocolConstants.SignatureLength)} bytes; this one is {ProtocolText.Number(rs.Length)}.");
        }

        var copy = rs.ToArray();
        var r = P256Curve.ToUnsigned(copy.AsSpan(0, P256Curve.FieldBytes));
        var s = P256Curve.ToUnsigned(copy.AsSpan(P256Curve.FieldBytes));
        if (r.IsZero || r >= P256Curve.N)
        {
            throw new ProtocolException(ProtocolError.InvalidSignature, "The signature's r is out of range.");
        }

        if (s.IsZero || s >= P256Curve.N)
        {
            throw new ProtocolException(ProtocolError.InvalidSignature, "The signature's s is out of range.");
        }

        if (s > P256Curve.HalfN)
        {
            throw new ProtocolException(ProtocolError.InvalidSignature, "The signature's s is not in the low half, so the signature is not canonical.");
        }

        return new ProtocolSignature(copy);
    }

    /// <summary>
    /// Brings a signature the platform produced into canonical form: an s in the high half is
    /// replaced by n - s, which is the other valid signature over the same input with the same r.
    /// </summary>
    internal static ProtocolSignature Normalize(ReadOnlySpan<byte> rs)
    {
        if (rs.Length != ProtocolConstants.SignatureLength)
        {
            throw new ProtocolException(ProtocolError.InvalidSignature, "The platform produced a signature of an unexpected length.");
        }

        Span<byte> normalized = stackalloc byte[ProtocolConstants.SignatureLength];
        rs.CopyTo(normalized);
        var s = P256Curve.ToUnsigned(normalized.Slice(P256Curve.FieldBytes));
        if (s > P256Curve.HalfN)
        {
            P256Curve.WriteFixed32(P256Curve.N - s, normalized.Slice(P256Curve.FieldBytes));
        }

        return FromBytes(normalized);
    }

    /// <summary>A copy of the 64 bytes.</summary>
    public byte[] ToArray() => (byte[])bytes.Clone();
}
