using System.Collections.Generic;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Network.Sharing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// When the sharing progress window comes to the front (the owner's request of October 4, 2026):
/// once when each new sharing operation begins, a save's share and a Try again included, never
/// again while that operation's progress changes, and never for an operation the player dismissed.
/// </summary>
public partial class CharacterSharingTests
{
    [Fact]
    public void ProgressFront_ComesForwardOnce_ForASavesShare_ThroughItsBuildPublishAndResult()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var shared = harness.Sharing.View;
        var progress = new SharingProgress();
        var front = new SharingProgressFront();
        var raises = new List<bool>();
        void Frame(CharacterSharingView sharing, LiveView live, int at) => raises.Add(front.ShouldRaise(progress.Update(Aria, sharing, live, At(at))));

        Frame(shared, LiveView.Idle, 0);
        Frame(shared, Build(1), 1);
        Frame(shared, Build(1) with { PreparingImages = true }, 2);
        Frame(shared, Build(1) with { PreparingImages = true, Waiting = true }, 3);

        // Handed over, the publish carries the build on under a number of its own: the same operation.
        var signing = shared.With(publish: new PublishStatus(Aria, 2, PublishStep.Signing, Build: 1));
        Frame(signing, LiveView.Idle, 4);
        var sending = signing.With(publish: signing.Publish! with { Step = PublishStep.Sending });
        Frame(sending, LiveView.Idle, 5);
        Frame(sending, LiveView.Idle, 30);
        var ended = sending.With(publish: sending.Publish! with { Step = PublishStep.Ended, Outcome = new SharingNotice(Aria, SharingNoticeKind.Published) });
        Frame(ended, LiveView.Idle, 31);
        Frame(ended, LiveView.Idle, 32);

        Assert.Equal([false, true, false, false, false, false, false, false, false], raises);
    }

    [Fact]
    public void ProgressFront_ComesForwardAgain_ForEachTryAgain_AndForTheNextSave()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var shared = harness.Sharing.View;
        var progress = new SharingProgress();
        var front = new SharingProgressFront();
        bool Frame(CharacterSharingView sharing, LiveView live, int at) => front.ShouldRaise(progress.Update(Aria, sharing, live, At(at)));

        Assert.False(Frame(shared, LiveView.Idle, 0));
        var failed = shared.With(publish: new PublishStatus(Aria, 1, PublishStep.Ended, new SharingNotice(Aria, SharingNoticeKind.PublishWaiting)));

        // A result for a share this window never saw under way is new, and comes forward once.
        Assert.True(Frame(failed, LiveView.Idle, 1));
        Assert.False(Frame(failed, LiveView.Idle, 2));

        // Try again sends the waiting revision as a share of its own.
        var again = failed.With(publish: new PublishStatus(Aria, 2, PublishStep.Signing));
        Assert.True(Frame(again, LiveView.Idle, 3));
        var againSending = again.With(publish: again.Publish! with { Step = PublishStep.Sending });
        Assert.False(Frame(againSending, LiveView.Idle, 4));
        var unreachable = againSending.With(publish: againSending.Publish! with { Step = PublishStep.Ended, Outcome = new SharingNotice(Aria, SharingNoticeKind.Unreachable) });
        Assert.False(Frame(unreachable, LiveView.Idle, 5));

        // Try again after that builds the Active Plate again, as a save does.
        Assert.True(Frame(unreachable, Build(3), 6));
        Assert.False(Frame(unreachable, Build(3) with { PreparingImages = true }, 7));
    }

    [Fact]
    public void ProgressFront_NeverComesForward_ForADismissedOperation_ButDoesForTheNext()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var progress = new SharingProgress();
        var front = new SharingProgressFront();
        var sending = harness.Sharing.View.With(publish: new PublishStatus(Aria, 1, PublishStep.Sending));
        Assert.True(front.ShouldRaise(progress.Update(Aria, sending, LiveView.Idle, At(0))));

        progress.Dismiss();
        var hidden = progress.Update(Aria, sending, LiveView.Idle, At(1));
        Assert.False(hidden.Visible);
        Assert.False(front.ShouldRaise(hidden));
        var ended = sending.With(publish: sending.Publish! with { Step = PublishStep.Ended, Outcome = new SharingNotice(Aria, SharingNoticeKind.PublishWaiting) });
        Assert.False(front.ShouldRaise(progress.Update(Aria, ended, LiveView.Idle, At(2))));

        Assert.True(front.ShouldRaise(progress.Update(Aria, ended, Build(2), At(3))));
    }

    [Fact]
    public void ProgressFront_AnOlderShareStillUnderWay_NeverBeginsAnOperationAgain()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var shared = harness.Sharing.View;
        var progress = new SharingProgress();
        var front = new SharingProgressFront();
        bool Frame(CharacterSharingView sharing, LiveView live, int at) => front.ShouldRaise(progress.Update(Aria, sharing, live, At(at)));

        // A resend (share 2) starts while a build (share 1) runs: the newer share is a new operation.
        Assert.True(Frame(shared, Build(1), 0));
        var resend = shared.With(publish: new PublishStatus(Aria, 2, PublishStep.Signing));
        Assert.True(Frame(resend, Build(1), 1));

        // The resend gives way, and the window goes back to the older build: no new operation.
        var gaveWay = resend.With(publish: resend.Publish! with { Step = PublishStep.Ended });
        Assert.False(Frame(gaveWay, Build(1) with { Waiting = true }, 2));
        var handed = gaveWay.With(publish: new PublishStatus(Aria, 3, PublishStep.Signing, Build: 1));
        Assert.False(Frame(handed, LiveView.Idle, 3));
    }

    [Fact]
    public void ProgressFront_StaysBack_WhileHidden_OrWithoutAnOperation()
    {
        var front = new SharingProgressFront();
        Assert.False(front.ShouldRaise(SharingProgressView.Hidden));
        Assert.False(front.ShouldRaise(SharingProgressView.Hidden with { Operation = 4 }));
        var working = SharingProgressView.Hidden with { Stage = SharingProgressStage.Sending, Operation = 4 };
        Assert.True(front.ShouldRaise(working));
        Assert.False(front.ShouldRaise(working));
        Assert.False(front.ShouldRaise(working with { Operation = 0 }));
    }
}
