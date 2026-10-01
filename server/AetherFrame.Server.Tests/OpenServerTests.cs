using System;
using System.IO;
using System.Threading.Tasks;
using AetherFrame.Server.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>
/// The open alpha (the owner's direction of October 1, 2026): <see cref="ServerOptions.OpenToEveryone"/>
/// lets any character with a passing Lodestone check share and view, and only with decision I2's
/// per-job isolation of the image worker (or no worker at all, which refuses every image).
/// </summary>
public class OpenServerTests
{
    [Fact]
    public void OpeningTheServer_NeedsPerRunSockets_ForItsImageWorker()
    {
        var shared = Options(open: true, socket: "/run/aetherframe/images.sock");
        Assert.False(shared.IsOpen);
        var refused = Assert.Throws<InvalidOperationException>(shared.Validate);
        Assert.Contains("ImageWorkerRuns", refused.Message, StringComparison.Ordinal);

        var isolated = Options(open: true, socket: "/run/aetherframe/images.sock", runs: "/run/aetherframe/runs");
        Assert.True(isolated.IsOpen);
        isolated.Validate();

        // No worker refuses every image, so nothing is decoded at all.
        Assert.True(Options(open: true).IsOpen);
        Assert.False(Options(open: false, runs: "/run/aetherframe/runs").IsOpen);
    }

    [Fact]
    public void AnOpenServer_AllowsEveryId_AndAClosedOneOnlyItsList()
    {
        var closed = new Allowlist(new Monitor(Options(open: false, runs: "/r", ids: ["37220142"])), NullLogger<Allowlist>.Instance);
        Assert.True(closed.Allows(37220142));
        Assert.False(closed.Allows(12345678));

        var open = new Allowlist(new Monitor(Options(open: true, runs: "/r", ids: ["37220142"])), NullLogger<Allowlist>.Instance);
        Assert.True(open.Allows(37220142));
        Assert.True(open.Allows(12345678));

        // Asked to open without the isolation, the server stays closed.
        var notIsolated = new Allowlist(new Monitor(Options(open: true, socket: "/s", ids: ["37220142"])), NullLogger<Allowlist>.Instance);
        Assert.False(notIsolated.Allows(12345678));
    }

    [Fact]
    public async Task TheAllowlistCommand_SaysWhenTheServerIsOpen()
    {
        using var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["allowlist"], null!, null!, output, ["37220142"], open: true));
        Assert.StartsWith("Open to everyone", output.ToString(), StringComparison.Ordinal);

        using var closed = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["allowlist"], null!, null!, closed, ["37220142"]));
        Assert.DoesNotContain("Open to everyone", closed.ToString(), StringComparison.Ordinal);
    }

    private static ServerOptions Options(bool open, string socket = "", string runs = "", string[]? ids = null) => new()
    {
        DeploymentName = "plates.aetherframe.net",
        DatabasePath = "x.db",
        OpenToEveryone = open,
        ImageWorkerSocket = socket,
        ImageWorkerRuns = runs,
        AllowedLodestoneIds = [.. ids ?? []],
    };

    private sealed class Monitor(ServerOptions value) : IOptionsMonitor<ServerOptions>
    {
        public ServerOptions CurrentValue => value;

        public ServerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ServerOptions, string?> listener) => null;
    }
}
