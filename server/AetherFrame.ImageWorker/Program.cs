using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace AetherFrame.ImageWorker;

/// <summary>
/// The image worker's entry point (decision I2): one job per run, in a container with no network, no
/// database, no key and no configuration. It connects to the socket the server owns, and ends after
/// one answer, or when the watchdog fires.
/// </summary>
internal static class WorkerProgram
{
    public static Task<int> Main()
    {
        // The container gives the worker no network (N2-8); if it has one, it does nothing.
        if (OperatingSystem.IsLinux() && !WorkerRun.HasNoNetwork(NetworkInterface.GetAllNetworkInterfaces().Select(n => n.NetworkInterfaceType)))
        {
            Console.Error.WriteLine("The image worker has a network interface besides loopback, and refuses to run (decision I2).");
            return Task.FromResult(3);
        }

        var socketPath = Environment.GetEnvironmentVariable("AETHERFRAME_IMAGE_SOCKET") ?? "/run/aetherframe/images.sock";
        return WorkerRun.RunOnceAsync(socketPath, CancellationToken.None, () => WorkerRun.StartWatchdog(WorkerRun.JobDeadline));
    }
}
