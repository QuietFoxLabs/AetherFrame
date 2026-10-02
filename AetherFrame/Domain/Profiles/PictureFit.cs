using System.Numerics;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// The size a picture is drawn at in <see cref="ProfileImageFit.Fit"/> mode: as large as fits inside
/// its box at its own aspect ratio (it is then centered in the box). One computation for the renderer
/// (<c>ImageFitLayout</c>) and for the paint plan, which lays a Portrait Frame on the drawn picture,
/// so the two agree exactly.
/// </summary>
public static class PictureFit
{
    /// <summary>The drawn size of a picture <paramref name="pixels"/> in size, inside <paramref name="box"/>.
    /// Both must be positive.</summary>
    public static Vector2 Size(Vector2 box, Vector2 pixels)
    {
        var imageAspect = pixels.X / pixels.Y;
        var boxAspect = box.X / box.Y;
        return imageAspect > boxAspect
            ? new Vector2(box.X, box.X / imageAspect)
            : new Vector2(box.Y * imageAspect, box.Y);
    }
}
