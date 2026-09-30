using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>Where a share check stands.</summary>
internal enum ShareCheckStage
{
    /// <summary>Nothing is being checked.</summary>
    Idle,

    /// <summary>The saved Plate is being resolved as the renderer draws it, on the framework thread, a frame at a time while fonts load.</summary>
    Resolving,

    /// <summary>The images it draws are being prepared, off the framework thread.</summary>
    Preparing,

    /// <summary>A candidate is ready: exactly what would be shared.</summary>
    Ready,

    /// <summary>The Plate can't be shared as it is; <see cref="ShareCheckView.Problems"/> says why.</summary>
    Refused,

    /// <summary>The check couldn't finish; <see cref="ShareCheckView.Failure"/> says why.</summary>
    Failed,
}

/// <summary>Why a check couldn't finish: nothing about the Plate itself.</summary>
internal enum ShareCheckFailure
{
    /// <summary>Nothing failed.</summary>
    None,

    /// <summary>The saved Plate couldn't be read, or isn't the Plate the Library names.</summary>
    PlateUnavailable,

    /// <summary>Its fonts were still being built after every frame the check waits.</summary>
    FontsLoading,

    /// <summary>Image preparation's known-answer check didn't pass in this session, so no copies are prepared until it starts again (D5's N2-6 note, (3)).</summary>
    PreparationOff,

    /// <summary>Preparing its images failed in an unexpected way; the log names the kind.</summary>
    PreparationFailed,

    /// <summary>Resolving it failed in an unexpected way (a file it reads, say); the log names the kind.</summary>
    ResolveFailed,

    /// <summary>The plugin is unloading, so nothing new starts.</summary>
    Unloading,
}

/// <summary>What the window reads each frame: one immutable value, replaced whole.</summary>
internal sealed class ShareCheckView
{
    internal static readonly ShareCheckView Idle = new(ShareCheckStage.Idle, Guid.Empty, string.Empty, null, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.None);

    internal ShareCheckView(ShareCheckStage stage, Guid plateId, string plateName, SnapshotCandidate? candidate, IReadOnlyList<PlateSnapshotProblem> problems, ShareCheckFailure failure, ProfileDocument? source = null)
    {
        Source = source;
        Stage = stage;
        PlateId = plateId;
        PlateName = plateName;
        Candidate = candidate;
        Problems = problems;
        Failure = failure;
    }

    internal ShareCheckStage Stage { get; }

    /// <summary>The Plate checked, as the Library names it.</summary>
    internal Guid PlateId { get; }

    /// <summary>The Plate's local name, for the window's heading only.</summary>
    internal string PlateName { get; }

    /// <summary>What would be shared, for <see cref="ShareCheckStage.Ready"/>.</summary>
    internal SnapshotCandidate? Candidate { get; }

    /// <summary>Why it can't be shared, for <see cref="ShareCheckStage.Refused"/>.</summary>
    internal IReadOnlyList<PlateSnapshotProblem> Problems { get; }

    /// <summary>Why the check couldn't finish, for <see cref="ShareCheckStage.Failed"/>.</summary>
    internal ShareCheckFailure Failure { get; }

    /// <summary>
    /// The private copy of the saved Plate the check read, and a candidate was built from: what a
    /// viewer draws to show the Plate exactly as it will be shared (C3). Never an editor's document.
    /// </summary>
    internal ProfileDocument? Source { get; }
}

/// <summary>Everything a <see cref="ShareCheck"/> needs from outside it, so the plugin suite drives it with fakes and the plugin with the Library, the renderer and Dalamud's texture pipeline.</summary>
internal sealed class ShareCheckSeams
{
    /// <summary>
    /// A private copy of the saved Plate, deserialized afresh from its saved JSON and never an
    /// editor's document (D5's N2-6 note, (6)); null when it can't be read. The Library names the
    /// Plate by its file, and the copy must carry that id.
    /// </summary>
    internal required Func<Guid, ProfileDocument?> OpenSavedPlate { get; init; }

    /// <summary>Asks the fonts a Plate uses to be built (the renderer's own prewarming); framework thread.</summary>
    internal required Action<ProfileDocument> Prewarm { get; init; }

    /// <summary>The renderer's measurements; answered on the framework thread.</summary>
    internal required IPlateMeasurements Measurements { get; init; }

    /// <summary>Image preparation's known-answer check, run at most once a session: the same task every time.</summary>
    internal required Func<Task<bool>> SelfTest { get; init; }

    /// <summary>Prepares every required copy, off the framework thread (N2-6b).</summary>
    internal required Func<IReadOnlyList<ImageRequirement>, CancellationToken, Task<IReadOnlyDictionary<ImageRequirement, ImagePreparation>>> Prepare { get; init; }

    /// <summary>Registers the preparation with the plugin's unload tracking; null once unloading began, and then nothing starts.</summary>
    internal required Func<IDisposable?> BeginOperation { get; init; }

    /// <summary>Signaled when unloading begins: a preparation under way is cancelled, so unloading never waits on it.</summary>
    internal CancellationToken Stopping { get; init; }

    /// <summary>Where error kinds go; never a message, a path, an id or a Plate's text.</summary>
    internal required Action<string> Log { get; init; }

    /// <summary>How many frames a check waits for fonts before it gives up: about five seconds at 60 frames a second.</summary>
    internal int ResolveFrames { get; init; } = 300;
}

/// <summary>
/// The preview's share check (N2-6c's second part; N2-6's design, sections 1 and 3): a saved Plate
/// becomes a candidate, exactly what would be shared, or the reasons it can't be. It reads a private
/// copy of the Plate's saved JSON, never an editor's document. It resolves that copy as the
/// renderer draws it on the framework thread, a frame at a time while the Plate's fonts are built,
/// and gives up after <see cref="ShareCheckSeams.ResolveFrames"/>, never measuring with a stand-in.
/// Then, off the framework thread, and only once image preparation's known-answer check has passed
/// this session, it prepares the images drawn and maps the result. A new check drops the one before
/// it, and a result that arrives after that is never shown. Compiled only in the networking preview
/// flavour.
/// </summary>
internal sealed class ShareCheck : IDisposable
{
    private readonly ShareCheckSeams seams;
    private readonly object gate = new();
    private volatile ShareCheckView view = ShareCheckView.Idle;
    private ProfileDocument? plate;
    private CachedSizes? measurements;
    private int framesLeft;
    private int generation;
    private CancellationTokenSource? preparing;

    internal ShareCheck(ShareCheckSeams seams)
    {
        ArgumentNullException.ThrowIfNull(seams);
        this.seams = seams;
    }

    /// <summary>Where the check stands.</summary>
    internal ShareCheckView View => view;

    /// <summary>Starts checking <paramref name="plateId"/>'s saved state, dropping any check under way. Framework thread.</summary>
    internal void Begin(Guid plateId)
    {
        Drop();
        ProfileDocument? copy;
        try
        {
            copy = seams.OpenSavedPlate(plateId);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            seams.Log("Sharing: the saved Plate couldn't be read: " + PublishOutcome.Describe(e));
            copy = null;
        }

        if (copy is null || copy.ProfileId != plateId)
        {
            Show(new ShareCheckView(ShareCheckStage.Failed, plateId, copy?.Name ?? string.Empty, null, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.PlateUnavailable));
            return;
        }

        plate = copy;
        measurements = new CachedSizes(seams.Measurements);
        framesLeft = seams.ResolveFrames;
        Show(new ShareCheckView(ShareCheckStage.Resolving, plateId, copy.Name, null, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.None, copy));
    }

    /// <summary>Resolves the Plate when it waits to be, once a frame: framework thread only.</summary>
    internal void OnFrame()
    {
        if (plate is not { } current || measurements is not { } sizes || view.Stage != ShareCheckStage.Resolving)
        {
            return;
        }

        bool ready;
        ResolvedPlate resolved;
        try
        {
            seams.Prewarm(current);
            ready = PlateSnapshotBuilder.TryResolve(current, sizes, out resolved);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Once, not every frame: the check ends here.
            seams.Log("Sharing: resolving a Plate failed: " + PublishOutcome.Describe(e));
            plate = null;
            measurements = null;
            Show(Stage(ShareCheckStage.Failed, failure: ShareCheckFailure.ResolveFailed));
            return;
        }

        if (!ready)
        {
            if (--framesLeft <= 0)
            {
                plate = null;
                measurements = null;
                Show(Stage(ShareCheckStage.Failed, failure: ShareCheckFailure.FontsLoading));
            }

            return;
        }

        plate = null;
        measurements = null;
        var registration = seams.BeginOperation();
        if (registration is null)
        {
            Show(Stage(ShareCheckStage.Failed, failure: ShareCheckFailure.Unloading));
            return;
        }

        CancellationTokenSource cancellation;
        int mine;
        lock (gate)
        {
            // Cancelled by a new check, the window's disposal, or unloading, whichever comes first.
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(seams.Stopping);
            preparing = cancellation;
            mine = generation;
        }

        Show(Stage(ShareCheckStage.Preparing));
        _ = Task.Run(() => PrepareAsync(resolved, mine, cancellation, registration));
    }

    /// <summary>Drops the check under way, and shows nothing.</summary>
    internal void Reset()
    {
        Drop();
        Show(ShareCheckView.Idle);
    }

    public void Dispose() => Drop();

    private async Task PrepareAsync(ResolvedPlate resolved, int mine, CancellationTokenSource source, IDisposable registration)
    {
        var cancellation = source.Token;
        try
        {
            // A false result or any exception turns preparation off for the session (D5's N2-6 note, (3)).
            // Waiting for it observes the check's cancellation; the check itself runs on and keeps
            // its result for the session.
            bool passed;
            try
            {
                passed = await seams.SelfTest().WaitAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                seams.Log("Sharing: image preparation's check failed: " + PublishOutcome.Describe(e));
                passed = false;
            }

            ShareCheckView next;
            try
            {
                if (!passed)
                {
                    next = Stage(ShareCheckStage.Failed, failure: ShareCheckFailure.PreparationOff);
                }
                else
                {
                    var prepared = await seams.Prepare(resolved.Requirements, cancellation).ConfigureAwait(false);
                    var result = PlateSnapshotBuilder.Map(resolved, prepared);
                    next = result.Candidate is { } candidate
                        ? Stage(ShareCheckStage.Ready, candidate: candidate)
                        : Stage(ShareCheckStage.Refused, problems: result.Problems);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                seams.Log("Sharing: preparing a Plate's images failed: " + PublishOutcome.Describe(e));
                next = Stage(ShareCheckStage.Failed, failure: ShareCheckFailure.PreparationFailed);
            }

            lock (gate)
            {
                if (mine == generation && !cancellation.IsCancellationRequested)
                {
                    view = next;
                }
            }
        }
        finally
        {
            // The linked source is released with the preparation, so no check stays registered on
            // the plugin's Stopping token until unload. Drop tolerates one already disposed.
            lock (gate)
            {
                if (ReferenceEquals(preparing, source))
                {
                    preparing = null;
                }
            }

            source.Dispose();
            registration.Dispose();
        }
    }

    /// <summary>The current Plate's view at <paramref name="stage"/>.</summary>
    private ShareCheckView Stage(ShareCheckStage stage, SnapshotCandidate? candidate = null, IReadOnlyList<PlateSnapshotProblem>? problems = null, ShareCheckFailure failure = ShareCheckFailure.None)
    {
        var current = view;
        return new ShareCheckView(stage, current.PlateId, current.PlateName, candidate, problems ?? Array.Empty<PlateSnapshotProblem>(), failure, current.Source);
    }

    private void Show(ShareCheckView next)
    {
        lock (gate)
        {
            view = next;
        }
    }

    /// <summary>Drops the check under way: its preparation is cancelled, and its result never shown.</summary>
    private void Drop()
    {
        CancellationTokenSource? cancel;
        lock (gate)
        {
            generation++;
            cancel = preparing;
            preparing = null;
        }

        plate = null;
        measurements = null;
        try
        {
            cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// The renderer's measurements, with each image's size read once for the check: while fonts
    /// build, the Plate is resolved every frame, and its image files' headers aren't read again
    /// each time on the framework thread.
    /// </summary>
    private sealed class CachedSizes(IPlateMeasurements inner) : IPlateMeasurements
    {
        private readonly Dictionary<Guid, (bool Known, int Width, int Height)> sizes = new();

        public bool TryMeasureNaturalWidth(TextProfileElement element, out float width) => inner.TryMeasureNaturalWidth(element, out width);

        public bool TryGetDisplayOverride(ProfileDocument plate, TextProfileElement element, out string? display) =>
            inner.TryGetDisplayOverride(plate, element, out display);

        public bool TryGetImageSize(Guid image, out int width, out int height)
        {
            if (!sizes.TryGetValue(image, out var size))
            {
                var known = inner.TryGetImageSize(image, out var w, out var h);
                size = (known, w, h);
                sizes[image] = size;
            }

            (width, height) = (size.Width, size.Height);
            return size.Known;
        }
    }
}
