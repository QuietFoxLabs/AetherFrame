using System;
using System.Linq;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Authentic documents (signed by a real key) whose payload breaks the schema: a valid signature
/// buys nothing, the payload decoder refuses each fault by itself.
/// </summary>
public class AdversarialPayloadTests
{
    [Fact]
    public void HandBuiltSamplePayload_MatchesTheModelByteForByte()
    {
        Assert.Equal(Samples.Snapshot().EncodePayload(), PayloadBuilder.Snapshot());
        Assert.Equal(Samples.Retraction().EncodePayload(), PayloadBuilder.Retraction());
    }

    [Theory]
    [InlineData("schema 0", ProtocolError.UnsupportedVersion)]
    [InlineData("schema 2", ProtocolError.UnsupportedVersion)]
    [InlineData("zero profile id", ProtocolError.InvalidValue)]
    [InlineData("zero revision id", ProtocolError.InvalidValue)]
    [InlineData("createdAt over max", ProtocolError.InvalidValue)]
    [InlineData("createdAt u64 max", ProtocolError.InvalidValue)]
    [InlineData("name with NUL", ProtocolError.InvalidText)]
    [InlineData("name invalid utf8", ProtocolError.InvalidText)]
    [InlineData("name overlong utf8", ProtocolError.InvalidText)]
    [InlineData("name encoded surrogate", ProtocolError.InvalidText)]
    [InlineData("name one scalar over max", ProtocolError.LimitExceeded)]
    [InlineData("name one byte over max bytes", ProtocolError.LimitExceeded)]
    [InlineData("name length claims more than present", ProtocolError.Truncated)]
    [InlineData("name length huge", ProtocolError.LimitExceeded)]
    [InlineData("image count 9 declared", ProtocolError.LimitExceeded)]
    [InlineData("image count huge", ProtocolError.LimitExceeded)]
    [InlineData("image count more than present", ProtocolError.Truncated)]
    [InlineData("image count less than present", ProtocolError.TrailingBytes)]
    [InlineData("images unsorted", ProtocolError.NotCanonical)]
    [InlineData("images duplicate", ProtocolError.NotCanonical)]
    [InlineData("image zero asset id", ProtocolError.InvalidValue)]
    [InlineData("image zero digest", ProtocolError.InvalidValue)]
    [InlineData("image format 0", ProtocolError.InvalidValue)]
    [InlineData("image format 4", ProtocolError.InvalidValue)]
    [InlineData("image zero bytes", ProtocolError.InvalidValue)]
    [InlineData("image bytes over max", ProtocolError.LimitExceeded)]
    [InlineData("image bytes u64 max", ProtocolError.LimitExceeded)]
    [InlineData("image zero width", ProtocolError.InvalidValue)]
    [InlineData("image width over max", ProtocolError.LimitExceeded)]
    [InlineData("image zero height", ProtocolError.InvalidValue)]
    [InlineData("image height over max", ProtocolError.LimitExceeded)]
    [InlineData("image width u32 max", ProtocolError.LimitExceeded)]
    [InlineData("image pixels over max", ProtocolError.LimitExceeded)]
    [InlineData("images total bytes over max", ProtocolError.LimitExceeded)]
    [InlineData("trailing byte", ProtocolError.TrailingBytes)]
    [InlineData("truncated image", ProtocolError.Truncated)]
    [InlineData("nested count abuse", ProtocolError.LimitExceeded)]
    public void SignedButInvalidSnapshotPayloads_AreRefused(string name, ProtocolError expected)
    {
        using var signer = TestPersonas.CreateA();
        var document = PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, SnapshotPayloadCases.Build(name));
        ProtocolAssert.Throws(expected, () => SignedDocumentCodec.Verify(document));
    }

    [Theory]
    [InlineData("schema 2", ProtocolError.UnsupportedVersion)]
    [InlineData("zero profile id", ProtocolError.InvalidValue)]
    [InlineData("issuedAt over max", ProtocolError.InvalidValue)]
    [InlineData("trailing byte", ProtocolError.TrailingBytes)]
    [InlineData("truncated", ProtocolError.Truncated)]
    [InlineData("snapshot payload under retraction type", ProtocolError.InvalidValue)]
    public void SignedButInvalidRetractionPayloads_AreRefused(string name, ProtocolError expected)
    {
        using var signer = TestPersonas.CreateA();
        var payload = name switch
        {
            "schema 2" => PayloadBuilder.Retraction(schema: 2),
            "zero profile id" => PayloadBuilder.Retraction(profileId: new byte[16]),
            "issuedAt over max" => PayloadBuilder.Retraction(issuedAt: (ulong)ProtocolLimits.MaxUnixSeconds + 1),
            "trailing byte" => PayloadBuilder.Retraction(trailing: [0]),
            "truncated" => PayloadBuilder.Retraction().Take(20).ToArray(),
            "snapshot payload under retraction type" => PayloadBuilder.Snapshot(),
            _ => throw new ArgumentException(name),
        };
        var document = PayloadBuilder.Signed(DocumentType.ProfileRetraction, signer, payload);
        ProtocolAssert.Throws(expected, () => SignedDocumentCodec.Verify(document));
    }

    [Theory]
    [InlineData("images unsorted, second digest zero", ProtocolError.NotCanonical)]
    [InlineData("image digest zero, then cut inside width", ProtocolError.InvalidValue)]
    [InlineData("image format 9 and byte length 0", ProtocolError.InvalidValue)]
    [InlineData("image byte length over max and width 0", ProtocolError.LimitExceeded)]
    [InlineData("name with NUL after 32001 scalars", ProtocolError.InvalidText)]
    [InlineData("image count 9 and trailing byte", ProtocolError.LimitExceeded)]
    [InlineData("trailing byte and images totalling over max", ProtocolError.TrailingBytes)]
    public void PayloadsWithSeveralFaults_AreRefusedForTheFirstInSchemaOrder(string name, ProtocolError expected)
    {
        // docs/networking/ProtocolSpecification-v1.md, "Errors": each rule is checked at the earliest
        // point in reading order at which it can be, so the first fault in that order names the error.
        var overMax = Enumerable.Range(1, 6).Select(i => PayloadBuilder.Image(AssetId.Parse("ast_" + i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)), byteLength: (ulong)ProtocolLimits.MaxImageBytes)).ToArray();
        var payload = name switch
        {
            "images unsorted, second digest zero" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset2), PayloadBuilder.Image(Samples.Asset1, digest: new byte[32])]),
            "image digest zero, then cut inside width" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, digest: new byte[32])])[..^6],
            "image format 9 and byte length 0" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, format: 9, byteLength: 0)]),
            "image byte length over max and width 0" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, byteLength: (ulong)ProtocolLimits.MaxImageBytes + 1, width: 0)]),
            "name with NUL after 32001 scalars" => PayloadBuilder.Snapshot(nameBytes: [.. ProtocolConstants.StrictUtf8.GetBytes(new string('a', ProtocolLimits.MaxTextScalars + 1)), 0x00]),
            "image count 9 and trailing byte" => PayloadBuilder.Snapshot(imageCount: 9, trailing: [0]),
            "trailing byte and images totalling over max" => PayloadBuilder.Snapshot(images: overMax, trailing: [0]),
            _ => throw new ArgumentException(name),
        };
        using var signer = TestPersonas.CreateA();
        ProtocolAssert.Throws(expected, () => SignedDocumentCodec.Verify(PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, payload)));
    }

    [Fact]
    public void MaximalSnapshot_RoundTrips_AndFitsTheDocumentLimit()
    {
        using var signer = TestPersonas.CreateA();
        var maximal = PayloadBuilder.MaximalSnapshot();
        var document = SignedDocumentCodec.Sign(maximal, signer);
        Assert.True(document.Length <= ProtocolLimits.MaxDocumentBytes);
        var verified = Assert.IsType<ProfileSnapshot>(SignedDocumentCodec.Verify(document).Document);
        Assert.Equal(maximal.Name, verified.Name);
        Assert.Equal(8, verified.Images.Count);
        Assert.Equal(ProtocolLimits.MaxProfileImageBytes, verified.TotalImageBytes);
        Assert.Equal(maximal.EncodePayload(), verified.EncodePayload());
    }
}
