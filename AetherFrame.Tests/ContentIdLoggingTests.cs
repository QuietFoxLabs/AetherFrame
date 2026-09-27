using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A character's binding file is named after the character's Content ID, which AetherFrame keeps
/// as local binding data only: no log line may carry one, because a log is what players paste into
/// bug reports. Every way a binding file reaches the log is driven here with a Content ID of a
/// real one's size. The messages the Library writes must not name it on their own, and every
/// exception, as the plugin's log hands it to Dalamud (<see cref="LogPrivacy.ForLog"/>, applied by
/// <see cref="RedactingAetherFrameLog"/>), must not carry it either — even an I/O exception whose
/// message holds the file's full path.
/// </summary>
public class ContentIdLoggingTests
{
    private const ulong ContentId = 18_014_498_565_680_714UL;
    private static readonly string Cid = ContentId.ToString(CultureInfo.InvariantCulture);
    private static readonly CharacterContext Character = new(ContentId, "Private Hero", "Phoenix");

    // ---------------------------------------------------------------- the Library's binding paths

    [Fact]
    public async Task DamagedBinding_AtLoad_IsLoggedWithoutItsContentId()
    {
        using var fixture = new LibraryFixture();
        fixture.WriteBindingJson(ContentId, "{ \"Version\": 2, \"Pro");
        var log = new RecordingLog();

        await Load(fixture, fixture.Store, log);

        Assert.Contains(log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("a character binding file", StringComparison.Ordinal));
        AssertNoContentId(log);
    }

    [Fact]
    public async Task DamagedBinding_WithADamagedBackup_NamesNeitherInItsException()
    {
        // The store's own error names the file: "Neither '{ContentId}.json' nor its backup copy ...".
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        fixture.WriteBindingJson(ContentId, "{ damaged");
        store.Backups[fixture.Paths.GetBindingPath(ContentId)] = "{ damaged too";
        var log = new RecordingLog();

        await Load(fixture, store, log);

        Assert.Contains(log.Exceptions, e => e.Message.Contains(Cid, StringComparison.Ordinal));
        AssertNoContentId(log);
    }

    [Fact]
    public async Task LockedBinding_AtLoad_IsLoggedWithoutItsContentId_AndSetActiveStillAsksForARestart()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var seeded = await Load(fixture, store, new RecordingLog());
        var plate = await seeded.CreatePlateAsync(PlateStartingLayout.Blank, Character);
        var log = new RecordingLog();

        PlateLibraryException refused;
        using (new FileStream(fixture.Paths.GetBindingPath(ContentId), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var library = await Load(fixture, store, log);
            refused = await Assert.ThrowsAsync<PlateLibraryException>(() => library.SetActivePlateAsync(Character, plate.PlateId));
        }

        // Step 30's message to the player is unchanged; only the log lost the Content ID.
        Assert.Equal("This character's Plate settings couldn't be read when AetherFrame started. Restart the game to try again.", refused.Message);
        Assert.Contains(log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("could not open a character binding file", StringComparison.Ordinal) && m.Contains("restarted", StringComparison.Ordinal));
        Assert.Contains(log.Exceptions, e => e.Message.Contains(Cid, StringComparison.Ordinal));
        AssertNoContentId(log);
    }

    [Fact]
    public async Task NewerVersionBinding_IsLoggedWithoutItsContentId()
    {
        using var fixture = new LibraryFixture();
        fixture.WriteBindingJson(ContentId, $$"""{ "Version": 99, "ContentId": {{Cid}} }""");
        var log = new RecordingLog();

        await Load(fixture, fixture.Store, log);

        Assert.Contains(log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("newer version", StringComparison.Ordinal));
        AssertNoContentId(log);
    }

    [Fact]
    public async Task BindingReadFromItsBackup_IsLoggedWithoutItsContentId_AndStillKeptInRecovery()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var seeded = await Load(fixture, store, new RecordingLog());
        await seeded.CreatePlateAsync(PlateStartingLayout.Blank, Character);
        fixture.WriteBindingJson(ContentId, "{ \"Version\": 2, \"Pro");
        var log = new RecordingLog();

        await Load(fixture, store, log);

        Assert.Contains(log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains(LogPrivacy.CharacterBindingFile, StringComparison.Ordinal) && m.Contains("backup copy", StringComparison.Ordinal));
        AssertNoContentId(log);

        // Only the log changed: the Recovery copy is still named after the binding, as before.
        Assert.StartsWith(Cid + ".damaged-", Path.GetFileName(Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DamagedBindingReplacedByAWrite_ItsRecoveryCopy_IsLoggedWithoutItsContentId()
    {
        using var fixture = new LibraryFixture();
        fixture.WriteBindingJson(ContentId, "not json at all");
        var log = new RecordingLog();
        var library = await Load(fixture, fixture.Store, log);

        await library.CreatePlateAsync(PlateStartingLayout.Blank, Character);

        Assert.Contains(log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("kept a copy", StringComparison.Ordinal) && m.Contains(LogPrivacy.CharacterBindingFile, StringComparison.Ordinal));
        AssertNoContentId(log);
        Assert.StartsWith(Cid + ".damaged-", Path.GetFileName(Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstRunMigration_BindingWriteAndBackupFailures_AreLoggedWithoutTheContentId()
    {
        var store = new FaultInjectingStore { FailWrite = LibraryFiles.IsBinding, FailCopy = LibraryFiles.IsBinding };
        using var fixture = new LibraryFixture(store);
        var profileId = Guid.NewGuid();
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, ContentId));
        fixture.WriteBindingJson(ContentId, LegacyData.VersionOneBinding(ContentId, profileId, profileId));
        var log = new RecordingLog();

        var library = await Load(fixture, store, log);

        Assert.Equal(profileId, library.GetActivePlateId(ContentId));
        Assert.Contains(log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("could not back up a character binding file", StringComparison.Ordinal));
        Assert.Contains(log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("could not write a character binding file", StringComparison.Ordinal));
        Assert.Contains(log.Exceptions, e => e.Message.Contains(Cid, StringComparison.Ordinal));
        AssertNoContentId(log);
    }

    [Fact]
    public async Task BindingWriteFailures_OnCreateAndDelete_AreLoggedWithoutTheContentId()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var log = new RecordingLog();
        var library = await Load(fixture, store, log);
        var linked = await library.CreatePlateAsync(PlateStartingLayout.Blank, Character);
        store.FailWrite = LibraryFiles.IsBinding;

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.CreatePlateAsync(PlateStartingLayout.Blank, Character));
        await library.DeletePlateAsync(linked.PlateId);

        Assert.Equal(2, log.Exceptions.Count(e => e.Message.Contains(Cid, StringComparison.Ordinal)));
        AssertNoContentId(log);
    }

    [Fact]
    public async Task ABindingSavedOverAStuckTemporaryFile_IsReportedWithoutItsContentId()
    {
        var inner = new FaultInjectingStore { FailWrite = LibraryFiles.IsBinding };
        using var fixture = new LibraryFixture(inner);
        var log = new RecordingLog();
        var library = await Load(fixture, new StuckTempFallbackFileStore(inner, log), log);
        var bindingPath = fixture.Paths.GetBindingPath(ContentId);
        Directory.CreateDirectory(Path.GetDirectoryName(bindingPath)!);

        using (new FileStream(StuckTempFallbackFileStore.TemporaryPathFor(bindingPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await library.CreatePlateAsync(PlateStartingLayout.Blank, Character);
        }

        Assert.True(File.Exists(bindingPath));
        Assert.Contains(log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains($"\"{LogPrivacy.CharacterBindingFile}\"", StringComparison.Ordinal) && m.Contains("stuck open", StringComparison.Ordinal));
        AssertNoContentId(log);
    }

    // ---------------------------------------------------------------- the redaction itself

    [Theory]
    [InlineData("18014498565680714.json")]
    [InlineData("1001.json")]
    [InlineData(@"C:\Users\Someone\AppData\Roaming\XIVLauncher\pluginConfigs\AetherFrame\Characters\18014498565680714.json")]
    [InlineData("The process cannot access the file 'C:\\Game Data\\AetherFrame\\Characters\\18014498565680714.json' because it is being used by another process.")]
    [InlineData("/home/player/AetherFrame/Characters/18014498565680714.json")]
    [InlineData("18014498565680714.damaged-20260927-010203-004.json")]
    [InlineData(@"MigrationBackups\Characters\18014498565680714.json")]
    [InlineData("18014498565680714.json.tmp")]
    [InlineData(@"Characters\.18014498565680714.json.0f8fad5bd9cb469fa165e3c7b4c1a7a2.tmp")]
    [InlineData("Neither '18014498565680714.json' nor its backup copy could be read: bad")]
    [InlineData("(18014498565680714.json)")]
    public void Redact_RemovesEveryFileNamedByANumber(string text)
    {
        var redacted = LogPrivacy.Redact(text);

        Assert.DoesNotContain("1001", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Cid, redacted, StringComparison.Ordinal);
        Assert.Contains(LogPrivacy.CharacterBindingFile, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("694ec66d-4fd6-4303-8ae4-99f2a81fe3fd.json")]
    [InlineData("12345678-1234-1234-1234-123456789012.json")]
    [InlineData(@"Profiles\12345678-1234-1234-1234-123456789012.damaged-20260927-010203-004.json")]
    [InlineData(".694ec66d-4fd6-4303-8ae4-99f2a81fe3fd.png.0f8fad5bd9cb469fa165e3c7b4c1a7a2.tmp")]
    [InlineData("0123456789abcdef0123456789abcdef.png")]
    [InlineData("library.json")]
    [InlineData("AetherFrame.json")]
    [InlineData("step7-mono-1024.aetherframe")]
    [InlineData("The image is a 16-bit PNG with 33.6 megapixels.")]
    [InlineData("AetherFrame 0.1.5 (build ab26da0) loaded")]
    [InlineData("Plate Library loaded: 100 Plate(s), 2 character binding(s).")]
    public void Redact_LeavesEveryOtherNameAndNumberAlone(string text)
    {
        Assert.Equal(text, LogPrivacy.Redact(text));
    }

    [Fact]
    public void FileName_ShowsABindingGenerically_AndAnyOtherFileByName()
    {
        var root = Path.Combine("pluginConfigs", "AetherFrame");
        var paths = new PlateStoragePaths(root);
        var plateId = Guid.NewGuid();

        Assert.Equal(LogPrivacy.CharacterBindingFile, LogPrivacy.FileName(paths.GetBindingPath(ContentId)));
        Assert.Equal(LogPrivacy.CharacterBindingFile, LogPrivacy.FileName(paths.GetRecoveryPath(paths.GetBindingPath(ContentId), DateTime.UtcNow)));
        Assert.Equal($"{plateId}.json", LogPrivacy.FileName(paths.GetPlatePath(plateId)));
        Assert.Equal("library.json", LogPrivacy.FileName(paths.LibraryFile));
    }

    [Fact]
    public void ForLog_KeepsAnExceptionThatNamesNoBinding_AsItIs()
    {
        var unrelated = new IOException(@"The process cannot access the file 'C:\x\Profiles\694ec66d-4fd6-4303-8ae4-99f2a81fe3fd.json'.");

        Assert.Same(unrelated, LogPrivacy.ForLog(unrelated));
        Assert.Null(LogPrivacy.ForLog(null));
    }

    [Fact]
    public void ForLog_RedactsTheMessageAndInnerExceptions_KeepingTypeResultAndStackTrace()
    {
        var path = new PlateStoragePaths(Path.Combine("C:", "pluginConfigs", "AetherFrame")).GetBindingPath(ContentId);
        Exception original;
        try
        {
            try
            {
                throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            }
            catch (Exception inner)
            {
                throw new IOException($"Couldn't write '{Path.GetFileName(path)}'.", inner) { HResult = unchecked((int)0x80070005) };
            }
        }
        catch (Exception ex)
        {
            original = ex;
        }

        var logged = LogPrivacy.ForLog(original);
        var text = logged.ToString();

        Assert.NotSame(original, logged);
        Assert.DoesNotContain(Cid, text, StringComparison.Ordinal);
        Assert.StartsWith(typeof(IOException).FullName + ": Couldn't write '" + LogPrivacy.CharacterBindingFile + "'.", text, StringComparison.Ordinal);
        Assert.Contains(typeof(UnauthorizedAccessException).FullName!, text, StringComparison.Ordinal);
        Assert.Contains(nameof(ForLog_RedactsTheMessageAndInnerExceptions_KeepingTypeResultAndStackTrace), text, StringComparison.Ordinal);
        Assert.Equal(original.HResult, logged.HResult);
        Assert.DoesNotContain(Cid, logged.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactingLog_RedactsEveryMessageAndException_BeforeTheyReachTheSink()
    {
        var sink = new RecordingLog();
        var log = new RedactingAetherFrameLog(sink);

        log.Information($"read {Cid}.json");
        log.Warning($"copied {Cid}.json");
        log.Error(new IOException($"'{Cid}.json' is locked"), $"could not open {Cid}.json");
        log.Error(null, "no exception");

        Assert.Equal(4, sink.Messages.Count);
        Assert.All(sink.Messages, m => Assert.DoesNotContain(Cid, m, StringComparison.Ordinal));
        Assert.Equal($"E could not open {LogPrivacy.CharacterBindingFile}", sink.Messages[2]);
        Assert.Equal("E no exception", sink.Messages[^1]);
        var exception = Assert.Single(sink.Exceptions).ToString();
        Assert.DoesNotContain(Cid, exception, StringComparison.Ordinal);
        Assert.StartsWith($"{typeof(IOException).FullName}: '{LogPrivacy.CharacterBindingFile}' is locked", exception, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<PlateLibraryService> Load(LibraryFixture fixture, IPlateFileStore store, RecordingLog log)
    {
        var library = new PlateLibraryService(fixture.Paths, store, log, () => fixture.Clock.Now);
        await library.InitializeAsync();
        return library;
    }

    /// <summary>
    /// No message names the Content ID, and no exception does once the plugin's log front has
    /// passed it (<see cref="LogPrivacy.ForLog"/>), which keeps the exception's type.
    /// </summary>
    private static void AssertNoContentId(RecordingLog log)
    {
        Assert.All(log.Messages, m => Assert.DoesNotContain(Cid, m, StringComparison.Ordinal));
        Assert.All(log.Exceptions, e =>
        {
            var logged = LogPrivacy.ForLog(e).ToString();
            Assert.DoesNotContain(Cid, logged, StringComparison.Ordinal);
            Assert.Contains(e.GetType().FullName!, logged, StringComparison.Ordinal);
        });
    }

    /// <summary>Every message, and every exception as it was handed over.</summary>
    private sealed class RecordingLog : IAetherFrameLog
    {
        internal List<string> Messages { get; } = [];

        internal List<Exception> Exceptions { get; } = [];

        public void Information(string message) => Messages.Add("I " + message);

        public void Warning(string message) => Messages.Add("W " + message);

        public void Error(Exception? exception, string message)
        {
            Messages.Add("E " + message);
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
    }
}
