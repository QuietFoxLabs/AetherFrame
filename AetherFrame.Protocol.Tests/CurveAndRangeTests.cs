using System;
using System.Numerics;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;
using Xunit;
using static AetherFrame.Protocol.Tests.DocumentMutations;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// The field-range rules of the specification, each hit with an input that passes every other rule:
/// coordinates at or above p that satisfy the curve equation modulo p, and s exactly at the low-S
/// boundary. Checked against the reference implementation, never only against the library.
/// </summary>
public class CurveAndRangeTests
{
    private static readonly BigInteger HalfN = ReferenceP256.Parse("7FFFFFFF800000007FFFFFFFFFFFFFFFDE737D56D38BCF4279DCE5617E3192A8");

    [Fact]
    public void ThePublishedConstants_AreConsistent()
    {
        // floor(n / 2) as the specification prints it, and n = 2 * floor(n / 2) + 1 since n is odd.
        Assert.Equal(HalfN, ReferenceP256.N >> 1);
        Assert.Equal(HalfN, P256Curve.HalfN);
        Assert.Equal(ReferenceP256.N, (HalfN * 2) + 1);
        Assert.Equal(ReferenceP256.P, P256Curve.P);
        Assert.Equal(ReferenceP256.B, P256Curve.B);
        Assert.True(ReferenceP256.IsOnCurve(CurvePoints.SmallXKey));
        Assert.True(ReferenceP256.IsOnCurve(CurvePoints.SmallYKey));
    }

    [Fact]
    public void NonReducedX_IsTheSamePointInAnotherEncoding_AndIsRefused()
    {
        var reduced = PersonaPublicKey.FromBytes(CurvePoints.SmallXKey);
        Assert.Equal((CurvePoints.SmallX + ReferenceP256.P) % ReferenceP256.P, CurvePoints.SmallX);
        Assert.NotEqual(Hex.Of(CurvePoints.SmallXKey), Hex.Of(CurvePoints.SmallXKeyNonReduced));

        // Accepting the non-reduced form would give the same point a second persona identity.
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => PersonaPublicKey.FromBytes(CurvePoints.SmallXKeyNonReduced));
        Assert.False(ReferenceP256.IsOnCurve(CurvePoints.SmallXKeyNonReduced));
        Assert.NotEqual(reduced.Id, PersonaId.Parse(ReferenceProtocol.PersonaId(CurvePoints.SmallXKeyNonReduced)));

        using var signer = TestPersonas.CreateA();
        var document = Substitute(Samples.SignedSnapshot(signer), Layout.Key, CurvePoints.SmallXKeyNonReduced);
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => SignedDocumentCodec.Verify(document));
    }

    [Fact]
    public void NonReducedY_IsTheSamePointInAnotherEncoding_AndIsRefused()
    {
        var reduced = PersonaPublicKey.FromBytes(CurvePoints.SmallYKey);
        Assert.Equal((CurvePoints.SmallY + ReferenceP256.P) % ReferenceP256.P, CurvePoints.SmallY);

        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => PersonaPublicKey.FromBytes(CurvePoints.SmallYKeyNonReduced));
        Assert.False(ReferenceP256.IsOnCurve(CurvePoints.SmallYKeyNonReduced));
        Assert.NotEqual(reduced.Id, PersonaId.Parse(ReferenceProtocol.PersonaId(CurvePoints.SmallYKeyNonReduced)));

        using var signer = TestPersonas.CreateA();
        var document = Substitute(Samples.SignedSnapshot(signer), Layout.Key, CurvePoints.SmallYKeyNonReduced);
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => SignedDocumentCodec.Verify(document));
    }

    [Fact]
    public void IsOnCurve_RefusesEachCoordinateAtOrAbovePSeparately()
    {
        // Each half of the range check, hit with a value the curve equation alone would accept.
        var x = ReferenceP256.ToBytes32(CurvePoints.SmallX);
        var xy = ReferenceP256.ToBytes32(CurvePoints.SmallXY);
        var yx = ReferenceP256.ToBytes32(CurvePoints.SmallYX);
        var y = ReferenceP256.ToBytes32(CurvePoints.SmallY);
        var p = ReferenceP256.ToBytes32(ReferenceP256.P);

        Assert.True(P256Curve.IsOnCurve(x, xy));
        Assert.True(P256Curve.IsOnCurve(yx, y));
        Assert.False(P256Curve.IsOnCurve(ReferenceP256.ToBytes32(CurvePoints.SmallX + ReferenceP256.P), xy));
        Assert.False(P256Curve.IsOnCurve(yx, ReferenceP256.ToBytes32(CurvePoints.SmallY + ReferenceP256.P)));
        Assert.False(P256Curve.IsOnCurve(p, xy));
        Assert.False(P256Curve.IsOnCurve(yx, p));
        Assert.False(P256Curve.IsOnCurve(new byte[32], new byte[32]));
    }

    [Fact]
    public void IsOnCurve_AgreesWithTheReference_OnRandomPointsAndTheirNeighbours()
    {
        var random = new Random(256);
        var found = 0;
        for (var round = 0; round < 64; round++)
        {
            var xBytes = new byte[32];
            random.NextBytes(xBytes);
            var x = new BigInteger(xBytes, isUnsigned: true, isBigEndian: true) % ReferenceP256.P;
            if (ReferenceP256.LiftX(x) is not { } y)
            {
                Assert.False(P256Curve.IsOnCurve(ReferenceP256.ToBytes32(x), ReferenceP256.ToBytes32(x)));
                continue;
            }

            found++;
            foreach (var candidate in new[] { y, ReferenceP256.P - y, (y + 1) % ReferenceP256.P, (y + ReferenceP256.P - 1) % ReferenceP256.P })
            {
                Assert.Equal(ReferenceP256.IsOnCurve(x, candidate), P256Curve.IsOnCurve(ReferenceP256.ToBytes32(x), ReferenceP256.ToBytes32(candidate)));
            }

            Assert.True(P256Curve.IsOnCurve(ReferenceP256.ToBytes32(x), ReferenceP256.ToBytes32(y)));
            Assert.True(P256Curve.IsOnCurve(ReferenceP256.ToBytes32(x), ReferenceP256.ToBytes32(ReferenceP256.P - y)));
        }

        Assert.True(found > 16, "about half of all x have a point");
    }

    [Fact]
    public void LowS_BoundaryIsExact()
    {
        using var signer = TestPersonas.CreateA();
        var input = SigningInput.Create(DocumentType.ProfileRetraction, signer.PublicKey, Samples.Retraction().EncodePayload());
        var r = signer.Sign(input).ToArray().AsSpan(0, 32).ToArray();

        // s = floor(n/2) is the largest canonical s: well formed (and, with this r, a mismatch, not a refusal).
        var atBoundary = ProtocolSignature.FromBytes([.. r, .. ReferenceP256.ToBytes32(HalfN)]);
        Assert.False(SignatureVerifier.Verify(input, atBoundary));

        // s = floor(n/2) + 1 is the smallest high s: refused as non-canonical.
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.FromBytes([.. r, .. ReferenceP256.ToBytes32(HalfN + 1)]));

        // Normalization maps floor(n/2) + 1 to n - (floor(n/2) + 1) = floor(n/2), leaves floor(n/2) alone, and maps n - 1 to 1.
        Assert.Equal(ReferenceP256.ToBytes32(HalfN), ProtocolSignature.Normalize([.. r, .. ReferenceP256.ToBytes32(HalfN + 1)]).ToArray()[32..]);
        Assert.Equal(ReferenceP256.ToBytes32(HalfN), ProtocolSignature.Normalize([.. r, .. ReferenceP256.ToBytes32(HalfN)]).ToArray()[32..]);
        Assert.Equal(ReferenceP256.ToBytes32(BigInteger.One), ProtocolSignature.Normalize([.. r, .. ReferenceP256.ToBytes32(ReferenceP256.N - 1)]).ToArray()[32..]);
        Assert.Equal(r, ProtocolSignature.Normalize([.. r, .. ReferenceP256.ToBytes32(HalfN + 1)]).ToArray()[..32]);
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.Normalize([.. r, .. ReferenceP256.ToBytes32(ReferenceP256.N)]));
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => ProtocolSignature.Normalize([.. r, .. new byte[32]]));
    }
}
