using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;

namespace AetherFrame.Tests;

/// <summary>
/// The test assembly's entry point. The test runner never calls it: <see cref="RecoveryProcessTests"/>
/// starts this assembly as a process of its own with <c>--recovery-crash-host</c>, so continuous
/// recovery runs for real over a temporary Library, and then kills that process.
/// </summary>
public static class Program
{
    public static int Main(string[] args) =>
        args is [RecoveryCrashHost.Argument, var root, var plateId, var readyFile]
            ? RecoveryCrashHost.Run(root, Guid.Parse(plateId), readyFile)
            : 0;
}

/// <summary>
/// A disposable game session in its own process: it opens one Plate of the temporary Library at
/// <c>root</c> in the Advanced editor and adds a text element every <see cref="EditEvery"/>, never
/// saving, while continuous recovery checkpoints it on real files and a real lock. Its clock runs
/// <see cref="TimeScale"/> times fast, so a checkpoint is due shortly after each edit and writes and
/// pruning go on all the time. Once the editing holds <see cref="ReadyAfter"/> checkpoints it creates
/// <c>readyFile</c>, and it goes on until it is killed.
/// </summary>
internal static class RecoveryCrashHost
{
    internal const string Argument = "--recovery-crash-host";

    internal const double TimeScale = 20;

    internal const int ReadyAfter = 3;

    internal static readonly TimeSpan EditEvery = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(2);

    internal static int Run(string root, Guid plateId, string readyFile)
    {
        var paths = new PlateStoragePaths(root);
        var log = new TestLog();
        var library = new PlateLibraryService(paths, new SystemFileStore(), log, () => DateTime.UtcNow);
        library.InitializeAsync().GetAwaiter().GetResult();
        var profiles = new ProfileService(library);
        var assets = new AssetStorageService(paths.AssetsDirectory, paths.AssetStagingDirectory, new AssetMetadataStore(paths.AssetMetadataDirectory));
        var frame = 0;
        var session = new EditorSession(profiles, assets, new FakeImages(), log, () => ++frame);
        var clock = Stopwatch.StartNew();
        var store = new RecoveryCheckpointStore(paths, SystemRecoveryFiles.Instance, Guid.NewGuid(), log);
        var recovery = new ContinuousRecovery(profiles, session, () => EditorSurfaceKind.Advanced, store, "AetherFrame crash host", log, () => clock.Elapsed * TimeScale);

        profiles.OpenPlate(plateId);
        session.SyncWithCurrentProfile();
        var edits = 0;
        var nextEdit = TimeSpan.Zero;
        var signalled = false;
        while (clock.Elapsed < GiveUpAfter)
        {
            if (clock.Elapsed >= nextEdit)
            {
                session.AddTextElement($"edit {++edits}");
                nextEdit = clock.Elapsed + EditEvery;
            }

            session.SyncWithCurrentProfile();
            recovery.Tick();
            if (!signalled && recovery.CurrentEditId is { } editId && store.CountOwn(plateId, editId) >= ReadyAfter)
            {
                File.WriteAllText(readyFile + ".tmp", edits.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.Move(readyFile + ".tmp", readyFile);
                signalled = true;
            }

            Thread.Sleep(10);
        }

        Console.Error.WriteLine("The recovery crash host was never killed.");
        return 2;
    }
}
