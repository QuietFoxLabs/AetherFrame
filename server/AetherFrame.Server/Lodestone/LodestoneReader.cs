using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AetherFrame.Server.Lodestone;

/// <summary>The raw answer to one fetch: a status (0 when the request itself failed) and, within the size bound, the body.</summary>
internal sealed record LodestoneResponse(int Status, string? Html);

/// <summary>Fetches one Lodestone character page. The server's is <see cref="LodestoneHttpPages"/>; tests give their own.</summary>
internal interface ILodestonePages
{
    Task<LodestoneResponse> GetAsync(long lodestoneId, CancellationToken cancellation);
}

/// <summary>
/// The only way the server reaches the Lodestone (decision C2): <c>GET</c> of
/// <c>https://na.finalfantasyxiv.com/lodestone/character/&lt;id&gt;/</c>, which serves every region's
/// characters, with a fixed User-Agent, no redirect followed, at most 1 MiB read, and one 10-second
/// deadline over the whole fetch, the body included. The host is fixed here, never configured or
/// taken from a request, so the server can't be used as a proxy. When the Lodestone refuses the
/// server's host, the fetch goes through the operator's Lodestone relay (<see cref="CreateHandler"/>).
/// </summary>
internal sealed class LodestoneHttpPages(IHttpClientFactory clients) : ILodestonePages
{
    public const string ClientName = "lodestone";

    public const int MaxBytes = 1024 * 1024;

    public static readonly Uri Origin = new("https://na.finalfantasyxiv.com/", UriKind.Absolute);

    /// <summary>The deadline over one whole fetch: connecting, the headers and the body (decision C2).</summary>
    internal TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The address of a character's page: the one address the server ever fetches (decision C2).</summary>
    public static Uri AddressOf(long lodestoneId) => new(Origin, "lodestone/character/" + lodestoneId.ToString(CultureInfo.InvariantCulture) + "/");

    /// <summary>The fixed User-Agent of every Lodestone fetch: AetherFrame and the deployment's own name (decision C2).</summary>
    public static string UserAgent(string deploymentName) => $"AetherFrame-Server/1 (+https://{deploymentName}/)";

    public async Task<LodestoneResponse> GetAsync(long lodestoneId, CancellationToken cancellation)
    {
        var address = AddressOf(lodestoneId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(Deadline);
        var token = deadline.Token;
        try
        {
            using var client = clients.CreateClient(ClientName);
            using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token);
            var status = (int)response.StatusCode;
            if (status is not (200 or 404) || response.Content.Headers.ContentLength > MaxBytes)
            {
                return new LodestoneResponse(status, null);
            }

            return new LodestoneResponse(status, await ReadBodyAsync(response, token));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException && !cancellation.IsCancellationRequested)
        {
            return new LodestoneResponse(0, null);
        }
    }

    /// <summary>A page's body as text, or null once it passes <see cref="MaxBytes"/>: nothing past the bound is read.</summary>
    internal static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// The client's handler: no redirects, no cookies, no proxy from the environment. With a
    /// <paramref name="relay"/> (<see cref="ServerOptions.LodestoneRelay"/>), each connection is an
    /// HTTPS tunnel through it (<c>CONNECT</c>): TLS still runs to the Lodestone, and its certificate
    /// is checked as before. Its connections are dropped when idle well before the relay drops them.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(IPEndPoint? relay)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        if (relay is not null)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(new Uri("http://" + relay + "/", UriKind.Absolute)) { BypassProxyOnLocal = false, UseDefaultCredentials = false };
            handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15);
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
        }

        return handler;
    }
}

/// <summary>How a read of a character's page ended.</summary>
internal enum LodestoneOutcome
{
    /// <summary>The page was read, and its fields passed.</summary>
    Found,

    /// <summary>The Lodestone's own "not found" page.</summary>
    NotFound,

    /// <summary>Anything else: an outage, a redirect, a changed layout, a refused field.</summary>
    Failed,

    /// <summary>The fetch budget or queue is full; nothing was fetched.</summary>
    Busy,

    /// <summary>
    /// A read through the player's own connection that the Lodestone turned away, with a 403 or a
    /// 429: the player's connection, not the page, is at fault.
    /// </summary>
    Refused,

    /// <summary>
    /// A read through the player's own connection that got no complete, framed answer: the plugin
    /// couldn't connect or didn't in time, the fetch's deadline passed, the connection broke or was
    /// cut off, TLS failed (a certificate the server doesn't trust, say), or the body was ended only
    /// by the connection closing. Never "not found".
    /// </summary>
    Unanswered,
}

internal sealed record LodestoneRead(LodestoneOutcome Outcome, LodestoneCharacter? Character = null);

/// <summary>
/// The server-wide Lodestone fetch budget (decision C2): at most 60 fetches an hour, and at most 20
/// waiting or in flight. Re-reads (decision C1) use only the first half of the hour's budget, so
/// they never crowd out a player's check.
/// </summary>
internal sealed class LodestoneBudget(TimeProvider time)
{
    public const int PerHour = 60;

    public const int MaxQueued = 20;

    private readonly Queue<DateTimeOffset> recent = new();
    private readonly object gate = new();
    private int inFlight;

    public bool TryAcquire(bool reread)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            while (recent.Count > 0 && now - recent.Peek() >= TimeSpan.FromHours(1))
            {
                recent.Dequeue();
            }

            var limit = reread ? PerHour / 2 : PerHour;
            if (recent.Count >= limit || inFlight >= MaxQueued)
            {
                return false;
            }

            recent.Enqueue(now);
            inFlight++;
            return true;
        }
    }

    /// <summary>Whether a fetch could start now, without taking a place: checked before anything that differs between requests.</summary>
    public bool HasRoom(bool reread)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            var recentCount = 0;
            foreach (var at in recent)
            {
                if (now - at < TimeSpan.FromHours(1))
                {
                    recentCount++;
                }
            }

            return recentCount < (reread ? PerHour / 2 : PerHour) && inFlight < MaxQueued;
        }
    }

    public void Release()
    {
        lock (gate)
        {
            inFlight--;
        }
    }
}

/// <summary>Reads a character's page within the budget, and parses it. The page is discarded; nothing of it is logged.</summary>
internal sealed class LodestoneReader(ILodestonePages pages, LodestoneBudget budget, Worlds worlds, ILogger<LodestoneReader> logger)
{
    private readonly SemaphoreSlim oneAtATime = new(1, 1);

    public async Task<LodestoneRead> ReadAsync(long lodestoneId, bool reread, CancellationToken cancellation)
    {
        if (!budget.TryAcquire(reread))
        {
            return new LodestoneRead(LodestoneOutcome.Busy);
        }

        try
        {
            await oneAtATime.WaitAsync(cancellation);
            LodestoneResponse response;
            try
            {
                response = await pages.GetAsync(lodestoneId, cancellation);
            }
            finally
            {
                oneAtATime.Release();
            }

            var read = Interpret(response, worlds);
            if (read.Outcome == LodestoneOutcome.Failed)
            {
                logger.LogInformation("A Lodestone read failed with status {Status}.", response.Status);
            }

            return read;
        }
        finally
        {
            budget.Release();
        }
    }

    /// <summary>
    /// What a fetched page says (decisions C1 and C2): the Lodestone's own "not found" page with a 404
    /// is <see cref="LodestoneOutcome.NotFound"/>, a character page that passes with a 200 is
    /// <see cref="LodestoneOutcome.Found"/>, and anything else is <see cref="LodestoneOutcome.Failed"/>.
    /// </summary>
    internal static LodestoneRead Interpret(LodestoneResponse response, Worlds worlds)
    {
        if (response is { Status: 404, Html: { } missing } && LodestonePage.IsNotFoundPage(missing))
        {
            return new LodestoneRead(LodestoneOutcome.NotFound);
        }

        if (response is { Status: 200, Html: { } html } && LodestonePage.Read(html, worlds) is { } character)
        {
            return new LodestoneRead(LodestoneOutcome.Found, character);
        }

        return new LodestoneRead(LodestoneOutcome.Failed);
    }
}
