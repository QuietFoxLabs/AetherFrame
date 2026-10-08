using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The bundled-art texture load (the Celestial Sakura UI hitch investigation): every artwork is
/// decoded and uploaded at most once, on the thread pool, at most two at once per loader, never inside Draw; Draw only looks up
/// what is ready; every texture is released exactly once.
/// </summary>
public class BuiltInArtLoaderTests
{
    private static readonly BuiltInArtAsset Frame = BuiltInArtCatalog.CelestialSakuraPlateFrameArt;
    private static readonly BuiltInArtAsset Corner = BuiltInArtCatalog.CelestialSakuraCornerOrnamentArt;

    // A failure bound for the gated tests' waits, never their proof.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public void FirstRequest_StartsTheOnlyLoad_OffTheDrawThread_AndReturnsAtOnce()
    {
        using var gate = new ManualResetEventSlim();
        var calls = 0;
        var loadThread = -1;
        using var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            Interlocked.Increment(ref calls);
            loadThread = Environment.CurrentManagedThreadId;
            gate.Wait(TimeSpan.FromSeconds(10));
            return Task.FromResult(Levels(512, 256));
        });

        // Frames while it loads: nothing drawn, nothing blocked, no second load.
        for (var frame = 0; frame < 100; frame++)
        {
            Assert.Null(loader.GetLevelOrNull(Frame, 300f));
        }

        Assert.Equal(1, loader.LoadsStarted);
        gate.Set();
        var level = WaitForLevel(loader, Frame, 300f);

        Assert.Equal(512, level.LongSide);
        Assert.NotEqual(Environment.CurrentManagedThreadId, loadThread);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void AfterLoading_DrawIsALookup_NoFurtherLoadOrPixelWork()
    {
        var calls = 0;
        using var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Levels(512, 256, 128));
        });

        var first = WaitForLevel(loader, Frame, 100f);
        for (var frame = 0; frame < 1000; frame++)
        {
            Assert.Same(first, loader.GetLevelOrNull(Frame, 100f));
        }

        Assert.Equal(1, calls);
        Assert.Equal(1, loader.LoadsStarted);
        Assert.Equal(512, loader.GetLevelOrNull(Frame, 400f)!.LongSide); // level choice still follows the drawn size
        Assert.Equal(128, loader.GetLevelOrNull(Frame, 20f)!.LongSide);
    }

    [Fact]
    public void EachArtwork_LoadsOnce_Independently()
    {
        var loaded = new List<string>();
        using var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            lock (loaded)
            {
                loaded.Add(art.Id);
            }

            return Task.FromResult(Levels(64));
        });

        WaitForLevel(loader, Frame, 10f);
        WaitForLevel(loader, Corner, 10f);
        WaitForLevel(loader, Frame, 10f);

        Assert.Equal(2, loader.LoadsStarted);
        Assert.Equal([Corner.Id, Frame.Id], loaded.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AFailedLoad_IsNotRetried_AndIsNeverDrawn()
    {
        var calls = 0;
        using var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidDataException("broken");
        });

        Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(10)));
        Thread.Sleep(50);
        for (var frame = 0; frame < 10; frame++)
        {
            Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        }

        Assert.Equal(1, loader.LoadsStarted);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Dispose_ReleasesEveryLoadedTexture_Once_AndStopsLoading()
    {
        var textures = Levels(128, 64);
        var loader = new BuiltInArtLoader<FakeTexture>((art, _) => Task.FromResult(textures));
        WaitForLevel(loader, Frame, 10f);

        loader.Dispose();
        loader.Dispose();

        Assert.All(textures.Levels, t => Assert.Equal(1, t.DisposeCount));
        Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        Assert.Null(loader.GetLevelOrNull(Corner, 10f));
        Assert.Equal(1, loader.LoadsStarted);
    }

    [Fact]
    public void Dispose_WhileLoading_CancelsIt_AndReleasesWhatItFinishesWith()
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var textures = Levels(128, 64);
        var cancelled = false;
        var loader = new BuiltInArtLoader<FakeTexture>((art, token) =>
        {
            started.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            cancelled = token.IsCancellationRequested;
            return Task.FromResult(textures);
        });
        Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        loader.Dispose();
        gate.Set();

        Assert.True(SpinWait.SpinUntil(() => textures.Levels.All(t => t.DisposeCount == 1), TimeSpan.FromSeconds(10)));
        Assert.True(cancelled);
        Thread.Sleep(50);
        Assert.All(textures.Levels, t => Assert.Equal(1, t.DisposeCount));
    }

    [Fact]
    public async Task Dispose_InTheSameFrameAsTheFirstRequest_NeverLeaksOrDraws()
    {
        // The frame ends before the load does: the load is held until after Dispose, so the first
        // request always finds it unfinished. Then it is either cancelled while still queued (nothing
        // is made) or runs and has what it made released as it finishes: either way nothing leaks
        // and nothing is drawn. A load that finishes before Dispose is the case of
        // Dispose_ReleasesEveryLoadedTexture_Once_AndStopsLoading. Without the gate, a load this
        // fast could finish between the first request starting it and checking it (about 2 % of
        // runs), and the first request then correctly returned the texture, failing this test.
        using var gate = new ManualResetEventSlim();
        var made = new List<FakeTexture>();
        var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            var levels = Levels(64);
            lock (made)
            {
                made.AddRange(levels.Levels);
            }

            return Task.FromResult(levels);
        });

        Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        loader.Dispose();
        gate.Set();
        await loader.ReleasesAfterDispose.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        lock (made)
        {
            Assert.All(made, t => Assert.Equal(1, t.DisposeCount));
        }
    }

    [Fact]
    public void TheRealFamily_LoadsThroughTheLoader_EachOnce()
    {
        // The texture cache's own CPU path (resource bytes -> LoadLevels) per artwork, run by the loader.
        var calls = 0;
        using var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            Interlocked.Increment(ref calls);
            using var stream = typeof(BuiltInArtLoaderTests).Assembly.GetManifestResourceStream(art.ResourceName)!;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var levels = BundledArtImage.LoadLevels(bytes, art);
            return Task.FromResult(new LoadedArt<FakeTexture>(levels.Select(l => new FakeTexture(l.LongSide)).ToList(), levels.Select(l => l.LongSide).ToArray()));
        });

        var family = BuiltInArtCatalog.OfFamily(BuiltInArtCatalog.CelestialSakuraFamily).ToList();
        foreach (var art in family)
        {
            Assert.Null(loader.GetLevelOrNull(art, 5000f)); // every piece requested in the same frame: none blocks
        }

        foreach (var art in family)
        {
            Assert.Equal(Math.Max(art.PixelWidth, art.PixelHeight), WaitForLevel(loader, art, 5000f).LongSide);
        }

        Assert.Equal(family.Count, calls);
        Assert.Equal(family.Count, loader.LoadsStarted);
    }

    // At most two at once. Every wait below is bounded only so a failure ends; none is the proof.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AtMostTwoLoadsRun_TheRestWaitTheirTurn_WithoutBlockingTheDrawThread(bool blocking)
    {
        var gated = new GatedLoads(blocking);
        using var loader = new BuiltInArtLoader<FakeTexture>(gated.Load);
        var arts = DistinctArt(5);

        // The first two take both turns and are held there, synchronously or in their async part.
        Request(loader, arts[0]);
        Request(loader, arts[1]);
        await Task.WhenAll(gated.Entered(arts[0]), gated.Entered(arts[1])).WaitAsync(Bound);

        // With both turns taken, asking for three more still returns at once: they wait off the draw thread.
        await Task.Run(() =>
        {
            foreach (var art in arts.Skip(2))
            {
                Request(loader, art);
            }
        }).WaitAsync(Bound);
        Assert.True(SpinWait.SpinUntil(() => loader.LoadsWaiting == 3, Bound));
        Assert.Equal(2, gated.Running);
        Assert.Equal(2, gated.Calls);
        Assert.Equal(5, loader.LoadsStarted);

        foreach (var art in arts)
        {
            gated.Open(art);
        }

        foreach (var art in arts)
        {
            Assert.Equal(64, WaitForLevel(loader, art, 100f).LongSide);
        }

        Assert.Equal(BuiltInArtLoader<FakeTexture>.MaxConcurrentLoads, gated.MaxRunning);
        Assert.Equal(5, gated.Calls);
        Assert.Equal(5, loader.LoadsStarted);
    }

    [Fact]
    public async Task EachLoader_HasItsOwnTwoTurns()
    {
        var first = new GatedLoads(blocking: false);
        var second = new GatedLoads(blocking: false);
        using var a = new BuiltInArtLoader<FakeTexture>(first.Load);
        using var b = new BuiltInArtLoader<FakeTexture>(second.Load);
        var arts = DistinctArt(2);

        foreach (var art in arts)
        {
            Request(a, art);
            Request(b, art);
        }

        // Four held at once: two in each loader.
        await Task.WhenAll(arts.SelectMany(art => new[] { first.Entered(art), second.Entered(art) })).WaitAsync(Bound);
        Assert.Equal(2, first.Running);
        Assert.Equal(2, second.Running);

        foreach (var art in arts)
        {
            first.Open(art);
            second.Open(art);
            WaitForLevel(a, art, 10f);
            WaitForLevel(b, art, 10f);
        }
    }

    [Fact]
    public async Task AWaitingArtwork_AskedForEveryFrame_IsQueuedOnce_AndLoadsOnce()
    {
        var gated = new GatedLoads(blocking: true);
        using var loader = new BuiltInArtLoader<FakeTexture>(gated.Load);
        var arts = DistinctArt(3);
        Request(loader, arts[0]);
        Request(loader, arts[1]);
        await Task.WhenAll(gated.Entered(arts[0]), gated.Entered(arts[1])).WaitAsync(Bound);

        for (var frame = 0; frame < 100; frame++)
        {
            foreach (var art in arts)
            {
                Request(loader, art);
            }
        }

        Assert.True(SpinWait.SpinUntil(() => loader.LoadsWaiting == 1, Bound));
        Assert.Equal(3, loader.LoadsStarted);

        foreach (var art in arts)
        {
            gated.Open(art);
        }

        foreach (var art in arts)
        {
            WaitForLevel(loader, art, 10f);
        }

        Assert.Equal(3, gated.Calls);
        Assert.Equal(3, loader.LoadsStarted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailedLoad_GivesItsTurnToAWaitingOne(bool blocking)
    {
        var gated = new GatedLoads(blocking);
        using var loader = new BuiltInArtLoader<FakeTexture>(gated.Load);
        var arts = DistinctArt(3);
        Request(loader, arts[0]);
        Request(loader, arts[1]);
        await Task.WhenAll(gated.Entered(arts[0]), gated.Entered(arts[1])).WaitAsync(Bound);
        Request(loader, arts[2]);
        Assert.True(SpinWait.SpinUntil(() => loader.LoadsWaiting == 1, Bound));

        gated.Fail(arts[0]);
        await gated.Entered(arts[2]).WaitAsync(Bound);

        gated.Open(arts[1]);
        gated.Open(arts[2]);
        WaitForLevel(loader, arts[1], 10f);
        WaitForLevel(loader, arts[2], 10f);
        Assert.Null(loader.GetLevelOrNull(arts[0], 10f));
        Assert.Equal(3, gated.Calls);
        Assert.Equal(3, loader.LoadsStarted);
    }

    [Fact]
    public async Task Dispose_CancelsTheWaitingLoadsBeforeTheyBegin_AndReleasesWhatTheRunningOnesFinishWith()
    {
        // The running loads ignore their cancellation and finish after Dispose.
        var gated = new GatedLoads(blocking: true);
        var loader = new BuiltInArtLoader<FakeTexture>(gated.Load);
        var arts = DistinctArt(4);
        Request(loader, arts[0]);
        Request(loader, arts[1]);
        await Task.WhenAll(gated.Entered(arts[0]), gated.Entered(arts[1])).WaitAsync(Bound);
        Request(loader, arts[2]);
        Request(loader, arts[3]);
        Assert.True(SpinWait.SpinUntil(() => loader.LoadsWaiting == 2, Bound));

        loader.Dispose();
        foreach (var art in arts)
        {
            gated.Open(art);
        }

        await loader.ReleasesAfterDispose.WaitAsync(Bound);

        Assert.Equal(2, gated.Calls);
        Assert.False(gated.Entered(arts[2]).IsCompleted);
        Assert.False(gated.Entered(arts[3]).IsCompleted);
        Assert.Equal(0, loader.LoadsWaiting);
        Assert.Equal(4, gated.Made.Count);
        Assert.All(gated.Made, t => Assert.Equal(1, t.DisposeCount));
        Assert.All(arts, art => Assert.Null(loader.GetLevelOrNull(art, 10f)));
    }

    [Fact]
    public async Task Dispose_RacingLoadsAsTheyFinish_ReleasesEveryTexture_Once()
    {
        // Each round lets two held loads (and the one waiting behind them) finish while Dispose runs:
        // whichever way each one falls, everything made is released exactly once.
        for (var round = 0; round < 100; round++)
        {
            var gated = new GatedLoads(blocking: round % 2 == 0);
            var loader = new BuiltInArtLoader<FakeTexture>(gated.Load);
            var arts = DistinctArt(3);
            foreach (var art in arts)
            {
                Request(loader, art);
            }

            Assert.True(SpinWait.SpinUntil(() => gated.Calls == 2 && loader.LoadsWaiting == 1, Bound));

            using var go = new Barrier(2);
            var opening = Task.Run(() =>
            {
                go.SignalAndWait(Bound);
                foreach (var art in arts)
                {
                    gated.Open(art);
                }
            });
            Assert.True(go.SignalAndWait(Bound));
            loader.Dispose();
            await opening.WaitAsync(Bound);
            await loader.ReleasesAfterDispose.WaitAsync(Bound);

            Assert.InRange(gated.Calls, 2, 3);
            Assert.Equal(gated.Calls * 2, gated.Made.Count);
            Assert.All(gated.Made, t => Assert.Equal(1, t.DisposeCount));
            Assert.Equal(0, loader.LoadsWaiting);
        }
    }

    private static BuiltInArtAsset[] DistinctArt(int count) => BuiltInArtCatalog.All.DistinctBy(a => a.Id, StringComparer.Ordinal).Take(count).ToArray();

    private static void Request(BuiltInArtLoader<FakeTexture> loader, BuiltInArtAsset art) => Assert.Null(loader.GetLevelOrNull(art, 10f));

    private static FakeTexture WaitForLevel(BuiltInArtLoader<FakeTexture> loader, BuiltInArtAsset art, float screenPixels)
    {
        FakeTexture? level = null;
        Assert.True(SpinWait.SpinUntil(() => (level = loader.GetLevelOrNull(art, screenPixels)) is not null, TimeSpan.FromSeconds(30)), $"{art.Id} never loaded");
        return level!;
    }

    private static LoadedArt<FakeTexture> Levels(params int[] longSides) => new(longSides.Select(s => new FakeTexture(s)).ToList(), longSides);

    /// <summary>
    /// A load per artwork held at a gate the test opens (or fails), counting how many run at once.
    /// Blocking loads wait inside the callback; the others return a task and wait in its async part.
    /// Neither looks at its token, like a load that can't stop early.
    /// </summary>
    private sealed class GatedLoads(bool blocking)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> entered = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> gates = new(StringComparer.Ordinal);
        private readonly List<FakeTexture> made = new();
        private int calls;
        private int running;
        private int maxRunning;

        public int Calls => Volatile.Read(ref calls);

        public int Running => Volatile.Read(ref running);

        public int MaxRunning => Volatile.Read(ref maxRunning);

        public List<FakeTexture> Made
        {
            get
            {
                lock (made)
                {
                    return made.ToList();
                }
            }
        }

        public Task Entered(BuiltInArtAsset art) => Entry(art).Task;

        public void Open(BuiltInArtAsset art) => Gate(art).TrySetResult(true);

        public void Fail(BuiltInArtAsset art) => Gate(art).TrySetResult(false);

        public Task<LoadedArt<FakeTexture>> Load(BuiltInArtAsset art, CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            var now = Interlocked.Increment(ref running);
            for (var seen = Volatile.Read(ref maxRunning); now > seen; seen = Volatile.Read(ref maxRunning))
            {
                if (Interlocked.CompareExchange(ref maxRunning, now, seen) == seen)
                {
                    break;
                }
            }

            Entry(art).TrySetResult();
            if (!blocking)
            {
                return LoadAsync(art);
            }

            try
            {
                var gate = Gate(art).Task;
                if (!gate.Wait(Bound))
                {
                    throw new TimeoutException($"{art.Id} was never let through");
                }

                return Task.FromResult(Finish(art, gate.Result));
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        private async Task<LoadedArt<FakeTexture>> LoadAsync(BuiltInArtAsset art)
        {
            try
            {
                return Finish(art, await Gate(art).Task.WaitAsync(Bound).ConfigureAwait(false));
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        private LoadedArt<FakeTexture> Finish(BuiltInArtAsset art, bool succeed)
        {
            if (!succeed)
            {
                throw new InvalidDataException($"{art.Id} failed");
            }

            var levels = Levels(64, 32);
            lock (made)
            {
                made.AddRange(levels.Levels);
            }

            return levels;
        }

        private TaskCompletionSource Entry(BuiltInArtAsset art) => entered.GetOrAdd(art.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        private TaskCompletionSource<bool> Gate(BuiltInArtAsset art) => gates.GetOrAdd(art.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class FakeTexture(int longSide) : IDisposable
    {
        private int disposeCount;

        public int LongSide { get; } = longSide;

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public void Dispose() => Interlocked.Increment(ref disposeCount);
    }
}
