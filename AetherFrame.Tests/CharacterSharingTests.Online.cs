using System;
using System.IO;
using AetherFrame.Services.Network.Sharing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The one-time notice that gates the online count ("The online count" in the decision register):
/// nothing of the count is sent until the player has seen what it sends, and only the consent they
/// agreed to, never the check that follows it, counts as having seen it. GPT asked for these two in
/// its review of <c>a6b8012</c>, October 5, 2026.
/// </summary>
public partial class CharacterSharingTests
{
    [Fact]
    public void TheOnlineNotice_IsDue_ForACheckStartedBeforeThisBuild_AndItsPassIsNotConsent()
    {
        using var harness = new SharingHarness();

        // The player agreed to the old build's consent, which said nothing about the count, and the
        // check was still under way when this build arrived (no marker, a character being checked).
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.WaitIdle();
        Assert.Equal(SharingStage.Checking, harness.Sharing.View.Find(Aria)!.Stage);
        File.Delete(Path.Combine(harness.Root, CharacterSharing.OnlineNoticeFileName));
        harness.Restart();
        Assert.True(harness.Sharing.View.OnlineNotice);

        // The check passes. That is not the consent this build shows, so the notice stays and
        // nothing of the count is sent for the character.
        Assert.True(harness.Sharing.TryNewCode(Aria));
        harness.WaitIdle();
        Assert.True(harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh"));
        harness.WaitIdle();
        Assert.Equal(SharingStage.Shared, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.True(harness.Sharing.View.OnlineNotice);
        Assert.Null(OnlineCount.TargetOf(harness.Sharing.View, Aria));

        // Got it, and only then.
        Assert.True(harness.Sharing.TryDismissOnlineNotice());
        Assert.False(harness.Sharing.View.OnlineNotice);
        Assert.NotNull(OnlineCount.TargetOf(harness.Sharing.View, Aria));
        harness.Restart();
        Assert.False(harness.Sharing.View.OnlineNotice);
    }

    [Fact]
    public void TheOnlineNotice_SurvivesAKeyReplacementsCheck_UntilItIsAcknowledged()
    {
        using var harness = new SharingHarness();
        harness.Bound();

        // A key replacement under way when this build arrived: the character is still bound, and the
        // consent for the new key was the old build's, which never mentioned the count.
        Assert.True(harness.Sharing.TryStart(Aria, newKey: true));
        harness.WaitIdle();
        Assert.True(harness.Sharing.View.Find(Aria)!.ReplacingKey);
        File.Delete(Path.Combine(harness.Root, CharacterSharing.OnlineNoticeFileName));
        harness.Restart();
        Assert.True(harness.Sharing.View.OnlineNotice);
        Assert.Null(OnlineCount.TargetOf(harness.Sharing.View, Aria));

        // The replacement's check passes, so the character shares under its new key; the notice is
        // still due, and the count is still sending nothing.
        Assert.True(harness.Sharing.TryNewCode(Aria));
        harness.WaitIdle();
        Assert.True(harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh"));
        harness.WaitIdle();
        var entry = harness.Sharing.View.Find(Aria)!;
        Assert.Equal(SharingStage.Shared, entry.Stage);
        Assert.False(entry.ReplacingKey);
        Assert.True(harness.Sharing.View.OnlineNotice);
        Assert.Null(OnlineCount.TargetOf(harness.Sharing.View, Aria));

        Assert.True(harness.Sharing.TryDismissOnlineNotice());
        Assert.NotNull(OnlineCount.TargetOf(harness.Sharing.View, Aria));
    }

    [Fact]
    public void TurningSharingOn_CountsAsSeeingTheNotice_SinceTheConsentSaysIt()
    {
        using var harness = new SharingHarness();
        Assert.False(harness.Sharing.View.OnlineNotice);
        Assert.False(File.Exists(Path.Combine(harness.Root, CharacterSharing.OnlineNoticeFileName)));

        // The consent this build shows says what the count sends, so agreeing to it is the notice
        // seen, before any request of the count's can be made.
        Assert.Contains(SharingText.OnlineCountSends, SharingText.Consent);
        Assert.True(harness.Sharing.TryStart(Aria, newKey: false));
        harness.WaitIdle();
        Assert.False(harness.Sharing.View.OnlineNotice);
        Assert.True(File.Exists(Path.Combine(harness.Root, CharacterSharing.OnlineNoticeFileName)));
        harness.Restart();
        Assert.False(harness.Sharing.View.OnlineNotice);
    }

    [Fact]
    public void TheCount_FollowsTheGamesTick_NotDrawing()
    {
        // Dalamud calls no plugin's draw while it hides plugin windows (a hidden interface, a
        // cutscene, group pose), so a logout there would leave the heartbeats going. The behaviour
        // is in OnlineCountTests; this holds the wiring that makes it reachable.
        var plugin = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Plugin.cs"));
        Assert.Contains("Framework.Update += OnFrameworkTick;", plugin, StringComparison.Ordinal);
        Assert.Contains("private void OnFrameworkTick(IFramework framework) => presenceDriver.Tick();", plugin, StringComparison.Ordinal);
        Assert.Contains("Framework.Update -= OnFrameworkTick;", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("onlineCount.Update(", plugin, StringComparison.Ordinal);

        // And the driver sends nothing for a character that isn't logged in.
        Assert.Null(OnlineCount.TargetOf(new CharacterSharingView(true, false, false, [], null, null, null), null));
    }
}
