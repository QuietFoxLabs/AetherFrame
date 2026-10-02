using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services.Art;
using AetherFrame.Services.Network.Transport;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Art on demand (the owner's choice of October 1, 2026): the table of hosted artwork matches the
/// files exactly; the loader loads only what its source can read and tells the window what is
/// missing; the store keeps verified copies, downloads at most two at a time and only when asked;
/// and the download client fetches exactly the pinned file and nothing else.
/// </summary>
public class ArtOnDemandTests
{
    private static readonly string AssetsDirectory = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Assets");

    // The table.

    [Fact]
    public void EveryHostedFile_IsInTheTable_WithExactlyItsBytes()
    {
        var onDisk = Directory.EnumerateFiles(Path.Combine(AssetsDirectory, "Components"), "*.png", SearchOption.AllDirectories)
            .Select(full => Path.GetRelativePath(AssetsDirectory, full).Replace('\\', '/'))
            .Where(path => !path.StartsWith("Components/CelestialDream/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(onDisk, ArtFiles.All.Select(file => file.Path).Order(StringComparer.Ordinal));
        foreach (var file in ArtFiles.All)
        {
            Assert.True(ArtFiles.IsWellFormed(file), file.Path);
            var bytes = File.ReadAllBytes(Path.Combine(AssetsDirectory, file.Path));
            Assert.Equal(file.Length, bytes.LongLength);
            Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    [Fact]
    public void ThePlainTextTable_IsTheCompiledOne()
    {
        var rows = File.ReadAllLines(Path.Combine(AssetsDirectory, "ArtFiles.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' '))
            .Select(parts => new ArtFile(parts[3], long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), parts[1], parts[0]))
            .ToList();

        Assert.Equal(ArtFiles.All, rows);
    }

    [Fact]
    public void EveryArtwork_IsHosted_OrIsCelestialDreams_AndItsPathIsItsFile()
    {
        foreach (var art in BuiltInArtCatalog.All)
        {
            Assert.True(File.Exists(Path.Combine(AssetsDirectory, art.AssetPath)), $"{art.Id}: {art.AssetPath}");
            Assert.Equal(BuiltInArtCatalog.ResourcePrefix + art.AssetPath.Replace('/', '.'), art.ResourceName);
            if (!art.AssetPath.StartsWith("Components/CelestialDream/", StringComparison.Ordinal))
            {
                Assert.NotNull(ArtFiles.Find(art.AssetPath));
            }
        }

        Assert.Null(ArtFiles.Find(BuiltInArtCatalog.AstrolabePivot.AssetPath));
        Assert.Null(ArtFiles.Find(null));
        Assert.Null(ArtFiles.Find(ArtFiles.All[0].Path.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("Components/A/B.png", true)]
    [InlineData("Components/A/B.PNG", false)]
    [InlineData("Components/../B.png", false)]
    [InlineData("Components/A/B C.png", false)]
    [InlineData("Components/A/B/C.png", false)]
    [InlineData("StylePreviews/A.png", false)]
    [InlineData("Components/A/B.png?x=1", false)]
    [InlineData("Components/A/B.png\n", false)]
    public void OnlyAPlainPath_IsWellFormed(string path, bool wellFormed)
    {
        Assert.Equal(wellFormed, ArtFiles.IsWellFormed(new ArtFile(path, 10, new string('a', 64), new string('b', 40))));
    }

    [Fact]
    public void ACommitDigestOrLengthOfAnyOtherShape_IsNotWellFormed()
    {
        var good = new ArtFile("Components/A/B.png", 10, new string('a', 64), new string('b', 40));
        Assert.True(ArtFiles.IsWellFormed(good));
        Assert.False(ArtFiles.IsWellFormed(good with { Commit = new string('B', 40) }));
        Assert.False(ArtFiles.IsWellFormed(good with { Commit = "main" }));
        Assert.False(ArtFiles.IsWellFormed(good with { Sha256 = new string('a', 63) }));
        Assert.False(ArtFiles.IsWellFormed(good with { Length = 0 }));
        Assert.False(ArtFiles.IsWellFormed(good with { Length = ArtFiles.MaxLength + 1 }));
    }

    // The loader.

    [Fact]
    public void NothingLoads_UntilTheSourceCanReadIt_AndWhatIsMissingIsRecorded()
    {
        var source = new FakeSource { State = ArtState.NotDownloaded };
        var calls = 0;
        using var loader = new BuiltInArtLoader<FakeTexture>(source, (art, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Levels(64));
        });
        var art = BuiltInArtCatalog.CelestialSakuraPlateFrameArt;
        var misses = new List<BuiltInArtAsset>();

        loader.BeginMisses(misses);
        for (var frame = 0; frame < 20; frame++)
        {
            Assert.Null(loader.GetLevelOrNull(art, 100f));
        }

        loader.EndMisses();
        Assert.Equal([art], misses);
        Assert.Equal(0, loader.LoadsStarted);

        // Outside a scope nothing is recorded.
        Assert.Null(loader.GetLevelOrNull(art, 100f));
        Assert.Single(misses);

        source.State = ArtState.Cached;
        Assert.True(SpinWait.SpinUntil(() => loader.GetLevelOrNull(art, 100f) is not null, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void AFailedLoad_IsRetried_OnlyAfterTheSourceChanges()
    {
        var source = new FakeSource { State = ArtState.Cached };
        var calls = 0;
        using var loader = new BuiltInArtLoader<FakeTexture>(source, (art, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidDataException("damaged");
            }

            return Task.FromResult(Levels(64));
        });
        var art = BuiltInArtCatalog.CelestialSakuraPlateFrameArt;

        Assert.Null(loader.GetLevelOrNull(art, 10f));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(10)));
        Thread.Sleep(50);
        for (var frame = 0; frame < 20; frame++)
        {
            Assert.Null(loader.GetLevelOrNull(art, 10f));
        }

        Assert.Equal(1, loader.LoadsStarted);

        // The damaged copy was removed and downloaded again: the source changed.
        source.Generation++;
        Assert.True(SpinWait.SpinUntil(() => loader.GetLevelOrNull(art, 10f) is not null, TimeSpan.FromSeconds(10)));
        Assert.Equal(2, loader.LoadsStarted);
    }

    [Fact]
    public void AFailedLoadWhoseCopyWasRemoved_IsRecordedAsMissing()
    {
        var source = new FakeSource { State = ArtState.Cached };
        using var loader = new BuiltInArtLoader<FakeTexture>(source, (art, _) =>
        {
            source.State = ArtState.NotDownloaded;
            throw new InvalidDataException("damaged");
        });
        var art = BuiltInArtCatalog.CelestialSakuraPlateFrameArt;
        Assert.Null(loader.GetLevelOrNull(art, 10f));
        Assert.True(SpinWait.SpinUntil(() => source.State == ArtState.NotDownloaded, TimeSpan.FromSeconds(10)));
        Thread.Sleep(50);

        var misses = new List<BuiltInArtAsset>();
        loader.BeginMisses(misses);
        Assert.Null(loader.GetLevelOrNull(art, 10f));
        Assert.Null(loader.GetLevelOrNull(art, 10f));
        loader.EndMisses();

        Assert.Equal([art], misses);
    }

    // The store.

    [Fact]
    public async Task EmbeddedArt_IsReadFromThePlugin_AndNeverDownloaded()
    {
        using var folder = new TempDirectory();
        var art = Asset("Components/Embedded/Embedded_Background.png");
        var downloads = 0;
        using var store = Store(folder, [art.ResourceName], resource => resource == art.ResourceName ? [1, 2, 3] : null, _ => null);
        store.Downloader = (_, _, _) =>
        {
            Interlocked.Increment(ref downloads);
            return Task.FromResult(Array.Empty<byte>());
        };
        await store.Idle();

        Assert.Equal(ArtState.Embedded, store.Status(art).State);
        Assert.Equal([1, 2, 3], store.ReadVerified(art));
        store.Request([art], retryFailed: true);
        await store.Idle();
        Assert.Equal(0, downloads);
    }

    [Fact]
    public async Task HostedArt_DownloadsWhenRequested_IsKept_AndReadsVerified()
    {
        using var folder = new TempDirectory();
        var (art, file, bytes) = Hosted("Components/Set/Set_Background.png", 5000);
        using var store = Store(folder, [], _ => null, path => path == file.Path ? file : null);
        await store.Idle();
        Assert.Equal(ArtState.Unavailable, store.Status(art).State); // no downloader: a build without networking

        store.Downloader = (requested, progress, _) =>
        {
            Assert.Same(file, requested);
            progress.Report(bytes.Length);
            return Task.FromResult(bytes.ToArray());
        };
        Assert.Equal(new ArtStatus(ArtState.NotDownloaded, Total: 5000), store.Status(art));
        var before = store.Generation;

        store.Request([art, art], retryFailed: false);
        await store.Idle();

        Assert.Equal(ArtState.Cached, store.Status(art).State);
        Assert.True(store.Generation > before);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(folder.Path, ArtStore.CacheFolderName, file.Sha256 + ".png")));
        Assert.Equal(bytes, store.ReadVerified(art));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(folder.Path, ArtStore.CacheFolderName), "*.tmp"));

        // A new store finds it on this PC.
        using var later = Store(folder, [], _ => null, path => path == file.Path ? file : null);
        await later.Idle();
        Assert.Equal(ArtState.Cached, later.Status(art).State);
        Assert.Equal(bytes, later.ReadVerified(art));
    }

    [Fact]
    public async Task ADamagedCopy_IsDeletedWhenRead_AndTheArtworkIsDownloadableAgain()
    {
        using var folder = new TempDirectory();
        var (art, file, _) = Hosted("Components/Set/Set_Divider.png", 300);
        var cache = Path.Combine(folder.Path, ArtStore.CacheFolderName);
        Directory.CreateDirectory(cache);
        var copy = Path.Combine(cache, file.Sha256 + ".png");
        File.WriteAllBytes(copy, new byte[300]);
        using var store = Store(folder, [], _ => null, path => path == file.Path ? file : null);
        store.Downloader = (_, _, _) => Task.FromResult(new byte[1]);
        await store.Idle();
        Assert.Equal(ArtState.Cached, store.Status(art).State);
        var before = store.Generation;

        Assert.Throws<InvalidDataException>(() => store.ReadVerified(art));

        Assert.False(File.Exists(copy));
        Assert.Equal(ArtState.NotDownloaded, store.Status(art).State);
        Assert.True(store.Generation > before);
    }

    [Fact]
    public async Task ADamagedCopyThatCantBeRemoved_FailsUntilTryAgain_InsteadOfDownloadingEveryFrame()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Elsewhere a file held open can still be deleted.
        }

        using var folder = new TempDirectory();
        var (art, file, bytes) = Hosted("Components/Set/Set_Held.png", 300);
        var cache = Path.Combine(folder.Path, ArtStore.CacheFolderName);
        Directory.CreateDirectory(cache);
        var copy = Path.Combine(cache, file.Sha256 + ".png");
        File.WriteAllBytes(copy, new byte[300]);
        var calls = 0;
        using var store = Store(folder, [], _ => null, path => path == file.Path ? file : null);
        store.Downloader = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(bytes.ToArray());
        };
        await store.Idle();

        using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<InvalidDataException>(() => store.ReadVerified(art));
            var held = store.Status(art);
            Assert.Equal(ArtState.Failed, held.State);
            Assert.Equal("a damaged copy on this PC couldn't be removed", held.Problem);

            // A window asking every frame downloads nothing.
            store.Request([art], retryFailed: false);
            await store.Idle();
            Assert.Equal(0, calls);
        }

        // Once it is free, the player's Try again replaces it with a good copy.
        store.Request([art], retryFailed: true);
        await store.Idle();
        Assert.Equal(1, calls);
        Assert.Equal(ArtState.Cached, store.Status(art).State);
        Assert.Equal(bytes, store.ReadVerified(art));
    }

    [Fact]
    public async Task ACopyAnotherProgramHolds_FailsAfterAFewTries_AndTryAgainDownloadsIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Elsewhere a file held open can still be read.
        }

        using var folder = new TempDirectory();
        var (art, file, bytes) = Hosted("Components/Set/Set_Locked.png", 200);
        var cache = Path.Combine(folder.Path, ArtStore.CacheFolderName);
        Directory.CreateDirectory(cache);
        var copy = Path.Combine(cache, file.Sha256 + ".png");
        File.WriteAllBytes(copy, bytes);
        var calls = 0;
        using var store = Store(folder, [], _ => null, path => path == file.Path ? file : null);
        store.Downloader = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(bytes.ToArray());
        };
        await store.Idle();

        using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.ReadVerified(art));
            var held = store.Status(art);
            Assert.Equal(ArtState.Failed, held.State);
            Assert.Equal("the copy on this PC couldn't be read", held.Problem);
            store.Request([art], retryFailed: false);
            await store.Idle();
            Assert.Equal(0, calls);
        }

        store.Request([art], retryFailed: true);
        await store.Idle();
        Assert.Equal(1, calls);
        Assert.Equal(ArtState.Cached, store.Status(art).State);
        Assert.Equal(bytes, store.ReadVerified(art));
    }

    [Fact]
    public async Task AnUnexpectedFailure_EndsFailed_NeverStuckDownloading()
    {
        using var folder = new TempDirectory();
        var (art, file, _) = Hosted("Components/Set/Set_Odd.png", 50);
        using var store = Store(folder, [], _ => null, path => path == file.Path ? file : null);
        store.Downloader = (_, _, _) => throw new InvalidOperationException("unexpected");
        await store.Idle();

        store.Request([art], retryFailed: false);
        await store.Idle();

        Assert.Equal(new ArtStatus(ArtState.Failed, Total: 50, Problem: "something went wrong"), store.Status(art));
    }

    [Fact]
    public async Task ADamagedCopyFoundOnSaving_IsReplaced_AndAGoodOneIsKept()
    {
        using var folder = new TempDirectory();
        var (art, file, bytes) = Hosted("Components/Set/Set_Raced.png", 400);
        var cache = Path.Combine(folder.Path, ArtStore.CacheFolderName);
        var copy = Path.Combine(cache, file.Sha256 + ".png");
        var release = new TaskCompletionSource();
        using var store = Store(folder, [], _ => null, path => path == file.Path ? file : null);
        store.Downloader = async (_, _, _) =>
        {
            await release.Task;
            return bytes.ToArray();
        };
        await store.Idle();

        store.Request([art], retryFailed: false);
        Assert.True(SpinWait.SpinUntil(() => store.Status(art).State == ArtState.Downloading, TimeSpan.FromSeconds(10)));

        // Something else wrote a damaged copy meanwhile.
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(copy, new byte[400]);
        release.SetResult();
        await store.Idle();

        Assert.Equal(ArtState.Cached, store.Status(art).State);
        Assert.Equal(bytes, File.ReadAllBytes(copy));
        Assert.Empty(Directory.EnumerateFiles(cache, "*.tmp"));
    }

    [Fact]
    public async Task WrongBytes_AreNeverKept_AndTheFailureWaitsForTryAgain()
    {
        using var folder = new TempDirectory();
        var (art, file, bytes) = Hosted("Components/Set/Set_Frame.png", 100);
        var calls = 0;
        using var store = Store(folder, [], _ => null, path => path == file.Path ? file : null);
        store.Downloader = (_, _, _) => Interlocked.Increment(ref calls) == 1
            ? Task.FromResult(new byte[100])
            : Task.FromResult(bytes.ToArray());
        await store.Idle();

        store.Request([art], retryFailed: false);
        await store.Idle();
        var failed = store.Status(art);
        Assert.Equal(ArtState.Failed, failed.State);
        Assert.Contains("wasn't the one AetherFrame expects", failed.Problem, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.Path, ArtStore.CacheFolderName)) && Directory.EnumerateFiles(Path.Combine(folder.Path, ArtStore.CacheFolderName)).Any());

        // A window asking again each frame doesn't retry; the player's Try again does.
        store.Request([art], retryFailed: false);
        await store.Idle();
        Assert.Equal(1, calls);
        store.Request([art], retryFailed: true);
        await store.Idle();
        Assert.Equal(2, calls);
        Assert.Equal(ArtState.Cached, store.Status(art).State);
    }

    [Fact]
    public async Task AFailedDownload_SaysWhy()
    {
        using var folder = new TempDirectory();
        var (art, file, _) = Hosted("Components/Set/Set_Corner.png", 100);
        using var store = Store(folder, [], _ => null, path => path == file.Path ? file : null);
        store.Downloader = (_, _, _) => throw new ArtDownloadException(ArtDownloadProblem.Busy, 429);
        await store.Idle();

        store.Request([art], retryFailed: false);
        await store.Idle();

        Assert.Equal(new ArtStatus(ArtState.Failed, Total: 100, Problem: "GitHub is busy; try again in a few minutes"), store.Status(art));
    }

    [Fact]
    public async Task AtMostTwoDownloads_RunAtOnce()
    {
        using var folder = new TempDirectory();
        var hosted = Enumerable.Range(0, 5).Select(i => Hosted($"Components/Set/Set_Piece{i}.png", 64 + i)).ToList();
        var byPath = hosted.ToDictionary(h => h.File.Path, h => h.File);
        var release = new TaskCompletionSource();
        var running = 0;
        var most = 0;
        using var store = Store(folder, [], _ => null, path => byPath.GetValueOrDefault(path));
        store.Downloader = async (file, _, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            await release.Task;
            Interlocked.Decrement(ref running);
            return hosted.Single(h => h.File == file).Bytes.ToArray();
        };
        await store.Idle();

        store.Request(hosted.Select(h => h.Art), retryFailed: false);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref running) == ArtStore.MaxConcurrentDownloads, TimeSpan.FromSeconds(10)));
        Thread.Sleep(50);
        Assert.Equal(ArtStore.MaxConcurrentDownloads, hosted.Count(h => store.Status(h.Art).State == ArtState.Downloading));
        Assert.Equal(hosted.Count - ArtStore.MaxConcurrentDownloads, hosted.Count(h => store.Status(h.Art).State == ArtState.Queued));

        release.SetResult();
        await store.Idle();
        Assert.All(hosted, h => Assert.Equal(ArtState.Cached, store.Status(h.Art).State));
        Assert.Equal(ArtStore.MaxConcurrentDownloads, Volatile.Read(ref most));
    }

    [Fact]
    public async Task Unloading_CancelsDownloads_AndNoneStartsOnceItHasBegun()
    {
        using var folder = new TempDirectory();
        var (art, file, _) = Hosted("Components/Set/Set_Slow.png", 64);
        using var stopping = new CancellationTokenSource();
        var store = Store(folder, [], _ => null, path => path == file.Path ? file : null, stopping: stopping.Token);
        store.Downloader = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return [];
        };
        await store.Idle();

        store.Request([art], retryFailed: false);
        Assert.True(SpinWait.SpinUntil(() => store.Status(art).State == ArtState.Downloading, TimeSpan.FromSeconds(10)));
        stopping.Cancel();
        await store.Idle();
        Assert.Equal(ArtState.NotDownloaded, store.Status(art).State);
        store.Dispose();

        // Unloading has begun: an operation can't register, so nothing starts.
        using var refusing = Store(folder, [], _ => null, path => path == file.Path ? file : null, beginOperation: () => null);
        var calls = 0;
        refusing.Downloader = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Array.Empty<byte>());
        };
        await refusing.Idle();
        refusing.Request([art], retryFailed: false);
        await refusing.Idle();
        Assert.Equal(0, calls);
        Assert.Equal(ArtState.NotDownloaded, refusing.Status(art).State);
    }

    [Fact]
    public async Task ListingTheCache_DeletesOnlyStaleTemporaryFiles()
    {
        using var folder = new TempDirectory();
        var cache = Path.Combine(folder.Path, ArtStore.CacheFolderName);
        Directory.CreateDirectory(cache);
        var stale = Path.Combine(cache, new string('a', 64) + ".old.tmp");
        var fresh = Path.Combine(cache, new string('b', 64) + ".new.tmp");
        File.WriteAllBytes(stale, [1]);
        File.WriteAllBytes(fresh, [1]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - ArtStore.StaleTemporaryAge - TimeSpan.FromMinutes(5));

        using var store = Store(folder, [], _ => null, _ => null);
        await store.Idle();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task ArtTheTableDoesntName_IsUnavailable_AndNeverRequested()
    {
        using var folder = new TempDirectory();
        var art = Asset("Components/Unknown/Unknown_Background.png");
        var calls = 0;
        using var store = Store(folder, [], _ => null, _ => null);
        store.Downloader = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Array.Empty<byte>());
        };
        await store.Idle();

        Assert.Equal(ArtState.Unavailable, store.Status(art).State);
        store.Request([art], retryFailed: true);
        await store.Idle();
        Assert.Equal(0, calls);
        Assert.Throws<FileNotFoundException>(() => store.ReadVerified(art));
    }

    // The download client.

    [Fact]
    public async Task TheClient_FetchesExactlyThePinnedFile_FromGitHubsRawHost()
    {
        var (_, file, bytes) = Hosted("Components/Set/Set_Background.png", 70_000);
        var handler = new FakeHandler(_ => Ok(bytes));
        using var client = new ArtDownloadClient(handler, disposeHandler: false, new Version(0, 1, 9));
        var reported = new List<long>();

        var received = await client.DownloadAsync(file, new SyncProgress(reported.Add), CancellationToken.None);

        Assert.Equal(bytes, received);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/{file.Commit}/AetherFrame/Assets/{file.Path}", request.RequestUri!.AbsoluteUri);
        Assert.Equal("AetherFrame/0.1.9", request.Headers.UserAgent.ToString());
        Assert.Null(request.Content);
        Assert.Equal(70_000, reported[^1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, false)]
    [InlineData(HttpStatusCode.MovedPermanently, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task AnythingButTheFile_IsRefused_AndNoRedirectIsFollowed(HttpStatusCode status, bool busy)
    {
        var problem = busy ? ArtDownloadProblem.Busy : ArtDownloadProblem.Refused;
        var (_, file, _) = Hosted("Components/Set/Set_Background.png", 64);
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(new byte[64]) };
            response.Headers.Location = new Uri("https://example.invalid/elsewhere.png");
            return response;
        });
        using var client = new ArtDownloadClient(handler, disposeHandler: false, new Version(0, 1, 9));

        var refused = await Assert.ThrowsAsync<ArtDownloadException>(() => client.DownloadAsync(file, null, CancellationToken.None));

        Assert.Equal(problem, refused.Problem);
        Assert.Equal((int)status, refused.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AnAnswerOfAnyOtherLengthOrDigest_IsRefused()
    {
        var (_, file, bytes) = Hosted("Components/Set/Set_Background.png", 1000);

        async Task<ArtDownloadProblem> Problem(Func<HttpResponseMessage> answer)
        {
            using var client = new ArtDownloadClient(new FakeHandler(_ => answer()), disposeHandler: false, new Version(0, 1, 9));
            return (await Assert.ThrowsAsync<ArtDownloadException>(() => client.DownloadAsync(file, null, CancellationToken.None))).Problem;
        }

        Assert.Equal(ArtDownloadProblem.WrongSize, await Problem(() => Ok(bytes[..999])));
        Assert.Equal(ArtDownloadProblem.WrongSize, await Problem(() => Ok([.. bytes, 0])));
        Assert.Equal(ArtDownloadProblem.WrongSize, await Problem(() => Unsized(bytes[..999])));
        Assert.Equal(ArtDownloadProblem.WrongSize, await Problem(() => Unsized([.. bytes, 0])));
        var tampered = bytes.ToArray();
        tampered[500] ^= 1;
        Assert.Equal(ArtDownloadProblem.WrongDigest, await Problem(() => Ok(tampered)));
        Assert.Equal(ArtDownloadProblem.WrongDigest, await Problem(() => Unsized(tampered)));
    }

    [Fact]
    public async Task AMalformedFile_IsRefusedBeforeAnythingIsSent()
    {
        var (_, file, _) = Hosted("Components/Set/Set_Background.png", 64);
        var handler = new FakeHandler(_ => Ok([]));
        using var client = new ArtDownloadClient(handler, disposeHandler: false, new Version(0, 1, 9));

        await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadAsync(file with { Commit = "main" }, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadAsync(file with { Path = "Components/../../x.png" }, null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NoConnection_ASlowHost_AndCancellation_AreToldApart()
    {
        var (_, file, _) = Hosted("Components/Set/Set_Background.png", 64);

        using (var offline = new ArtDownloadClient(new FakeHandler(_ => throw new HttpRequestException("no route")), disposeHandler: false, new Version(0, 1, 9)))
        {
            Assert.Equal(ArtDownloadProblem.Offline, (await Assert.ThrowsAsync<ArtDownloadException>(() => offline.DownloadAsync(file, null, CancellationToken.None))).Problem);
        }

        var slow = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Ok([]);
        });
        using (var client = new ArtDownloadClient(slow, disposeHandler: false, new Version(0, 1, 9)) { FileTimeout = TimeSpan.FromMilliseconds(100) })
        {
            Assert.Equal(ArtDownloadProblem.TimedOut, (await Assert.ThrowsAsync<ArtDownloadException>(() => client.DownloadAsync(file, null, CancellationToken.None))).Problem);
        }

        using (var client = new ArtDownloadClient(slow, disposeHandler: false, new Version(0, 1, 9)))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(file, null, cancel.Token));
        }
    }

    [Fact]
    public void TheAddress_IsBuiltFromTheTableAlone()
    {
        var file = ArtFiles.All[0];
        Assert.Equal($"QuietFoxLabs/AetherFrame/{file.Commit}/AetherFrame/Assets/{file.Path}", ArtHosting.PathOf(file));
        Assert.Equal("raw.githubusercontent.com", ArtHosting.Host);
        Assert.Throws<ArgumentException>(() => ArtHosting.PathOf(file with { Sha256 = "x" }));
    }

    private static ArtStore Store(
        TempDirectory folder,
        IEnumerable<string> embedded,
        Func<string, byte[]?> readEmbedded,
        Func<string, ArtFile?> findFile,
        Func<IDisposable?>? beginOperation = null,
        CancellationToken stopping = default) =>
        new(embedded, readEmbedded, Path.Combine(folder.Path, ArtStore.CacheFolderName), findFile, beginOperation, stopping, _ => { });

    private static BuiltInArtAsset Asset(string path) =>
        new("af.asset.test." + Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), "Test", PlateComponentKind.Background,
            BuiltInArtCatalog.ResourcePrefix + path.Replace('/', '.'), 16, 9, Tintable: false, DefaultOpacity: 1f, CornerArtPlacement.Mirror, 1f);

    private static (BuiltInArtAsset Art, ArtFile File, byte[] Bytes) Hosted(string path, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) + path.Length);
        }

        var file = new ArtFile(path, length, Convert.ToHexStringLower(SHA256.HashData(bytes)), new string('c', 40));
        return (Asset(path), file, bytes);
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    /// <summary>A 200 whose length isn't announced: its body is read until it ends.</summary>
    private static HttpResponseMessage Unsized(byte[] body) => new(HttpStatusCode.OK) { Content = new StreamContent(new UnseekableStream(body)) };

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static LoadedArt<FakeTexture> Levels(params int[] longSides) => new(longSides.Select(s => new FakeTexture(s)).ToList(), longSides);

    private sealed class FakeTexture(int longSide) : IDisposable
    {
        public int LongSide { get; } = longSide;

        public void Dispose()
        {
        }
    }

    private sealed class FakeSource : IArtSource
    {
        private int generation;
        private volatile ArtState state;

        public ArtState State
        {
            get => state;
            set
            {
                state = value;
                Interlocked.Increment(ref generation);
            }
        }

        public int Generation
        {
            get => Volatile.Read(ref generation);
            set => Volatile.Write(ref generation, value);
        }

        public ArtStatus Status(BuiltInArtAsset art) => new(State);

        public byte[] ReadVerified(BuiltInArtAsset art) => [];
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
            : this((request, _) => Task.FromResult(answer(request)))
        {
        }

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        {
            this.answer = answer;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return answer(request, cancellationToken);
        }
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private sealed class UnseekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 100));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
