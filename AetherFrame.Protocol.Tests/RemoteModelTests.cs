using System;
using System.Linq;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>The remote models refuse every value outside the limits when they are built, so a model that exists can always be encoded.</summary>
public class RemoteModelTests
{
    [Fact]
    public void Snapshot_RefusesEmptyIdsAndOutOfRangeTimestamps()
    {
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ProfileSnapshot(default, Samples.Revision, Samples.CreatedAt, "n", []));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ProfileSnapshot(Samples.Profile, default, Samples.CreatedAt, "n", []));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ProfileSnapshot(Samples.Profile, Samples.Revision, -1, "n", []));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ProfileSnapshot(Samples.Profile, Samples.Revision, ProtocolLimits.MaxUnixSeconds + 1, "n", []));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ProfileRetraction(default, Samples.IssuedAt));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ProfileRetraction(Samples.Profile, long.MaxValue));

        var edge = new ProfileSnapshot(Samples.Profile, Samples.Revision, ProtocolLimits.MaxUnixSeconds, "n", []);
        Assert.Equal(new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero), edge.CreatedAt);
        Assert.Equal(DateTimeOffset.UnixEpoch, new ProfileRetraction(Samples.Profile, 0).IssuedAt);
    }

    [Fact]
    public void Snapshot_RefusesBadTextAndNullArguments()
    {
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "a\0b", []));
        ProtocolAssert.Throws(ProtocolError.InvalidText, () => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "\ud800", []));
        ProtocolAssert.Throws(ProtocolError.InvalidLength, () => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "", []));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, new string('x', ProtocolLimits.MaxNameScalars + 1), []));
        Assert.Throws<ArgumentNullException>(() => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, null!, []));
        Assert.Throws<ArgumentNullException>(() => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "n", null!));
        Assert.Equal(ProtocolLimits.MaxNameScalars, new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, new string('x', ProtocolLimits.MaxNameScalars), []).Name.Length);
    }

    [Fact]
    public void Snapshot_SortsImagesAndRefusesDuplicatesCountAndTotalBytes()
    {
        var sorted = new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "n", [Samples.Image(Samples.Asset2), Samples.Image(Samples.Asset1)]);
        Assert.Equal([Samples.Asset1, Samples.Asset2], sorted.Images.Select(i => i.AssetId));

        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "n", [Samples.Image(Samples.Asset1), Samples.Image(Samples.Asset1, 0x22)]));

        var nine = Enumerable.Range(1, 9).Select(i => Samples.Image(AssetId.Parse("ast_" + i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "n", nine));
        Assert.Equal(8, new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "n", nine.Take(8)).Images.Count);

        var full = Enumerable.Range(1, 5).Select(i => Samples.Image(AssetId.Parse("ast_" + i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)), bytes: ProtocolLimits.MaxImageBytes)).ToArray();
        Assert.Equal(5 * ProtocolLimits.MaxImageBytes, new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "n", full).TotalImageBytes);
        var over = full.Append(Samples.Image(AssetId.Parse("ast_" + 6.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)), bytes: 1)).ToArray();
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "n", over));
    }

    [Fact]
    public void ImageReference_RefusesEveryValueOutsideItsLimits()
    {
        var digest = Samples.Digest(0x11);
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ImageReference(default, digest, ImageFormat.Png, 1, 1, 1));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ImageReference(Samples.Asset1, new byte[31], ImageFormat.Png, 1, 1, 1));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ImageReference(Samples.Asset1, new byte[33], ImageFormat.Png, 1, 1, 1));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ImageReference(Samples.Asset1, new byte[32], ImageFormat.Png, 1, 1, 1));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ImageReference(Samples.Asset1, digest, (ImageFormat)0, 1, 1, 1));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ImageReference(Samples.Asset1, digest, (ImageFormat)4, 1, 1, 1));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ImageReference(Samples.Asset1, digest, ImageFormat.Png, 0, 1, 1));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ImageReference(Samples.Asset1, digest, ImageFormat.Png, -1, 1, 1));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => new ImageReference(Samples.Asset1, digest, ImageFormat.Png, ProtocolLimits.MaxImageBytes + 1, 1, 1));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ImageReference(Samples.Asset1, digest, ImageFormat.Png, 1, 0, 1));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ImageReference(Samples.Asset1, digest, ImageFormat.Png, 1, 1, 0));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new ImageReference(Samples.Asset1, digest, ImageFormat.Png, 1, -5, 1));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => new ImageReference(Samples.Asset1, digest, ImageFormat.Png, 1, ProtocolLimits.MaxImageDimension + 1, 1));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => new ImageReference(Samples.Asset1, digest, ImageFormat.Png, 1, 1, ProtocolLimits.MaxImageDimension + 1));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => new ImageReference(Samples.Asset1, digest, ImageFormat.Png, 1, 5000, 4001));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => new ImageReference(Samples.Asset1, digest, ImageFormat.Png, 1, ProtocolLimits.MaxImageDimension, ProtocolLimits.MaxImageDimension));

        var edge = new ImageReference(Samples.Asset1, digest, ImageFormat.WebP, ProtocolLimits.MaxImageBytes, 5000, 4000);
        Assert.Equal(20_000_000L, (long)edge.Width * edge.Height);
        Assert.Equal(ProtocolLimits.MaxImageBytes, edge.ByteLength);
        Assert.Equal(digest, edge.Sha256.ToArray());
        Assert.NotNull(new ImageReference(Samples.Asset1, digest, ImageFormat.Png, 1, ProtocolLimits.MaxImageDimension, 2441));
    }
}
