using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Packages;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The decoded-memory limit must count what the game's decoder really keeps: a 16-bit colour PNG
/// is uploaded at 16 bits per channel (8 bytes per pixel), so the 128 MiB ceiling is reached at
/// half the megapixels an 8-bit image is allowed. 8-bit inspection stays exactly as it was.
/// </summary>
public class ImageSafety16BitTests
{
    private const uint LimitWidth = 8192;
    private const uint LimitHeight = 4096; // 8192 x 4096 = MaxPixelCount exactly

    [Fact]
    public void SixteenBitRgba_AtThePixelLimit_IsRejected_WithAPlayerFacingReason()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "deep.png", ImageTestSupport.SixteenBitRgbaPng(LimitWidth, LimitHeight)))!;

        Assert.Equal(ImageSafety.MaxPixelCount, inspection.PixelCount);
        Assert.Equal(ImageSafety.SixteenBitBytesPerPixel, inspection.BytesPerPixel);
        Assert.Equal(inspection.PixelCount * 8, inspection.EstimatedDecodedBytes);
        Assert.True(inspection.EstimatedDecodedBytes > ImageSafety.MaxDecodedBytes);

        var reason = ImageSafety.Validate(inspection);
        Assert.NotNull(reason);
        Assert.Contains("16-bit", reason, StringComparison.Ordinal);
        Assert.Contains("megapixels", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(dir.Path, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SixteenBitRgba_AtHalfThePixelLimit_IsAccepted()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "deep.png", ImageTestSupport.SixteenBitRgbaPng(4096, 4096)))!;

        Assert.Equal(ImageSafety.MaxDecodedBytes, inspection.EstimatedDecodedBytes);
        Assert.Null(ImageSafety.Validate(inspection));
    }

    [Fact]
    public void SixteenBitRgba_JustOverHalfThePixelLimit_IsRejected()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "deep.png", ImageTestSupport.SixteenBitRgbaPng(4097, 4096)))!;

        Assert.Contains("16-bit", ImageSafety.Validate(inspection), StringComparison.Ordinal);
    }

    [Fact]
    public void EightBitRgba_AtThePixelLimit_IsStillAccepted()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "wide.png", TestImages.Png((int)LimitWidth, (int)LimitHeight)))!;

        Assert.Equal(ImageSafety.DefaultBytesPerPixel, inspection.BytesPerPixel);
        Assert.Equal(inspection.PixelCount * 4, inspection.EstimatedDecodedBytes);
        Assert.Null(ImageSafety.Validate(inspection));
    }

    [Fact]
    public void EightBitRgba_OverThePixelLimit_KeepsTheMegapixelMessage()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "big.png", TestImages.Png((int)LimitWidth, (int)LimitHeight + 1)))!;

        var reason = ImageSafety.Validate(inspection)!;
        Assert.StartsWith("The image has too many pixels", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("16-bit", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SixteenBitGreyscaleWithAlpha_AtThePixelLimit_IsRejected()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "ga.png", ImageTestSupport.Png(LimitWidth, LimitHeight, 16, ImageTestSupport.GreyscaleAlpha)))!;

        Assert.Equal(ImageSafety.SixteenBitBytesPerPixel, inspection.BytesPerPixel);
        Assert.Contains("16-bit", ImageSafety.Validate(inspection), StringComparison.Ordinal);
    }

    /// <summary>
    /// 48-bit RGB is converted to 32-bit RGBA by the decoder, but the wide frame is still
    /// materialized on the way, so it is deliberately counted at the 16-bit rate.
    /// </summary>
    [Fact]
    public void SixteenBitTruecolour_AtThePixelLimit_IsRejected()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "rgb.png", ImageTestSupport.Png(LimitWidth, LimitHeight, 16, ImageTestSupport.Truecolour)))!;

        Assert.Equal(ImageSafety.SixteenBitBytesPerPixel, inspection.BytesPerPixel);
        Assert.Contains("16-bit", ImageSafety.Validate(inspection), StringComparison.Ordinal);
    }

    [Fact]
    public void SixteenBitGreyscale_CountsTheDefaultRate_AndIsAcceptedAtThePixelLimit()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "grey.png", ImageTestSupport.Png(LimitWidth, LimitHeight, 16, ImageTestSupport.Greyscale)))!;

        Assert.Equal(ImageSafety.DefaultBytesPerPixel, inspection.BytesPerPixel);
        Assert.Null(ImageSafety.Validate(inspection));
    }

    [Theory]
    [InlineData(8, ImageTestSupport.TruecolourAlpha, 4)]
    [InlineData(8, ImageTestSupport.Truecolour, 4)]
    [InlineData(16, ImageTestSupport.TruecolourAlpha, 8)]
    [InlineData(16, ImageTestSupport.Truecolour, 8)]
    [InlineData(16, ImageTestSupport.GreyscaleAlpha, 8)]
    [InlineData(16, ImageTestSupport.Greyscale, 4)]
    [InlineData(4, 3, 4)]
    public void BytesPerPixel_FollowsBitDepthAndColourType(byte bitDepth, byte colourType, int expected)
    {
        Assert.Equal(expected, ImageSafety.BytesPerPixelOf(new PngHeader(10, 10, bitDepth, colourType)));
    }

    [Fact]
    public void PngHeader_SurfacesBitDepthAndColourType()
    {
        Assert.True(ImageDimensionReader.TryReadPng(ImageTestSupport.Png(640, 360, 16, ImageTestSupport.Truecolour), out var png));

        Assert.Equal(new PngHeader(640, 360, 16, ImageTestSupport.Truecolour), png);
    }

    [Fact]
    public void JpegAndWebP_CountTheDefaultRate()
    {
        using var dir = new TempDirectory();

        var jpeg = ImageSafety.Inspect(TestImages.Write(dir.Path, "a.jpg", TestImages.Jpeg(64, 48)))!;
        var webp = ImageSafety.Inspect(TestImages.Write(dir.Path, "a.webp", TestImages.WebPExtended(64, 48)))!;

        Assert.Equal(ImageSafety.DefaultBytesPerPixel, jpeg.BytesPerPixel);
        Assert.Equal(ImageSafety.DefaultBytesPerPixel, webp.BytesPerPixel);
    }

    [Fact]
    public void HeaderTruncatedBeforeTheColourType_ReadsNoDimensions()
    {
        using var dir = new TempDirectory();
        var path = TestImages.Write(dir.Path, "cut.png", ImageTestSupport.Png(64, 48)[..25]);

        Assert.Null(ImageDimensionReader.TryReadDimensions(path));
        Assert.False(ImageDimensionReader.TryReadPng(ImageTestSupport.Png(64, 48).AsSpan(0, 25), out _));
        Assert.True(ImageDimensionReader.TryReadPng(ImageTestSupport.Png(64, 48).AsSpan(0, 26), out _));
        Assert.Contains("couldn't be read", ImageSafety.Validate(ImageSafety.Inspect(path)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x80000000u, 10u)]
    [InlineData(10u, 0x80000000u)]
    [InlineData(uint.MaxValue, uint.MaxValue)]
    public void DimensionsPastIntMaxValue_ReadAsNoDimensions_NeverNegative(uint width, uint height)
    {
        using var dir = new TempDirectory();
        var path = TestImages.Write(dir.Path, "absurd.png", ImageTestSupport.Png(width, height));

        Assert.Null(ImageDimensionReader.TryReadDimensions(path));
        Assert.False(ImageDimensionReader.TryReadPng(ImageTestSupport.Png(width, height), out _));

        var inspection = ImageSafety.Inspect(path)!;
        Assert.Equal(0, inspection.Width);
        Assert.Equal(0, inspection.Height);
        Assert.NotNull(ImageSafety.Validate(inspection));
    }

    [Fact]
    public void DimensionsAtIntMaxValue_StillRead_AndAreRejectedByTheSideLimit()
    {
        using var dir = new TempDirectory();
        var path = TestImages.Write(dir.Path, "edge.png", ImageTestSupport.Png(int.MaxValue, 1));

        Assert.Equal((int.MaxValue, 1), ImageDimensionReader.TryReadDimensions(path));
        Assert.Contains("pixels per side", ImageSafety.Validate(ImageSafety.Inspect(path)), StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RefusesASixteenBitImageAtThePixelLimit_AndAcceptsAnEightBitOne()
    {
        using var dir = new TempDirectory();
        var paths = new PlateStoragePaths(dir.Path);
        var storage = new AssetStorageService(paths.AssetsDirectory, paths.AssetStagingDirectory, new AssetMetadataStore(paths.AssetMetadataDirectory));
        var deep = TestImages.Write(Path.Combine(dir.Path, "src"), "deep.png", ImageTestSupport.SixteenBitRgbaPng(LimitWidth, LimitHeight));
        var wide = TestImages.Write(Path.Combine(dir.Path, "src"), "wide.png", TestImages.Png((int)LimitWidth, (int)LimitHeight));

        var error = Assert.Throws<InvalidOperationException>(() => storage.ImportImage(deep));
        Assert.Contains("16-bit", error.Message, StringComparison.Ordinal);
        Assert.Empty(storage.ListAssets());

        var accepted = storage.ImportImage(wide);
        Assert.NotNull(storage.ResolveAssetPath(accepted));
    }

    [Fact]
    public async Task Package_WithASixteenBitImageAtThePixelLimit_IsRefusedAsImageInvalid()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();

        var assetId = fixture.AddImage(TestImages.Png(64, 48));
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Small");
        var document = library.OpenDocumentForEditing(created.PlateId);
        document.Elements.Add(new ImageProfileElement { AssetId = assetId, Position = new Vector2(10, 10), Size = new Vector2(64, 48) });
        await library.SavePlateDocumentAsync(document);
        var valid = fixture.Export(packages, created.PlateId, "valid.aetherframe");
        var manifest = PackageFiles.Json(PackageFiles.Entry(PackageFiles.Read(valid), PackagePaths.ManifestPath));
        var assetEntry = PackagePaths.AssetPath(Guid.ParseExact(manifest["assets"]![0]!["id"]!.GetValue<string>(), "N"), ".png");

        string Craft(byte[] image) => PackageFiles.Rewrite(valid, entries =>
        {
            PackageFiles.Entry(entries, assetEntry).Bytes = image;
            PackageFiles.EditManifest(entries, m =>
            {
                m["assets"]![0]!["width"] = (int)LimitWidth;
                m["assets"]![0]!["height"] = (int)LimitHeight;
            });
        });

        var before = fixture.SnapshotInstallation();
        var deep = packages.Inspect(Craft(ImageTestSupport.SixteenBitRgbaPng(LimitWidth, LimitHeight)));
        try
        {
            Assert.False(deep.CanImport, "a 16-bit 32 megapixel image was importable: " + deep.DescribeForLog());
            Assert.Contains(deep.Diagnostics.Errors, e => e.Code == PackageErrorCode.ImageInvalid);
            Assert.Contains(deep.Diagnostics.Errors, e => e.Message.Contains("16-bit", StringComparison.Ordinal));
            Assert.All(deep.Diagnostics.Errors, e => Assert.DoesNotContain(fixture.Paths.Root, e.Message, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            deep.Dispose();
        }

        Assert.Equal(before, fixture.SnapshotInstallation());
        Assert.True(fixture.StagingIsEmpty);

        // The same size at 8 bits is within the limit, so the refusal is about depth, not size.
        var wide = packages.Inspect(Craft(TestImages.Png((int)LimitWidth, (int)LimitHeight)));
        try
        {
            Assert.True(wide.CanImport, wide.DescribeForLog());
        }
        finally
        {
            wide.Dispose();
        }
    }
}
