using System;
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
    /// Runs once. The exit code is 0 for an answered job, and 1 when there was none.
    /// <paramref name="jobReceived"/> runs when a job has arrived: the worker's entry point starts the
    /// watchdog there.
    /// </summary>
    public static async Task<int> RunOnceAsync(string socketPath, CancellationToken cancellation, Action? jobReceived = null)
    {
        using var socket = await ConnectAsync(socketPath, cancellation);
        if (socket is null)
        {
            return 1;
        }

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var job = await ImageJobWire.ReadJobAsync(stream, cancellation);
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

    /// <summary>Ends the process with exit code 2 once <paramref name="deadline"/> has passed: the one limit a stuck decoder can't hold off.</summary>
    public static void StartWatchdog(TimeSpan deadline)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(deadline);
            Environment.Exit(2);
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
