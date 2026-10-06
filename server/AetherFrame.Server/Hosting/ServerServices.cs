using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Server.Lodestone;
using AetherFrame.Server.Requests;
using AetherFrame.Server.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Hosting;

/// <summary>The JSON the server answers with: camelCase properties, nothing else configured.</summary>
internal static class ServerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// The stage 1 allowlist of Lodestone ids (decision C8), read from the current configuration at
/// every use, so removing an id takes effect as soon as the configuration reloads. With
/// <see cref="ServerOptions.OpenToEveryone"/>, and I2's condition met, it allows every id (the
/// open alpha).
/// </summary>
internal sealed class Allowlist(IOptionsMonitor<ServerOptions> options, ILogger<Allowlist> logger)
{
    public bool Allows(long lodestoneId)
    {
        IReadOnlyList<string> ids;
        try
        {
            var current = options.CurrentValue;
            if (current.IsOpen)
            {
                return true;
            }

            ids = current.AllowedLodestoneIds;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            // A configuration that can't be read as a list allows no one.
            logger.LogWarning("The allowlist can't be read ({ErrorKind}); it allows no one until it can.", e.GetType().Name);
            return false;
        }

        foreach (var text in ids)
        {
            if (LodestoneIds.TryParse(text, out var id) && id == lodestoneId)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The request log (decision S5): for each request, its id, the route's template, the status, the
/// duration and a failure's kind. Never an address, a body, a proof, a challenge, a code, a marker,
/// an identifier, a Lodestone id, a name or a World. Every response gets <c>Cache-Control: no-store</c>
/// and <c>X-Content-Type-Options: nosniff</c> here too.
/// </summary>
internal sealed class RequestLog(RequestDelegate next, ILogger<RequestLog> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var started = Stopwatch.GetTimestamp();
        http.Response.OnStarting(() =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Task.CompletedTask;
        });

        try
        {
            await next(http);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            http.Items[SignedRequests.ErrorKindItem] = "exception:" + e.GetType().Name;
            if (!http.Response.HasStarted)
            {
                http.Response.Clear();
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }
        finally
        {
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(none)";
            var kind = http.Items[SignedRequests.ErrorKindItem] as string ?? "-";
            logger.LogInformation(
                "{RequestId} {Route} {Status} {DurationMs} {ErrorKind}",
                http.TraceIdentifier,
                route,
                http.Response.StatusCode,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                kind);
        }
    }
}

/// <summary>Creates the database's tables before the server takes requests.</summary>
internal sealed class DatabaseStartup(ServerDatabase database) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => database.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The daily re-read (decision C1): each binding's Lodestone page is read about once a day, paced
/// evenly over the day, within the half of the fetch budget re-reads may use. A page that was read
/// updates the name and World, and the day of the binding's last successful read; the Lodestone's own
/// "not found" page, twice a day apart, removes the binding; anything else leaves it alone. It runs
/// only while the operator's Lodestone relay is set ("Checking a character through the player's own
/// connection"): without one, players' own re-reads keep names current, and a binding not read within
/// 30 days stops answering lookups.
/// </summary>
internal sealed class Rereads(BindingStore bindings, LodestoneReader lodestone, Allowlist allowlist, AetherFrame.Server.Presence.PresenceStore presence, IOptions<ServerOptions> options, TimeProvider time, ILogger<Rereads> logger) : BackgroundService
{
    /// <summary>The shortest pause between two re-reads.</summary>
    public static readonly TimeSpan MinimumPause = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RereadsEnabled || options.Value.Relay is null)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Nothing here may stop the host: every failure is logged by its type and the loop goes on.
            try
            {
                var personas = await bindings.ListPersonasAsync(stoppingToken);
                var pause = personas.Count == 0 ? TimeSpan.FromHours(1) : TimeSpan.FromTicks(Math.Max(MinimumPause.Ticks, TimeSpan.FromDays(1).Ticks / personas.Count));
                foreach (var persona in personas)
                {
                    await Task.Delay(pause, time, stoppingToken);
                    try
                    {
                        await RereadAsync(persona, stoppingToken);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        logger.LogWarning("A re-read failed with {ErrorKind}.", e.GetType().Name);
                    }
                }

                if (personas.Count == 0)
                {
                    await Task.Delay(pause, time, stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("The re-read schedule failed with {ErrorKind}.", e.GetType().Name);
                await Task.Delay(MinimumPause, time, stoppingToken);
            }
        }
    }

    /// <summary>Re-reads one binding's page. Internal, so tests can drive it without the schedule.</summary>
    internal async Task RereadAsync(Protocol.Identity.PersonaId persona, CancellationToken cancellation)
    {
        var binding = await bindings.FindByPersonaAsync(persona, cancellation);
        if (binding is null || !allowlist.Allows(binding.LodestoneId))
        {
            return;
        }

        var read = await lodestone.ReadAsync(binding.LodestoneId, reread: true, cancellation);
        if (read.Outcome is not (LodestoneOutcome.Found or LodestoneOutcome.NotFound))
        {
            return;
        }

        if (await bindings.ApplyRereadAsync(persona, binding.LodestoneId, read.Character, cancellation) == RereadResult.Removed)
        {
            // The binding is gone: the character stops counting as online at once, instead of
            // staying counted while its heartbeats keep a session the start's check passed.
            presence.ForgetKey(persona);
        }
    }
}
