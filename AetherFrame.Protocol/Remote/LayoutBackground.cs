using System;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// A schema 2 layout's background (docs/networking/ProtocolSpecification-v1.md, section 8.5): what
/// its mode draws, the pattern over it, and, for the image mode, which of the snapshot's images it
/// shows. Every field is always present. Validated on construction; immutable.
/// </summary>
public sealed class LayoutBackground
{
    /// <summary>The smallest pattern scale, in hundredths of a canvas unit.</summary>
    public const int MinTextureScale = 400;

    /// <summary>The largest pattern scale, in hundredths of a canvas unit.</summary>
    public const int MaxTextureScale = 12_800;

    /// <summary>Builds a background, refusing any value outside its range.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public LayoutBackground(
        LayoutBackgroundMode mode,
        LayoutColor primary,
        LayoutColor secondary,
        int gradientAngle,
        byte opacity,
        LayoutTexture texture,
        byte textureIntensity,
        int textureScale,
        int textureRotation,
        AssetId imageAssetId,
        LayoutImageFit imageFit,
        LayoutFlips imageFlips)
    {
        LayoutFields.CheckCode((byte)mode, "background.mode", (byte)LayoutBackgroundMode.Image);
        LayoutFields.CheckAngle(gradientAngle, "background.gradientAngle");
        LayoutFields.CheckCode((byte)texture, "background.texture", (byte)LayoutTexture.Brick);
        LayoutFields.CheckRange(textureScale, MinTextureScale, MaxTextureScale, "background.textureScale");
        LayoutFields.CheckAngle(textureRotation, "background.textureRotation");
        CheckImage(mode, imageAssetId);
        LayoutFields.CheckCode((byte)imageFit, "background.imageFit", (byte)LayoutImageFit.Fill);
        LayoutFields.CheckFlags((byte)imageFlips, "background.imageFlips", (byte)(LayoutFlips.Horizontal | LayoutFlips.Vertical));

        Mode = mode;
        Primary = primary;
        Secondary = secondary;
        GradientAngle = gradientAngle;
        Opacity = opacity;
        Texture = texture;
        TextureIntensity = textureIntensity;
        TextureScale = textureScale;
        TextureRotation = textureRotation;
        ImageAssetId = imageAssetId;
        ImageFit = imageFit;
        ImageFlips = imageFlips;
    }

    /// <summary>A background that draws nothing.</summary>
    public static LayoutBackground None { get; } = new(
        LayoutBackgroundMode.None, default, default, 0, 255, LayoutTexture.None, 0, 2_400, 0, default, LayoutImageFit.Stretch, LayoutFlips.None);

    /// <summary>What the background draws under its pattern.</summary>
    public LayoutBackgroundMode Mode { get; }

    /// <summary>The primary colour: the solid colour, or the gradient's start.</summary>
    public LayoutColor Primary { get; }

    /// <summary>The gradient's end colour.</summary>
    public LayoutColor Secondary { get; }

    /// <summary>The gradient's angle, in hundredths of a degree.</summary>
    public int GradientAngle { get; }

    /// <summary>The whole background's opacity.</summary>
    public byte Opacity { get; }

    /// <summary>The pattern drawn over the mode.</summary>
    public LayoutTexture Texture { get; }

    /// <summary>How strongly the pattern shows.</summary>
    public byte TextureIntensity { get; }

    /// <summary>The pattern's scale, in hundredths of a canvas unit.</summary>
    public int TextureScale { get; }

    /// <summary>The pattern's rotation, in hundredths of a degree.</summary>
    public int TextureRotation { get; }

    /// <summary>The image the image mode shows, one of the snapshot's; empty for every other mode.</summary>
    public AssetId ImageAssetId { get; }

    /// <summary>How the image fills the canvas.</summary>
    public LayoutImageFit ImageFit { get; }

    /// <summary>How the image is mirrored.</summary>
    public LayoutFlips ImageFlips { get; }

    internal void Write(CanonicalWriter writer, LayoutImageNaming naming)
    {
        writer.WriteU8((byte)Mode);
        Primary.Write(writer);
        Secondary.Write(writer);
        writer.WriteI32(GradientAngle);
        writer.WriteU8(Opacity);
        writer.WriteU8((byte)Texture);
        writer.WriteU8(TextureIntensity);
        writer.WriteI32(TextureScale);
        writer.WriteI32(TextureRotation);
        naming.Write(writer, ImageAssetId);
        writer.WriteU8((byte)ImageFit);
        writer.WriteU8((byte)ImageFlips);
    }

    /// <summary>
    /// Reads a background, checking each field as soon as it is read. Its image is named as
    /// <paramref name="naming"/> says.
    /// </summary>
    internal static LayoutBackground Read(ref CanonicalReader reader, LayoutImageNaming naming)
    {
        var mode = (LayoutBackgroundMode)LayoutFields.ReadCode(ref reader, "background.mode", (byte)LayoutBackgroundMode.Image);
        var primary = LayoutColor.Read(ref reader, "background.primary");
        var secondary = LayoutColor.Read(ref reader, "background.secondary");
        var gradientAngle = LayoutFields.ReadAngle(ref reader, "background.gradientAngle");
        var opacity = reader.ReadU8("background.opacity");
        var texture = (LayoutTexture)LayoutFields.ReadCode(ref reader, "background.texture", (byte)LayoutTexture.Brick);
        var intensity = reader.ReadU8("background.textureIntensity");
        var scale = LayoutFields.ReadExtent(ref reader, "background.textureScale", MinTextureScale, MaxTextureScale);
        var rotation = LayoutFields.ReadAngle(ref reader, "background.textureRotation");
        var imageAssetId = naming.Read(ref reader, "background.imageAssetId");
        CheckImage(mode, imageAssetId);
        var fit = (LayoutImageFit)LayoutFields.ReadCode(ref reader, "background.imageFit", (byte)LayoutImageFit.Fill);
        var flips = (LayoutFlips)LayoutFields.ReadFlags(ref reader, "background.imageFlips", (byte)(LayoutFlips.Horizontal | LayoutFlips.Vertical));
        return new LayoutBackground(mode, primary, secondary, gradientAngle, opacity, texture, intensity, scale, rotation, imageAssetId, fit, flips);
    }

    private static void CheckImage(LayoutBackgroundMode mode, AssetId imageAssetId)
    {
        if ((mode == LayoutBackgroundMode.Image) == imageAssetId.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "A background names an image exactly when its mode is the image mode.");
        }
    }
}
