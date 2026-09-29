using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Signing;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Deterministic fuzz-style campaigns: thousands of seeded mutations of valid documents, each of
/// which must end in a protocol exception within a small allocation, and never in anything else.
/// Every case is reproducible from its seed and index.
/// </summary>
public class FuzzTests
{
    private const int CasesPerSeed = 4000;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SeededMutationsOfSignedDocuments_AreAlwaysRefused(int seed)
    {
        using var signer = TestPersonas.CreateA();
        var bases = new[]
        {
            Samples.SignedSnapshot(signer),
            Samples.SignedRetraction(signer),
            SignedDocumentCodec.Sign(new ProfileSnapshot(Samples.Profile, Samples.Revision, 1, "Caf\u00e9 \U0001F600 \u65e5\u672c", [Samples.Image(Samples.Asset1)]), signer),
            SignedDocumentCodec.Sign(new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "n", []), signer),
        };

        var random = new Random(seed * 1_000_003);
        var stopwatch = Stopwatch.StartNew();
        var errors = new Dictionary<ProtocolError, int>();
        var strategies = new HashSet<string>();
        var skipped = 0;
        for (var index = 0; index < CasesPerSeed; index++)
        {
            var document = bases[random.Next(bases.Length)];
            var mutated = Mutator.Apply(random, document, out var strategy);
            strategies.Add(strategy);
            if (mutated.AsSpan().SequenceEqual(document))
            {
                skipped++;
                continue;
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            ProtocolError error;
            try
            {
                error = Assert.Throws<ProtocolException>(() => SignedDocumentCodec.Verify(mutated)).Error;
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed} case {index} ({strategy}) was not refused cleanly: {e.Message}");
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 64 * 1024, $"seed {seed} case {index} ({strategy}) allocated {allocated} bytes");
            errors[error] = errors.GetValueOrDefault(error) + 1;
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"the campaign took {stopwatch.Elapsed}");
        Assert.True(skipped < CasesPerSeed / 10, $"{skipped} no-op mutations");
        Assert.True(errors.Count >= 6, "the mutations reached only " + string.Join(", ", errors.Keys));
        Assert.Equal(Mutator.Strategies.Length, strategies.Count);
    }

    [Fact]
    public void SeededPayloadMutations_ReSigned_AreEitherRefusedOrDecodeToTheirOwnCanonicalBytes()
    {
        // With a fresh signature over every mutated payload, the envelope accepts them all and only
        // the payload decoder stands between the bytes and a model. Whatever it accepts must
        // re-encode to exactly the bytes it read: there is one encoding of every model.
        using var signer = TestPersonas.CreateA();
        var payloads = new[] { Samples.Snapshot().EncodePayload(), PayloadBuilder.MaximalSnapshot().EncodePayload().AsSpan(0, 200).ToArray(), Samples.Retraction().EncodePayload() };
        var random = new Random(2026_09_27);
        var accepted = 0;
        var refused = new HashSet<ProtocolError>();
        for (var index = 0; index < 3000; index++)
        {
            var type = index % 3 == 2 ? DocumentType.ProfileRetraction : DocumentType.ProfileSnapshot;
            var payload = payloads[index % 3];
            var mutated = MutatePayload(random, payload);
            if (mutated.Length == 0 || mutated.Length > ProtocolLimits.MaxPayloadBytes)
            {
                continue;
            }

            var document = PayloadBuilder.Signed(type, signer, mutated);
            try
            {
                var verified = SignedDocumentCodec.Verify(document);
                Assert.Equal(mutated, verified.Document.EncodePayload());
                Assert.Equal(type, verified.DocumentType);
                accepted++;
            }
            catch (ProtocolException e)
            {
                refused.Add(e.Error);
            }
        }

        Assert.True(accepted > 0, "some mutations should still be valid documents");
        Assert.Contains(ProtocolError.Truncated, refused);
        Assert.Contains(ProtocolError.TrailingBytes, refused);
        Assert.Contains(ProtocolError.InvalidValue, refused);
    }

    [Fact]
    public void FuzzCases_AreReproducibleFromTheirSeed()
    {
        using var signer = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(signer);
        var first = Enumerable.Range(0, 200).Select(i => Mutator.Apply(new Random(42), document, out var _strategy)).First();
        var second = Mutator.Apply(new Random(42), document, out _);
        Assert.Equal(first, second);
    }

    private static byte[] MutatePayload(Random random, byte[] payload)
    {
        var copy = (byte[])payload.Clone();
        var offset = random.Next(copy.Length);
        switch (random.Next(5))
        {
            case 0:
                copy[offset] ^= (byte)(1 << random.Next(8));
                return copy;
            case 1:
                copy[offset] = (byte)random.Next(256);
                return copy;
            case 2:
                return copy.AsSpan(0, offset).ToArray();
            case 3:
                return [.. copy, (byte)random.Next(256)];
            default:
                return [.. copy.AsSpan(0, offset), (byte)random.Next(256), .. copy.AsSpan(offset)];
        }
    }
}
