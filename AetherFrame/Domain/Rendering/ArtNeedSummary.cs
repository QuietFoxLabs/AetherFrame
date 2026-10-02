using System.Collections.Generic;
using System.Globalization;

namespace AetherFrame.Domain.Rendering;

/// <summary>What a window says about the artwork its Plate needs but can't draw yet.</summary>
public enum ArtNeedKind
{
    /// <summary>Nothing to say: everything is drawn, or its state is still being checked.</summary>
    None,

    /// <summary>Downloading, or about to.</summary>
    Downloading,

    /// <summary>A download failed and waits for the player's Try again.</summary>
    Failed,

    /// <summary>This build can't download it.</summary>
    Unavailable,
}

/// <summary>
/// One line about the artwork a window's Plate is missing (art on demand): what is downloading and
/// how far it has got, or why it failed, or that this build can't have it. Pure, so it is tested.
/// </summary>
public readonly record struct ArtNeedSummary(ArtNeedKind Kind, long Received = 0, long Total = 0, string? Problem = null)
{
    /// <summary>
    /// The summary of <paramref name="statuses"/>: downloading while anything is pending (with the
    /// bytes received of all of it), then failed (the first problem), then unavailable. Artwork that
    /// is readable, or still being checked, says nothing.
    /// </summary>
    public static ArtNeedSummary Of(IEnumerable<ArtStatus> statuses)
    {
        long received = 0;
        long total = 0;
        string? problem = null;
        var pending = false;
        var failed = false;
        var unavailable = false;
        foreach (var status in statuses)
        {
            switch (status.State)
            {
                case ArtState.NotDownloaded:
                case ArtState.Queued:
                    pending = true;
                    total += status.Total;
                    break;
                case ArtState.Downloading:
                    pending = true;
                    received += status.Received;
                    total += status.Total;
                    break;
                case ArtState.Failed:
                    failed = true;
                    problem ??= status.Problem;
                    break;
                case ArtState.Unavailable:
                    unavailable = true;
                    break;
            }
        }

        return pending ? new ArtNeedSummary(ArtNeedKind.Downloading, received, total)
            : failed ? new ArtNeedSummary(ArtNeedKind.Failed, Problem: problem)
            : unavailable ? new ArtNeedSummary(ArtNeedKind.Unavailable)
            : default;
    }

    /// <summary>The line a player reads; empty for <see cref="ArtNeedKind.None"/>.</summary>
    public string Label => Kind switch
    {
        ArtNeedKind.Downloading when Total > 0 => $"Downloading artwork from GitHub: {Megabytes(Received)} of {Megabytes(Total)} MB",
        ArtNeedKind.Downloading => "Downloading artwork from GitHub",
        ArtNeedKind.Failed => $"Couldn't download artwork: {Problem ?? "something went wrong"}.",
        ArtNeedKind.Unavailable => "This build can't download artwork, so some isn't drawn.",
        _ => string.Empty,
    };

    /// <summary>Bytes as megabytes, one decimal ("4.1").</summary>
    public static string Megabytes(long bytes) => (bytes / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture);
}
