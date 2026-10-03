using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AetherFrame.Services.Network.Transport;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The server's side of a WebSocket, for a handler that answers the plugin in memory: the upgrade's
/// <c>101</c>, its accept key computed as RFC 6455 says, and the WebSocket over a connected pair of
/// loopback sockets, served by the test. Nothing here is the plugin's.
/// </summary>
internal static class WebSocketStandIn
{
    private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Whether <paramref name="request"/> asks to upgrade to a WebSocket.</summary>
    internal static bool IsUpgrade(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get && request.Headers.Upgrade.Any(product => string.Equals(product.Name, "websocket", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Accepts the upgrade and runs <paramref name="serve"/> on the server's end in the background.
    /// Whatever it throws is kept in <paramref name="faults"/>, so a test can see it.
    /// </summary>
    internal static async Task<HttpResponseMessage> AcceptAsync(HttpRequestMessage request, Func<WebSocket, Task> serve, ConcurrentQueue<Exception> faults)
    {
        var key = request.Headers.GetValues("Sec-WebSocket-Key").Single();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)));
        var (client, server) = await ConnectedPairAsync();
        var socket = WebSocket.CreateFromStream(server, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
        _ = Task.Run(async () =>
        {
            try
            {
                await serve(socket);
            }
            catch (Exception exception)
            {
                faults.Enqueue(exception);
            }
            finally
            {
                socket.Dispose();
                await server.DisposeAsync();
            }
        });

        var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols) { Content = new RawStreamContent(client), RequestMessage = request };
        response.Headers.Connection.Add("Upgrade");
        response.Headers.Upgrade.Add(new ProductHeaderValue("websocket"));
        response.Headers.TryAddWithoutValidation("Sec-WebSocket-Accept", accept);
        return response;
    }

    /// <summary>One whole message: its type and bytes; a close as <see cref="WebSocketMessageType.Close"/> with none.</summary>
    internal static async Task<(WebSocketMessageType Type, byte[] Bytes)> ReceiveAsync(WebSocket socket, CancellationToken cancellation = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(chunk, cancellation);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, []);
            }

            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage)
            {
                return (result.MessageType, buffer.ToArray());
            }
        }
    }

    /// <summary>The next message, which must be the text <paramref name="expected"/>.</summary>
    internal static async Task ExpectTextAsync(WebSocket socket, string expected)
    {
        var (type, bytes) = await ReceiveAsync(socket);
        Assert.Equal((WebSocketMessageType.Text, expected), (type, Encoding.UTF8.GetString(bytes)));
    }

    internal static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    internal static Task SendBinaryAsync(WebSocket socket, byte[] bytes) =>
        socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

    /// <summary>Sends the final message, then closes as the server does, waiting for the plugin's close in answer.</summary>
    internal static async Task FinishAsync(WebSocket socket, string final)
    {
        await SendTextAsync(socket, final);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (socket.State != WebSocketState.Closed)
        {
            var (type, _) = await ReceiveAsync(socket, patience.Token);
            if (type == WebSocketMessageType.Close)
            {
                break;
            }
        }
    }

    private static async Task<(Stream Client, Stream Server)> ConnectedPairAsync()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connecting = client.ConnectAsync(listener.LocalEndPoint!);
        var server = await listener.AcceptAsync();
        await connecting;
        return (new NetworkStream(client, ownsSocket: true), new NetworkStream(server, ownsSocket: true));
    }

    /// <summary>A response body that is the upgraded connection itself, readable and writable, as .NET's own handler gives it.</summary>
    private sealed class RawStreamContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => stream;

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
    }
}

/// <summary>
/// A stand-in for the pipe's connection to the Lodestone: the address it reports, the bytes the
/// server sent toward it, and the bytes it answers with, which a test writes. It never connects.
/// </summary>
internal sealed class FakeLodestoneLink : LodestonePipe.Link
{
    private readonly Channel<byte[]> answers = Channel.CreateUnbounded<byte[]>();
    private readonly MemoryStream received = new();

    internal FakeLodestoneLink(IPAddress? address) => Address = address;

    internal IPAddress? Address { get; }

    /// <summary>When set, connecting waits for it, or fails as it fails.</summary>
    internal Task? ConnectGate { get; init; }

    internal bool Connected { get; private set; }

    internal bool Disposed { get; private set; }

    /// <summary>What the server sent toward the Lodestone, joined.</summary>
    internal byte[] Received
    {
        get
        {
            lock (received)
            {
                return received.ToArray();
            }
        }
    }

    /// <summary>Signalled each time bytes arrive toward the Lodestone.</summary>
    internal SemaphoreSlim Arrived { get; } = new(0);

    /// <summary>The Lodestone's bytes, as the next read gives them.</summary>
    internal void Answer(byte[] bytes) => answers.Writer.TryWrite(bytes);

    /// <summary>The Lodestone closes its side: the next read gives 0.</summary>
    internal void End() => answers.Writer.TryComplete();

    internal override async Task<IPAddress?> ConnectAsync(CancellationToken cancellation)
    {
        if (ConnectGate is { } gate)
        {
            await gate.WaitAsync(cancellation);
        }

        Connected = true;
        return Address;
    }

    internal override ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
    {
        lock (received)
        {
            received.Write(bytes.Span);
        }

        Arrived.Release();
        return ValueTask.CompletedTask;
    }

    internal override async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellation)
    {
        if (!await answers.Reader.WaitToReadAsync(cancellation))
        {
            return 0;
        }

        var next = await answers.Reader.ReadAsync(cancellation);
        Assert.True(next.Length <= buffer.Length, "a test answers in pieces the pipe can read whole");
        next.CopyTo(buffer);
        return next.Length;
    }

    public override void Dispose()
    {
        Disposed = true;
        answers.Writer.TryComplete();
    }
}
