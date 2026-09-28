using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

/// <summary>The publication writes only the branch that players' repository URL reads.</summary>
public class PublicationTargetTests
{
    [Theory]
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/plugin-repository/pluginmaster.json")]
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/refs/heads/plugin-repository/pluginmaster.json")]
    public void BothRawAddressForms_NameTheBranch(string url)
    {
        var target = PublicationTarget.FromConfiguration(TestPackages.Configuration(TestPackages.ConfigJson(pluginMasterUrl: url)));

        Assert.Equal("plugin-repository", target.Branch);
        Assert.Equal("richhiiee", target.Owner);
        Assert.Equal("AetherFrame", target.Repository);
        Assert.Equal(url, target.Url);
    }

    [Theory]
    // Another repository or owner: the workflow's token could not write there, and must not try.
    [InlineData("https://raw.githubusercontent.com/someone-else/AetherFrame/plugin-repository/pluginmaster.json", "the root of a branch in the source repository")]
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrameAssets/plugin-repository/pluginmaster.json", "the root of a branch in the source repository")]
    // A branch that holds source code.
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/master/pluginmaster.json", "never holds source code")]
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/refs/heads/main/pluginmaster.json", "never holds source code")]
    // Not a branch root, not the file the workflow writes, or not a branch at all.
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/plugin-repository/repo/pluginmaster.json", "the root of a branch")]
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/plugin-repository/repo.json", "the root of a branch")]
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/refs/tags/plugin-repository/pluginmaster.json", "the root of a branch")]
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/refs/pluginmaster.json", "never holds source code")]
    // A name that git, a URL and a shell might read differently.
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/Plugin_Repository/pluginmaster.json", "lowercase words joined by hyphens")]
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/plugin--repository/pluginmaster.json", "lowercase words joined by hyphens")]
    // Another host: the workflow has no way to publish there.
    [InlineData("https://richhiiee.github.io/AetherFrame/pluginmaster.json", "not served from raw.githubusercontent.com")]
    public void OtherAddresses_AreRefused(string url, string reason)
    {
        var config = TestPackages.Configuration(TestPackages.ConfigJson(pluginMasterUrl: url));

        var error = Assert.Throws<ReleaseCheckException>(() => PublicationTarget.FromConfiguration(config));

        Assert.Contains(reason, error.Message);
    }

    [Theory]
    [InlineData("gh-pages")]
    [InlineData("master")]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_RefusesAnyBranchButTheOneTheUrlServes(string? branch)
    {
        var error = Assert.Throws<ReleaseCheckException>(() => PublicationTarget.Resolve(TestPackages.Configuration(), branch));

        Assert.Contains("is not 'plugin-repository', the branch pluginMasterUrl", error.Message);
    }

    [Fact]
    public void Resolve_AcceptsTheConfiguredBranch()
    {
        Assert.Equal("plugin-repository", PublicationTarget.Resolve(TestPackages.Configuration(), "plugin-repository").Branch);
    }
}
