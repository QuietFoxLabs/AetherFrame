using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Encoding;
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

        var expected = new ProfileSnapshot(Samples.Profile, Samples.Revision, utc.ToUnixTimeSeconds(), "n", []).EncodePayload();
        Assert.Equal(expected, new ProfileSnapshot(Samples.Profile, Samples.Revision, tokyo.ToUnixTimeSeconds(), "n", []).EncodePayload());
        Assert.Equal(expected, new ProfileSnapshot(Samples.Profile, Samples.Revision, honolulu.ToUnixTimeSeconds(), "n", []).EncodePayload());
        Assert.Equal(utc, new ProfileSnapshot(Samples.Profile, Samples.Revision, honolulu.ToUnixTimeSeconds(), "n", []).CreatedAt);
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
    public void Names_AreNeverNormalized()
    {
        // Decision D4: a name refuses controls and invisible format characters, but every name it
        // accepts is kept exactly: no normalization form, no trimming, no case folding.
        var composed = "Caf\u00e9";
        var decomposed = "Cafe\u0301";
        Assert.Equal(composed, decomposed.Normalize(NormalizationForm.FormC));
        Assert.NotEqual(Encode(composed), Encode(decomposed));
        Assert.NotEqual(Encode(" a"), Encode("a"));
        Assert.NotEqual(Encode("a "), Encode("a"));
        Assert.NotEqual(Encode("A"), Encode("a"));

        foreach (var text in new[] { composed, decomposed, " a ", "\U0001F600\U0001F3FD", "\u0301", "\u05e9\u05dc\u05d5\u05dd", "a  b" })
        {
            using var signer = TestPersonas.CreateA();
            var verified = SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, text, []), signer));
            Assert.Equal(text, Assert.IsType<ProfileSnapshot>(verified.Document).Name);
        }
    }

    [Fact]
    public void Text_IsNeverNormalized()
    {
        // The general text rules (docs/networking/ProtocolSpecification-v1.md, section 2.3), which
        // schema 2's texts use: line breaks, byte order marks and format characters are content.
        Assert.NotEqual(ProtocolText.Encode("a\r\nb", "t"), ProtocolText.Encode("a\nb", "t"));
        Assert.NotEqual(ProtocolText.Encode("a\nb", "t"), ProtocolText.Encode("a\rb", "t"));
        Assert.NotEqual(ProtocolText.Encode("\ufeffa", "t"), ProtocolText.Encode("a", "t"));
        Assert.NotEqual(ProtocolText.Encode("\u200bx", "t"), ProtocolText.Encode("x", "t"));

        foreach (var text in new[] { "a\r\nb", "\ufeffa", "\u200b", "\u202ea", "\t\n\r", "Cafe\u0301" })
        {
            Assert.Equal(text, ProtocolText.Decode(ProtocolText.Encode(text, "t"), "t"));
        }
    }

    [Fact]
    public void Encoding_UsesNoPlatformDependentValues()
    {
        // A line break inside a text is content: "\n" is the one byte 0x0A whatever the platform's
        // newline is, "\r\n" is two bytes, and lengths and the schema version are big-endian on
        // every machine. (A snapshot's name starts at offset 42 of the payload: schema, two ids and
        // the timestamp.)
        Assert.Equal(new byte[] { (byte)'a', 0x0A, (byte)'b' }, ProtocolText.Encode("a\nb", "t"));
        Assert.Equal(new byte[] { (byte)'a', 0x0D, 0x0A, (byte)'b' }, ProtocolText.Encode("a\r\nb", "t"));
        var name = Encode("a\u00e9b");
        Assert.Equal(new byte[] { 0, 0, 0, 4, (byte)'a', 0xC3, 0xA9, (byte)'b' }, name.AsSpan(42, 8).ToArray());
        Assert.Equal(new byte[] { 0x00, 0x01 }, name.AsSpan(0, 2).ToArray());
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x65, 0x53, 0xF1, 0x00 }, Samples.Snapshot().EncodePayload().AsSpan(34, 8).ToArray());
    }

    private static byte[] Encode(string text) => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, text, []).EncodePayload();
}
