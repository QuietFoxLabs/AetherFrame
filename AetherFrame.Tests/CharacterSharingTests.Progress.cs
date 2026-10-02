using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Network.Sharing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The small sharing progress window's model (the owner's request of October 2, 2026): the steps
/// of each share as the sharing service and the live publisher report them, the result in the
/// notices' own words, what the player can do about it, only ever the newest share, and nothing at
/// all for a character that doesn't share.
/// </summary>
public partial class CharacterSharingTests
{
    [Fact]
    public void Progress_FollowsEachStep_ThenSaysItIsShared_AndClosesByItself()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var progress = new SharingProgress();
        var shared = harness.Sharing.View;

        Assert.False(progress.Update(Aria, shared, LiveView.Idle, At(0)).Visible);
        Assert.Equal(SharingProgressStage.Preparing, progress.Update(Aria, shared, Build(1), At(1)).Stage);
        Assert.Equal((SharingProgressStage.PreparingImages, SharingText.PreparingImages), Stage(progress.Update(Aria, shared, Build(1) with { PreparingImages = true }, At(2))));

        // Handed over: the live publisher is idle, and the service signs the build's candidate, then sends it.
        var signing = shared.With(publish: new PublishStatus(Aria, 1, PublishStep.Signing));
        Assert.Equal((SharingProgressStage.Signing, SharingText.Signing), Stage(progress.Update(Aria, signing, LiveView.Idle, At(3))));
        var sending = signing.With(publish: signing.Publish! with { Step = PublishStep.Sending });
        var working = progress.Update(Aria, sending, LiveView.Idle, At(40));
        Assert.Equal((SharingProgressStage.Sending, SharingText.Sending), Stage(working));
        Assert.Equal(TimeSpan.FromSeconds(39), working.Elapsed);
        Assert.Equal("Still working, 0:39 so far.", SharingText.Elapsed(working.Elapsed));

        var published = new SharingNotice(Aria, SharingNoticeKind.Published);
        var ended = sending.With(publish: sending.Publish! with { Step = PublishStep.Ended, Outcome = published });
        var done = progress.Update(Aria, ended, LiveView.Idle, At(41));
        Assert.Equal((SharingProgressStage.Shared, SharingText.Notice(published), SharingProgressAction.None), (done.Stage, done.Message, done.Action));
        Assert.True(progress.Update(Aria, ended, LiveView.Idle, At(45)).Visible);
        Assert.False(progress.Update(Aria, ended, LiveView.Idle, At(46)).Visible);
        Assert.False(progress.Update(Aria, ended, LiveView.Idle, At(60)).Visible);
    }

    [Fact]
    public void Progress_ShowsWhyAShareDidntGoThrough_UntilClosed_WithWhatTryAgainDoes()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var shared = harness.Sharing.View;
        var cases = new (SharingNoticeKind Kind, SharingProgressAction Action)[]
        {
            (SharingNoticeKind.PublishWaiting, SharingProgressAction.SendAgain),
            (SharingNoticeKind.PublishStopped, SharingProgressAction.SendAgain),
            (SharingNoticeKind.Unreachable, SharingProgressAction.ShareAgain),
            (SharingNoticeKind.PublishNotStored, SharingProgressAction.ShareAgain),
            (SharingNoticeKind.Failed, SharingProgressAction.ShareAgain),
            (SharingNoticeKind.KeyUnavailable, SharingProgressAction.OpenSharing),
            (SharingNoticeKind.UpdateNeeded, SharingProgressAction.OpenSharing),
            (SharingNoticeKind.TakenOver, SharingProgressAction.OpenSharing),
            (SharingNoticeKind.PublishUnrecorded, SharingProgressAction.None),
        };

        var share = 0;
        foreach (var (kind, action) in cases)
        {
            var progress = new SharingProgress();
            progress.Update(Aria, shared, LiveView.Idle, At(0));
            var notice = new SharingNotice(Aria, kind);
            var ended = shared.With(publish: new PublishStatus(Aria, ++share, PublishStep.Ended, notice));
            var shown = progress.Update(Aria, ended, LiveView.Idle, At(1));
            Assert.Equal((SharingProgressStage.Problem, SharingText.Notice(notice), action), (shown.Stage, shown.Message, shown.Action));

            // It stays until the player closes it.
            Assert.Equal(shown, progress.Update(Aria, ended, LiveView.Idle, At(600)));
            progress.Dismiss();
            Assert.False(progress.Update(Aria, ended, LiveView.Idle, At(601)).Visible);
        }
    }

    [Fact]
    public void Progress_ShowsAPlateThatCantBeShared_AndACheckThatCouldntFinish()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var shared = harness.Sharing.View;
        var problems = new List<PlateSnapshotProblem> { new(PlateSnapshotRefusal.ValueOutOfRange, null) };

        var progress = new SharingProgress();
        progress.Update(Aria, shared, Build(1), At(0));
        var refused = progress.Update(Aria, shared, new LiveView(Aria, false, problems, ShareCheckFailure.None, Share: 1), At(1));
        Assert.Equal((SharingProgressStage.Problem, SharingText.CantShare, SharingProgressAction.None), (refused.Stage, refused.Message, refused.Action));
        Assert.Same(problems, refused.Problems);

        foreach (var (failure, action) in new[] { (ShareCheckFailure.FontsLoading, SharingProgressAction.ShareAgain), (ShareCheckFailure.PreparationOff, SharingProgressAction.None) })
        {
            progress = new SharingProgress();
            progress.Update(Aria, shared, Build(1), At(0));
            var failed = progress.Update(Aria, shared, new LiveView(Aria, false, Array.Empty<PlateSnapshotProblem>(), failure, Share: 1), At(1));
            Assert.Equal((SharingProgressStage.Problem, ShareMessages.For(failure), action), (failed.Stage, failed.Message, failed.Action));
        }

        // Unloading has nothing to say.
        progress = new SharingProgress();
        progress.Update(Aria, shared, Build(1), At(0));
        Assert.False(progress.Update(Aria, shared, new LiveView(Aria, false, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.Unloading, Share: 1), At(1)).Visible);
    }

    [Fact]
    public void Progress_ShowsNothing_ForACharacterThatDoesntShare_OrForAnotherCharacter()
    {
        using var harness = new SharingHarness();
        var progress = new SharingProgress();

        // Not shared: a publish handed over for it, or a build, shows nothing.
        var notShared = harness.Sharing.View.With(publish: new PublishStatus(Aria, 1, PublishStep.Signing));
        Assert.False(progress.Update(Aria, notShared, Build(1), At(0)).Visible);
        Assert.False(progress.Update(Aria, notShared.With(publish: notShared.Publish! with { Step = PublishStep.Ended }), LiveView.Idle, At(1)).Visible);

        // Shared, but the work is another character's, or nobody is logged in.
        harness.Bound();
        var bram = harness.Sharing.View.With(publish: new PublishStatus(Bram, 2, PublishStep.Sending));
        Assert.False(progress.Update(Aria, bram, Build(3) with { ContentId = Bram }, At(2)).Visible);
        Assert.False(progress.Update(null, harness.Sharing.View, Build(4), At(3)).Visible);
    }

    [Fact]
    public void Progress_ShowsNothingFromBeforeArriving_AndANewShareReplacesTheLastResult()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var waiting = harness.Sharing.View.With(publish: new PublishStatus(Aria, 1, PublishStep.Ended, new SharingNotice(Aria, SharingNoticeKind.PublishWaiting)));

        // A result from before the window followed this character is never shown.
        var progress = new SharingProgress();
        Assert.False(progress.Update(Aria, waiting, LiveView.Idle, At(0)).Visible);

        // Another share's result shows; the next share takes its place.
        var refused = waiting.With(publish: new PublishStatus(Aria, 2, PublishStep.Ended, new SharingNotice(Aria, SharingNoticeKind.PublishRefused, "clock-ahead")));
        Assert.Equal(SharingProgressStage.Problem, progress.Update(Aria, refused, LiveView.Idle, At(1)).Stage);
        Assert.Equal(SharingProgressStage.Preparing, progress.Update(Aria, refused, Build(3), At(2)).Stage);
        var signing = refused.With(publish: new PublishStatus(Aria, 3, PublishStep.Signing));
        Assert.Equal(SharingProgressStage.Signing, progress.Update(Aria, signing, LiveView.Idle, At(3)).Stage);

        // A publish that ended with nothing to say (a newer build took its place) leaves nothing.
        var silent = signing.With(publish: signing.Publish! with { Step = PublishStep.Ended });
        Assert.False(progress.Update(Aria, silent, LiveView.Idle, At(4)).Visible);

        // Another character: nothing of this one's is shown.
        var later = silent.With(publish: new PublishStatus(Aria, 4, PublishStep.Ended, new SharingNotice(Aria, SharingNoticeKind.Published)));
        progress.Update(Bram, later, LiveView.Idle, At(5));
        Assert.False(progress.Update(Aria, later, LiveView.Idle, At(6)).Visible);
    }

    [Fact]
    public void Progress_ALaterBuildsProblem_IsNeverReplacedByAnOlderSharesOutcome()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var progress = new SharingProgress();
        var sendingOlder = harness.Sharing.View.With(publish: new PublishStatus(Aria, 1, PublishStep.Sending));
        Assert.Equal(SharingProgressStage.Sending, progress.Update(Aria, sendingOlder, LiveView.Idle, At(0)).Stage);

        // A newer build starts while the older one is sent: the newer is what the window follows.
        Assert.Equal(SharingProgressStage.Preparing, progress.Update(Aria, sendingOlder, Build(2), At(1)).Stage);
        var problems = new List<PlateSnapshotProblem> { new(PlateSnapshotRefusal.ValueOutOfRange, null) };
        var refused = new LiveView(Aria, false, problems, ShareCheckFailure.None, Share: 2);
        Assert.Equal((SharingProgressStage.Problem, SharingText.CantShare), Stage(progress.Update(Aria, sendingOlder, refused, At(2))));

        // The older send then ends as shared: the window still says the newer Plate can't be shared.
        var olderShared = sendingOlder.With(publish: sendingOlder.Publish! with { Step = PublishStep.Ended, Outcome = new SharingNotice(Aria, SharingNoticeKind.Published) });
        Assert.Equal((SharingProgressStage.Problem, SharingText.CantShare), Stage(progress.Update(Aria, olderShared, refused, At(3))));

        // Closed: the older share's progress and result stay out of sight too.
        progress.Dismiss();
        Assert.False(progress.Update(Aria, olderShared, refused, At(4)).Visible);
        var progress2 = new SharingProgress();
        progress2.Update(Aria, sendingOlder, Build(2), At(0));
        progress2.Update(Aria, sendingOlder, refused, At(1));
        progress2.Dismiss();
        Assert.False(progress2.Update(Aria, sendingOlder, refused, At(2)).Visible);
        Assert.False(progress2.Update(Aria, olderShared, refused, At(3)).Visible);
    }

    [Fact]
    public void Progress_HidingOneShare_HidesItAndItsResult_ButNotTheNext()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var progress = new SharingProgress();
        var sending = harness.Sharing.View.With(publish: new PublishStatus(Aria, 1, PublishStep.Sending));
        Assert.True(progress.Update(Aria, sending, LiveView.Idle, At(0)).Visible);

        progress.Dismiss();
        Assert.False(progress.Update(Aria, sending, LiveView.Idle, At(1)).Visible);
        var ended = sending.With(publish: sending.Publish! with { Step = PublishStep.Ended, Outcome = new SharingNotice(Aria, SharingNoticeKind.PublishWaiting) });
        Assert.False(progress.Update(Aria, ended, LiveView.Idle, At(2)).Visible);

        // A waiting revision sent again is a share of its own, and shows, its failure included.
        var again = ended.With(publish: new PublishStatus(Aria, 2, PublishStep.Signing));
        Assert.Equal(SharingProgressStage.Signing, progress.Update(Aria, again, LiveView.Idle, At(3)).Stage);
        progress.Dismiss();
        Assert.False(progress.Update(Aria, again, LiveView.Idle, At(4)).Visible);

        // So does the next build, while the hidden share is still under way.
        Assert.Equal(SharingProgressStage.Preparing, progress.Update(Aria, again, Build(3), At(5)).Stage);
        var failed = again.With(publish: new PublishStatus(Aria, 3, PublishStep.Ended, new SharingNotice(Aria, SharingNoticeKind.Unreachable)));
        Assert.Equal(SharingProgressStage.Problem, progress.Update(Aria, failed, LiveView.Idle, At(6)).Stage);
    }

    [Fact]
    public void Progress_SaysWhenTheShareWaitsForAnotherCharactersSend()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var progress = new SharingProgress();
        var waiting = Build(2) with { PreparingImages = true, Waiting = true };

        var other = harness.Sharing.View.With(publish: new PublishStatus(Bram, 1, PublishStep.Sending));
        Assert.Equal((SharingProgressStage.Waiting, SharingText.WaitingForOther), Stage(progress.Update(Aria, other, waiting, At(0))));

        // Waiting for anything else (a lookup, say) is waiting for its turn.
        var busy = harness.Sharing.View.With(busy: true);
        Assert.Equal((SharingProgressStage.Waiting, SharingText.WaitingTurn), Stage(progress.Update(Aria, busy, waiting, At(1))));
    }

    [Fact]
    public void ThePublishStatus_CarriesItsShare_SaysWhenItSends_AndEndsWithItsNotice()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        Assert.Null(harness.Sharing.View.Publish);

        // While the publish is on its way, the service says it is sending.
        PublishStep? during = null;
        harness.Server.PublishAnswer = () =>
        {
            during = harness.Sharing.View.Publish!.Step;
            return (HttpStatusCode.ServiceUnavailable, null);
        };
        harness.Sharing.Supersede(Aria);
        var build = harness.Sharing.BuildGeneration(Aria);
        var candidate = PublicationCandidates.Simple();
        harness.Sharing.TryPublish(Aria, candidate, candidate.PlateId, build);
        Assert.Equal(PublishStep.Sending, during);
        Assert.Equal(new PublishStatus(Aria, build, PublishStep.Ended, new SharingNotice(Aria, SharingNoticeKind.PublishWaiting)), harness.Sharing.View.Publish);
        Assert.Contains("Sharing: sending the Active Plate came to TryLater (answer: 503).", harness.Log);

        // A waiting revision sent again is a later share of its own.
        harness.Server.PublishAnswer = () => (HttpStatusCode.NoContent, null);
        harness.Sharing.TrySendWaiting(Aria);
        var again = harness.Sharing.View.Publish!;
        Assert.True(again.Share > build);
        Assert.Equal((PublishStep.Ended, SharingNoticeKind.Published), (again.Step, again.Outcome!.Kind));

        // Other operations leave it as it is.
        harness.Sharing.TryPause(Aria);
        Assert.Same(again, harness.Sharing.View.Publish);
    }

    [Fact]
    public async Task Progress_FollowsALiveShare_FromTheSaveToItsResult()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        using var live = new LiveHarness(harness);
        var plate = live.Save();
        live.Active = plate.ProfileId;
        live.Frames(2);
        var progress = new SharingProgress();
        var seen = new List<SharingProgressStage>();
        var clock = 0;
        SharingProgressView Frame()
        {
            live.Publisher.OnFrame();
            var view = progress.Update(Aria, harness.Sharing.View, live.Publisher.View, At(clock++));
            if (seen.Count == 0 || seen[^1] != view.Stage)
            {
                seen.Add(view.Stage);
            }

            return view;
        }

        Frame();
        live.Publisher.PlateSaved(plate.ProfileId);
        var deadline = DateTime.UtcNow + Patience;
        while (Frame().Stage != SharingProgressStage.Shared)
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5);
        }

        // The service runs each publish to its end before the next frame here, so its own steps
        // are covered by the publish status's test.
        Assert.Equal(SharingProgressStage.Hidden, seen[0]);
        Assert.Contains(SharingProgressStage.Preparing, seen);
        Assert.Equal(SharingProgressStage.Shared, seen[^1]);
        Assert.Single(harness.Server.Publishes);
    }

    /// <summary>The live publisher building the character's Active Plate as share <paramref name="share"/>.</summary>
    private static LiveView Build(long share) => new(Aria, true, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.None, Share: share);

    private static TimeSpan At(int seconds) => TimeSpan.FromSeconds(seconds);

    private static (SharingProgressStage Stage, string Message) Stage(SharingProgressView view) => (view.Stage, view.Message);
}
