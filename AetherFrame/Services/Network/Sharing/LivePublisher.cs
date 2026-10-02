using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AetherFrame.Protocol.Identity;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Plates;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>
/// What the sharing windows show of the live publisher, for one character: whether its Active Plate
/// is being prepared (its images too, once that has begun, and whether it is ready and waits for
/// the sharing service to be free), or why it couldn't be shared as it is. <see cref="Share"/> is
/// the build's number (<see cref="CharacterSharing.BuildGeneration"/>), which the publish of its
/// candidate carries too. Replaced whole at each change, so a window that keeps one can tell it
/// from the next.
/// </summary>
internal sealed record LiveView(ulong ContentId, bool Building, IReadOnlyList<PlateSnapshotProblem> Problems, ShareCheckFailure Failure, bool PreparingImages = false, bool Waiting = false, long Share = 0)
{
    internal static readonly LiveView Idle = new(0, false, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.None);
}

/// <summary>
/// Publishes the logged-in character's Active Plate live (NETWORK2's N2-9c; decision C3, as the
/// owner's direction of October 2, 2026 amends it): when the character shares, and its Active Plate
/// is saved, becomes another Plate, or sharing starts, resumes or moves to a new key, the saved
/// Plate is built into a candidate by a share check of its own (a private copy of the saved JSON,
/// resolved as the renderer draws it, its images prepared), and handed to
/// <see cref="CharacterSharing.TryPublish"/>, which signs and sends it with no screen before it.
/// Nothing is published on arriving at a character, only on a change after it (or the player's
/// <see cref="Retry"/>), and only once the Library and the sharing file are both read, so a value
/// becoming known is never taken for a change. A candidate that goes out of date (another build,
/// another character, sharing stopping) before its send begins is never sent (see
/// <see cref="CharacterSharing.TryPublish"/>), and a send of an older revision for the character
/// gives way once a newer candidate is ready and waits for the service
/// (<see cref="CharacterSharing.StopOlderSend"/>). At login it asks for C1's re-read when the
/// game shows another name or World than the binding's. It runs on the framework thread, a frame at
/// a time; saves may be reported from any thread. Compiled only in the networking preview flavour.
/// </summary>
internal sealed class LivePublisher : IDisposable
{
    private readonly CharacterSharing sharing;
    private readonly ShareCheck check;
    private readonly Func<CharacterContext?> currentCharacter;
    private readonly Func<ulong, Guid?> activePlateOf;
    private readonly Func<bool> libraryLoaded;
    private readonly ConcurrentQueue<Guid> saved = new();
    private ulong watchedCharacter;
    private Guid? watchedActive;
    private bool watchedShared;
    private PersonaId? watchedKey;
    private ProfileId? watchedBinding;
    private bool rereadDue;
    private (ulong ContentId, Guid PlateId, long Generation)? building;
    private SnapshotCandidate? ready;
    private volatile LiveView view = LiveView.Idle;
    private volatile bool retry;

    internal LivePublisher(CharacterSharing sharing, ShareCheck check, Func<CharacterContext?> currentCharacter, Func<ulong, Guid?> activePlateOf, Func<bool> libraryLoaded)
    {
        this.sharing = sharing ?? throw new ArgumentNullException(nameof(sharing));
        this.check = check ?? throw new ArgumentNullException(nameof(check));
        this.currentCharacter = currentCharacter ?? throw new ArgumentNullException(nameof(currentCharacter));
        this.activePlateOf = activePlateOf ?? throw new ArgumentNullException(nameof(activePlateOf));
        this.libraryLoaded = libraryLoaded ?? throw new ArgumentNullException(nameof(libraryLoaded));
    }

    /// <summary>Where publishing the Active Plate stands.</summary>
    internal LiveView View => view;

    /// <summary>A Plate was saved: any thread. It is published at the next frame when it is the sharing character's Active Plate.</summary>
    internal void PlateSaved(Guid plateId) => saved.Enqueue(plateId);

    /// <summary>
    /// The player asked to try again after a share that didn't go through: the sharing character's
    /// Active Plate is built and shared again at the next frame, as a save would. Any thread.
    /// </summary>
    internal void Retry() => retry = true;

    /// <summary>
    /// Whether the Sharing window offers to share the character's Active Plate now: the character
    /// shares, it has an Active Plate the server doesn't show for it, and nothing is being built or
    /// sent for it. That is the case after arriving with such a Plate, since nothing is published
    /// for arriving, and after a share that didn't go through.
    /// </summary>
    internal static bool OffersShareNow(CharacterSharingView sharing, LiveView live, SharingCharacter entry, Guid? activePlate) =>
        entry is { Stage: SharingStage.Shared, ReplacingKey: false }
        && activePlate is { } plate && plate != entry.PublishedPlate
        && !(live.ContentId == entry.ContentId && live.Building)
        && !(sharing.Publish is { Step: not PublishStep.Ended } underWay && underWay.ContentId == entry.ContentId);

    /// <summary>
    /// Whether the Sharing window offers to send the character's waiting revision again: the server
    /// couldn't take it, its Plate is still the Active Plate, and no newer build of the Active Plate
    /// is under way, which would replace it.
    /// </summary>
    internal static bool OffersSendAgain(CharacterSharingView sharing, LiveView live, ulong contentId, Guid? activePlate) =>
        sharing.Notice is { Kind: SharingNoticeKind.PublishWaiting } waiting && waiting.ContentId == contentId
        && activePlate is { } plate && waiting.Plate == plate
        && !(live.ContentId == contentId && live.Building);

    /// <summary>One frame's work: the framework thread only.</summary>
    internal void OnFrame()
    {
        check.OnFrame();
        var sharingView = sharing.View;
        if (!sharingView.Loaded)
        {
            sharing.TryLoad();
        }

        // Until the Library and the sharing file are both read, nothing is known: arriving waits,
        // so a value becoming known is never taken for a change.
        if (!sharingView.Loaded || sharingView.Unreadable || !libraryLoaded())
        {
            Leave();
            return;
        }

        // Whoever is logged in, and when nobody is: a send stops once its Plate is no longer its
        // character's Active Plate. A logout or a switch of characters alone never stops one.
        sharing.StopStaleSends();
        if (currentCharacter() is not { } character)
        {
            Leave();
            return;
        }

        var entry = sharingView.Find(character.ContentId);
        var active = activePlateOf(character.ContentId);
        var shared = entry is { Stage: SharingStage.Shared, ReplacingKey: false };
        if (character.ContentId != watchedCharacter)
        {
            // A login, or another character: nothing is published for arriving.
            Leave();
            watchedCharacter = character.ContentId;
            watchedActive = active;
            watchedShared = shared;
            watchedKey = entry?.Key;
            watchedBinding = entry?.ProfileId;
            rereadDue = entry is { IsBound: true };
            return;
        }

        if (rereadDue && character.Name is { } name && character.HomeWorld is { } world)
        {
            rereadDue = entry is { IsBound: true } && !CharacterSharing.SameCharacter(entry, name, world) && !sharing.TryReread(character.ContentId, name, world);
        }

        // Sharing starting, resuming, or moving to a new key or binding is a change; so is a new
        // Active Plate, and a save of the Active Plate.
        var changed = shared && (!watchedShared || !Equals(entry!.Key, watchedKey) || entry.ProfileId != watchedBinding);
        watchedShared = shared;
        watchedKey = entry?.Key;
        watchedBinding = entry?.ProfileId;
        if (active != watchedActive)
        {
            watchedActive = active;
            changed |= shared;
        }

        while (saved.TryDequeue(out var plate))
        {
            changed |= shared && plate == active;
        }

        if (retry)
        {
            retry = false;
            changed |= shared;
        }

        if (!shared || active is null)
        {
            Drop(character.ContentId);
            return;
        }

        if (changed)
        {
            // A candidate built or handed over before this one is out of date, and never sent.
            sharing.Supersede(character.ContentId);
            building = (character.ContentId, active.Value, sharing.BuildGeneration(character.ContentId));
            ready = null;
            view = new LiveView(character.ContentId, true, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.None, Share: building.Value.Generation);
            check.Begin(active.Value);
        }

        if (building is not { } target)
        {
            return;
        }

        if (target.PlateId != active)
        {
            Drop(character.ContentId);
            return;
        }

        if (ready is null)
        {
            var built = check.View;
            if (built.PlateId != target.PlateId)
            {
                return;
            }

            switch (built.Stage)
            {
                case ShareCheckStage.Ready when built.Candidate is { } candidate:
                    ready = candidate;
                    break;
                case ShareCheckStage.Preparing when !view.PreparingImages:
                    view = view with { PreparingImages = true };
                    return;
                case ShareCheckStage.Refused:
                    view = new LiveView(target.ContentId, false, built.Problems, ShareCheckFailure.None, Share: target.Generation);
                    Finish();
                    return;
                case ShareCheckStage.Failed:
                    view = new LiveView(target.ContentId, false, Array.Empty<PlateSnapshotProblem>(), built.Failure, Share: target.Generation);
                    Finish();
                    return;
                default:
                    return;
            }
        }

        // A busy service is asked again next frame; the candidate waits. A send of an older revision
        // for the character gives way to it, so it goes next; another character's send goes on.
        if (ready is not { } waiting)
        {
            return;
        }

        if (sharing.TryPublish(target.ContentId, waiting, target.Generation))
        {
            view = LiveView.Idle;
            Finish();
            return;
        }

        sharing.StopOlderSend(target.ContentId);
        if (!view.Waiting)
        {
            view = view with { Waiting = true };
        }
    }

    public void Dispose() => check.Dispose();

    /// <summary>No character is watched: what was being built for the last one goes, and so does a try again asked for it.</summary>
    private void Leave()
    {
        if (watchedCharacter != 0)
        {
            Drop(watchedCharacter);
        }

        watchedCharacter = 0;
        retry = false;
        saved.Clear();
    }

    /// <summary>Drops a candidate in the making for the character, and one handed over but not yet sent: sharing stopped, the Active Plate is gone, or the character changed.</summary>
    private void Drop(ulong contentId)
    {
        if (building is not null)
        {
            Finish();
        }

        sharing.Supersede(contentId);
        view = LiveView.Idle;
    }

    private void Finish()
    {
        building = null;
        ready = null;
        check.Reset();
    }
}
