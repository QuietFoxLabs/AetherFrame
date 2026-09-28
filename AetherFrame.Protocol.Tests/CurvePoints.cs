using System;
using System.Numerics;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// P-256 points with a coordinate small enough that coordinate + p still fits 32 bytes. Each such
/// point has a second, non-reduced encoding that satisfies the curve equation modulo p; a reader
/// that did not require X &lt; p and Y &lt; p (specification, section 3) would accept it and derive a
/// second identity for the same key. Both points are re-checked against the curve equation here,
/// so the constants cannot silently be wrong.
/// </summary>
internal static class CurvePoints
{
    /// <summary>
    /// A small x with a point on P-256; y is derived from the equation. x = 0 is on the curve too (b
    /// is a square), x = 1 to 4 are not; 5 is used so that the coordinate is plainly non-zero.
    /// </summary>
    public static readonly BigInteger SmallX = 5;

    public static readonly BigInteger SmallXY = ReferenceP256.LiftX(SmallX) ?? throw new InvalidOperationException("x = 5 is on P-256");

    /// <summary>
    /// A point with y = 1. Its x is a root of x^3 - 3x + (b - 1) over the field, found offline with a
    /// Cantor-Zassenhaus root finder during the 2026-09-28 review (docs/networking/NETWORK0_HANDOFF.md,
    /// "Remediation"); a y this small cannot be found by searching x.
    /// </summary>
    public static readonly BigInteger SmallYX = ReferenceP256.Parse("09E78D4EF60D05F750F6636209092BC43CBDD6B47E11A9DE20A9FEB2A50BB96C");

    public static readonly BigInteger SmallY = BigInteger.One;

    /// <summary>The uncompressed encoding 0x04 ‖ X ‖ Y; a coordinate above 2^256 does not fit and throws.</summary>
    public static byte[] Encode(BigInteger x, BigInteger y) => [0x04, .. ReferenceP256.ToBytes32(x), .. ReferenceP256.ToBytes32(y)];

    public static byte[] SmallXKey => Encode(SmallX, SmallXY);

    /// <summary>The same point as <see cref="SmallXKey"/> with X written as x + p.</summary>
    public static byte[] SmallXKeyNonReduced => Encode(SmallX + ReferenceP256.P, SmallXY);

    public static byte[] SmallYKey => Encode(SmallYX, SmallY);

    /// <summary>The same point as <see cref="SmallYKey"/> with Y written as y + p.</summary>
    public static byte[] SmallYKeyNonReduced => Encode(SmallYX, SmallY + ReferenceP256.P);
}
