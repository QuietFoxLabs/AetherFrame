using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AetherFrame.LodestoneRelay;

/// <summary>The relay's settings: where it listens, the one client it serves, and its limits.</summary>
internal sealed class RelayOptions
{
    /// <summary>The address and port the relay listens on: the PC's Tailscale address, never a wildcard.</summary>
    public required IPEndPoint Listen { get; init; }

    /// <summary>The only address served: the server's Tailscale address. Any other connection is closed unanswered.</summary>
    public required IPAddress Client { get; init; }

    /// <summary>The time a client has to send its whole request head.</summary>
    public TimeSpan HeadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The time the Lodestone has to accept the connection.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A tunnel with no bytes either way for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest a tunnel stays open, whatever passes through it.</summary>
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The most bytes a tunnel carries from the server to the Lodestone.</summary>
    public long MaxBytesUp { get; init; } = 1024 * 1024;

    /// <summary>The most bytes a tunnel carries from the Lodestone to the server.</summary>
    public long MaxBytesDown { get; init; } = 16 * 1024 * 1024;

    /// <summary>The most tunnels open at once; the server's fetch budget needs far fewer.</summary>
    public int MaxTunnels { get; init; } = 8;

    /// <summary>
    /// The most tunnels opened in any hour: twice the server's own budget of 60 fetches an hour
    /// (decision C2), so the relay holds even if the server didn't.
    /// </summary>
    public int MaxTunnelsPerHour { get; init; } = 120;
}

/// <summary>
/// A CONNECT-only relay to the Lodestone. A client at <see cref="RelayOptions.Client"/> sends
/// <c>CONNECT na.finalfantasyxiv.com:443 HTTP/1.1</c>; the relay connects to that host and port, answers
/// <c>200</c>, and copies bytes both ways until either side closes or a limit is reached. Any other
/// request is answered <c>403</c>, and nothing is connected. The host and port are fixed here, never
/// taken from configuration, so the relay can't reach anything else. It never reads or keeps what
/// passes through a tunnel: that is TLS between the server and the Lodestone.
/// </summary>
internal sealed class Relay
{
    /// <summary>The only host a tunnel goes to.</summary>
    public const string Host = "na.finalfantasyxiv.com";

    /// <summary>The only port a tunnel goes to.</summary>
    public const int Port = 443;

    /// <summary>The longest request head read; .NET's CONNECT is well under it.</summary>
    public const int MaxHeadBytes = 4096;

    private static readonly byte[] Established = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
    private static readonly byte[] Forbidden = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    private static readonly byte[] BadGateway = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    private static readonly byte[] Busy = Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    private readonly RelayOptions options;
    private readonly Func<CancellationToken, ValueTask<Stream>> connectUpstream;
    private readonly Action<string> log;
    private readonly System.Collections.Generic.Queue<long> hour = new();
    private int open;

    /// <param name="options">The settings.</param>
    /// <param name="log">Where one line per connection goes: never what a tunnel carries.</param>
    /// <param name="connectUpstream">Opens the connection to the Lodestone; only tests give their own.</param>
    public Relay(RelayOptions options, Action<string> log, Func<CancellationToken, ValueTask<Stream>>? connectUpstream = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        if (options.Listen.Address.Equals(IPAddress.Any) || options.Listen.Address.Equals(IPAddress.IPv6Any))
        {
            throw new ArgumentException("The relay listens on one address, never a wildcard.", nameof(options));
        }

        this.options = options;
        this.log = log;
        this.connectUpstream = connectUpstream ?? ConnectToLodestoneAsync;
    }

    /// <summary>The address the relay listens on, once <see cref="RunAsync"/> has started.</summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>Tunnels open now.</summary>
    public int OpenTunnels => Volatile.Read(ref open);

    /// <summary>Accepts connections until <paramref name="stop"/> is cancelled.</summary>
    public async Task RunAsync(Action? started, CancellationToken stop)
    {
        using var listener = new TcpListener(options.Listen);
        listener.Start();
        LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
        started?.Invoke();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync(stop);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // Such as the address going away for a moment: wait, rather than spin.
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), stop);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                _ = ServeAsync(socket, stop);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken stop)
    {
        using var client = socket;
        var from = (client.RemoteEndPoint as IPEndPoint)?.Address;
        if (from is null || !Normal(from).Equals(Normal(options.Client)))
        {
            // Not the server: closed without a word, so the port tells others nothing.
            Log("refused a connection from " + (from is null ? "an unknown address" : Normal(from).ToString()));
            return;
        }

        var started = DateTime.UtcNow;
        var counted = false;
        try
        {
            using var stream = new NetworkStream(client, ownsSocket: false);

            // The head is read before any answer, so an answer never meets unread bytes: closing then
            // would reset the connection, and the client could lose the answer.
            var head = await ReadHeadAsync(stream, stop);
            if (head is null)
            {
                Log("no complete request head (too long, too slow, or closed): closed");
                return;
            }

            if (!IsLodestoneConnect(head.Value.Line))
            {
                await SendQuietlyAsync(client, Forbidden, stop);
                Log("refused a request that isn't CONNECT " + Host + ":" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": answered 403");
                return;
            }

            counted = true;
            if (Interlocked.Increment(ref open) > options.MaxTunnels)
            {
                await SendQuietlyAsync(client, Busy, stop);
                Log("too many tunnels open: answered 503");
                return;
            }

            if (!TryTakeFromHour())
            {
                await SendQuietlyAsync(client, Busy, stop);
                Log("too many tunnels this hour: answered 503");
                return;
            }

            Stream upstream;
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                connecting.CancelAfter(options.ConnectTimeout);
                try
                {
                    upstream = await connectUpstream(connecting.Token);
                }
                catch (Exception e) when (e is SocketException or IOException or OperationCanceledException && !stop.IsCancellationRequested)
                {
                    await SendQuietlyAsync(client, BadGateway, stop);
                    Log("couldn't reach the Lodestone (" + e.GetType().Name + "): answered 502");
                    return;
                }
            }

            await using (upstream)
            {
                await stream.WriteAsync(Established, stop);
                if (head.Value.Leftover.Length > 0)
                {
                    // Bytes the client sent after its head, before the answer: they go on as they came.
                    await upstream.WriteAsync(head.Value.Leftover, stop);
                }

                var (up, down, ended) = await PipeAsync(stream, upstream, stop);
                Log("tunnel closed (" + ended + "): " + up + " bytes up, " + down + " bytes down, " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Log("connection ended (" + e.GetType().Name + ")");
        }
        finally
        {
            if (counted)
            {
                Interlocked.Decrement(ref open);
            }
        }
    }

    /// <summary>Takes one of the hour's tunnels, when the last hour has opened fewer than <see cref="RelayOptions.MaxTunnelsPerHour"/>.</summary>
    private bool TryTakeFromHour()
    {
        lock (hour)
        {
            var now = Environment.TickCount64;
            while (hour.Count > 0 && now - hour.Peek() >= 3_600_000)
            {
                hour.Dequeue();
            }

            if (hour.Count >= options.MaxTunnelsPerHour)
            {
                return false;
            }

            hour.Enqueue(now);
            return true;
        }
    }

    /// <summary>Whether a request line is exactly the one CONNECT the relay serves: the host's case aside, nothing differs.</summary>
    internal static bool IsLodestoneConnect(string line)
    {
        var parts = line.Split(' ');
        return parts.Length == 3
            && parts[0] == "CONNECT"
            && string.Equals(parts[1], Host + ":" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            && parts[2] is "HTTP/1.1" or "HTTP/1.0";
    }

    /// <summary>Reads up to the blank line ending the head, within its size and time; null when there is none.</summary>
    private async Task<(string Line, byte[] Leftover)?> ReadHeadAsync(Stream stream, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(options.HeadTimeout);
        var buffer = new byte[MaxHeadBytes];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token);
                if (read == 0)
                {
                    return null;
                }

                length += read;
                var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    var head = buffer.AsSpan(0, end);
                    var lineEnd = head.IndexOf("\r\n"u8);
                    var line = lineEnd < 0 ? head : head[..lineEnd];
                    foreach (var b in line)
                    {
                        if (b is < 0x20 or > 0x7E)
                        {
                            // Only printable ASCII is ever compared.
                            return (string.Empty, []);
                        }
                    }

                    return (Encoding.ASCII.GetString(line), buffer.AsSpan(end + 4, length - end - 4).ToArray());
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Copies both ways until one side ends, the tunnel idles, or a limit is reached; returns the bytes each way and why it ended.</summary>
    private async Task<(long Up, long Down, string Ended)> PipeAsync(Stream client, Stream upstream, CancellationToken stop)
    {
        using var tunnel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        tunnel.CancelAfter(options.MaxLifetime);
        long up = 0, down = 0;
        var lastActivity = Environment.TickCount64;
        var ended = "closed";

        async Task CopyAsync(Stream from, Stream to, long max, bool upward)
        {
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await from.ReadAsync(chunk, tunnel.Token);
                if (read == 0)
                {
                    return;
                }

                var total = upward ? Interlocked.Add(ref up, read) : Interlocked.Add(ref down, read);
                if (total > max)
                {
                    ended = upward ? "too many bytes up" : "too many bytes down";
                    return;
                }

                Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
                await to.WriteAsync(chunk.AsMemory(0, read), tunnel.Token);
            }
        }

        var copyUp = CopyAsync(client, upstream, options.MaxBytesUp, upward: true);
        var copyDown = CopyAsync(upstream, client, options.MaxBytesDown, upward: false);
        var idle = WatchIdleAsync();

        async Task WatchIdleAsync()
        {
            var step = TimeSpan.FromMilliseconds(Math.Max(10, options.IdleTimeout.TotalMilliseconds / 4));
            while (!tunnel.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(step, tunnel.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (Environment.TickCount64 - Interlocked.Read(ref lastActivity) > options.IdleTimeout.TotalMilliseconds)
                {
                    ended = "idle";
                    await tunnel.CancelAsync();
                    return;
                }
            }
        }

        try
        {
            await Task.WhenAny(copyUp, copyDown);
            if (ended == "closed" && tunnel.IsCancellationRequested && !stop.IsCancellationRequested)
            {
                ended = "open too long";
            }
        }
        finally
        {
            if (!tunnel.IsCancellationRequested)
            {
                await tunnel.CancelAsync();
            }

            try
            {
                await Task.WhenAll(copyUp, copyDown, idle);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // One side ended first; the other's read was cancelled or broke.
            }
        }

        return (Interlocked.Read(ref up), Interlocked.Read(ref down), ended);
    }

    private static async ValueTask<Stream> ConnectToLodestoneAsync(CancellationToken cancellation)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(Host, Port, cancellation);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static IPAddress Normal(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static async Task SendQuietlyAsync(Socket socket, byte[] answer, CancellationToken stop)
    {
        try
        {
            await socket.SendAsync(answer, SocketFlags.None, stop);
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client is gone; nothing more to say.
        }
    }

    private void Log(string text) => log(DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " " + text);
}
