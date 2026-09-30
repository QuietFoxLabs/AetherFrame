using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.ImageJobs;

namespace AetherFrame.ImageWorker;

/// <summary>
/// One run of the image worker (decision I2): connect to the server's socket, take one job,
/// re-encode it, answer, and end. The container starts it again for the next job, so no process of
/// one job survives into another. A watchdog ends the process if a job takes too long, whatever the
/// decoder is doing.
/// </summary>
public static class WorkerRun
{
    /// <summary>The longest a job may take before the process ends itself.</summary>
    public static readonly TimeSpan JobDeadline = TimeSpan.FromSeconds(20);

    /// <summary>How long a run waits for the server's socket to accept it.</summary>
    public static readonly TimeSpan ConnectPatience = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a connected run waits for a job before it ends, so the run the server hands a job to
    /// is always a recent one (the server skips connections older than 12 seconds).
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Runs once. The exit code is 0 for an answered job, and 1 when there was none.
    /// <paramref name="jobReceived"/> runs when a job has arrived: the worker's entry point starts the
    /// watchdog there.
    /// </summary>
    public static async Task<int> RunOnceAsync(string socketPath, CancellationToken cancellation, Action? jobReceived = null, TimeSpan? idleTimeout = null)
    {
        using var socket = await ConnectAsync(socketPath, cancellation);
        if (socket is null)
        {
            return 1;
        }

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        ImageJob? job;
        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            idle.CancelAfter(idleTimeout ?? IdleTimeout);
            try
            {
                job = await ImageJobWire.ReadJobAsync(stream, idle.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return 1;
            }
        }

        if (job is null)
        {
            return 1;
        }

        jobReceived?.Invoke();

        var output = ImageRecoder.Recode((JobFormat)job.Format, job.Width, job.Height, job.Bytes);
        await ImageJobWire.WriteReplyAsync(stream, output, cancellation);
        socket.Shutdown(SocketShutdown.Both);
        return 0;
    }

    /// <summary>Whether every network interface is loopback: the worker's container has no network.</summary>
    public static bool HasNoNetwork(IEnumerable<System.Net.NetworkInformation.NetworkInterfaceType> interfaces) =>
        interfaces.All(type => type == System.Net.NetworkInformation.NetworkInterfaceType.Loopback);

    /// <summary>
    /// Ends the process at once, with no cleanup, once <paramref name="deadline"/> has passed. It
    /// stops a decoder that is stuck, not one an exploit controls, which runs in this same process:
    /// that is what the deployment's own limit on a worker run's life is for (N2-8).
    /// </summary>
    public static void StartWatchdog(TimeSpan deadline)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(deadline);
            Environment.FailFast("The image job ran past its deadline.");
        })
        {
            IsBackground = true,
            Name = "watchdog",
        };
        thread.Start();
    }

    private static async Task<Socket?> ConnectAsync(string socketPath, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + ConnectPatience;
        while (DateTime.UtcNow < deadline)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellation);
                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
            }
        }

        return null;
    }
}
