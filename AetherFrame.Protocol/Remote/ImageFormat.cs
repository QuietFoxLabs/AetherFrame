namespace AetherFrame.Protocol.Remote;

/// <summary>
/// The container format of a source image, as the publishing client sniffed it from the bytes
/// (never from a file extension): the three formats AetherFrame imports locally. A closed set; a
/// value not listed here is refused.
/// </summary>
public enum ImageFormat : byte
{
    /// <summary>image/png</summary>
    Png = 1,

    /// <summary>image/jpeg</summary>
    Jpeg = 2,

    /// <summary>image/webp</summary>
    WebP = 3,
}

internal static class ImageFormats
{
    public static bool IsKnown(ImageFormat format) => format is ImageFormat.Png or ImageFormat.Jpeg or ImageFormat.WebP;
}
