using System;
using System.Collections.Generic;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>
/// Whether two layout snapshots say the same thing, field for field: every public property of the
/// snapshot, of its background, of each item and of each image declaration. The commit compares
/// the snapshot it signed with what the signed bytes decode to (N2-6's design, section 2, step 8),
/// so a codec that lost or changed anything on the way can never store bytes that say other than
/// what the player was shown. An item of a kind this build doesn't know is never the same as
/// anything. The plugin suite checks that every public property is compared here, so a property
/// the protocol gains fails a test until it is added.
/// </summary>
internal static class SnapshotComparer
{
    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> hold the same fields, item for item and image for image.</summary>
    internal static bool Same(ProfileLayoutSnapshot a, ProfileLayoutSnapshot b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.ProfileId == b.ProfileId
            && a.RevisionId == b.RevisionId
            && a.CreatedAtUnixSeconds == b.CreatedAtUnixSeconds
            && a.CreatedAt == b.CreatedAt
            && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            && a.CanvasWidth == b.CanvasWidth
            && a.CanvasHeight == b.CanvasHeight
            && a.DocumentType == b.DocumentType
            && a.TotalImageBytes == b.TotalImageBytes
            && a.TotalTextScalars == b.TotalTextScalars
            && Same(a.Background, b.Background)
            && SameList(a.Items, b.Items, Same)
            && SameList(a.Images, b.Images, Same);
    }

    /// <summary>Whether two backgrounds hold the same fields.</summary>
    internal static bool Same(LayoutBackground a, LayoutBackground b) =>
        a.Mode == b.Mode
        && a.Primary == b.Primary
        && a.Secondary == b.Secondary
        && a.GradientAngle == b.GradientAngle
        && a.Opacity == b.Opacity
        && a.Texture == b.Texture
        && a.TextureIntensity == b.TextureIntensity
        && a.TextureScale == b.TextureScale
        && a.TextureRotation == b.TextureRotation
        && a.ImageAssetId == b.ImageAssetId
        && a.ImageFit == b.ImageFit
        && a.ImageFlips == b.ImageFlips;

    /// <summary>Whether two image declarations hold the same fields.</summary>
    internal static bool Same(ImageReference a, ImageReference b) =>
        a.AssetId == b.AssetId
        && a.Sha256.SequenceEqual(b.Sha256)
        && a.Format == b.Format
        && a.ByteLength == b.ByteLength
        && a.Width == b.Width
        && a.Height == b.Height;

    /// <summary>Whether two items are of the same kind and hold the same fields; never for a kind this build doesn't know.</summary>
    internal static bool Same(LayoutItem a, LayoutItem b) => (a, b) switch
    {
        (LayoutText x, LayoutText y) =>
            x.Position == y.Position
            && x.Width == y.Width
            && x.Height == y.Height
            && string.Equals(x.Text, y.Text, StringComparison.Ordinal)
            && string.Equals(x.Font, y.Font, StringComparison.Ordinal)
            && x.FontSize == y.FontSize
            && x.Color == y.Color
            && x.Align == y.Align
            && x.VerticalAlign == y.VerticalAlign
            && x.Flags == y.Flags
            && x.LetterSpacing == y.LetterSpacing
            && x.LineSpacing == y.LineSpacing
            && x.AutoFitMinimum == y.AutoFitMinimum
            && x.OutlineColor == y.OutlineColor
            && x.OutlineThickness == y.OutlineThickness
            && x.ShadowColor == y.ShadowColor
            && x.ShadowX == y.ShadowX
            && x.ShadowY == y.ShadowY
            && x.Layout == y.Layout
            && x.Kind == y.Kind,
        (LayoutImage x, LayoutImage y) =>
            x.AssetId == y.AssetId
            && x.Position == y.Position
            && x.Width == y.Width
            && x.Height == y.Height
            && x.Rotation == y.Rotation
            && x.Fit == y.Fit
            && x.Flips == y.Flips
            && x.Opacity == y.Opacity
            && x.Kind == y.Kind,
        (LayoutQuad x, LayoutQuad y) =>
            x.A == y.A && x.B == y.B && x.C == y.C && x.D == y.D && x.Color == y.Color && x.Kind == y.Kind,
        (LayoutTriangle x, LayoutTriangle y) =>
            x.A == y.A && x.B == y.B && x.C == y.C && x.Color == y.Color && x.Kind == y.Kind,
        (LayoutImageQuad x, LayoutImageQuad y) =>
            x.AssetId == y.AssetId && x.A == y.A && x.B == y.B && x.C == y.C && x.D == y.D && x.Tint == y.Tint && x.Kind == y.Kind,
        (LayoutArtQuad x, LayoutArtQuad y) =>
            string.Equals(x.Art, y.Art, StringComparison.Ordinal) && x.A == y.A && x.B == y.B && x.C == y.C && x.D == y.D && x.Tint == y.Tint && x.Kind == y.Kind,
        _ => false,
    };

    private static bool SameList<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> same)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var index = 0; index < a.Count; index++)
        {
            if (!same(a[index], b[index]))
            {
                return false;
            }
        }

        return true;
    }
}
