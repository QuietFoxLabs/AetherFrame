using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Server.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AetherFrame.Server.Images;

/// <summary>What one canary came to.</summary>
internal enum CanaryOutcome
{
    /// <summary>The image came back and passed the server's check.</summary>
    Succeeded,

    /// <summary>The worker refused the image, failed on it, or answered something the check refuses.</summary>
    Failed,

    /// <summary>The worker's queue was full, or no worker came in time: counted neither way.</summary>
    Busy,
}

/// <summary>
/// The image canary behind <c>/v1/health</c>'s <c>images</c> (known bug 14). On the server's own
/// timer, never a request's, a fixed tiny PNG takes a publish image's path: section 8.2.1 against its
/// declaration, the image worker, then <see cref="ProcessedImages.Check"/>; the answer is then
/// discarded. It touches no database, character, limit or budget. The first runs a minute after
/// start, then one an hour, and after a failure or a busy worker the next runs 5 minutes later. A
/// busy worker is counted neither way, since a queue of players' images is no fault.
/// </summary>
internal sealed class ImageCanary(IImageProcessor processor, ServerHealth health, TimeProvider time, ILogger<ImageCanary> logger) : BackgroundService
{
    /// <summary>The canary's width and height in pixels.</summary>
    public const int Width = 2;

    /// <inheritdoc cref="Width"/>
    public const int Height = 2;

    /// <summary>
    /// A 2 by 2 PNG, 8-bit RGBA and not interlaced, of four colours, one of them half transparent:
    /// the signature, then <c>IHDR</c>, one <c>IDAT</c> of zlib-compressed rows, and <c>IEND</c>, each
    /// with its CRC. These are exactly the chunks the worker's encoder writes, so a worker that
    /// answers its input unchanged passes the check too.
    /// </summary>
    internal static ReadOnlySpan<byte> Png => PngBytes;

    private static readonly byte[] PngBytes = Convert.FromHexString(
        "89504E470D0A1A0A0000000D494844520000000200000002080600000072B60D240000001B4944415478DA63386060F0" +
        "DFE080C17F06038303FF3F7CF8D000004D4509AE2AFF82590000000049454E44AE426082");

    /// <summary>How long after start the first canary runs.</summary>
    internal TimeSpan FirstRun { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long after a good canary the next runs.</summary>
    internal TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long after a failed canary, or a busy worker, the next runs.</summary>
    internal TimeSpan Retry { get; set; } = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!health.WorkerConfigured)
        {
            return;
        }

        await Task.Delay(FirstRun, time, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var outcome = await RunOnceAsync(stoppingToken);
            await Task.Delay(outcome == CanaryOutcome.Succeeded ? Interval : Retry, time, stoppingToken);
        }
    }

    /// <summary>Runs one canary and records it, unless the worker was busy. Internal, so tests can drive it without the timer.</summary>
    internal async Task<CanaryOutcome> RunOnceAsync(CancellationToken cancellation)
    {
        var outcome = await TryAsync(cancellation);
        if (outcome != CanaryOutcome.Busy)
        {
            health.CanaryFinished(outcome == CanaryOutcome.Succeeded);
        }

        return outcome;
    }

    private async Task<CanaryOutcome> TryAsync(CancellationToken cancellation)
    {
        try
        {
            var bytes = Png.ToArray();
            var declared = new ImageReference(AssetId.NewId(), SHA256.HashData(bytes), ImageFormat.Png, bytes.Length, Width, Height);
            ImageSniffer.CheckDeclared(bytes, declared);
            var processed = await processor.ProcessAsync(declared, bytes, cancellation);
            if (processed.IsBusy)
            {
                return CanaryOutcome.Busy;
            }

            return processed.Bytes is not null && ProcessedImages.Check(processed.Bytes, declared) ? CanaryOutcome.Succeeded : CanaryOutcome.Failed;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            logger.LogWarning("The image canary failed with {ErrorKind}.", e.GetType().Name);
            return CanaryOutcome.Failed;
        }
    }
}
