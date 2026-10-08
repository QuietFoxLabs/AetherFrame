using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Owns the GPU textures of the built-in artwork (<see cref="BuiltInArtCatalog"/>). Each artwork is
/// read through its <see cref="IArtSource"/> (the plugin assembly, or since art on demand a
/// downloaded copy checked against <see cref="ArtFiles"/>), decoded into
/// a short chain of halved levels (see <see cref="BundledArtImage"/>) and uploaded once, the first
/// time a Plate draws it — on the thread pool, never inside Draw (see <see cref="BuiltInArtLoader{TTexture}"/>):
/// until it is ready the artwork simply isn't drawn, and drawing then only picks a level for the
/// on-screen size — nothing is decoded, created or allocated per frame. Textures live until no
/// window has asked for them for two minutes, or until the plugin unloads (a level chain costs about
/// 4/3 of its top level: ~1.4 MB for a 512 x 512 artwork, ~8.4 MB for each full-resolution Celestial
/// Sakura piece); the draw owner brackets every frame with <see cref="BeginFrame"/> and
/// <see cref="EndFrame"/>, and a released artwork loads again from its local copy when next drawn. Art whose bytes can't be read
/// yet isn't drawn, and the windows drawing it learn so through <see cref="BeginMisses"/>. Art that
/// fails to read or decode is logged and not drawn until its source changes.
/// </summary>
internal sealed class BuiltInArtTextureCache : IDisposable
{
    private readonly BuiltInArtLoader<IDalamudTextureWrap> loader;

    internal BuiltInArtTextureCache(IArtSource source)
    {
        Source = source;
        loader = new BuiltInArtLoader<IDalamudTextureWrap>(source, (art, cancellationToken) => LoadAsync(source, art, cancellationToken));
    }

    /// <summary>Where the artwork's bytes come from, and where each stands.</summary>
    internal IArtSource Source { get; }

    /// <summary>Draw thread: collects the artwork drawn from now on whose bytes can't be read yet (see <see cref="BuiltInArtLoader{TTexture}.BeginMisses"/>).</summary>
    internal void BeginMisses(System.Collections.Generic.List<BuiltInArtAsset> into) => loader.BeginMisses(into);

    /// <summary>Draw thread: ends <see cref="BeginMisses"/>.</summary>
    internal void EndMisses() => loader.EndMisses();

    /// <summary>Draw thread, once a frame before any window draws (see <see cref="BuiltInArtLoader{TTexture}.BeginFrame"/>).</summary>
    internal void BeginFrame() => loader.BeginFrame();

    /// <summary>Draw thread, once a frame after every window drew: releases idle artwork (see
    /// <see cref="BuiltInArtLoader{TTexture}.EndFrame"/>). Dalamud defers a wrap's release until after rendering.</summary>
    internal void EndFrame() => loader.EndFrame();

    /// <summary>The level of <paramref name="art"/> to draw <paramref name="screenPixels"/> across, or null
    /// while it is loading or when it can't be loaded.</summary>
    internal IDalamudTextureWrap? GetWrapOrNull(BuiltInArtAsset art, float screenPixels) => loader.GetLevelOrNull(art, screenPixels);

    public void Dispose() => loader.Dispose();

    /// <summary>Runs on the thread pool: decode and prepare (CPU), then upload through Dalamud's async API.</summary>
    private static async Task<LoadedArt<IDalamudTextureWrap>> LoadAsync(IArtSource source, BuiltInArtAsset art, CancellationToken cancellationToken)
    {
        var created = new List<IDalamudTextureWrap>();
        try
        {
            var bytes = source.ReadVerified(art);
            var levels = BundledArtImage.LoadLevels(bytes, art);
            var sizes = new int[levels.Count];
            for (var i = 0; i < levels.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sizes[i] = levels[i].LongSide;
                created.Add(await DalamudServices.TextureProvider.CreateFromRawAsync(
                    RawImageSpecification.Rgba32(levels[i].Width, levels[i].Height), levels[i].Rgba, $"AetherFrame.Art.{art.Id}.{levels[i].LongSide}", cancellationToken)
                    .ConfigureAwait(false));
            }

            return new LoadedArt<IDalamudTextureWrap>(created, sizes);
        }
        catch (Exception ex)
        {
            foreach (var wrap in created)
            {
                wrap.Dispose();
            }

            if (ex is not OperationCanceledException)
            {
                DalamudServices.Log.Warning(ex, $"AetherFrame could not load the built-in artwork {art.Id}.");
            }

            throw;
        }
    }
}
