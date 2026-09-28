using System;
using System.Linq;
using System.Numerics;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;
using Xunit;
using static AetherFrame.Protocol.Tests.DocumentMutations;

namespace AetherFrame.Protocol.Tests;

/// <summary>Hostile key material and signatures inside otherwise well-formed documents.</summary>
public class AdversarialKeyAndSignatureTests
{
    [Fact]
    public void InvalidCurveMaterial_IsRefusedAsInvalidKey()
    {
        using var signer = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(signer);
        var p = ReferenceP256.ToBytes32(ReferenceP256.P);
        var pMinusOne = ReferenceP256.ToBytes32(ReferenceP256.P - 1);
        var x = document.AsSpan(Layout.Key + 1, 32).ToArray();
        var y = document.AsSpan(Layout.Key + 33, 32).ToArray();

        var keys = new (string Name, byte[] Key)[]
        {
            ("all zero", new byte[65]),
            ("prefix 0x04, zero point", [0x04, .. new byte[64]]),
            ("prefix 0x02 compressed", [0x02, .. x, .. y]),
            ("prefix 0x03 compressed", [0x03, .. x, .. y]),
            ("prefix 0x00", [0x00, .. x, .. y]),
            ("x = p", [0x04, .. p, .. y]),
            ("y = p", [0x04, .. x, .. p]),
            ("x = p - 1", [0x04, .. pMinusOne, .. y]),
            ("all 0xFF", Enumerable.Repeat((byte)0xFF, 65).ToArray()),
            ("x and y swapped", [0x04, .. y, .. x]),
            ("y + 1", [0x04, .. x, .. ReferenceP256.ToBytes32(new BigInteger(y, true, true) + 1)]),
            ("x + 1", [0x04, .. ReferenceP256.ToBytes32(new BigInteger(x, true, true) + 1), .. y]),
            ("random", RandomPoint(1)),
            ("random 2", RandomPoint(2)),
        };

        foreach (var (name, key) in keys)
        {
            Assert.Equal(65, key.Length);
            var mutated = Substitute(document, Layout.Key, key);
            var error = ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(mutated)).Error;
            Assert.True(error == ProtocolError.InvalidKey, $"{name}: {error}");
            ProtocolAssert.Throws(ProtocolError.InvalidKey, () => PersonaPublicKey.FromBytes(key));
        }
    }

    [Fact]
    public void SignatureVariants_AreRefusedForTheRightReason()
    {
        using var signer = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(signer);
        var offset = Layout.SignatureOffset(document);
        var r = document.AsSpan(offset, 32).ToArray();
        var s = document.AsSpan(offset + 32, 32).ToArray();
        var n = ReferenceP256.ToBytes32(ReferenceP256.N);
        var highS = ReferenceP256.ToBytes32(ReferenceP256.N - new BigInteger(s, true, true));

        var invalid = new (string Name, byte[] Signature)[]
        {
            ("r = 0", [.. new byte[32], .. s]),
            ("s = 0", [.. r, .. new byte[32]]),
            ("r = n", [.. n, .. s]),
            ("s = n", [.. r, .. n]),
            ("high s", [.. r, .. highS]),
            ("all 0xFF", Enumerable.Repeat((byte)0xFF, 64).ToArray()),
        };
        foreach (var (name, signature) in invalid)
        {
            var mutated = Substitute(document, offset, signature);
            var error = ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(mutated)).Error;
            Assert.True(error == ProtocolError.InvalidSignature, $"{name}: {error}");
        }

        var mismatched = new (string Name, byte[] Signature)[]
        {
            ("r = 1", [.. ReferenceP256.ToBytes32(BigInteger.One), .. s]),
            ("s = 1", [.. r, .. ReferenceP256.ToBytes32(BigInteger.One)]),
            ("r = n - 1", [.. ReferenceP256.ToBytes32(ReferenceP256.N - 1), .. s]),
            ("s = floor(n/2)", [.. r, .. ReferenceP256.ToBytes32(ReferenceP256.N >> 1)]),
            ("last bit of s flipped", [.. r, .. s.Take(31), (byte)(s[31] ^ 1)]),
        };
        foreach (var (name, signature) in mismatched)
        {
            var mutated = Substitute(document, offset, signature);
            var error = ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(mutated)).Error;
            Assert.True(error == ProtocolError.SignatureMismatch, $"{name}: {error}");
        }

        // Swapping r and s: the old r becomes s, which is refused as non-canonical when it is in the high half and as a mismatch otherwise.
        var swapped = Substitute(document, offset, [.. s, .. r]);
        var swappedError = new BigInteger(r, true, true) > (ReferenceP256.N >> 1) ? ProtocolError.InvalidSignature : ProtocolError.SignatureMismatch;
        ProtocolAssert.Throws(swappedError, () => SignedDocumentCodec.Verify(swapped));
    }

    [Fact]
    public void SignatureFromAnotherDocumentPersonaOrVersion_NeverVerifies()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        var snapshotPayload = Samples.Snapshot().EncodePayload();
        var retractionPayload = Samples.Retraction().EncodePayload();
        var snapshotSignature = a.Sign(SigningInput.Create(DocumentType.ProfileSnapshot, a.PublicKey, snapshotPayload));

        // Same persona, other payload.
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(SignedDocumentCodec.Assemble(DocumentType.ProfileRetraction, a.PublicKey, retractionPayload, snapshotSignature)));
        // Same payload, other persona (key substituted).
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(SignedDocumentCodec.Assemble(DocumentType.ProfileSnapshot, b.PublicKey, snapshotPayload, snapshotSignature)));
        // Same payload and persona, other type.
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(SignedDocumentCodec.Assemble(DocumentType.ProfileRetraction, a.PublicKey, snapshotPayload, snapshotSignature)));
        // A signature over the raw payload bytes (no domain tag) never verifies as a document.
        using (var raw = a.PublicKey.CreateEcdsa())
        {
            Assert.False(raw.VerifyData(snapshotPayload, snapshotSignature.Bytes, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        // A future version 2 that reused the tag would still not accept a version 1 signature: the version is signed.
        var document = SignedDocumentCodec.Assemble(DocumentType.ProfileSnapshot, a.PublicKey, snapshotPayload, snapshotSignature);
        ProtocolAssert.Throws(ProtocolError.UnsupportedVersion, () => SignedDocumentCodec.Verify(Mutate(document, Layout.Version + 1, 2)));
    }

    private static byte[] RandomPoint(int seed)
    {
        var random = new Random(seed);
        var point = new byte[65];
        random.NextBytes(point);
        point[0] = 0x04;
        point[1] = 0x10;
        point[33] = 0x10;
        return point;
    }
}
