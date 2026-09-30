using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AetherFrame.ImageJobs;
using AetherFrame.Protocol.Remote;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Images;

/// <summary>
/// The server's side of the image worker (decision I2). The server owns the listening socket, in a
/// folder the worker's container mounts read-only, so the worker has no socket it could replace.
/// Each worker run connects, takes one job and ends; at most 16 jobs wait, and a 17th is told to
/// retry. Everything the worker answers is untrusted: <see cref="ProcessedImages.Check"/> checks it.
/// </summary>
internal sealed class ImageWorkerClient(IOptions<ServerOptions> options, ILogger<ImageWorkerClient> logger) : BackgroundService, IImageProcessor
{
    /// <summary>The most jobs waiting or running at once (decision I2).</summary>
    public const int MaxQueued = 16;

    /// <summary>How long a job waits for its turn and a worker run, from the moment it is queued.</summary>
    internal TimeSpan WorkerPatience { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a worker has to answer a job.</summary>
    internal TimeSpan JobDeadline { get; set; } = TimeSpan.FromSeconds(30);

    private readonly Channel<Socket> connected = Channel.CreateBounded<Socket>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropWrite });
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
                // A worker run that ended before it took the job (its connection is stale) is skipped
                // for the next one; one that took it and failed refuses the image.
                while (true)
                {
                    Socket worker;
                    try
                    {
                        worker = await connected.Reader.ReadAsync(waiting.Token);
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
                            await ImageJobWire.WriteJobAsync(stream, new ImageJob((byte)declared.Format, declared.Width, declared.Height, bytes.ToArray()), deadline.Token);
                        }
                        catch (Exception e) when (e is IOException or SocketException && !cancellation.IsCancellationRequested)
                        {
                            continue;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            // The worker runs as another user; it may connect, and nothing else is in the folder.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }

        listener.Listen(4);
        while (!stoppingToken.IsCancellationRequested)
        {
            var worker = await listener.AcceptAsync(stoppingToken);
            if (!connected.Writer.TryWrite(worker))
            {
                worker.Dispose();
            }
        }
    }
}
