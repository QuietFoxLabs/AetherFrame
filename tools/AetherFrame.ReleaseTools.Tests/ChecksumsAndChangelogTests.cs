using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

public class ChecksumsTests
{
    [Fact]
    public void Format_IsSortedLowercaseTwoSpacesLf()
    {
        var text = Checksums.Format(new[] { ("b.zip", new string('B', 64)), ("a.txt", new string('a', 64)) });

        Assert.Equal($"{new string('a', 64)}  a.txt\n{new string('b', 64)}  b.zip\n", text);
    }

    [Fact]
    public void ForFiles_HashesEachFileUnderItsName_Deterministically()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("hello.txt"), "hello");
        File.WriteAllBytes(directory.File("AetherFrame-0.1.5.zip"), new byte[] { 1, 2, 3 });

        var text = Checksums.ForFiles(new[] { directory.File("hello.txt"), directory.File("AetherFrame-0.1.5.zip") });

        Assert.Equal(
            "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81  AetherFrame-0.1.5.zip\n" +
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824  hello.txt\n",
            text);
        Assert.Equal(text, Checksums.ForFiles(new[] { directory.File("AetherFrame-0.1.5.zip"), directory.File("hello.txt") }));
    }

    [Fact]
    public void ForFiles_RefusesMissingDuplicateOrNoFiles()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("a.txt"), "a");
        Directory.CreateDirectory(directory.File("sub"));
        File.WriteAllText(directory.File(Path.Combine("sub", "a.txt")), "other a");

        Assert.Contains("not found", Assert.Throws<ReleaseCheckException>(() => Checksums.ForFiles(new[] { directory.File("missing.txt") })).Message);
        Assert.Contains("unique names", Assert.Throws<ReleaseCheckException>(() => Checksums.ForFiles(new[] { directory.File("a.txt"), directory.File(Path.Combine("sub", "a.txt")) })).Message);
        Assert.Contains("no files", Assert.Throws<ReleaseCheckException>(() => Checksums.ForFiles(Array.Empty<string>())).Message);
    }

    [Fact]
    public void Parse_AcceptsTextAndBinaryModeLines_AndUppercaseHashes()
    {
        var entries = Checksums.Parse($"{new string('A', 64)}  a.zip\r\n{new string('b', 64)} *b.zip\n", "test");

        Assert.Equal(new[] { (new string('a', 64), "a.zip"), (new string('b', 64), "b.zip") }, entries);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("\n", "empty")]
    [InlineData("abc  a.zip\n", "not '<sha256>  <file name>'")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef a.zip\n", "not '<sha256>  <file name>'")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  a.zip\n\n0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  b.zip\n", "not '<sha256>  <file name>'")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  ../a.zip\n", "not a plain file name")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  sub/a.zip\n", "not a plain file name")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  C:\\a.zip\n", "not a plain file name")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  a.zip\n0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  a.zip\n", "more than once")]
    public void Parse_RefusesMalformedFiles(string text, string message)
    {
        Assert.Contains(message, Assert.Throws<ReleaseCheckException>(() => Checksums.Parse(text, "test")).Message);
    }

    [Fact]
    public void Verify_ChecksEveryListedFile()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("a.txt"), "a");
        File.WriteAllText(directory.File("b.txt"), "b");
        var checksums = directory.File("SHA256SUMS.txt");
        File.WriteAllText(checksums, Checksums.ForFiles(new[] { directory.File("a.txt"), directory.File("b.txt") }));

        var ok = new CheckList();
        Checksums.Verify(checksums, directory.Path, ok);
        TestPackages.AllPassed(ok);
        Assert.Equal(3, ok.Checks.Count);

        File.WriteAllText(directory.File("b.txt"), "tampered");
        File.Delete(directory.File("a.txt"));
        var bad = new CheckList();
        Checksums.Verify(checksums, directory.Path, bad);
        Assert.Contains("missing", TestPackages.Failure(bad, "checksum of a.txt"));
        Assert.Contains("listed as", TestPackages.Failure(bad, "checksum of b.txt"));

        var absent = new CheckList();
        Checksums.Verify(directory.File("none.txt"), directory.Path, absent);
        Assert.Contains("not found", TestPackages.Failure(absent, "checksum file"));
    }

    [Fact]
    public void Listed_FindsOneFilesHash()
    {
        using var directory = new TempDirectory();
        var checksums = directory.File("SHA256SUMS.txt");
        File.WriteAllText(checksums, $"{new string('a', 64)}  AetherFrame-0.1.5.zip\n");

        Assert.Equal(new string('a', 64), Checksums.Listed(checksums, "AetherFrame-0.1.5.zip"));
        Assert.Contains("no line for Other.zip", Assert.Throws<ReleaseCheckException>(() => Checksums.Listed(checksums, "Other.zip")).Message);
    }
}

public class ChangelogSectionsTests
{
    private const string Changelog = """
        # Changelog

        ## [Unreleased]

        - Not yet.

        ## [0.1.6] - 2026-10-01

        ### Fixed

        - Newer thing.

        ## [0.1.5] - 2026-09-26

        Intro line.

        ### Added

        - Older thing.

        [Unreleased]: https://github.com/richhiiee/AetherFrame/compare/v0.1.6...HEAD
        [0.1.6]: https://github.com/richhiiee/AetherFrame/compare/v0.1.5...v0.1.6
        [0.1.5]: https://github.com/richhiiee/AetherFrame/releases/tag/v0.1.5
        """;

    [Fact]
    public void Section_EndsAtTheNextVersionHeading()
    {
        Assert.Equal("### Fixed\n\n- Newer thing.", ChangelogSections.Section(Changelog, new ProductVersion(0, 1, 6)));
    }

    [Fact]
    public void Section_EndsAtTheLinkList()
    {
        Assert.Equal("Intro line.\n\n### Added\n\n- Older thing.", ChangelogSections.Section(Changelog, new ProductVersion(0, 1, 5)));
    }

    [Fact]
    public void Section_NormalizesWindowsLineEnds()
    {
        // Built from LF text: on a Windows checkout this source file, and so the raw string above, has CRLF.
        var windows = Changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.Equal("### Fixed\n\n- Newer thing.", ChangelogSections.Section(windows, new ProductVersion(0, 1, 6)));
    }

    [Theory]
    [InlineData("## [0.1.7] - 2026-10-02\n\n- x.\n", "0.1.6", "has no '## [0.1.6] - YYYY-MM-DD' section")]
    [InlineData("## [0.1.6]\n\n- x.\n", "0.1.6", "has no '## [0.1.6] - YYYY-MM-DD' section")]
    [InlineData("## [0.1.6] - soon\n\n- x.\n", "0.1.6", "has no '## [0.1.6] - YYYY-MM-DD' section")]
    [InlineData("## [0.1.6] - 2026-10-01\n\n\n## [0.1.5] - 2026-09-26\n\n- y.\n", "0.1.6", "is empty")]
    [InlineData("## [0.1.6] - 2026-10-01\n\n- x.\n\n## [0.1.6] - 2026-10-02\n\n- again.\n", "0.1.6", "2 sections")]
    public void MissingEmptyOrRepeatedSection_IsRefused(string changelog, string version, string message)
    {
        Assert.Contains(message, Assert.Throws<ReleaseCheckException>(() => ChangelogSections.Section(changelog, ProductVersion.Parse(version, "test"))).Message);
    }

    [Fact]
    public void OverlongSection_IsRefused()
    {
        var changelog = "## [0.1.6] - 2026-10-01\n\n" + new string('x', ChangelogSections.MaxLength + 1) + "\n";

        Assert.Contains("limited to", Assert.Throws<ReleaseCheckException>(() => ChangelogSections.Section(changelog, new ProductVersion(0, 1, 6))).Message);
    }

    [Fact]
    public void Read_NeedsTheFile()
    {
        using var directory = new TempDirectory();
        Assert.Contains("not found", Assert.Throws<ReleaseCheckException>(() => ChangelogSections.Read(directory.File("CHANGELOG.md"), new ProductVersion(0, 1, 5))).Message);
        File.WriteAllText(directory.File("CHANGELOG.md"), Changelog);
        Assert.Equal("### Fixed\n\n- Newer thing.", ChangelogSections.Read(directory.File("CHANGELOG.md"), new ProductVersion(0, 1, 6)));
    }
}
