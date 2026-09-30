using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Network.Publishing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// NETWORK2's share check (N2-6c's second part): a saved Plate, read as a private copy of its saved
/// state, becomes exactly what would be shared, or the reasons it can't, or a failure that names its
/// cause. It resolves a frame at a time while fonts load, prepares images only once the session's
/// known-answer check has passed, and never shows the result of a check it dropped.
/// </summary>
public sealed class ShareCheckTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<string> log = new();
    private readonly Dictionary<Guid, ProfileDocument> saved = new();
    private readonly WaitingMeasurements measurements = new();
    private int registrationsOpen;
    private int prewarmed;

    public void Dispose()
    {
        foreach (var line in log)
        {
            Assert.DoesNotContain("Canary", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ASavedPlate_BecomesExactlyWhatWouldBeShared()
    {
        var plate = Save(image: true);
        using var check = NewCheck();

        check.Begin(plate.ProfileId);
        Assert.Equal((ShareCheckStage.Resolving, plate.Name), (check.View.Stage, check.View.PlateName));
        check.OnFrame();
        await Settled(check);

        var view = check.View;
        Assert.Equal(ShareCheckStage.Ready, view.Stage);
        var candidate = view.Candidate!;
        Assert.Equal(plate.ProfileId, candidate.PlateId);
        Assert.Contains(candidate.Items, item => item is LayoutText { Text: "Shared words" });
        Assert.Single(candidate.Images);
        Assert.Equal(1, prewarmed);
        Assert.Equal(0, registrationsOpen);
    }

    [Fact]
    public async Task TheCheckReadsTheSavedState_NeverTheDocumentAnEditorHolds()
    {
        var plate = Save(image: false);
        var opened = new List<ProfileDocument>();
        using var check = NewCheck(open: id =>
        {
            // A private copy each time, as the Library's reading of the saved JSON gives.
            var copy = PlateDocumentsCopy(saved[id]);
            opened.Add(copy);
            return copy;
        });

        check.Begin(plate.ProfileId);

        // An edit to the document the Library holds, after the copy was made, isn't what is shared.
        ((TextProfileElement)saved[plate.ProfileId].Elements.OfType<TextProfileElement>().First(e => e.Text == "Shared words")).Text = "An unsaved edit";
        check.OnFrame();
        await Settled(check);

        Assert.Contains(check.View.Candidate!.Items, item => item is LayoutText { Text: "Shared words" });
        Assert.DoesNotContain(check.View.Candidate!.Items, item => item is LayoutText { Text: "An unsaved edit" });
        Assert.NotSame(saved[plate.ProfileId], Assert.Single(opened));
    }

    [Fact]
    public async Task FontsStillLoading_AreWaitedFor_ThenGivenUpOn()
    {
        var plate = Save(image: false);
        using var check = NewCheck(frames: 3);
        measurements.Ready = false;

        check.Begin(plate.ProfileId);
        check.OnFrame();
        check.OnFrame();
        Assert.Equal(ShareCheckStage.Resolving, check.View.Stage);

        measurements.Ready = true;
        check.OnFrame();
        await Settled(check);
        Assert.Equal(ShareCheckStage.Ready, check.View.Stage);

        measurements.Ready = false;
        check.Begin(plate.ProfileId);
        for (var frame = 0; frame < 3; frame++)
        {
            check.OnFrame();
        }

        Assert.Equal((ShareCheckStage.Failed, ShareCheckFailure.FontsLoading), (check.View.Stage, check.View.Failure));

        // Nothing more happens on later frames: a new check is needed.
        measurements.Ready = true;
        check.OnFrame();
        Assert.Equal(ShareCheckStage.Failed, check.View.Stage);
    }

    [Fact]
    public async Task APlateThatCantBeShared_IsRefused_WithItsReasons()
    {
        var plate = Save(image: false);
        plate.Name = "Two" + Environment.NewLine + "lines";
        using var check = NewCheck();

        check.Begin(plate.ProfileId);
        check.OnFrame();
        await Settled(check);

        Assert.Equal(ShareCheckStage.Refused, check.View.Stage);
        Assert.Null(check.View.Candidate);
        Assert.Contains(check.View.Problems, problem => problem.Refusal == PlateSnapshotRefusal.Name);
    }

    [Fact]
    public async Task ImagePreparationsCheck_MustPass_OrNothingIsPrepared()
    {
        var plate = Save(image: true);
        var prepared = 0;
        foreach (var selfTest in new Func<Task<bool>>[] { () => Task.FromResult(false), () => Task.FromException<bool>(new InvalidOperationException("Canary")) })
        {
            using var check = NewCheck(selfTest: selfTest, prepare: (requirements, cancellation) =>
            {
                Interlocked.Increment(ref prepared);
                return Prepare(requirements, cancellation);
            });

            check.Begin(plate.ProfileId);
            check.OnFrame();
            await Settled(check);

            Assert.Equal((ShareCheckStage.Failed, ShareCheckFailure.PreparationOff), (check.View.Stage, check.View.Failure));
        }

        Assert.Equal(0, prepared);
        Assert.Contains(log, line => line.StartsWith("Sharing: image preparation's check failed: InvalidOperationException 0x", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APreparationThatThrows_FailsTheCheck_AndTheLogNamesItsKindOnly()
    {
        var plate = Save(image: true);
        using var check = NewCheck(prepare: (_, _) => Task.FromException<IReadOnlyDictionary<ImageRequirement, ImagePreparation>>(new System.IO.IOException("Canary at a path")));

        check.Begin(plate.ProfileId);
        check.OnFrame();
        await Settled(check);

        Assert.Equal((ShareCheckStage.Failed, ShareCheckFailure.PreparationFailed), (check.View.Stage, check.View.Failure));
        Assert.Contains(log, line => line.StartsWith("Sharing: preparing a Plate's images failed: IOException 0x", StringComparison.Ordinal));
        Assert.Equal(0, registrationsOpen);
    }

    [Fact]
    public void APlateThatCantBeRead_OrIsntTheOneNamed_FailsTheCheck()
    {
        var plate = Save(image: false);
        var other = Save(image: false);
        foreach (var open in new Func<Guid, ProfileDocument?>[]
        {
            _ => null,
            _ => other,
            _ => throw new InvalidOperationException("Canary"),
        })
        {
            using var check = NewCheck(open: open);
            check.Begin(plate.ProfileId);
            Assert.Equal((ShareCheckStage.Failed, ShareCheckFailure.PlateUnavailable, plate.ProfileId), (check.View.Stage, check.View.Failure, check.View.PlateId));
            check.OnFrame();
            Assert.Equal(ShareCheckStage.Failed, check.View.Stage);
        }
    }

    [Fact]
    public async Task ANewCheck_DropsTheOneBefore_AndItsResultIsNeverShown()
    {
        var first = Save(image: true);
        var second = Save(image: false);
        var slow = new TaskCompletionSource<IReadOnlyDictionary<ImageRequirement, ImagePreparation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstCancellation = default;
        using var check = NewCheck(prepare: (requirements, cancellation) =>
        {
            if (requirements.Count > 0)
            {
                firstCancellation = cancellation;
                return slow.Task;
            }

            return Prepare(requirements, cancellation);
        });

        check.Begin(first.ProfileId);
        check.OnFrame();
        Assert.Equal(ShareCheckStage.Preparing, check.View.Stage);
        await WaitFor(() => firstCancellation.CanBeCanceled);

        check.Begin(second.ProfileId);
        Assert.True(firstCancellation.IsCancellationRequested);
        check.OnFrame();
        await Settled(check);
        Assert.Equal((ShareCheckStage.Ready, second.ProfileId), (check.View.Stage, check.View.PlateId));

        // The first check's preparation finishes late: nothing of it is shown.
        slow.SetResult(await Prepare(Resolve(first).Requirements, CancellationToken.None));
        await WaitFor(() => registrationsOpen == 0);
        Assert.Equal((ShareCheckStage.Ready, second.ProfileId), (check.View.Stage, check.View.PlateId));
        Assert.Equal(second.ProfileId, check.View.Candidate!.PlateId);
    }

    [Fact]
    public async Task Unloading_CancelsAPreparationUnderWay_SoUnloadingNeverWaitsOnIt()
    {
        var plate = Save(image: true);
        using var stopping = new CancellationTokenSource();
        var cancelled = false;
        using var check = NewCheck(stopping: stopping.Token, prepare: async (requirements, cancellation) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return await Prepare(requirements, cancellation);
        });

        check.Begin(plate.ProfileId);
        check.OnFrame();
        Assert.Equal(ShareCheckStage.Preparing, check.View.Stage);

        stopping.Cancel();
        await WaitFor(() => registrationsOpen == 0);
        Assert.True(cancelled);
        Assert.Equal(ShareCheckStage.Preparing, check.View.Stage);
    }

    [Fact]
    public async Task Unloading_DoesntWaitForTheKnownAnswerCheck()
    {
        var plate = Save(image: true);
        using var stopping = new CancellationTokenSource();
        var neverEnds = new TaskCompletionSource<bool>();
        using var check = NewCheck(stopping: stopping.Token, selfTest: () => neverEnds.Task);

        check.Begin(plate.ProfileId);
        check.OnFrame();
        Assert.Equal(ShareCheckStage.Preparing, check.View.Stage);

        stopping.Cancel();
        await WaitFor(() => registrationsOpen == 0);
        Assert.Equal(ShareCheckStage.Preparing, check.View.Stage);
        Assert.DoesNotContain(log, line => line.Contains("check failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachImagesSize_IsReadOncePerCheck_WhileFontsLoad()
    {
        var plate = Save(image: true);
        using var check = NewCheck(frames: 10);
        measurements.Ready = false;

        check.Begin(plate.ProfileId);
        for (var frame = 0; frame < 5; frame++)
        {
            check.OnFrame();
        }

        measurements.Ready = true;
        check.OnFrame();
        await Settled(check);

        Assert.Equal(ShareCheckStage.Ready, check.View.Stage);
        Assert.Equal(1, measurements.SizeReads);

        // A new check reads it again: the file may have changed since.
        check.Begin(plate.ProfileId);
        check.OnFrame();
        await Settled(check);
        Assert.Equal(2, measurements.SizeReads);
    }

    [Fact]
    public void AResolveThatThrows_FailsTheCheckOnce_AndTheLogNamesItsKindOnly()
    {
        var plate = Save(image: false);
        using var check = NewCheck();
        measurements.Throw = true;

        check.Begin(plate.ProfileId);
        check.OnFrame();

        Assert.Equal((ShareCheckStage.Failed, ShareCheckFailure.ResolveFailed), (check.View.Stage, check.View.Failure));
        check.OnFrame();
        Assert.Single(log, line => line.StartsWith("Sharing: resolving a Plate failed: IOException 0x", StringComparison.Ordinal));
    }

    [Fact]
    public void NothingIsPrepared_OnceUnloadingHasBegun()
    {
        var plate = Save(image: true);
        using var check = NewCheck(beginOperation: () => null);

        check.Begin(plate.ProfileId);
        check.OnFrame();

        Assert.Equal((ShareCheckStage.Failed, ShareCheckFailure.Unloading), (check.View.Stage, check.View.Failure));
    }

    [Fact]
    public void Reset_ShowsNothing()
    {
        var plate = Save(image: false);
        using var check = NewCheck();
        check.Begin(plate.ProfileId);

        check.Reset();
        check.OnFrame();

        Assert.Same(ShareCheckView.Idle, check.View);
    }

    private static ProfileDocument PlateDocumentsCopy(ProfileDocument document) =>
        AetherFrame.Persistence.PlateDocuments.Materialize(System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(document, AetherFrame.Persistence.JsonOptions.Default))!.AsObject());

    private static async Task Settled(ShareCheck check) =>
        await WaitFor(() => check.View.Stage is not (ShareCheckStage.Resolving or ShareCheckStage.Preparing));

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5);
        }
    }

    private static ResolvedPlate Resolve(ProfileDocument plate)
    {
        Assert.True(PlateSnapshotBuilder.TryResolve(plate, new FixedMeasurements(), out var resolved));
        return resolved;
    }

    private static Task<IReadOnlyDictionary<ImageRequirement, ImagePreparation>> Prepare(IReadOnlyList<ImageRequirement> requirements, CancellationToken cancellation)
    {
        var prepared = new Dictionary<ImageRequirement, ImagePreparation>();
        foreach (var requirement in requirements)
        {
            var png = PreparedPngs.Png(requirement.Window.Width, requirement.Window.Height);
            prepared[requirement] = ImagePreparation.Prepared(PreparedPngs.Prepared(png, requirement.Window.Width, requirement.Window.Height));
        }

        return Task.FromResult<IReadOnlyDictionary<ImageRequirement, ImagePreparation>>(prepared);
    }

    private ProfileDocument Save(bool image)
    {
        var plate = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Shared", PublicationCandidates.Now);
        plate.Elements.Add(new TextProfileElement { Text = "Shared words", Position = new Vector2(20f, 20f), Size = new Vector2(300f, 60f), ZIndex = 0 });
        if (image)
        {
            plate.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), Position = new Vector2(40f, 120f), Size = new Vector2(64f, 32f), ZIndex = 1 });
        }

        saved[plate.ProfileId] = plate;
        return plate;
    }

    private ShareCheck NewCheck(
        Func<Guid, ProfileDocument?>? open = null,
        Func<Task<bool>>? selfTest = null,
        Func<IReadOnlyList<ImageRequirement>, CancellationToken, Task<IReadOnlyDictionary<ImageRequirement, ImagePreparation>>>? prepare = null,
        Func<IDisposable?>? beginOperation = null,
        int frames = 120,
        CancellationToken stopping = default) => new(new ShareCheckSeams
        {
            OpenSavedPlate = open ?? (id => saved.TryGetValue(id, out var plate) ? plate : null),
            Prewarm = _ => Interlocked.Increment(ref prewarmed),
            Measurements = measurements,
            SelfTest = selfTest ?? (() => Task.FromResult(true)),
            Prepare = prepare ?? Prepare,
            BeginOperation = beginOperation ?? (() =>
            {
                Interlocked.Increment(ref registrationsOpen);
                return new Registration(this);
            }),
            Stopping = stopping,
            Log = log.Enqueue,
            ResolveFrames = frames,
        });

    /// <summary>Measurements that answer "not ready" until told otherwise, as fonts still being built do.</summary>
    private sealed class WaitingMeasurements : IPlateMeasurements
    {
        private readonly FixedMeasurements inner = new();

        internal volatile bool Ready = true;

        internal volatile bool Throw;

        private int sizeReads;

        internal int SizeReads => Volatile.Read(ref sizeReads);

        public bool TryMeasureNaturalWidth(TextProfileElement element, out float width)
        {
            inner.TryMeasureNaturalWidth(element, out width);
            return Ready;
        }

        public bool TryGetDisplayOverride(ProfileDocument plate, TextProfileElement element, out string? display)
        {
            if (Throw)
            {
                throw new System.IO.IOException("Canary at a path");
            }

            inner.TryGetDisplayOverride(plate, element, out display);
            return Ready;
        }

        public bool TryGetImageSize(Guid image, out int width, out int height)
        {
            Interlocked.Increment(ref sizeReads);
            return inner.TryGetImageSize(image, out width, out height);
        }
    }

    private sealed class Registration(ShareCheckTests owner) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner.registrationsOpen);
            }
        }
    }
}
