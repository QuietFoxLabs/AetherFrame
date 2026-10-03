using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Requests;

namespace AetherFrame.Services.Network.Transport;

/// <summary>
/// The Lodestone check and re-read sent through the player's own connection (ServerApi-v1.md,
/// section 2.3; "Checking a character through the player's own connection" in the decision
/// register, which amends R2 and R3 for this one type). A WebSocket to the deployment's hostname at
/// the action's path carries the signed body a <c>POST</c> would; when the server asks with
/// <c>open</c>, one TCP connection to <c>na.finalfantasyxiv.com</c>, port 443, is opened and its
/// bytes carried both ways in binary messages. The server runs TLS to the Lodestone over it, so
/// only encrypted bytes pass here: nothing here can read or change the page.
/// <para>
/// The host and the port are constants, never an address from the server. The connected address
/// must be a global unicast one on the allowed list before any byte is forwarded, and the bytes
/// toward the Lodestone must be TLS records that start with a ClientHello naming the Lodestone
/// alone (<see cref="TlsRecords"/>), so the server can't reach another site at the same address.
/// At most 16 KiB go toward the
/// Lodestone and 2 MiB come from it, in messages of at most 64 KiB, with one <c>open</c>, within
/// 45 seconds for the whole exchange. The WebSocket's handshake runs on the plugin's one handler
/// (<see cref="SharingHandler"/>), through an invoker, with the version header and nothing else
/// set. Nothing about the exchange is logged here, and the only call that leaves this type is
/// <see cref="RunAsync"/>, which runs the whole exchange and returns the final answer: no stream,
/// socket or WebSocket is ever handed out. Compiled only in the networking preview flavour.
/// </para>
/// </summary>
internal sealed class LodestonePipe : IDisposable
{
    /// <summary>The most bytes the server may send toward the Lodestone through one pipe.</summary>
    internal const int MaxBytesToLodestone = 16 * 1024;

    /// <summary>The most bytes the Lodestone may send through one pipe.</summary>
    internal const int MaxBytesFromLodestone = 2 * 1024 * 1024;

    /// <summary>The largest WebSocket message, either way, after the first.</summary>
    internal const int MaxMessageBytes = 64 * 1024;

    /// <summary>The largest text message read: <c>open</c>, <c>close</c>, or the final answer.</summary>
    internal const int MaxTextBytes = 8 * 1024;

    // The one place the pipe ever connects. Constants, so the compiled IL names them literally.
    private const string LodestoneHost = "na.finalfantasyxiv.com";
    private const int LodestonePort = 443;

    private const byte HandshakeRecord = 22;
    private const byte ClientHelloType = 1;
    private const int ServerNameExtension = 0;
    private const int EncryptedClientHelloExtension = 0xFE0D;

    /// <summary>The largest TLS record's body: 2^14 bytes of plaintext, and 256 more once protected (RFC 8446, section 5.2).</summary>
    private const int MaxRecordBytes = 16 * 1024 + 256;

    private static readonly byte[] Opened = "opened"u8.ToArray();
    private static readonly byte[] Failed = "failed"u8.ToArray();
    private static readonly byte[] Eof = "eof"u8.ToArray();

    private readonly DeploymentName deployment;
    private readonly HttpMessageInvoker invoker;
    private readonly string versionHeader;
    private readonly Func<Link> links;

    /// <summary>
    /// A pipe for <paramref name="deployment"/>, whose WebSocket handshakes run on
    /// <paramref name="handler"/> (the plugin's <see cref="SharingHandler"/>; tests pass their own),
    /// naming <paramref name="pluginVersion"/> as every request does.
    /// </summary>
    internal LodestonePipe(DeploymentName deployment, HttpMessageHandler handler, Version pluginVersion)
        : this(deployment, handler, pluginVersion, static () => new SocketLink())
    {
    }

    /// <summary>A pipe whose connection to the Lodestone is <paramref name="links"/>'s: for tests, which never reach the Lodestone.</summary>
    internal LodestonePipe(DeploymentName deployment, HttpMessageHandler handler, Version pluginVersion, Func<Link> links)
    {
        this.deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(pluginVersion);
        this.links = links ?? throw new ArgumentNullException(nameof(links));
        invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        versionHeader = "AetherFrame/" + pluginVersion.ToString(3);
    }

    /// <summary>How long the whole exchange may take: a little longer than the server's own 40 seconds.</summary>
    internal TimeSpan ExchangeTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>How long connecting to the Lodestone may take: within the server's 10 seconds for <c>opened</c>.</summary>
    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Whether the pipe may carry bytes to <paramref name="address"/>: a global unicast address on
    /// the allowed list, read as IPv4 first when it is IPv4 mapped into IPv6. Loopback, private,
    /// link-local, shared, unique local, multicast, unspecified, documentation and relay addresses
    /// never pass, since DNS blockers and hosts files map names to them.
    /// </summary>
    internal static bool Allows(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var length))
        {
            return false;
        }

        return length switch
        {
            4 => AllowsIPv4(bytes[..4]),
            16 => AllowsIPv6(bytes),
            _ => false,
        };
    }

    /// <summary>
    /// Sends <paramref name="signedBody"/> (a <c>u16</c> proof length, the proof, then the action's
    /// body: exactly what a <c>POST</c> carries) as <paramref name="kind"/>, the check or the
    /// re-read, and carries the Lodestone's bytes when the server asks. Returns the final answer:
    /// its status, and as the body the JSON a <c>POST</c> would have got, or a <c>409</c>'s fresh
    /// challenge's 32 bytes; with the reason and the day of the last read when it carries them.
    /// </summary>
    /// <exception cref="SharingException">No final answer came, or one the plugin can't use.</exception>
    internal async Task<SharingResponse> RunAsync(RequestProofKind kind, byte[] signedBody, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(signedBody);
        var path = kind switch
        {
            RequestProofKind.LodestoneCheck => "v1/lodestone/check",
            RequestProofKind.LodestoneReread => "v1/lodestone/reread",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "Only the check and the re-read go through the pipe."),
        };

        if (signedBody.Length is 0 or > 2 + ProtocolLimits.MaxRequestProofBytes + ProtocolLimits.MaxActionBodyBytes)
        {
            throw new ArgumentException("A signed action is at most 4,552 bytes.", nameof(signedBody));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(ExchangeTimeout);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", versionHeader);
        using var exchange = new Exchange(this, socket, deadline.Token);
        try
        {
            await socket.ConnectAsync(new Uri("wss://" + deployment.Value + "/" + path, UriKind.Absolute), invoker, deadline.Token).ConfigureAwait(false);
            return await exchange.RunAsync(signedBody).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException("The request was cancelled.", cancellation);
        }
        catch (OperationCanceledException)
        {
            throw new SharingException(null, "The server didn't answer in time.");
        }
        catch (WebSocketException)
        {
            throw new SharingException(null, "The server can't be reached, or ended the exchange.");
        }
        catch (HttpRequestException e)
        {
            throw new SharingException(null, "The server can't be reached (" + e.HttpRequestError + ").");
        }
        catch (IOException)
        {
            throw new SharingException(null, "The connection to the server broke.");
        }
        finally
        {
            if (socket.State != WebSocketState.Closed)
            {
                socket.Abort();
            }
        }
    }

    public void Dispose() => invoker.Dispose();

    /// <summary>
    /// Reads the final message strictly: one JSON object with <c>status</c>, and at most
    /// <c>body</c> (an object) with <c>readDay</c> for a <c>200</c>, <c>challenge</c> with a
    /// <c>409</c>, and <c>reason</c> (<c>lodestone:refused</c>) with a <c>503</c>.
    /// </summary>
    /// <exception cref="SharingException">It isn't one.</exception>
    internal static SharingResponse ReadFinal(ReadOnlySpan<byte> message)
    {
        try
        {
            using var document = JsonDocument.Parse(message.ToArray(), new JsonDocumentOptions { MaxDepth = 3, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException();
            }

            int? status = null;
            byte[]? body = null;
            long? readDay = null;
            byte[]? challenge = null;
            string? reason = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw new InvalidDataException();
                }

                var value = property.Value;
                switch (property.Name)
                {
                    case "status" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is >= 200 and <= 599:
                        status = number;
                        break;
                    case "body" when value.ValueKind == JsonValueKind.Object:
                        body = Encoding.UTF8.GetBytes(value.GetRawText());
                        break;
                    case "readDay" when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var day) && day is >= 0 and <= 1_000_000:
                        readDay = day;
                        break;
                    case "challenge" when value.ValueKind == JsonValueKind.String:
                        challenge = Convert.FromBase64String(value.GetString()!);
                        break;
                    case "reason" when value.ValueKind == JsonValueKind.String && value.GetString() == "lodestone:refused":
                        reason = value.GetString();
                        break;
                    default:
                        throw new InvalidDataException();
                }
            }

            if (status is not { } code
                || (readDay is not null && (code != 200 || body is null))
                || (challenge is not null && (code != 409 || challenge.Length != ProtocolConstants.ChallengeLength || body is not null))
                || (reason is not null && (code != 503 || body is not null)))
            {
                throw new InvalidDataException();
            }

            return new SharingResponse((HttpStatusCode)code, challenge ?? body ?? [], body is null ? null : "application/json") { Reason = reason, ReadDay = readDay };
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or FormatException)
        {
            throw new SharingException(null, "The server's final answer isn't one the plugin can use.");
        }
    }

    private static bool AllowsIPv4(ReadOnlySpan<byte> a)
    {
        // The allowed list: 1.0.0.0 to 223.255.255.255, the unicast blocks, less every special one.
        if (a[0] is 0 or > 223)
        {
            return false;
        }

        return !(a[0] == 10                                         // private
            || (a[0] == 100 && (a[1] & 0xC0) == 64)                  // shared, 100.64.0.0/10
            || a[0] == 127                                           // loopback
            || (a[0] == 169 && a[1] == 254)                          // link-local
            || (a[0] == 172 && (a[1] & 0xF0) == 16)                  // private, 172.16.0.0/12
            || (a[0] == 192 && a[1] == 0 && a[2] is 0 or 2)          // IETF assignments, documentation
            || (a[0] == 192 && a[1] == 88 && a[2] == 99)             // 6to4 relays
            || (a[0] == 192 && a[1] == 168)                          // private
            || (a[0] == 198 && (a[1] & 0xFE) == 18)                  // benchmarking, 198.18.0.0/15
            || (a[0] == 198 && a[1] == 51 && a[2] == 100)            // documentation
            || (a[0] == 203 && a[1] == 0 && a[2] == 113));           // documentation
    }

    private static bool AllowsIPv6(ReadOnlySpan<byte> a)
    {
        // The allowed list: global unicast, 2000::/3, less the blocks under it that aren't a host's.
        if ((a[0] & 0xE0) != 0x20)
        {
            return false;
        }

        return !((a[0] == 0x20 && a[1] == 0x01 && (a[2] & 0xFE) == 0x00)       // IETF assignments and Teredo, 2001::/23
            || (a[0] == 0x20 && a[1] == 0x01 && a[2] == 0x0D && a[3] == 0xB8)  // documentation, 2001:db8::/32
            || (a[0] == 0x20 && a[1] == 0x02)                                  // 6to4, 2002::/16
            || (a[0] == 0x3F && a[1] == 0xFF && (a[2] & 0xF0) == 0x00));       // documentation, 3fff::/20
    }

    /// <summary>
    /// Whether a handshake record's body is exactly one TLS ClientHello (RFC 8446, section 4.1.2)
    /// that names the Lodestone, and only it: one <c>server_name</c> extension (RFC 6066, section
    /// 3) holding one <c>host_name</c>, <see cref="LodestoneHost"/> in any case, and no Encrypted
    /// Client Hello, whose hidden name could choose another site at the same address. The
    /// ClientHello is the one plaintext part of the server's TLS that says which site answers.
    /// </summary>
    private static bool IsClientHelloForTheLodestone(ReadOnlySpan<byte> record)
    {
        if (record.Length < 4 || record[0] != ClientHelloType || ((record[1] << 16) | (record[2] << 8) | record[3]) != record.Length - 4)
        {
            return false;
        }

        var hello = record[4..];
        var at = 2 + 32;                                                           // legacy_version, random
        if (!Skip(hello, ref at, lengthBytes: 1, max: 32)                          // legacy_session_id
            || !Skip(hello, ref at, lengthBytes: 2, max: 0xFFFE)                   // cipher_suites
            || !Skip(hello, ref at, lengthBytes: 1, max: 0xFF)                     // legacy_compression_methods
            || hello.Length < at + 2 || ((hello[at] << 8) | hello[at + 1]) != hello.Length - at - 2)
        {
            return false;
        }

        at += 2;
        var named = false;
        while (at < hello.Length)
        {
            if (hello.Length < at + 4)
            {
                return false;
            }

            var type = (hello[at] << 8) | hello[at + 1];
            var length = (hello[at + 2] << 8) | hello[at + 3];
            at += 4;
            if (hello.Length < at + length || type == EncryptedClientHelloExtension)
            {
                return false;
            }

            if (type == ServerNameExtension)
            {
                // ServerNameList: one entry, host_name (0), the Lodestone's name.
                var names = hello.Slice(at, length);
                if (named || names.Length < 5 || ((names[0] << 8) | names[1]) != names.Length - 2 || names[2] != 0
                    || ((names[3] << 8) | names[4]) != names.Length - 5 || !Ascii.EqualsIgnoreCase(names[5..], LodestoneHost))
                {
                    return false;
                }

                named = true;
            }

            at += length;
        }

        return named;
    }

    /// <summary>Steps over a vector with a length prefix of <paramref name="lengthBytes"/> bytes and at most <paramref name="max"/> bytes; false when it doesn't fit.</summary>
    private static bool Skip(ReadOnlySpan<byte> bytes, ref int at, int lengthBytes, int max)
    {
        if (bytes.Length < at + lengthBytes)
        {
            return false;
        }

        var length = lengthBytes == 1 ? bytes[at] : (bytes[at] << 8) | bytes[at + 1];
        at += lengthBytes;
        if (length > max || bytes.Length < at + length)
        {
            return false;
        }

        at += length;
        return true;
    }

    /// <summary>
    /// The pipe's one connection to the Lodestone, as the exchange uses it. The plugin's is a TCP
    /// socket (<see cref="SocketLink"/>); tests give their own, so nothing they run reaches the
    /// Lodestone.
    /// </summary>
    internal abstract class Link : IDisposable
    {
        /// <summary>Connects to the Lodestone; the address connected to, which the exchange checks before forwarding a byte.</summary>
        internal abstract Task<IPAddress?> ConnectAsync(CancellationToken cancellation);

        /// <summary>Sends bytes toward the Lodestone.</summary>
        internal abstract ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation);

        /// <summary>Reads bytes from the Lodestone: 0 once it has closed its side.</summary>
        internal abstract ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellation);

        public abstract void Dispose();
    }

    /// <summary>
    /// The plugin's connection: one TCP socket, dual-stack where the system has IPv6, connected only
    /// to the one host and port this type names, never through a proxy.
    /// </summary>
    private sealed class SocketLink : Link
    {
        private readonly Socket socket;

        internal SocketLink()
        {
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
        }

        internal override async Task<IPAddress?> ConnectAsync(CancellationToken cancellation)
        {
            await socket.ConnectAsync(new DnsEndPoint(LodestoneHost, LodestonePort), cancellation).ConfigureAwait(false);
            return (socket.RemoteEndPoint as IPEndPoint)?.Address;
        }

        internal override ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation) =>
            SendAllAsync(bytes, cancellation);

        internal override ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellation) =>
            socket.ReceiveAsync(buffer, SocketFlags.None, cancellation);

        public override void Dispose()
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // Never connected, or already reset.
            }
            catch (ObjectDisposedException)
            {
            }

            socket.Dispose();
        }

        private async ValueTask SendAllAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
        {
            while (!bytes.IsEmpty)
            {
                var sent = await socket.SendAsync(bytes, SocketFlags.None, cancellation).ConfigureAwait(false);
                bytes = bytes[sent..];
            }
        }
    }

    /// <summary>
    /// The server's bytes toward the Lodestone, read as TLS records (RFC 8446, section 5.1), which
    /// may arrive split across messages or several in one. The first record must be a handshake,
    /// and every handshake record one whole ClientHello for the Lodestone, held back until it is
    /// whole and checked (<see cref="IsClientHelloForTheLodestone"/>); a second one, after a
    /// HelloRetryRequest, is checked the same way. Change cipher spec, alerts and protected
    /// records pass as they come. Anything else ends the exchange.
    /// </summary>
    private sealed class TlsRecords
    {
        private readonly byte[] held = new byte[5 + MaxRecordBytes];
        private int heldCount;
        private int passing;
        private bool started;

        /// <summary>The bytes that may go on now, in order: all but a header or a handshake record not yet whole.</summary>
        /// <exception cref="SharingException">The bytes break the rules above.</exception>
        internal ReadOnlyMemory<byte> Take(ReadOnlySpan<byte> bytes)
        {
            var output = new byte[heldCount + bytes.Length];
            var count = 0;
            var at = 0;
            while (at < bytes.Length)
            {
                if (passing > 0)
                {
                    var through = Math.Min(passing, bytes.Length - at);
                    bytes.Slice(at, through).CopyTo(output.AsSpan(count));
                    count += through;
                    at += through;
                    passing -= through;
                    continue;
                }

                if (heldCount < 5)
                {
                    var header = Math.Min(5 - heldCount, bytes.Length - at);
                    bytes.Slice(at, header).CopyTo(held.AsSpan(heldCount));
                    heldCount += header;
                    at += header;
                    if (heldCount == 5)
                    {
                        count += Begin(output.AsSpan(count));
                    }

                    continue;
                }

                var whole = 5 + Length;
                var body = Math.Min(whole - heldCount, bytes.Length - at);
                bytes.Slice(at, body).CopyTo(held.AsSpan(heldCount));
                heldCount += body;
                at += body;
                if (heldCount == whole)
                {
                    if (!IsClientHelloForTheLodestone(held.AsSpan(5, Length)))
                    {
                        throw new SharingException(null, "The server's handshake toward the Lodestone isn't a ClientHello for it alone.");
                    }

                    held.AsSpan(0, whole).CopyTo(output.AsSpan(count));
                    count += whole;
                    heldCount = 0;
                }
            }

            return output.AsMemory(0, count);
        }

        private int Length => (held[3] << 8) | held[4];

        /// <summary>A record's header is whole: checks it, and lets it through at once unless the record is a handshake, which is held whole first.</summary>
        private int Begin(Span<byte> output)
        {
            var type = held[0];
            if (held[1] != 0x03 || held[2] is < 0x01 or > 0x04 || Length > MaxRecordBytes || (!started && type != HandshakeRecord))
            {
                throw new SharingException(null, "The server's bytes toward the Lodestone aren't a TLS handshake.");
            }

            started = true;
            switch (type)
            {
                case HandshakeRecord when Length is > 0 and <= 16 * 1024:
                    return 0;
                case 20 or 21 or 23:
                    // Change cipher spec, an alert, or a protected record.
                    held.AsSpan(0, 5).CopyTo(output);
                    passing = Length;
                    heldCount = 0;
                    return 5;
                default:
                    throw new SharingException(null, "The server sent a TLS record toward the Lodestone the pipe doesn't carry.");
            }
        }
    }

    /// <summary>One exchange over one WebSocket: the signed body, then the server's requests, until its final message.</summary>
    private sealed class Exchange(LodestonePipe pipe, ClientWebSocket socket, CancellationToken deadline) : IDisposable
    {
        private readonly SemaphoreSlim sending = new(1, 1);
        private readonly CancellationTokenSource pumping = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        private Link? link;
        private Task pump = Task.CompletedTask;
        private bool openSeen;
        private bool open;
        private bool closed;
        private readonly TlsRecords records = new();
        private long toLodestone;
        private long fromLodestone;
        private volatile bool pumpFailed;

        internal async Task<SharingResponse> RunAsync(byte[] signedBody)
        {
            await SendAsync(signedBody, WebSocketMessageType.Binary).ConfigureAwait(false);
            var buffer = new byte[MaxMessageBytes + 1];
            while (true)
            {
                var (type, count) = await ReceiveAsync(buffer).ConfigureAwait(false);
                if (pumpFailed)
                {
                    throw new SharingException(null, "The pipe broke its limits.");
                }

                switch (type)
                {
                    case WebSocketMessageType.Close:
                        throw new SharingException(null, "The server ended the exchange without an answer.");
                    case WebSocketMessageType.Binary:
                        await TowardLodestoneAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                        break;
                    default:
                        var text = buffer.AsSpan(0, count);
                        if (count > MaxTextBytes)
                        {
                            throw new SharingException(null, "The server's message is longer than the exchange allows.");
                        }

                        if (text.SequenceEqual("open"u8))
                        {
                            await OpenAsync().ConfigureAwait(false);
                        }
                        else if (text.SequenceEqual("close"u8))
                        {
                            await ClosePipeAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            var answer = ReadFinal(text);
                            await ClosePipeAsync().ConfigureAwait(false);
                            await CloseQuietlyAsync().ConfigureAwait(false);
                            return answer;
                        }

                        break;
                }
            }
        }

        public void Dispose()
        {
            pumping.Cancel();
            link?.Dispose();
            try
            {
                pump.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The pump ended with the exchange; its outcome no longer matters.
            }

            pumping.Dispose();
            sending.Dispose();
        }

        /// <summary><c>open</c>: connects once, checks the address, then answers <c>opened</c> and starts carrying the Lodestone's bytes, or answers <c>failed</c>.</summary>
        private async Task OpenAsync()
        {
            if (openSeen)
            {
                throw new SharingException(null, "The server asked for a second pipe.");
            }

            openSeen = true;
            link = pipe.links();
            var connected = false;
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(deadline))
            {
                connecting.CancelAfter(pipe.ConnectTimeout);
                try
                {
                    connected = Allows(await link.ConnectAsync(connecting.Token).ConfigureAwait(false));
                }
                catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // No connection: the server is told so, unless the exchange's own time ran out.
                    connected = false;
                }
            }

            deadline.ThrowIfCancellationRequested();

            if (!connected)
            {
                link.Dispose();
                link = null;
                await SendAsync(Failed, WebSocketMessageType.Text).ConfigureAwait(false);
                return;
            }

            open = true;
            await SendAsync(Opened, WebSocketMessageType.Text).ConfigureAwait(false);
            pump = Task.Run(FromLodestoneAsync, CancellationToken.None);
        }

        /// <summary>The server's bytes toward the Lodestone: only while the pipe is open, within the total, as TLS records that <see cref="TlsRecords"/> lets through.</summary>
        private async Task TowardLodestoneAsync(ReadOnlyMemory<byte> bytes)
        {
            if (!open || closed || link is null)
            {
                throw new SharingException(null, "The server sent bytes with no pipe open.");
            }

            toLodestone += bytes.Length;
            if (toLodestone > MaxBytesToLodestone || bytes.Length > MaxMessageBytes)
            {
                throw new SharingException(null, "The server sent more toward the Lodestone than a pipe carries.");
            }

            var passing = records.Take(bytes.Span);
            if (passing.IsEmpty)
            {
                return;
            }

            try
            {
                await link.SendAsync(passing, deadline).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
            {
                // The Lodestone's side broke: the server sees the bytes stop, and its fetch fails.
            }
        }

        /// <summary>Carries the Lodestone's bytes to the server until it closes its side (then <c>eof</c>), the pipe closes, or a limit is passed.</summary>
        private async Task FromLodestoneAsync()
        {
            var chunk = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    var read = await link!.ReceiveAsync(chunk, pumping.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    fromLodestone += read;
                    if (fromLodestone > MaxBytesFromLodestone)
                    {
                        pumpFailed = true;
                        socket.Abort();
                        return;
                    }

                    await SendAsync(chunk.AsMemory(0, read), WebSocketMessageType.Binary, pumping.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The pipe closed, or the exchange ended.
                return;
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
            {
                // The Lodestone's side broke: like its close, the server is told the bytes have ended.
            }
            catch (WebSocketException)
            {
                return;
            }

            try
            {
                await SendAsync(Eof, WebSocketMessageType.Text, pumping.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // The exchange ended first.
            }
        }

        /// <summary><c>close</c>, or the final answer: the pipe stops, and its connection closes.</summary>
        private async Task ClosePipeAsync()
        {
            if (closed)
            {
                return;
            }

            closed = true;
            open = false;
            await pumping.CancelAsync().ConfigureAwait(false);
            link?.Dispose();
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // It stopped.
            }
        }

        /// <summary>After the final answer: closes in answer to the server's close, within the exchange's time.</summary>
        private async Task CloseQuietlyAsync()
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, deadline).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The answer is in; a close that doesn't complete changes nothing.
            }
        }

        private Task SendAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type) => SendAsync(bytes, type, deadline);

        /// <summary>
        /// Sends one whole message, one at a time. <paramref name="cancellation"/> stops a send that
        /// hasn't begun; one under way runs to its end within the exchange's time, since cancelling a
        /// WebSocket's send part way aborts the WebSocket, and the server's final answer with it.
        /// </summary>
        private async Task SendAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type, CancellationToken cancellation)
        {
            await sending.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                await socket.SendAsync(bytes, type, endOfMessage: true, deadline).ConfigureAwait(false);
            }
            finally
            {
                sending.Release();
            }
        }

        /// <summary>One whole message, at most <see cref="MaxMessageBytes"/>: a longer one ends the exchange.</summary>
        private async Task<(WebSocketMessageType Type, int Count)> ReceiveAsync(byte[] buffer)
        {
            var count = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(count), deadline).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (WebSocketMessageType.Close, 0);
                }

                count += result.Count;
                if (count > MaxMessageBytes)
                {
                    throw new SharingException(null, "The server's message is longer than the exchange allows.");
                }

                if (result.EndOfMessage)
                {
                    return (result.MessageType, count);
                }
            }
        }
    }
}
