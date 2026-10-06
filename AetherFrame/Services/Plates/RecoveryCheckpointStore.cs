using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Plates;

/// <summary>One recovery checkpoint file of a run that has ended, as the next load finds it.</summary>
internal sealed record CheckpointFile(string Path, Guid SessionId, Guid PlateId, Guid EditId, long Sequence);

/// <summary>
/// Where recovery checkpoints live while a Plate is edited, so a crash leaves the unsaved changes
/// behind (<see cref="PlateStoragePaths.RecoverySessionsDirectory"/>). Each run of AetherFrame in a
/// game client has a folder of its own, named for its session id, created with the first checkpoint
/// and holding a lock file the run keeps open, unshared, until it unloads: a folder whose lock is held
/// belongs to a client still running, and nothing else reads, offers, claims or prunes it. Free of Dalamud.
///
/// <para><b>Writing.</b> A checkpoint is a <see cref="PlateDraft"/> under a name of its own (its Plate,
/// its editing and a sequence that grows, see <see cref="PlateStoragePaths.GetCheckpointPath"/>), never
/// written over. Its text passes the proof every draft write passes, goes to a flushed temporary file
/// renamed into place, and is read back from disk and parsed before anything older is retired. A write
/// that fails or reads back wrong leaves the earlier checkpoints exactly as they were.</para>
///
/// <para><b>Retention.</b> Each editing keeps its newest <see cref="KeptPerEdit"/> checkpoints; older
/// ones are deleted only after a newer one read back intact. Only this run's own files are ever
/// pruned or retired; another run's, an older version's and every unanswered draft stay.</para>
///
/// <para>Every method may run on any thread, but one writer at a time (see <see cref="RecoveryCheckpointWriter"/>).</para>
/// </summary>
internal sealed class RecoveryCheckpointStore
{
    /// <summary>Checkpoints kept for one editing of one Plate.</summary>
    internal const int KeptPerEdit = 5;

    /// <summary>Answered drafts and checkpoints kept in the Drafts trash; older ones are deleted at load.</summary>
    internal const int KeptInTrash = 50;

    /// <summary>How old an interrupted write's temporary file, or an empty folder, must be before a load removes it.</summary>
    internal static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

    private readonly PlateStoragePaths paths;
    private readonly IRecoveryFiles files;
    private readonly IAetherFrameLog log;
    private readonly Func<DateTime> utcNow;
    private readonly object gate = new();

    private IDisposable? sessionLock;
    private bool closed;

    internal RecoveryCheckpointStore(PlateStoragePaths paths, IRecoveryFiles files, Guid sessionId, IAetherFrameLog? log = null, Func<DateTime>? utcNow = null)
    {
        this.paths = paths;
        this.files = files;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        SessionId = sessionId;
        SessionDirectory = paths.GetRecoverySessionDirectory(sessionId);
    }

    /// <summary>This run's id, in every checkpoint it writes and in its folder's name.</summary>
    internal Guid SessionId { get; }

    internal string SessionDirectory { get; }

    internal PlateStoragePaths Paths => paths;

    internal IRecoveryFiles Files => files;

    /// <summary>
    /// Writes <paramref name="checkpoint"/> (its session, editing and sequence set) as a new file, reads
    /// it back, and only then prunes its editing to the newest <see cref="KeptPerEdit"/>. Returns its
    /// path. Throws when it can't be written or doesn't read back intact; nothing older is touched then,
    /// and a file that read back wrong is removed again.
    /// </summary>
    internal string Write(PlateDraft checkpoint)
    {
        if (checkpoint.SessionId != SessionId || checkpoint.EditId is not { } editId || checkpoint.Sequence is not { } sequence)
        {
            throw new ArgumentException("A recovery checkpoint names this run, its editing and its sequence.", nameof(checkpoint));
        }

        OpenSession();
        var path = paths.GetCheckpointPath(SessionId, checkpoint.PlateId, editId, sequence);
        if (files.FileExists(path))
        {
            throw new IOException($"AetherFrame's recovery checkpoint {LogPrivacy.FileName(path)} exists already.");
        }

        if (DraftStore.Prove(checkpoint, log) is not { } json)
        {
            throw new InvalidDataException("The recovery checkpoint couldn't be read back before it was written.");
        }

        files.WriteNew(path, Encoding.UTF8.GetBytes(json));
        try
        {
            if (Read(path, SessionId, checkpoint.PlateId, editId, sequence) is not { DraftId: var readId } || readId != checkpoint.DraftId)
            {
                throw new InvalidDataException("It read back as something else.");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            throw new InvalidDataException($"AetherFrame's recovery checkpoint didn't read back intact: {ex.Message}", ex);
        }

        Prune(checkpoint.PlateId, editId);
        return path;
    }

    /// <summary>
    /// Deletes every checkpoint of one editing in this run's folder: it was saved, discarded or handed
    /// on, so none of them may be offered. Throws when one can't be deleted (it is retried).
    /// </summary>
    internal void Retire(Guid plateId, Guid editId)
    {
        foreach (var file in OwnCheckpoints().Where(f => f.PlateId == plateId && f.EditId == editId))
        {
            files.DeleteFile(file.Path);
        }
    }

    /// <summary>How many checkpoints this run's folder holds for one editing.</summary>
    internal int CountOwn(Guid plateId, Guid editId) => OwnCheckpoints().Count(f => f.PlateId == plateId && f.EditId == editId);

    /// <summary>
    /// Ends this run: no more checkpoints, the lock goes, and its folder is removed when nothing is
    /// left in it. Checkpoints still there stay for the next load to offer.
    /// </summary>
    internal void CloseSession()
    {
        lock (gate)
        {
            closed = true;
            if (sessionLock is null)
            {
                return;
            }

            sessionLock.Dispose();
            sessionLock = null;
        }

        try
        {
            files.DeleteFile(PlateStoragePaths.GetRecoverySessionLockPath(SessionDirectory));
            files.TryDeleteEmptyDirectory(SessionDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame couldn't tidy its recovery folder as it unloaded: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Every checkpoint of runs that have ended (their lock not held), and, separately, how many runs
    /// are still going: their folders are left alone. This run's own folder is never listed.
    /// <c>Complete</c> is false when the Sessions folder or an ended run's folder couldn't be listed, or
    /// whether a run's lock is held couldn't be told: what it holds is then unknown, and a later load
    /// looks again.
    /// </summary>
    internal (IReadOnlyList<CheckpointFile> Files, int RunningSessions, bool Complete) ListEndedSessions()
    {
        var found = new List<CheckpointFile>();
        var running = 0;
        var folders = ListRunFolders();
        var complete = folders is not null;
        foreach (var directory in folders ?? [])
        {
            if (!PlateStoragePaths.TryParseRecoverySessionDirectoryName(directory, out var sessionId) || sessionId == SessionId)
            {
                continue;
            }

            try
            {
                if (files.IsLockHeld(PlateStoragePaths.GetRecoverySessionLockPath(directory)))
                {
                    running++;
                    continue;
                }

                found.AddRange(ListCheckpoints(directory, sessionId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another game client removed it as it unloaded, or it can't be listed now: a later load looks again.
                log.Warning($"AetherFrame couldn't list a recovery folder: {ex.GetType().Name}.");
                complete = false;
            }
        }

        return (found, running, complete);
    }

    /// <summary>
    /// Reads one checkpoint: the draft, or null for a newer version's (left as it is). Damage, bytes
    /// that aren't valid text, or content that doesn't match its name throw <see cref="InvalidDataException"/>;
    /// a file that can't be opened throws as it is.
    /// </summary>
    internal (PlateDraft Draft, string DocumentJson)? ReadForOffer(CheckpointFile file)
    {
        var text = files.ReadText(file.Path);
        if (text.HasInvalidBytes)
        {
            throw new InvalidDataException($"The recovery checkpoint holds bytes that aren't valid {text.EncodingName}.");
        }

        var parsed = DraftDocuments.Parse(text.Text);
        if (parsed.Status == DraftTextStatus.NewerVersion)
        {
            return null;
        }

        var draft = parsed.Draft!;
        RequireName(draft, file.SessionId, file.PlateId, file.EditId, file.Sequence);
        return (draft, parsed.DocumentJson!);
    }

    /// <summary>
    /// What a load tidies, never touching a running client's folder or anything that could be offered:
    /// an interrupted write's temporary files and an ended run's lock file once they are
    /// <see cref="LeftoverAge"/> old, ended runs' folders left empty, and the Drafts trash beyond its
    /// newest <see cref="KeptInTrash"/> files. Failures are logged and left for a later load.
    /// </summary>
    internal void Sweep()
    {
        var now = utcNow();
        foreach (var directory in ListRunFolders() ?? [])
        {
            try
            {
                if (!PlateStoragePaths.TryParseRecoverySessionDirectoryName(directory, out var sessionId) || sessionId == SessionId)
                {
                    continue;
                }

                var lockPath = PlateStoragePaths.GetRecoverySessionLockPath(directory);
                if (files.IsLockHeld(lockPath))
                {
                    continue;
                }

                foreach (var leftover in files.ListFiles(directory, "*.tmp"))
                {
                    if (now - files.GetLastWriteTimeUtc(leftover) >= LeftoverAge)
                    {
                        files.DeleteFile(leftover);
                    }
                }

                if (files.FileExists(lockPath) && now - files.GetLastWriteTimeUtc(lockPath) >= LeftoverAge
                    && files.ListFiles(directory, "*").All(f => string.Equals(f, lockPath, StringComparison.OrdinalIgnoreCase)))
                {
                    files.DeleteFile(lockPath);
                }

                if (now - files.GetDirectoryCreationTimeUtc(directory) >= LeftoverAge)
                {
                    files.TryDeleteEmptyDirectory(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warning($"AetherFrame couldn't tidy a recovery folder: {ex.GetType().Name}.");
            }
        }

        try
        {
            var trashed = files.ListFiles(paths.DraftTrashDirectory, "*.json")
                .Select(f => (Path: f, Written: files.GetLastWriteTimeUtc(f)))
                .OrderByDescending(f => f.Written)
                .ThenByDescending(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Skip(KeptInTrash)
                .ToList();
            foreach (var (path, _) in trashed)
            {
                files.DeleteFile(path);
            }

            if (trashed.Count > 0)
            {
                log.Information($"AetherFrame removed the {trashed.Count} oldest answered unsaved changes from its Trash folder, which keeps the newest {KeptInTrash}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame couldn't trim its Drafts trash: {ex.GetType().Name}.");
        }
    }

    // Every run's folder; null, logged, when the Sessions folder can't be listed now (a later load looks again).
    private IReadOnlyList<string>? ListRunFolders()
    {
        try
        {
            return files.ListDirectories(paths.RecoverySessionsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame couldn't list its recovery folders: {ex.GetType().Name}.");
            return null;
        }
    }

    /// <summary>This run's folder and lock, made with its first checkpoint. A lock that can't be taken fails the write, which is retried.</summary>
    private void OpenSession()
    {
        lock (gate)
        {
            if (closed)
            {
                throw new InvalidOperationException("AetherFrame is unloading: no more recovery checkpoints.");
            }

            if (sessionLock is not null)
            {
                return;
            }

            var lockPath = PlateStoragePaths.GetRecoverySessionLockPath(SessionDirectory);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    sessionLock = files.HoldLock(lockPath);
                    return;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 3)
                {
                    // Another client's load may be removing an empty folder of this name's parent at this moment: try again.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unlocked, another game client would take this run's checkpoints for a crashed one's: none are written.
                    throw new IOException("AetherFrame couldn't lock its recovery folder, so it writes no recovery checkpoint until it can.", ex);
                }
            }
        }
    }

    private IEnumerable<CheckpointFile> OwnCheckpoints() => ListCheckpoints(SessionDirectory, SessionId);

    private List<CheckpointFile> ListCheckpoints(string directory, Guid sessionId)
    {
        var found = new List<CheckpointFile>();
        foreach (var path in files.ListFiles(directory, "*.json"))
        {
            if (PlateStoragePaths.TryParseCheckpointFileName(path, out var plateId, out var editId, out var sequence))
            {
                found.Add(new CheckpointFile(path, sessionId, plateId, editId, sequence));
            }
        }

        return found;
    }

    /// <summary>The newest <see cref="KeptPerEdit"/> of one editing stay; deleting an older one that fails is logged, and the next checkpoint tries again.</summary>
    private void Prune(Guid plateId, Guid editId)
    {
        var older = OwnCheckpoints()
            .Where(f => f.PlateId == plateId && f.EditId == editId)
            .OrderByDescending(f => f.Sequence)
            .Skip(KeptPerEdit)
            .ToList();
        foreach (var file in older)
        {
            TryDelete(file.Path);
        }
    }

    private PlateDraft? Read(string path, Guid sessionId, Guid plateId, Guid editId, long sequence) =>
        ReadForOffer(new CheckpointFile(path, sessionId, plateId, editId, sequence))?.Draft;

    private static void RequireName(PlateDraft draft, Guid sessionId, Guid plateId, Guid editId, long sequence)
    {
        if (draft.SessionId != sessionId || draft.PlateId != plateId || draft.EditId != editId || draft.Sequence != sequence)
        {
            throw new InvalidDataException("The recovery checkpoint doesn't match its file's name.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            files.DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame couldn't remove an older recovery checkpoint ({ex.GetType().Name}); it tries again with the next one.");
        }
    }
}
