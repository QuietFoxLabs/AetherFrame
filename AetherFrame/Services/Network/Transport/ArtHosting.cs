using System;
using AetherFrame.Domain.Components;

namespace AetherFrame.Services.Network.Transport;

/// <summary>
/// Where hosted artwork is downloaded from ("Art on demand" in the decision register): GitHub's raw
/// file host, at the commit of this repository each file is pinned to, so the bytes at an address
/// never change. The address is built from <see cref="ArtFiles"/>' compiled table alone, never from
/// a Plate, a package or anything another player sent.
/// </summary>
internal static class ArtHosting
{
    /// <summary>The host, the only one besides the sharing server the plugin talks to (decision R2, as amended).</summary>
    internal const string Host = "raw.githubusercontent.com";

    /// <summary>The repository the files are committed to.</summary>
    internal const string Repository = "QuietFoxLabs/AetherFrame";

    /// <summary>The folder of the repository the table's paths are below.</summary>
    internal const string AssetsFolder = "AetherFrame/Assets";

    /// <summary>The address of <paramref name="file"/> on <see cref="Host"/>, relative to its root.</summary>
    /// <exception cref="ArgumentException">The file isn't well formed (<see cref="ArtFiles.IsWellFormed"/>).</exception>
    internal static string PathOf(ArtFile file)
    {
        if (!ArtFiles.IsWellFormed(file))
        {
            throw new ArgumentException("Only a well-formed hosted file has an address.", nameof(file));
        }

        return Repository + "/" + file.Commit + "/" + AssetsFolder + "/" + file.Path;
    }
}
