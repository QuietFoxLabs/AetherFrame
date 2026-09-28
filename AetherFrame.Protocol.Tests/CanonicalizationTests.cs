using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Logically identical content always produces identical bytes, whatever the construction order,
/// the culture, the time zone or the platform; and text that differs is never quietly made equal.
/// </summary>
public class CanonicalizationTests
{
    [Fact]
    public void ImageSets_EncodeTheSameInAnyInsertionOrder()
    {
        var ids = Enumerable.Range(1, 8).Select(i => AssetId.Parse("ast_" + i.ToString("x32", CultureInfo.InvariantCulture))).ToArray();
        var images = ids.Select((id, index) => Samples.Image(id, (byte)(index + 1), ImageFormat.WebP, 100 + index, 10 + index, 20 + index)).ToArray();

        var expected = new ProfileSnapshot(Samples.Profile, Samples.Revision, 5, "n", images).EncodePayload();
        var random = new Random(20260927);
        for (var round = 0; round < 50; round++)
        {
            var shuffled = images.OrderBy(_ => random.Next()).ToArray();
            Assert.Equal(expected, new ProfileSnapshot(Samples.Profile, Samples.Revision, 5, "n", shuffled).EncodePayload());
        }

        Assert.Equal(expected, new ProfileSnapshot(Samples.Profile, Samples.Revision, 5, "n", images.Reverse()).EncodePayload());
    }

    [Theory]
    [InlineData("")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("th-TH")]
    public void Encoding_DoesNotDependOnTheCurrentCulture(string cultureName)
    {
        var expected = Samples.Snapshot().EncodePayload();
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Assert.Equal(expected, Samples.Snapshot().EncodePayload());
            Assert.Equal(expected, new ProfileSnapshot(Samples.Profile, Samples.Revision, Samples.CreatedAt, "Sample Plate", [Samples.Image(Samples.Asset2, 0x22, ImageFormat.Jpeg, 5678, 1920, 1080), Samples.Image(Samples.Asset1)]).EncodePayload());
            Assert.Equal("prf_a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", Samples.Profile.ToString());
            Assert.Equal(Samples.Profile, ProfileId.Parse("prf_a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"));
            var message = ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => new ImageReference(Samples.Asset1, Samples.Digest(1), ImageFormat.Png, 1, 8193, 1)).Message;
            Assert.Contains("8193", message, StringComparison.Ordinal);
            Assert.Contains("8192", message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous.CurrentCulture;
            CultureInfo.CurrentUICulture = previous.CurrentUICulture;
        }
    }

    [Fact]
    public void Timestamps_AreTheSameInstantFromAnyOffset()
    {
        var utc = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var tokyo = new DateTimeOffset(2026, 9, 27, 21, 0, 0, TimeSpan.FromHours(9));
        var honolulu = new DateTimeOffset(2026, 9, 27, 2, 0, 0, TimeSpan.FromHours(-10));
        Assert.Equal(utc.ToUnixTimeSeconds(), tokyo.ToUnixTimeSeconds());
        Assert.Equal(utc.ToUnixTimeSeconds(), honolulu.ToUnixTimeSeconds());

        var expected = new ProfileSnapshot(Samples.Profile, Samples.Revision, utc.ToUnixTimeSeconds(), "", []).EncodePayload();
        Assert.Equal(expected, new ProfileSnapshot(Samples.Profile, Samples.Revision, tokyo.ToUnixTimeSeconds(), "", []).EncodePayload());
        Assert.Equal(expected, new ProfileSnapshot(Samples.Profile, Samples.Revision, honolulu.ToUnixTimeSeconds(), "", []).EncodePayload());
        Assert.Equal(utc, new ProfileSnapshot(Samples.Profile, Samples.Revision, honolulu.ToUnixTimeSeconds(), "", []).CreatedAt);
    }

    [Fact]
    public void Encoding_IsStableAcrossRepeatedSerialization()
    {
        var first = Samples.Snapshot().EncodePayload();
        for (var round = 0; round < 1000; round++)
        {
            Assert.Equal(first, Samples.Snapshot().EncodePayload());
        }

        using var signer = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(signer);
        for (var round = 0; round < 100; round++)
        {
            var verified = SignedDocumentCodec.Verify(document);
            Assert.Equal(first, verified.Document.EncodePayload());
        }
    }

    [Fact]
    public void Text_IsNeverNormalized()
    {
        var composed = "Caf\u00e9";
        var decomposed = "Cafe\u0301";
        Assert.Equal(composed, decomposed.Normalize(NormalizationForm.FormC));
        Assert.NotEqual(Encode(composed), Encode(decomposed));

        Assert.NotEqual(Encode("a\r\nb"), Encode("a\nb"));
        Assert.NotEqual(Encode("a\nb"), Encode("a\rb"));
        Assert.NotEqual(Encode(" a"), Encode("a"));
        Assert.NotEqual(Encode("a "), Encode("a"));
        Assert.NotEqual(Encode("\ufeffa"), Encode("a"));
        Assert.NotEqual(Encode("A"), Encode("a"));
        Assert.NotEqual(Encode("\u200bx"), Encode("x"));

        foreach (var text in new[] { composed, decomposed, "a\r\nb", "\ufeffa", "\u200b", "\U0001F600\U0001F3FD", "\u0301", "\u202ea", "\t\n\r" })
        {
            using var signer = TestPersonas.CreateA();
            var verified = SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, text, []), signer));
            Assert.Equal(text, Assert.IsType<ProfileSnapshot>(verified.Document).Name);
        }
    }

    [Fact]
    public void Encoding_UsesNoPlatformDependentValues()
    {
        // A line break inside a name is content: "\n" is the one byte 0x0A whatever the platform's
        // newline is, "\r\n" is two bytes, and the schema version is big-endian on every machine.
        // (The name starts at offset 42 of the payload: schema, two ids and the timestamp.)
        var lineFeed = Encode("a\nb");
        Assert.Equal(new byte[] { 0, 0, 0, 3, (byte)'a', 0x0A, (byte)'b' }, lineFeed.AsSpan(42, 7).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 4, (byte)'a', 0x0D, 0x0A, (byte)'b' }, Encode("a\r\nb").AsSpan(42, 8).ToArray());
        Assert.Equal(new byte[] { 0x00, 0x01 }, lineFeed.AsSpan(0, 2).ToArray());
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x65, 0x53, 0xF1, 0x00 }, Samples.Snapshot().EncodePayload().AsSpan(34, 8).ToArray());
    }

    private static byte[] Encode(string text) => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, text, []).EncodePayload();
}
