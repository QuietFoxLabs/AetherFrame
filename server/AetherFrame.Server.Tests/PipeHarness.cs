using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Lodestone;

namespace AetherFrame.Server.Tests;

/// <summary>
/// A certificate authority made for the tests, and the certificates a stand-in Lodestone answers
/// with. The server trusts the authority only through <see cref="PipedPages.TrustForTests"/>.
/// </summary>
internal static class TestAuthority
{
    private static readonly Lazy<X509Certificate2> Root = new(CreateRoot);

    private static readonly Lazy<X509Certificate2> LodestoneCertificate = new(() => Issue(LodestoneHttpPages.Origin.Host));

    private static readonly Lazy<X509Certificate2> OtherNameCertificate = new(() => Issue("lodestone.example.com"));

    private static readonly Lazy<X509Certificate2> SelfSignedCertificate = new(() => SelfSigned(LodestoneHttpPages.Origin.Host));

    /// <summary>The Lodestone's name, issued by the test authority: what a pipe to the right place answers with.</summary>
    public static X509Certificate2 Lodestone => LodestoneCertificate.Value;

    /// <summary>Another name, issued by the same authority.</summary>
    public static X509Certificate2 OtherName => OtherNameCertificate.Value;

    /// <summary>The Lodestone's name, signed by its own key: what a player forging a page would have to answer with.</summary>
    public static X509Certificate2 OwnCertificate => SelfSignedCertificate.Value;

    /// <summary>A chain policy that trusts the test authority alone, with no revocation check (it publishes none).</summary>
    public static X509ChainPolicy Trust()
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        policy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(Root.Value.RawData));
        return policy;
    }

    private static X509Certificate2 CreateRoot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=AetherFrame Test Authority", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Loaded again from its PFX, as TLS on Windows needs a key it can find.
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    private static X509Certificate2 Issue(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Leaf(name, key);
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root.Value, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = request.Create(Root.Value, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var made = Leaf(name, key).CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    private static CertificateRequest Leaf(string name, ECDsa key)
    {
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        return request;
    }
}

/// <summary>
/// A stand-in for the Lodestone on loopback, which a test's plugin connects its pipe to: TLS with the
/// certificate the test chooses, then one answer to the first request on each connection. It records
/// each request's head. Nothing here reaches the real Lodestone.
/// </summary>
internal sealed class TlsLodestone : IAsyncDisposable
{
    /// <summary>A response header a log must never show: CloudFront's edge location would tell a player's region.</summary>
    public const string EdgeHeaderValue = "TEST-EDGE-7731";

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly X509Certificate2 certificate;
    private readonly SslProtocols protocols;
    private int connections;

    public TlsLodestone(X509Certificate2? certificate = null, SslProtocols protocols = SslProtocols.Tls12 | SslProtocols.Tls13)
    {
        this.certificate = certificate ?? TestAuthority.Lodestone;
        this.protocols = protocols;
        listener.Start();
        _ = AcceptAsync();
    }

    public IPEndPoint EndPoint => (IPEndPoint)listener.LocalEndpoint;

    /// <summary>The answer's bytes, given the request's head: by default a 404 with the Lodestone's own "not found" page.</summary>
    public Func<string, byte[]> Answer { get; set; } = _ => Framed(404, LodestoneHtml.NotFoundPage);

    /// <summary>Whether the connection closes right after the answer, as a body ended only by the close needs.</summary>
    public bool CloseAfterAnswer { get; set; }

    /// <summary>Whether it reads the request and never answers.</summary>
    public bool Stall { get; set; }

    /// <summary>Each request's head, as it arrived inside TLS.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Whether any request arrived inside TLS: only possible once the server accepted the certificate.</summary>
    public bool SawRequest => !Requests.IsEmpty;

    public int Connections => Volatile.Read(ref connections);

    /// <summary>A response framed by its length, carrying the edge header.</summary>
    public static byte[] Framed(int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return Concat($"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nX-Amz-Cf-Pop: {EdgeHeaderValue}\r\n\r\n", bytes);
    }

    /// <summary>A response in chunked encoding, in chunks of at most 16 KiB.</summary>
    public static byte[] Chunked(int status, string body) => Chunked(status, Encoding.UTF8.GetBytes(body));

    /// <summary>A response in chunked encoding, in chunks of at most 16 KiB.</summary>
    public static byte[] Chunked(int status, byte[] body)
    {
        using var answer = new MemoryStream();
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: text/html; charset=utf-8\r\nTransfer-Encoding: chunked\r\nX-Amz-Cf-Pop: {EdgeHeaderValue}\r\n\r\n");
        answer.Write(head);
        for (var offset = 0; offset < body.Length; offset += 16 * 1024)
        {
            var length = Math.Min(16 * 1024, body.Length - offset);
            answer.Write(Encoding.ASCII.GetBytes(length.ToString("x", CultureInfo.InvariantCulture) + "\r\n"));
            answer.Write(body, offset, length);
            answer.Write("\r\n"u8);
        }

        answer.Write("0\r\n\r\n"u8);
        return answer.ToArray();
    }

    /// <summary>A response whose body ends only when the connection closes: no length, no chunks.</summary>
    public static byte[] Unframed(int status, string body) =>
        Concat($"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n\r\n", Encoding.UTF8.GetBytes(body));

    /// <summary>A response that says it is longer than it is: the rest never comes.</summary>
    public static byte[] CutOff(int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return Concat($"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length + 1000}\r\n\r\n", bytes);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        stop.Dispose();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        _ => "Status",
    };

    private static byte[] Concat(string head, byte[] body)
    {
        var bytes = new byte[Encoding.ASCII.GetByteCount(head) + body.Length];
        var written = Encoding.ASCII.GetBytes(head, bytes);
        body.CopyTo(bytes, written);
        return bytes;
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref connections);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = protocols }, stop.Token);
                var head = await ReadHeadAsync(tls);
                if (head is null)
                {
                    return;
                }

                Requests.Enqueue(head);
                if (!Stall)
                {
                    await tls.WriteAsync(Answer(head), stop.Token);
                    await tls.FlushAsync(stop.Token);
                    if (CloseAfterAnswer)
                    {
                        return;
                    }
                }

                // Kept open, as the Lodestone keeps a connection alive, until the client closes it.
                var drain = new byte[4096];
                while (await tls.ReadAsync(drain, stop.Token) > 0)
                {
                }
            }
            catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException or SocketException or System.ComponentModel.Win32Exception)
            {
                // A client that refused the certificate, or closed the connection: nothing to answer.
            }
        }
    }

    /// <summary>A request's head, up to its empty line, at most 16 KiB; null when the connection ends first.</summary>
    private async Task<string?> ReadHeadAsync(Stream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one, stop.Token) == 0)
            {
                return null;
            }

            head.Add(one[0]);
            if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
            {
                return Encoding.ASCII.GetString(head.ToArray());
            }
        }

        return null;
    }
}

/// <summary>What the plugin does when the server sends <c>open</c>.</summary>
internal enum OpenAnswer
{
    /// <summary>Connects to <see cref="PluginPipe.Lodestone"/> and answers <c>opened</c>.</summary>
    Connect,

    /// <summary>Answers <c>failed</c>.</summary>
    Fail,

    /// <summary>Never answers.</summary>
    Ignore,

    /// <summary>Connects, and answers <c>opened</c> twice.</summary>
    OpenedTwice,

    /// <summary>Answers with a binary message, as if it forwarded bytes before connecting.</summary>
    Bytes,
}

/// <summary>What one WebSocket exchange came to, as the plugin saw it.</summary>
internal sealed class PipeRun
{
    /// <summary>The status that refused the upgrade, if the server didn't accept it.</summary>
    public int? RefusedWith { get; set; }

    /// <summary>The server's text messages other than the final one, in order: <c>open</c> and <c>close</c>.</summary>
    public List<string> Texts { get; } = [];

    /// <summary>The final message, if one came.</summary>
    public JsonElement? Final { get; set; }

    /// <summary>When <c>close</c> came, from the first message's sending.</summary>
    public TimeSpan? CloseAt { get; set; }

    /// <summary>When the final message came, from the first message's sending.</summary>
    public TimeSpan? FinalAt { get; set; }

    /// <summary>Whether the server dropped the WebSocket, or closed it, with no final message.</summary>
    public bool Cut { get; set; }

    /// <summary>The final message's status.</summary>
    public int Status => Final?.GetProperty("status").GetInt32() ?? throw new Xunit.Sdk.XunitException("No final message came: " + (Cut ? "the session was cut." : "none."));

    /// <summary>The final message's body, as a POST would have got it.</summary>
    public JsonElement Body => Final!.Value.GetProperty("body");

    /// <summary>The final message's reason, if it has one.</summary>
    public string? Reason => Final is { } final && final.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
}

/// <summary>
/// The plugin's side of the WebSocket exchange (ServerApi-v1.md, section 2.3), as the tests play it,
/// through the test host's WebSocket client: the first message, then <c>open</c> answered as
/// <see cref="OnOpen"/> says, the pipe's bytes forwarded both ways to <see cref="Lodestone"/>, and
/// <c>eof</c> when it closes its side. Every wait is bounded, so a test fails rather than hangs.
/// </summary>
internal sealed class PluginPipe
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static int nextAddress;

    /// <summary>The address the upgrade comes from: a fresh one each time unless a test names one, so tests don't share an address's limits.</summary>
    public IPAddress Address { get; init; } = NextAddress();

    /// <summary>An <c>Origin</c> header to send, which plugins never do.</summary>
    public string? Origin { get; init; }

    /// <summary>Where <c>open</c> connects.</summary>
    public IPEndPoint? Lodestone { get; init; }

    public OpenAnswer OnOpen { get; init; } = OpenAnswer.Connect;

    /// <summary>A plugin that keeps forwarding its connection's bytes after <c>close</c>.</summary>
    public bool KeepForwardingAfterClose { get; init; }

    /// <summary>How long the plugin waits after the upgrade before its first message.</summary>
    public TimeSpan FirstMessageDelay { get; init; }

    /// <summary>Anything the plugin sends right after its first message.</summary>
    public Func<WebSocket, Task>? AfterFirstMessage { get; init; }

    /// <summary>Anything the plugin sends right after <c>opened</c>.</summary>
    public Func<WebSocket, Task>? AfterOpened { get; init; }

    /// <summary>Anything the plugin sends right after <c>close</c>.</summary>
    public Func<WebSocket, Task>? AfterClose { get; init; }

    /// <summary>A fresh address in the documentation ranges (RFC 5737), one per call; they come round again after 512.</summary>
    public static IPAddress NextAddress()
    {
        var next = Interlocked.Increment(ref nextAddress);
        return (next & 0x100) == 0 ? new IPAddress([203, 0, 113, (byte)next]) : new IPAddress([198, 51, 100, (byte)next]);
    }

    /// <summary>Sends <paramref name="text"/> as one text message.</summary>
    public static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    /// <summary>Runs one exchange at <paramref name="path"/> with <paramref name="firstMessage"/> as its first message.</summary>
    public async Task<PipeRun> RunAsync(TestServer server, string path, byte[] firstMessage)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var client = server.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.HttpContext.Connection.RemoteIpAddress = Address;
            if (Origin is not null)
            {
                request.Headers.Origin = Origin;
            }
        };

        WebSocket socket;
        try
        {
            socket = await client.ConnectAsync(new Uri("ws://localhost" + path), timeout.Token);
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("Incomplete handshake, status code: ", StringComparison.Ordinal))
        {
            return new PipeRun { RefusedWith = int.Parse(e.Message["Incomplete handshake, status code: ".Length..], CultureInfo.InvariantCulture) };
        }

        using (socket)
        {
            return await ExchangeAsync(socket, firstMessage);
        }
    }

    /// <summary>Runs the plugin's side of one exchange on <paramref name="socket"/>, already open, from the first message to the final one.</summary>
    public async Task<PipeRun> ExchangeAsync(WebSocket socket, byte[] firstMessage)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var run = new PipeRun();
        var sending = new SemaphoreSlim(1, 1);
        TcpClient? connection = null;
        var forwarding = Task.CompletedTask;
        var clock = Stopwatch.StartNew();
        try
        {
            if (FirstMessageDelay > TimeSpan.Zero)
            {
                await Task.Delay(FirstMessageDelay, timeout.Token);
            }

            if (!await TrySendAsync(socket, sending, firstMessage, WebSocketMessageType.Binary))
            {
                run.Cut = true;
                return run;
            }

            if (AfterFirstMessage is not null)
            {
                await AfterFirstMessage(socket);
            }

            while (true)
            {
                var (type, bytes) = await ReceiveAsync(socket, timeout.Token);
                if (type is null or WebSocketMessageType.Close)
                {
                    run.Cut = run.Final is null;
                    if (type is not null)
                    {
                        await TryCloseAsync(socket);
                    }

                    return run;
                }

                if (type == WebSocketMessageType.Binary)
                {
                    if (connection is { Connected: true })
                    {
                        try
                        {
                            await connection.GetStream().WriteAsync(bytes, timeout.Token);
                        }
                        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                        {
                        }
                    }

                    continue;
                }

                var text = Encoding.UTF8.GetString(bytes);
                if (text == "open")
                {
                    run.Texts.Add(text);
                    if (OnOpen is OpenAnswer.Fail)
                    {
                        await TrySendAsync(socket, sending, "failed"u8.ToArray(), WebSocketMessageType.Text);
                    }
                    else if (OnOpen is OpenAnswer.Bytes)
                    {
                        await TrySendAsync(socket, sending, [0x16, 0x03, 0x03], WebSocketMessageType.Binary);
                    }
                    else if (OnOpen is OpenAnswer.Connect or OpenAnswer.OpenedTwice)
                    {
                        connection = new TcpClient();
                        await connection.ConnectAsync(Lodestone!, timeout.Token);
                        forwarding = ForwardAsync(connection.GetStream(), socket, sending);
                        await TrySendAsync(socket, sending, "opened"u8.ToArray(), WebSocketMessageType.Text);
                        if (OnOpen is OpenAnswer.OpenedTwice)
                        {
                            await TrySendAsync(socket, sending, "opened"u8.ToArray(), WebSocketMessageType.Text);
                        }

                        if (AfterOpened is not null)
                        {
                            await AfterOpened(socket);
                        }
                    }
                }
                else if (text == "close")
                {
                    run.Texts.Add(text);
                    run.CloseAt = clock.Elapsed;
                    if (!KeepForwardingAfterClose)
                    {
                        connection?.Dispose();
                    }

                    if (AfterClose is not null)
                    {
                        await AfterClose(socket);
                    }
                }
                else
                {
                    run.FinalAt = clock.Elapsed;
                    using var final = JsonDocument.Parse(text);
                    run.Final = final.RootElement.Clone();
                }
            }
        }
        finally
        {
            connection?.Dispose();
            await forwarding.WaitAsync(Wait);
        }
    }

    /// <summary>The plugin's connection's bytes, forwarded as binary messages, then <c>eof</c> when it ends.</summary>
    private static async Task ForwardAsync(Stream from, WebSocket to, SemaphoreSlim sending)
    {
        // The first bytes come only after "opened" goes: the server sends its ClientHello once it reads it.
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer)) > 0)
            {
                if (!await TrySendAsync(to, sending, buffer.AsMemory(0, read).ToArray(), WebSocketMessageType.Binary))
                {
                    return;
                }
            }

            await TrySendAsync(to, sending, "eof"u8.ToArray(), WebSocketMessageType.Text);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or SocketException)
        {
            // The plugin closed its connection, at "close".
        }
    }

    private static async Task<bool> TrySendAsync(WebSocket socket, SemaphoreSlim sending, byte[] bytes, WebSocketMessageType type)
    {
        await sending.WaitAsync();
        try
        {
            await socket.SendAsync(bytes, type, endOfMessage: true, CancellationToken.None);
            return true;
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            return false;
        }
        finally
        {
            sending.Release();
        }
    }

    private static async Task<(WebSocketMessageType? Type, byte[] Bytes)> ReceiveAsync(WebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[128 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellation);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (WebSocketMessageType.Close, []);
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return (result.MessageType, message.ToArray());
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            return (null, []);
        }
    }

    private static async Task TryCloseAsync(WebSocket socket)
    {
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException)
        {
        }
    }
}

/// <summary>The signed bodies a plugin sends, as a WebSocket's first message.</summary>
internal static class SignedBodies
{
    /// <summary>The signed body of <paramref name="body"/> as <paramref name="kind"/>, under a fresh challenge.</summary>
    public static async Task<byte[]> SignAsync(this Player player, RequestProofKind kind, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var challenge = await player.ChallengeAsync();
        var proof = RequestProofCodec.SignAction(kind, bytes, DeploymentName.Parse(TestServer.Deployment), challenge, player.Key);
        return Player.Envelope(proof, bytes);
    }

    /// <summary>A check's signed body, claiming <paramref name="name"/> of <paramref name="world"/>.</summary>
    public static Task<byte[]> CheckBodyAsync(this Player player, long lodestoneId, string code, string name = "Aria Starfall", string world = "Gilgamesh") =>
        player.SignAsync(RequestProofKind.LodestoneCheck, $"{{\"lodestoneId\":\"{lodestoneId}\",\"code\":\"{code}\",\"name\":\"{name}\",\"world\":\"{world}\"}}");

    /// <summary>A re-read's signed body.</summary>
    public static Task<byte[]> RereadBodyAsync(this Player player) => player.SignAsync(RequestProofKind.LodestoneReread, "{}");
}
