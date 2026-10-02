using System;

namespace AetherFrame.Services.Network.Transport;

/// <summary>
/// The plugin's one connection (decision R2): the handler through Dalamud's connect callback, the
/// client over it for <see cref="SharingDeployment.Name"/>, and the artwork client over the same
/// handler for <see cref="ArtHosting.Host"/> ("Art on demand"), made and disposed together. It keeps
/// every networking type inside Services/Network (decision R3): the rest of the plugin holds this and
/// the clients, never a handler.
/// </summary>
internal sealed class SharingConnection : IDisposable
{
    private readonly SharingHandler handler = new();

    internal SharingConnection(Version pluginVersion)
    {
        Client = new SharingClient(SharingDeployment.Name, handler.Handler, disposeHandler: false, pluginVersion);
        Art = new ArtDownloadClient(handler.Handler, disposeHandler: false, pluginVersion);
    }

    internal SharingClient Client { get; }

    /// <summary>Downloads hosted artwork; nothing calls it but the artwork store, on a player's action.</summary>
    internal ArtDownloadClient Art { get; }

    public void Dispose()
    {
        Art.Dispose();
        Client.Dispose();
        handler.Dispose();
    }
}
