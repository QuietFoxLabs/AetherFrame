using System;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.UI.Rendering;

namespace AetherFrame.Hosting.Network.Publishing;

/// <summary>
/// The renderer's own measurements, for resolving a Plate as it draws (N2-6's design, section 8):
/// a text's natural width with the fonts it is drawn with, the Favorite Jobs display the renderer
/// draws instead of the stored names, and a managed image's size from its file's header. A font not
/// built yet answers "not ready", for a measurement and for the Favorite Jobs display alike, and the
/// resolve waits: nothing falls back to a width or to names the renderer wouldn't draw. Framework
/// thread only, as ImGui's fonts are. Compiled only in the networking preview flavour.
/// </summary>
internal sealed class RendererPlateMeasurements : IPlateMeasurements
{
    private readonly ProfileRenderResources resources;
    private readonly AssetStorageService assets;

    internal RendererPlateMeasurements(ProfileRenderResources resources, AssetStorageService assets)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
    }

    public bool TryMeasureNaturalWidth(TextProfileElement element, out float width) =>
        ProfileTextRenderer.TryMeasureNaturalWidth(element, resources.Fonts, out width);

    public bool TryGetDisplayOverride(ProfileDocument plate, TextProfileElement element, out string? display)
    {
        // As FavoriteJobsDisplay does for the renderer, without its cache: a width that can't be
        // measured yet makes the whole display "not ready" instead of the stored full names.
        var measured = true;
        display = BasicFavoriteJobs.DisplayText(
            plate,
            element,
            id => resources.Jobs.Find(id),
            text =>
            {
                if (ProfileTextRenderer.TryMeasureNaturalWidth(element, text, resources.Fonts, out var width))
                {
                    return width;
                }

                measured = false;
                return null;
            });
        return measured;
    }

    public bool TryGetImageSize(Guid image, out int width, out int height)
    {
        (width, height) = (0, 0);
        if (assets.ResolveAssetPath(image) is not { } path || ImageDimensionReader.TryReadDimensions(path) is not { } size)
        {
            return false;
        }

        (width, height) = size;
        return true;
    }
}
