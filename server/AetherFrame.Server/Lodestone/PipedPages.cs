using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Lodestone;

/// <summary>
/// The places for reads through players' own connections ("Checking a character through the
/// player's own connection" in the decision register): at most <see cref="Max"/> at once. They are
/// their own: a piped read never waits for the relay's one-at-a-time lock, and never counts against
/// <see cref="LodestoneBudget"/>'s hourly 60, which were meant for one shared address. A place is
/// taken only once the challenge is consumed and the code is valid (a check) or the binding found (a
/// re-read), and released when its holder is disposed, on every exit.
/// </summary>
internal sealed class PipedReads
{
    /// <summary>The most piped reads at once.</summary>
    public const int Max = 20;

    private int inUse;

    /// <summary>How many places are taken now: for tests.</summary>
    internal int InUse => Volatile.Read(ref inUse);

    /// <summary>A place for one piped read, or null when all are taken.</summary>
    public IDisposable? TryTake()
    {
        if (Interlocked.Increment(ref inUse) > Max)
        {
            Interlocked.Decrement(ref inUse);
            return null;
        }

        return new Place(this);
    }

    private sealed class Place(PipedReads reads) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                Interlocked.Decrement(ref reads.inUse);
            }
        }
    }
}

/// <summary>The fetch through a player's pipe. The server's is <see cref="PipedPages"/>; a test of the exchange alone gives its own.</summary>
internal interface IPipedPages
{
    Task<LodestoneRead> ReadAsync(long lodestoneId, Stream pipe, CancellationToken deadline, CancellationToken session);
}

/// <summary>
/// C2's fetch through a player's own connection: the same fixed address and User-Agent as
/// <see cref="LodestoneHttpPages"/>, no redirect, at most 1 MiB, HTTP/1.1 exactly, over a handler made
/// for that one pipe and disposed with it, never through the client factory. TLS runs from the server
/// to the Lodestone over the pipe (1.3 only), and the certificate is checked as .NET checks any:
/// the player carries only encrypted bytes. A response whose body would end only when the connection
/// closes is refused, since .NET's TLS stream reports such an end as clean without TLS's closure
/// alert (RFC 9112, section 9.8). The Lodestone's response headers are never logged.
/// </summary>
internal sealed class PipedPages(IOptions<ServerOptions> options, Worlds worlds, ILogger<PipedPages> logger) : IPipedPages
{
    /// <summary>
    /// The trust a test needs for its own TLS server, as a custom trust store. Only tests set it:
    /// nothing in configuration or options reaches it, and without it the system's trust applies.
    /// </summary>
    internal X509ChainPolicy? TrustForTests { get; set; }

    /// <summary>
    /// Reads <paramref name="lodestoneId"/>'s page over <paramref name="pipe"/>, by
    /// <paramref name="deadline"/>. A 403 or 429 is <see cref="LodestoneOutcome.Refused"/>; no
    /// complete, framed answer is <see cref="LodestoneOutcome.Unanswered"/>; anything else reads as a
    /// relayed fetch does. When <paramref name="session"/> ends the read throws instead.
    /// </summary>
    public async Task<LodestoneRead> ReadAsync(long lodestoneId, Stream pipe, CancellationToken deadline, CancellationToken session)
    {
        try
        {
            using var client = new HttpClient(CreateHandler(pipe)) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, LodestoneHttpPages.AddressOf(lodestoneId))
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            request.Headers.UserAgent.ParseAdd(LodestoneHttpPages.UserAgent(options.Value.DeploymentName));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline);
            var status = (int)response.StatusCode;
            if (status is 403 or 429)
            {
                logger.LogInformation("A Lodestone read through a player's connection was refused with status {Status}.", status);
                return new LodestoneRead(LodestoneOutcome.Refused);
            }

            if (status is not (200 or 404) || response.Content.Headers.ContentLength > LodestoneHttpPages.MaxBytes)
            {
                logger.LogInformation("A Lodestone read through a player's connection failed with status {Status}.", status);
                return new LodestoneRead(LodestoneOutcome.Failed);
            }

            if (response.Content.Headers.ContentLength is null && response.Headers.TransferEncodingChunked != true)
            {
                logger.LogInformation("A Lodestone read through a player's connection got a body with no length or chunks.");
                return new LodestoneRead(LodestoneOutcome.Unanswered);
            }

            var read = LodestoneReader.Interpret(new LodestoneResponse(status, await LodestoneHttpPages.ReadBodyAsync(response, deadline)), worlds);
            if (read.Outcome == LodestoneOutcome.Failed)
            {
                logger.LogInformation("A Lodestone read through a player's connection failed with status {Status}.", status);
            }

            return read;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !session.IsCancellationRequested)
        {
            // Only the exception's type: a message can quote a response header.
            logger.LogInformation("A Lodestone read through a player's connection got no answer ({ErrorKind}).", e.GetType().Name);
            return new LodestoneRead(LodestoneOutcome.Unanswered);
        }
    }

    /// <summary>
    /// The handler for one pipe: at most one connection, no proxy, no redirects, no cookies, no
    /// automatic decompression, no tracing headers, no draining of an unread body, TLS 1.3 only, and
    /// never a certificate-validation callback. Its connect callback hands out
    /// <paramref name="pipe"/> once, for the Lodestone's host and port only, and fails on any other
    /// call, so no connection is ever made or reused for anything else.
    /// </summary>
    internal SocketsHttpHandler CreateHandler(Stream pipe)
    {
        var handed = 0;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 1,
            MaxResponseDrainSize = 0,
            ActivityHeadersPropagator = null,
            SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls13 },
            ConnectCallback = (context, _) =>
            {
                if (Interlocked.Exchange(ref handed, 1) != 0
                    || !string.Equals(context.DnsEndPoint.Host, LodestoneHttpPages.Origin.Host, StringComparison.OrdinalIgnoreCase)
                    || context.DnsEndPoint.Port != LodestoneHttpPages.Origin.Port)
                {
                    throw new InvalidOperationException("A pipe carries one connection, to the Lodestone alone.");
                }

                return ValueTask.FromResult(pipe);
            },
        };

        if (TrustForTests is { } trust)
        {
            handler.SslOptions.CertificateChainPolicy = trust.Clone();
        }

        return handler;
    }
}
