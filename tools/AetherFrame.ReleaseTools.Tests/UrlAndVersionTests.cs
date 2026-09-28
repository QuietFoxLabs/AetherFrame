using System;
using System.IO;
using System.Text;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

public class DownloadUrlTemplateTests
{
    [Fact]
    public void GitHubReleaseTemplate_ResolvesToTheAssetUrl()
    {
        var template = DownloadUrlTemplate.Parse(TestPackages.DownloadTemplate);

        Assert.Equal(
            "https://github.com/richhiiee/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip",
            template.Resolve("AetherFrame", new ProductVersion(0, 1, 5), "AetherFrame-0.1.5.zip"));
    }

    [Fact]
    public void InternalNamePlaceholder_IsSubstituted()
    {
        var template = DownloadUrlTemplate.Parse("https://example.invalid/{internalName}/{version}/{package}");

        Assert.Equal("https://example.invalid/AetherFrame/0.1.5/AetherFrame-0.1.5.zip", template.Resolve("AetherFrame", new ProductVersion(0, 1, 5), "AetherFrame-0.1.5.zip"));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("https://github.com/o/r/releases/download/v{version}/AetherFrame.zip", "must end with '/{package}'")]
    [InlineData("https://github.com/o/r/releases/download/{package}/v{version}", "must end with '/{package}'")]
    [InlineData("https://github.com/o/r/releases/download/v{version}/{package}.zip", "must end with '/{package}'")]
    [InlineData("https://github.com/o/r/releases/download/v{tag}/{package}", "unknown placeholder(s) {tag}")]
    [InlineData("https://github.com/o/r/{}/{package}", "unknown placeholder(s) {}")]
    [InlineData("http://github.com/o/r/releases/download/v{version}/{package}", "must use https")]
    [InlineData("https://user:secret@github.com/o/r/releases/download/v{version}/{package}", "user information")]
    [InlineData("https://github.com:8443/o/r/releases/download/v{version}/{package}", "default https port")]
    [InlineData("https://github.com/o/r/releases/download/v{version}/{package}?token=1", "must end with '/{package}'")]
    [InlineData("https://github.com/o/r/releases/down load/v{version}/{package}", "characters that do not belong")]
    [InlineData("https://github.com/o/r/releases/download/v{{version}}/{package}", "characters that do not belong")]
    [InlineData("github.com/o/r/releases/download/v{version}/{package}", "not an absolute URL")]
    [InlineData("file:///C:/releases/v{version}/{package}", "must use https")]
    [InlineData("https://github.com/richhiiee/AetherFrame/releases/download/../../../../someone-else/AetherFrame/releases/download/v{version}/{package}", "not written in canonical form")]
    [InlineData("https://github.com/richhiiee/AetherFrame/releases/download/%2e%2e/%2e%2e/%2e%2e/%2e%2e/someone-else/v{version}/{package}", "not written in canonical form")]
    [InlineData("https://localhost/releases/v{version}/{package}", "dotted domain name")]
    public void BadTemplate_IsRefused(string template, string message)
    {
        var e = Assert.Throws<ReleaseCheckException>(() => DownloadUrlTemplate.Parse(template));
        Assert.Contains(message, e.Message);
    }
}

public class UrlsTests
{
    [Theory]
    [InlineData("https://github.com/richhiiee/AetherFrame")]
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/plugin-repository/pluginmaster.json")]
    [InlineData("https://raw.githubusercontent.com/richhiiee/AetherFrame/refs/heads/plugin-repository/pluginmaster.json")]
    [InlineData("https://dry-run.invalid/a/b.zip")]
    public void PlainHttpsUrls_Pass(string url) => Assert.Equal(url, Urls.ValidateHttps(url, "test").OriginalString);

    [Theory]
    [InlineData("http://github.com/x", "must use https")]
    [InlineData("https://github.com/x#frag", "fragment")]
    [InlineData("https://github.com/x?y=1", "query string")]
    [InlineData("https://a:b@github.com/x", "user information")]
    [InlineData("https://github.com:444/x", "default https port")]
    [InlineData("https://192.168.0.1/x", "must name a host")]
    [InlineData("https://github.com/x y", "characters that do not belong")]
    [InlineData("https://github.com/x\u00e9", "characters that do not belong")]
    [InlineData("https://github.com/x\ty", "characters that do not belong")]
    [InlineData("github.com/x", "not an absolute URL")]
    [InlineData("", "empty")]
    [InlineData("javascript:alert(1)", "must use https")]
    [InlineData("https://localhost/x", "dotted domain name")]
    [InlineData("https://intranet/x", "dotted domain name")]
    [InlineData("https://github.com./x", "dotted domain name")]
    [InlineData("https://GitHub.com/x", "a client would request 'https://github.com/x'")]
    [InlineData("https://github.com:443/x", "a client would request 'https://github.com/x'")]
    [InlineData("https://github.com/a/../x", "a client would request 'https://github.com/x'")]
    [InlineData("https://github.com/a/./x", "a client would request 'https://github.com/a/x'")]
    [InlineData("https://github.com/a/%2e%2e/x", "not written in canonical form")]
    public void OtherUrls_AreRefused(string url, string message)
    {
        Assert.Contains(message, Assert.Throws<ReleaseCheckException>(() => Urls.ValidateHttps(url, "test")).Message);
    }

    [Fact]
    public void LastSegment_IsTheFileName()
    {
        Assert.Equal("AetherFrame-0.1.5.zip", Urls.LastSegment(new Uri("https://github.com/o/r/releases/download/v0.1.5/AetherFrame-0.1.5.zip")));
        Assert.Equal(string.Empty, Urls.LastSegment(new Uri("https://github.com/o/r/")));
    }
}

public class ProductVersionTests
{
    [Theory]
    [InlineData("0.1.5", 0, 1, 5)]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("10.20.30", 10, 20, 30)]
    public void Parse_AcceptsMajorMinorPatch(string text, int major, int minor, int patch)
    {
        var version = ProductVersion.Parse(text, "test");

        Assert.Equal(new ProductVersion(major, minor, patch), version);
        Assert.Equal(text, version.ToString());
        Assert.Equal("v" + text, version.Tag);
        Assert.Equal(new Version(major, minor, patch, 0), version.AssemblyVersion);
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("0.1.5.0")]
    [InlineData("01.1.5")]
    [InlineData("0.1.05")]
    [InlineData("v0.1.5")]
    [InlineData("0.1.5-beta")]
    [InlineData(" 0.1.5")]
    [InlineData("0.1.5 ")]
    [InlineData("-1.0.0")]
    [InlineData("1.0.0.")]
    [InlineData("")]
    [InlineData("1234567890.0.0")]
    public void Parse_RefusesAnythingElse(string text)
    {
        Assert.False(ProductVersion.TryParse(text, out _));
        Assert.Throws<ReleaseCheckException>(() => ProductVersion.Parse(text, "test"));
    }

    [Theory]
    [InlineData("0.1.5.0", true)]
    [InlineData("0.1.5.1", false)]
    [InlineData("0.1.5", false)]
    [InlineData("0.1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("00.1.5.0", false)]
    [InlineData("0.01.5.0", false)]
    [InlineData("0.1.5.00", false)]
    [InlineData("+0.1.5.0", false)]
    [InlineData(" 0.1.5.0", false)]
    [InlineData("0.1.5.0 ", false)]
    public void FromAssemblyVersion_NeedsFourPartsEndingInZero(string? text, bool accepted)
    {
        if (accepted)
        {
            Assert.Equal(new ProductVersion(0, 1, 5), ProductVersion.FromAssemblyVersion(text, "test"));
        }
        else
        {
            Assert.Throws<ReleaseCheckException>(() => ProductVersion.FromAssemblyVersion(text, "test"));
        }
    }

    [Fact]
    public void Comparisons_OrderNumerically()
    {
        Assert.True(new ProductVersion(0, 1, 9) < new ProductVersion(0, 1, 10));
        Assert.True(new ProductVersion(0, 2, 0) > new ProductVersion(0, 1, 99));
        Assert.True(new ProductVersion(1, 0, 0) >= new ProductVersion(1, 0, 0));
        Assert.True(new ProductVersion(0, 1, 5) <= new ProductVersion(0, 1, 5));
    }

    [Fact]
    public void VersionProps_IsReadFromItsOneVersionElement()
    {
        using var directory = new TempDirectory();
        Assert.Equal(new ProductVersion(0, 1, 5), ProductVersion.ReadVersionProps(TestPackages.VersionProps(directory)));
        Assert.Equal(new ProductVersion(0, 2, 0), ProductVersion.ReadVersionProps(TestPackages.VersionProps(directory, "0.2.0")));
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><Version>0.1.5</Version></PropertyGroup><PropertyGroup><Version>0.1.6</Version></PropertyGroup></Project>", "exactly once")]
    [InlineData("<Project><PropertyGroup><AssemblyVersion>0.1.5.0</AssemblyVersion></PropertyGroup></Project>", "exactly once")]
    [InlineData("<Project><PropertyGroup><Version>0.1.5.0</Version></PropertyGroup></Project>", "not a MAJOR.MINOR.PATCH")]
    [InlineData("<Project><PropertyGroup><Version>0.1.5</Version>", "well-formed XML")]
    public void BadVersionProps_IsRefused(string xml, string message)
    {
        using var directory = new TempDirectory();
        var path = directory.File("Version.props");
        File.WriteAllText(path, xml);

        Assert.Contains(message, Assert.Throws<ReleaseCheckException>(() => ProductVersion.ReadVersionProps(path)).Message);
        Assert.Contains("not found", Assert.Throws<ReleaseCheckException>(() => ProductVersion.ReadVersionProps(directory.File("Missing.props"))).Message);
    }
}

public class RepositoryConfigurationTests
{
    [Fact]
    public void ValidConfiguration_Loads()
    {
        using var directory = new TempDirectory();
        var configuration = RepositoryConfiguration.Load(TestPackages.Config(directory));

        Assert.Equal("AetherFrame", configuration.InternalName);
        Assert.Equal(15, configuration.DalamudApiLevel);
        Assert.Equal(TestPackages.RepoUrl, configuration.SourceRepositoryUrl);
        Assert.Equal("https://raw.githubusercontent.com/richhiiee/AetherFrame/refs/heads/plugin-repository/pluginmaster.json", configuration.PluginMasterUrl);
        Assert.Equal("AetherFrame-0.1.5.zip", configuration.PackageFileName(new ProductVersion(0, 1, 5)));
        Assert.Equal("https://github.com/richhiiee/AetherFrame/releases/download/v0.1.5/AetherFrame-0.1.5.zip", configuration.DownloadUrl(new ProductVersion(0, 1, 5)));
    }

    [Theory]
    [InlineData("{\"internalName\": \"AetherFrame\", \"dalamudApiLevel\": 15, \"sourceRepositoryUrl\": \"https://github.com/richhiiee/AetherFrame\", \"pluginMasterUrl\": \"https://x.invalid/p.json\", \"downloadUrlTemplate\": \"https://x.invalid/v{version}/{package}\", \"extra\": 1}", "unknown key(s): extra")]
    [InlineData("{\"internalName\": \"Aether Frame\", \"dalamudApiLevel\": 15, \"sourceRepositoryUrl\": \"https://github.com/richhiiee/AetherFrame\", \"pluginMasterUrl\": \"https://x.invalid/p.json\", \"downloadUrlTemplate\": \"https://x.invalid/v{version}/{package}\"}", "internalName")]
    [InlineData("{\"internalName\": \"../AetherFrame\", \"dalamudApiLevel\": 15, \"sourceRepositoryUrl\": \"https://github.com/richhiiee/AetherFrame\", \"pluginMasterUrl\": \"https://x.invalid/p.json\", \"downloadUrlTemplate\": \"https://x.invalid/v{version}/{package}\"}", "internalName")]
    [InlineData("{\"internalName\": \"AetherFrame\", \"dalamudApiLevel\": 0, \"sourceRepositoryUrl\": \"https://github.com/richhiiee/AetherFrame\", \"pluginMasterUrl\": \"https://x.invalid/p.json\", \"downloadUrlTemplate\": \"https://x.invalid/v{version}/{package}\"}", "dalamudApiLevel")]
    [InlineData("{\"internalName\": \"AetherFrame\", \"sourceRepositoryUrl\": \"https://github.com/richhiiee/AetherFrame\", \"pluginMasterUrl\": \"https://x.invalid/p.json\", \"downloadUrlTemplate\": \"https://x.invalid/v{version}/{package}\"}", "dalamudApiLevel")]
    [InlineData("{\"internalName\": \"AetherFrame\", \"dalamudApiLevel\": 15, \"sourceRepositoryUrl\": \"http://github.com/richhiiee/AetherFrame\", \"pluginMasterUrl\": \"https://x.invalid/p.json\", \"downloadUrlTemplate\": \"https://x.invalid/v{version}/{package}\"}", "sourceRepositoryUrl")]
    [InlineData("{\"internalName\": \"AetherFrame\", \"dalamudApiLevel\": 15, \"sourceRepositoryUrl\": \"https://github.com/richhiiee/AetherFrame\", \"pluginMasterUrl\": \"https://x.invalid/plugins\", \"downloadUrlTemplate\": \"https://x.invalid/v{version}/{package}\"}", "must point at a .json file")]
    [InlineData("{\"internalName\": \"AetherFrame\", \"dalamudApiLevel\": 15, \"sourceRepositoryUrl\": \"https://github.com/richhiiee/AetherFrame\", \"pluginMasterUrl\": \"https://x.invalid/p.json\", \"downloadUrlTemplate\": \"https://x.invalid/latest.zip\"}", "must end with '/{package}'")]
    [InlineData("not json", "not valid JSON")]
    public void BadConfiguration_IsRefused(string json, string message)
    {
        Assert.Contains(message, Assert.Throws<ReleaseCheckException>(() => RepositoryConfiguration.Parse(Encoding.UTF8.GetBytes(json), "test")).Message);
    }

    [Fact]
    public void MissingConfigurationFile_IsRefused()
    {
        using var directory = new TempDirectory();
        Assert.Contains("not found", Assert.Throws<ReleaseCheckException>(() => RepositoryConfiguration.Load(directory.File("missing.json"))).Message);
    }
}
