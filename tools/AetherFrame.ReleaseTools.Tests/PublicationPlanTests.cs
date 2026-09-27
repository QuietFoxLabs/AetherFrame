using System;
using System.Linq;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

/// <summary>What one request does to the published channels (PublicationPlan's rules).</summary>
public class PublicationPlanTests
{
    [Theory]
    // First publications: a first pre-release is testing-exclusive; stable starts as stable only.
    [InlineData("none", "0.1.6", "testing", "exclusive 0.1.6")]
    [InlineData("none", "0.1.6", "stable", "stable 0.1.6")]
    // Testing moves on; a published stable version always stays.
    [InlineData("exclusive 0.1.6", "0.1.7", "testing", "exclusive 0.1.7")]
    [InlineData("stable 0.1.6", "0.1.7", "testing", "stable 0.1.6 testing 0.1.7")]
    [InlineData("stable 0.1.6 testing 0.1.7", "0.1.8", "testing", "stable 0.1.6 testing 0.1.8")]
    // Stable: promoting the testing version clears the slot; an older testing version is dropped (Dalamud
    // would ignore it); a newer one stays for testers, including over a testing-exclusive entry.
    [InlineData("exclusive 0.1.6", "0.1.6", "stable", "stable 0.1.6")]
    [InlineData("stable 0.1.6 testing 0.1.7", "0.1.7", "stable", "stable 0.1.7")]
    [InlineData("stable 0.1.6 testing 0.1.7", "0.1.8", "stable", "stable 0.1.8")]
    [InlineData("exclusive 0.1.6", "0.1.7", "stable", "stable 0.1.7")]
    [InlineData("stable 0.1.6 testing 0.1.8", "0.1.7", "stable", "stable 0.1.7 testing 0.1.8")]
    [InlineData("exclusive 0.1.7", "0.1.6", "stable", "stable 0.1.6 testing 0.1.7")]
    [InlineData("stable 0.1.6", "0.1.7", "stable", "stable 0.1.7")]
    public void Publish(string current, string version, string channel, string expected)
    {
        var plan = Plan(current, version, channel);

        Assert.Equal(PublicationChange.Publish, plan.Change);
        Assert.Equal(State(expected), plan.Target);
    }

    [Theory]
    [InlineData("exclusive 0.1.6", "0.1.6", "testing", false)]
    [InlineData("stable 0.1.6 testing 0.1.7", "0.1.7", "testing", false)]
    [InlineData("stable 0.1.6 testing 0.1.7", "0.1.6", "stable", false)]
    [InlineData("stable 0.1.6", "0.1.6", "testing", false)]
    // Re-running a rollback that already happened is harmless too.
    [InlineData("stable 0.1.6", "0.1.6", "stable", true)]
    public void AskingForWhatAChannelServes_ChangesNothing(string current, string version, string channel, bool rollback)
    {
        var plan = Plan(current, version, channel, rollback);

        Assert.Equal(PublicationChange.Unchanged, plan.Change);
        Assert.Equal(State(current), plan.Target);
    }

    [Theory]
    // Removing a bad testing version: testers get the stable version again.
    [InlineData("stable 0.1.6 testing 0.1.7", "0.1.6", "testing", "stable 0.1.6")]
    [InlineData("stable 0.1.5 testing 0.1.7", "0.1.6", "testing", "stable 0.1.5 testing 0.1.6")]
    [InlineData("exclusive 0.1.7", "0.1.6", "testing", "exclusive 0.1.6")]
    // Pointing stable back at a known good release; a newer testing version stays.
    [InlineData("stable 0.1.7", "0.1.6", "stable", "stable 0.1.6")]
    [InlineData("stable 0.1.7 testing 0.1.8", "0.1.6", "stable", "stable 0.1.6 testing 0.1.8")]
    public void Rollback(string current, string version, string channel, string expected)
    {
        var plan = Plan(current, version, channel, rollback: true);

        Assert.Equal(PublicationChange.Rollback, plan.Change);
        Assert.Equal(State(expected), plan.Target);
    }

    [Theory]
    // An accidental downgrade: Dalamud would never offer it to players who already updated.
    [InlineData("stable 0.1.7", "0.1.6", "stable", false, "older than the 0.1.7 the stable channel serves now")]
    [InlineData("exclusive 0.1.7", "0.1.6", "testing", false, "older than the 0.1.7 the testing channel serves now")]
    [InlineData("stable 0.1.6 testing 0.1.7", "0.1.6", "testing", false, "run again with rollback")]
    // Testers always get at least the stable version, rollback or not.
    [InlineData("stable 0.1.6", "0.1.5", "testing", false, "always get at least the stable version, 0.1.6")]
    [InlineData("stable 0.1.6", "0.1.5", "testing", true, "always get at least the stable version, 0.1.6")]
    // A rollback must move a channel back.
    [InlineData("none", "0.1.6", "testing", true, "nothing to roll back")]
    [InlineData("exclusive 0.1.6", "0.1.5", "stable", true, "nothing to roll back")]
    [InlineData("stable 0.1.6", "0.1.7", "stable", true, "a rollback only moves a channel to an older version")]
    public void Refused(string current, string version, string channel, bool rollback, string reason)
    {
        var error = Assert.Throws<ReleaseCheckException>(() => Plan(current, version, channel, rollback));

        Assert.Contains(reason, error.Message);
    }

    [Theory]
    [InlineData("beta")]
    [InlineData("Testing")]
    [InlineData("")]
    [InlineData(null)]
    public void OnlyTheTwoChannelsExist(string? channel)
    {
        var error = Assert.Throws<ReleaseCheckException>(() => ReleaseChannels.Parse(channel));

        Assert.Contains("neither 'testing' nor 'stable'", error.Message);
    }

    [Fact]
    public void Releases_AreEveryVersionTheTargetDescribes_OldestFirst()
    {
        Assert.Equal(new[] { "0.1.6", "0.1.7" }, Plan("stable 0.1.6", "0.1.7", "testing").Releases.Select(v => v.ToString()));
        Assert.Equal(new[] { "0.1.6" }, Plan("none", "0.1.6", "testing").Releases.Select(v => v.ToString()));
        Assert.Equal(new[] { "0.1.6", "0.1.8" }, Plan("stable 0.1.7 testing 0.1.8", "0.1.6", "stable", rollback: true).Releases.Select(v => v.ToString()));
    }

    [Theory]
    [InlineData("none", "0.1.6", "testing", false, "Publish 0.1.6 to testing")]
    [InlineData("exclusive 0.1.6", "0.1.6", "stable", false, "Promote 0.1.6 from testing to stable")]
    [InlineData("stable 0.1.6 testing 0.1.7", "0.1.6", "testing", true, "Roll back testing from 0.1.7 to 0.1.6")]
    [InlineData("stable 0.1.6", "0.1.6", "stable", false, "0.1.6 is already on stable")]
    public void Titles_SayWhatHappened(string current, string version, string channel, bool rollback, string title)
    {
        Assert.Equal(title, Plan(current, version, channel, rollback).Title);
    }

    [Fact]
    public void State_IsReadFromEachEntryShape()
    {
        using var directory = new TempDirectory();
        var configuration = TestPackages.Configuration();
        var changelog = TestPackages.Changelog(directory, ("0.1.6", "- New."), ("0.1.5", "- Old."));
        var stable = TestPackages.ValidReport(directory, "0.1.5", configuration, changelog);
        var testing = TestPackages.ValidReport(directory, "0.1.6", configuration, changelog);
        var time = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

        Assert.Equal(State("stable 0.1.5"), RepositoryState.FromEntry(RepositoryGenerator.Build(configuration, stable, null, false, time)));
        Assert.Equal(State("stable 0.1.5 testing 0.1.6"), RepositoryState.FromEntry(RepositoryGenerator.Build(configuration, stable, testing, false, time)));
        Assert.Equal(State("exclusive 0.1.6"), RepositoryState.FromEntry(RepositoryGenerator.Build(configuration, null, testing, true, time)));
    }

    private static PublicationPlan Plan(string current, string version, string channel, bool rollback = false) =>
        PublicationPlan.Compute(State(current), ProductVersion.Parse(version, "version"), ReleaseChannels.Parse(channel), rollback);

    /// <summary>"none", "stable X", "stable X testing Y" or "exclusive Y".</summary>
    private static RepositoryState State(string text)
    {
        var words = text.Split(' ');
        ProductVersion V(int index) => ProductVersion.Parse(words[index], "version");
        return words switch
        {
            ["none"] => RepositoryState.Empty,
            ["stable", _] => RepositoryState.StableOnly(V(1)),
            ["stable", _, "testing", _] => RepositoryState.StableAndTesting(V(1), V(3)),
            ["exclusive", _] => RepositoryState.TestingExclusive(V(1)),
            _ => throw new ArgumentException(text),
        };
    }
}
