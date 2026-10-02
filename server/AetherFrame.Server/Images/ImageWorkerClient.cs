using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AetherFrame.ImageJobs;
using AetherFrame.Protocol.Remote;
using AetherFrame.Server.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Images;

/// <summary>
/// The server's side of the image worker (decision I2). The server owns the listening socket, in a
/// folder the worker's container mounts read-only, so the worker has no socket it could replace.
/// Each worker run connects, takes one job and ends; at most 16 jobs wait, and a 17th is told to
/// retry. Everything the worker answers is untrusted: <see cref="ProcessedImages.Check"/> checks it.
/// <para>
/// With <see cref="ServerOptions.ImageWorkerRuns"/>, each run has a socket of its own (I2's per-job
/// isolation): the server offers one fresh socket at a time, under a random name, answers exactly
/// one connection on it, then closes it and deletes it before it offers the next. The host mounts
/// only the socket on offer into a run's container, so a run an exploit controls can take the one
/// job it was started for and nothing else: its socket answers no second connection, and it can't
/// see any other.
/// </para>
/// <para>
/// Each connection is recorded as it is accepted, in either mode: <c>/v1/health</c>'s <c>worker</c>
/// (<see cref="ServerHealth"/>).
/// </para>
/// </summary>
internal sealed class ImageWorkerClient(IOptions<ServerOptions> options, ServerHealth health, ILogger<ImageWorkerClient> logger) : BackgroundService, IImageProcessor
{
    /// <summary>The most jobs waiting or running at once (decision I2).</summary>
    public const int MaxQueued = 16;

    /// <summary>How long a job waits for its turn and a worker run, from the moment it is queued.</summary>
    internal TimeSpan WorkerPatience { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a worker has to answer a job.</summary>
    internal TimeSpan JobDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How old a waiting connection may be and still be given a job: under the worker run's own
    /// <c>WorkerRun.IdleTimeout</c> (15 seconds), after which the run ends, so a job is never handed
    /// to a run that is timing out. The deployment also ends every run from outside after its life
    /// (N2-8).
    /// </summary>
    internal TimeSpan MaxConnectionAge { get; set; } = TimeSpan.FromSeconds(12);

    // Worker runs that have connected, newest last. When it is full, the oldest is dropped and closed.
    private readonly Channel<(Socket Socket, long Arrived)> connected = Channel.CreateBounded<(Socket Socket, long Arrived)>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest },
        static dropped => dropped.Socket.Dispose());
    private readonly SemaphoreSlim queue = new(MaxQueued, MaxQueued);

    // One job at a time: decoders never run in parallel (decision I2).
    private readonly SemaphoreSlim oneAtATime = new(1, 1);

    public async Task<ImageProcessing> ProcessAsync(ImageReference declared, ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
    {
        if (!queue.Wait(0))
        {
            return ImageProcessing.Busy;
        }

        try
        {
            // The patience runs from the moment the job is queued: its turn and a worker both come within it.
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            waiting.CancelAfter(WorkerPatience);
            try
            {
                await oneAtATime.WaitAsync(waiting.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return ImageProcessing.Busy;
            }

            try
            {
                var job = new ImageJob((byte)declared.Format, declared.Width, declared.Height, bytes.ToArray());

                // A worker run that ended before it took the job (its connection is stale) is skipped
                // for the next one; one that took it and failed refuses the image.
                while (true)
                {
                    Socket worker;
                    try
                    {
                        (worker, var arrived) = await connected.Reader.ReadAsync(waiting.Token);
                        if (Stopwatch.GetElapsedTime(arrived) > MaxConnectionAge)
                        {
                            worker.Dispose();
                            continue;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    {
                        logger.LogWarning("No image worker connected in time.");
                        return ImageProcessing.Busy;
                    }

                    using (worker)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                        deadline.CancelAfter(JobDeadline);
                        await using var stream = new NetworkStream(worker, ownsSocket: false);
                        try
                        {
                            await ImageJobWire.WriteJobAsync(stream, job, deadline.Token);
                        }
                        catch (Exception e) when (e is IOException or SocketException && !cancellation.IsCancellationRequested)
                        {
                            continue;
                        }
                        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                        {
                            // A run that connected and never read: refused, like one that stalls on the job.
                            logger.LogWarning("An image job wasn't taken in time.");
                            return ImageProcessing.Refused;
                        }

                        try
                        {
                            var output = await ImageJobWire.ReadReplyAsync(stream, deadline.Token);
                            return output is null ? ImageProcessing.Refused : ImageProcessing.Recoded(output);
                        }
                        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException && !cancellation.IsCancellationRequested)
                        {
                            // A worker that dies or stalls on a job refuses that image: the job may be what broke it.
                            logger.LogWarning("An image job ended with {ErrorKind}.", e.GetType().Name);
                            return ImageProcessing.Refused;
                        }
                    }
                }
            }
            finally
            {
                oneAtATime.Release();
            }
        }
        finally
        {
            queue.Release();
        }
    }

    /// <summary>The name pattern of a run's socket in <see cref="ServerOptions.ImageWorkerRuns"/>.</summary>
    internal const string RunSocketPattern = "run-*.sock";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.IsNullOrEmpty(options.Value.ImageWorkerRuns))
        {
            await OfferRunSocketsAsync(options.Value.ImageWorkerRuns, stoppingToken);
            return;
        }

        var path = options.Value.ImageWorkerSocket;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows())
        {
            // The worker runs as another user in the server's group (N2-8): the group may connect,
            // and no one else.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }

        listener.Listen(4);
        while (!stoppingToken.IsCancellationRequested)
        {
            var worker = await listener.AcceptAsync(stoppingToken);
            health.WorkerConnected();
            if (!connected.Writer.TryWrite((worker, Stopwatch.GetTimestamp())))
            {
                worker.Dispose();
            }
        }
    }

    /// <summary>One socket per run: offered, one connection answered, then closed and deleted, and the next offered.</summary>
    private async Task OfferRunSocketsAsync(string folder, CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(folder);

        // An earlier server's sockets answer nothing: none is left for the host to mount.
        foreach (var stale in Directory.GetFiles(folder, RunSocketPattern))
        {
            File.Delete(stale);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var path = Path.Combine(folder, "run-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)) + ".sock");
            Socket worker;
            using (var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                try
                {
                    listener.Bind(new UnixDomainSocketEndPoint(path));
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
                    }

                    listener.Listen(1);
                    worker = await listener.AcceptAsync(stoppingToken);
                    health.WorkerConnected();
                }
                finally
                {
                    // Deleted before anything else happens: the host never mounts a socket that has
                    // already answered, and this one answers nothing more once its listener closes.
                    File.Delete(path);
                }
            }

            if (!connected.Writer.TryWrite((worker, Stopwatch.GetTimestamp())))
            {
                worker.Dispose();
            }
        }
    }
}
