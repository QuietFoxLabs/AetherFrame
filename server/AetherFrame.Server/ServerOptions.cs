using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Protocol.Requests;

namespace AetherFrame.Server;

/// <summary>
/// The server's configuration, from the <c>AetherFrame</c> section (environment variables such as
/// <c>AetherFrame__DeploymentName</c> in the container, N2-8). Checked once at startup: a server
/// with a bad value doesn't start.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "AetherFrame";

    /// <summary>
    /// The deployment's own name (section 14.1 of the specification): the DNS hostname players'
    /// plugins are configured with. It comes from configuration only, never from a request.
    /// </summary>
    public string DeploymentName { get; set; } = "";

    /// <summary>
    /// Whether a name reserved for tests may be used. Only the tests set it: a server that accepts
    /// real keys refuses to start with such a name (section 13, rule 10).
    /// </summary>
    public bool AllowTestDeploymentName { get; set; }

    /// <summary>The SQLite database file.</summary>
    public string DatabasePath { get; set; } = "";

    /// <summary>
    /// The Lodestone ids allowed to bind, publish and view in stage 1 (decision C8), as decimal
    /// strings. Removing one takes effect at once. An empty list allows no one.
    /// </summary>
    public List<string> AllowedLodestoneIds { get; set; } = [];

    /// <summary>
    /// The open alpha (the owner's direction of October 1, 2026): any character with a passing
    /// Lodestone check may bind, publish and view, and <see cref="AllowedLodestoneIds"/> no longer
    /// limits who. It takes effect only with I2's per-job isolation (<see cref="ImageWorkerRuns"/>),
    /// or with no image worker at all, which refuses every image (<see cref="IsOpen"/>).
    /// </summary>
    public bool OpenToEveryone { get; set; }

    /// <summary>Whether the server is open to everyone now: <see cref="OpenToEveryone"/>, with I2's condition met.</summary>
    internal bool IsOpen => OpenToEveryone && (ImageWorkerRuns.Length > 0 || ImageWorkerSocket.Length == 0);

    /// <summary>
    /// The addresses of the reverse proxy (Caddy, N2-8) whose forwarded headers are trusted. Only
    /// loopback when empty (decision R4).
    /// </summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>
    /// The Worlds a character can be on, when the operator must add one before the next release;
    /// empty means the built-in list (<see cref="Lodestone.Worlds"/>).
    /// </summary>
    public List<string> Worlds { get; set; } = [];

    /// <summary>
    /// The Unix socket the server listens on for image worker runs (decision I2), in a folder the
    /// worker's container mounts read-only. Empty: no worker, and every image is refused.
    /// </summary>
    public string ImageWorkerSocket { get; set; } = "";

    /// <summary>
    /// A folder for one socket per worker run (decision I2's per-job isolation), in the volume the
    /// worker host can see: the server offers exactly one fresh socket in it at a time, answers one
    /// connection on it, then closes and deletes it, and the host mounts only that socket into the
    /// next run. When set, it takes the place of <see cref="ImageWorkerSocket"/>.
    /// </summary>
    public string ImageWorkerRuns { get; set; } = "";

    /// <summary>The folder the daily backup is written to (N2-8), or empty for none.</summary>
    public string BackupFolder { get; set; } = "";

    /// <summary>
    /// The oldest plugin version the server answers, told to plugins by <c>/v1/status</c>. It is 0.1.9
    /// since that release: an earlier build draws its frames, cut to fit, as grey boxes (ROADMAP.md,
    /// section 5, frames that fit), so it is asked to update before it shares.
    /// </summary>
    public string MinimumPlugin { get; set; } = "0.1.9";

    /// <summary>
    /// The Lodestone relay (docs/networking/Runbook.md), as <c>address:port</c>, or empty to reach
    /// the Lodestone directly. When set, every Lodestone request, and nothing else, goes through it as
    /// an HTTPS tunnel, so TLS still runs from the server to the Lodestone and its certificate is
    /// checked as before. It is read at start: changing it needs a restart.
    /// </summary>
    public string LodestoneRelay { get; set; } = "";

    /// <summary>Whether the daily re-read runs (decision C1). Only the tests turn it off, to drive it themselves.</summary>
    public bool RereadsEnabled { get; set; } = true;

    /// <summary>
    /// The least time a failed check takes to answer, so its timing doesn't tell an id on the
    /// allowlist from one off it (ServerApi-v1.md, section 7). Not configurable; the tests shorten it.
    /// </summary>
    internal TimeSpan CheckFailureFloor { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The least time a failed check answers after its read through the player's own connection
    /// ended, besides <see cref="CheckFailureFloor"/> from its start: longer than parsing a page and
    /// the steps after the allowlist take, so an allowlist refusal and a later failure leave at the
    /// same time (ServerApi-v1.md, section 2.3). Not configurable; the tests shorten it.
    /// </summary>
    internal TimeSpan CheckFailureAfterRead { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The checked deployment name.</summary>
    internal DeploymentName Deployment { get; private set; } = null!;

    /// <summary>The checked Lodestone relay, or null to reach the Lodestone directly.</summary>
    internal System.Net.IPEndPoint? Relay { get; private set; }

    /// <summary>
    /// Reads <see cref="LodestoneRelay"/>: empty is none; otherwise exactly an address and a port,
    /// written as .NET writes them back (no name, no scheme, no path), and not a wildcard, broadcast
    /// or multicast address, and no IPv6 scope, which a proxy address can't carry.
    /// </summary>
    internal static bool TryParseRelay(string text, out System.Net.IPEndPoint? relay)
    {
        relay = null;
        if (text.Length == 0)
        {
            return true;
        }

        if (!System.Net.IPEndPoint.TryParse(text, out var endPoint) || endPoint.Port == 0 || endPoint.ToString() != text)
        {
            return false;
        }

        var address = endPoint.Address;
        if ((address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0) || address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any) || address.Equals(System.Net.IPAddress.Broadcast)
            || address.Equals(System.Net.IPAddress.None) || address.Equals(System.Net.IPAddress.IPv6None) || address.IsIPv6Multicast
            || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224 and <= 239))
        {
            return false;
        }

        relay = endPoint;
        return true;
    }

    /// <summary>
    /// Refuses ASP.NET Core's own forwarded-headers switch, from any configuration source (the
    /// environment with or without the <c>ASPNETCORE_</c> prefix, a settings file or the command
    /// line): it clears the trusted proxy lists, so any client could then set the address every
    /// limit counts (decision R4).
    /// </summary>
    /// <exception cref="InvalidOperationException">The switch is on.</exception>
    public static void RefuseForwardedHeadersSwitch(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        if (string.Equals(configuration["ForwardedHeaders_Enabled"], "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ForwardedHeaders_Enabled must not be set: the server trusts forwarded headers from AetherFrame:KnownProxies alone.");
        }
    }

    /// <summary>Checks every value, and refuses to start on the first bad one.</summary>
    /// <exception cref="InvalidOperationException">A value that would make the server unsafe or unusable.</exception>
    internal void Validate()
    {
        if (!Protocol.Requests.DeploymentName.TryParse(DeploymentName, out var deployment) || deployment is null)
        {
            throw new InvalidOperationException("AetherFrame:DeploymentName is not a deployment name (the specification's section 14.1).");
        }

        if (deployment.IsReservedForTesting && !AllowTestDeploymentName)
        {
            throw new InvalidOperationException("AetherFrame:DeploymentName is reserved for tests; a server that accepts real keys refuses it (section 13, rule 10).");
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException("AetherFrame:DatabasePath is not set.");
        }

        if (AllowedLodestoneIds.Any(text => !Lodestone.LodestoneIds.TryParse(text, out _)))
        {
            throw new InvalidOperationException("AetherFrame:AllowedLodestoneIds holds a value that is not a Lodestone id.");
        }

        if (OpenToEveryone && !IsOpen)
        {
            throw new InvalidOperationException("AetherFrame:OpenToEveryone needs AetherFrame:ImageWorkerRuns: decision I2 opens a server only with per-job isolation of its image worker.");
        }

        if (KnownProxies.Any(text => !System.Net.IPAddress.TryParse(text, out _)))
        {
            throw new InvalidOperationException("AetherFrame:KnownProxies holds a value that is not an address.");
        }


        if (!Version.TryParse(MinimumPlugin, out _))
        {
            throw new InvalidOperationException("AetherFrame:MinimumPlugin is not a version.");
        }

        if (Worlds.Any(world => !Lodestone.Worlds.IsWellFormed(world)))
        {
            throw new InvalidOperationException("AetherFrame:Worlds holds a value that is not a World's name.");
        }

        if (!TryParseRelay(LodestoneRelay, out var relay))
        {
            throw new InvalidOperationException("AetherFrame:LodestoneRelay is not an address and port, such as 100.101.102.103:8443.");
        }

        Deployment = deployment;
        Relay = relay;
    }
}
