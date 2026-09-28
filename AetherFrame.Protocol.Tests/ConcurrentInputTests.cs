using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Threading;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Signing;
using Xunit;
using Xunit.Sdk;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// A caller's buffer may be rewritten by another thread while the protocol reads it (a reused
/// network buffer, a mapped file). Every reader copies its input before checking it, so such a
/// buffer can only make an input invalid: every accept is the genuine value, every refusal is a
/// protocol exception, and a value that passed a check is the value used afterwards. These tests
/// hammer each reader for a fraction of a second while another thread flips its input; they cannot
/// prove the absence of a race, but the check-then-copy versions of these readers failed them
/// within milliseconds (docs/networking/NETWORK0_HANDOFF.md, "Remediation").
/// </summary>
public class ConcurrentInputTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void Verify_WhileTheKeyIsRewritten_AcceptsOnlyTheGenuineDocumentOrRefusesCleanly()
    {
        using var signer = TestPersonas.CreateA();
        var genuine = Samples.SignedSnapshot(signer);
        var offset = Layout.Key + 64;
        Race(genuine, offset, (byte)(genuine[offset] ^ 0x01), buffer => AcceptGenuine(buffer, genuine, signer.PublicKey.Id));
    }

    [Fact]
    public void Verify_WhileTheSignatureIsRewritten_AcceptsOnlyTheGenuineDocumentOrRefusesCleanly()
    {
        using var signer = TestPersonas.CreateA();
        var genuine = Samples.SignedSnapshot(signer);
        var offset = Layout.SignatureOffset(genuine) + 32;
        var s = new BigInteger(genuine.AsSpan(offset, 32), isUnsigned: true, isBigEndian: true);
        var highS = ReferenceP256.ToBytes32(ReferenceP256.N - s);
        Race(genuine, offset, highS, buffer => AcceptGenuine(buffer, genuine, signer.PublicKey.Id));
    }

    [Fact]
    public void Verify_WhileThePayloadIsRewritten_AcceptsOnlyTheGenuineDocumentOrRefusesCleanly()
    {
        using var signer = TestPersonas.CreateA();
        var genuine = Samples.SignedSnapshot(signer);
        var offset = Layout.Payload + 42 + 4; // the first byte of the name
        Race(genuine, offset, (byte)'X', buffer => AcceptGenuine(buffer, genuine, signer.PublicKey.Id));
    }

    [Fact]
    public void PublicKeyFromBytes_WhileRewritten_ReturnsOnlyPointsOnTheCurve()
    {
        using var signer = TestPersonas.CreateA();
        var genuine = signer.PublicKey.ToArray();
        Race(genuine, 64, (byte)(genuine[64] ^ 0x01), buffer =>
        {
            var key = PersonaPublicKey.FromBytes(buffer);
            if (!ReferenceP256.IsOnCurve(key.Bytes) || !key.Bytes.SequenceEqual(genuine))
            {
                throw new XunitException("FromBytes returned a key that is not the point that passed its check.");
            }
        });
    }

    [Fact]
    public void SignatureFromBytes_WhileRewritten_ReturnsOnlyCanonicalSignatures()
    {
        using var signer = TestPersonas.CreateA();
        var genuine = signer.Sign(SigningInput.Create(DocumentType.ProfileRetraction, signer.PublicKey, Samples.Retraction().EncodePayload())).ToArray();
        var s = new BigInteger(genuine.AsSpan(32), isUnsigned: true, isBigEndian: true);
        Race(genuine, 32, ReferenceP256.ToBytes32(ReferenceP256.N - s), buffer =>
        {
            var signature = ProtocolSignature.FromBytes(buffer);
            var returned = new BigInteger(signature.Bytes.Slice(32), isUnsigned: true, isBigEndian: true);
            if (returned.IsZero || returned > (ReferenceP256.N >> 1))
            {
                throw new XunitException("FromBytes returned a signature whose s is not in the low half.");
            }
        });
    }

    [Fact]
    public void OpaqueIdsAndDigests_WhileRewrittenToZero_AreNeverReturnedEmpty()
    {
        var id = Samples.Profile.ToArray();
        Race(id, 15, 0, buffer =>
        {
            var parsed = ProfileId.FromBytes(buffer);
            if (parsed.IsEmpty)
            {
                throw new XunitException("FromBytes returned an empty profile id.");
            }
        });

        var digest = new byte[32];
        digest[31] = 1;
        Race(digest, 31, 0, buffer =>
        {
            var image = new ImageReference(Samples.Asset1, buffer, ImageFormat.Png, 1, 1, 1);
            if (image.Sha256.IndexOfAnyExcept((byte)0) < 0)
            {
                throw new XunitException("ImageReference holds an all-zero digest.");
            }
        });
    }

    [Fact]
    public void PlatformKeyImport_RefusalsAreReportedAsInvalidKey()
    {
        // A point the protocol's own check would refuse, built behind FromBytes so the platform sees
        // it: whatever exception the platform uses (Windows CNG: PlatformNotSupportedException;
        // OpenSSL: CryptographicException), the protocol reports an invalid key and nothing else.
        using var signer = TestPersonas.CreateA();
        var offCurve = signer.PublicKey.ToArray();
        offCurve[64] ^= 0x01;
        var constructor = typeof(PersonaPublicKey).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(byte[])])!;
        var key = (PersonaPublicKey)constructor.Invoke([offCurve]);
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => key.CreateEcdsa().Dispose());

        var payload = Samples.Retraction().EncodePayload();
        var signature = signer.Sign(SigningInput.Create(DocumentType.ProfileRetraction, signer.PublicKey, payload));
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => SignatureVerifier.Verify(SigningInput.Create(DocumentType.ProfileRetraction, key, payload), signature));
    }

    private static void AcceptGenuine(byte[] buffer, byte[] genuine, PersonaId persona)
    {
        var verified = SignedDocumentCodec.Verify(buffer);
        if (verified.Persona != persona || !verified.Document.EncodePayload().AsSpan().SequenceEqual(genuine.AsSpan(Layout.Payload, Layout.PayloadLengthOf(genuine))))
        {
            throw new XunitException("Verify accepted a document that is not the genuine one.");
        }
    }

    private static void Race(byte[] template, int offset, byte alternative, Action<byte[]> attempt) =>
        Race(template, offset, [alternative], attempt);

    /// <summary>
    /// Runs <paramref name="attempt"/> on a shared buffer for <see cref="Duration"/> while another
    /// thread keeps rewriting <paramref name="alternative"/> over the original bytes at
    /// <paramref name="offset"/>. Anything but a protocol exception or a return is a failure.
    /// </summary>
    private static void Race(byte[] template, int offset, byte[] alternative, Action<byte[]> attempt)
    {
        var shared = (byte[])template.Clone();
        var original = template.AsSpan(offset, alternative.Length).ToArray();
        var stop = 0;
        var writer = new Thread(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                alternative.CopyTo(shared, offset);
                original.CopyTo(shared, offset);
            }
        });
        writer.Start();

        var outcomes = new Dictionary<string, int>();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < Duration)
            {
                string outcome;
                try
                {
                    attempt(shared);
                    outcome = "accepted";
                }
                catch (ProtocolException e)
                {
                    outcome = e.Error.ToString();
                }
                catch (XunitException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    throw new XunitException($"An exception other than ProtocolException escaped: {e.GetType().FullName}: {e.Message}");
                }

                outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            writer.Join();
        }

        Assert.NotEmpty(outcomes);
    }
}
