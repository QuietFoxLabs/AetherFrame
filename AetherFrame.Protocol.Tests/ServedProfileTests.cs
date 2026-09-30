using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Requests;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// The served profile (docs/networking/ProtocolSpecification-v1.md, section 8.6; decision D6): what
/// the server builds from a verified snapshot, what it leaves out, and how strictly a viewer reads it.
/// </summary>
public class ServedProfileTests
{
    private static readonly RevisionMarker Marker = ReferenceServed.Marker;

    [Fact]
    public void Build_TheRichSnapshot_ReadsBackItsLayoutWithImagesByIndex()
    {
        var snapshot = LayoutSamples.Rich();
        var served = ServedProfile.Read(ServedProfile.Build(snapshot, Marker));

        Assert.Equal(Marker, served.Marker);
        Assert.Equal(snapshot.Name, served.Name);
        Assert.Equal(snapshot.CanvasWidth, served.CanvasWidth);
        Assert.Equal(snapshot.CanvasHeight, served.CanvasHeight);
        Assert.Equal(snapshot.TotalTextScalars, served.TotalTextScalars);
        Assert.Equal(snapshot.Items.Select(i => i.Kind), served.Items.Select(i => i.Kind));
        Assert.Equal("Aria Starfall", Assert.IsType<LayoutText>(served.Items[0]).Text);

        // The snapshot's images in ascending asset id order become indexes 0 and 1.
        Assert.Equal(ServedProfile.ImageKey(0), served.Background.ImageAssetId);
        Assert.Equal(1, served.ImageIndexOf(Assert.IsType<LayoutImage>(served.Items[1]).AssetId));
        Assert.Equal(0, served.ImageIndexOf(Assert.IsType<LayoutImageQuad>(served.Items[4]).AssetId));
        Assert.Equal(
            snapshot.Images.Select(i => (i.Format, i.Width, i.Height)),
            served.Images.Select(i => (i.Format, i.Width, i.Height)));
    }

    [Fact]
    public void Build_CarriesNoIdentifierTimeDigestOrLength()
    {
        var snapshot = LayoutSamples.Rich();
        var body = ServedProfile.Build(snapshot, Marker);
        var payload = snapshot.EncodePayload();

        var forbidden = new List<(string What, byte[] Bytes)>
        {
            ("the profile id", snapshot.ProfileId.ToArray()),
            ("the revision id", snapshot.RevisionId.ToArray()),
            ("createdAt", BigEndian((ulong)snapshot.CreatedAtUnixSeconds)),
        };
        foreach (var image in snapshot.Images)
        {
            forbidden.Add(("an asset id", image.AssetId.ToArray()));
            forbidden.Add(("an image digest", image.Sha256.ToArray()));
            forbidden.Add(("an image byte length", BigEndian((ulong)image.ByteLength)));
        }

        foreach (var (what, bytes) in forbidden)
        {
            Assert.True(body.AsSpan().IndexOf(bytes) < 0, "the served profile holds " + what);
        }

        // Smaller than its payload even with the 22-byte header: the size bound of section 8.6.
        Assert.True(body.Length < payload.Length, $"{body.Length} bytes served for a payload of {payload.Length}");
    }

    [Fact]
    public void Build_TheMinimalSnapshot_HasNoImages()
    {
        var served = ServedProfile.Read(ServedProfile.Build(LayoutSamples.Minimal(), Marker));
        Assert.Empty(served.Images);
        Assert.Empty(served.Items);
        Assert.True(served.Background.ImageAssetId.IsEmpty);
    }

    [Fact]
    public void Build_WithoutAMarker_IsRefused()
    {
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => ServedProfile.Build(LayoutSamples.Rich(), default));
        Assert.Throws<ArgumentNullException>(() => ServedProfile.Build(null!, Marker));
    }

    [Fact]
    public void Build_UsesAFreshMarkerOnlyWhenGivenOne()
    {
        var a = RevisionMarker.NewMarker();
        var b = RevisionMarker.NewMarker();
        Assert.NotEqual(a, b);
        Assert.False(a.IsEmpty);
        Assert.Equal(a, ServedProfile.Read(ServedProfile.Build(LayoutSamples.Rich(), a)).Marker);
        Assert.Equal(a, RevisionMarker.Parse(a.ToString()));
        Assert.StartsWith(RevisionMarker.Prefix, a.ToString(), StringComparison.Ordinal);
        Assert.False(RevisionMarker.TryParse("mrk_" + new string('0', 32), out _));
        Assert.False(RevisionMarker.TryParse("mrk_" + new string('A', 32), out _));
        Assert.False(RevisionMarker.TryParse("ast_" + new string('a', 32), out _));
    }

    [Fact]
    public void ImageKeys_AreDistinctAndNamedBackByIndexOnly()
    {
        var keys = Enumerable.Range(0, ProtocolLimits.MaxImagesPerProfile).Select(ServedProfile.ImageKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, key => Assert.False(key.IsEmpty));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServedProfile.ImageKey(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServedProfile.ImageKey(ProtocolLimits.MaxImagesPerProfile));

        var served = ServedProfile.Read(ServedProfile.Build(LayoutSamples.Rich(), Marker));
        Assert.Equal(-1, served.ImageIndexOf(ServedProfile.ImageKey(2)));
        Assert.Equal(-1, served.ImageIndexOf(Samples.Asset1));
    }

    [Fact]
    public void Read_NeverReadsASignedDocumentOrARequestProof_NorTheReverse()
    {
        using var signer = TestPersonas.CreateA();
        var document = SignedDocumentCodec.Sign(LayoutSamples.Rich(), signer);
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => ServedProfile.Read(document));
        var proof = RequestProofCodec.SignAction(RequestProofKind.Lookup, "{}"u8, DeploymentName.Parse("plates.example.com"), RequestChallenge.Parse(ProofSamples.ChallengeText), signer);
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => ServedProfile.Read(proof));

        var served = ServedProfile.Build(LayoutSamples.Rich(), Marker);
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => SignedDocumentCodec.Verify(served));

        // A body short enough to pass a proof's size limit still isn't one.
        var minimal = ServedProfile.Build(LayoutSamples.Minimal(), Marker);
        Assert.True(minimal.Length <= ProtocolLimits.MaxRequestProofBytes);
        ProtocolAssert.Throws(ProtocolError.InvalidFraming, () => RequestProofCodec.VerifyAction(minimal, "{}"u8, DeploymentName.Parse("plates.example.com"), RequestProofKind.Lookup));
    }

    [Fact]
    public void Read_OverTheSizeLimit_IsRefusedBeforeParsing_AndTheLimitItselfIsRead()
    {
        var valid = ServedProfile.Build(LayoutSamples.Rich(), Marker);
        var over = new byte[ProtocolLimits.MaxServedProfileBytes + 1];
        valid.CopyTo(over, 0);
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => ServedProfile.Read(over));

        // At the limit the body is read, and fails only on what follows the valid profile.
        ProtocolAssert.Throws(ProtocolError.TrailingBytes, () => ServedProfile.Read(over.AsSpan(0, ProtocolLimits.MaxServedProfileBytes)));
    }

    [Fact]
    public void Read_WithTwoFaults_RefusesForTheEarlierInReadingOrder()
    {
        static ProtocolException Refusal(Action<ReferenceServed.ServedSpec> change)
        {
            var spec = new ReferenceServed.ServedSpec();
            change(spec);
            return Assert.Throws<ProtocolException>(() => ServedProfile.Read(ReferenceServed.Write(spec)));
        }

        // An index is checked as soon as it is read, before the counts and the end that follow it.
        Assert.Equal(ProtocolError.InvalidValue, Refusal(s => { s.BackgroundImage = 8; s.ItemCount = 2049; }).Error);
        Assert.Equal(ProtocolError.InvalidValue, Refusal(s => { s.BackgroundImage = 8; s.Trailing = [0]; }).Error);

        // The version before the marker, and the marker before the name.
        Assert.Equal(ProtocolError.UnsupportedVersion, Refusal(s => { s.Version = 2; s.Marker = new byte[16]; }).Error);
        Assert.Equal(ProtocolError.InvalidValue, Refusal(s => { s.Marker = new byte[16]; s.Name = ""; }).Error);

        // Trailing bytes before the rules over the whole body, and those rules in their order.
        Assert.Equal(ProtocolError.TrailingBytes, Refusal(s => { s.Items = RichItemsNaming(2); s.Trailing = [0]; }).Error);
        Assert.Equal(ProtocolError.LimitExceeded, Refusal(s =>
        {
            s.Items = [.. RichItemsNaming(2), .. Enumerable.Repeat<Action<ReferenceLayout.Bytes>>(w => ReferenceServed.Text(w, new string('a', 2048)), 16)];
        }).Error);

        // Both limits are LimitExceeded, so the message tells which rule came first: the pixels.
        var pixelsFirst = Refusal(s =>
        {
            s.Items = [.. ReferenceServed.RichItems(imageIndex: 1, quadIndex: 2), .. Enumerable.Repeat<Action<ReferenceLayout.Bytes>>(w => ReferenceServed.Text(w, new string('a', 2048)), 16)];
            s.Images = [(1, 8192, 2048), (2, 8192, 2048), (1, 8192, 2048)];
        });
        Assert.Equal(ProtocolError.LimitExceeded, pixelsFirst.Error);
        Assert.Contains("pixels", pixelsFirst.Message, StringComparison.Ordinal);

        static List<Action<ReferenceLayout.Bytes>> RichItemsNaming(byte index) => ReferenceServed.RichItems(imageIndex: index, quadIndex: 1);
    }

    [Fact]
    public void Read_TheRichReference_IsWhatBuildWrites()
    {
        Assert.Equal(ReferenceServed.Write(new ReferenceServed.ServedSpec()), ServedProfile.Build(LayoutSamples.Rich(), Marker));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SeededMutations_AreRefusedOrReadToTheirOwnBytes(int seed)
    {
        var bases = new[]
        {
            ServedProfile.Build(LayoutSamples.Rich(), Marker),
            ServedProfile.Build(LayoutSamples.Minimal(), Marker),
        };

        var random = new Random(seed * 7_919);
        var refused = 0;
        for (var index = 0; index < 3000; index++)
        {
            var body = bases[random.Next(bases.Length)];
            var mutated = Mutator.ApplyGeneric(random, body, out var strategy);
            ServedProfile served;
            try
            {
                served = ServedProfile.Read(mutated);
            }
            catch (ProtocolException)
            {
                refused++;
                continue;
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed} case {index} ({strategy}) was not refused cleanly: {e.Message}");
            }

            // Whatever is accepted has exactly one encoding: the bytes it was read from.
            Assert.True(served.Encode().AsSpan().SequenceEqual(mutated), $"seed {seed} case {index} ({strategy}) read to other bytes");
        }

        Assert.True(refused > 1000, $"only {refused} mutations were refused");
    }

    private static byte[] BigEndian(ulong value)
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }
}
