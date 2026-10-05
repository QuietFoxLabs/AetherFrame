using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Recovery checkpoints on disk: the newest five of each editing kept, every one validated before an
/// older one goes, an interrupted or damaged newest one leaving an earlier one on offer, failed
/// writes retried, and several game clients sharing one folder without touching each other's work.
/// </summary>
public class RecoveryStorageTests
{
    [Fact]
    public async Task EachEditing_KeepsItsNewestFiveCheckpoints()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        for (var i = 1; i <= 8; i++)
        {
            game.Edit($"edit {i}");
            await game.Recovery.RunAsync(6);
        }

        Assert.Equal(8, game.Recovery.Files.Written.Count);
        var kept = game.Recovery.OwnCheckpoints().Select(RecoveryRig.Read).ToList();
        Assert.Equal(RecoveryCheckpointStore.KeptPerEdit, kept.Count);
        Assert.Equal(new long?[] { 8, 7, 6, 5, 4 }, kept.Select(k => k.Sequence).ToArray());
        Assert.Contains(kept[0].Document.Elements, e => e is TextProfileElement { Text: "edit 8" });
    }

    [Fact]
    public async Task AnInterruptedWrite_KeepsTheEarlierCheckpoint_AndIsOfferedFromIt()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Interrupted"));
        game.Edit("safe");
        await game.Recovery.RunAsync(6);

        // The process dies half way through the next write: only its temporary file is left.
        game.Recovery.Files.InterruptWrite = _ => true;
        game.Edit("lost with the crash");
        await game.Recovery.RunAsync(6);
        game.Recovery.Files.Crash();

        Assert.Single(Directory.GetFiles(game.Recovery.Store.SessionDirectory, "*.tmp"));
        var next = await GameSession.StartAsync(fixture);
        var offered = Assert.Single(await next.LoadKeptChangesAsync());
        Assert.Contains(offered.Draft.Document.Elements, e => e is TextProfileElement { Text: "safe" });
        Assert.DoesNotContain(offered.Draft.Document.Elements, e => e is TextProfileElement { Text: "lost with the crash" });
    }

    [Fact]
    public async Task ADamagedNewestCheckpoint_LeavesTheEarlierOnesOnOffer_AndIsLeftAsItIs()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit("one");
        await game.Recovery.RunAsync(6);
        game.Edit("two");
        await game.Recovery.RunAsync(6);
        var newest = game.Recovery.OwnCheckpoints()[0];
        game.Recovery.Files.Crash();

        // Damaged on disk after it was written (and validated).
        var bytes = File.ReadAllBytes(newest);
        File.WriteAllBytes(newest, bytes[..(bytes.Length / 2)]);

        var next = await GameSession.StartAsync(fixture);
        var offered = Assert.Single(await next.LoadKeptChangesAsync());
        Assert.Equal(1, offered.Draft.Sequence);
        Assert.Contains(offered.Draft.Document.Elements, e => e is TextProfileElement { Text: "one" });
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public async Task ACheckpointThatDoesntReadBack_IsRemoved_AndTheOlderOneStays()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit("one");
        await game.Recovery.RunAsync(6);
        var first = Assert.Single(game.Recovery.OwnCheckpoints());

        game.Recovery.Files.DamageAfterWrite = _ => true;
        game.Edit("two");
        await game.Recovery.RunAsync(1);
        await game.Recovery.RunAsync(5);

        Assert.Equal(new[] { first }, game.Recovery.OwnCheckpoints());
        Assert.Equal(RecoveryIndicatorKind.Failing, game.Recovery.Recovery.Indicator.Kind);
    }

    [Fact]
    public async Task AFailedWrite_ShowsTheWarning_IsRetried_AndThenProtects()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        await game.Recovery.FrameAsync();
        game.Edit();
        Assert.Equal(RecoveryIndicatorKind.Pending, (await IndicatorAfterAsync(game, 0.2)).Kind);

        game.Recovery.Files.FailWrite = _ => true;
        await game.Recovery.RunAsync(6);
        var failing = game.Recovery.Recovery.Indicator;
        Assert.Equal(RecoveryIndicatorKind.Failing, failing.Kind);
        Assert.Null(failing.LastCheckpointUtc);
        Assert.NotNull(failing.RetryIn);
        Assert.Contains("couldn't be written", EditorDocumentCommands.RecoveryWarning(failing), StringComparison.Ordinal);
        Assert.Empty(game.Recovery.OwnCheckpoints());

        // The disk recovers: the next retry (or Retry now) writes it.
        game.Recovery.Files.FailWrite = null;
        game.Recovery.Recovery.RetryNow();
        await game.Recovery.SettleAsync();
        await game.Recovery.SettleAsync();

        Assert.Single(game.Recovery.OwnCheckpoints());
        var protectedNow = game.Recovery.Recovery.Indicator;
        Assert.Equal(RecoveryIndicatorKind.Protected, protectedNow.Kind);
        Assert.NotNull(protectedNow.LastCheckpointUtc);
        Assert.Null(EditorDocumentCommands.RecoveryWarning(protectedNow));
    }

    [Fact]
    public async Task AFailedWrite_IsRetriedOnItsOwn_AfterAPause()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit();
        var failures = 0;
        game.Recovery.Files.FailWrite = _ => ++failures <= 2;
        await game.Recovery.RunAsync(30);

        Assert.Equal(3, failures);
        Assert.Single(game.Recovery.OwnCheckpoints());
        Assert.Equal(RecoveryIndicatorKind.Protected, game.Recovery.Recovery.Indicator.Kind);
    }

    [Fact]
    public async Task ARunningClientsCheckpoints_AreNeitherOfferedNorTouched_UntilItEnds()
    {
        using var fixture = new LibraryFixture();
        var first = await GameSession.StartAsync(fixture);
        first.Open(await first.CreatePlateAsync(name: "Still open elsewhere"));
        first.Edit();
        await first.Recovery.RunAsync(6);
        var before = Snapshot(fixture);

        // A second game client starts over the same folder long after: nothing of the first is its to offer or tidy.
        fixture.Clock.Tick(3600);
        var second = await GameSession.StartAsync(fixture);
        second.Recovery.Store.Sweep();
        Assert.Empty(await second.LoadKeptChangesAsync());
        Assert.Equal(before, Snapshot(fixture));
        Assert.True(File.Exists(PlateStoragePaths.GetRecoverySessionLockPath(first.Recovery.Store.SessionDirectory)));

        // The first then crashes: the next start offers its work.
        first.Recovery.Files.Crash();
        var third = await GameSession.StartAsync(fixture);
        Assert.Single(await third.LoadKeptChangesAsync());
    }

    [Fact]
    public async Task TwoClientsEditingOnePlate_KeepIndependentCheckpoints_AndPruneOnlyTheirOwn()
    {
        using var fixture = new LibraryFixture();
        var first = await GameSession.StartAsync(fixture);
        var plateId = await first.CreatePlateAsync();
        var second = await GameSession.StartAsync(fixture);
        first.Open(plateId);
        second.Open(plateId);

        for (var i = 1; i <= 7; i++)
        {
            first.Edit($"first {i}");
            await first.Recovery.RunAsync(6);
            second.Edit($"second {i}");
            await second.Recovery.RunAsync(6);
        }

        Assert.Equal(RecoveryCheckpointStore.KeptPerEdit, first.Recovery.OwnCheckpoints().Length);
        Assert.Equal(RecoveryCheckpointStore.KeptPerEdit, second.Recovery.OwnCheckpoints().Length);
        Assert.Equal(2 * RecoveryCheckpointStore.KeptPerEdit, KeptFiles.Checkpoints(fixture.Paths).Length);

        first.Recovery.Files.Crash();
        second.Recovery.Files.Crash();
        var next = await GameSession.StartAsync(fixture);
        var offered = await next.LoadKeptChangesAsync();
        Assert.Equal(2, offered.Count);
        Assert.Equal(2, offered.Select(o => o.Draft.SessionId).Distinct().Count());
        Assert.All(offered, o => Assert.Equal(RecoveryCheckpointStore.KeptPerEdit - 1, o.Older.Count));
    }

    [Fact]
    public async Task Sweep_RemovesOldLeftoversOfEndedRuns_AndTrimsTheTrash_ButNothingThatCanBeOffered()
    {
        using var fixture = new LibraryFixture();
        var crashed = await GameSession.StartAsync(fixture);
        crashed.Open(await crashed.CreatePlateAsync());
        crashed.Edit("offered later");
        await crashed.Recovery.RunAsync(6);
        crashed.Recovery.Files.InterruptWrite = _ => true;
        crashed.Edit("interrupted");
        await crashed.Recovery.RunAsync(6);
        crashed.Recovery.Files.Crash();
        var checkpoint = Assert.Single(crashed.Recovery.OwnCheckpoints());

        // An ended run with nothing left but its lock file.
        var empty = fixture.Paths.GetRecoverySessionDirectory(Guid.NewGuid());
        Directory.CreateDirectory(empty);
        File.WriteAllText(PlateStoragePaths.GetRecoverySessionLockPath(empty), string.Empty);

        // Sixty answered drafts in the trash.
        Directory.CreateDirectory(fixture.Paths.DraftTrashDirectory);
        for (var i = 0; i < 60; i++)
        {
            var path = Path.Combine(fixture.Paths.DraftTrashDirectory, $"{Guid.NewGuid()}.unsaved-{i:D4}.json");
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-60 + i));
        }

        // Too young to be leftovers yet: only the trash is trimmed.
        var store = new RecoveryCheckpointStore(fixture.Paths, new TestRecoveryFiles(), Guid.NewGuid(), fixture.Log, () => DateTime.UtcNow);
        store.Sweep();
        Assert.Single(Directory.GetFiles(crashed.Recovery.Store.SessionDirectory, "*.tmp"));
        Assert.True(Directory.Exists(empty));
        Assert.Equal(RecoveryCheckpointStore.KeptInTrash, KeptFiles.Trashed(fixture.Paths).Length);
        Assert.DoesNotContain(KeptFiles.Trashed(fixture.Paths), p => p.Contains(".unsaved-0000.", StringComparison.Ordinal));

        // Ten minutes on: the temporary file and the empty run go, the checkpoint stays.
        var later = new RecoveryCheckpointStore(fixture.Paths, new TestRecoveryFiles(), Guid.NewGuid(), fixture.Log, () => DateTime.UtcNow + RecoveryCheckpointStore.LeftoverAge + TimeSpan.FromMinutes(1));
        later.Sweep();
        Assert.Empty(Directory.GetFiles(crashed.Recovery.Store.SessionDirectory, "*.tmp"));
        Assert.False(Directory.Exists(empty));
        Assert.True(File.Exists(checkpoint));
        Assert.Single(await (await GameSession.StartAsync(fixture)).LoadKeptChangesAsync());
    }

    [Fact]
    public async Task ACleanUnload_RemovesItsLockAndEmptyFolder_AndKeepsWhatIsUnsaved()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit();
        await game.Recovery.RunAsync(6);

        game.Recovery.Recovery.Stop();
        game.Recovery.Store.CloseSession();

        Assert.False(File.Exists(PlateStoragePaths.GetRecoverySessionLockPath(game.Recovery.Store.SessionDirectory)));
        Assert.Single(KeptFiles.Checkpoints(fixture.Paths));
        Assert.Null(game.Recovery.Recovery.Writer.Write(RecoveryRig.Read(KeptFiles.Checkpoints(fixture.Paths)[0])));
    }

    // Every checkpoint and temporary file, hashed (a running client's lock can't be read).
    private static string[] Snapshot(LibraryFixture fixture) =>
        Directory.GetFiles(fixture.Paths.RecoverySessionsDirectory, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(PlateStoragePaths.RecoverySessionLockName, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(p => p + " " + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))))
            .ToArray();

    private static async Task<RecoveryIndicator> IndicatorAfterAsync(GameSession game, double seconds)
    {
        await game.Recovery.FrameAsync(seconds);
        return game.Recovery.Recovery.Indicator;
    }
}
