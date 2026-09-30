using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Plates;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>What the Sharing window shows of the live publisher, for one character: whether its Active Plate is being prepared, or why it couldn't be shared as it is.</summary>
internal sealed record LiveView(ulong ContentId, bool Building, IReadOnlyList<PlateSnapshotProblem> Problems, ShareCheckFailure Failure)
{
    internal static readonly LiveView Idle = new(0, false, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.None);
}

/// <summary>
/// Publishes the logged-in character's Active Plate live (NETWORK2's N2-9c; decision C3): when the
/// character shares, and its Active Plate is saved, becomes another Plate, or sharing starts or
/// resumes, the saved Plate is built into a candidate by a share check of its own (a private copy
/// of the saved JSON, resolved as the renderer draws it, its images prepared), and handed to
/// <see cref="CharacterSharing.TryPublish"/>, which shows a Plate never shared before first and
/// sends the rest. Nothing is published on arriving at a character, only on a change after it. At
/// login it asks for C1's re-read when the game shows another name or World than the binding's.
/// It runs on the framework thread, a frame at a time; saves may be reported from any thread.
/// Compiled only in the networking preview flavour.
/// </summary>
internal sealed class LivePublisher : IDisposable
{
    private readonly CharacterSharing sharing;
    private readonly ShareCheck check;
    private readonly Func<CharacterContext?> currentCharacter;
    private readonly Func<ulong, Guid?> activePlateOf;
    private readonly ConcurrentQueue<Guid> saved = new();
    private ulong watchedCharacter;
    private Guid? watchedActive;
    private SharingStage? watchedStage;
    private bool rereadDue;
    private (ulong ContentId, Guid PlateId)? building;
    private SnapshotCandidate? ready;
    private volatile LiveView view = LiveView.Idle;

    internal LivePublisher(CharacterSharing sharing, ShareCheck check, Func<CharacterContext?> currentCharacter, Func<ulong, Guid?> activePlateOf)
    {
        this.sharing = sharing ?? throw new ArgumentNullException(nameof(sharing));
        this.check = check ?? throw new ArgumentNullException(nameof(check));
        this.currentCharacter = currentCharacter ?? throw new ArgumentNullException(nameof(currentCharacter));
        this.activePlateOf = activePlateOf ?? throw new ArgumentNullException(nameof(activePlateOf));
    }

    /// <summary>Where publishing the Active Plate stands.</summary>
    internal LiveView View => view;

    /// <summary>A Plate was saved: any thread. It is published at the next frame when it is the sharing character's Active Plate.</summary>
    internal void PlateSaved(Guid plateId) => saved.Enqueue(plateId);

    /// <summary>One frame's work: the framework thread only.</summary>
    internal void OnFrame()
    {
        check.OnFrame();
        var sharingView = sharing.View;
        if (!sharingView.Loaded)
        {
            sharing.TryLoad();
            saved.Clear();
            return;
        }

        if (currentCharacter() is not { } character)
        {
            watchedCharacter = 0;
            Drop();
            saved.Clear();
            return;
        }

        var entry = sharingView.Find(character.ContentId);
        var active = activePlateOf(character.ContentId);
        if (character.ContentId != watchedCharacter)
        {
            // A login, or another character: nothing is published for arriving.
            watchedCharacter = character.ContentId;
            watchedActive = active;
            watchedStage = entry?.Stage;
            rereadDue = entry is { IsBound: true };
            Drop();
            saved.Clear();
            return;
        }

        if (rereadDue && character.Name is { } name && character.HomeWorld is { } world)
        {
            rereadDue = entry is { IsBound: true } && !CharacterSharing.SameCharacter(entry, name, world) && !sharing.TryReread(character.ContentId, name, world);
        }

        var shared = entry is { Stage: SharingStage.Shared };
        var changed = shared && watchedStage != SharingStage.Shared;
        watchedStage = entry?.Stage;
        if (active != watchedActive)
        {
            watchedActive = active;
            changed |= shared;
        }

        while (saved.TryDequeue(out var plate))
        {
            changed |= shared && plate == active;
        }

        if (!shared)
        {
            Drop();
            return;
        }

        if (changed && active is { } activePlate)
        {
            building = (character.ContentId, activePlate);
            ready = null;
            view = new LiveView(character.ContentId, true, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.None);
            check.Begin(activePlate);
        }

        if (building is not { } target)
        {
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
                case ShareCheckStage.Refused:
                    view = new LiveView(target.ContentId, false, built.Problems, ShareCheckFailure.None);
                    Finish();
                    return;
                case ShareCheckStage.Failed:
                    view = new LiveView(target.ContentId, false, Array.Empty<PlateSnapshotProblem>(), built.Failure);
                    Finish();
                    return;
                default:
                    return;
            }
        }

        // A busy service is asked again next frame; the candidate waits.
        if (ready is { } waiting && sharing.TryPublish(target.ContentId, waiting, approved: false))
        {
            view = LiveView.Idle;
            Finish();
        }
    }

    public void Dispose() => check.Dispose();

    /// <summary>Drops a candidate in the making: sharing stopped, or the character changed.</summary>
    private void Drop()
    {
        if (building is not null)
        {
            Finish();
        }

        view = LiveView.Idle;
    }

    private void Finish()
    {
        building = null;
        ready = null;
        check.Reset();
    }
}
