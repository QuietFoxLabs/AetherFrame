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
/// notices' own words, what the player can do about it, and nothing at all for a character that
/// doesn't share.
/// </summary>
public partial class CharacterSharingTests
{
    private static readonly LiveView Building = new(Aria, true, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.None);

    [Fact]
    public void Progress_FollowsEachStep_ThenSaysItIsShared_AndClosesByItself()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var progress = new SharingProgress();
        var shared = harness.Sharing.View;

        Assert.False(progress.Update(Aria, shared, LiveView.Idle, At(0)).Visible);
        Assert.Equal(SharingProgressStage.Preparing, progress.Update(Aria, shared, Building, At(1)).Stage);
        var images = Building with { PreparingImages = true };
        Assert.Equal((SharingProgressStage.PreparingImages, SharingText.PreparingImages), Stage(progress.Update(Aria, shared, images, At(2))));

        // Handed over: the live publisher is idle, the service signs, then sends.
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

        var number = 0;
        foreach (var (kind, action) in cases)
        {
            var progress = new SharingProgress();
            progress.Update(Aria, shared, LiveView.Idle, At(0));
            var notice = new SharingNotice(Aria, kind);
            var ended = shared.With(publish: new PublishStatus(Aria, ++number, PublishStep.Ended, notice));
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
        progress.Update(Aria, shared, Building, At(0));
        var refused = progress.Update(Aria, shared, new LiveView(Aria, false, problems, ShareCheckFailure.None), At(1));
        Assert.Equal((SharingProgressStage.Problem, SharingText.CantShare, SharingProgressAction.None), (refused.Stage, refused.Message, refused.Action));
        Assert.Same(problems, refused.Problems);

        foreach (var (failure, action) in new[] { (ShareCheckFailure.FontsLoading, SharingProgressAction.ShareAgain), (ShareCheckFailure.PreparationOff, SharingProgressAction.None) })
        {
            progress = new SharingProgress();
            progress.Update(Aria, shared, Building, At(0));
            var failed = progress.Update(Aria, shared, new LiveView(Aria, false, Array.Empty<PlateSnapshotProblem>(), failure), At(1));
            Assert.Equal((SharingProgressStage.Problem, ShareMessages.For(failure), action), (failed.Stage, failed.Message, failed.Action));
        }

        // Unloading has nothing to say.
        progress = new SharingProgress();
        progress.Update(Aria, shared, Building, At(0));
        Assert.False(progress.Update(Aria, shared, new LiveView(Aria, false, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.Unloading), At(1)).Visible);
    }

    [Fact]
    public void Progress_ShowsNothing_ForACharacterThatDoesntShare_OrForAnotherCharacter()
    {
        using var harness = new SharingHarness();
        var progress = new SharingProgress();

        // Not shared: a publish handed over for it, or a build, shows nothing.
        var notShared = harness.Sharing.View.With(publish: new PublishStatus(Aria, 1, PublishStep.Signing));
        Assert.False(progress.Update(Aria, notShared, Building, At(0)).Visible);
        Assert.False(progress.Update(Aria, notShared.With(publish: notShared.Publish! with { Step = PublishStep.Ended }), LiveView.Idle, At(1)).Visible);

        // Shared, but the work is another character's, or nobody is logged in.
        harness.Bound();
        var bram = harness.Sharing.View.With(publish: new PublishStatus(Bram, 2, PublishStep.Sending));
        Assert.False(progress.Update(Aria, bram, Building with { ContentId = Bram }, At(2)).Visible);
        Assert.False(progress.Update(null, harness.Sharing.View, Building, At(3)).Visible);
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
        Assert.Equal(SharingProgressStage.Preparing, progress.Update(Aria, refused, Building, At(2)).Stage);
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
    public void Progress_HiddenWhileWorking_StaysHiddenUntilThatShareEnds()
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

        // The next share shows again.
        Assert.Equal(SharingProgressStage.Preparing, progress.Update(Aria, ended, Building, At(3)).Stage);
    }

    [Fact]
    public void ThePublishStatus_NumbersEachPublish_SaysWhenItSends_AndEndsWithItsNotice()
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
        harness.Publish(PublicationCandidates.Simple());
        Assert.Equal(PublishStep.Sending, during);
        Assert.Equal(new PublishStatus(Aria, 1, PublishStep.Ended, new SharingNotice(Aria, SharingNoticeKind.PublishWaiting)), harness.Sharing.View.Publish);
        Assert.Contains("Sharing: sending the Active Plate came to TryLater (answer: 503).", harness.Log);

        harness.Server.PublishAnswer = () => (HttpStatusCode.NoContent, null);
        harness.Sharing.TrySendWaiting(Aria);
        Assert.Equal(new PublishStatus(Aria, 2, PublishStep.Ended, new SharingNotice(Aria, SharingNoticeKind.Published)), harness.Sharing.View.Publish);

        // Other operations leave it as it is.
        harness.Sharing.TryPause(Aria);
        Assert.Equal(2, harness.Sharing.View.Publish!.Number);
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

    private static TimeSpan At(int seconds) => TimeSpan.FromSeconds(seconds);

    private static (SharingProgressStage Stage, string Message) Stage(SharingProgressView view) => (view.Stage, view.Message);
}
