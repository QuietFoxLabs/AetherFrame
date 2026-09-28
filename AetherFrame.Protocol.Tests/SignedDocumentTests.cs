using System;
using System.Linq;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Remote;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>The signed document envelope: layout and round trips.</summary>
public class SignedDocumentTests
{
    [Fact]
    public void Snapshot_RoundTripsThroughSignAndVerify()
    {
        using var signer = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(signer);

        Assert.Equal("AFPD"u8.ToArray(), document.Take(4));
        Assert.Equal(new byte[] { 0, 1 }, document.Skip(4).Take(2));
        Assert.Equal(1, document[6]);
        Assert.Equal(signer.PublicKey.ToArray(), document.Skip(7).Take(65));
        var payloadLength = Layout.PayloadLengthOf(document);
        Assert.Equal(new byte[] { 0, 0, (byte)(payloadLength >> 8), (byte)payloadLength }, document.Skip(72).Take(4));
        Assert.Equal(ProtocolLimits.SignedDocumentOverheadBytes + payloadLength, document.Length);

        var verified = SignedDocumentCodec.Verify(document);
        Assert.Equal(signer.PublicKey, verified.PublicKey);
        Assert.Equal(signer.PublicKey.Id, verified.Persona);
        Assert.Equal(DocumentType.ProfileSnapshot, verified.DocumentType);
        var snapshot = Assert.IsType<ProfileSnapshot>(verified.Document);
        Assert.Equal(Samples.Profile, snapshot.ProfileId);
        Assert.Equal(Samples.Revision, snapshot.RevisionId);
        Assert.Equal(Samples.CreatedAt, snapshot.CreatedAtUnixSeconds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Samples.CreatedAt), snapshot.CreatedAt);
        Assert.Equal(Samples.Name, snapshot.Name);
        Assert.Equal([Samples.Asset1, Samples.Asset2], snapshot.Images.Select(i => i.AssetId));
        Assert.Equal(1234 + 5678, snapshot.TotalImageBytes);
        Assert.Equal(ImageFormat.Jpeg, snapshot.Images[1].Format);
        Assert.Equal(1920, snapshot.Images[1].Width);
        Assert.Equal(Samples.Digest(0x22), snapshot.Images[1].Sha256ToArray());
    }

    [Fact]
    public void Retraction_RoundTripsThroughSignAndVerify()
    {
        using var signer = TestPersonas.CreateB();
        var verified = SignedDocumentCodec.Verify(Samples.SignedRetraction(signer));
        Assert.Equal(signer.PublicKey.Id, verified.Persona);
        var retraction = Assert.IsType<ProfileRetraction>(verified.Document);
        Assert.Equal(Samples.Profile, retraction.ProfileId);
        Assert.Equal(Samples.IssuedAt, retraction.IssuedAtUnixSeconds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Samples.IssuedAt), retraction.IssuedAt);
    }

    [Fact]
    public void Verify_ReturnsAnImmutableViewWhoseIdentityComesFromTheKey()
    {
        using var signer = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(signer);
        var first = SignedDocumentCodec.Verify(document);
        document[Layout.Payload] ^= 0xFF;

        // The verified document holds its own copies: changing the input afterwards changes nothing.
        var snapshot = Assert.IsType<ProfileSnapshot>(first.Document);
        Assert.Equal(Samples.Name, snapshot.Name);
        Assert.Equal(signer.PublicKey.Id, first.Persona);
        var keyCopy = first.PublicKey.ToArray();
        keyCopy[1] ^= 0xFF;
        Assert.Equal(signer.PublicKey, first.PublicKey);
        var digestCopy = snapshot.Images[0].Sha256ToArray();
        digestCopy[0] ^= 0xFF;
        Assert.Equal(Samples.Digest(0x11), snapshot.Images[0].Sha256ToArray());
        Assert.Throws<NotSupportedException>(() => ((System.Collections.Generic.IList<ImageReference>)snapshot.Images).Clear());
    }
}
