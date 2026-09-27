using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// <see cref="ReliableReads"/>, the read the in-game store makes over Dalamud's reliable storage:
/// the file on disk comes first, the backup only answers content the reader rejects (through the
/// reader's second run, which is what the loaders report as recovered), and a file that can't be
/// read at all is an I/O failure — never, silently, a possibly older backup.
/// </summary>
public class ReliableReadsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aetherframe-tests", Guid.NewGuid().ToString("N"));
    private readonly string path;
    private int backupReads;

    public ReliableReadsTests()
    {
        Directory.CreateDirectory(root);
        path = Path.Combine(root, "file.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task IntactFile_IsReadOnce_AndTheBackupIsNeverAsked()
    {
        File.WriteAllText(path, "on disk");
        var seen = new System.Collections.Generic.List<string>();

        await ReliableReads.ReadTextAsync(path, seen.Add, Backup("stale backup"));

        Assert.Equal(["on disk"], seen);
        Assert.Equal(0, backupReads);
    }

    [Fact]
    public async Task RejectedFile_IsReadAgainFromTheBackup_OnTheReadersSecondRun()
    {
        File.WriteAllText(path, "{ truncated");
        var seen = new System.Collections.Generic.List<string>();

        await ReliableReads.ReadTextAsync(path, text =>
        {
            seen.Add(text);
            if (text.StartsWith('{'))
            {
                throw new InvalidDataException("unusable");
            }
        }, Backup("from backup"));

        Assert.Equal(["{ truncated", "from backup"], seen);
        Assert.Equal(1, backupReads);
        Assert.Equal("{ truncated", File.ReadAllText(path)); // the read never rewrites the file
    }

    [Fact]
    public async Task RejectedFile_WithoutABackup_IsContentDamage_WithTheReadersOwnVerdictInside()
    {
        File.WriteAllText(path, "{ truncated");
        var verdict = new InvalidDataException("Plate is unreadable.");

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => ReliableReads.ReadTextAsync(path, _ => throw verdict, NoBackup()));

        Assert.Same(verdict, failure.InnerException);
        Assert.Equal(verdict.Message, failure.Message);
        Assert.Equal(1, backupReads);
    }

    [Fact]
    public async Task RejectedFile_AndRejectedBackup_IsContentDamage_NamingTheFileOnly()
    {
        File.WriteAllText(path, "{ truncated");
        var runs = 0;

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => ReliableReads.ReadTextAsync(path, _ =>
        {
            runs++;
            throw new InvalidDataException("unusable");
        }, Backup("{ also truncated")));

        Assert.Equal(2, runs);
        Assert.Contains("file.json", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(root, failure.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidDataException>(failure.InnerException);
    }

    [Fact]
    public async Task MissingFile_IsUnavailable_EvenWhenABackupExists()
    {
        var runs = 0;

        await Assert.ThrowsAsync<FileNotFoundException>(() => ReliableReads.ReadTextAsync(path, _ => runs++, Backup("stale backup")));

        Assert.Equal(0, runs);
        Assert.Equal(0, backupReads);
    }

    [Fact]
    public async Task FileThatFailsToRead_IsUnavailable_NotSilentlyTheBackup()
    {
        // A folder where the file should be: the read fails at the I/O level, not on content.
        Directory.CreateDirectory(path);
        var runs = 0;

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => ReliableReads.ReadTextAsync(path, _ => runs++, Backup("stale backup")));

        Assert.True(failure is IOException or UnauthorizedAccessException, failure.GetType().Name);
        Assert.Equal(0, runs);
        Assert.Equal(0, backupReads);
    }

    [Fact]
    public async Task ThroughTheLoader_ADamagedFile_IsReportedAsRecovered_AndAMissingOneIsNot()
    {
        var plateId = Guid.NewGuid();
        var backup = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "From backup", DateTime.UtcNow), JsonOptions.Default);
        var store = new BackupSimulatingStore();
        store.Backups[path] = backup;

        File.WriteAllText(path, "{ truncated");
        var recovered = await VersionedJson.ReadAsync(store, path, PersistenceSchemas.ProfileDocument, PlateDocuments.Deserialize);
        Assert.True(recovered.RecoveredFromBackup);
        Assert.Equal("From backup", recovered.Value!.Name);

        File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => VersionedJson.ReadAsync(store, path, PersistenceSchemas.ProfileDocument, PlateDocuments.Deserialize));
    }

    private Func<Task<string>> Backup(string contents) => () =>
    {
        backupReads++;
        return Task.FromResult(contents);
    };

    private Func<Task<string>> NoBackup() => () =>
    {
        backupReads++;
        throw new FileNotFoundException("no backup row", path);
    };
}
