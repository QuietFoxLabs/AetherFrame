using System;
using System.Collections.Generic;
using AetherFrame.Services.Network.Publishing;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>What the sharing progress window shows.</summary>
internal enum SharingProgressStage
{
    /// <summary>Nothing: the window is closed.</summary>
    Hidden,

    /// <summary>The saved Active Plate is being read and checked as it is drawn.</summary>
    Preparing,

    /// <summary>Its images are being prepared.</summary>
    PreparingImages,

    /// <summary>It is ready, and waits for the sharing service, which is busy with something else.</summary>
    Waiting,

    /// <summary>The key and the server are checked, and the Plate is signed on this PC.</summary>
    Signing,

    /// <summary>The signed Plate is being sent to the sharing server.</summary>
    Sending,

    /// <summary>The server took it: a short confirmation that closes by itself.</summary>
    Shared,

    /// <summary>It didn't go through: the reason, until the player closes it or shares again.</summary>
    Problem,
}

/// <summary>What the player can do about a share that didn't go through, besides closing the window.</summary>
internal enum SharingProgressAction
{
    /// <summary>Nothing here helps: the Plate must change, or there is nothing left to do.</summary>
    None,

    /// <summary>A signed revision waits on this PC: send it again, as the Sharing window's Try sending again does.</summary>
    SendAgain,

    /// <summary>Nothing waits: build the Active Plate and share it again, as a save would.</summary>
    ShareAgain,

    /// <summary>Only the Sharing window can help: a key that can't be opened, an update, or a character shared elsewhere now.</summary>
    OpenSharing,
}

/// <summary>What the progress window draws this frame: one immutable value.</summary>
internal sealed record SharingProgressView(
    SharingProgressStage Stage,
    ulong ContentId,
    string Message,
    TimeSpan Elapsed,
    IReadOnlyList<PlateSnapshotProblem> Problems,
    SharingProgressAction Action)
{
    internal static readonly SharingProgressView Hidden = new(SharingProgressStage.Hidden, 0, string.Empty, TimeSpan.Zero, Array.Empty<PlateSnapshotProblem>(), SharingProgressAction.None);

    /// <summary>Whether the window is open.</summary>
    internal bool Visible => Stage != SharingProgressStage.Hidden;

    /// <summary>Whether sharing is still under way.</summary>
    internal bool Working => Stage is SharingProgressStage.Preparing or SharingProgressStage.PreparingImages or SharingProgressStage.Waiting or SharingProgressStage.Signing or SharingProgressStage.Sending;
}

/// <summary>
/// Follows the sharing of the logged-in character's Active Plate for the small progress window
/// (the owner's request of October 2, 2026), from what the Sharing window already reads: the live
/// publisher's <see cref="LiveView"/> while the Plate is prepared, then the sharing service's
/// <see cref="PublishStatus"/> while it is signed and sent, and the notice the publish ended with.
/// Each of those is replaced whole at every change, so a new one is told from the last by reference.
/// <para>
/// Every share carries its number (<see cref="LiveView.Share"/>, <see cref="PublishStatus.Share"/>),
/// taken when a build starts or a publish is handed over; a higher number started later, and the
/// window shows the newest share under way, and never an older share's progress or result in place
/// of a newer one's result. The publish of a build's candidate carries the build on
/// (<see cref="PublishStatus.Build"/>): its time so far, and whether the player hid it. A share
/// the player hid stays hidden, its result included, and the next share shows again.
/// </para>
/// It shows only while the character shares, and only what happens after it began following that
/// character: nothing from before a login or a switch of characters. Framework thread only; it
/// sends nothing itself.
/// </summary>
internal sealed class SharingProgress
{
    /// <summary>How long the confirmation that the Plate is shared stays up.</summary>
    internal static readonly TimeSpan SharedFor = TimeSpan.FromSeconds(5);

    private bool following;
    private ulong character;
    private LiveView? lastLive;
    private PublishStatus? lastPublish;
    private long floor;
    private long workingShare;
    private TimeSpan workingSince;
    private long hiddenShare;
    private (SharingProgressView View, long Share, TimeSpan At, Guid Plate)? result;

    /// <summary>
    /// The window's view for this frame, given the character logged in (null for none), the sharing
    /// service's view, the live publisher's view, a monotonic <paramref name="now"/>, and the
    /// character's Active Plate: a waiting revision is offered to be sent again only while its Plate
    /// is still that.
    /// </summary>
    internal SharingProgressView Update(ulong? loggedIn, CharacterSharingView sharing, LiveView live, TimeSpan now, Guid? activePlate = null)
    {
        ArgumentNullException.ThrowIfNull(sharing);
        ArgumentNullException.ThrowIfNull(live);
        var id = loggedIn ?? 0;
        if (!following || id != character)
        {
            // Arriving at a character: what happened before is not this window's to show, though
            // what is still under way for it is.
            following = true;
            character = id;
            lastLive = live;
            lastPublish = sharing.Publish;
            floor = 0;
            workingShare = 0;
            hiddenShare = 0;
            result = null;
        }

        if (id == 0)
        {
            return SharingProgressView.Hidden;
        }

        // What changed since the last frame: a publish under way or ended, with the notice it
        // left, or a build under way or one that couldn't be shared. A publish that ended with no
        // word had nothing to do (a newer build took its place, or sharing stopped).
        if (!ReferenceEquals(sharing.Publish, lastPublish))
        {
            lastPublish = sharing.Publish;
            if (lastPublish is { } publish && publish.ContentId == id)
            {
                // The publish of a build's candidate carries that build on, hidden or not, and
                // its time so far.
                if (publish.Build != 0 && publish.Build == hiddenShare)
                {
                    hiddenShare = publish.Share;
                }

                if (publish.Build != 0 && publish.Build == workingShare)
                {
                    workingShare = publish.Share;
                }

                if (publish is { Step: PublishStep.Ended, Outcome: { } outcome })
                {
                    Record(ResultOf(id, outcome), publish.Share, now, outcome.Plate);
                }
            }
        }

        if (!ReferenceEquals(live, lastLive))
        {
            lastLive = live;
            if (live.ContentId == id && live.Share != 0 && !live.Building && ResultOf(id, live) is { } built)
            {
                Record(built, live.Share, now, default);
            }
        }

        var (working, share) = Working(id, sharing, live);
        if (working != SharingProgressStage.Hidden)
        {
            if (share != workingShare)
            {
                // A share starts, or reaches this window for the first time.
                workingShare = share;
                workingSince = now;
            }

            if (result is { } older && older.Share < share)
            {
                // A newer share is under way: an older result has nothing more to say.
                result = null;
            }

            if (share == hiddenShare)
            {
                return SharingProgressView.Hidden;
            }

            // Only a send can be stopped from the Sharing window; anything else is waited out.
            var message = working == SharingProgressStage.Waiting && sharing.Publish is { Step: PublishStep.Sending } other && other.ContentId != id
                ? SharingText.WaitingForOther
                : MessageOf(working);
            return new SharingProgressView(working, id, message, now - workingSince, Array.Empty<PlateSnapshotProblem>(), SharingProgressAction.None);
        }

        workingShare = 0;
        if (result is not { } shown)
        {
            return SharingProgressView.Hidden;
        }

        if (shown.View.Stage == SharingProgressStage.Shared && now - shown.At >= SharedFor)
        {
            result = null;
            return SharingProgressView.Hidden;
        }

        // A waiting revision of a Plate that isn't the Active Plate any more is never sent again:
        // trying again then shares the Active Plate there is, if any.
        if (shown.View.Action == SharingProgressAction.SendAgain && shown.Plate != activePlate)
        {
            return shown.View with { Action = activePlate is null ? SharingProgressAction.None : SharingProgressAction.ShareAgain };
        }

        return shown.View;
    }

    /// <summary>The player closed the window: a result goes, and a share still under way stays out of sight until it ends, its result included.</summary>
    internal void Dismiss()
    {
        result = null;
        if (workingShare != 0)
        {
            hiddenShare = workingShare;
        }
    }

    /// <summary>
    /// A share's result, kept unless it is older than the last result (a later build's refusal
    /// outlasts an older send's outcome), or the player hid that share. Either way a result no
    /// older than the last sets how old a share may be and still be shown, so hiding a newer share
    /// never lets an older one show again.
    /// </summary>
    private void Record(SharingProgressView view, long share, TimeSpan now, Guid plate)
    {
        if (share < floor)
        {
            return;
        }

        floor = share;
        if (share != hiddenShare)
        {
            result = (view, share, now, plate);
        }
    }

    /// <summary>
    /// The newest share under way for the character, while it shares: a publish signing or
    /// sending, or a build being prepared or waiting for the service. A share older than the last
    /// result (an older revision still being sent after a newer build couldn't be shared) isn't
    /// shown.
    /// </summary>
    private (SharingProgressStage Stage, long Share) Working(ulong id, CharacterSharingView sharing, LiveView live)
    {
        if (sharing.Find(id) is not { Stage: SharingStage.Shared })
        {
            return (SharingProgressStage.Hidden, 0);
        }

        var publishing = sharing.Publish is { } publish && publish.ContentId == id && publish.Step != PublishStep.Ended && publish.Share >= floor ? publish : null;
        var building = live.ContentId == id && live.Building && live.Share >= floor;
        if (publishing is not null && (!building || publishing.Share >= live.Share))
        {
            return (publishing.Step == PublishStep.Sending ? SharingProgressStage.Sending : SharingProgressStage.Signing, publishing.Share);
        }

        if (building)
        {
            var stage = live.Waiting ? SharingProgressStage.Waiting : live.PreparingImages ? SharingProgressStage.PreparingImages : SharingProgressStage.Preparing;
            return (stage, live.Share);
        }

        return (SharingProgressStage.Hidden, 0);
    }

    private static string MessageOf(SharingProgressStage stage) => stage switch
    {
        SharingProgressStage.Preparing => SharingText.Building,
        SharingProgressStage.PreparingImages => SharingText.PreparingImages,
        SharingProgressStage.Waiting => SharingText.WaitingTurn,
        SharingProgressStage.Signing => SharingText.Signing,
        _ => SharingText.Sending,
    };

    /// <summary>A publish's end, in the notice's own words, with what the player can do about it.</summary>
    private static SharingProgressView ResultOf(ulong id, SharingNotice outcome)
    {
        var stage = outcome.Kind == SharingNoticeKind.Published ? SharingProgressStage.Shared : SharingProgressStage.Problem;
        var action = outcome.Kind switch
        {
            SharingNoticeKind.Published or SharingNoticeKind.PublishUnrecorded or SharingNoticeKind.PublishWithdrawn => SharingProgressAction.None,
            SharingNoticeKind.PublishWaiting or SharingNoticeKind.PublishStopped => SharingProgressAction.SendAgain,
            SharingNoticeKind.KeyUnavailable or SharingNoticeKind.UpdateNeeded or SharingNoticeKind.TakenOver or SharingNoticeKind.NoLongerBound => SharingProgressAction.OpenSharing,
            _ => SharingProgressAction.ShareAgain,
        };

        return new SharingProgressView(stage, id, SharingText.Notice(outcome), TimeSpan.Zero, Array.Empty<PlateSnapshotProblem>(), action);
    }

    /// <summary>A build that couldn't be shared: the Plate as it is, or the check that couldn't finish. Null for nothing to say (unloading).</summary>
    private static SharingProgressView? ResultOf(ulong id, LiveView live)
    {
        if (live.Problems.Count > 0)
        {
            return new SharingProgressView(SharingProgressStage.Problem, id, SharingText.CantShare, TimeSpan.Zero, live.Problems, SharingProgressAction.None);
        }

        return live.Failure switch
        {
            ShareCheckFailure.None or ShareCheckFailure.Unloading => null,
            ShareCheckFailure.PreparationOff => new SharingProgressView(SharingProgressStage.Problem, id, ShareMessages.For(live.Failure), TimeSpan.Zero, Array.Empty<PlateSnapshotProblem>(), SharingProgressAction.None),
            _ => new SharingProgressView(SharingProgressStage.Problem, id, ShareMessages.For(live.Failure), TimeSpan.Zero, Array.Empty<PlateSnapshotProblem>(), SharingProgressAction.ShareAgain),
        };
    }
}
