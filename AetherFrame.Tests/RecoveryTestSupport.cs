using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Continuous recovery as the plugin wires it, over one editor session, on an elapsed-time clock the
/// test moves: <see cref="FrameAsync"/> is one editor frame (the session syncs, recovery ticks), then
/// waits for whatever the writer was asked to do, so every test reads a settled folder.
/// </summary>
internal sealed class RecoveryRig
{
    private readonly EditorSession session;

    internal RecoveryRig(
        PlateStoragePaths paths, ProfileService profiles, EditorSession session, Func<EditorSurfaceKind?> surface, IAetherFrameLog log,
        TestRecoveryFiles? files = null, Func<DateTime>? utcNow = null, Guid? sessionId = null)
    {
        this.session = session;
        Files = files ?? new TestRecoveryFiles();
        Store = new RecoveryCheckpointStore(paths, Files, sessionId ?? Guid.NewGuid(), log, utcNow);
        Recovery = new ContinuousRecovery(profiles, session, surface, Store, "AetherFrame test", log, () => Now, utcNow);
    }

    internal TestRecoveryFiles Files { get; }

    internal RecoveryCheckpointStore Store { get; }

    internal ContinuousRecovery Recovery { get; }

    /// <summary>The elapsed time recovery schedules by.</summary>
    internal TimeSpan Now { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>One frame after <paramref name="seconds"/> pass, then the writer settled.</summary>
    internal async Task FrameAsync(double seconds = 0)
    {
        Now += TimeSpan.FromSeconds(seconds);
        session.SyncWithCurrentProfile();
        Recovery.Tick();
        Assert.True(await Recovery.Writer.WaitIdleAsync(TimeSpan.FromSeconds(10)), "The recovery writer didn't settle.");
    }

    /// <summary>Frames every <paramref name="step"/> seconds for <paramref name="seconds"/>, calling <paramref name="each"/> before each one.</summary>
    internal async Task RunAsync(double seconds, double step = 0.1, Action? each = null)
    {
        for (var t = 0.0; t < seconds - 1e-9; t += step)
        {
            each?.Invoke();
            await FrameAsync(step);
        }
    }

    /// <summary>A frame once the writer already settled, so its outcome is applied.</summary>
    internal Task SettleAsync() => FrameAsync(0);

    /// <summary>This run's checkpoint files, newest first.</summary>
    internal string[] OwnCheckpoints() =>
        Files.ListFiles(Store.SessionDirectory, "*.json").OrderByDescending(p => p, StringComparer.Ordinal).ToArray();

    /// <summary>A checkpoint file as read back.</summary>
    internal static PlateDraft Read(string path) => DraftDocuments.Parse(File.ReadAllText(path)).Draft!;
}

/// <summary>
/// The recovery checkpoints' plain files, with failures and holds to inject, a count of writes that
/// reached the disk, and <see cref="Crash"/>, which lets go of this run's lock the way a killed
/// process does: without tidying anything.
/// </summary>
internal sealed class TestRecoveryFiles : IRecoveryFiles
{
    private readonly SystemRecoveryFiles files = new();
    private readonly List<IDisposable> locks = new();
    private readonly object gate = new();
    private ManualResetEventSlim? hold;

    /// <summary>Writes that reached the disk, in order.</summary>
    internal List<string> Written { get; } = new();

    /// <summary>Writes whose path matches fail before anything is written (a full disk).</summary>
    internal Func<string, bool>? FailWrite { get; set; }

    /// <summary>Writes whose path matches leave only their temporary file, half written, as a process killed mid-write does.</summary>
    internal Func<string, bool>? InterruptWrite { get; set; }

    /// <summary>Writes whose path matches land, then their bytes are damaged before they are read back.</summary>
    internal Func<string, bool>? DamageAfterWrite { get; set; }

    /// <summary>Deletes whose path matches fail.</summary>
    internal Func<string, bool>? FailDelete { get; set; }

    /// <summary>Taking a run's lock fails (no permission on the folder, say).</summary>
    internal bool FailLock { get; set; }

    /// <summary>Listing folders throws, as a Sessions folder that can't be listed would.</summary>
    internal bool FailListDirectories { get; set; }

    /// <summary>Signalled when a held write has started.</summary>
    internal ManualResetEventSlim WriteStarted { get; } = new();

    /// <summary>The next writes wait until <see cref="Release"/>.</summary>
    internal void Hold()
    {
        WriteStarted.Reset();
        hold = new ManualResetEventSlim(false);
    }

    internal void Release() => hold?.Set();

    /// <summary>Lets go of every lock this run holds, leaving its files as they are: what a crash does.</summary>
    internal void Crash()
    {
        lock (gate)
        {
            foreach (var held in locks)
            {
                held.Dispose();
            }

            locks.Clear();
        }
    }

    public IReadOnlyList<string> ListFiles(string directory, string pattern) => files.ListFiles(directory, pattern);

    public IReadOnlyList<string> ListDirectories(string directory) =>
        FailListDirectories ? throw new IOException($"Injected listing failure: '{directory}'") : files.ListDirectories(directory);

    public void WriteNew(string path, byte[] contents)
    {
        if (hold is { } waiting)
        {
            WriteStarted.Set();
            waiting.Wait(TimeSpan.FromSeconds(30));
            hold = null;
        }

        if (FailWrite?.Invoke(path) == true)
        {
            throw new IOException($"Injected write failure: '{path}'");
        }

        if (InterruptWrite?.Invoke(path) == true)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"), contents[..(contents.Length / 2)]);
            throw new IOException($"Injected interruption: '{path}'");
        }

        files.WriteNew(path, contents);
        lock (gate)
        {
            Written.Add(path);
        }

        if (DamageAfterWrite?.Invoke(path) == true)
        {
            File.WriteAllBytes(path, contents[..(contents.Length / 3)]);
        }
    }

    public StoredText ReadText(string path) => files.ReadText(path);

    public bool FileExists(string path) => files.FileExists(path);

    public void DeleteFile(string path)
    {
        if (FailDelete?.Invoke(path) == true)
        {
            throw new IOException($"Injected delete failure: '{path}'");
        }

        files.DeleteFile(path);
    }

    public bool TryDeleteEmptyDirectory(string directory) => files.TryDeleteEmptyDirectory(directory);

    public DateTime GetLastWriteTimeUtc(string path) => files.GetLastWriteTimeUtc(path);

    public DateTime GetDirectoryCreationTimeUtc(string directory) => files.GetDirectoryCreationTimeUtc(directory);

    public IDisposable HoldLock(string lockPath)
    {
        if (FailLock)
        {
            throw new UnauthorizedAccessException($"Injected lock failure: '{lockPath}'");
        }

        var held = files.HoldLock(lockPath);
        lock (gate)
        {
            locks.Add(held);
        }

        return new LockRelease(this, held);
    }

    public bool IsLockHeld(string lockPath) => files.IsLockHeld(lockPath);

    private sealed class LockRelease(TestRecoveryFiles owner, IDisposable held) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.locks.Remove(held);
            }

            held.Dispose();
        }
    }
}
