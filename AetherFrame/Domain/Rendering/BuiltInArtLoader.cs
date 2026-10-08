using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;

namespace AetherFrame.Domain.Rendering;

/// <summary>One artwork's loaded levels (largest first, as <see cref="BundledArtImage.BuildLevels"/>
/// makes them) and each level's longer side, for <see cref="BundledArtImage.SelectLevel"/>.</summary>
public sealed record LoadedArt<TTexture>(IReadOnlyList<TTexture> Levels, int[] LongSides);

/// <summary>
/// Loads each artwork lazily and off the draw thread; the draw thread only ever looks up what is
/// ready. Pure scheduling (no Dalamud: the texture type, the load and the source are supplied), so
/// the rules are tested.
///
/// <para><b>Why.</b> Decoding a full-resolution Celestial Sakura piece and preparing its levels
/// takes ~45–55 ms of CPU, plus an 8 MB upload; done inside Draw on first use, one Plate showing
/// the whole family stalled the UI for hundreds of milliseconds. Now the first request starts the
/// load on the thread pool and returns null (the artwork isn't drawn yet, like a user image still
/// loading), and a later frame draws it.</para>
///
/// <para><b>Art on demand.</b> A load starts only once the <see cref="IArtSource"/> can read the
/// artwork's bytes (inside the plugin, or downloaded). Until then the artwork isn't drawn, and the
/// draw records it in the current miss list (<see cref="BeginMisses"/>), so the window drawing it
/// can offer or start its download.</para>
///
/// <para><b>At most two at once.</b> Each loader runs at most <see cref="MaxConcurrentLoads"/>
/// whole loads (read, decode and upload) at a time; the rest wait their turn, off the draw thread.
/// Applying or browsing a family requests every piece in one frame, and running them all at once
/// burst the CPU and memory with a full-resolution decode each.</para>
///
/// <para><b>Lifetime.</b> Every texture a load produces is disposed exactly once: by
/// <see cref="EndFrame"/> once it is idle (below), by <see cref="Dispose"/> when the load has finished, or as soon as it finishes when it is still
/// running then (it is cancelled too, so it usually stops early; a load still waiting its turn is
/// cancelled before it begins). A failed load is retried only
/// after the source's <see cref="IArtSource.Generation"/> has changed since it failed (a download
/// finished, or a damaged copy was removed), never every frame.</para>
///
/// <para><b>Idle artwork is released.</b> Every request records when, and in which frame, the
/// artwork was asked for, loading or not. The draw owner marks each frame with
/// <see cref="BeginFrame"/> and <see cref="EndFrame"/>; at most every <see cref="SweepInterval"/>,
/// <see cref="EndFrame"/> releases each finished, successful load not asked for in
/// <see cref="IdleTimeout"/> (by a monotonic clock) nor in the current frame, however long that
/// frame took. Its entry is removed first, then each of its levels is disposed once; a later request
/// loads it again from its source, in turn like any other load. Loads still running, and failed or
/// cancelled ones (with their wait for a new generation), are never touched. GPT's policy of
/// October 8, 2026: each style tried held about 33 MB of GPU memory until the plugin unloaded.</para>
/// </summary>
public sealed class BuiltInArtLoader<TTexture> : IDisposable
    where TTexture : class, IDisposable
{
    /// <summary>How many loads one loader runs at a time.</summary>
    internal const int MaxConcurrentLoads = 2;

    /// <summary>How long a loaded artwork stays after it was last asked for.</summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How often, at most, <see cref="EndFrame"/> looks for idle artwork.</summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    private readonly IArtSource source;
    private readonly Func<BuiltInArtAsset, CancellationToken, Task<LoadedArt<TTexture>>> load;
    private readonly Func<TimeSpan> clock;
    private readonly Dictionary<string, Task<LoadedArt<TTexture>>> loads = new(StringComparer.Ordinal);

    // The source's generation when each failed load was found failed: retried once it differs.
    private readonly Dictionary<string, int> failedAt = new(StringComparer.Ordinal);

    // When, and in which frame, each artwork was last asked for.
    private readonly Dictionary<string, (TimeSpan At, long Frame)> lastAsked = new(StringComparer.Ordinal);

    // The sweep's own list, kept so a sweep allocates nothing.
    private readonly List<KeyValuePair<string, Task<LoadedArt<TTexture>>>> idle = new();
    private long frame;
    private TimeSpan nextSweep;

    // The releases handed to loads still running at Dispose, so a test can wait for them instead of guessing.
    private readonly List<Task> releasesAfterDispose = new();

    // Never disposed: a load still running when this is disposed keeps reading its token.
    private readonly CancellationTokenSource disposing = new();

    // The turns to run a load. Never disposed, for the same reason: a load ending after Dispose
    // still gives its turn back (it holds no handle until AvailableWaitHandle is asked for).
    private readonly SemaphoreSlim turns = new(MaxConcurrentLoads, MaxConcurrentLoads);
    private int waiting;
    private List<BuiltInArtAsset>? misses;
    private bool disposed;

    /// <param name="load">Decodes and uploads one artwork. Always started on the thread pool, never on
    /// the caller's thread; may throw (the artwork is then not drawn until the source changes).</param>
    public BuiltInArtLoader(Func<BuiltInArtAsset, CancellationToken, Task<LoadedArt<TTexture>>> load)
        : this(AlwaysReadable.Instance, load)
    {
    }

    /// <param name="source">Says whether an artwork's bytes can be read yet; the load reads them through it.</param>
    /// <param name="load">Decodes and uploads one artwork. Always started on the thread pool, never on
    /// the caller's thread; may throw (the artwork is then not drawn until the source changes).</param>
    public BuiltInArtLoader(IArtSource source, Func<BuiltInArtAsset, CancellationToken, Task<LoadedArt<TTexture>>> load)
        : this(source, load, StartClock())
    {
    }

    /// <param name="load">As above, for every artwork readable at once.</param>
    /// <param name="clock">Monotonic time since some fixed start, never the wall clock (tests supply their own).</param>
    internal BuiltInArtLoader(Func<BuiltInArtAsset, CancellationToken, Task<LoadedArt<TTexture>>> load, Func<TimeSpan> clock)
        : this(AlwaysReadable.Instance, load, clock)
    {
    }

    /// <param name="source">As above.</param>
    /// <param name="load">As above.</param>
    /// <param name="clock">Monotonic time since some fixed start, never the wall clock (tests supply their own).</param>
    internal BuiltInArtLoader(IArtSource source, Func<BuiltInArtAsset, CancellationToken, Task<LoadedArt<TTexture>>> load, Func<TimeSpan> clock)
    {
        this.source = source;
        this.load = load;
        this.clock = clock;
        nextSweep = clock() + SweepInterval;
    }

    /// <summary>How many loads have been started, waiting their turn or running (one per artwork, plus one per retry or reload after release).</summary>
    public int LoadsStarted { get; private set; }

    /// <summary>
    /// Completes once every load that was still running at <see cref="Dispose"/> has ended and
    /// released what it made (a load cancelled before it started counts as ended). Already complete
    /// when nothing was running then. For tests, which otherwise could only wait a guessed time.
    /// </summary>
    internal Task ReleasesAfterDispose => Task.WhenAll(releasesAfterDispose);

    /// <summary>How many loads are waiting for a turn right now. For tests, which otherwise couldn't
    /// tell a load waiting its turn from one not yet scheduled.</summary>
    internal int LoadsWaiting => Volatile.Read(ref waiting);

    /// <summary>
    /// Draw thread: from now until <see cref="EndMisses"/>, every artwork a draw asks for whose bytes
    /// can't be read yet is added to <paramref name="into"/> (once per artwork). A window draws its
    /// Plates between the two, then offers or starts what is missing. Scopes don't nest: a new one
    /// replaces the last.
    /// </summary>
    public void BeginMisses(List<BuiltInArtAsset> into) => misses = into;

    /// <summary>Draw thread: ends the scope <see cref="BeginMisses"/> started.</summary>
    public void EndMisses() => misses = null;

    /// <summary>Draw thread: a new frame starts, before it asks for any artwork. Nothing asked for
    /// from now until the next one is released.</summary>
    public void BeginFrame() => frame++;

    /// <summary>
    /// Draw thread: the frame has drawn everything. At most every <see cref="SweepInterval"/>, releases
    /// each loaded artwork not asked for in <see cref="IdleTimeout"/> nor in this frame.
    /// </summary>
    public void EndFrame()
    {
        if (disposed)
        {
            return;
        }

        var now = clock();
        if (now < nextSweep)
        {
            return;
        }

        nextSweep = now + SweepInterval;
        foreach (var entry in loads)
        {
            // Only a finished, successful load: one still running is never cut short for idleness,
            // and a failed one keeps waiting for a new generation.
            if (entry.Value.IsCompletedSuccessfully
                && lastAsked.TryGetValue(entry.Key, out var asked)
                && asked.Frame != frame
                && now - asked.At >= IdleTimeout)
            {
                idle.Add(entry);
            }
        }

        if (idle.Count == 0)
        {
            return;
        }

        // Forgotten first, so nothing can hand out or release them again; then released, once each.
        foreach (var entry in idle)
        {
            loads.Remove(entry.Key);
            lastAsked.Remove(entry.Key);
        }

        try
        {
            foreach (var entry in idle)
            {
                Release(entry.Value);
            }
        }
        finally
        {
            idle.Clear();
        }
    }

    /// <summary>
    /// Draw thread: the level of <paramref name="art"/> to draw <paramref name="screenPixels"/> across,
    /// or null while its bytes can't be read yet, while it is loading, after it failed, or once
    /// disposed. Never blocks and never touches pixels: the first readable call starts the load, and
    /// every later call is a lookup. Every call counts as a use (<see cref="EndFrame"/>).
    /// </summary>
    public TTexture? GetLevelOrNull(BuiltInArtAsset art, float screenPixels)
    {
        if (disposed)
        {
            return null;
        }

        lastAsked[art.Id] = (clock(), frame);
        if (loads.TryGetValue(art.Id, out var task))
        {
            if (task.IsCompletedSuccessfully)
            {
                var loaded = task.Result;
                return loaded.Levels.Count == 0 ? null : loaded.Levels[BundledArtImage.SelectLevel(loaded.LongSides, screenPixels)];
            }

            if (!task.IsCompleted)
            {
                return null;
            }

            // Failed or cancelled: wait for the source to change before loading it again.
            var generation = source.Generation;
            if (!failedAt.TryGetValue(art.Id, out var at))
            {
                failedAt[art.Id] = generation;
                RecordIfMissing(art);
                return null;
            }

            if (at == generation)
            {
                RecordIfMissing(art);
                return null;
            }

            loads.Remove(art.Id);
            failedAt.Remove(art.Id);
        }

        if (!source.Status(art).Readable)
        {
            Record(art);
            return null;
        }

        var token = disposing.Token;
        var started = Task.Run(() => LoadInTurnAsync(art, token), token);
        started.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // reported by the load
        loads.Add(art.Id, started);
        LoadsStarted++;
        return null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disposing.Cancel();
        foreach (var task in loads.Values)
        {
            // Exactly one of the two runs per load, so every texture is released exactly once.
            if (task.IsCompleted)
            {
                Release(task);
            }
            else
            {
                releasesAfterDispose.Add(task.ContinueWith(Release, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            }
        }

        loads.Clear();
        failedAt.Clear();
        lastAsked.Clear();
    }

    private static Func<TimeSpan> StartClock()
    {
        var watch = Stopwatch.StartNew();
        return () => watch.Elapsed;
    }

    /// <summary>Thread pool: waits for a turn, then runs the whole load in it. A load cancelled while
    /// it waits never begins; the turn is given back however the load ends, before its task completes.</summary>
    private async Task<LoadedArt<TTexture>> LoadInTurnAsync(BuiltInArtAsset art, CancellationToken token)
    {
        Interlocked.Increment(ref waiting);
        try
        {
            await turns.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref waiting);
        }

        try
        {
            token.ThrowIfCancellationRequested();
            return await load(art, token).ConfigureAwait(false);
        }
        finally
        {
            turns.Release();
        }
    }

    private static void Release(Task<LoadedArt<TTexture>> task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            return;
        }

        foreach (var level in task.Result.Levels)
        {
            level.Dispose();
        }
    }

    private void RecordIfMissing(BuiltInArtAsset art)
    {
        // A load that failed because its copy was damaged leaves the artwork downloadable again.
        if (!source.Status(art).Readable)
        {
            Record(art);
        }
    }

    private void Record(BuiltInArtAsset art)
    {
        if (misses is { } list && !list.Contains(art))
        {
            list.Add(art);
        }
    }

    /// <summary>Every artwork readable at once: the source of a loader given only a load (tests, and
    /// the behavior before art on demand).</summary>
    private sealed class AlwaysReadable : IArtSource
    {
        internal static readonly AlwaysReadable Instance = new();

        public int Generation => 0;

        public ArtStatus Status(BuiltInArtAsset art) => new(ArtState.Embedded);

        public byte[] ReadVerified(BuiltInArtAsset art) => throw new NotSupportedException("A loader without a source reads nothing itself.");
    }
}
