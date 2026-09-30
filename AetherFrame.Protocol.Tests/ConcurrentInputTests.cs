using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Requests;
using AetherFrame.Protocol.Signing;
using Xunit;
using Xunit.Sdk;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// A caller's buffer may be rewritten by another thread while the protocol reads it (a reused
/// network buffer, a mapped file). Every reader copies its input before checking it, so such a
/// buffer can only make an input invalid: every accept is the genuine value, every refusal is a
/// protocol exception, and a value that passed a check is the value used afterwards. These tests
/// hammer each reader for a fraction of a second (longer, up to a bound, where a race must be seen
/// to have run) while another thread flips its input; they cannot
/// prove the absence of a race, but the check-then-copy versions of these readers failed them
/// within milliseconds (docs/networking/NETWORK0_HANDOFF.md, "Remediation").
/// </summary>
public class ConcurrentInputTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(300);

    /// <summary>The longest a race keeps going to see the outcomes it requires.</summary>
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(20);

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
    public void VerifySubmission_WhileTheDocumentIsRewritten_HashesVerifiesAndReturnsOneCopy()
    {
        // Section 14.4: the bytes hashed are the bytes verified and the bytes a server stores. With
        // the document changing under the reader, an accept returns the genuine bytes and every
        // refusal is the digest's. A reader that hashed one read of the buffer and verified another
        // would also refuse some copies by their signature instead.
        using var signer = TestPersonas.CreateA();
        var genuine = Samples.SignedSnapshot(signer);
        var deployment = DeploymentName.Parse(ProofSamples.DeploymentText);
        var proof = RequestProofCodec.Sign(genuine, deployment, RequestChallenge.Parse(ProofSamples.ChallengeText), signer);
        var outcomes = Race(genuine, Layout.Payload + 42 + 4, [(byte)'X'], buffer =>
        {
            var submission = RequestProofCodec.VerifySubmission(proof, buffer, deployment);
            if (!submission.DocumentBytes.SequenceEqual(genuine))
            {
                throw new XunitException("VerifySubmission accepted bytes that are not the genuine document.");
            }
        }, RequiredOutcomes(ProtocolError.ProofMismatch));
        Assert.True(outcomes.Keys.All(k => k is "accepted" or nameof(ProtocolError.ProofMismatch)), "VerifySubmission refused a rewritten document for another reason than its digest: " + string.Join(", ", outcomes.Keys));
        AssertRaceWasExercised(outcomes, ProtocolError.ProofMismatch);
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
        // The shared buffer alternates between an all-zero id, which must be refused, and an id whose
        // only non-zero byte is the last one, which must be accepted. A reader that checked the
        // buffer in one read and took its value in another could pass the check on the non-zero
        // state and return the empty id from the zero state: that is exactly what 568c137's
        // check-then-read Id128.FromBytes did, and this test fails against it within milliseconds.
        // (An id such as a1a1…a1 with one byte rewritten never becomes empty and tests nothing.)
        var id = new byte[16];
        id[15] = 1;
        var idOutcomes = Race(id, 15, [0], buffer =>
        {
            var parsed = ProfileId.FromBytes(buffer);
            if (parsed.IsEmpty)
            {
                throw new XunitException("FromBytes returned an empty profile id.");
            }
        }, RequiredOutcomes(ProtocolError.InvalidValue));
        AssertRaceWasExercised(idOutcomes, ProtocolError.InvalidValue);

        var digest = new byte[32];
        digest[31] = 1;
        var digestOutcomes = Race(digest, 31, [0], buffer =>
        {
            var image = new ImageReference(Samples.Asset1, buffer, ImageFormat.Png, 1, 1, 1);
            if (image.Sha256.IndexOfAnyExcept((byte)0) < 0)
            {
                throw new XunitException("ImageReference holds an all-zero digest.");
            }
        }, RequiredOutcomes(ProtocolError.InvalidValue));
        AssertRaceWasExercised(digestOutcomes, ProtocolError.InvalidValue);
    }

    /// <summary>
    /// The outcomes a race must produce to have exercised anything: an acceptance and the expected
    /// refusal. None on one core, where the scheduler decides and a race can only be vacuous, never wrong.
    /// </summary>
    private static string[] RequiredOutcomes(ProtocolError expectedRefusal) =>
        Environment.ProcessorCount < 2 ? [] : ["accepted", expectedRefusal.ToString()];

    /// <summary>
    /// A race that never produced both an acceptance and the expected refusal exercised nothing: the
    /// writer thread did not interleave with the reader. Required wherever a second core is available;
    /// on one core the scheduler decides, and the test can only be vacuous, never wrong.
    /// </summary>
    private static void AssertRaceWasExercised(Dictionary<string, int> outcomes, ProtocolError expectedRefusal)
    {
        if (Environment.ProcessorCount < 2)
        {
            return;
        }

        Assert.True(outcomes.ContainsKey("accepted"), "the genuine value was never accepted: " + string.Join(", ", outcomes.Keys));
        Assert.True(outcomes.ContainsKey(expectedRefusal.ToString()), "the rewritten value was never seen: " + string.Join(", ", outcomes.Keys));
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

    private static Dictionary<string, int> Race(byte[] template, int offset, byte alternative, Action<byte[]> attempt) =>
        Race(template, offset, [alternative], attempt);

    /// <summary>
    /// Runs <paramref name="attempt"/> on a shared buffer for <see cref="Duration"/> while another
    /// thread keeps rewriting <paramref name="alternative"/> over the original bytes at
    /// <paramref name="offset"/>. Anything but a protocol exception or a return is a failure. Timing
    /// starts once the writer is running. A race given <paramref name="required"/> outcomes keeps
    /// going past <see cref="Duration"/>, up to <see cref="MaximumDuration"/>, until it has seen them
    /// all: on a loaded machine the writer may get little time in the first fraction of a second.
    /// </summary>
    private static Dictionary<string, int> Race(byte[] template, int offset, byte[] alternative, Action<byte[]> attempt, string[]? required = null)
    {
        var shared = (byte[])template.Clone();
        var original = template.AsSpan(offset, alternative.Length).ToArray();
        var stop = 0;
        var running = 0;
        var writer = new Thread(() =>
        {
            Volatile.Write(ref running, 1);
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
            var spin = new SpinWait();
            while (Volatile.Read(ref running) == 0)
            {
                spin.SpinOnce();
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < Duration || (stopwatch.Elapsed < MaximumDuration && !SeenAll(outcomes, required)))
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
        return outcomes;
    }

    private static bool SeenAll(Dictionary<string, int> outcomes, string[]? required) =>
        required is null || Array.TrueForAll(required, outcomes.ContainsKey);
}
