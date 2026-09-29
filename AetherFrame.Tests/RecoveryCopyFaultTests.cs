using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A Recovery copy under its final name is the Libraries' only evidence that a damaged file, or one
/// read with bytes that aren't valid text, is safe to write over. So a copy that fails after part of
/// it has reached the disk (a full disk) must leave nothing behind: no partial file under the copy's
/// name, no temporary file, the source unchanged, and a file that already has that name untouched.
/// The write that needed the copy is refused, and once the disk has room again the copy holds
/// exactly the original bytes, even when the clock gives the retry the same name. A copy is flushed
/// to disk before it is given its name, and never locks its source against other readers.
/// </summary>
public class RecoveryCopyFaultTests
{
    /// <summary>Bytes a copy may write before the disk is full: fewer than any Library file holds.</summary>
    private const int DiskRoom = 16;

    private static readonly DateTime SourceModified = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    /// <summary>
    /// Bytes no text decoder would give back unchanged: a UTF-8 BOM, then seeded noise with a NUL
    /// every thousand bytes, larger than one copy buffer.
    /// </summary>
    private static byte[] NotText()
    {
        var bytes = new byte[300 * 1024];
        new Random(20260929).NextBytes(bytes);
        bytes[0] = 0xEF;
        bytes[1] = 0xBB;
        bytes[2] = 0xBF;
        for (var i = 3; i < bytes.Length; i += 1000)
        {
            bytes[i] = 0x00;
        }

        return bytes;
    }

    /// <summary>A source file holding <paramref name="bytes"/>, last written long ago, and where its Recovery copy goes.</summary>
    private static (string Source, string Destination) SeedSource(TempDirectory directory, byte[] bytes)
    {
        var source = Path.Combine(directory.Path, "damaged.json");
        File.WriteAllBytes(source, bytes);
        File.SetLastWriteTimeUtc(source, SourceModified);
        return (source, Path.Combine(directory.Path, "Recovery", "damaged.damaged-20260901-120000-000.json"));
    }

    private static string[] FilesIn(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories) : [];

    [Fact]
    public void CopyFailingPartway_LeavesNothingInTheDestinationFolder_AndTheSourceUnchanged()
    {
        using var directory = new TempDirectory();
        var original = NotText();
        var (source, destination) = SeedSource(directory, original);
        var copies = new DiskFullCopies { FailAfterBytes = 100_000 };

        var thrown = Assert.Throws<IOException>(() => new SystemFileStore(copies.Create).CopyFile(source, destination));

        var fault = Assert.Single(copies.Faults);
        Assert.True(ReferenceEquals(fault, thrown) || ReferenceEquals(fault, thrown.InnerException), thrown.ToString());
        Assert.InRange(Assert.Single(copies.PartialLengths), 1, original.Length - 1);
        var temporary = Assert.Single(copies.CreatedPaths);
        Assert.Equal(Path.GetDirectoryName(destination), Path.GetDirectoryName(temporary));
        Assert.Matches($@"^\.{Regex.Escape(Path.GetFileName(destination))}\.[0-9A-Fa-f-]{{32,36}}\.tmp$", Path.GetFileName(temporary));

        // What a crash at that moment would have left: the partial copy under its temporary name only.
        Assert.DoesNotContain(destination, Assert.Single(copies.NamesAtFault));
        Assert.False(File.Exists(destination));
        Assert.Empty(FilesIn(Path.GetDirectoryName(destination)!));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(SourceModified, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public void CopyRetriedOnceTheDiskHasRoom_HoldsExactlyTheSourceBytes_AndItsModifiedTime()
    {
        using var directory = new TempDirectory();
        var original = NotText();
        var (source, destination) = SeedSource(directory, original);
        var copies = new DiskFullCopies { FailAfterBytes = 100_000 };
        var store = new SystemFileStore(copies.Create);
        Assert.Throws<IOException>(() => store.CopyFile(source, destination));

        copies.FailAfterBytes = null;
        store.CopyFile(source, destination);

        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Equal(SourceModified, File.GetLastWriteTimeUtc(destination));
        Assert.Equal([destination], FilesIn(Path.GetDirectoryName(destination)!));
        Assert.All(copies.CreatedPaths, path => Assert.False(File.Exists(path), $"Left behind: {path}"));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(SourceModified, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public void Copy_IsFlushedToDisk_BeforeItIsGivenItsName()
    {
        using var directory = new TempDirectory();
        var original = NotText();
        var (source, destination) = SeedSource(directory, original);
        var copies = new DiskFullCopies { Destination = destination };

        new SystemFileStore(copies.Create).CopyFile(source, destination);

        Assert.Contains(copies.DiskFlushes, flush => flush.BytesWritten == original.Length);
        Assert.DoesNotContain(copies.DiskFlushes, flush => flush.DestinationExisted);
        Assert.Equal(original, File.ReadAllBytes(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingDestination_IsNeverTouched(bool diskFull)
    {
        using var directory = new TempDirectory();
        var (source, destination) = SeedSource(directory, NotText());
        byte[] kept = [.. "kept earlier "u8, 0xFF, 0x00];
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, kept);
        var keptModified = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(destination, keptModified);
        var copies = new DiskFullCopies { FailAfterBytes = diskFull ? DiskRoom : null };

        Assert.Throws<IOException>(() => new SystemFileStore(copies.Create).CopyFile(source, destination));

        // Refused before anything is copied (see DestinationAppearingDuringTheCopy_... for later).
        Assert.Empty(copies.CreatedPaths);
        Assert.Equal(kept, File.ReadAllBytes(destination));
        Assert.Equal(keptModified, File.GetLastWriteTimeUtc(destination));
        Assert.Equal([destination], FilesIn(Path.GetDirectoryName(destination)!));
        Assert.All(copies.CreatedPaths, path => Assert.False(File.Exists(path), $"Left behind: {path}"));
    }

    /// <summary>
    /// A file that takes the copy's name while the copy is being written (another program, or a
    /// copy of the same file in the same millisecond) is never replaced by the copy's rename, and
    /// never deleted by a failed copy's cleanup.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DestinationAppearingDuringTheCopy_IsNeverReplacedOrRemoved(bool diskFull)
    {
        using var directory = new TempDirectory();
        var (source, destination) = SeedSource(directory, NotText());
        byte[] appeared = [.. "appeared meanwhile "u8, 0xFF];
        var appearedModified = new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var copies = new DiskFullCopies { FailAfterBytes = diskFull ? 100_000 : null };
        copies.DuringWrite = () =>
        {
            if (!File.Exists(destination))
            {
                File.WriteAllBytes(destination, appeared);
                File.SetLastWriteTimeUtc(destination, appearedModified);
            }
        };

        Assert.Throws<IOException>(() => new SystemFileStore(copies.Create).CopyFile(source, destination));

        Assert.NotEmpty(copies.CreatedPaths);
        Assert.Equal(appeared, File.ReadAllBytes(destination));
        Assert.Equal(appearedModified, File.GetLastWriteTimeUtc(destination));
        Assert.Equal([destination], FilesIn(Path.GetDirectoryName(destination)!));
    }

    /// <summary>A full disk often shows only when the written data is flushed, or when the copy
    /// can't even be created: either way nothing is left, and the source is untouched.</summary>
    [Theory]
    [InlineData("flush")]
    [InlineData("create")]
    public void CopyFailingOutsideItsWrites_LeavesNothingBehind(string where)
    {
        using var directory = new TempDirectory();
        var original = NotText();
        var (source, destination) = SeedSource(directory, original);
        var copies = new DiskFullCopies { FailOnDiskFlush = where == "flush" };
        Func<string, FileStream> create = where == "create"
            ? _ => throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070))
            : copies.Create;

        Assert.Throws<IOException>(() => new SystemFileStore(create).CopyFile(source, destination));

        Assert.False(File.Exists(destination));
        Assert.Empty(FilesIn(Path.GetDirectoryName(destination)!));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(SourceModified, File.GetLastWriteTimeUtc(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Source_StaysReadableAndUnchanged_WhileItIsCopied(bool diskFull)
    {
        using var directory = new TempDirectory();
        var original = NotText();
        var (source, destination) = SeedSource(directory, original);
        var readsDuringCopy = new List<byte[]>();
        var copies = new DiskFullCopies
        {
            FailAfterBytes = diskFull ? 100_000 : null,
            DuringWrite = () => readsDuringCopy.Add(File.ReadAllBytes(source)),
        };

        var failure = Record.Exception(() => new SystemFileStore(copies.Create).CopyFile(source, destination));

        Assert.Equal(diskFull, failure is IOException);
        Assert.NotEmpty(readsDuringCopy);
        Assert.All(readsDuringCopy, read => Assert.Equal(original, read));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(SourceModified, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public void Source_HeldOpenForWritingByAnotherProgram_IsStillCopiedExactly()
    {
        using var directory = new TempDirectory();
        var original = NotText();
        var (source, destination) = SeedSource(directory, original);
        var copies = new DiskFullCopies();

        using (new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            new SystemFileStore(copies.Create).CopyFile(source, destination);
        }

        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    /// <summary>The file's bytes with the first character of <paramref name="quoted"/>'s content replaced by a byte that is never valid UTF-8.</summary>
    private static byte[] WithInvalidByte(byte[] file, string quoted)
    {
        var at = Encoding.ASCII.GetString(file).IndexOf(quoted, StringComparison.Ordinal) + 1;
        Assert.True(at > 0, $"{quoted} isn't in the file");
        var damaged = file.ToArray();
        damaged[at] = 0xFF;
        return damaged;
    }

    /// <summary>A Plate saved through the backup-keeping store, so its file has a usable backup.</summary>
    private static async Task<(Guid PlateId, string Path)> SeedPlateAsync(LibraryFixture fixture)
    {
        var seeded = await fixture.LoadAsync();
        var plateId = (await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, "Kept")).PlateId;
        return (plateId, fixture.Paths.GetPlatePath(plateId));
    }

    private static LibraryFixture DiskFullLibrary(DiskFullCopies copies) =>
        new(new BackupSimulatingStore(new SystemFileStore(copies.Create)));

    private static Task WritePlateAsync(PlateLibraryService library, Guid plateId, string operation) => operation == "save"
        ? library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId))
        : library.RenamePlateAsync(plateId, "Renamed");

    /// <summary>
    /// At least <paramref name="attempts"/> copies failed partway, each with part of the file on
    /// disk; and at each failure, what a crash would have left in Recovery was only temporary files:
    /// never a partial file under a copy's own name, which the Libraries would take for a kept copy.
    /// </summary>
    private static void AssertFailedPartway(DiskFullCopies copies, int attempts, int fileLength)
    {
        Assert.True(copies.PartialLengths.Count >= attempts, $"{copies.PartialLengths.Count} copies failed partway, expected at least {attempts}.");
        Assert.All(copies.PartialLengths, length => Assert.InRange(length, 1, fileLength - 1));
        Assert.All(copies.NamesAtFault.SelectMany(names => names), name => Assert.Matches(@"^\..*\.tmp$", Path.GetFileName(name)));
    }

    /// <summary>Nothing under Recovery, and none of the files a copy was written to left anywhere.</summary>
    private static void AssertNothingLeftBehind(PlateStoragePaths paths, DiskFullCopies copies)
    {
        Assert.Empty(FilesIn(paths.RecoveryDirectory));
        Assert.All(copies.CreatedPaths, path => Assert.False(File.Exists(path), $"Left behind: {path}"));
    }

    /// <summary>The only file under Recovery is <paramref name="path"/>'s copy, stamped with the clock, holding exactly <paramref name="original"/>.</summary>
    private static void AssertKeptExactly(PlateStoragePaths paths, FakeClock clock, string path, byte[] original)
    {
        var kept = Assert.Single(FilesIn(paths.RecoveryDirectory));
        var name = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(path)}.damaged-{clock.Now:yyyyMMdd-HHmmss-fff}{Path.GetExtension(path)}");
        Assert.Equal(Path.Combine(paths.RecoveryDirectory, name), kept);
        Assert.Equal(original, File.ReadAllBytes(kept));
    }

    [Theory]
    [InlineData("save")]
    [InlineData("rename")]
    public async Task DamagedPlate_WhoseCopyFailsPartwayAtLoad_IsNeverWrittenOver_UntilAnExactCopyIsKept(string operation)
    {
        var copies = new DiskFullCopies();
        using var fixture = DiskFullLibrary(copies);
        var (plateId, path) = await SeedPlateAsync(fixture);
        var file = File.ReadAllBytes(path);
        var damaged = file[..(file.Length / 2)];
        File.WriteAllBytes(path, damaged);
        copies.FailAfterBytes = DiskRoom;

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        Assert.Equal("Kept", library.FindPlate(plateId)!.DisplayName);
        AssertFailedPartway(copies, attempts: 1, damaged.Length);
        AssertNothingLeftBehind(fixture.Paths, copies);
        Assert.Equal(damaged, File.ReadAllBytes(path));

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => WritePlateAsync(library, plateId, operation));
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        AssertFailedPartway(copies, attempts: 2, damaged.Length);
        AssertNothingLeftBehind(fixture.Paths, copies);
        Assert.Equal(damaged, File.ReadAllBytes(path));

        copies.FailAfterBytes = null;
        await WritePlateAsync(library, plateId, operation);

        AssertKeptExactly(fixture.Paths, fixture.Clock, path, damaged);
        Assert.All(copies.CreatedPaths, created => Assert.False(File.Exists(created), $"Left behind: {created}"));
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, reloaded.FindPlate(plateId)!.Status);
        Assert.Equal(operation == "save" ? "Kept" : "Renamed", reloaded.FindPlate(plateId)!.DisplayName);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("rename")]
    public async Task InvalidTextPlate_WhoseCopyFailsPartwayOnWrite_IsNeverWrittenOver_UntilAnExactCopyIsKept(string operation)
    {
        var copies = new DiskFullCopies();
        using var fixture = DiskFullLibrary(copies);
        var (plateId, path) = await SeedPlateAsync(fixture);
        var damaged = WithInvalidByte(File.ReadAllBytes(path), "\"Kept\"");
        File.WriteAllBytes(path, damaged);
        copies.FailAfterBytes = DiskRoom;

        var library = await fixture.LoadAsync();

        var summary = library.FindPlate(plateId)!;
        Assert.Equal(PlateStatus.Ready, summary.Status);
        Assert.Equal("�ept", summary.DisplayName);
        Assert.Equal(PlateLibraryService.InvalidTextPlateProblem, summary.Problem);
        Assert.Empty(copies.CreatedPaths);
        Assert.Empty(FilesIn(fixture.Paths.RecoveryDirectory));
        Assert.Equal(damaged, File.ReadAllBytes(path));

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => WritePlateAsync(library, plateId, operation));
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        AssertFailedPartway(copies, attempts: 1, damaged.Length);
        AssertNothingLeftBehind(fixture.Paths, copies);
        Assert.Equal(damaged, File.ReadAllBytes(path));

        copies.FailAfterBytes = null;
        await WritePlateAsync(library, plateId, operation);

        AssertKeptExactly(fixture.Paths, fixture.Clock, path, damaged);
        Assert.DoesNotContain(File.ReadAllBytes(path), b => b >= 0x80);
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, reloaded.FindPlate(plateId)!.Status);
        Assert.Null(reloaded.FindPlate(plateId)!.Problem);
        Assert.Equal(operation == "save" ? "�ept" : "Renamed", reloaded.FindPlate(plateId)!.DisplayName);
    }

    [Fact]
    public async Task InvalidTextBinding_WhoseCopyFailsPartway_RefusesSetActive_UntilAnExactCopyIsKept()
    {
        var copies = new DiskFullCopies();
        using var fixture = DiskFullLibrary(copies);
        var seeded = await fixture.LoadAsync();
        var first = (await seeded.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "First")).PlateId;
        var second = (await seeded.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Second")).PlateId;
        var path = fixture.Paths.GetBindingPath(Characters.Alice.ContentId);
        var damaged = WithInvalidByte(File.ReadAllBytes(path), $"\"{Characters.Alice.Name}\"");
        File.WriteAllBytes(path, damaged);
        copies.FailAfterBytes = DiskRoom;

        var library = await fixture.LoadAsync();

        Assert.Equal(first, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal(new[] { first, second }.Order(), library.GetBinding(Characters.Alice.ContentId)!.PlateIds.Order());
        Assert.Empty(copies.CreatedPaths);

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.SetActivePlateAsync(Characters.Alice, second));
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        AssertFailedPartway(copies, attempts: 1, damaged.Length);
        AssertNothingLeftBehind(fixture.Paths, copies);
        Assert.Equal(damaged, File.ReadAllBytes(path));

        copies.FailAfterBytes = null;
        await library.SetActivePlateAsync(Characters.Alice, second);

        AssertKeptExactly(fixture.Paths, fixture.Clock, path, damaged);
        var binding = fixture.ReadBinding(Characters.Alice.ContentId);
        Assert.Equal(second, binding.GetProperty("ActiveProfileId").GetGuid());
        Assert.Equal(new[] { first, second }.Order(), binding.GetProperty("ProfileIds").EnumerateArray().Select(id => id.GetGuid()).Order());
        Assert.Equal(Characters.Alice.Name, binding.GetProperty("LastKnownCharacterName").GetString());
    }

    [Fact]
    public async Task InvalidTextIndex_WhoseCopyFailsPartway_RefusesTheMove_UntilAnExactCopyIsKept()
    {
        var copies = new DiskFullCopies();
        using var fixture = DiskFullLibrary(copies);
        var seeded = await fixture.LoadAsync();
        for (var i = 0; i < 3; i++)
        {
            await seeded.CreatePlateAsync(PlateStartingLayout.Blank, null, $"Plate {i}");
        }

        // The player's own order, which a rebuild (newest first) wouldn't give.
        var created = fixture.ReadLibraryOrder();
        await seeded.MovePlateAsync(created[2], created[0], placeAfter: false);
        var order = fixture.ReadLibraryOrder();
        Assert.NotEqual(created, order);
        var text = File.ReadAllText(fixture.Paths.LibraryFile);
        var at = text.IndexOf('{', StringComparison.Ordinal) + 1;
        byte[] damaged = [.. Encoding.UTF8.GetBytes(text[..at]), .. "\"Note\": \"x"u8, 0xFF, .. "y\","u8, .. Encoding.UTF8.GetBytes(text[at..])];
        File.WriteAllBytes(fixture.Paths.LibraryFile, damaged);
        copies.FailAfterBytes = DiskRoom;

        var library = await fixture.LoadAsync();

        Assert.Equal(order, library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Empty(copies.CreatedPaths);

        var refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.MovePlateAsync(order[2], order[0], placeAfter: false));
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        AssertFailedPartway(copies, attempts: 1, damaged.Length);
        AssertNothingLeftBehind(fixture.Paths, copies);
        Assert.Equal(damaged, File.ReadAllBytes(fixture.Paths.LibraryFile));

        // A change from the order either before or after the refused move, so this write happens.
        copies.FailAfterBytes = null;
        await library.MovePlateAsync(order[1], order[0], placeAfter: false);

        AssertKeptExactly(fixture.Paths, fixture.Clock, fixture.Paths.LibraryFile, damaged);
        var saved = fixture.ReadLibraryOrder();
        Assert.Equal(library.GetOrderedPlates().Select(p => p.PlateId), saved);
        Assert.Equal(order.Order(), saved.Order());
        Assert.True(saved.IndexOf(order[1]) < saved.IndexOf(order[0]));
        Assert.Equal("x�y", JsonDocument.Parse(File.ReadAllText(fixture.Paths.LibraryFile)).RootElement.GetProperty("Note").GetString());
    }

    [Fact]
    public async Task InvalidTextTemplate_WhoseCopyFailsPartway_RefusesTheRename_UntilAnExactCopyIsKept()
    {
        var copies = new DiskFullCopies();
        using var fixture = new TemplateLibraryFixture(new BackupSimulatingStore(new SystemFileStore(copies.Create)));
        var seeded = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await seeded.SaveAsTemplateAsync(plate.PlateId, "Kept");
        var path = fixture.Paths.GetTemplatePath(templateId);
        var damaged = WithInvalidByte(File.ReadAllBytes(path), "\"Kept\"");
        File.WriteAllBytes(path, damaged);
        copies.FailAfterBytes = DiskRoom;

        var templates = fixture.CreateService();
        await templates.InitializeAsync();

        var summary = templates.FindTemplate(templateId)!;
        Assert.True(summary.IsReady);
        Assert.Equal(TemplateLibraryService.InvalidTextTemplateProblem, summary.Problem);
        Assert.Empty(copies.CreatedPaths);

        var refused = await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.RenameTemplateAsync(templateId, "Renamed"));
        Assert.Equal(PlateLibraryService.UnpreservedDamagedFileMessage, refused.Message);
        AssertFailedPartway(copies, attempts: 1, damaged.Length);
        AssertNothingLeftBehind(fixture.Paths, copies);
        Assert.Equal(damaged, File.ReadAllBytes(path));
        Assert.Equal("�ept", templates.FindTemplate(templateId)!.DisplayName);

        copies.FailAfterBytes = null;
        await templates.RenameTemplateAsync(templateId, "Renamed");

        AssertKeptExactly(fixture.Paths, fixture.Clock, path, damaged);
        var reloaded = fixture.CreateService();
        await reloaded.InitializeAsync();
        Assert.True(reloaded.FindTemplate(templateId)!.IsReady);
        Assert.Null(reloaded.FindTemplate(templateId)!.Problem);
        Assert.Equal("Renamed", reloaded.FindTemplate(templateId)!.DisplayName);
    }

    /// <summary>
    /// The clock stands still, so the next session's copy has the same name as the failed one: a
    /// partial file left under that name would pass for the kept copy and let the save go ahead.
    /// </summary>
    [Theory]
    [InlineData("damaged")]
    [InlineData("invalid-text")]
    public async Task PartialCopy_UnderAFrozenClock_IsNeverTakenForTheKeptCopy_ByTheNextSession(string damage)
    {
        var copies = new DiskFullCopies();
        using var fixture = DiskFullLibrary(copies);
        var (plateId, path) = await SeedPlateAsync(fixture);
        var file = File.ReadAllBytes(path);
        var damaged = damage == "damaged" ? file[..(file.Length / 2)] : WithInvalidByte(file, "\"Kept\"");
        File.WriteAllBytes(path, damaged);
        copies.FailAfterBytes = DiskRoom;

        var failed = await fixture.LoadAsync();
        if (damage == "invalid-text")
        {
            await Assert.ThrowsAsync<PlateLibraryException>(() => failed.SavePlateDocumentAsync(failed.OpenDocumentForEditing(plateId)));
        }

        AssertFailedPartway(copies, attempts: 1, damaged.Length);

        // The game dies at the fault instead: every file a copy had created then stays, holding the
        // partial bytes it held at that moment.
        foreach (var (names, length) in copies.NamesAtFault.Zip(copies.PartialLengths))
        {
            foreach (var left in names)
            {
                File.WriteAllBytes(left, damaged[..(int)length]);
            }
        }

        copies.FailAfterBytes = null;
        var next = await fixture.LoadAsync();
        await next.SavePlateDocumentAsync(next.OpenDocumentForEditing(plateId));

        // The copy under the Recovery name is complete and exact; a crash's leftover is only ever a
        // temporary file beside it.
        var name = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(path)}.damaged-{fixture.Clock.Now:yyyyMMdd-HHmmss-fff}{Path.GetExtension(path)}");
        var kept = Path.Combine(fixture.Paths.RecoveryDirectory, name);
        Assert.Equal(damaged, File.ReadAllBytes(kept));
        Assert.All(FilesIn(fixture.Paths.RecoveryDirectory).Where(f => f != kept), f => Assert.Matches(@"^\..*\.tmp$", Path.GetFileName(f)));
        Assert.NotEqual(damaged, File.ReadAllBytes(path));
        Assert.Equal(PlateStatus.Ready, (await fixture.LoadAsync()).FindPlate(plateId)!.Status);
    }
}

/// <summary>What a copy's stream saw when it was flushed to disk.</summary>
internal readonly record struct DiskFlush(long BytesWritten, bool DestinationExisted);

/// <summary>
/// Makes the copies of a <see cref="SystemFileStore"/> given <see cref="Create"/> (its test
/// constructor), each a <see cref="DiskFullAfterStream"/> that fails once
/// <see cref="FailAfterBytes"/> have reached the disk, and records what every copy did.
/// </summary>
internal sealed class DiskFullCopies
{
    /// <summary>How much each new copy may write before the disk is full; null lets every copy finish.</summary>
    internal long? FailAfterBytes { get; set; }

    /// <summary>The copy's final name, checked whenever a copy is flushed to disk.</summary>
    internal string? Destination { get; set; }

    /// <summary>Runs at the start of every write, while a copy is in progress.</summary>
    internal Action? DuringWrite { get; set; }

    /// <summary>Every file a copy created to write its bytes to, in order.</summary>
    internal List<string> CreatedPaths { get; } = new();

    /// <summary>The length on disk of each copy that failed, at the moment it failed.</summary>
    internal List<long> PartialLengths { get; } = new();

    /// <summary>The failures thrown, in order.</summary>
    internal List<IOException> Faults { get; } = new();

    /// <summary>At each failure, every file in the copy's folder: what a crash at that moment would leave.</summary>
    internal List<string[]> NamesAtFault { get; } = new();

    /// <summary>When true, a copy's flush to disk fails with the disk-full error (the data written until then stays).</summary>
    internal bool FailOnDiskFlush { get; set; }

    internal List<DiskFlush> DiskFlushes { get; } = new();

    internal FileStream Create(string path)
    {
        var stream = new DiskFullAfterStream(path, FailAfterBytes ?? long.MaxValue, this);
        CreatedPaths.Add(path);
        return stream;
    }
}

/// <summary>
/// A new file on a disk that fills up after <c>allowedBytes</c>: writes pass through until then;
/// the part of a write that still fits is written and flushed to disk, so the partial output is
/// really there, and the write throws the error Windows reports for a full disk.
/// </summary>
internal sealed class DiskFullAfterStream : FileStream
{
    private readonly long allowedBytes;
    private readonly DiskFullCopies copies;
    private long written;

    internal DiskFullAfterStream(string path, long allowedBytes, DiskFullCopies copies)
        : base(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        this.allowedBytes = allowedBytes;
        this.copies = copies;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        copies.DuringWrite?.Invoke();
        var room = allowedBytes - written;
        if (count <= room)
        {
            base.Write(buffer, offset, count);
            written += count;
            return;
        }

        base.Write(buffer, offset, (int)room);
        written += room;
        base.Flush(flushToDisk: true);

        // Asked through this handle: on Windows a file held with no sharing may report a stale
        // size by name.
        copies.PartialLengths.Add(Length);
        throw DiskFull();
    }

    private IOException DiskFull()
    {
        copies.NamesAtFault.Add(Directory.GetFiles(Path.GetDirectoryName(Name)!));
        var full = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        copies.Faults.Add(full);
        return full;
    }

    // Every other way of writing goes through the overload above, so all of them share its limit.
    public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

    public override void WriteByte(byte value) => Write([value], 0, 1);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        try
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        catch (IOException ex)
        {
            return ValueTask.FromException(ex);
        }
    }

    public override void Flush(bool flushToDisk)
    {
        if (flushToDisk)
        {
            copies.DiskFlushes.Add(new DiskFlush(written, copies.Destination is { } destination && File.Exists(destination)));
            if (copies.FailOnDiskFlush)
            {
                base.Flush(flushToDisk: false);
                throw DiskFull();
            }
        }

        base.Flush(flushToDisk);
    }
}
