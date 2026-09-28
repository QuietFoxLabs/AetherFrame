using System;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Signing;
using Xunit;

namespace AetherFrame.Protocol.Tests;

internal static class Hex
{
    public static byte[] Parse(string hex) => Convert.FromHexString(hex);

    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}

/// <summary>
/// Synthetic test personas. Each private scalar is the SHA-256 of a fixed label reduced modulo the
/// group order, so every run, every machine and every independent implementation gets the same
/// keys, and none of them can be mistaken for a real player's key.
/// </summary>
internal static class TestPersonas
{
    public const string LabelA = "AetherFrame.Protocol test persona A";
    public const string LabelB = "AetherFrame.Protocol test persona B";

    public static BigInteger ScalarA => Scalar(LabelA);

    public static BigInteger ScalarB => Scalar(LabelB);

    public static EcdsaPersonaSigner CreateA() => Create(ScalarA);

    public static EcdsaPersonaSigner CreateB() => Create(ScalarB);

    public static BigInteger Scalar(string label)
    {
        var digest = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(label));
        var scalar = new BigInteger(digest, isUnsigned: true, isBigEndian: true) % ReferenceP256.N;
        return scalar.IsZero ? BigInteger.One : scalar;
    }

    public static EcdsaPersonaSigner Create(BigInteger scalar) => new(CreateEcdsa(scalar));

    /// <summary>The platform key of a test persona, for tests that sign around the protocol's signer.</summary>
    public static ECDsa CreateEcdsa(BigInteger scalar)
    {
        var (x, y) = ReferenceP256.PublicKey(scalar);
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = ReferenceP256.ToBytes32(scalar),
            Q = new ECPoint { X = x, Y = y },
        });
    }
}

/// <summary>Fixed sample values, patterned so they are obviously synthetic.</summary>
internal static class Samples
{
    public static readonly ProfileId Profile = ProfileId.Parse("prf_a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1");
    public static readonly ProfileId ProfileB = ProfileId.Parse("prf_e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5");
    public static readonly RevisionId Revision = RevisionId.Parse("rev_b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2");

    // One revision id per sample snapshot: within a profile a revision id names exactly one document
    // (specification, section 13, rule 4), so vectors with different content never share one.
    public static readonly RevisionId RevisionUnicode = RevisionId.Parse("rev_b3b3b3b3b3b3b3b3b3b3b3b3b3b3b3b3");
    public static readonly RevisionId RevisionEmpty = RevisionId.Parse("rev_b4b4b4b4b4b4b4b4b4b4b4b4b4b4b4b4");
    public static readonly RevisionId RevisionMaximal = RevisionId.Parse("rev_b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5");
    public static readonly AssetId Asset1 = AssetId.Parse("ast_c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3");
    public static readonly AssetId Asset2 = AssetId.Parse("ast_d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4");
    public const long CreatedAt = 1_700_000_000;
    public const long IssuedAt = 1_700_000_100;
    public const string Name = "Sample Plate";

    /// <summary>
    /// The owner of each sample profile id, as the record a backend would hold: persona A published
    /// prf_a1a1…, persona B published prf_e5e5…. The vectors that are valid examples are signed by the
    /// recorded owner; the serverObligations vectors are signed by the other persona.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ProfileOwners = new Dictionary<string, string>
    {
        [Profile.ToString()] = "A",
        [ProfileB.ToString()] = "B",
    };

    public static byte[] Digest(byte fill)
    {
        var digest = new byte[32];
        Array.Fill(digest, fill);
        return digest;
    }

    public static ImageReference Image(AssetId id, byte fill = 0x11, ImageFormat format = ImageFormat.Png, long bytes = 1234, int width = 640, int height = 480) =>
        new(id, Digest(fill), format, bytes, width, height);

    public static ProfileSnapshot Snapshot() =>
        new(Profile, Revision, CreatedAt, Name, [Image(Asset1), Image(Asset2, 0x22, ImageFormat.Jpeg, 5678, 1920, 1080)]);

    public static ProfileRetraction Retraction() => new(Profile, IssuedAt);

    public static byte[] SignedSnapshot(IPersonaSigner signer) => SignedDocumentCodec.Sign(Snapshot(), signer);

    public static byte[] SignedRetraction(IPersonaSigner signer) => SignedDocumentCodec.Sign(Retraction(), signer);
}

/// <summary>Where the parts of a signed document sit (docs/networking/ProtocolSpecification-v1.md, "Signed document").</summary>
internal static class Layout
{
    public const int Magic = 0;
    public const int Version = 4;
    public const int Type = 6;
    public const int Key = 7;
    public const int PayloadLength = 72;
    public const int Payload = 76;

    public static int PayloadLengthOf(byte[] document) => document.Length - ProtocolLimits.SignedDocumentOverheadBytes;

    public static int SignatureOffset(byte[] document) => document.Length - ProtocolConstants.SignatureLength;
}

internal static class ProtocolAssert
{
    /// <summary>The action must fail with exactly this protocol error.</summary>
    public static ProtocolException Throws(ProtocolError expected, Action action)
    {
        var exception = Assert.Throws<ProtocolException>(action);
        Assert.Equal(expected, exception.Error);
        AssertSafeMessage(exception);
        return exception;
    }

    /// <summary>Hostile input may fail for any protocol reason, but only with a protocol exception: anything else is a crash.</summary>
    public static ProtocolException Rejects(Action action)
    {
        var exception = Assert.Throws<ProtocolException>(action);
        AssertSafeMessage(exception);
        return exception;
    }

    /// <summary>A message never repeats key, signature or payload bytes: no run of 24 or more hex digits.</summary>
    private static void AssertSafeMessage(ProtocolException exception)
    {
        var run = 0;
        foreach (var c in exception.Message)
        {
            run = Uri.IsHexDigit(c) ? run + 1 : 0;
            Assert.True(run < 24, "The message looks like it contains raw bytes: " + exception.Message);
        }
    }
}
