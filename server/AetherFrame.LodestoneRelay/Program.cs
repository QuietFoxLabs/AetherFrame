using System;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AetherFrame.LodestoneRelay;

/// <summary>
/// The Lodestone relay's command line (docs/networking/Runbook.md, "The Lodestone relay"):
/// <c>AetherFrame.LodestoneRelay --listen &lt;this PC's Tailscale address&gt;:&lt;port&gt; --client &lt;the server's Tailscale address&gt;</c>.
/// It runs until Ctrl+C or the window closes.
/// </summary>
internal static class RelayProgram
{
    private const string Usage = "Usage: AetherFrame.LodestoneRelay --listen <address>:<port> --client <address>\n"
        + "  --listen  this PC's Tailscale address and a port, for example 100.101.102.103:8443\n"
        + "  --client  the server's Tailscale address, the only one served, for example 100.64.10.20";

    public static async Task<int> Main(string[] args)
    {
        IPEndPoint? listen = null;
        IPAddress? client = null;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--listen" when IPEndPoint.TryParse(args[i + 1], out var endPoint) && endPoint.Port != 0 && endPoint.ToString() == args[i + 1]:
                    listen = endPoint;
                    break;
                case "--client" when IPAddress.TryParse(args[i + 1], out var address) && address.ToString() == args[i + 1]:
                    client = address;
                    break;
                default:
                    Console.Error.WriteLine("Not understood: " + args[i] + " " + args[i + 1]);
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        if (args.Length % 2 != 0 || listen is null || client is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (listen.Address.Equals(IPAddress.Any) || listen.Address.Equals(IPAddress.IPv6Any))
        {
            Console.Error.WriteLine("--listen must be one address, this PC's Tailscale address, never 0.0.0.0 or [::]: the relay is for the server alone.");
            return 2;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        // The console is written from a queue of its own: a click in the window (Windows' QuickEdit)
        // pauses writing, and must never pause the relay. Lines beyond the queue's room are dropped.
        var lines = Channel.CreateBounded<string>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var writer = Task.Run(async () =>
        {
            await foreach (var line in lines.Reader.ReadAllAsync())
            {
                Console.WriteLine(line);
            }
        });

        var relay = new Relay(new RelayOptions { Listen = listen, Client = client }, line => lines.Writer.TryWrite(line));
        try
        {
            await relay.RunAsync(
                () => lines.Writer.TryWrite("The Lodestone relay is listening on " + relay.LocalEndPoint + " for " + client + " only, to " + Relay.Host + ":" + Relay.Port + " only. Ctrl+C stops it."),
                stop.Token);
        }
        catch (System.Net.Sockets.SocketException e)
        {
            lines.Writer.TryComplete();
            await writer;
            Console.Error.WriteLine("The relay can't listen on " + listen + ": " + e.Message);
            Console.Error.WriteLine("Is Tailscale connected, and is that this PC's Tailscale address?");
            return 1;
        }

        lines.Writer.TryComplete();
        await writer;
        return 0;
    }
}
