using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.ImageJobs;
using AetherFrame.ImageWorker;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Hosting;
using AetherFrame.Server.Images;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>
/// The health check (ServerApi-v1.md, section 3; known bug 14): the three signals and their times,
/// <c>GET /v1/health</c>'s one answer, the worker's connections, the image canary, the backup's
/// outcome, and the log.
/// </summary>
public class ServerHealthTests
{
    private const long Aria = 12345678;
    private const long Bram = 23456789;

    private static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFreshServer_HasNoWorkerUntilARunConnects_AndImagesAndTheBackupHealthyForTheirGrace()
    {
        var time = new ManualTime(Start);
        var health = NewHealth(time);
        Assert.Equal(new HealthAnswer(false, true, true), health.Current);

        // The worker has no grace: unhealthy from the start until a run connects, a few seconds in.
        time.Now = Start + TimeSpan.FromSeconds(5);
        Assert.Equal(new HealthAnswer(false, true, true), health.Current);
        health.WorkerConnected();
        Assert.Equal(new HealthAnswer(true, true, true), health.Current);

        // Images and the backup, with nothing finished yet, have 15 minutes.
        time.Now = Start + TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1);
        Assert.Equal(new HealthAnswer(false, true, true), health.Current);
        time.Now = Start + TimeSpan.FromMinutes(15);
        Assert.Equal(new HealthAnswer(false, false, false), health.Current);
    }

    [Fact]
    public void TheWorker_IsHealthyForTwoMinutesAfterEachConnection()
    {
        var time = new ManualTime(Start);
        var health = NewHealth(time);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(health.Current.Worker);

        health.WorkerConnected();
        Assert.True(health.Current.Worker);
        time.Advance(TimeSpan.FromSeconds(119));
        Assert.True(health.Current.Worker);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(health.Current.Worker);
        health.WorkerConnected();
        Assert.True(health.Current.Worker);

        // Only a connection counts, never the start: with none, or only an old one, the worker is
        // unhealthy at the start itself.
        var state = health.State with { LastWorkerConnection = null };
        Assert.False(ServerHealth.Evaluate(Start, state).Worker);
        state = state with { LastWorkerConnection = Start - TimeSpan.FromHours(1) };
        Assert.False(ServerHealth.Evaluate(Start, state).Worker);
    }

    [Fact]
    public void Images_AreHealthyForThreeHoursAfterAGoodCanary_AndNotAfterTwoFailuresInARow()
    {
        var time = new ManualTime(Start);
        var health = NewHealth(time);
        time.Advance(TimeSpan.FromHours(5));
        Assert.False(health.Current.Images);

        health.CanaryFinished(succeeded: true);
        Assert.True(health.Current.Images);
        time.Advance(TimeSpan.FromHours(3) - TimeSpan.FromTicks(1));
        Assert.True(health.Current.Images);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.False(health.Current.Images);

        // One failure is no verdict; two in a row are, until a canary succeeds again.
        health.CanaryFinished(succeeded: true);
        health.CanaryFinished(succeeded: false);
        Assert.True(health.Current.Images);
        health.CanaryFinished(succeeded: false);
        Assert.False(health.Current.Images);
        health.CanaryFinished(succeeded: false);
        Assert.False(health.Current.Images);
        health.CanaryFinished(succeeded: true);
        Assert.True(health.Current.Images);

        // Within the start's grace too.
        var fresh = NewHealth(new ManualTime(Start));
        fresh.CanaryFinished(succeeded: false);
        Assert.True(fresh.Current.Images);
        fresh.CanaryFinished(succeeded: false);
        Assert.False(fresh.Current.Images);
    }

    [Fact]
    public void TheBackup_IsHealthyFor26HoursAfterAGoodRun_AndNotAfterAFailedOne()
    {
        var time = new ManualTime(Start);
        var health = NewHealth(time);
        health.BackupFinished(succeeded: true);
        time.Advance(TimeSpan.FromHours(26) - TimeSpan.FromTicks(1));
        Assert.True(health.Current.Backup);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.False(health.Current.Backup);
        health.BackupFinished(succeeded: true);
        Assert.True(health.Current.Backup);

        // A failed run is unhealthy at once, however recent the last good one, until a run succeeds.
        health.BackupFinished(succeeded: false);
        Assert.False(health.Current.Backup);
        health.BackupFinished(succeeded: true);
        Assert.True(health.Current.Backup);

        // So is a first run that fails within the start's grace.
        var fresh = NewHealth(new ManualTime(Start));
        fresh.BackupFinished(succeeded: false);
        Assert.False(fresh.Current.Backup);
    }

    [Fact]
    public void WithNoWorkerOrBackupFolder_TheirSignalsAreNeverHealthy()
    {
        var time = new ManualTime(Start);
        var health = new ServerHealth(Options.Create(new ServerOptions()), time);
        Assert.False(health.WorkerConfigured);
        Assert.Equal(new HealthAnswer(false, false, false), health.Current);
        health.WorkerConnected();
        health.CanaryFinished(succeeded: true);
        health.BackupFinished(succeeded: true);
        Assert.Equal(new HealthAnswer(false, false, false), health.Current);

        // Either worker setting is a worker, healthy once a run connects.
        foreach (var options in new[] { new ServerOptions { ImageWorkerSocket = "images.sock" }, new ServerOptions { ImageWorkerRuns = "runs" } })
        {
            var configured = new ServerHealth(Options.Create(options), time);
            Assert.True(configured.WorkerConfigured);
            configured.WorkerConnected();
            Assert.True(configured.Current.Worker);
        }
    }

    [Fact]
    public void TheWatch_LogsEachChangeOnce_ByItsKindAlone()
    {
        var time = new ManualTime(Start);
        var health = new ServerHealth(Options.Create(new ServerOptions { ImageWorkerRuns = "runs" }), time);
        var log = new CapturedLog();
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(log));
        using var watch = new HealthWatch(health, time, factory.CreateLogger<HealthWatch>());

        // No backup folder, and no run connected yet: both unhealthy from the first look, and each said once.
        watch.CheckOnce();
        watch.CheckOnce();
        time.Advance(TimeSpan.FromMinutes(2));
        watch.CheckOnce();
        watch.CheckOnce();
        health.WorkerConnected();
        watch.CheckOnce();
        watch.CheckOnce();
        Assert.Equal(
            ["Server health: the image worker turned unhealthy.", "Server health: the backup turned unhealthy.", "Server health: the image worker turned healthy again."],
            log.Lines.Select(Message));
        Assert.All(log.Lines, line => Assert.Contains(" Warning: ", line, StringComparison.Ordinal));

        time.Advance(TimeSpan.FromMinutes(15));
        watch.CheckOnce();
        Assert.Equal(
            ["Server health: the image worker turned unhealthy.", "Server health: image processing turned unhealthy."],
            log.Lines.Skip(3).Select(Message));
    }

    [Fact]
    public async Task TheRoute_AnswersExactlyThreeBooleans_AsJson_WithTheUsualHeaders()
    {
        using var server = new TestServer { UseImageWorkerRuns = true };
        using var client = server.CreateClient();
        Assert.Equal("{\"worker\":false,\"images\":true,\"backup\":false}", Encoding.UTF8.GetString(await HealthAsync(client)));

        // A run connects: once the next socket is offered, the run on the first has been accepted.
        var first = await ImageWorkerTests.OfferedAsync(server.ImageWorkerRuns);
        using var run = await ConnectAsync(first);
        await ImageWorkerTests.OfferedAsync(server.ImageWorkerRuns, except: first);
        using (var response = await client.GetAsync("/v1/health"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal("{\"worker\":true,\"images\":true,\"backup\":false}", Encoding.UTF8.GetString(bytes));
            using var document = JsonDocument.Parse(bytes);
            Assert.Equal(["worker", "images", "backup"], document.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.All(document.RootElement.EnumerateObject(), property => Assert.True(property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False));
        }

        // Unhealthy is still 200: a server that answers at all is up.
        server.Time.Advance(TimeSpan.FromMinutes(15));
        using var later = await client.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        Assert.Equal("{\"worker\":false,\"images\":false,\"backup\":false}", await later.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheAnswer_IsTheSameBytes_WhateverTheRequestCarries()
    {
        using var server = new TestServer { UseImageWorkerRuns = true };
        using var client = server.CreateClient();
        var expected = await HealthAsync(client);
        foreach (var (name, value) in new[]
        {
            ("Host", "attacker.example"),
            ("X-Forwarded-For", "203.0.113.7"),
            ("X-Forwarded-Host", "attacker.example"),
            ("X-Forwarded-Proto", "http"),
            ("Cookie", "session=1234; admin=true"),
            ("Accept-Encoding", "gzip, deflate, br"),
            ("Accept", "text/html"),
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
            Assert.True(request.Headers.TryAddWithoutValidation(name, value), name);
            Assert.Equal(expected, await HealthAsync(client, request));
        }

        using var query = new HttpRequestMessage(HttpMethod.Get, "/v1/health?verbose=true&worker=false&counts=1");
        Assert.Equal(expected, await HealthAsync(client, query));
    }

    [Fact]
    public async Task ABody_IsRefusedBeforeItIsRead_AndOtherMethodsGet405()
    {
        using var server = new TestServer();
        using var client = server.CreateClient();
        foreach (HttpContent content in new HttpContent[] { new ByteArrayContent([1]), new ByteArrayContent(new byte[64 * 1024]), new UnsizedContent([1, 2, 3]) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/health") { Content = content };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        // An empty body is no body.
        using (var empty = new HttpRequestMessage(HttpMethod.Get, "/v1/health") { Content = new ByteArrayContent([]) })
        {
            using var response = await client.SendAsync(empty);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Every other method is ASP.NET Core's 405, with nothing of the answer.
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch, HttpMethod.Head, HttpMethod.Options })
        {
            using var request = new HttpRequestMessage(method, "/v1/health");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task TheAnswer_IsTheSameBytes_BeforeAndAfterBindingPublishingAndReporting()
    {
        using var server = new TestServer { UseImageWorkerRuns = true };
        using var client = server.CreateClient();
        var before = await HealthAsync(client);
        Assert.Equal("{\"worker\":false,\"images\":true,\"backup\":false}", Encoding.UTF8.GetString(before));

        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        await bram.BindAsync(Bram, "Bram Oakes", "Gilgamesh");
        using (var published = await aria.PublishAsync(Plates.Snapshot(profile, "Plate")))
        {
            Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
        }

        using (var lookup = await bram.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}"))
        {
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        }

        using (var report = await bram.SendAsync("/v1/report", RequestProofKind.Report, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"reason\":\"spam\"}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, report.StatusCode);
        }

        Assert.Equal(before, await HealthAsync(client));
    }

    [Fact]
    public async Task ManyCalls_ChangeNoRow_AndStartNoJobFetchOrCount()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        var png = Plates.Png(2, 2);
        using (var published = await aria.PublishAsync(Plates.Snapshot(profile, "Plate", png), png))
        {
            Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
        }

        var limiter = server.Services.GetRequiredService<RateLimiter>();
        var rows = await RowsAsync(server);
        var counters = limiter.Count;
        var fetched = server.Lodestone.Fetched.Count;
        var jobs = server.Images.Jobs;
        using var client = server.CreateClient();
        for (var call = 0; call < 100; call++)
        {
            await HealthAsync(client);
        }

        Assert.Equal(rows, await RowsAsync(server));
        Assert.Equal(counters, limiter.Count);
        Assert.Equal(fetched, server.Lodestone.Fetched.Count);
        Assert.Equal(jobs, server.Images.Jobs);
    }

    [Fact]
    public async Task ARunsConnection_IsRecorded_AndTheWorkerTurnsUnhealthyTwoMinutesAfterTheLast()
    {
        var folder = TempFolder();
        var runs = Path.Combine(folder, "runs");
        var time = new ManualTime(Start);
        var health = NewHealth(time, runs);
        using var client = ImageWorkerTests.NewRunsClient(runs, health);
        using var stop = new CancellationTokenSource();
        await client.StartAsync(stop.Token);
        try
        {
            var first = await ImageWorkerTests.OfferedAsync(runs);
            time.Advance(TimeSpan.FromMinutes(2));
            Assert.False(health.Current.Worker);

            // Once the next socket is offered, the run on the first has been accepted.
            using var run = await ConnectAsync(first);
            var second = await ImageWorkerTests.OfferedAsync(runs, except: first);
            Assert.Equal(time.Now, health.State.LastWorkerConnection);
            Assert.True(health.Current.Worker);
            time.Advance(TimeSpan.FromSeconds(119));
            Assert.True(health.Current.Worker);
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.False(health.Current.Worker);

            using var next = await ConnectAsync(second);
            await ImageWorkerTests.OfferedAsync(runs, except: second);
            Assert.True(health.Current.Worker);
        }
        finally
        {
            await stop.CancelAsync();
            await client.StopAsync(default);
            Delete(folder);
        }
    }

    [Fact]
    public async Task AConnectionToTheSharedSocket_IsRecordedToo()
    {
        var folder = TempFolder();
        var time = new ManualTime(Start);
        var health = new ServerHealth(Options.Create(new ServerOptions { ImageWorkerSocket = "images.sock" }), time);
        using var client = ImageWorkerTests.NewClient(folder, health);
        using var stop = new CancellationTokenSource();
        await client.StartAsync(stop.Token);
        try
        {
            time.Advance(TimeSpan.FromMinutes(2));
            Assert.False(health.Current.Worker);
            using var run = await ConnectAsync(Path.Combine(folder, "images.sock"));
            await UntilAsync(() => health.Current.Worker);
            Assert.Equal(time.Now, health.State.LastWorkerConnection);
        }
        finally
        {
            await stop.CancelAsync();
            await client.StopAsync(default);
            Delete(folder);
        }
    }

    [Fact]
    public async Task KnownBug13_RunsThatNeverConnect_ReadUnhealthyAtOnce()
    {
        // Known bug 13, in process: the server offers per-run sockets, and no run ever connects. The
        // worker has no grace after the start, so it reads unhealthy at once, and stays so.
        using var server = new TestServer { UseImageWorkerRuns = true };
        using var client = server.CreateClient();
        Assert.Equal("{\"worker\":false,\"images\":true,\"backup\":false}", Encoding.UTF8.GetString(await HealthAsync(client)));
        await ImageWorkerTests.OfferedAsync(server.ImageWorkerRuns);
        Assert.Equal("{\"worker\":false,\"images\":true,\"backup\":false}", Encoding.UTF8.GetString(await HealthAsync(client)));

        server.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("{\"worker\":false,\"images\":true,\"backup\":false}", Encoding.UTF8.GetString(await HealthAsync(client)));

        // The canary finds no worker in time, which counts neither way, so images turn unhealthy
        // when the start's grace ends with no canary through.
        server.Services.GetRequiredService<ImageWorkerClient>().WorkerPatience = TimeSpan.FromMilliseconds(200);
        Assert.Equal(CanaryOutcome.Busy, await server.Services.GetRequiredService<ImageCanary>().RunOnceAsync(default));
        server.Time.Advance(TimeSpan.FromMinutes(13));
        Assert.Equal("{\"worker\":false,\"images\":false,\"backup\":false}", Encoding.UTF8.GetString(await HealthAsync(client)));

        server.Services.GetRequiredService<HealthWatch>().CheckOnce();
        Assert.Contains("Server health: the image worker turned unhealthy.", server.Log.All, StringComparison.Ordinal);
        Assert.Contains("Server health: image processing turned unhealthy.", server.Log.All, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCanary_IsATinyPng_ThatPassesSection821_AndTheWorkersRoundTrip()
    {
        var png = ImageCanary.Png.ToArray();
        Assert.Equal(new SniffedImage(ImageFormat.Png, ImageCanary.Width, ImageCanary.Height), ImageSniffer.Sniff(png));
        Assert.True(ProcessedImages.IsWorkerPng(png));
        var declared = new ImageReference(AssetId.NewId(), SHA256.HashData(png), ImageFormat.Png, png.Length, ImageCanary.Width, ImageCanary.Height);
        ImageSniffer.CheckDeclared(png, declared);

        // The worker decodes it, its CRCs checked, and what it writes back passes the server's check.
        var output = ImageRecoder.Recode(JobFormat.Png, ImageCanary.Width, ImageCanary.Height, png);
        Assert.NotNull(output);
        Assert.True(ProcessedImages.Check(output, declared));
    }

    [Fact]
    public async Task TheCanary_GoesThroughARealWorkerRun_AndRecordsASuccess()
    {
        var folder = TempFolder();
        var runs = Path.Combine(folder, "runs");
        var time = new ManualTime(Start);
        var health = NewHealth(time, runs);
        using var client = ImageWorkerTests.NewRunsClient(runs, health);
        using var canary = new ImageCanary(client, health, time, NullLogger<ImageCanary>.Instance);
        using var stop = new CancellationTokenSource();
        await client.StartAsync(stop.Token);
        try
        {
            var offered = await ImageWorkerTests.OfferedAsync(runs);
            time.Advance(TimeSpan.FromHours(1));
            var run = Task.Run(() => WorkerRun.RunOnceAsync(offered, stop.Token));
            Assert.Equal(CanaryOutcome.Succeeded, await canary.RunOnceAsync(default));
            Assert.Equal(time.Now, health.State.LastCanarySuccess);
            Assert.True(health.Current.Images);
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await stop.CancelAsync();
            await client.StopAsync(default);
            Delete(folder);
        }
    }

    [Fact]
    public async Task RefusedOrHostileOutput_FailsTheCanary_AndTwoFailuresInARowTurnImagesUnhealthy()
    {
        var time = new ManualTime(Start);
        var health = NewHealth(time);
        var worker = new FakeImages();
        using var canary = new ImageCanary(worker, health, time, NullLogger<ImageCanary>.Instance);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CanaryOutcome.Succeeded, await canary.RunOnceAsync(default));
        Assert.True(health.Current.Images);

        foreach (var (kind, answer) in new (string, Func<byte[], byte[]?>)[]
        {
            ("refused", _ => null),
            ("another size", _ => Plates.Png(3, 2)),
            ("a JPEG", _ => Images.Jpeg(2, 2)),
            ("not an image", _ => [1, 2, 3]),
            ("a PNG with a text chunk", _ => Images.Png(2, 2, withMetadata: true)),
            ("trailing bytes", bytes => [.. bytes, 0]),
        })
        {
            worker.Answer = answer;
            Assert.True(await canary.RunOnceAsync(default) == CanaryOutcome.Failed, kind);
            Assert.True(health.Current.Images, kind);
            Assert.True(await canary.RunOnceAsync(default) == CanaryOutcome.Failed, kind);
            Assert.False(health.Current.Images, kind);
            worker.Answer = bytes => bytes;
            Assert.True(await canary.RunOnceAsync(default) == CanaryOutcome.Succeeded, kind);
            Assert.True(health.Current.Images, kind);
        }
    }

    [Fact]
    public async Task ABusyWorker_CountsNeitherWay()
    {
        var time = new ManualTime(Start);
        var health = NewHealth(time);
        var worker = new FakeImages();
        using var canary = new ImageCanary(worker, health, time, NullLogger<ImageCanary>.Instance);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CanaryOutcome.Succeeded, await canary.RunOnceAsync(default));
        worker.Answer = _ => null;
        Assert.Equal(CanaryOutcome.Failed, await canary.RunOnceAsync(default));

        var before = health.State;
        worker.Busy = true;
        Assert.Equal(CanaryOutcome.Busy, await canary.RunOnceAsync(default));
        Assert.Equal(CanaryOutcome.Busy, await canary.RunOnceAsync(default));
        Assert.Equal(before, health.State);
        Assert.True(health.Current.Images);

        // A failure after the busy spell is the second in a row, and busy never turns them healthy again.
        worker.Busy = false;
        Assert.Equal(CanaryOutcome.Failed, await canary.RunOnceAsync(default));
        Assert.False(health.Current.Images);
        worker.Busy = true;
        worker.Answer = bytes => bytes;
        Assert.Equal(CanaryOutcome.Busy, await canary.RunOnceAsync(default));
        Assert.False(health.Current.Images);
    }

    [Fact]
    public async Task TheCanary_TouchesNoCharactersLimitsNoRowAndNoLodestone()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        var png = Plates.Png(2, 2);
        using (var published = await aria.PublishAsync(Plates.Snapshot(profile, "Plate", png), png))
        {
            Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
        }

        var limiter = server.Services.GetRequiredService<RateLimiter>();
        var rows = await RowsAsync(server);
        var counters = limiter.Count;
        var fetched = server.Lodestone.Fetched.Count;
        var jobs = server.Images.Jobs;
        var canary = server.Services.GetRequiredService<ImageCanary>();

        // More failures and images than any character's hour allows (3 and 32), had they counted.
        server.Images.Answer = _ => null;
        for (var run = 0; run < 40; run++)
        {
            Assert.Equal(CanaryOutcome.Failed, await canary.RunOnceAsync(default));
        }

        server.Images.Answer = bytes => bytes;
        for (var run = 0; run < 40; run++)
        {
            Assert.Equal(CanaryOutcome.Succeeded, await canary.RunOnceAsync(default));
        }

        Assert.Equal(jobs + 80, server.Images.Jobs);
        Assert.Equal(rows, await RowsAsync(server));
        Assert.Equal(counters, limiter.Count);
        Assert.Equal(fetched, server.Lodestone.Fetched.Count);

        // Aria's next publish with an image goes through: none of it counted against her.
        using var again = await aria.PublishAsync(Plates.Snapshot(profile, "Plate again", png), png);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task AGoodBackupRun_IsHealthy_UntilItIs26HoursOld()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        var folder = TempFolder();
        try
        {
            var options = Options.Create(new ServerOptions { BackupFolder = folder, DatabasePath = server.DatabasePath });
            var health = new ServerHealth(options, server.Time);
            using var backups = new Backups(options, server.Services.GetRequiredService<ServerDatabase>(), health, server.Time, NullLogger<Backups>.Instance);
            server.Time.Advance(TimeSpan.FromHours(1));
            Assert.False(health.Current.Backup);

            await backups.RunOnceAsync(default);
            Assert.Single(Directory.GetFiles(folder));
            Assert.True(health.Current.Backup);
            server.Time.Advance(TimeSpan.FromHours(26) - TimeSpan.FromTicks(1));
            Assert.True(health.Current.Backup);
            server.Time.Advance(TimeSpan.FromTicks(1));
            Assert.False(health.Current.Backup);
        }
        finally
        {
            Delete(folder);
        }
    }

    [Fact]
    public async Task AFailedCopy_IsUnhealthy_AndTheSweepStillDeletesCopiesPastTheRetention()
    {
        var folder = TempFolder();
        try
        {
            var time = new ManualTime(Start);
            var (backups, health) = NewBackups(folder, folder, time);
            using var disposing = backups;

            // Copies from 8 and 30 days before, another day's partial copy, and yesterday's, which stays.
            var old = new[] { "server-20260922.db", "server-20260831.db", "server-20260925.db.partial" }.Select(name => Path.Combine(folder, name)).ToArray();
            var yesterday = Path.Combine(folder, "server-20260929.db");
            foreach (var path in old.Append(yesterday))
            {
                await File.WriteAllBytesAsync(path, [1]);
            }

            // Today's copy can't be written: a folder holds its partial file's name.
            Directory.CreateDirectory(Path.Combine(folder, "server-20260930.db.partial"));
            await Assert.ThrowsAnyAsync<Exception>(() => backups.RunOnceAsync(default));
            Assert.False(health.Current.Backup);
            Assert.All(old, path => Assert.False(File.Exists(path), path));
            Assert.True(File.Exists(yesterday));
            Assert.False(File.Exists(Path.Combine(folder, "server-20260930.db")));
        }
        finally
        {
            Delete(folder);
        }
    }

    [Fact]
    public async Task ABackupFolderThatIsAFile_OrNone_IsUnhealthy()
    {
        var folder = TempFolder();
        try
        {
            var file = Path.Combine(folder, "not-a-folder");
            await File.WriteAllBytesAsync(file, [1]);
            var time = new ManualTime(Start);
            var (backups, health) = NewBackups(file, folder, time);
            using var disposing = backups;
            Assert.True(health.Current.Backup);
            await Assert.ThrowsAnyAsync<Exception>(() => backups.RunOnceAsync(default));
            Assert.False(health.Current.Backup);
        }
        finally
        {
            Delete(folder);
        }

        Assert.False(new ServerHealth(Options.Create(new ServerOptions()), new ManualTime(Start)).Current.Backup);
    }

    [Fact]
    public async Task AFailedBackup_IsTriedAgainSoon_AndAGoodOneWaitsTheDay()
    {
        var folder = TempFolder();
        try
        {
            var time = new ManualTime(Start);
            var (defaults, _) = NewBackups(folder, folder, time);
            using (defaults)
            {
                Assert.Equal(TimeSpan.FromHours(24), defaults.Interval);
                Assert.Equal(TimeSpan.FromHours(1), defaults.RetryAfterFailure);
            }

            // A good run (today's copy is there already, so only the sweep runs) waits the whole interval.
            var good = Path.Combine(folder, "good");
            Directory.CreateDirectory(good);
            await File.WriteAllBytesAsync(Path.Combine(good, "server-20260930.db"), [1]);
            var (backups, health) = NewBackups(good, folder, time);
            using (backups)
            {
                backups.RetryAfterFailure = TimeSpan.FromMilliseconds(20);
                await backups.StartAsync(default);
                await UntilAsync(() => health.State.LastBackupSuccess is not null);
                var old = Path.Combine(good, "server-20260901.db");
                await File.WriteAllBytesAsync(old, [1]);
                await Task.Delay(500);
                Assert.True(File.Exists(old), "a good run was followed by another before its interval");
                await backups.StopAsync(default);
            }

            // A failing one is tried again after its retry pause, again and again.
            var file = Path.Combine(folder, "not-a-folder");
            await File.WriteAllBytesAsync(file, [1]);
            var log = new CapturedLog();
            using var factory = LoggerFactory.Create(logging => logging.AddProvider(log));
            var (failing, _) = NewBackups(file, folder, time, factory.CreateLogger<Backups>());
            using (failing)
            {
                failing.RetryAfterFailure = TimeSpan.FromMilliseconds(20);
                await failing.StartAsync(default);
                await UntilAsync(() => log.Lines.Count(line => line.Contains("A backup failed with", StringComparison.Ordinal)) >= 3);
                await failing.StopAsync(default);
            }
        }
        finally
        {
            Delete(folder);
        }
    }

    [Fact]
    public async Task TheLog_HoldsTheRouteAndEachChangeOnce_AndNoIdentifierCodeNameOrWorld()
    {
        using var server = new TestServer { UseImageWorkerRuns = true };
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        var bound = await aria.BindAsync(Aria);
        var profile = ProfileId.Parse(bound.GetProperty("profileId").GetString()!);
        await bram.BindAsync(Bram, "Bram Oakes", "Gilgamesh");
        var code = await aria.CodeAsync();
        using (await aria.PublishAsync(Plates.Snapshot(profile, "Secret Plate Name")))
        {
        }

        using (await bram.SendAsync("/v1/report", RequestProofKind.Report, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"reason\":\"spam\"}"))
        {
        }

        using var client = server.CreateClient();
        var watch = server.Services.GetRequiredService<HealthWatch>();
        var first = await ImageWorkerTests.OfferedAsync(server.ImageWorkerRuns);
        for (var call = 0; call < 5; call++)
        {
            await HealthAsync(client);
        }

        // The worker turns unhealthy, and back once a run connects: each change is one line,
        // however many requests and looks there are.
        server.Time.Advance(TimeSpan.FromMinutes(2));
        for (var call = 0; call < 5; call++)
        {
            await HealthAsync(client);
            watch.CheckOnce();
        }

        using (await ConnectAsync(first))
        {
            await ImageWorkerTests.OfferedAsync(server.ImageWorkerRuns, except: first);
            for (var call = 0; call < 5; call++)
            {
                await HealthAsync(client);
                watch.CheckOnce();
            }
        }

        var log = server.Log.All;
        Assert.Contains("/v1/health", log, StringComparison.Ordinal);
        foreach (var change in new[] { "the image worker turned unhealthy.", "the image worker turned healthy again.", "the backup turned unhealthy." })
        {
            Assert.Single(server.Log.Lines, line => line.Contains("Warning: Server health: " + change, StringComparison.Ordinal));
        }

        foreach (var secret in new[]
        {
            aria.Key.PublicKey.Id.ToString(),
            aria.Key.PublicKey.Id.ToString()[4..],
            bram.Key.PublicKey.Id.ToString(),
            Aria.ToString(CultureInfo.InvariantCulture),
            Bram.ToString(CultureInfo.InvariantCulture),
            code,
            code[3..],
            profile.ToString(),
            "Aria",
            "Starfall",
            "Bram",
            "Gilgamesh",
            "Secret Plate Name",
            "spam",
            "na.finalfantasyxiv.com",
            Path.GetFileNameWithoutExtension(first)["run-".Length..],
        })
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
    }

    /// <summary>A health with a worker (per-run sockets in <paramref name="runs"/>) and a backup folder configured.</summary>
    private static ServerHealth NewHealth(ManualTime time, string runs = "runs") =>
        new(Options.Create(new ServerOptions { ImageWorkerRuns = runs, BackupFolder = "backups" }), time);

    /// <summary>A backup into <paramref name="backupFolder"/>, of a database in <paramref name="folder"/> that only a copy opens, with its own health.</summary>
    private static (Backups Backups, ServerHealth Health) NewBackups(string backupFolder, string folder, ManualTime time, ILogger<Backups>? logger = null)
    {
        var options = Options.Create(new ServerOptions { BackupFolder = backupFolder, DatabasePath = Path.Combine(folder, "server.db") });
        var health = new ServerHealth(options, time);
        return (new Backups(options, new ServerDatabase(options, NullLogger<ServerDatabase>.Instance), health, time, logger ?? NullLogger<Backups>.Instance), health);
    }

    /// <summary>Asks for the health and returns the answer's bytes, which must be a 200.</summary>
    private static async Task<byte[]> HealthAsync(HttpClient client, HttpRequestMessage? request = null)
    {
        using var owned = request is null ? new HttpRequestMessage(HttpMethod.Get, "/v1/health") : null;
        using var response = await client.SendAsync(request ?? owned!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Every row of every table, and the schema itself, as text.</summary>
    private static async Task<List<string>> RowsAsync(TestServer server)
    {
        var rows = new List<string>();
        await using var connection = new SqliteConnection("Data Source=" + server.DatabasePath + ";Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add("sqlite_master|" + Row(reader));
                if (reader.GetString(0) == "table")
                {
                    tables.Add(reader.GetString(1));
                }
            }
        }

        Assert.Contains("bindings", tables);
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table + "\";";
            await using var reader = await command.ExecuteReaderAsync();
            var tableRows = new List<string>();
            while (await reader.ReadAsync())
            {
                tableRows.Add(table + "|" + Row(reader));
            }

            tableRows.Sort(StringComparer.Ordinal);
            rows.AddRange(tableRows);
        }

        return rows;
    }

    private static string Row(SqliteDataReader reader) => string.Join(
        "|",
        Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index)
            ? "null"
            : reader.GetValue(index) is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)));

    /// <summary>A log line's message, from "Server health" on.</summary>
    private static string Message(string line) => line[line.IndexOf("Server health", StringComparison.Ordinal)..].TrimEnd();

    /// <summary>Connects to a worker socket as a run would, trying until it answers.</summary>
    private static async Task<Socket> ConnectAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
                return socket;
            }
            catch (SocketException) when (attempt < 100)
            {
                socket.Dispose();
                await Task.Delay(50);
            }
        }
    }

    /// <summary>Waits, up to 10 seconds, until <paramref name="condition"/> holds.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition never held");
            await Task.Delay(20);
        }
    }

    /// <summary>A folder of its own, with a short path, since a Unix socket's path is limited to about 108 bytes.</summary>
    private static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "afh-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A body that doesn't declare its length.</summary>
    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
