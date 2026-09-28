using System;
using System.Collections.Generic;
using AetherFrame.Protocol.Documents;
using Xunit;
using static AetherFrame.Protocol.Tests.DocumentMutations;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Every byte from a future server is hostile. Truncation at every offset, a flipped bit at every
/// position, every extreme length, and repeated abuse: each must end in a controlled protocol
/// exception, never in any other exception, and never in a large allocation.
/// </summary>
public class AdversarialFramingTests
{
    [Fact]
    public void TruncationAtEveryOffset_IsRefusedWithAProtocolException()
    {
        using var signer = TestPersonas.CreateA();
        foreach (var document in new[] { Samples.SignedSnapshot(signer), Samples.SignedRetraction(signer) })
        {
            var errors = new HashSet<ProtocolError>();
            for (var length = 0; length < document.Length; length++)
            {
                var truncated = Truncate(document, length);
                errors.Add(ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(truncated)).Error);
            }

            Assert.Contains(ProtocolError.Truncated, errors);
            Assert.DoesNotContain(ProtocolError.SignatureMismatch, errors);
        }
    }

    [Fact]
    public void EveryBitFlip_IsRefusedWithAProtocolException()
    {
        using var signer = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(signer);
        var errors = new HashSet<ProtocolError>();
        for (var offset = 0; offset < document.Length; offset++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var mutated = Mutate(document, offset, (byte)(document[offset] ^ (1 << bit)));
                errors.Add(ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(mutated)).Error);
            }
        }

        Assert.Contains(ProtocolError.InvalidFraming, errors);
        Assert.Contains(ProtocolError.UnsupportedVersion, errors);
        Assert.Contains(ProtocolError.UnknownDocumentType, errors);
        Assert.Contains(ProtocolError.InvalidKey, errors);
        Assert.Contains(ProtocolError.SignatureMismatch, errors);
        Assert.Contains(ProtocolError.LimitExceeded, errors);
        Assert.Contains(ProtocolError.Truncated, errors);
        Assert.Contains(ProtocolError.TrailingBytes, errors);
    }

    [Fact]
    public void ExtremeDeclaredLengths_AreRefusedWithoutAllocatingForThem()
    {
        using var signer = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(signer);
        var lengths = new uint[] { 0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF, (uint)ProtocolLimits.MaxPayloadBytes, (uint)ProtocolLimits.MaxPayloadBytes + 1, (uint)ProtocolLimits.MaxDocumentBytes, (uint)document.Length, (uint)document.Length * 2 };
        foreach (var length in lengths)
        {
            var mutated = WithPayloadLength(document, length);
            ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(mutated));

            // Measured without the test framework in the way (its own Assert.Throws allocates tens of
            // kilobytes): what is left is the exception and its message, never a buffer sized by the
            // declared length.
            var allocated = AllocatedByRejection(mutated);
            Assert.True(allocated < 4 * 1024, $"declared length {length} allocated {allocated} bytes");
        }
    }

    [Fact]
    public void OversizedInput_IsRefusedBeforeItIsRead()
    {
        var huge = new byte[ProtocolLimits.MaxDocumentBytes + 1];
        "AFPD"u8.CopyTo(huge);
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => SignedDocumentCodec.Verify(huge));
        Assert.True(AllocatedByRejection(huge) < 4 * 1024);

        var exact = new byte[ProtocolLimits.MaxDocumentBytes];
        "AFPD"u8.CopyTo(exact);
        exact[5] = 1;
        exact[6] = 1;
        ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(exact));
    }

    [Fact]
    public void RandomBytes_AreRefusedWithoutCrashing()
    {
        var random = new Random(7);
        for (var round = 0; round < 2000; round++)
        {
            var bytes = new byte[random.Next(0, 300)];
            random.NextBytes(bytes);
            if (random.Next(2) == 0 && bytes.Length >= 4)
            {
                "AFPD"u8.CopyTo(bytes);
            }

            if (random.Next(2) == 0 && bytes.Length >= 7)
            {
                bytes[4] = 0;
                bytes[5] = 1;
                bytes[6] = (byte)random.Next(1, 3);
            }

            ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(bytes));
        }
    }

    [Fact]
    public void RepeatedHostileParsing_LeavesNoStateBehind()
    {
        using var signer = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(signer);
        var hostile = new[]
        {
            Truncate(document, 100),
            Append(document, 1),
            WithPayloadLength(document, 0xFFFFFFFF),
            Flip(document, Layout.Payload + 3),
            Mutate(document, Layout.Type, 9),
        };

        for (var round = 0; round < 500; round++)
        {
            foreach (var input in hostile)
            {
                ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(input));
            }

            Assert.Equal(signer.PublicKey.Id, SignedDocumentCodec.Verify(document).Persona);
        }
    }

    /// <summary>Bytes the protocol allocates to refuse <paramref name="document"/>, measured on a warm throw path.</summary>
    private static long AllocatedByRejection(byte[] document)
    {
        Reject(document);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Reject(document);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void Reject(byte[] document)
    {
        try
        {
            SignedDocumentCodec.Verify(document);
        }
        catch (ProtocolException)
        {
            return;
        }

        throw new InvalidOperationException("accepted");
    }
}
