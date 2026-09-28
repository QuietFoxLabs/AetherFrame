using System;
using System.IO;
using AetherFrame.Persistence;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The plain store's copy, which in game makes every Recovery copy of a damaged file and every
/// pre-migration backup: exact bytes, never over an existing file, and nothing left behind when
/// it can't be made.
/// </summary>
public class SystemFileStoreCopyTests
{
    [Fact]
    public void Copy_KeepsTheExactBytes_IncludingOnesThatArentText()
    {
        using var directory = new TempDirectory();
        var source = Path.Combine(directory.Path, "damaged.json");
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'{', 0xFF, 0x00, 0xC3, 0x28];
        File.WriteAllBytes(source, bytes);
        var destination = Path.Combine(directory.Path, "Recovery", "damaged.damaged-1.json");

        new SystemFileStore().CopyFile(source, destination);

        Assert.Equal(bytes, File.ReadAllBytes(destination));
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    [Fact]
    public void Copy_KeepsTheSourcesModifiedTime()
    {
        using var directory = new TempDirectory();
        var source = Path.Combine(directory.Path, "damaged.json");
        File.WriteAllText(source, "{ truncated");
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);
        var destination = Path.Combine(directory.Path, "Recovery", "damaged.damaged-1.json");

        new SystemFileStore().CopyFile(source, destination);

        Assert.Equal(modified, File.GetLastWriteTimeUtc(destination));
    }

    [Fact]
    public void Copy_NeverReplacesAnExistingFile()
    {
        using var directory = new TempDirectory();
        var source = Path.Combine(directory.Path, "damaged.json");
        var destination = Path.Combine(directory.Path, "kept.json");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "kept earlier");

        Assert.Throws<IOException>(() => new SystemFileStore().CopyFile(source, destination));

        Assert.Equal("kept earlier", File.ReadAllText(destination));
    }

    [Fact]
    public void Copy_OfAMissingFile_CreatesNothing()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "Recovery", "gone.json");

        Assert.Throws<FileNotFoundException>(() => new SystemFileStore().CopyFile(Path.Combine(directory.Path, "gone.json"), destination));

        Assert.False(File.Exists(destination));
    }
}
