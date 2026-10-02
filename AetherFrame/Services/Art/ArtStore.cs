using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Rendering;

namespace AetherFrame.Services.Art;

/// <summary>
/// Where each built-in artwork's bytes come from (art on demand, the owner's choice of October 1,
/// 2026): the plugin assembly when it embeds the artwork, otherwise a copy downloaded the first time
/// a player uses it, kept on this PC and checked against <see cref="ArtFiles"/> on every read.
///
/// <para><b>The cache.</b> One file per hosted file, named by its SHA-256
/// (<c>&lt;config&gt;/artwork-cache/&lt;sha256&gt;.png</c>), so two game clients sharing the folder
/// never disagree about a name. A download is written to a temporary file and moved into place, never
/// over an existing copy. The folder is listed once, off the draw thread, when the store is made;
/// until then hosted artwork is <see cref="ArtState.Checking"/>. A copy that fails its check when
/// read is deleted, and the artwork is downloadable again.</para>
///
/// <para><b>Downloads.</b> Only through <see cref="Downloader"/>, which only the networking flavour
/// sets (decision R3): without one, hosted artwork that isn't on this PC is
/// <see cref="ArtState.Unavailable"/>. At most two run at once. Each starts only from
/// <see cref="Request"/>, which the windows call on a player's action ("Art on demand" in the
/// decision register); a failed one waits for the player to try again.</para>
///
/// <para>Thread-safe: the draw thread reads <see cref="Status"/>; loads read
/// <see cref="ReadVerified"/> on the thread pool; downloads run on the thread pool.</para>
/// </summary>
internal sealed class ArtStore : IArtSource, IDisposable
{
    /// <summary>The cache folder's name inside the plugin's configuration directory.</summary>
    internal const string CacheFolderName = "artwork-cache";

    /// <summary>How many downloads run at once.</summary>
    internal const int MaxConcurrentDownloads = 2;

    /// <summary>How old a temporary file left by an interrupted download must be before it is deleted.</summary>
    internal static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromHours(1);

    /// <summary>How many times a downloaded copy another program holds (an antivirus scan, say) is read before giving up.</summary>
    private const int ReadAttempts = 3;

    private readonly HashSet<string> embedded;
    private readonly Func<string, byte[]?> readEmbedded;
    private readonly Func<string, ArtFile?> findFile;
    private readonly string cacheDirectory;
    private readonly Func<IDisposable?>? beginOperation;
    private readonly Action<string> log;
    private readonly ConcurrentDictionary<string, Where> where = new(StringComparer.Ordinal);

    // By SHA-256: Cached, Queued, Downloading or Failed. A hosted file with no entry is not on this PC.
    private readonly ConcurrentDictionary<string, ArtStatus> states = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ArtFile> waitingForList = new();
    private readonly SemaphoreSlim slots = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private readonly CancellationTokenSource stopping;
    private readonly List<Task> running = new();
    private Func<ArtFile, IProgress<long>, CancellationToken, Task<byte[]>>? downloader;
    private volatile bool listed;
    private int generation;
    private bool disposed;

    /// <param name="embeddedResources">The manifest resource names the plugin assembly embeds.</param>
    /// <param name="readEmbedded">Reads one embedded resource's bytes, or null when it is missing.</param>
    /// <param name="cacheDirectory">Where downloaded copies are kept (made when the first one is saved).</param>
    /// <param name="findFile">The hosted file at an asset path (<see cref="ArtFiles.Find"/>; tests pass their own).</param>
    /// <param name="beginOperation">Registers a download that writes a file, so unloading waits for it;
    /// null once unloading has begun (the download doesn't start then).</param>
    /// <param name="stopping">Signaled when the plugin unloads: downloads stop.</param>
    /// <param name="log">Where problems are logged.</param>
    internal ArtStore(
        IEnumerable<string> embeddedResources,
        Func<string, byte[]?> readEmbedded,
        string cacheDirectory,
        Func<string, ArtFile?> findFile,
        Func<IDisposable?>? beginOperation,
        CancellationToken stopping,
        Action<string> log)
    {
        embedded = new HashSet<string>(embeddedResources, StringComparer.Ordinal);
        this.readEmbedded = readEmbedded;
        this.cacheDirectory = cacheDirectory;
        this.findFile = findFile;
        this.beginOperation = beginOperation;
        this.stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        this.log = log;
        Track(Task.Run(List));
    }

    private enum Where
    {
        Embedded,
        Hosted,
        Nowhere,
    }

    /// <summary>
    /// Downloads one hosted file, checked against its length and SHA-256, reporting the bytes received
    /// so far. Set once, by the networking flavour, before the first frame; until then (and always in
    /// a build without networking) hosted artwork that isn't on this PC can't be had.
    /// </summary>
    internal Func<ArtFile, IProgress<long>, CancellationToken, Task<byte[]>>? Downloader
    {
        get => Volatile.Read(ref downloader);
        set
        {
            Volatile.Write(ref downloader, value);
            Bump();
        }
    }

    /// <inheritdoc />
    public int Generation => Volatile.Read(ref generation);

    /// <summary>A store over the plugin assembly's own resources, with its cache in <paramref name="configDirectory"/>.</summary>
    internal static ArtStore ForAssembly(Assembly assembly, string configDirectory, Func<IDisposable?>? beginOperation, CancellationToken stopping, Action<string> log) =>
        new(
            assembly.GetManifestResourceNames(),
            name =>
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    return null;
                }

                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return bytes;
            },
            Path.Combine(configDirectory, CacheFolderName),
            ArtFiles.Find,
            beginOperation,
            stopping,
            log);

    /// <inheritdoc />
    public ArtStatus Status(BuiltInArtAsset art)
    {
        switch (Locate(art))
        {
            case Where.Embedded:
                return new ArtStatus(ArtState.Embedded);
            case Where.Hosted:
                var file = findFile(art.AssetPath)!;
                if (states.TryGetValue(file.Sha256, out var state))
                {
                    return state;
                }

                if (!listed)
                {
                    return new ArtStatus(ArtState.Checking, Total: file.Length);
                }

                return Downloader is null
                    ? new ArtStatus(ArtState.Unavailable, Total: file.Length)
                    : new ArtStatus(ArtState.NotDownloaded, Total: file.Length);
            default:
                return new ArtStatus(ArtState.Unavailable);
        }
    }

    /// <summary>The hosted file <paramref name="art"/> downloads from, or null when it is embedded or nowhere.</summary>
    internal ArtFile? HostedFile(BuiltInArtAsset art) => Locate(art) == Where.Hosted ? findFile(art.AssetPath) : null;

    /// <inheritdoc />
    public byte[] ReadVerified(BuiltInArtAsset art)
    {
        switch (Locate(art))
        {
            case Where.Embedded:
                return readEmbedded(art.ResourceName) ?? throw new FileNotFoundException("The plugin's embedded artwork is missing.", art.ResourceName);
            case Where.Hosted:
                var file = findFile(art.AssetPath)!;
                var path = CachePath(file);
                var bytes = ReadCopy(file, path);
                if (!Matches(bytes, file))
                {
                    if (TryDelete(path))
                    {
                        Forget(file);
                        log($"AetherFrame removed a damaged copy of the artwork {art.Id}; it downloads again.");
                    }
                    else
                    {
                        // Downloading again would only meet the same copy: wait for the player instead.
                        Fail(file, "a damaged copy on this PC couldn't be removed");
                        log($"AetherFrame found a damaged copy of the artwork {art.Id} that it couldn't remove ({path}).");
                    }

                    throw new InvalidDataException("The downloaded artwork was damaged.");
                }

                return bytes;
            default:
                throw new FileNotFoundException("This build can't have the artwork " + art.Id + ".");
        }
    }

    /// <summary>
    /// Starts downloading every hosted artwork in <paramref name="art"/> that isn't on this PC yet,
    /// queued or downloading (embedded and unavailable artwork is skipped). One that failed is tried
    /// again only when <paramref name="retryFailed"/>: a player's "Try again", never a window asking
    /// again each frame. Asked before the cache has been listed, a download starts once it has. Safe
    /// from any thread, and to call every frame.
    /// </summary>
    internal void Request(IEnumerable<BuiltInArtAsset> art, bool retryFailed)
    {
        if (disposed || Downloader is null)
        {
            return;
        }

        foreach (var file in art.Select(HostedFile).OfType<ArtFile>().Distinct())
        {
            if (!listed)
            {
                if (!waitingForList.Contains(file))
                {
                    waitingForList.Enqueue(file);
                }

                // The listing may have ended in between: then it won't drain this one.
                if (listed)
                {
                    StartWaiting();
                }

                continue;
            }

            Start(file, retryFailed);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stopping.Cancel();
    }

    /// <summary>For tests: completes when the listing and every download started so far have ended.</summary>
    internal Task Idle()
    {
        lock (running)
        {
            return Task.WhenAll(running.ToArray());
        }
    }

    private static bool Matches(byte[] bytes, ArtFile file) =>
        bytes.LongLength == file.Length && Convert.ToHexStringLower(SHA256.HashData(bytes)) == file.Sha256;

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private Where Locate(BuiltInArtAsset art) => where.GetOrAdd(art.Id, _ =>
        embedded.Contains(art.ResourceName) ? Where.Embedded
        : findFile(art.AssetPath) is { } file && ArtFiles.IsWellFormed(file) ? Where.Hosted
        : Where.Nowhere);

    private string CachePath(ArtFile file) => Path.Combine(cacheDirectory, file.Sha256 + ".png");

    private void Bump() => Interlocked.Increment(ref generation);

    private void Fail(ArtFile file, string problem)
    {
        states[file.Sha256] = new ArtStatus(ArtState.Failed, Total: file.Length, Problem: problem);
        Bump();
    }

    /// <summary>
    /// A downloaded copy's bytes. One that is gone makes the artwork downloadable again; one that
    /// another program holds is read again a few times, then the artwork fails until the player tries
    /// again (so a held copy is never mistaken for a missing one, and never read every frame).
    /// </summary>
    private byte[] ReadCopy(ArtFile file, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                Forget(file);
                throw new FileNotFoundException("The downloaded artwork is no longer on this PC; it downloads again.", path, e);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt < ReadAttempts)
                {
                    Thread.Sleep(100 * attempt);
                    continue;
                }

                Fail(file, "the copy on this PC couldn't be read");
                throw;
            }
        }
    }

    private void StartWaiting()
    {
        while (waitingForList.TryDequeue(out var file))
        {
            Start(file, retryFailed: false);
        }
    }
    private void Forget(ArtFile file)
    {
        if (states.TryRemove(file.Sha256, out _))
        {
            Bump();
        }
    }

    private void Track(Task task)
    {
        lock (running)
        {
            running.RemoveAll(t => t.IsCompleted);
            running.Add(task);
        }
    }

    private void List()
    {
        try
        {
            if (Directory.Exists(cacheDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(cacheDirectory))
                {
                    var name = Path.GetFileName(path);
                    if (name.EndsWith(".tmp", StringComparison.Ordinal))
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > StaleTemporaryAge)
                        {
                            TryDelete(path);
                        }

                        continue;
                    }

                    // "<64 hex>.png": checked against its file when read, not here.
                    if (name.Length == 68 && name.EndsWith(".png", StringComparison.Ordinal) && name[..64].All(Uri.IsHexDigit))
                    {
                        states.TryAdd(name[..64].ToLowerInvariant(), new ArtStatus(ArtState.Cached, Total: new FileInfo(path).Length));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log("AetherFrame couldn't list its downloaded artwork (" + e.Message + "); it downloads again when used.");
        }
        finally
        {
            listed = true;
            Bump();
            StartWaiting();
        }
    }

    private void Start(ArtFile file, bool retryFailed)
    {
        if (disposed)
        {
            return;
        }

        var queued = new ArtStatus(ArtState.Queued, Total: file.Length);
        if (!states.TryAdd(file.Sha256, queued))
        {
            if (!retryFailed || !states.TryGetValue(file.Sha256, out var current) || current.State != ArtState.Failed || !states.TryUpdate(file.Sha256, queued, current))
            {
                return;
            }
        }

        Bump();
        Track(Task.Run(() => DownloadAsync(file)));
    }

    private async Task DownloadAsync(ArtFile file)
    {
        IDisposable? lease = null;
        if (beginOperation is not null && (lease = beginOperation()) is null)
        {
            states.TryRemove(file.Sha256, out _);
            Bump();
            return;
        }

        try
        {
            var token = stopping.Token;
            await slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var download = Downloader ?? throw new OperationCanceledException(token);
                states[file.Sha256] = new ArtStatus(ArtState.Downloading, 0, file.Length);
                Bump();
                var progress = new Progress(this, file);
                var bytes = await download(file, progress, token).ConfigureAwait(false);
                if (!Matches(bytes, file))
                {
                    throw new ArtDownloadException(ArtDownloadProblem.WrongDigest);
                }

                token.ThrowIfCancellationRequested();
                Save(file, bytes);
                states[file.Sha256] = new ArtStatus(ArtState.Cached, Total: file.Length);
            }
            finally
            {
                slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            states.TryRemove(file.Sha256, out _);
        }
        catch (ArtDownloadException e)
        {
            states[file.Sha256] = new ArtStatus(ArtState.Failed, Total: file.Length, Problem: e.Message);
            log($"AetherFrame couldn't download the artwork {file.Path}: {e.Message}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            states[file.Sha256] = new ArtStatus(ArtState.Failed, Total: file.Length, Problem: "it couldn't be saved on this PC");
            log($"AetherFrame couldn't save the artwork {file.Path}: {e.Message}.");
        }
        catch (Exception e)
        {
            // Never left Queued or Downloading for the session.
            states[file.Sha256] = new ArtStatus(ArtState.Failed, Total: file.Length, Problem: "something went wrong");
            log($"AetherFrame couldn't download the artwork {file.Path}: {e}");
        }
        finally
        {
            Bump();
            lease?.Dispose();
        }
    }

    private void Save(ArtFile file, byte[] bytes)
    {
        Directory.CreateDirectory(cacheDirectory);
        var target = CachePath(file);
        var temporary = Path.Combine(cacheDirectory, file.Sha256 + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            try
            {
                File.Move(temporary, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another game client saved the same file first, or a damaged copy is still there:
                // keep a good copy, and replace a damaged one (a failure here fails the download).
                if (!CopyIsGood(file, target))
                {
                    File.Move(temporary, target, overwrite: true);
                }
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                TryDelete(temporary);
            }
        }
    }

    private static bool CopyIsGood(ArtFile file, string path)
    {
        try
        {
            return Matches(File.ReadAllBytes(path), file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reports a download's bytes so far into its state, without changing the generation
    /// (progress isn't a change the loader waits for).</summary>
    private sealed class Progress(ArtStore store, ArtFile file) : IProgress<long>
    {
        public void Report(long value) =>
            store.states[file.Sha256] = new ArtStatus(ArtState.Downloading, Math.Clamp(value, 0, file.Length), file.Length);
    }
}
