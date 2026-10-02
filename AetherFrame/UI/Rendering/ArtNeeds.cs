using System.Collections.Generic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services.Art;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// The artwork one window's Plate needed but couldn't draw last frame (art on demand). A window that
/// shows a Plate on the player's action (an editor, the Plate Viewer) draws it between
/// <see cref="Begin"/> and <see cref="End"/>, which downloads what is missing ("Art on demand" in the
/// decision register, point 2); a failed download waits for <see cref="TryAgain"/>. Draw thread only.
/// </summary>
internal sealed class ArtNeeds
{
    private List<BuiltInArtAsset> collecting = new();
    private List<BuiltInArtAsset> missing = new();
    private readonly List<ArtStatus> statuses = new();

    // Everything missing since this download run began (the window had nothing to say, or only a
    // failure), so its progress counts the files already done and never goes backwards as each one
    // finishes, and a failed run's files never count in the next.
    private readonly List<BuiltInArtAsset> tracked = new();

    /// <summary>What last frame's Plate couldn't draw.</summary>
    internal IReadOnlyList<BuiltInArtAsset> Missing => missing;

    /// <summary>Starts collecting what the Plate drawn next can't draw.</summary>
    internal void Begin(ProfileRenderResources resources)
    {
        collecting.Clear();
        resources.Art.BeginMisses(collecting);
    }

    /// <summary>Stops collecting, and starts downloading what was missing (a download already
    /// running, done or failed is left as it is).</summary>
    internal void End(ProfileRenderResources resources)
    {
        resources.Art.EndMisses();
        (missing, collecting) = (collecting, missing);
        if (missing.Count > 0)
        {
            resources.ArtStore.Request(missing, retryFailed: false);
        }
    }

    /// <summary>
    /// What to say about <see cref="Missing"/>. While it downloads, the bytes count everything missing
    /// since this download run began, the files already here included.
    /// </summary>
    internal ArtNeedSummary Summary(ArtStore store)
    {
        statuses.Clear();
        foreach (var art in missing)
        {
            statuses.Add(store.Status(art));
        }

        var now = ArtNeedSummary.Of(statuses);
        if (now.Kind != ArtNeedKind.Downloading)
        {
            tracked.Clear();
            return now;
        }

        foreach (var art in missing)
        {
            if (!tracked.Contains(art))
            {
                tracked.Add(art);
            }
        }

        long received = 0;
        long total = 0;
        foreach (var art in tracked)
        {
            var status = store.Status(art);
            var length = store.HostedFile(art)?.Length ?? status.Total;
            received += status.Readable ? length : status.State == ArtState.Downloading ? status.Received : 0;
            total += length;
        }

        return now with { Received = received, Total = total };
    }

    /// <summary>The player's Try again: downloads every missing artwork again, the failed ones too.</summary>
    internal void TryAgain(ArtStore store) => store.Request(missing, retryFailed: true);
}
