using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.ImageJobs;
using AetherFrame.ImageWorker;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Images;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>
/// The image worker (decision I2): what it re-encodes and what it leaves out, the server's exact
/// check of its output, the one exchange between them, and a publish through a real worker run.
/// </summary>
public class ImageWorkerTests
{
    private const string Secret = "secret-metadata";

    [Fact]
    public void APng_IsReEncodedWithoutItsMetadata_ToExactlyTheWorkersChunks()
    {
        var input = Images.Png(40, 30, withMetadata: true);
        Assert.Contains(Secret, Encoding.ASCII.GetString(input), StringComparison.Ordinal);
        var output = ImageRecoder.Recode(JobFormat.Png, 40, 30, input);
        Assert.NotNull(output);
        Assert.DoesNotContain(Secret, Encoding.ASCII.GetString(output), StringComparison.Ordinal);
        Assert.True(ProcessedImages.Check(output, Images.Declared(ImageFormat.Png, 40, 30, input)));
        Assert.True(ProcessedImages.IsWorkerPng(output));
    }

    [Fact]
    public void AJpeg_IsReEncodedWithoutItsMetadata()
    {
        var input = Images.Jpeg(64, 48, withMetadata: true);
        var output = ImageRecoder.Recode(JobFormat.Jpeg, 64, 48, input);
        Assert.NotNull(output);
        Assert.DoesNotContain(Secret, Encoding.ASCII.GetString(output), StringComparison.Ordinal);
        Assert.True(ProcessedImages.Check(output, Images.Declared(ImageFormat.Jpeg, 64, 48, input)));
    }

    [Fact]
    public void AGreyscaleJpeg_ComesBackInColour_AndOneByOneSurvives()
    {
        var grey = Images.Jpeg(16, 16, grey: true);
        var output = ImageRecoder.Recode(JobFormat.Jpeg, 16, 16, grey);
        Assert.NotNull(output);
        Assert.True(ProcessedImages.IsWorkerJpeg(output, 16, 16));

        var tiny = ImageRecoder.Recode(JobFormat.Png, 1, 1, Images.Png(1, 1));
        Assert.NotNull(tiny);
        Assert.True(ProcessedImages.IsWorkerPng(tiny));
    }

    [Fact]
    public void ALargeNoisyPng_ComesBackInManyIdatChunks_AndPasses()
    {
        var random = new Random(7);
        using var image = new Image<Rgba32>(900, 700);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                foreach (ref var pixel in rows.GetRowSpan(y))
                {
                    pixel = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
                }
            }
        });
        using var input = new MemoryStream();
        image.SaveAsPng(input);
        var output = ImageRecoder.Recode(JobFormat.Png, 900, 700, input.ToArray());
        Assert.NotNull(output);
        Assert.True(CountChunks(output, "IDAT") > 1);
        Assert.True(ProcessedImages.Check(output, Images.Declared(ImageFormat.Png, 900, 700, output)));
    }

    [Theory]
    [InlineData(72, 72, 1)]
    [InlineData(300, 300, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(118, 118, 2)]
    public void AJpegOfAnyDensity_ComesBackAt96Dpi_AndPasses(double horizontal, double vertical, int units)
    {
        using var image = new Image<Rgba32>(64, 48, new Rgba32(9, 90, 180, 255));
        image.Metadata.ResolutionUnits = (SixLabors.ImageSharp.Metadata.PixelResolutionUnit)units;
        image.Metadata.HorizontalResolution = horizontal;
        image.Metadata.VerticalResolution = vertical;
        using var input = new MemoryStream();
        image.Save(input, new JpegEncoder { Quality = 80 });
        var bytes = input.ToArray();

        // The input really carries its own density: JFIF's units at byte 13, then the two densities.
        Assert.Equal((byte)units, bytes[13]);
        Assert.Equal((int)horizontal, (bytes[14] << 8) | bytes[15]);

        var output = ImageRecoder.Recode(JobFormat.Jpeg, 64, 48, bytes);
        Assert.NotNull(output);
        Assert.True(ProcessedImages.Check(output, Images.Declared(ImageFormat.Jpeg, 64, 48, output)));
    }

    [Fact]
    public void AnExifOrientation_IsNotApplied()
    {
        using var image = new Image<Rgba32>(64, 48, new Rgba32(1, 2, 3, 255));
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        using var input = new MemoryStream();
        image.SaveAsJpeg(input);
        var output = ImageRecoder.Recode(JobFormat.Jpeg, 64, 48, input.ToArray());
        Assert.NotNull(output);
        Assert.Equal(new SniffedImage(ImageFormat.Jpeg, 64, 48), ImageSniffer.Sniff(output));
    }

    [Fact]
    public void APngWithABadChecksum_IsRefused()
    {
        var png = Images.Png(40, 30);
        var bad = (byte[])png.Clone();
        bad[8 + 8 + 13] ^= 0xFF;
        Assert.Null(ImageRecoder.Recode(JobFormat.Png, 40, 30, bad));
        Assert.NotNull(ImageRecoder.Recode(JobFormat.Png, 40, 30, png));
    }

    [Fact]
    public void HostileWorkerOutput_IsRefusedWithoutThrowing()
    {
        var jpeg = ImageRecoder.Recode(JobFormat.Jpeg, 64, 48, Images.Jpeg(64, 48))!;
        var declared = Images.Declared(ImageFormat.Jpeg, 64, 48, jpeg);
        Assert.True(ProcessedImages.Check(jpeg, declared));

        // Fill bytes before EOI, and before the JFIF header: section 8.2.1 allows both.
        byte[] fillAtEnd = [.. jpeg[..^2], 0xFF, 0xFF, 0xD9];
        byte[] fillAtStart = [0xFF, 0xD8, 0xFF, .. jpeg[2..]];
        Assert.False(ProcessedImages.Check(fillAtEnd, declared));
        Assert.False(ProcessedImages.Check(fillAtStart, declared));

        // A JFIF header grown to carry a thumbnail.
        var thumbnail = new byte[100 * 50 * 3];
        var app0Length = 16 + thumbnail.Length;
        byte[] withThumbnail = [0xFF, 0xD8, 0xFF, 0xE0, (byte)(app0Length >> 8), (byte)app0Length, .. jpeg[6..17], 100, 50, .. thumbnail, .. jpeg[20..]];
        Assert.False(ProcessedImages.Check(withThumbnail, declared));

        // Other Huffman tables, and a marker inside the scan.
        var tables = (byte[])jpeg.Clone();
        var huffman = tables.AsSpan().IndexOf(new byte[] { 0xFF, 0xC4 });
        tables[huffman + 30] ^= 0x01;
        Assert.False(ProcessedImages.Check(tables, declared));
        var marker = (byte[])jpeg.Clone();
        marker[^10] = 0xFF;
        marker[^9] = 0xD0;
        Assert.False(ProcessedImages.Check(marker, declared));

        // Short or empty inputs at every step.
        for (var length = 0; length < jpeg.Length; length += 37)
        {
            Assert.False(ProcessedImages.IsWorkerJpeg(jpeg.AsSpan(0, length), 64, 48));
        }

        var png = ImageRecoder.Recode(JobFormat.Png, 40, 30, Images.Png(40, 30))!;
        for (var length = 0; length < png.Length; length += 7)
        {
            Assert.False(ProcessedImages.IsWorkerPng(png.AsSpan(0, length)));
        }

        // A chunk whose length runs past the end.
        var runaway = (byte[])png.Clone();
        runaway[8 + 25 + 0] = 0x7F;
        Assert.False(ProcessedImages.IsWorkerPng(runaway));
    }

    [Fact]
    public async Task AStaleWorkerConnection_IsSkippedForTheNextRun()
    {
        using var folder = new TempFolder();
        using var client = NewClient(folder.Path);
        using var stop = new CancellationTokenSource();
        await client.StartAsync(stop.Token);
        try
        {
            // A run that connects and ends before any job.
            using (var stale = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
            {
                // The listener starts in the background: try until it answers.
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        await stale.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(folder.Socket));
                        break;
                    }
                    catch (System.Net.Sockets.SocketException) when (attempt < 100)
                    {
                        await Task.Delay(50);
                    }
                }

                stale.Shutdown(System.Net.Sockets.SocketShutdown.Both);
            }

            await Task.Delay(200);
            var workers = RunWorkersAsync(folder.Socket, stop.Token);
            var png = Images.Png(40, 30);
            var processed = await client.ProcessAsync(Images.Declared(ImageFormat.Png, 40, 30, png), png, default);
            Assert.NotNull(processed.Bytes);
            await stop.CancelAsync();
            await workers.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        finally
        {
            await stop.CancelAsync();
            await client.StopAsync(default);
        }
    }

    private static int CountChunks(byte[] png, string type)
    {
        var count = 0;
        var position = 8;
        while (position + 12 <= png.Length)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            if (Encoding.ASCII.GetString(png, position + 4, 4) == type)
            {
                count++;
            }

            position += 12 + length;
        }

        return count;
    }

    [Fact]
    public void AnImageUnlikeItsJob_OrNoImage_IsRefused()
    {
        var png = Images.Png(40, 30);
        Assert.Null(ImageRecoder.Recode(JobFormat.Png, 41, 30, png));
        Assert.Null(ImageRecoder.Recode(JobFormat.Jpeg, 40, 30, png));
        Assert.Null(ImageRecoder.Recode(JobFormat.Png, 40, 30, [1, 2, 3]));
        Assert.Null(ImageRecoder.Recode(JobFormat.Png, 40, 30, png[..(png.Length / 2)]));
        Assert.Null(ImageRecoder.Recode(JobFormat.Png, 40, 30, []));
    }

    [Fact]
    public void TheServersCheck_RefusesAnythingTheWorkersEncoderDoesntWrite()
    {
        var png = ImageRecoder.Recode(JobFormat.Png, 40, 30, Images.Png(40, 30))!;
        var declaredPng = Images.Declared(ImageFormat.Png, 40, 30, png);
        Assert.True(ProcessedImages.Check(png, declaredPng));

        // A valid PNG with a text chunk, or in RGB, or interlaced: not what the worker writes.
        Assert.False(ProcessedImages.Check(Images.Png(40, 30, withMetadata: true), declaredPng));
        Assert.False(ProcessedImages.Check(Images.Png(40, 30, rgb: true), declaredPng));
        Assert.False(ProcessedImages.Check(Images.Png(40, 30, interlaced: true), declaredPng));
        Assert.False(ProcessedImages.Check(png, Images.Declared(ImageFormat.Png, 40, 31, png)));

        var jpeg = ImageRecoder.Recode(JobFormat.Jpeg, 64, 48, Images.Jpeg(64, 48))!;
        var declaredJpeg = Images.Declared(ImageFormat.Jpeg, 64, 48, jpeg);
        Assert.True(ProcessedImages.Check(jpeg, declaredJpeg));

        // With EXIF, or with one component: refused.
        Assert.False(ProcessedImages.Check(Images.Jpeg(64, 48, withMetadata: true), declaredJpeg));
        Assert.False(ProcessedImages.Check(Images.Jpeg(64, 48, grey: true), declaredJpeg));

        // The same frame marked progressive (SOF2), which section 8.2.1 allows and the worker never writes.
        var progressive = (byte[])jpeg.Clone();
        var frame = progressive.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        progressive[frame + 1] = 0xC2;
        Assert.False(ProcessedImages.Check(progressive, declaredJpeg));

        // A comment segment spliced in after the JFIF header.
        var withComment = jpeg[..20].Concat(new byte[] { 0xFF, 0xFE, 0x00, 0x04, (byte)'h', (byte)'i' }).Concat(jpeg[20..]).ToArray();
        Assert.False(ProcessedImages.Check(withComment, declaredJpeg));
    }

    [Fact]
    public async Task TheWire_CarriesOneJobAndOneReply_AndRefusesAnythingElse()
    {
        using var stream = new MemoryStream();
        await ImageJobWire.WriteJobAsync(stream, new ImageJob(1, 40, 30, [1, 2, 3]), default);
        stream.Position = 0;
        var job = await ImageJobWire.ReadJobAsync(stream, default);
        Assert.Equal((1, 40, 30), (job!.Format, job.Width, job.Height));
        Assert.Equal([1, 2, 3], job.Bytes);

        static async Task<ImageJob?> ReadJob(byte[] bytes) => await ImageJobWire.ReadJobAsync(new MemoryStream(bytes), default);
        var good = stream.ToArray();
        Assert.Null(await ReadJob(good[..^1]));
        Assert.Null(await ReadJob([.. "AFIX"u8.ToArray(), .. good[4..]]));
        var badFormat = (byte[])good.Clone();
        badFormat[5] = 3;
        Assert.Null(await ReadJob(badFormat));
        var huge = (byte[])good.Clone();
        huge[14] = 0x7F;
        Assert.Null(await ReadJob(huge));

        using var reply = new MemoryStream();
        await ImageJobWire.WriteReplyAsync(reply, [9, 8], default);
        reply.Position = 0;
        Assert.Equal([9, 8], await ImageJobWire.ReadReplyAsync(reply, default));
        using var refusal = new MemoryStream();
        await ImageJobWire.WriteReplyAsync(refusal, null, default);
        refusal.Position = 0;
        Assert.Null(await ImageJobWire.ReadReplyAsync(refusal, default));
    }

    [Fact]
    public async Task TheServer_SendsAJobToAWorkerRun_AndChecksWhatComesBack()
    {
        using var folder = new TempFolder();
        using var client = NewClient(folder.Path);
        using var stop = new CancellationTokenSource();
        await client.StartAsync(stop.Token);
        var workers = RunWorkersAsync(folder.Socket, stop.Token);
        try
        {
            var png = Images.Png(40, 30, withMetadata: true);
            var processed = await client.ProcessAsync(Images.Declared(ImageFormat.Png, 40, 30, png), png, default);
            Assert.False(processed.IsBusy);
            Assert.NotNull(processed.Bytes);
            Assert.True(ProcessedImages.Check(processed.Bytes, Images.Declared(ImageFormat.Png, 40, 30, png)));

            // The next job gets a fresh run: one job per process.
            var jpeg = Images.Jpeg(64, 48);
            Assert.NotNull((await client.ProcessAsync(Images.Declared(ImageFormat.Jpeg, 64, 48, jpeg), jpeg, default)).Bytes);

            // A worker that can't decode the bytes refuses them.
            var junk = Images.Png(40, 30);
            junk[^20] ^= 0xFF;
            var refused = await client.ProcessAsync(Images.Declared(ImageFormat.Png, 40, 30, junk), junk.Take(40).ToArray(), default);
            Assert.Null(refused.Bytes);
        }
        finally
        {
            await stop.CancelAsync();
            await client.StopAsync(default);
            await workers.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    [Fact]
    public async Task AfterAnIdleSpell_AJobStillMeetsALiveRun()
    {
        using var folder = new TempFolder();
        using var client = NewClient(folder.Path);
        client.MaxConnectionAge = TimeSpan.FromSeconds(1);
        using var stop = new CancellationTokenSource();
        await client.StartAsync(stop.Token);

        // Runs that end themselves after half a second idle, as the real ones do after 15: several
        // come and go, each leaving a connection behind that is closed or stale.
        var workers = RunWorkersAsync(folder.Socket, stop.Token, idleTimeout: TimeSpan.FromMilliseconds(500));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            var png = Images.Png(40, 30);
            var started = System.Diagnostics.Stopwatch.StartNew();
            var processed = await client.ProcessAsync(Images.Declared(ImageFormat.Png, 40, 30, png), png, default);
            Assert.NotNull(processed.Bytes);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"the job took {started.Elapsed}");
        }
        finally
        {
            await stop.CancelAsync();
            await client.StopAsync(default);
            await workers.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    [Fact]
    public async Task NoWorker_IsBusy_AndTheQueueIsBounded()
    {
        using var folder = new TempFolder();
        using var client = NewClient(folder.Path);
        client.WorkerPatience = TimeSpan.FromSeconds(3);
        using var stop = new CancellationTokenSource();
        await client.StartAsync(stop.Token);
        try
        {
            var png = Images.Png(4, 4);
            var declared = Images.Declared(ImageFormat.Png, 4, 4, png);
            var waiting = Enumerable.Range(0, ImageWorkerClient.MaxQueued).Select(_ => client.ProcessAsync(declared, png, default)).ToList();
            await Task.Delay(200);
            Assert.True((await client.ProcessAsync(declared, png, default)).IsBusy);
            Assert.All(await Task.WhenAll(waiting), result => Assert.True(result.IsBusy));
        }
        finally
        {
            await stop.CancelAsync();
            await client.StopAsync(default);
        }
    }

    [Fact]
    public async Task AWorkerThatAnswersJunk_OrHangsUp_RefusesTheImage()
    {
        using var folder = new TempFolder();
        using var client = NewClient(folder.Path);
        using var stop = new CancellationTokenSource();
        await client.StartAsync(stop.Token);
        try
        {
            var png = Images.Png(4, 4);
            var declared = Images.Declared(ImageFormat.Png, 4, 4, png);
            var junk = FakeWorkerAsync(folder.Socket, "AFIR"u8.ToArray().Concat(new byte[] { 1, 0, 0x7F, 0, 0, 0 }).ToArray());
            Assert.Null((await client.ProcessAsync(declared, png, default)).Bytes);
            await junk;
            var hangUp = FakeWorkerAsync(folder.Socket, []);
            Assert.Null((await client.ProcessAsync(declared, png, default)).Bytes);
            await hangUp;
        }
        finally
        {
            await stop.CancelAsync();
            await client.StopAsync(default);
        }
    }

    [Fact]
    public async Task APlateWithAnImage_IsPublishedThroughARealWorker_AndServedAsItsReEncode()
    {
        using var server = new TestServer { UseImageWorker = true };
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        using var stop = new CancellationTokenSource();
        var profile = ProfileId.Parse((await aria.BindAsync(12345678)).GetProperty("profileId").GetString()!);
        await bram.BindAsync(23456789, "Bram Oakes", "Gilgamesh");
        var workers = RunWorkersAsync(server.ImageWorkerSocket, stop.Token);
        try
        {
            var png = Images.Png(40, 30, withMetadata: true);
            using (var published = await aria.PublishAsync(Plates.Snapshot(profile, "Plate", png), png))
            {
                Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
            }

            using var lookup = await bram.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}");
            var served = ServedProfile.Read(await lookup.Content.ReadAsByteArrayAsync());
            using var image = await bram.SendAsync("/v1/image", RequestProofKind.Image, $"{{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"marker\":\"{served.Marker}\",\"index\":0}}");
            var bytes = await image.Content.ReadAsByteArrayAsync();
            Assert.NotEqual(png, bytes);
            Assert.DoesNotContain(Secret, Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
            Assert.True(ProcessedImages.IsWorkerPng(bytes));
            Assert.Equal(new SniffedImage(ImageFormat.Png, 40, 30), ImageSniffer.Sniff(bytes));
        }
        finally
        {
            await stop.CancelAsync();
            await workers.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    [Fact]
    public async Task EachRun_HasASocketOfItsOwn_ThatAnswersOneConnection_AndIsGoneOnceItHas()
    {
        // I2's per-job isolation: one socket on offer at a time, under a random name; it answers
        // one connection, then it is deleted and closed, and a fresh one is offered for the next run.
        using var folder = new TempFolder();
        var runs = System.IO.Path.Combine(folder.Path, "runs");
        Directory.CreateDirectory(runs);
        var stale = System.IO.Path.Combine(runs, "run-0123456789abcdef0123456789abcdef.sock");
        File.WriteAllText(stale, "an earlier server's");
        using var client = NewRunsClient(runs);
        using var stop = new CancellationTokenSource();
        await client.StartAsync(stop.Token);
        try
        {
            var first = await OfferedAsync(runs, except: stale);
            Assert.False(File.Exists(stale));
            Assert.Matches("^run-[0-9a-f]{32}\\.sock$", System.IO.Path.GetFileName(first));
            Assert.Single(Directory.GetFiles(runs));

            // A run connects and takes its job, through the one socket it was given.
            var png = Images.Png(40, 30, withMetadata: true);
            var worker = Task.Run(() => WorkerRun.RunOnceAsync(first, stop.Token));
            var processed = await client.ProcessAsync(Images.Declared(ImageFormat.Png, 40, 30, png), png, default);
            Assert.NotNull(processed.Bytes);
            await worker.WaitAsync(TimeSpan.FromSeconds(10));

            // Its socket is gone and answers nothing more: a run an exploit controls can't come back for another job.
            Assert.False(File.Exists(first));
            using var again = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(() => again.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(first)));

            // The next run gets a socket of its own.
            var second = await OfferedAsync(runs, except: first);
            Assert.NotEqual(first, second);
            Assert.Single(Directory.GetFiles(runs));
            var jpeg = Images.Jpeg(64, 48);
            var next = Task.Run(() => WorkerRun.RunOnceAsync(second, stop.Token));
            Assert.NotNull((await client.ProcessAsync(Images.Declared(ImageFormat.Jpeg, 64, 48, jpeg), jpeg, default)).Bytes);
            await next.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await stop.CancelAsync();
            await client.StopAsync(default);
        }
    }

    [Fact]
    public async Task ASecondConnectionToARunsSocket_GetsNothing_EvenWhileTheFirstIsOpen()
    {
        using var folder = new TempFolder();
        var runs = System.IO.Path.Combine(folder.Path, "runs");
        using var client = NewRunsClient(runs);
        using var stop = new CancellationTokenSource();
        await client.StartAsync(stop.Token);
        try
        {
            var offered = await OfferedAsync(runs);
            using var first = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            await first.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(offered));
            await OfferedAsync(runs, except: offered);

            using var second = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(() => second.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(offered)));
        }
        finally
        {
            await stop.CancelAsync();
            await client.StopAsync(default);
        }
    }

    private static ImageWorkerClient NewRunsClient(string runs)
    {
        var options = Options.Create(new ServerOptions { ImageWorkerRuns = runs });
        return new ImageWorkerClient(options, NullLogger<ImageWorkerClient>.Instance) { WorkerPatience = TimeSpan.FromSeconds(10), JobDeadline = TimeSpan.FromSeconds(10) };
    }

    /// <summary>The socket the server offers now, once there is one (other than <paramref name="except"/>).</summary>
    private static async Task<string> OfferedAsync(string runs, string? except = null)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (Directory.Exists(runs) && Directory.GetFiles(runs, ImageWorkerClient.RunSocketPattern).FirstOrDefault(path => path != except) is { } offered)
            {
                return offered;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("No run socket was offered.");
    }

    private static ImageWorkerClient NewClient(string folder)
    {
        var options = Options.Create(new ServerOptions { ImageWorkerSocket = System.IO.Path.Combine(folder, "images.sock") });
        return new ImageWorkerClient(options, NullLogger<ImageWorkerClient>.Instance) { WorkerPatience = TimeSpan.FromSeconds(10), JobDeadline = TimeSpan.FromSeconds(10) };
    }

    /// <summary>Worker runs, one after another, as the container's restart policy would start them.</summary>
    internal static Task RunWorkersAsync(string socket, CancellationToken stop, TimeSpan? idleTimeout = null) => Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await WorkerRun.RunOnceAsync(socket, stop, idleTimeout: idleTimeout);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception) when (!stop.IsCancellationRequested)
            {
                await Task.Delay(50, stop);
            }
        }
    }, stop);

    /// <summary>A worker that reads a job and answers <paramref name="reply"/>, whatever it is.</summary>
    private static Task FakeWorkerAsync(string socket, byte[] reply) => Task.Run(async () =>
    {
        using var connection = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        await connection.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socket));
        await using var stream = new System.Net.Sockets.NetworkStream(connection, ownsSocket: false);
        await ImageJobWire.ReadJobAsync(stream, default);
        await stream.WriteAsync(reply);
        connection.Shutdown(System.Net.Sockets.SocketShutdown.Both);
    });

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "af-img-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Socket => System.IO.Path.Combine(Path, "images.sock");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>Images made with ImageSharp itself, with and without the metadata a worker must drop.</summary>
internal static class Images
{
    public static byte[] Png(int width, int height, bool withMetadata = false, bool rgb = false, bool interlaced = false)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(10, 200, 30, 128));
        if (withMetadata)
        {
            image.Metadata.GetPngMetadata().TextData.Add(new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData("Comment", "secret-metadata", "", ""));
        }

        using var output = new MemoryStream();
        image.Save(output, new PngEncoder
        {
            BitDepth = PngBitDepth.Bit8,
            ColorType = rgb ? PngColorType.Rgb : PngColorType.RgbWithAlpha,
            InterlaceMethod = interlaced ? PngInterlaceMode.Adam7 : PngInterlaceMode.None,
            SkipMetadata = !withMetadata,
        });
        return output.ToArray();
    }

    public static byte[] Jpeg(int width, int height, bool withMetadata = false, bool grey = false)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 100, 30, 255));
        if (withMetadata)
        {
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Software, "secret-metadata");
        }

        using var output = new MemoryStream();
        image.Save(output, new JpegEncoder
        {
            Quality = 80,
            ColorType = grey ? JpegEncodingColor.Luminance : JpegEncodingColor.YCbCrRatio420,
            SkipMetadata = !withMetadata,
        });
        return output.ToArray();
    }

    public static ImageReference Declared(ImageFormat format, int width, int height, byte[] bytes) =>
        new(AssetId.NewId(), SHA256.HashData(bytes), format, bytes.Length, width, height);
}
