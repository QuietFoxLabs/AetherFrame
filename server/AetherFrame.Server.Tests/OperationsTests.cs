using System;
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
            var backups = new Backups(options, server.Services.GetRequiredService<ServerDatabase>(), server.Time, NullLogger<Backups>.Instance);

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
