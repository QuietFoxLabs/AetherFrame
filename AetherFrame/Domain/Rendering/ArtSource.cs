using AetherFrame.Domain.Components;

namespace AetherFrame.Domain.Rendering;

/// <summary>Where an artwork's bytes stand (art on demand).</summary>
public enum ArtState
{
    /// <summary>Not known yet: the downloaded copies are still being listed, off the draw thread.</summary>
    Checking,

    /// <summary>Inside the plugin assembly.</summary>
    Embedded,

    /// <summary>Downloaded earlier and kept on this PC.</summary>
    Cached,

    /// <summary>Hosted and not on this PC: it downloads when the player uses it.</summary>
    NotDownloaded,

    /// <summary>Waiting for its turn to download.</summary>
    Queued,

    /// <summary>Downloading now.</summary>
    Downloading,

    /// <summary>The last download failed (<see cref="ArtStatus.Problem"/> says why); it waits for the player to try again.</summary>
    Failed,

    /// <summary>Neither inside the plugin nor something this build can download (a build without
    /// networking, or art the table doesn't name).</summary>
    Unavailable,
}

/// <summary>An artwork's state, with the download's progress and the last problem.</summary>
/// <param name="State">Where its bytes stand.</param>
/// <param name="Received">Bytes received so far, while downloading.</param>
/// <param name="Total">The file's length, for a hosted artwork; 0 otherwise.</param>
/// <param name="Problem">Why the last download failed, in words a player reads; null otherwise.</param>
public readonly record struct ArtStatus(ArtState State, long Received = 0, long Total = 0, string? Problem = null)
{
    /// <summary>True when its bytes can be read now: inside the plugin, or downloaded.</summary>
    public bool Readable => State is ArtState.Embedded or ArtState.Cached;
}

/// <summary>
/// Where the artwork loader gets an artwork's bytes (art on demand): the plugin assembly, or a
/// downloaded copy checked against <see cref="ArtFiles"/>. Pure, so the loader's rules are tested.
/// </summary>
public interface IArtSource
{
    /// <summary>Changes whenever any artwork's state does; the loader retries a failed load only
    /// after it has changed, never every frame.</summary>
    int Generation { get; }

    /// <summary>Any thread, the draw thread included: a snapshot, without blocking or touching a file.</summary>
    ArtStatus Status(BuiltInArtAsset art);

    /// <summary>
    /// Off the draw thread: the artwork's bytes, from the plugin assembly or from its downloaded
    /// copy, whose length and SHA-256 are checked first (a copy that fails is deleted, and the
    /// artwork is downloadable again). Throws when they can't be read.
    /// </summary>
    byte[] ReadVerified(BuiltInArtAsset art);
}
