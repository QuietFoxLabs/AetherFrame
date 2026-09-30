using System;
using System.Net.Http;
using Dalamud.Networking.Http;

namespace AetherFrame.Services.Network.Transport;

/// <summary>
/// The plugin's connection to the sharing server (decisions R2 and R3): Dalamud's dual-stack
/// connect callback, no redirect followed, no cookies, and bounded headers and connection life.
/// The plugin opens no socket itself: the callback does. Compiled only in the networking preview
/// flavour.
/// </summary>
internal sealed class SharingHandler : IDisposable
{
    private readonly HappyEyeballsCallback callback;

    public SharingHandler()
    {
        callback = new HappyEyeballsCallback();
        Handler = new SocketsHttpHandler
        {
            ConnectCallback = callback.ConnectCallback,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxResponseHeadersLength = 16,
        };
    }

    /// <summary>The handler a <see cref="SharingClient"/> sends through; this object disposes it.</summary>
    public HttpMessageHandler Handler { get; }

    public void Dispose()
    {
        Handler.Dispose();
        callback.Dispose();
    }
}
