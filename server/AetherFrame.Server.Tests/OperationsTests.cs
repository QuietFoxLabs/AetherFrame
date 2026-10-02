using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using AetherFrame.ImageWorker;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Hosting;
using AetherFrame.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>What the deployment (N2-8) relies on: the daily backup, the operator's commands, and the worker's refusal to run with a network.</summary>
public class OperationsTests
{
    [Fact]
    public async Task TheBackup_IsWrittenOnceADay_AndDeletedAfterSevenDays()
    {
        using var server = new TestServer();
        using var player = server.NewPlayer();
        await player.BindAsync(12345678);
        var folder = Path.Combine(Path.GetTempPath(), "afb-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        try
        {
            var options = Options.Create(new ServerOptions { BackupFolder = folder, DatabasePath = server.DatabasePath });
            var backups = new Backups(options, server.Services.GetRequiredService<ServerDatabase>(), new ServerHealth(options, server.Time), server.Time, NullLogger<Backups>.Instance);

            await backups.RunOnceAsync(default);
            var first = Assert.Single(Directory.GetFiles(folder));
            Assert.EndsWith("server-20260930.db", first, StringComparison.Ordinal);
            await backups.RunOnceAsync(default);
            Assert.Single(Directory.GetFiles(folder));

            // The copy holds the binding, and opens as a database of its own.
            await using (var copy = new SqliteConnection("Data Source=" + first + ";Mode=ReadOnly;Pooling=False"))
            {
                await copy.OpenAsync();
                await using var command = copy.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM bindings;";
                Assert.Equal(1L, await command.ExecuteScalarAsync());
            }

            // Six days later there are seven copies; on the seventh, the first is gone.
            for (var day = 1; day <= 6; day++)
            {
                server.Time.Advance(TimeSpan.FromDays(1));
                await backups.RunOnceAsync(default);
            }

            Assert.Equal(7, Directory.GetFiles(folder).Length);
            server.Time.Advance(TimeSpan.FromDays(1));
            await backups.RunOnceAsync(default);
            Assert.Equal(7, Directory.GetFiles(folder).Length);
            Assert.DoesNotContain(first, Directory.GetFiles(folder));

            // A stray partial copy from a failed day is deleted with the old copies.
            var stray = Path.Combine(folder, "server-20261001.db.partial");
            await File.WriteAllBytesAsync(stray, [1]);
            server.Time.Advance(TimeSpan.FromDays(1));
            await backups.RunOnceAsync(default);
            Assert.False(File.Exists(stray));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ACopy_IsGoneWithinSevenDaysOfItsWriting_WhenARestartMovesTheRuns()
    {
        // The review's case. One server writes October 1's copy at 01:00 and stops; the next starts
        // at 22:00 on October 3 and runs every hour from then. A daily run at the restart's time of
        // day kept that copy until 22:00 on October 8, past the 7 days the consent text promises.
        using var server = new TestServer();
        using var player = server.NewPlayer();
        await player.BindAsync(12345678);
        var folder = Path.Combine(Path.GetTempPath(), "afb-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        try
        {
            var options = Options.Create(new ServerOptions { BackupFolder = folder, DatabasePath = server.DatabasePath });
            var database = server.Services.GetRequiredService<ServerDatabase>();
            var written = new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);
            var copy = Path.Combine(folder, "server-20261001.db");
            server.Time.Now = written;
            using (var first = new Backups(options, database, new ServerHealth(options, server.Time), server.Time, NullLogger<Backups>.Instance))
            {
                await first.RunOnceAsync(default);
            }

            Assert.True(File.Exists(copy));
            using var second = new Backups(options, database, new ServerHealth(options, server.Time), server.Time, NullLogger<Backups>.Instance);
            var dayEight = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
            DateTimeOffset? lastKept = null;
            server.Time.Now = new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.Zero);
            while (server.Time.Now <= written + Backups.Retention)
            {
                await second.RunOnceAsync(default);
                var when = server.Time.Now.ToString("u", CultureInfo.InvariantCulture);
                if (server.Time.Now >= dayEight)
                {
                    Assert.False(File.Exists(copy), "October 1's copy is still there after the run at " + when);
                }

                if (File.Exists(copy))
                {
                    // It stays until the next run at most, an interval later: that must still be within its 7 days.
                    Assert.True(server.Time.Now + second.Interval <= written + Backups.Retention, "October 1's copy is kept past its 7 days by the run at " + when);
                    lastKept = server.Time.Now;
                }

                server.Time.Advance(second.Interval);
            }

            // It went at 23:00 on October 7, the first run 6 days and 23 hours after its day began.
            Assert.Equal(new DateTimeOffset(2026, 10, 7, 22, 0, 0, TimeSpan.Zero), lastKept);
            Assert.False(File.Exists(copy));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task TheOperatorsCommands_ListRemoveAndResolve()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(12345678)).GetProperty("profileId").GetString()!);
        await bram.BindAsync(23456789, "Bram Oakes", "Gilgamesh");
        using (await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate"), aria.Key)))
        {
        }

        using (await bram.SendAsync("/v1/report", RequestProofKind.Report, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"reason\":\"spam\"}"))
        {
        }

        var database = server.Services.GetRequiredService<ServerDatabase>();
        var bindings = server.Services.GetRequiredService<BindingStore>();
        async Task<(int Code, string Output)> RunAsync(params string[] args)
        {
            using var output = new StringWriter();
            var code = await AdminCommands.RunAsync(args, database, bindings, output);
            return (code, output.ToString());
        }

        var characters = await RunAsync("characters");
        Assert.Equal(0, characters.Code);
        Assert.Contains("12345678  Aria Starfall  Gilgamesh  shown", characters.Output, StringComparison.Ordinal);
        Assert.Contains("2 rows.", characters.Output, StringComparison.Ordinal);

        var reports = await RunAsync("reports");
        Assert.Contains("12345678  spam", reports.Output, StringComparison.Ordinal);
        var number = reports.Output.Split("  ")[0];
        Assert.Equal(0, (await RunAsync("resolve-report", number)).Code);
        Assert.Equal(1, (await RunAsync("resolve-report", number)).Code);

        Assert.Equal(0, (await RunAsync("remove-character", "12345678")).Code);
        foreach (var table in new[] { "latest", "images", "revisions" })
        {
            Assert.Equal(0L, await server.CountAsync($"SELECT COUNT(*) FROM {table};"));
        }

        Assert.Equal(1, (await RunAsync("remove-character", "12345678")).Code);
        Assert.Equal(2, (await RunAsync("remove-character", "0123")).Code);
        Assert.Equal(2, (await RunAsync()).Code);

        using var allowlistOutput = new StringWriter();
        Assert.Equal(1, await AdminCommands.RunAsync(["allowlist"], database, bindings, allowlistOutput, ["12345678", "0123"]));
        Assert.Contains("allows no one until the file is fixed", allowlistOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("12345678", allowlistOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("0123  (not a Lodestone id: ignored)", allowlistOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorker_RunsOnlyWithNoNetworkButLoopback()
    {
        Assert.True(WorkerRun.HasNoNetwork([NetworkInterfaceType.Loopback]));
        Assert.True(WorkerRun.HasNoNetwork([]));
        Assert.False(WorkerRun.HasNoNetwork([NetworkInterfaceType.Loopback, NetworkInterfaceType.Ethernet]));
        Assert.False(WorkerRun.HasNoNetwork([NetworkInterfaceType.Tunnel]));
    }
}
