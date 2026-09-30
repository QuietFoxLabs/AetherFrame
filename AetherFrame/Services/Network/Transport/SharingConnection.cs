using System;

namespace AetherFrame.Services.Network.Transport;

/// <summary>
/// The plugin's one connection to the sharing server (decision R2): the handler through Dalamud's
/// connect callback, and the client over it for <see cref="SharingDeployment.Name"/>, made and
/// disposed together. It keeps every networking type inside Services/Network (decision R3): the
/// rest of the plugin holds this and the client, never a handler.
/// </summary>
internal sealed class SharingConnection : IDisposable
{
    private readonly SharingHandler handler = new();

    internal SharingConnection(Version pluginVersion)
    {
        Client = new SharingClient(SharingDeployment.Name, handler.Handler, disposeHandler: false, pluginVersion);
    }

    internal SharingClient Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        handler.Dispose();
    }
}
