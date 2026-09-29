using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The at-rest envelope: it decodes exactly what it encoded, the header is the context, and every
/// malformed input is refused without an exception.
/// </summary>
public class ProtectedKeyEnvelopeTests
{
    private static readonly PersonaSlotId Slot = PersonaSlotId.Parse("slot_0123456789abcdef0123456789abcdef");
    private const string Protector = "test.unprotected.v1";

    private static byte[] Envelope(byte[]? blob = null, string protector = Protector, PersonaSlotId? slot = null)
    {
        var header = ProtectedKeyEnvelope.EncodeHeader(slot ?? Slot, protector, SyntheticKeys.BasePoint);
        return ProtectedKeyEnvelope.Encode(header, blob ?? new byte[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public void DecodesExactlyWhatItEncoded_AndTheHeaderIsTheContext()
    {
        var header = ProtectedKeyEnvelope.EncodeHeader(Slot, Protector, SyntheticKeys.BasePoint);
        var blob = new byte[] { 9, 8, 7 };
        var envelope = ProtectedKeyEnvelope.Encode(header, blob);

        Assert.True(ProtectedKeyEnvelope.TryDecode(envelope, out var decoded));
        Assert.Equal(Slot, decoded.Slot);
        Assert.Equal(Protector, decoded.ProtectorId);
        Assert.Equal(SyntheticKeys.BasePoint, decoded.PublicKey);
        Assert.Equal(header, decoded.Context);
        Assert.Equal(blob, decoded.Blob);
        Assert.Equal(header.Length + 4 + blob.Length, envelope.Length);
    }

    [Fact]
    public void TheHeaderIsLaidOutAsDocumented()
    {
        var header = ProtectedKeyEnvelope.EncodeHeader(Slot, "a.b", SyntheticKeys.BasePoint);
        Assert.Equal("AFPK"u8.ToArray(), header[..4]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)));
        Assert.Equal(Convert.FromHexString("0123456789abcdef0123456789abcdef"), header[6..22]);
        Assert.Equal(3, header[22]);
        Assert.Equal("a.b"u8.ToArray(), header[23..26]);
        Assert.Equal(SyntheticKeys.BasePoint.ToArray(), header[26..]);
        Assert.Equal(26 + ProtocolConstants.PublicKeyLength, header.Length);
    }

    [Theory]
    [InlineData("windows.dpapi.currentuser.v1", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("Test.v1", false)]
    [InlineData("test v1", false)]
    [InlineData("test_v1", false)]
    [InlineData("tést.v1", false)]
    public void ProtectorIds_AreLowercaseAsciiWordsWithDotsAndHyphens(string id, bool valid)
    {
        Assert.Equal(valid, ProtectedKeyEnvelope.IsValidProtectorId(id));
    }

    [Fact]
    public void ProtectorIds_AreAtMost64Characters()
    {
        Assert.True(ProtectedKeyEnvelope.IsValidProtectorId(new string('a', 64)));
        Assert.False(ProtectedKeyEnvelope.IsValidProtectorId(new string('a', 65)));
        Assert.False(ProtectedKeyEnvelope.IsValidProtectorId(null));
    }

    [Fact]
    public void EncodeHeader_RefusesAnEmptySlotAndAnInvalidId()
    {
        Assert.Throws<ArgumentException>(() => ProtectedKeyEnvelope.EncodeHeader(default, Protector, SyntheticKeys.BasePoint));
        Assert.Throws<ArgumentException>(() => ProtectedKeyEnvelope.EncodeHeader(Slot, "Bad", SyntheticKeys.BasePoint));
    }

    [Fact]
    public void Encode_RefusesAnEmptyOrOversizedBlob()
    {
        var header = ProtectedKeyEnvelope.EncodeHeader(Slot, Protector, SyntheticKeys.BasePoint);
        Assert.Throws<ArgumentException>(() => ProtectedKeyEnvelope.Encode(header, ReadOnlySpan<byte>.Empty));
        Assert.Throws<ArgumentException>(() => ProtectedKeyEnvelope.Encode(header, new byte[ProtectedKeyEnvelope.MaxBlobLength + 1]));
        Assert.True(ProtectedKeyEnvelope.TryDecode(ProtectedKeyEnvelope.Encode(header, new byte[ProtectedKeyEnvelope.MaxBlobLength]), out _));
    }

    [Fact]
    public void Refuses_ATrailingByte_ATruncation_AndAnEmptyInput()
    {
        var envelope = Envelope();
        Assert.False(ProtectedKeyEnvelope.TryDecode([.. envelope, 0], out _));
        Assert.False(ProtectedKeyEnvelope.TryDecode(envelope.AsSpan(0, envelope.Length - 1), out _));
        Assert.False(ProtectedKeyEnvelope.TryDecode(ReadOnlySpan<byte>.Empty, out _));
        for (var length = 0; length < envelope.Length; length++)
        {
            Assert.False(ProtectedKeyEnvelope.TryDecode(envelope.AsSpan(0, length), out _));
        }
    }

    [Fact]
    public void Refuses_AnotherMagic_AnotherVersion_AndAZeroSlot()
    {
        var wrongMagic = Envelope();
        wrongMagic[0] = (byte)'B';
        Assert.False(ProtectedKeyEnvelope.TryDecode(wrongMagic, out _));

        var wrongVersion = Envelope();
        BinaryPrimitives.WriteUInt16BigEndian(wrongVersion.AsSpan(4), 2);
        Assert.False(ProtectedKeyEnvelope.TryDecode(wrongVersion, out _));

        var zeroSlot = Envelope();
        zeroSlot.AsSpan(6, 16).Clear();
        Assert.False(ProtectedKeyEnvelope.TryDecode(zeroSlot, out _));
    }

    [Fact]
    public void Refuses_ABadProtectorIdLengthOrCharacter()
    {
        var zeroLength = Envelope();
        zeroLength[22] = 0;
        Assert.False(ProtectedKeyEnvelope.TryDecode(zeroLength, out _));

        var tooLong = Envelope();
        tooLong[22] = 65;
        Assert.False(ProtectedKeyEnvelope.TryDecode(tooLong, out _));

        var uppercase = Envelope();
        uppercase[23] = (byte)'T';
        Assert.False(ProtectedKeyEnvelope.TryDecode(uppercase, out _));

        // A length that runs past the public key into the blob still fails: the total no longer fits.
        var overrun = Envelope();
        overrun[22] = (byte)(Protector.Length + 1);
        Assert.False(ProtectedKeyEnvelope.TryDecode(overrun, out _));
    }

    [Fact]
    public void Refuses_APublicKeyThatIsNotOnTheCurve_AndABlobLengthThatDisagrees()
    {
        var offCurve = Envelope();
        offCurve[23 + Protector.Length + 10] ^= 0x01;
        Assert.False(ProtectedKeyEnvelope.TryDecode(offCurve, out _));

        var blobStart = 23 + Protector.Length + ProtocolConstants.PublicKeyLength;
        var longer = Envelope();
        BinaryPrimitives.WriteUInt32BigEndian(longer.AsSpan(blobStart), 6);
        Assert.False(ProtectedKeyEnvelope.TryDecode(longer, out _));

        var shorter = Envelope();
        BinaryPrimitives.WriteUInt32BigEndian(shorter.AsSpan(blobStart), 4);
        Assert.False(ProtectedKeyEnvelope.TryDecode(shorter, out _));

        var zero = Envelope();
        BinaryPrimitives.WriteUInt32BigEndian(zero.AsSpan(blobStart), 0);
        Assert.False(ProtectedKeyEnvelope.TryDecode(zero, out _));

        var huge = Envelope();
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(blobStart), uint.MaxValue);
        Assert.False(ProtectedKeyEnvelope.TryDecode(huge, out _));
    }

    [Fact]
    public void RandomDamage_NeverThrows_AndEverySuccessfulDecodeIsTheOriginal()
    {
        var envelope = Envelope(blob: RandomNumberGenerator.GetBytes(48));
        var random = new Random(20260929);
        for (var round = 0; round < 5000; round++)
        {
            var damaged = (byte[])envelope.Clone();
            var flips = 1 + random.Next(3);
            for (var flip = 0; flip < flips; flip++)
            {
                damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
            }

            if (ProtectedKeyEnvelope.TryDecode(damaged, out var decoded))
            {
                // The header carries no checksum by design: damage that keeps a field well formed
                // (another slot, another id, the blob) decodes, and the protector's context binding
                // refuses it later. What must hold is that what decoded is exactly what was read.
                Assert.Equal(damaged.AsSpan(0, decoded.Context.Length).ToArray(), decoded.Context);
                Assert.Equal(damaged.AsSpan(decoded.Context.Length + 4).ToArray(), decoded.Blob);
                Assert.True(ProtectedKeyEnvelope.IsValidProtectorId(decoded.ProtectorId));
                Assert.False(decoded.Slot.IsEmpty);
            }
        }

        for (var round = 0; round < 2000; round++)
        {
            var garbage = new byte[random.Next(0, 400)];
            random.NextBytes(garbage);
            Assert.False(ProtectedKeyEnvelope.TryDecode(garbage, out _));
        }
    }
}
